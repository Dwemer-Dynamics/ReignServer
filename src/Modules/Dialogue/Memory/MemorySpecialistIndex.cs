using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private const string MemorySpecialistSchema = "reign-memory-specialist-v1";
        private const string MemorySpecialistIndexSchema = MemorySpecialistSchema + "+player-narrative-v3";
        private static readonly Dictionary<string, string> MemorySpecialistTopics = new Dictionary<string, string>
        {
            ["family"] = @"\b(family|father|mother|daughter|son|sibling|parents?|children)\b",
            ["marriage"] = @"\b(marri\w*|wedding|husband|wife|spouse|divorce|betroth\w*)\b",
            ["bereavement"] = @"\b(grief|grieving|widow\w*|bereav\w*|mourn\w*|died|death|funeral)\b",
            ["friendship"] = @"\b(friend\w*|companionship|reconcil\w*)\b",
            ["romance"] = @"\b(romance|romantic|lover|affection|courtship|kiss\w*)\b",
            ["leadership"] = @"\b(leader\w*|leadership|allegiance|loyalty|loyal|follow you|follow me|follow him|follow her)\b",
            ["independence"] = @"\b(independent|independence|self.rule|sovereign\w*|nation|kingdom)\b",
            ["commerce"] = @"\b(trad\w*|commerce|merchant\w*|caravan\w*|workshop\w*|profit\w*)\b",
            ["land"] = @"\b(land|estate\w*|fief\w*|town|homestead|property)\b",
            ["military"] = @"\b(army|armies|soldier\w*|troops?|battle\w*|siege|warfare|elite units?)\b",
            ["tournaments"] = @"\b(tournament\w*|arena|joust\w*|champion\w*|prize\w*)\b",
            ["correspondence"] = @"\b(letter\w*|courier\w*|correspondence|wrote|written|messenger\w*)\b",
            ["gifts"] = @"\b(gift\w*|keepsake\w*|carv\w*|memento\w*|present I|present you)\b",
            ["botany"] = @"\b(botan\w*|plant\w*|flower\w*|herb\w*|garden\w*|petal\w*|seed\w*|flora)\b",
            ["travel"] = @"\b(travel\w*|journey\w*|road|visit\w*|return\w*|departure)\b",
            ["captivity"] = @"\b(captiv\w*|prison\w*|ransom\w*|rescue\w*|imprison\w*|released)\b",
            ["identity"] = @"\b(name|identity|called|nickname|alias|recogniz\w*)\b",
            ["commitments"] = @"\b(promise\w*|commit\w*|agree\w*|oath\w*|obligation\w*|pledge\w*)\b",
            ["boundaries"] = @"\b(boundar\w*|permission|consent|refus\w*|not willing|stop asking)\b"
        };

        private static List<string> MemorySpecialistQueryTopics(string text) => MemorySpecialistTopics
            .Where(p => Regex.IsMatch(text ?? "", p.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(p => p.Key).ToList();

        private static void EnsureMemorySpecialistSchema(ReignDbConnection connection)
        {
            DialogueSchema.CreateMemorySpecialistSources(connection);
            DialogueSchema.EnsureColumnMemorySpecialistSourcesPublicTopicsJson(connection);
            DialogueSchema.EnsureColumnMemorySpecialistSourcesPublicEntitiesJson(connection);
            DialogueSchema.EnsureColumnMemorySpecialistSourcesSourceRevision(connection);
            EnsureLateInteractionMemorySchema(connection);
        }

        internal static string MemorySpecialistSourceHash(Dictionary<string, object> source) => PromptHash(CanonicalJson(
            new[] { "turn_id", "session_id", "exchange_id", "role", "speaker_id", "speaker_name", "text", "participants_json", "payload_json", "status", "world_day", "ts", "turn_order", "accepted_sequence" }
                .ToDictionary(k => k, k => source != null && source.TryGetValue(k, out var value) ? value : null)));

        private static Dictionary<string, object> BuildMemorySpecialistDocument(Dictionary<string, object> source,
            Dictionary<string, object> extraction = null)
        {
            string text = ConversationTurnTextForRecall(source), id = ReadString(source, "turn_id", ""), speaker = ReadString(source, "speaker_id", "");
            var payload = TryParseJsonObject(ReadString(source, "payload_json", "{}")) ?? TestDict();
            var names = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            void Name(string name, string identity)
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(identity)) return;
                if (!names.TryGetValue(name, out var identities)) names[name] = identities = new HashSet<string>(StringComparer.Ordinal);
                identities.Add(identity);
            }
            Name(ReadString(source, "speaker_name", ""), speaker);
            Name(ReadFirstString(payload, "playerName", "playerHeroName"), ReadFirstString(payload, "playerHeroStringId", "playerId"));
            Name(ReadFirstString(payload, "speakerName", "npcName"), ReadFirstString(payload, "heroStringId", "speakerHeroStringId"));
            foreach (var profile in MergedInteractionParticipantProfiles(payload)) Name(ReadString(profile, "name", ""), Program.CharacterIdFrom(profile));
            var entities = new List<Dictionary<string, object>>();
            foreach (var pair in names)
                foreach (Match match in Regex.Matches(text, @"(?<![\p{L}\p{Nd}_])" + Regex.Escape(pair.Key) + @"(?![\p{L}\p{Nd}_])", RegexOptions.IgnoreCase))
                    entities.Add(TestDict("kind", "person", "role", "mentioned", "text", match.Value, "start", match.Index, "end", match.Index + match.Length,
                        "entityId", pair.Value.Count == 1 ? pair.Value.Single() : "", "candidateEntityIds", pair.Value.OrderBy(x => x).ToList(),
                        "authority", pair.Value.Count == 1 ? "source_metadata_name_match" : "ambiguous_identity"));
            var topics = MemorySpecialistQueryTopics(text);
            bool valid = ReadBool(extraction, "ok", false) && ReadString(extraction, "schema", "") == MemorySpecialistSchema
                && ReadString(extraction, "sourceId", "") == id && ReadString(extraction, "offsetEncoding", "") == "utf16"
                && string.Equals(ReadString(extraction, "textHash", ""), SemanticSha256Hex(text), StringComparison.OrdinalIgnoreCase);
            int rejected = 0;
            if (valid)
            {
                foreach (var item in ReadDictionaryList(extraction, "entities").Take(128))
                {
                    int start = ReadInt(item, "start", -1), end = ReadInt(item, "end", -1);
                    string value = ReadString(item, "text", ""), kind = ReadString(item, "kind", "");
                    double confidence = ReadDouble(item, "confidence", double.NaN);
                    if (start < 0 || end <= start || end > text.Length || text.Substring(start, end - start) != value
                        || !double.IsFinite(confidence) || confidence < .5 || confidence > 1
                        || !new[] { "person", "place", "object", "goal", "commitment", "life_event" }.Contains(kind)) { rejected++; continue; }
                    var known = names.TryGetValue(value, out var identities) ? identities : new HashSet<string>();
                    entities.Add(TestDict("kind", kind, "role", "mentioned", "text", value, "start", start, "end", end,
                        "entityId", kind == "person" && known.Count == 1 ? known.Single() : "", "candidateEntityIds", known.ToList(),
                        "confidence", confidence, "authority", "candidate_mention"));
                }
                topics.AddRange(ReadStringList(extraction, "topics").Where(MemorySpecialistTopics.ContainsKey));
            }
            return TestDict("schema", MemorySpecialistSchema, "sourceId", id, "sourceHash", MemorySpecialistSourceHash(source),
                "textHash", SemanticSha256Hex(text), "speakerId", speaker, "audienceIds", DialogueService.TextListFromJson(ReadString(source, "participants_json", "[]")),
                "actorIds", new string[0], "addresseeIds", new string[0], "entities", entities,
                "topics", topics.Distinct(StringComparer.Ordinal).OrderBy(x => x).ToList(), "authority", "retrieval_candidates_only",
                "extractorStatus", extraction == null ? "local_metadata" : valid ? "source_bound_candidates" : "rejected_source_identity",
                "extractorModelHash", valid ? ReadString(extraction, "modelHash", "") : "", "rejectedSpans", rejected);
        }

        private static void StoreMemorySpecialistDocument(ReignDbConnection connection, Dictionary<string, object> source,
            Dictionary<string, object> extraction = null, Dictionary<string, object> attempt = null)
        {
            if (ReadString(source, "status", "active") != "active" || string.IsNullOrWhiteSpace(ConversationTurnTextForRecall(source))) return;
            var current = DialogueStore.SelectConversationTurns77(connection, TestDict("id", ReadString(source, "turn_id", ""))).SingleOrDefault();
            if (current == null || MemorySpecialistSourceHash(current) != MemorySpecialistSourceHash(source)) return;
            var doc = BuildMemorySpecialistDocument(source, extraction);
            foreach (var item in attempt ?? TestDict()) doc[item.Key] = item.Value;
            string visible = ObserverConversationSourceText(source, "__other_observer__");
            var publicTopics = MemorySpecialistQueryTopics(visible);
            var publicEntities = ReadDictionaryList(doc, "entities").Where(e => visible.Contains(ReadString(e, "text", ""), StringComparison.Ordinal)).ToList();
            DialogueStore.UpsertMemorySpecialistSources(connection, TestDict(
                "id", ReadString(doc, "sourceId", ""), "hash", ReadString(doc, "sourceHash", ""), "textHash", ReadString(doc, "textHash", ""),
                "schema", MemorySpecialistIndexSchema, "speaker", ReadString(doc, "speakerId", ""), "topics", Json.Serialize(doc["topics"]),
                "entities", Json.Serialize(doc["entities"]), "payload", Json.Serialize(doc), "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                "publicTopics", Json.Serialize(publicTopics), "publicEntities", Json.Serialize(publicEntities), "revision", ReadString(current, "source_revision", "")));
        }

        private static string QualifiedMemoryExtractorHash(Dictionary<string, object> settings)
        {
            string hash = ReadString(settings, "localMemoryExtractorQualifiedModelHash", "").ToLowerInvariant();
            return ReadBool(settings, "enableLocalMemoryExtractor", false) && Regex.IsMatch(hash, @"\A[0-9a-f]{64}\z") ? hash : "";
        }

        private static bool MemorySpecialistExtractionDue(Dictionary<string, object> document, string modelHash, long now)
        {
            if (modelHash.Length == 0) return false;
            if (ReadString(document, "extractorModelHash", "") == modelHash
                && ReadString(document, "extractorStatus", "") == "source_bound_candidates") return false;
            if (ReadString(document, "extractorExpectedModelHash", "") != modelHash) return true;
            return ReadInt(document, "extractorAttempts", 0) < 4 && ReadLong(document, "extractorNextAttemptTs", 0) <= now;
        }

        private static void IndexMemorySpecialistSource(string campaign, Dictionary<string, object> source,
            Dictionary<string, object> settings, bool allowExtraction = true)
        {
            if (ReadString(source, "turn_id", "").Length == 0) return;
            try
            {
                Dictionary<string, object> old;
                using (var read = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    old = DialogueStore.SelectMemorySpecialistSources(read, TestDict("id", source["turn_id"])).FirstOrDefault();
                }
                bool fresh = ReadString(old, "schema_version", "") == MemorySpecialistIndexSchema
                    && ReadString(old, "source_hash", "") == MemorySpecialistSourceHash(source);
                var previous = fresh ? TryParseJsonObject(ReadString(old, "payload_json", "{}")) : null;
                string qualifiedHash = QualifiedMemoryExtractorHash(settings);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                bool shouldExtract = allowExtraction && string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value)
                    && MemorySpecialistExtractionDue(previous, qualifiedHash, now);
                if (fresh && !shouldExtract)
                {
                    // xmin is only a cheap invalidation hint. The content hash
                    // remains authoritative, including after restore or VACUUM.
                    using var read = ReignDatabase.OpenCampaignConnection(campaign);
                    DialogueStore.UpdateMemorySpecialistSources(read, TestDict("id", source["turn_id"], "hash", MemorySpecialistSourceHash(source),
                        "revision", ReadString(source, "source_revision", "")));
                    return;
                }
                Dictionary<string, object> extraction = null;
                Dictionary<string, object> attempt = null;
                if (shouldExtract)
                {
                    string text = ConversationTurnTextForRecall(source);
                    string outcome = "timeout_or_unavailable";
                    try { extraction = TryParseJsonObject(DialogueService.PostJsonToUrl(SemanticWorkerUrl(settings, "/memory/extract"),
                        Json.Serialize(TestDict("sourceId", ReadString(source, "turn_id", ""), "text", text, "textHash", SemanticSha256Hex(text))), 5000)); }
                    catch { /* A background specialist cannot reject accepted dialogue. */ }
                    if (extraction != null) outcome = ReadString(extraction, "status", ReadBool(extraction, "ok", false) ? "returned_candidates" : "invalid_response");
                    if (!string.Equals(ReadString(extraction, "modelHash", ""), qualifiedHash, StringComparison.Ordinal))
                    { if (ReadBool(extraction, "ok", false)) outcome = "unqualified_model"; extraction = null; }
                    int attempts = (ReadString(previous, "extractorExpectedModelHash", "") == qualifiedHash ? ReadInt(previous, "extractorAttempts", 0) : 0) + 1;
                    attempt = TestDict("extractorExpectedModelHash", qualifiedHash, "extractorAttempts", attempts,
                        "extractorNextAttemptTs", now + 60L * (1 << Math.Min(attempts - 1, 3)), "extractorLastOutcome", outcome);
                }
                using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                using var transaction = connection.BeginTransaction();
                AcquirePostgreSqlTransactionMutationLock(connection);
                var current = DialogueStore.SelectConversationTurns6(connection, TestDict("id", source["turn_id"])).SingleOrDefault();
                if (current != null && MemorySpecialistSourceHash(current) == MemorySpecialistSourceHash(source))
                    StoreMemorySpecialistDocument(connection, current, extraction, attempt);
                transaction.Commit();
            }
            catch (Exception ex) { Program.LogOperational("memory.specialist_index_deferred", TestDict("sourceId", ReadString(source, "turn_id", ""), "errorType", ex.GetType().Name)); }
        }

        private static List<Dictionary<string, object>> FindMemorySpecialistTurns(ReignDbConnection connection, MemoryQueryPlan plan,
            DialogueService.KnowledgeAccessContext access, int limit, int maximumPages, bool requirePlayerSource)
        {
            if (!WorldSimulationService.TableExists(connection, "memory_specialist_sources")) return new List<Dictionary<string, object>>();
            var topics = MemorySpecialistQueryTopics(plan.Query);
            var ids = plan.ExplicitEntities.Distinct(StringComparer.Ordinal).ToList();
            if (topics.Count == 0 && ids.Count == 0) return new List<Dictionary<string, object>>();
            var args = TestDict("npc", plan.Observer, "timeline", plan.Timeline, "day", plan.WorldDay,
                "asOfSequence", access.AsOfAcceptedSequence, "asOfTimestamp", access.AsOfTimestamp, "schema", MemorySpecialistIndexSchema);
            var matches = new List<string>();
            foreach (var topic in topics)
            {
                string key = "topic" + matches.Count;
                args[key] = topic;
                matches.Add("((CASE WHEN i.speaker_id=$npc THEN i.topics_json::jsonb ELSE i.public_topics_json::jsonb END) ? $" + key + ")");
            }
            foreach (var id in ids)
            {
                string key = "entity" + matches.Count;
                args[key] = id;
                matches.Add("(i.speaker_id=$" + key + " OR EXISTS(SELECT 1 FROM jsonb_array_elements(CASE WHEN i.speaker_id=$npc THEN i.entities_json::jsonb ELSE i.public_entities_json::jsonb END) x WHERE x->>'entityId'=$" + key + "))");
            }
            string score = string.Join("+", matches.Select(m => "CASE WHEN " + m + " THEN 1 ELSE 0 END"));
            string sql = DialogueStore.SelectMemorySpecialistSourcesSql(score, string.Join(" OR ", matches), MemorySessionObserverSql("s", "t"), MemorySourceTimelineSql("conversation_turns", "t"));
            return QuerySourceBearingPrecisionTurns(connection, sql, args, access, limit, plan.SuppliedSources,
                    maximumPages, requirePlayerSource,
                    r => ReadString(r, "specialist_source_hash", "") == MemorySpecialistSourceHash(r));
        }

        // This sweep is independent of summary/embedding providers. It indexes
        // accepted originals after their transaction has committed; never on the
        // reply's acceptance path. New and old sources both make bounded progress.
        private static void ReconcileMemorySpecialistIndex(string campaign, int limit = 128)
        {
            try
            {
                List<Dictionary<string, object>> sources;
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    if (!WorldSimulationService.TableExists(connection, "memory_specialist_sources")) return;
                    string missing = DialogueStore.SelectConversationTurnsSql5;
                    var args = TestDict("schema", MemorySpecialistIndexSchema, "limit", Math.Max(1, Math.Min(256, limit)));
                    sources = DialogueStore.Statement3(connection, missing,args)
                        .Concat(DialogueStore.Statement4(connection, missing,args))
                        .GroupBy(r => ReadString(r, "turn_id", "")).Select(g => g.First()).ToList();
                }
                var settings = Program.LoadSettings();
                foreach (var source in sources)
                {
                    if (Program.PriorityBackgroundWorkShouldYield()) break;
                    IndexMemorySpecialistSource(campaign, source, settings, allowExtraction: false);
                }
                if (!Program.PriorityBackgroundWorkShouldYield()) ReconcileLateInteractionMemory(campaign, settings);
                // Qualified model upgrades have their own small budget. A cold
                // load timeout is retried with backoff, never on a dialogue turn;
                // four failures exhaust this source/model revision.
                string modelHash = QualifiedMemoryExtractorHash(settings);
                if (modelHash.Length > 0 && string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value))
                {
                    using var read = ReignDatabase.OpenCampaignConnection(campaign);
                    var upgrades = DialogueStore.SelectConversationTurns78(read, TestDict("schema", MemorySpecialistIndexSchema, "model", modelHash, "now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                    foreach (var source in upgrades)
                    {
                        if (Program.PriorityBackgroundWorkShouldYield()) break;
                        IndexMemorySpecialistSource(campaign, source, settings);
                    }
                }
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    string timeline = WorldSimulationService.ActiveWorldHistoryTimeline(connection);
                    var native = DialogueStore.SelectWorldHistoryEvents6(connection, TestDict("timeline", timeline)).FirstOrDefault();
                    var latest = DialogueStore.SelectConversationTurns79(connection);
                    foreach (var row in latest)
                    {
                        if (Program.PriorityBackgroundWorkShouldYield()) break;
                        string owner = ReadString(row, "speaker_id", "");
                        CharactersService.RefreshCharacterLifeProjection(campaign, owner, new DialogueService.KnowledgeAccessContext { NpcId = owner, TimelineId = timeline,
                            WorldDay = Math.Max(ReadDouble(row, "day", 0), ReadDouble(native, "day", 0)), AsOfTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                    }
                }
            }
            catch (Exception ex) { Program.LogOperational("memory.specialist_reconcile_deferred", TestDict("campaignId", campaign, "errorType", ex.GetType().Name)); }
        }

        private static List<Dictionary<string, object>> FuseMemorySpecialistRanks(List<Dictionary<string, object>> lexical,
            List<Dictionary<string, object>> specialist, int limit)
            => FuseMemorySearchRanks(limit, lexical, specialist);

        private static List<Dictionary<string, object>> FuseMemorySearchRanks(int limit, params List<Dictionary<string, object>>[] lists)
        {
            var nonempty = lists.Where(l => l.Count > 0).ToList();
            if (nonempty.Count == 0) return new();
            // Preserve the established lexical-only score scale. A sole later
            // discovery list still needs rank scores or downstream sorting loses it.
            if (nonempty.Count == 1 && lists[0].Count > 0) return nonempty[0].Take(limit).ToList();
            var rows = nonempty.SelectMany(l => l).GroupBy(r => ReadString(r, "turn_id", ""), StringComparer.Ordinal)
                .Select(g => new Dictionary<string, object>(g.First())).ToList();
            foreach (var row in rows)
            {
                string id = ReadString(row, "turn_id", "");
                row["relevance"] = 150d * nonempty.Sum(list => {
                    int rank = list.FindIndex(r => ReadString(r, "turn_id", "") == id);
                    return rank < 0 ? 0 : 1d / (61 + rank);
                });
            }
            return rows.OrderByDescending(r => ReadDouble(r, "relevance", 0)).ThenBy(r => ReadString(r, "turn_id", ""), StringComparer.Ordinal).Take(limit).ToList();
        }
    }
}
