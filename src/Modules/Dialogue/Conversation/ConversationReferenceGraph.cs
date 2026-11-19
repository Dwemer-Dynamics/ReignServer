using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        // Only reference metadata lives here. Turns, letters, agreement status,
        // corrections and knowledge acquisition keep their existing authorities.
        private const string ConversationReferenceGraphVersion = "reference-hints-v1";
        private const int ConversationReferenceMaximumNodes = 64;
        private const int ConversationReferenceMaximumScannedEdges = 512;
        private static readonly HashSet<string> ConversationReferenceRelations = new(StringComparer.Ordinal) {
            "refers_to", "answers_about", "clarifies", "amends", "withdraws", "depends_on"
        };

        private sealed class ConversationReferenceSource
        {
            internal string Type, Id, Hash, View, Speaker;
            internal long Sequence, Timestamp;
            internal double Day;
            internal Dictionary<string, object> Row;
            internal readonly List<string> DeliveredViews = new();
        }

        private sealed class ConversationReferenceOffer
        {
            internal string Signature, Generation, Mode, OfferedSetHash, Campaign;
            internal bool AmbiguousAttempt;
            internal readonly Dictionary<string, ConversationReferenceSource> Sources = new(StringComparer.Ordinal);
        }

        // A client field or provider output can never create this receipt.
        private static readonly ConditionalWeakTable<Dictionary<string, object>, Dictionary<string, ConversationReferenceOffer>> ConversationReferenceOffers = new();
        private static readonly ConditionalWeakTable<Dictionary<string, object>, Dictionary<string, List<Dictionary<string, object>>>> ExpectedConversationReferenceSources = new();
        private static readonly AsyncLocal<string> ConversationReferenceValidationMode = new();
        private static readonly ConditionalWeakTable<Dictionary<string, object>, Dictionary<string, Dictionary<string, object>>> ConversationReferenceDiagnostics = new();

        private static Dictionary<string, object> ConversationReferenceDiagnostic(Dictionary<string, object> payload, string observer)
        {
            if (payload == null || !ConversationReferenceDiagnostics.TryGetValue(payload, out var byObserver)) return TestDict();
            lock (byObserver) return byObserver.TryGetValue(observer, out var diagnostic) ? diagnostic : TestDict();
        }

        // Only metadata containers are stripped. A literal occurrence inside
        // authored speech remains speech. Malformed raw JSON with this private
        // field is withheld rather than leaking an incomplete quoted source.
        internal static object ConversationReferenceTransportPayload(object value, string method = "", string path = "")
        {
            // These existing operator evidence routes promise exact paginated
            // bytes/hashes. They intentionally expose opted-in private captures.
            // Normal dialogue/job/provider responses still remove proposals.
            if (method == "GET" && (path == "/audit/prompt" || path.StartsWith("/api/diagnostics/conversations/", StringComparison.Ordinal))) return value;
            return WithoutConversationReferenceMetadata(value);
        }

        internal static object WithoutConversationReferenceMetadata(object value, string key = "", bool providerResponse = false)
        {
            if (value is IDictionary dictionary)
            {
                bool response = providerResponse || dictionary.Contains("choices") || dictionary.Contains("finishReason");
                var clean = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry pair in dictionary)
                {
                    string child = Convert.ToString(pair.Key, CultureInfo.InvariantCulture) ?? "";
                    if (!child.Equals("memoryReferences", StringComparison.OrdinalIgnoreCase))
                        clean[child] = WithoutConversationReferenceMetadata(pair.Value, child,
                            child == "messages" ? false : response || child == "llm");
                }
                return clean;
            }
            if (value is string text)
            {
                bool rawResponse = key is "rawContent" or "responseText" || providerResponse && key == "content";
                if (!rawResponse || !text.Contains("\"memoryReferences\"", StringComparison.OrdinalIgnoreCase)) return text;
                var parsed = TryParseStrictJsonObject(text);
                return parsed == null ? "[private conversation reference metadata omitted]" : Json.Serialize(WithoutConversationReferenceMetadata(parsed));
            }
            if (value is IEnumerable list && value is not byte[])
            {
                var clean = new List<object>();
                foreach (var item in list) clean.Add(WithoutConversationReferenceMetadata(item, key, providerResponse));
                return clean;
            }
            return value;
        }

        private static string ConversationReferenceMode(Dictionary<string, object> settings = null)
        {
            string mode = ConversationReferenceValidationMode.Value;
            if (!string.IsNullOrEmpty(mode) && (!ReignPostgreSqlStorage.Options.IsValidation || string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value)))
                throw new InvalidOperationException("Reference graph fixtures require isolated validation.");
            settings ??= SettingsService.LoadSettings();
            if (string.IsNullOrEmpty(mode)) mode = ResolveConversationReferenceMode(settings, IsIsolatedMemoryValidation);
            return mode is "capture" or "enabled" ? mode : "off";
        }

        private static string ResolveConversationReferenceMode(Dictionary<string, object> settings, bool allowFixtureMode = false) =>
            allowFixtureMode && settings?.ContainsKey("memoryReferenceGraphMode") == true
                ? ReadString(settings, "memoryReferenceGraphMode", "off")
                : ResolveMemoryMode(settings, allowFixtureMode) == "precision" ? "enabled" : "off";

        private static readonly HashSet<string> ConversationReferenceOriginalKinds = new(StringComparer.Ordinal) {
            "accepted_exchange", "targeted_historical_exchange", "reported_exchange", "resolved_sequence", "letter" };

        private static bool ConversationReferenceSourceKind(string kind) => ConversationReferenceOriginalKinds.Contains(kind)
            || kind is "reference_graph_exchange" or "reference_graph_letter";

        private static string ConversationReferenceKey(string type, string id) => type + ":" + id;

        private static List<Dictionary<string, object>> DeliveredGraphRecallOriginals(Dictionary<string, object> payload,
            string observer, string final, Func<string, string> identityProjection)
        {
            var result = new List<Dictionary<string, object>>();
            if (ConversationReferenceMode() != "enabled" || payload == null
                || !ExpectedConversationReferenceSources.TryGetValue(payload, out var bySection)) return result;
            List<Dictionary<string, object>> expected;
            lock (bySection) expected = bySection.Values.SelectMany(v => v).Where(r =>
                ReadString(r, "kind", "") == "reference_graph_exchange" && ReadString(r, "observer", "") == observer).ToList();
            if (expected.Count == 0) return result;
            var access = DialogueService.BuildKnowledgeAccessContext(observer, ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"),
                ReadFirstString(payload, "locationId", "settlementId"), payload);
            using var connection = ReignDatabase.OpenCampaignConnection(ReadString(payload, "campaignId", ""));
            foreach (var record in expected)
            {
                string raw = ReadString(record, "text", "");
                string text = NormalizePromptSegment(identityProjection == null ? raw : identityProjection(raw));
                if (text.Length == 0 || !final.Contains(text, StringComparison.Ordinal)) continue;
                var turns = new List<Dictionary<string, object>>();
                bool complete = true;
                foreach (string id in ReadString(record, "sourceId", "").Split(','))
                {
                    var source = ResolveConversationReferenceSource(connection, "turn", id, access);
                    if (source == null || !text.Contains(source.View, StringComparison.Ordinal)) { complete = false; break; }
                    turns.Add(TestDict("role", ReadString(source.Row, "role", ""), "speakerId", source.Speaker,
                        "speaker", ReadString(source.Row, "speaker_name", source.Speaker), "text", source.View));
                }
                if (complete && turns.Count > 0) result.Add(TestDict("id", ReadString(record, "sourceId", ""), "text", text,
                    "turns", turns, "dyad", turns.Any(t => ReadString(t, "role", "") == "player")
                        && turns.Where(t => ReadString(t, "role", "") == "npc").All(t => ReadString(t, "speakerId", "") == observer),
                    "overlap", 0));
            }
            return result;
        }

        private static string ConversationReferenceEdgeId(string owner, string timeline, string generation,
            string fromType, string from, string fromHash, string toType, string to, string toHash,
            string relation, string originQuote, string targetQuote) => "ref_" + PromptHash(CanonicalJson(TestDict(
                "version", ConversationReferenceGraphVersion, "owner", owner, "timeline", timeline, "generation", generation,
                "fromType", fromType, "from", from, "fromHash", fromHash, "toType", toType, "to", to, "toHash", toHash,
                "relation", relation, "originQuote", originQuote, "targetQuote", targetQuote))).Substring(0, 40);

        private static string ConversationReferenceInstructions() => ConversationReferenceMode() == "off" ? "" :
            "Return private memoryReferences (max8; [] if unsupported). Evaluate player_input and npc_reply independently; select each concrete callback's strongest supplied antecedent. "
            + "origin=player_input|npc_reply (server assigns accepted ID); targetType=turn|letter; targetId=supplied canonical source ID. "
            + "relation=refers_to|answers_about|clarifies|amends|withdraws|depends_on. Exact originQuote:1–800 characters of input/final visible reply; exact targetQuote:12–800 of source. "
            + "Link the specific undertaking/question/event, not its topic. No speculative links, invented IDs, missing antecedents, graph-only targets or copied placeholders. Links prove no truth, fulfillment, cancellation, reading or firsthand knowledge; correction/agreement authorities still control. Never speak IDs.";

        private static string ConversationReferenceOutputSchema(string source)
        {
            if (ConversationReferenceMode() == "off") return source;
            // A bare [] example caused complete field omissions in real long
            // requests. Show both origins and every required field explicitly.
            var references = new[] { "player_input", "npc_reply" }.Select(origin => TestDict(
                "origin", origin, "targetType", "turn", "targetId", "SUPPLIED_ID",
                "relation", "refers_to", "originQuote", origin == "player_input" ? "INPUT_SPAN" : "REPLY_SPAN",
                "targetQuote", "SOURCE_QUOTE")).ToList();
            var example = TryParseStrictJsonObject(source);
            if (example == null) return source + "\nAdditional private output example (placeholders only; [] when unresolved): "
                + Json.Serialize(TestDict("memoryReferences", references));
            example["memoryReferences"] = references;
            return Json.Serialize(example);
        }

        // Capture all server-built original candidates before allocation, without
        // changing which records the existing required-evidence gate protects.
        private static void CaptureConversationReferenceSources(Dictionary<string, object> payload, string section, string text)
        {
            if (payload == null || ConversationReferenceMode() == "off") return;
            var bySection = ExpectedConversationReferenceSources.GetOrCreateValue(payload);
            var records = ContinuityPromptRecords(text, section == "canonicalTranscript")
                .Where(r => ConversationReferenceSourceKind(r.Kind))
                .Select(r => TestDict("sourceId", r.SourceId, "kind", r.Kind, "text", r.Text, "section", section, "observer", r.Observer)).ToList();
            lock (bySection) bySection[section] = records;
        }

        private static Dictionary<string, object> BindDeliveredConversationReferences(Dictionary<string, object> payload,
            string observer, string final, List<Dictionary<string, object>> canonicalLines, Func<string, string> identityProjection)
        {
            string mode = ConversationReferenceMode();
            if (payload == null || mode == "off") return TestDict("status", "off", "sourceCount", 0);
            var offer = new ConversationReferenceOffer { Signature = MemoryRecallTurnSignature(payload, observer), Mode = mode };
            var byObserver = ConversationReferenceOffers.GetOrCreateValue(payload);
            Dictionary<string, object> Unbound(string reason)
            {
                offer.Mode = "off";
                lock (byObserver) byObserver[observer] = offer;
                return TestDict("status", reason, "sourceCount", 0);
            }
            if (!ExpectedConversationReferenceSources.TryGetValue(payload, out var bySection)) return Unbound("no_server_capture");
            string campaign = ReadString(payload, "campaignId", "");
            if (campaign.Length == 0) return Unbound("no_campaign");
            offer.Campaign = campaign;
            var access = DialogueService.BuildKnowledgeAccessContext(observer, ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"),
                ReadFirstString(payload, "locationId", "settlementId"), payload);
            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            offer.Generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            List<Dictionary<string, object>> expected;
            lock (bySection) expected = bySection.Values.SelectMany(v => v).ToList();
            int missing = 0;
            string Project(string value) => NormalizePromptSegment(identityProjection == null ? value : identityProjection(value));
            void Add(string type, string id, string view)
            {
                view = Project(view);
                if (id.Length == 0 || view.Length == 0 || !final.Contains(view, StringComparison.Ordinal)) return;
                string key = ConversationReferenceKey(type, id);
                if (!offer.Sources.TryGetValue(key, out var source))
                {
                    if (offer.Sources.Count >= 128) { missing++; return; }
                    source = ResolveConversationReferenceSource(connection, type, id, access);
                    if (source == null) { missing++; return; }
                    offer.Sources.Add(key, source);
                }
                if (!source.DeliveredViews.Contains(view, StringComparer.Ordinal)) source.DeliveredViews.Add(view);
            }
            foreach (var record in expected)
            {
                // An unverified hint cannot bootstrap another hint merely by
                // bringing an original into view. Independent raw retrieval or
                // exact recent history may still offer that same source.
                if (ReadString(record, "kind", "").StartsWith("reference_graph_", StringComparison.Ordinal)) continue;
                if (ReadString(record, "observer", "") != observer
                    && !(ReadString(record, "section", "") == "canonicalTranscript" && ReadString(record, "observer", "") == "current_observer")) continue;
                string block = Project(ReadString(record, "text", ""));
                if (block.Length == 0 || !final.Contains(block, StringComparison.Ordinal)) continue;
                var ids = ReadString(record, "sourceId", "").Split(',').ToHashSet(StringComparer.Ordinal);
                if (ReadString(record, "kind", "") == "letter")
                {
                    int start = block.IndexOf("Exact letter body:\n", StringComparison.Ordinal);
                    if (ids.Count == 1 && start >= 0)
                    {
                        string body = block.Substring(start + "Exact letter body:\n".Length);
                        int end = body.LastIndexOf("[[/continuity]]", StringComparison.Ordinal);
                        Add("letter", ids.Single(), end >= 0 ? body.Substring(0, end) : body);
                    }
                    continue;
                }
                if (ReadString(record, "section", "") == "canonicalTranscript")
                {
                    foreach (var line in canonicalLines ?? new())
                    {
                        string id = ReadFirstString(line, "id", "turnId", "turn_id");
                        if (ids.Contains(id)) Add("turn", id, ReadString(line, "text", ""));
                    }
                    continue;
                }
                foreach (Match turn in Regex.Matches(block,
                    @"(?:\A|\n)(?:\[m\d+ revision=\d+\] )?- \[Day [^\]\r\n]+\] \[source=(?<id>[^\]\r\n]+) role=(?:player|npc)\] [^:\r\n]+:\s*(?<body>[\s\S]*?)(?=\n- \[Day |\n\[\[/continuity\]\]|\z)"))
                    if (ids.Contains(turn.Groups["id"].Value)) Add("turn", turn.Groups["id"].Value, turn.Groups["body"].Value);
            }
            offer.OfferedSetHash = PromptHash(CanonicalJson(offer.Sources.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
                TestDict("key", p.Key, "sourceHash", p.Value.Hash, "views", p.Value.DeliveredViews.OrderBy(v => v, StringComparer.Ordinal).ToArray())).ToList()));
            lock (byObserver)
            {
                // If two attempts on the same mutable request supplied different
                // originals, a response without an attempt-specific receipt may
                // not borrow the other attempt's offer. Raw recall still works.
                if (byObserver.TryGetValue(observer, out var previous) && previous.Signature == offer.Signature)
                    offer.AmbiguousAttempt = previous.AmbiguousAttempt || previous.OfferedSetHash != offer.OfferedSetHash;
                byObserver[observer] = offer;
            }
            return TestDict("schema", ConversationReferenceGraphVersion, "status", "bound_final_prompt", "sourceCount", offer.Sources.Count,
                "unboundOrOverflowSources", missing, "signature", offer.Signature, "offeredSetHash", offer.OfferedSetHash,
                "ambiguousAttempt", offer.AmbiguousAttempt, "truthEstablished", false);
        }

        private static void EnsureConversationReferenceGraphSchema(ReignDbConnection connection)
        {
            DialogueSchema.CreateConversationReferenceEdges(connection);
        }

        private static ConversationReferenceSource ResolveConversationReferenceSource(ReignDbConnection connection,
            string type, string id, DialogueService.KnowledgeAccessContext access)
        {
            if (access == null || access.NpcId.Length == 0 || id.Length is < 1 or > 512) return null;
            Dictionary<string, object> row;
            if (type == "turn")
            {
                row = DialogueStore.SelectConversationTurns6(connection, TestDict("id", id)).SingleOrDefault();
                if (row == null || !PrecisionTurnVisible(row, access.NpcId, access, connection)) return null;
                string original = ReadString(row, "text", "");
                string view = ObserverConversationText(ConversationTurnTextForRecall(row), ReadString(row, "speaker_id", ""),
                    access.NpcId, ReadString(row, "role", "") == "player");
                view = NormalizePromptSegment(DialogueService.SanitizeUnknownIdentityEvidenceText(view, access));
                if (view.Length == 0) return null;
                return new ConversationReferenceSource { Type = type, Id = id, Row = row, View = view,
                    Hash = PromptHash(CanonicalJson(TestDict("id", id, "speaker", ReadString(row, "speaker_id", ""),
                        "role", ReadString(row, "role", ""), "text", original))), Speaker = ReadString(row, "speaker_id", ""),
                    Sequence = ReadLong(row, "accepted_sequence", 0), Timestamp = ReadLong(row, "ts", 0), Day = ReadDouble(row, "world_day", 0) };
            }
            if (type != "letter" || !WorldSimulationService.TableExists(connection, "letters")) return null;
            row = DialogueStore.SelectLetters2(connection, TestDict("id", id)).SingleOrDefault();
            if (!LetterVisibleToObserver(connection, row, access.NpcId, access, out double learned)) return null;
            string letterView = NormalizePromptSegment(DialogueService.SanitizeUnknownIdentityEvidenceText(ReadString(row, "body", ""), access));
            return letterView.Length == 0 ? null : new ConversationReferenceSource { Type = type, Id = id, Row = row,
                View = letterView, Hash = LetterContentHash(row), Speaker = ReadString(row, "sender_id", ""), Day = learned,
                Sequence = ReadLong(row, "dispatch_sequence", 0), Timestamp = ReadLong(row, "created_ts", 0) };
        }

        private static string ConversationReferenceProposalError(Dictionary<string, object> proposal,
            string finalOriginText, ConversationReferenceSource offered, ConversationReferenceSource current)
        {
            if (proposal == null || proposal.Values.Any(value => value is not string)
                || proposal.Keys.Any(k => k is not ("origin" or "targetType" or "targetId" or "relation" or "originQuote" or "targetQuote")))
                return "invalid_reference_shape";
            if (!ConversationReferenceRelations.Contains(ReadString(proposal, "relation", ""))) return "invalid_reference_relation";
            string origin = ReadString(proposal, "origin", "");
            if (origin is not ("player_input" or "npc_reply")) return "invalid_reference_origin";
            string originQuote = NormalizePromptSegment(ReadString(proposal, "originQuote", ""));
            string targetQuote = NormalizePromptSegment(ReadString(proposal, "targetQuote", ""));
            if (originQuote.Length is < 1 or > 800 || targetQuote.Length is < 12 or > 800) return "invalid_reference_quote_length";
            if (!NormalizePromptSegment(finalOriginText).Contains(originQuote, StringComparison.Ordinal)) return "reference_origin_quote_mismatch";
            if (offered == null) return "reference_source_not_offered";
            if (current == null || offered.Hash != current.Hash) return "reference_source_changed_or_ineligible";
            if (!offered.DeliveredViews.Any(view => view.Contains(targetQuote, StringComparison.Ordinal))
                || !current.View.Contains(targetQuote, StringComparison.Ordinal)) return "reference_quote_outside_delivered_view";
            return "";
        }

        private static ConversationReferenceSource SelectOfferedConversationReference(ConversationReferenceOffer offer,
            string requestedType, string id, out string error)
        {
            error = "reference_source_not_offered";
            if (requestedType is not ("turn" or "letter")) { error = "invalid_reference_target_type"; return null; }
            if (offer == null || string.IsNullOrEmpty(id)) return null;
            // The final server receipt owns source identity and type. A model's
            // redundant type label cannot cause a global or cross-table search.
            // Even a matching claimed type cannot disambiguate a colliding ID.
            var matches = offer.Sources.Values.Where(source => source.Id.Equals(id, StringComparison.Ordinal)).Take(2).ToList();
            if (matches.Count > 1) { error = "ambiguous_reference_source_id"; return null; }
            if (matches.Count == 0) return null;
            error = "";
            return matches[0];
        }

        // This method runs on the accepted unit's existing transaction. There is
        // no model inference or source discovery here, and a failed/suppressed response cannot supply
        // trustworthy player-origin proposals either.
        private static Dictionary<string, object> StoreAcceptedConversationReferences(ReignDbConnection connection,
            AcceptedConversationUnit unit, List<Dictionary<string, object>> sourceRows, bool suppressed)
        {
            if (suppressed || unit.Parsed == null) return TestDict("status", "suppressed", "stored", 0);
            if (ConversationReferenceMode() == "off") return TestDict("status", "off", "stored", 0);
            if (!ConversationReferenceOffers.TryGetValue(unit.Payload, out var byObserver)) return TestDict("status", "not_captured", "stored", 0);
            ConversationReferenceOffer offer;
            lock (byObserver) byObserver.TryGetValue(unit.Hero, out offer);
            if (offer == null) return TestDict("status", "not_captured", "stored", 0);
            if (offer.Mode == "off" || offer.Campaign != unit.Campaign || offer.Signature != MemoryRecallTurnSignature(unit.Payload, unit.Hero)
                || offer.Generation != unit.Generation) return TestDict("status", "stale_offer", "stored", 0);
            if (offer.AmbiguousAttempt) return TestDict("status", "ambiguous_attempt_offer", "stored", 0);
            if (!unit.Parsed.TryGetValue("memoryReferences", out var rawProposals)) return TestDict("status", "missing_reference_array", "stored", 0);
            if (!IsConversationObjectArray(rawProposals))
                return TestDict("status", "invalid_reference_array", "stored", 0);
            var proposals = ReadDictionaryList(unit.Parsed, "memoryReferences");
            if (proposals.Count > 8) return TestDict("status", "too_many_proposals", "stored", 0);
            if (proposals.Count == 0) return TestDict("status", "no_references", "stored", 0);
            EnsureConversationReferenceGraphSchema(connection);
            var access = DialogueService.BuildKnowledgeAccessContext(unit.Hero, unit.Player, unit.Location, unit.Payload);
            var stored = new List<string>(); var rejected = new List<Dictionary<string, object>>();
            int normalizedTargetTypeProposals = 0;
            foreach (var proposal in proposals)
            {
                string requestedType = ReadString(proposal, "targetType", ""), id = ReadString(proposal, "targetId", "");
                var offered = SelectOfferedConversationReference(offer, requestedType, id, out string offerError);
                string type = offered?.Type ?? requestedType;
                var target = offered == null ? null : ResolveConversationReferenceSource(connection, type, id, access);
                bool player = ReadString(proposal, "origin", "") == "player_input";
                string quote = NormalizePromptSegment(ReadString(proposal, "originQuote", ""));
                // Only this observer's accepted projection participates. Never
                // attach the player's hidden author-only source or another NPC.
                var origin = sourceRows.Where(row => ReadString(row, "status", "") == "active"
                        && ReadString(row, "role", "") == (player ? "player" : "npc")
                        && ReadString(row, "speaker_id", "") == (player ? unit.Player : unit.Hero)
                        && PrecisionTurnVisible(row, unit.Hero, null, connection))
                    .FirstOrDefault(row => NormalizePromptSegment(DialogueService.SanitizeUnknownIdentityEvidenceText(ObserverConversationText(ConversationTurnTextForRecall(row),
                        ReadString(row, "speaker_id", ""), unit.Hero, player), access)).Contains(quote, StringComparison.Ordinal));
                string error = ConversationReferenceProposalError(proposal, player ? unit.PlayerText : unit.Reply, offered, target);
                if (error == "reference_source_not_offered" && offerError.Length > 0) error = offerError;
                if (origin == null) error = "accepted_origin_unavailable";
                if (error.Length == 0 && target.Sequence >= ReadLong(origin, "accepted_sequence", 0)) error = "reference_is_not_earlier_source";
                if (error.Length > 0)
                {
                    rejected.Add(TestDict("targetId", id, "reason", error));
                    continue;
                }
                string from = ReadString(origin, "turn_id", ""), relation = ReadString(proposal, "relation", "");
                string fromHash = PromptHash(CanonicalJson(TestDict("id", from, "speaker", ReadString(origin, "speaker_id", ""),
                    "role", ReadString(origin, "role", ""), "text", ReadString(origin, "text", ""))));
                string edgeId = ConversationReferenceEdgeId(unit.Hero, ContinuityTimeline(unit.Payload), unit.Generation,
                    "turn", from, fromHash, type, id, target.Hash, relation, quote, NormalizePromptSegment(ReadString(proposal, "targetQuote", "")));
                var metadata = TestDict("schema", ConversationReferenceGraphVersion, "status", "proposed",
                    "origin", player ? "player_input" : "npc_reply", "originQuote", quote,
                    "targetQuote", NormalizePromptSegment(ReadString(proposal, "targetQuote", "")),
                    "offeredViewHash", PromptHash(offered.View), "offerSignature", offer.Signature,
                    "acceptedReplyHash", PromptHash(unit.Reply ?? ""), "nativeFactVerified", false,
                    "relationEntailmentVerified", false, "changesAgreementStatus", false);
                metadata["requestedTargetType"] = requestedType;
                metadata["targetTypeDerivedFromUniqueOffer"] = type != requestedType;
                DialogueStore.InsertOrIgnoreConversationReferenceEdges(connection, TestDict("id", edgeId, "owner", unit.Hero, "timeline", ContinuityTimeline(unit.Payload),
                    "generation", unit.Generation, "from", from, "fromHash", fromHash, "type", type, "to", id,
                    "toHash", target.Hash, "relation", relation, "event", unit.EventId,
                    "sequence", ReadLong(origin, "accepted_sequence", 0), "day", ReadDouble(origin, "world_day", 0),
                    "ts", unit.Timestamp, "payload", Json.Serialize(metadata)));
                stored.Add(edgeId);
                if (type != requestedType) normalizedTargetTypeProposals++;
            }
            return TestDict("status", "captured_reference_hints", "stored", stored.Distinct().Count(), "edgeIds", stored.Distinct().ToArray(),
                "rejected", rejected, "normalizedTargetTypeProposals", normalizedTargetTypeProposals,
                "nativeFactsWritten", 0, "agreementStatusChanged", false);
        }

        private static List<ConversationReferenceSource> ExpandConversationReferences(ReignDbConnection connection,
            List<(string Type, string Id)> seeds, DialogueService.KnowledgeAccessContext access, string generation,
            int maximumHops, int maximumNodes, Dictionary<string, object> diagnostics)
        {
            var result = new List<ConversationReferenceSource>();
            if (!WorldSimulationService.TableExists(connection, "conversation_reference_edges")) return result;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var frontier = seeds.Distinct().Take(6).ToList();
            var sources = new Dictionary<string, ConversationReferenceSource>(StringComparer.Ordinal);
            var seenEdges = new HashSet<string>(StringComparer.Ordinal);
            int nodeLimit = Math.Clamp(maximumNodes, 1, ConversationReferenceMaximumNodes);
            int hopLimit = Math.Clamp(maximumHops, 1, ConversationReferenceMaximumNodes);
            int traversed = 0, blocked = 0, scanned = 0, queries = 0, hops = 0, forward = 0, reverse = 0;
            bool stop = false, nodeLimited = false, scanLimited = false, saturated = false, admissionLimited = false;
            ConversationReferenceSource Resolve(string type, string id)
            {
                string key = ConversationReferenceKey(type, id);
                if (!sources.TryGetValue(key, out var source)) sources[key] = source = ResolveConversationReferenceSource(connection, type, id, access);
                return source;
            }
            List<Dictionary<string, object>> ReadEdges((string Type, string Id) seed, bool incoming)
            {
                int limit = Math.Min(16, ConversationReferenceMaximumScannedEdges - scanned);
                if (limit <= 0) return new();
                string end = incoming ? "to" : "from";
                // Associations remain observer-owned. Seeing two passages does
                // not grant another character's unspoken inference about them.
                var rows = DialogueStore.SelectConversationReferenceEdges(connection, end, end,TestDict("owner", access.NpcId, "timeline", access.TimelineId,
                    "generation", generation, "sequence", access.AsOfAcceptedSequence, "day", access.WorldDay,
                    "type", seed.Type, "id", seed.Id, "limit", limit));
                queries++; scanned += rows.Count;
                if (limit == 16 && rows.Count == limit) saturated = true;
                return rows;
            }
            foreach (var seed in frontier) visited.Add(ConversationReferenceKey(seed.Type, seed.Id));
            for (int hop = 0; hop < hopLimit && frontier.Count > 0 && !stop; hop++)
            {
                hops = hop + 1;
                var next = new List<(string Type, string Id)>();
                foreach (var seed in frontier)
                {
                    if (Resolve(seed.Type, seed.Id) == null) { blocked++; continue; }
                    if (scanned >= ConversationReferenceMaximumScannedEdges) { scanLimited = true; stop = true; break; }
                    var outgoing = ReadEdges(seed, false);
                    var incoming = ReadEdges(seed, true);
                    int admitted = 0;
                    // Interleave directions so an older antecedent and its later
                    // callbacks can both survive a crowded source. No meaning or
                    // truth status is inverted when following an incoming hint.
                    for (int i = 0; i < Math.Max(outgoing.Count, incoming.Count) && !stop && admitted < 8; i++)
                    for (int side = 0; side < 2 && !stop && admitted < 8; side++)
                    {
                        bool backwards = side == 1;
                        var rows = backwards ? incoming : outgoing;
                        if (i >= rows.Count) continue;
                        var edge = rows[i];
                        if (!seenEdges.Add(ReadString(edge, "edge_id", ""))) continue;
                        traversed++;
                        var metadata = TryParseJsonObject(ReadString(edge, "payload_json", "{}"));
                        string originQuote = ReadString(metadata, "originQuote", ""), targetQuote = ReadString(metadata, "targetQuote", "");
                        if (!ConversationReferenceRelations.Contains(ReadString(edge, "relation", ""))
                            || ReadString(metadata, "schema", "") != ConversationReferenceGraphVersion
                            || originQuote.Length is < 1 or > 800 || targetQuote.Length is < 12 or > 800) { blocked++; continue; }
                        var from = Resolve(ReadString(edge, "from_type", ""), ReadString(edge, "from_id", ""));
                        var to = Resolve(ReadString(edge, "to_type", ""), ReadString(edge, "to_id", ""));
                        if (from == null || to == null || from.Hash != ReadString(edge, "from_hash", "")
                            || to.Hash != ReadString(edge, "to_hash", "")
                            || !from.View.Contains(originQuote, StringComparison.Ordinal)
                            || !to.View.Contains(targetQuote, StringComparison.Ordinal)) { blocked++; continue; }
                        var endpoint = backwards ? to : from;
                        if (endpoint.Type != seed.Type || endpoint.Id != seed.Id) { blocked++; continue; }
                        var target = backwards ? from : to;
                        if (!visited.Add(ConversationReferenceKey(target.Type, target.Id))) continue;
                        result.Add(target); next.Add((target.Type, target.Id));
                        admitted++;
                        if (backwards) reverse++; else forward++;
                        if (result.Count >= nodeLimit) { nodeLimited = true; stop = true; }
                    }
                    if (admitted == 8) admissionLimited = true;
                    if (scanned >= ConversationReferenceMaximumScannedEdges) { scanLimited = true; stop = true; }
                    if (stop) break;
                }
                frontier = next;
            }
            bool hopLimited = !stop && frontier.Count > 0 && hops >= hopLimit;
            diagnostics["traversedEdges"] = traversed; diagnostics["blockedEdges"] = blocked;
            diagnostics["expandedSources"] = result.Count; diagnostics["remainingFrontier"] = frontier.Count;
            diagnostics["traversalPolicy"] = "bounded_bidirectional_v2";
            diagnostics["scannedEdgeRows"] = scanned; diagnostics["edgeQueries"] = queries;
            diagnostics["sourceLookups"] = sources.Count; diagnostics["hopsUsed"] = hops;
            diagnostics["forwardAdmitted"] = forward; diagnostics["reverseAdmitted"] = reverse;
            diagnostics["nodeLimitReached"] = nodeLimited; diagnostics["scanCapReached"] = scanLimited;
            diagnostics["hopLimitReached"] = hopLimited; diagnostics["perNodeLimitSaturated"] = saturated;
            diagnostics["perNodeAdmitCapReached"] = admissionLimited;
            diagnostics["traversalCoverage"] = nodeLimited || scanLimited || hopLimited || saturated || admissionLimited ? "partial" : "bounded_complete";
            diagnostics["semanticStatus"] = "proposed_reference_only";
            return result;
        }

        // Called only after the restored source-membership and letter receipts
        // have been rebound to the new generation. Never infer ancestry from a
        // branch name or retain links to sources absent from that exact snapshot.
        private static void RebindRestoredConversationReferences(ReignDbConnection connection,
            Dictionary<string, object> point, string generation)
        {
            if (!WorldSimulationService.TableExists(connection, "conversation_reference_edges") || point == null
                || ReadString(point, "savePointId", "").Length == 0) return;
            string timeline = ReadString(point, "timelineId", "");
            if (timeline.Length == 0) return;
            double day = ReadDouble(point, "campaignTimeDays", -1);
            if (!double.IsFinite(day) || day < 0) return;
            var sourceCache = new Dictionary<string, ConversationReferenceSource>(StringComparer.Ordinal);
            var memberCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var edge in DialogueStore.SelectConversationReferenceEdges2(connection, TestDict("generation", generation, "day", day)))
            {
                string owner = ReadString(edge, "owner_id", "");
                var access = new DialogueService.KnowledgeAccessContext { NpcId = owner, TimelineId = timeline, WorldDay = day };
                ConversationReferenceSource Resolve(string type, string id)
                {
                    string key = owner + ":" + ConversationReferenceKey(type, id);
                    if (!sourceCache.TryGetValue(key, out var source)) sourceCache[key] = source = ResolveConversationReferenceSource(connection, type, id, access);
                    return source;
                }
                var from = Resolve(ReadString(edge, "from_type", ""), ReadString(edge, "from_id", ""));
                var to = Resolve(ReadString(edge, "to_type", ""), ReadString(edge, "to_id", ""));
                var metadata = TryParseJsonObject(ReadString(edge, "payload_json", "{}"));
                bool Member(ConversationReferenceSource source)
                {
                    if (source == null) return false;
                    string key = ConversationReferenceKey(source.Type, source.Id);
                    if (memberCache.TryGetValue(key, out bool member)) return member;
                    member = source.Type == "turn" ? IsVerifiedRestoredMemorySource(connection, source.Row, timeline)
                    : RestoredLetterContext(connection, timeline) != null && DialogueStore.SelectMemoryRestoredLetters2(connection, TestDict("id", source.Id, "generation", generation, "hash", source.Hash)).Count == 1;
                    memberCache[key] = member;
                    return member;
                }
                if (ReadString(metadata, "schema", "") != ConversationReferenceGraphVersion
                    || !ConversationReferenceRelations.Contains(ReadString(edge, "relation", ""))
                    || !Member(from) || !Member(to) || from.Hash != ReadString(edge, "from_hash", "") || to.Hash != ReadString(edge, "to_hash", "")
                    || !from.View.Contains(ReadString(metadata, "originQuote", ""), StringComparison.Ordinal)
                    || !to.View.Contains(ReadString(metadata, "targetQuote", ""), StringComparison.Ordinal)) continue;
                metadata = VerificationService.CloneDictionary(metadata);
                metadata["reboundFromEdge"] = ReadString(edge, "edge_id", "");
                metadata["restoredSavePointId"] = ReadString(point, "savePointId", "");
                string id = ConversationReferenceEdgeId(owner, timeline, generation, from.Type, from.Id, from.Hash,
                    to.Type, to.Id, to.Hash, ReadString(edge, "relation", ""), ReadString(metadata, "originQuote", ""), ReadString(metadata, "targetQuote", ""));
                DialogueStore.InsertOrIgnoreConversationReferenceEdges2(connection, TestDict("id", id, "owner", owner, "timeline", timeline, "generation", generation,
                    "fromType", from.Type, "from", from.Id, "fromHash", from.Hash, "toType", to.Type, "to", to.Id, "toHash", to.Hash,
                    "relation", ReadString(edge, "relation", ""), "event", ReadString(edge, "source_event_id", ""),
                    "sequence", ReadLong(edge, "as_of_sequence", from.Sequence), "day", ReadDouble(edge, "world_day", from.Day),
                    "ts", ReadLong(edge, "created_ts", 0), "payload", Json.Serialize(metadata)));
            }
            // These are derivative hints in this restored database. Exact old
            // snapshots retain their own rows; canonical sources are untouched.
            // Prevent one complete duplicate graph accumulating per restore.
            DialogueStore.DeleteConversationReferenceEdges(connection, TestDict("generation", generation));
        }

        private static string BuildConversationReferenceContext(string campaign, string observer, string query,
            Dictionary<string, object> payload, List<Dictionary<string, object>> priorLines, string ordinaryContext,
            Dictionary<string, object> settingsOverride = null)
        {
            if (settingsOverride != null && (!ReignPostgreSqlStorage.Options.IsValidation || string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value)))
                throw new InvalidOperationException("Reference graph settings fixtures require isolated validation.");
            if (ConversationReferenceMode() != "enabled" || payload == null || campaign.Length == 0
                || ReadBool(payload, "officialMemoryFirewall", false)) return "";
            var settings = settingsOverride ?? SettingsService.LoadSettings();
            var plan = PlanMemoryQuery(observer, ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"), query, payload);
            var access = DialogueService.BuildKnowledgeAccessContext(observer, plan.Peer, ReadFirstString(payload, "locationId", "settlementId"), payload);
            access.WorldDay = plan.WorldDay;
            var seeds = new List<(string Type, string Id)>();
            foreach (var row in (priorLines ?? new()).TakeLast(2))
            {
                string id = ReadFirstString(row, "id", "turnId", "turn_id");
                if (id.Length > 0) seeds.Add(("turn", id));
            }
            foreach (var record in ContinuityPromptRecords(ordinaryContext, false)
                .Where(r => r.Kind is "reported_exchange" or "targeted_historical_exchange" or "letter")
                .OrderByDescending(r => plan.Terms.Count(t => r.Text.Contains(t, StringComparison.OrdinalIgnoreCase))))
                foreach (string id in record.SourceId.Split(','))
                {
                    if (seeds.Count >= 6) break;
                    var key = (record.Kind == "letter" ? "letter" : "turn", id);
                    if (!seeds.Contains(key)) seeds.Add(key);
                }
            var diagnostic = TestDict("schema", ConversationReferenceGraphVersion, "mode", "enabled", "seedCount", seeds.Count,
                "preservedRawRetrieval", true, "ordinaryContextHash", PromptHash(ordinaryContext));
            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            string generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            int nodes = Math.Clamp(ReadInt(settings, "memoryReferenceGraphMaxNodes", 12), 1, ConversationReferenceMaximumNodes);
            var expanded = ExpandConversationReferences(connection, seeds, access, generation,
                ReadInt(settings, "memoryReferenceGraphHops", nodes), nodes, diagnostic);
            int allowance = Math.Clamp(ReadInt(settings, "memoryReferenceGraphMaxCharacters", 64000), 0, 240000);
            int used = 0, rejected = 0, duplicates = 0;
            var blocks = new List<(ConversationReferenceSource Source, string Text)>(); var rejectedIds = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            string visible = NormalizePromptSegment(ordinaryContext);
            // Select nearer linked context first; presentation chronology must
            // not silently change which whole units win the local allowance.
            foreach (var source in expanded)
            {
                MemoryEvidence evidence = source.Type == "letter"
                    ? CorrespondenceMemoryEvidence(source.Row, observer, source.Day)
                    : PrecisionExchangeEvidence(connection, source.Row, access, false, 10, compactAdjacentContext: false, preserveRecallContext: true);
                if (evidence == null) continue;
                if (visible.Contains(NormalizePromptSegment(evidence.Text), StringComparison.Ordinal)) { duplicates++; continue; }
                string key = evidence.Kind + ":" + evidence.SourceId;
                if (!seen.Add(key)) continue;
                var supplied = new Dictionary<string, string>(plan.SuppliedSources, StringComparer.Ordinal);
                foreach (var turn in evidence.SourceTurns)
                    if (visible.Contains(NormalizePromptSegment(ReadString(turn, "text", "")), StringComparison.Ordinal))
                        supplied[ReadString(turn, "id", "")] = ReadString(turn, "hash", "");
                evidence = RemoveSuppliedMemoryTurns(evidence, supplied);
                if (evidence == null) { duplicates++; continue; }
                string text = "A proposed reference led here. This graph-only source cannot supply new reference proposals. The edge is not corroboration or proof of a changed agreement. "
                    + "Use the original speaker, source date and current correction ledger; do not fill missing links.\n" + evidence.Text;
                string block = RenderContinuityRecord(evidence.SourceId, source.Type == "letter" ? "reference_graph_letter" : "reference_graph_exchange",
                    observer, evidence.Session, evidence.Day, 10, false, DialogueService.SanitizeUnknownIdentityEvidenceText(text, access));
                if (used + block.Length + 2 > allowance) { rejected++; rejectedIds.Add(evidence.SourceId); continue; }
                used += block.Length + 2; blocks.Add((source, block)); visible += "\n\n" + NormalizePromptSegment(evidence.Text);
            }
            diagnostic["renderedCharacters"] = used; diagnostic["budgetRejectedUnits"] = rejected;
            diagnostic["duplicateUnits"] = duplicates; diagnostic["unitSubcapCharacters"] = allowance;
            diagnostic["budgetRejectedSourceIds"] = rejectedIds;
            diagnostic["contextCoverage"] = rejected > 0 || ReadString(diagnostic, "traversalCoverage", "") == "partial" ? "partial" : "bounded_complete";
            diagnostic["finalPromptBudgetStillRequired"] = true;
            var byObserver = ConversationReferenceDiagnostics.GetOrCreateValue(payload);
            lock (byObserver) byObserver[observer] = diagnostic;
            return blocks.Count == 0 ? "" : "\n\n" + string.Join("\n\n", blocks.OrderBy(b => b.Source.Day)
                .ThenBy(b => b.Source.Timestamp).ThenBy(b => b.Source.Sequence).ThenBy(b => b.Source.Id, StringComparer.Ordinal).Select(b => b.Text));
        }
    }
}
