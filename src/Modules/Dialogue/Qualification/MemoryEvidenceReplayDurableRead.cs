using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private const string ReviewedDurableRecollectionKind = "reviewed_durable_recollection";
        private const string ReviewedDurableProjectionKey = "reviewedDurableProjection";
        private const string ReviewedDurableProjectionSchema = "reign-replay-reviewed-durable-projection-v1";

        private static string DurableReadReplayInputError(Dictionary<string, object> item)
        {
            if (!item.ContainsKey("durableReadReplay")) return "";
            var input = ReadDictionary(item, "durableReadReplay");
            var writer = ReadDictionary(item, "writerReplay");
            var reviewed = ReadDictionary(input, "reviewedFact");
            long cutoff = ReadLong(item, "asOfSequence", -1);
            double day = ReadDouble(input, "worldDay", double.NaN);
            string text = ReadString(reviewed, "text", "");
            if (input == null || writer == null || reviewed == null || cutoff is < 1 or > long.MaxValue - 1024
                || !double.IsFinite(day) || day <= ReadDouble(item, "worldDay", double.MaxValue)
                || ReadLong(writer, "snapshotAsOfSequence", -1) != cutoff || writer.ContainsKey("continuation")
                || !ReplaySha256(ReadString(ReadDictionary(writer, "response"), "requestSha256", ""))
                || item.ContainsKey("publishedPartyViewSha256") || ReadString(item, "route", "dialogue") is not ("dialogue" or "event")
                || input.Keys.Any(key => key is not ("query" or "worldDay" or "authored" or "reviewedFact"))
                || ReadString(input, "query", "").Length is < 1 or > 4000
                || reviewed.Keys.Any(key => key is not ("text" or "sourceBodySha256" or "reviewReceiptSha256"))
                || !ReplaySha256(ReadString(reviewed, "sourceBodySha256", "")) || !ReplaySha256(ReadString(reviewed, "reviewReceiptSha256", ""))
                || !ValidDurableProjectionText(text) || !IsConversationObjectArray(input.GetValueOrDefault("authored")))
                return "invalid_durable_read_replay";
            var authored = ReadDictionaryList(input, "authored");
            if (authored.Count != 29 || authored.Any(row => row.Keys.Any(key => key != "role" && key != "text")
                || ReadString(row, "text", "").Length is < 1 or > 4000 || ReadString(row, "role", "") is not ("player" or "npc"))
                || !authored.Any(row => ReadString(row, "role", "") == "player") || !authored.Any(row => ReadString(row, "role", "") == "npc"))
                return "invalid_durable_read_authored_window";
            return "";
        }

        private static void AdvanceEvidenceReplaySequence(string campaign, long cutoff)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation)
                throw new InvalidOperationException("Evidence replay sequence advancement requires isolated validation.");
            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            // Replay seeds explicit accepted sequences. The local serial sequence
            // must reach the imported cutoff before any subsequent publication.
            DialogueStore.SelectMemoryAcceptedTurnSequence(connection, TestDict("cutoff", cutoff));
        }

        private static bool ValidDurableProjectionText(string text) => text.Length is >= 16 and <= 480
            && !text.Any(char.IsControl) && !text.Contains("[[", StringComparison.Ordinal) && !text.Contains("<|", StringComparison.Ordinal);

        private static string DurableProjectionHash(Dictionary<string, object> projection)
        {
            var binding = CloneDictionary(projection);
            binding.Remove("projectionSha256");
            return ConversationExactTextHash(CanonicalJson(binding));
        }

        // The read gate asks for canonical occurrence and delivery metadata only.
        // Legacy lexical ranking has its own eligibility checks and row reads.
        private static Dictionary<string, object> ReadDurableSourceMetadata(ReignDbConnection connection, string sourceId) =>
            DialogueStore.SelectConversationTurns82(connection, TestDict("id", sourceId)).SingleOrDefault();

        private static bool DurableSourceIncludesPair(Dictionary<string, object> source, string observer, string peer)
        {
            var participants = DialogueService.TextListFromJson(ReadString(source, "participants_json", "[]"));
            return participants.Contains(observer) && participants.Contains(peer);
        }

        private static bool DurableMemoryBindingMatches(Dictionary<string, object> row, Dictionary<string, object> payload,
            string observer, string eventId, string sourceHash)
        {
            var evidence = ReadDictionary(payload, "sourceEvidence");
            return ReadString(row, "source", "") == "dialogue_llm" && ReadString(row, "status", "") == "active"
                && ReadString(row, "owner_id", "") == observer && ReadString(row, "event_id", "") == eventId
                && ReadString(payload, "sourceValidation", "") == "supported_span"
                && ReadString(ReadDictionary(payload, "semanticAdmission"), "status", "") == "exact_reported_source"
                && ReadString(evidence, "turnId", "") == "current_npc" && ReadString(evidence, "speakerId", "") == observer
                && ReadString(evidence, "sourceEventId", "") == eventId
                && sourceHash.Equals(ReadString(evidence, "sourceHash", ""), StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> StoreReviewedDurableProjection(string campaign, string observer, string peer,
            string timeline, Dictionary<string, object> accepted, Dictionary<string, object> review)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation)
                throw new InvalidOperationException("A reviewed replay projection requires isolated validation storage.");
            string sourceId = ReadString(accepted, "turn_id", ""), eventId = ReadString(accepted, "event_id", "");
            string original = ReadString(accepted, "text", ""), derived = ReadString(review, "text", "");
            string sourceHash = ConversationExactTextHash(original);
            if (sourceId.Length == 0 || eventId.Length == 0 || original.Length == 0 || derived.Contains(original, StringComparison.Ordinal)
                || !sourceHash.Equals(ReadString(review, "sourceBodySha256", ""), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The reviewed recollection is not pinned to the accepted source.");
            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            using var transaction = connection.BeginTransaction();
            var locked = DialogueStore.SelectConversationTurns83(connection, TestDict("id", sourceId)).Single();
            var source = ReadDurableSourceMetadata(connection, sourceId);
            if (ReadString(locked, "text", "") != original || ReadString(source, "event_id", "") != eventId
                || ReadString(source, "speaker_id", "") != observer || ReadString(source, "role", "") != "npc"
                || ReadString(source, "status", "") != "active" || ReadString(source, "timeline_id", "") != timeline
                || ReadLong(source, "accepted_sequence", -1) != ReadLong(accepted, "accepted_sequence", -1)
                || !DurableSourceIncludesPair(source, observer, peer))
                throw new InvalidOperationException("The accepted source changed before the reviewed projection could be stored.");
            var matches = new List<(Dictionary<string, object> Row, Dictionary<string, object> Payload)>();
            foreach (var row in DialogueStore.SelectMemories12(connection, TestDict("event", eventId, "observer", observer)))
            {
                var payload = TryParseJsonObject(ReadString(row, "payload_json", ""));
                var evidence = ReadDictionary(payload, "sourceEvidence");
                if (DurableMemoryBindingMatches(row, payload, observer, eventId, sourceHash) && ReadString(row, "summary", "") == original
                    && ReadString(evidence, "quote", "") == original && ReadInt(evidence, "start", -1) == 0
                    && ReadInt(evidence, "length", -1) == original.Length) matches.Add((row, payload));
            }
            if (matches.Count == 0) return TestDict("status", "no_accepted_write");
            var match = matches.Single();
            string memoryId = ReadString(match.Row, "memory_id", "");
            var projection = TestDict("schema", ReviewedDurableProjectionSchema, "memoryId", memoryId, "sourceTurnId", sourceId,
                "sourceEventId", eventId, "sourceBodySha256", sourceHash, "sourceSequence", ReadLong(source, "accepted_sequence", -1),
                "sourceTimestamp", ReadLong(source, "ts", -1), "sourceWorldDay", ReadDouble(source, "world_day", -1),
                "sourceRevision", ReadString(source, "source_revision", ""), "restoreGeneration", ReadString(source, "restore_generation", ""),
                "speakerId", observer, "peerId", peer, "timelineId", timeline, "text", derived,
                "reviewReceiptSha256", ReadString(review, "reviewReceiptSha256", ""));
            string projectionHash = DurableProjectionHash(projection);
            projection["projectionSha256"] = projectionHash;
            match.Payload[ReviewedDurableProjectionKey] = projection;
            DialogueStore.UpdateMemories8(connection, TestDict("payload", Json.Serialize(match.Payload), "id", memoryId));
            transaction.Commit();
            return TestDict("status", "stored", "memoryId", memoryId, "projectionSha256", projectionHash,
                "textSha256", ConversationExactTextHash(derived), "sourceRevision", projection["sourceRevision"],
                "restoreGeneration", projection["restoreGeneration"]);
        }

        internal sealed class ReviewedDurableReplayRead(string observer, string peer, string timeline)
        {
            internal string RankedId { get; private set; } = "";
            internal string DiscoveredId { get; private set; } = "";
            internal string ProjectionHash { get; private set; } = "";
            internal List<string> Denials { get; private set; } = new();

            internal MemoryEvidence ProjectRankedMemory(ReignDbConnection connection, Dictionary<string, object> row,
                DialogueService.KnowledgeAccessContext access, double score)
            {
                var payload = TryParseJsonObject(ReadString(row, "payload_json", ""));
                var projection = ReadDictionary(payload, ReviewedDurableProjectionKey);
                if (projection == null) return null;
                RankedId = ReadString(row, "memory_id", "");
                ProjectionHash = ReadString(projection, "projectionSha256", "");
                Denials = CheckBinding(connection, row, payload, projection, access);
                if (Denials.Count > 0) return null;
                var evidence = EvidenceFromRow(row, "memory_id", ReadString(projection, "text", ""), ReviewedDurableRecollectionKind, observer, false, score);
                DiscoveredId = evidence.SourceId;
                return evidence;
            }

            private List<string> CheckBinding(ReignDbConnection connection, Dictionary<string, object> row, Dictionary<string, object> payload,
                Dictionary<string, object> projection, DialogueService.KnowledgeAccessContext access)
            {
                if (ReadString(projection, "schema", "") != ReviewedDurableProjectionSchema
                    || !ValidDurableProjectionText(ReadString(projection, "text", ""))
                    || !ReplaySha256(ReadString(projection, "reviewReceiptSha256", ""))
                    || !ReplaySha256(ReadString(projection, "sourceBodySha256", ""))
                    || DurableProjectionHash(projection) != ReadString(projection, "projectionSha256", "")) return new() { "projection_integrity" };
                if (access.NpcId != observer || access.PlayerId != peer || access.TimelineId != timeline || access.PlayerIdentityUnknown
                    || ReadString(projection, "speakerId", "") != observer || ReadString(projection, "peerId", "") != peer
                    || ReadString(projection, "timelineId", "") != timeline) return new() { "reader_scope" };
                string sourceId = ReadString(projection, "sourceTurnId", ""), eventId = ReadString(projection, "sourceEventId", "");
                if (sourceId.Length == 0 || eventId.Length == 0 || ReadString(projection, "memoryId", "") != ReadString(row, "memory_id", "")
                    || !DurableMemoryBindingMatches(row, payload, observer, eventId, ReadString(projection, "sourceBodySha256", ""))
                    || !DialogueService.KnowledgeRowVisibleToNpc("memories", row, access)) return new() { "memory_binding" };
                var source = ReadDurableSourceMetadata(connection, sourceId);
                if (source == null) return new() { "source_missing" };
                var denials = new List<string>();
                if (ReadString(source, "source_revision", "") != ReadString(projection, "sourceRevision", "")
                    || ReadString(source, "restore_generation", "") != ReadString(projection, "restoreGeneration", "")) denials.Add("source_revision");
                if (!DurableSourceIncludesPair(source, observer, peer)) denials.Add("source_audience");
                if (ReadString(source, "status", "") != "active" || ReadString(source, "speaker_id", "") != observer
                    || ReadString(source, "event_id", "") != eventId || ReadString(source, "role", "") != "npc"
                    || ReadString(source, "timeline_id", "") != timeline
                    || ReadLong(source, "accepted_sequence", -1) != ReadLong(projection, "sourceSequence", -2)
                    || ReadLong(source, "ts", -1) != ReadLong(projection, "sourceTimestamp", -2)
                    || ReadDouble(source, "world_day", -1) != ReadDouble(projection, "sourceWorldDay", -2)
                    || !MemoryTimeVisible(source, access)) denials.Add("source_occurrence");
                if (ReviewedRecoverySourceSuperseded(connection, access, sourceId)) denials.Add("source_superseded");
                return denials;
            }

            internal Dictionary<string, object> CheckControls(string campaign, Dictionary<string, object> context)
            {
                if (DiscoveredId.Length == 0) return TestDict();
                using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                var row = DialogueStore.SelectMemories13(connection, TestDict("id", DiscoveredId)).Single();
                var payload = TryParseJsonObject(ReadString(row, "payload_json", ""));
                var projection = ReadDictionary(payload, ReviewedDurableProjectionKey);
                var access = DialogueService.BuildKnowledgeAccessContext(observer, peer, "", context);
                bool accepted = CheckBinding(connection, row, payload, projection, access).Count == 0;
                var changed = CloneDictionary(payload);
                var sourceEvidence = ReadDictionary(changed, "sourceEvidence");
                string hash = ReadString(sourceEvidence, "sourceHash", "");
                sourceEvidence["sourceHash"] = (hash[0] == '0' ? "1" : "0") + hash.Substring(1);
                bool tamperBlocked = CheckBinding(connection, row, changed, projection, access).SequenceEqual(new[] { "memory_binding" });
                var alteredProjection = CloneDictionary(projection);
                alteredProjection["text"] = "Altered: " + ReadString(projection, "text", "");
                bool alteredProjectionBlocked = CheckBinding(connection, row, payload, alteredProjection, access).SequenceEqual(new[] { "projection_integrity" });
                var foreign = DialogueService.BuildKnowledgeAccessContext(observer + ":foreign", peer, "", context);
                bool foreignBlocked = CheckBinding(connection, row, payload, projection, foreign).SequenceEqual(new[] { "reader_scope" });
                access.AsOfAcceptedSequence = ReadLong(projection, "sourceSequence", 0) - 1;
                bool earlyBlocked = CheckBinding(connection, row, payload, projection, access).SequenceEqual(new[] { "source_occurrence" });
                return TestDict("reopenedRecordVerified", accepted, "tamperedHashBlocked", tamperBlocked,
                    "tamperedProjectionBlocked", alteredProjectionBlocked, "foreignObserverBlocked", foreignBlocked, "earlierCutoffBlocked", earlyBlocked);
            }
        }

        private static string DurableWireText(Dictionary<string, object> wire) => string.Join("\n",
            ReadDictionaryList(TryParseJsonObject(ReadString(wire, "requestJson", "")), "messages")
                .Select(message => ReadString(message, "content", "")));

        private static Dictionary<string, object> ReplayDurableRevocations(string campaign, string observer, string peer, string timeline,
            string sourceId, string original, long sequence, string derived, Dictionary<string, object> stored,
            Func<ReviewedDurableReplayRead, Dictionary<string, object>> capture)
        {
            var controls = TestDict();
            if (!ReignPostgreSqlStorage.Options.IsValidation)
                throw new InvalidOperationException("Durable revocation controls require isolated validation storage.");
            string memoryId = ReadString(stored, "memoryId", ""), projectionHash = ReadString(stored, "projectionSha256", "");
            var memoryKey = TestDict("id", memoryId);
            string IndexedState()
            {
                using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                var record = DialogueStore.SelectMemories13(connection, memoryKey).Single();
                var index = DialogueStore.SelectMemoryFts(connection, memoryKey);
                if (index.Count == 0) throw new InvalidOperationException("The revocation control needs the original lexical index entry.");
                return ConversationExactTextHash(CanonicalJson(TestDict("record", record, "index", index)));
            }

            string indexed = IndexedState(), beforeRevision = ReadString(stored, "sourceRevision", "");
            foreach (bool narrowAudience in new[] { false, true })
            {
                Dictionary<string, object> changed;
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    // The first update leaves every value intact. It distinguishes
                    // the row version gate from the later change to source delivery.
                    string sql = narrowAudience
                        ? DialogueStore.UpdateConversationTurnsSql2
                        : DialogueStore.UpdateConversationTurnsSql3;
                    var args = narrowAudience ? TestDict("id", sourceId, "audience", Json.Serialize(new[] { observer })) : TestDict("id", sourceId);
                    changed = DialogueStore.QuerySql(connection, sql, args).Single();
                }
                string revision = ReadString(changed, "source_revision", "");
                bool sameOccurrence = ReadString(changed, "text", "") == original && ReadLong(changed, "accepted_sequence", -1) == sequence;
                bool revisionAdvanced = revision.Length > 0 && revision != beforeRevision;
                beforeRevision = revision;
                var reader = new ReviewedDurableReplayRead(observer, peer, timeline);
                var wire = capture(reader);
                string body = DurableWireText(wire);
                bool ranked = reader.RankedId == memoryId && reader.ProjectionHash == projectionHash;
                bool sameIndex = IndexedState() == indexed;
                var expected = narrowAudience ? new[] { "source_revision", "source_audience" } : new[] { "source_revision" };
                bool noLeak = ReadString(wire, "status", "") == "captured" && !body.Contains(derived, StringComparison.Ordinal)
                    && !body.Contains(original, StringComparison.Ordinal) && !body.Contains(memoryId, StringComparison.Ordinal);
                bool denied = sameOccurrence && revisionAdvanced && ranked && sameIndex && reader.Denials.SequenceEqual(expected)
                    && reader.DiscoveredId.Length == 0 && noLeak;
                controls[narrowAudience ? "audienceNarrowed" : "sameTextRevision"] = TestDict("passed", denied, "ranked", ranked,
                    "rankedMemoryId", reader.RankedId, "denials", reader.Denials, "storedProjectionSha256", reader.ProjectionHash,
                    "sameAuthoredOccurrence", sameOccurrence, "sourceRevision", revision, "revisionAdvanced", revisionAdvanced,
                    "indexedRowUnchanged", sameIndex, "indexSha256", indexed, "wire", wire, "noLeak", noLeak);
            }
            return controls;
        }

        private static Dictionary<string, object> ReplayLaterDurableRead(string campaign, string observer, string peer,
            Dictionary<string, object> item, Dictionary<string, object> payload, Dictionary<string, object> settings,
            List<Dictionary<string, object>> accepted,
            Func<Dictionary<string, object>, List<Dictionary<string, object>>, Dictionary<string, object>, Dictionary<string, object>> prepare)
        {
            var input = ReadDictionary(item, "durableReadReplay");
            var authors = ReadDictionaryList(input, "authored");
            var npc = accepted.Single(row => ReadString(row, "role", "") == "npc");
            var player = accepted.Single(row => ReadString(row, "role", "") == "player");
            string priorSession = ReadString(npc, "session_id", ""), timeline = ContinuityTimeline(payload);
            long cutoff = ReadLong(item, "asOfSequence", -1), written = ReadLong(npc, "accepted_sequence", -1);
            long ended = Math.Max(ReadLong(npc, "ts", -1), ReadLong(player, "ts", -1));
            double day = ReadDouble(input, "worldDay", 0d);
            if (ReadLong(player, "accepted_sequence", -1) <= cutoff || written <= ReadLong(player, "accepted_sequence", -1)
                || priorSession.Length == 0 || priorSession != ReadString(player, "session_id", "") || ended < 1)
                throw new InvalidOperationException("The accepted write is not ordered after the imported replay cutoff.");
            var review = ReadDictionary(input, "reviewedFact");
            var stored = StoreReviewedDurableProjection(campaign, observer, peer, timeline, npc, review);
            var view = new ReviewedDurableReplayRead(observer, peer, timeline);
            string session = observer + ":durable_scene:" + ReadString(item, "caseId", "");
            string query = ReadString(input, "query", ""), channel = ReadString(npc, "channel", "in_person");
            var context = EvidenceReplayNextMemoryContext(payload, query, session, session + ":current", written);
            context["worldDay"] = day;
            var audience = new List<string> { observer, peer };
            List<Dictionary<string, object>> rows;
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            using (var transaction = connection.BeginTransaction())
            {
                DialogueStore.UpdateConversationSessions9(connection, TestDict("end", ended, "prior", priorSession));
                DialogueStore.InsertConversationSessions3(connection, TestDict("session", session, "campaign", campaign, "npc", observer, "peer", peer, "start", ended + 1,
                        "end", ended + authors.Count, "audience", Json.Serialize(audience), "payload", Json.Serialize(context)));
                for (int i = 0; i < authors.Count; i++)
                {
                    var author = authors[i];
                    string role = ReadString(author, "role", "");
                    bool isPlayer = role == "player";
                    string id = session + ":" + i;
                    InsertConversationTurn(connection, id, session, id, i, id, role, isPlayer ? peer : observer,
                        ReadString(isPlayer ? player : npc, "speaker_name", ""), ReadString(author, "text", ""), channel,
                        day, ended + i + 1, "", audience, context);
                }
                rows = DialogueStore.SelectConversationTurns84(connection, TestDict("session", session));
                if (rows.Count != 29 || rows.Any(row => !EvidenceReplayAcceptedTranscriptTurn(row)
                    || ReadLong(row, "accepted_sequence", -1) <= written)
                    || !rows.Select(row => (ReadString(row, "role", ""), ReadString(row, "text", "")))
                        .SequenceEqual(authors.Select(row => (ReadString(row, "role", ""), ReadString(row, "text", "")))))
                    throw new InvalidOperationException("Fresh durable-read transcript did not preserve the later authored rows.");
                transaction.Commit();
            }
            long laterCutoff = ReadLong(rows[^1], "accepted_sequence", -1);
            context["memoryAsOfAcceptedSequence"] = laterCutoff;
            context["memoryAsOfTimestamp"] = ended + authors.Count;
            var transcript = rows.Select(row => TestDict("turnId", ReadString(row, "turn_id", ""), "speakerId", ReadString(row, "speaker_id", ""),
                "speaker", ReadString(row, "speaker_name", ""), "role", ReadString(row, "role", ""), "text", ReadString(row, "text", ""),
                "sessionId", session, "exchangeId", ReadString(row, "exchange_id", ""))).ToList();
            SetMemoryTranscriptContext(context, transcript);
            var wideContext = CloneDictionary(context);
            var controlContext = CloneDictionary(context);
            var wide = BuildPrecisionMemoryPacket(campaign, observer, peer, "", query, wideContext, settings, false);
            var compact = BuildPrecisionMemoryPacket(campaign, observer, peer, "", query, context, settings, false, durableRead: view);
            var controls = view.CheckControls(campaign, context);
            var wideWire = ReadDictionary(CaptureMemoryReplayProviderRequest(prepare(wideContext, transcript, wide), settings), "providerWireCapture");
            var compactWire = ReadDictionary(CaptureMemoryReplayProviderRequest(prepare(context, transcript, compact), settings), "providerWireCapture");
            var rendered = ReadDictionaryList(ReadDictionary(compact, "precisionEvidence"), "records");
            bool packed = view.DiscoveredId.Length > 0 && rendered.Any(record => ReadString(record, "sourceId", "") == view.DiscoveredId
                && ReadString(record, "kind", "") == ReviewedDurableRecollectionKind);
            string original = ReadString(npc, "text", ""), derived = ReadString(review, "text", ""), sourceId = ReadString(npc, "turn_id", "");
            Dictionary<string, object> CaptureRevocation(ReviewedDurableReplayRead reader)
            {
                var revocationContext = CloneDictionary(controlContext);
                var packet = BuildPrecisionMemoryPacket(campaign, observer, peer, "", query, revocationContext, settings, false, durableRead: reader);
                return ReadDictionary(CaptureMemoryReplayProviderRequest(prepare(revocationContext, transcript, packet), settings), "providerWireCapture");
            }
            var revocations = packed ? ReplayDurableRevocations(campaign, observer, peer, timeline, sourceId, original, written,
                derived, stored, CaptureRevocation) : TestDict();
            string body = DurableWireText(compactWire);
            bool preserves = ReadString(wideWire, "status", "") == "captured" && ReadString(compactWire, "status", "") == "captured"
                && !body.Contains(original, StringComparison.Ordinal) && body.Contains(derived, StringComparison.Ordinal) == packed
                && (!packed || (view.DiscoveredId == ReadString(stored, "memoryId", "") && !body.Contains(view.DiscoveredId, StringComparison.Ordinal)
                    && view.ProjectionHash == ReadString(stored, "projectionSha256", "")
                    && ReadBool(ReadDictionary(revocations, "sameTextRevision"), "passed", false)
                    && ReadBool(ReadDictionary(revocations, "audienceNarrowed"), "passed", false)));
            var sourceAudit = TestDict("sourceTurnId", sourceId, "sourceEventId", ReadString(npc, "event_id", ""), "sourceSequence", written,
                "sourceBodySha256", ConversationExactTextHash(original), "reviewReceiptSha256", ReadString(review, "reviewReceiptSha256", ""),
                "discoveredMemoryId", view.DiscoveredId, "rankedMemoryId", view.RankedId, "readProjectionSha256", view.ProjectionHash,
                "denials", view.Denials);
            return TestDict("status", preserves ? "captured" : "blocked", "source", sourceAudit, "memoryPacked", packed,
                "persistedReviewedProjection", stored, "revocations", revocations, "controls", controls, "widePacket", wide, "compactPacket", compact,
                "wideWire", wideWire, "compactWire", compactWire, "importedCutoff", cutoff, "acceptedPlayerSequence", ReadLong(player, "accepted_sequence", -1),
                "acceptedNpcSequence", written, "firstLaterSequence", ReadLong(rows[0], "accepted_sequence", -1), "laterCutoff", laterCutoff,
                "laterSessionId", session, "priorSessionId", priorSession, "worldDay", day, "priorAuthoredRows", transcript,
                "currentContributionSha256", ConversationExactTextHash(query), "providerCalls", 0);
        }
    }
}
