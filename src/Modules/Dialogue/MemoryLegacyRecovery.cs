using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string LegacyMemoryRecoveryVersion = "verified-prefork-snapshot-v1";

        // A recorded parent name is not source authority. Require a retained
        // boundary, or the exact canonical fork counter plus its immediate child
        // event. If either witness was pruned, leave that edge unproved.
        private static long VerifiedLegacyMemoryForkSequence(ReignDbConnection connection, Dictionary<string, object> branch)
        {
            string parent = ReadString(branch, "parent_timeline_id", ""), fork = ReadString(branch, "fork_event_id", "");
            if (parent.Length == 0 || fork.Length == 0) return -1;
            var retained = QuerySql(connection, "SELECT sequence FROM world_history_events WHERE event_id=$fork AND timeline_id=$parent;",
                TestDict("fork", fork, "parent", parent)).SingleOrDefault();
            if (retained != null) return ReadLong(retained, "sequence", -1);
            var match = Regex.Match(fork, "^" + Regex.Escape(parent) + @"_([0-9]{12})_[0-9a-f]{8}$", RegexOptions.CultureInvariant);
            if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long sequence)
                || sequence <= 0 || sequence == long.MaxValue) return -1;
            string child = ReadString(branch, "timeline_id", "");
            var first = QuerySql(connection, "SELECT event_id,sequence FROM world_history_events WHERE timeline_id=$child ORDER BY sequence LIMIT 1;",
                TestDict("child", child)).SingleOrDefault();
            return first != null && ReadLong(first, "sequence", -1) == sequence + 1
                && Regex.IsMatch(ReadString(first, "event_id", ""), "^" + Regex.Escape(child) + "_"
                    + (sequence + 1).ToString("D12", CultureInfo.InvariantCulture) + @"_[0-9a-f]{8}$", RegexOptions.CultureInvariant)
                ? sequence : -1;
        }

        private static List<Dictionary<string, object>> VerifiedLegacyMemorySnapshots(ReignDbConnection connection,
            string campaign, Dictionary<string, object> point)
        {
            var proofs = new List<Dictionary<string, object>>();
            string timeline = ReadString(point, "timelineId", "");
            if (timeline.Length == 0 || ReadString(point, "savePointId", "").Length == 0) return proofs;
            var ancestors = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long ceiling = ReadLong(point, "worldHistorySequence", -1);
            DateTimeOffset before = DateTimeOffset.MaxValue;
            for (int depth = 0; depth < 32; depth++)
            {
                if (!seen.Add(timeline)) return new(); // Corrupt/cyclic lineage is never authority.
                var branch = QuerySql(connection, "SELECT * FROM world_history_timelines WHERE timeline_id=$timeline AND campaign_id=$campaign;",
                    TestDict("timeline", timeline, "campaign", campaign)).SingleOrDefault();
                if (branch == null || ReadString(branch, "parent_timeline_id", "").Length == 0) break;
                long forkSequence = VerifiedLegacyMemoryForkSequence(connection, branch);
                if (forkSequence < 0 || !DateTimeOffset.TryParse(ReadString(branch, "created_utc", ""), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var created)) break;
                ceiling = Math.Min(ceiling, forkSequence);
                if (ceiling < 0) break;
                if (created < before) before = created;
                timeline = ReadString(branch, "parent_timeline_id", "");
                ancestors[timeline] = TestDict("ceiling", ceiling, "beforeEpoch", before.ToUnixTimeMilliseconds() / 1000d,
                    "forkEventId", ReadString(branch, "fork_event_id", ""));
                if (depth == 31) return new();
            }
            if (ancestors.Count == 0) return proofs;
            foreach (var savedPoint in ReadDictionaryList(ReadSaveSyncLedger(campaign), "points").Take(128))
            {
                string ancestor = ReadString(savedPoint, "timelineId", "");
                if (!ancestors.TryGetValue(ancestor, out var boundary)) continue;
                double day = ReadDouble(savedPoint, "campaignTimeDays", double.MaxValue);
                long sequence = ReadLong(savedPoint, "worldHistorySequence", -1);
                string head = ReadString(savedPoint, "worldHistoryHeadEventId", "");
                if (sequence < 0 || sequence > ReadLong(boundary, "ceiling", -1) || head.Length == 0
                    || day > ReadDouble(point, "campaignTimeDays", 0d)) continue;
                string root = SaveSyncSnapshotRoot(campaign, savedPoint);
                string manifestPath = Path.Combine(root, "manifest.json");
                if (!File.Exists(manifestPath) || !Directory.Exists(Path.Combine(root, "campaign"))) continue;
                var manifest = ReadJsonObject(manifestPath);
                string fingerprint = ReadString(savedPoint, "snapshotFingerprint", "");
                if (fingerprint.Length == 0 || ReadString(manifest, "campaignId", "") != campaign
                    || ReadString(manifest, "fingerprint", "") != fingerprint
                    || ReadString(manifest, "databaseProvider", "") != "postgresql") continue;
                string snapshotPoint = SaveSyncPostgreSqlSnapshotPointId(savedPoint);
                string schema = ReignPostgreSqlStorage.SnapshotSchemaName(campaign, snapshotPoint);
                var metadata = QuerySql(connection, @"SELECT extract(epoch FROM created_utc)::double precision AS created_epoch
FROM reign_meta.save_sync_snapshots WHERE campaign_id=$campaign AND save_point_id=$point AND schema_name=$schema;",
                    TestDict("campaign", campaign, "point", snapshotPoint, "schema", schema)).SingleOrDefault();
                if (metadata == null || ReadDouble(metadata, "created_epoch", double.MaxValue) >= ReadDouble(boundary, "beforeEpoch", 0d)
                    || ReadString(QuerySql(connection, "SELECT to_regclass($table)::text AS name;", TestDict("table", schema + ".world_history_timelines")).Single(), "name", "").Length == 0) continue;
                var savedTimeline = QuerySql(connection, "SELECT timeline_id FROM " + schema
                    + ".world_history_timelines WHERE campaign_id=$campaign AND timeline_id=$timeline AND is_active=1 AND head_sequence=$sequence AND head_event_id=$head;",
                    TestDict("campaign", campaign, "timeline", ancestor, "sequence", sequence, "head", head)).SingleOrDefault();
                if (savedTimeline == null) continue;
                proofs.Add(TestDict("schema", schema, "timeline", ancestor, "day", day, "sequence", sequence, "savePointId", snapshotPoint,
                    "createdEpoch", ReadDouble(metadata, "created_epoch", 0d), "boundarySequence", ReadLong(boundary, "ceiling", -1),
                    "forkEventId", ReadString(boundary, "forkEventId", "")));
            }
            return proofs;
        }

        private static void RecoverLegacySnapshotMemorySources(ReignDbConnection connection, string campaign,
            Dictionary<string, object> point, string generation)
        {
            if (point == null || ReadString(point, "savePointId", "").Length == 0) return;
            var proofs = VerifiedLegacyMemorySnapshots(connection, campaign, point);
            foreach (var proof in proofs)
            {
                foreach (var entry in SnapshotMemoryKeys)
                {
                    string schema = ReadString(proof, "schema", "");
                    if (!TableExists(connection, entry.Key) || ReadString(QuerySql(connection, "SELECT to_regclass($table)::text AS name;",
                        TestDict("table", schema + "." + entry.Key)).Single(), "name", "").Length == 0) continue;
                    string day = "COALESCE((to_jsonb(saved)->>'world_day')::double precision,(to_jsonb(saved)->>'learned_day')::double precision,"
                        + "((to_jsonb(saved)->>'payload_json')::jsonb->>'worldDay')::double precision,0)";
                    ExecuteSql(connection, "INSERT INTO memory_restored_sources(source_table,source_id,restore_generation,content_hash) SELECT '"
                        + entry.Key + "',saved." + entry.Value + ",$generation," + SnapshotSourceHashSql("saved") + " FROM " + schema + "." + entry.Key
                        + " saved JOIN " + entry.Key + " current_source ON current_source." + entry.Value + "=saved." + entry.Value
                        + " AND " + SnapshotSourceHashSql("current_source") + "=" + SnapshotSourceHashSql("saved")
                        + " WHERE " + SnapshotSourceTimelineSql("saved") + "=$ancestor AND " + day + "<=$day"
                        + " ON CONFLICT(source_table,source_id,restore_generation) DO NOTHING;",
                        TestDict("generation", generation, "ancestor", ReadString(proof, "timeline", ""), "day", ReadDouble(proof, "day", 0d)));
                }
                RecoverLegacyPerformanceSources(connection, proof, generation);
            }
            ExecuteSql(connection, "UPDATE memory_restored_source_context SET legacy_recovery_version=$version WHERE state_id='current' AND restore_generation=$generation;",
                TestDict("version", LegacyMemoryRecoveryVersion, "generation", generation));
            LogOperational("memory.legacy_source_recovery", TestDict("campaignId", campaign, "savePointId", ReadString(point, "savePointId", ""),
                "restoreGeneration", generation, "verifiedSnapshots", proofs.Select(p => TestDict("savePointId", p["savePointId"],
                    "timeline", p["timeline"], "boundarySequence", p["boundarySequence"], "forkEventId", p["forkEventId"])).ToList()));
        }

        private static bool LegacyPerformanceReceiptMatches(Dictionary<string, object> current, Dictionary<string, object> expected)
        {
            foreach (string key in expected.Keys.Where(k => k != "knowledge_rules_json"))
            {
                if (key == "world_day" || key == "sequence")
                {
                    if (ReadDouble(current, key, double.NaN) != ReadDouble(expected, key, double.NaN)) return false;
                }
                else if (ReadString(current, key, "") != ReadString(expected, key, "")) return false;
            }
            try
            {
                string Rules(Dictionary<string, object> receipt)
                {
                    var rules = ReadDictionaryList(TestDict("rules", Json.DeserializeObject(ReadString(receipt, "knowledge_rules_json", ""))), "rules");
                    if (rules.Any(r => r.Count != 4 || new[] { "audience_type", "audience_id", "available_day", "acquisition_mode" }.Any(k => !r.ContainsKey(k)))) return "invalid";
                    return string.Join("\n", rules.Select(r => Json.Serialize(TestDict("audience_type", ReadString(r, "audience_type", ""),
                        "audience_id", ReadString(r, "audience_id", ""), "available_day", ReadDouble(r, "available_day", double.MaxValue),
                        "acquisition_mode", ReadString(r, "acquisition_mode", "")))).OrderBy(r => r, StringComparer.Ordinal));
                }
                string actual = Rules(current);
                return actual != "invalid" && actual == Rules(expected);
            }
            catch { return false; } // Malformed old receipts cannot supply authority.
        }

        private static void RecoverLegacyPerformanceSources(ReignDbConnection connection, Dictionary<string, object> proof, string generation)
        {
            if (!TableExists(connection, "character_performance_events")) return;
            string schema = ReadString(proof, "schema", ""); // Produced only by the canonical verified snapshot resolver.
            foreach (string table in new[] { "world_history_events", "world_history_entities", "world_history_knowledge_rules" })
                if (ReadString(QuerySql(connection, "SELECT to_regclass($table)::text AS name;", TestDict("table", schema + "." + table)).Single(), "name", "").Length == 0) return;
            var parameters = TestDict("ancestor", ReadString(proof, "timeline", ""), "day", ReadDouble(proof, "day", 0d), "sequence", ReadLong(proof, "sequence", -1));
            var candidates = QuerySql(connection, "SELECT p.*, " + SnapshotSourceHashSql("p") + " AS restored_content_hash,to_jsonb(saved)::text AS native_source_json"
                + " FROM character_performance_events p JOIN " + schema + ".world_history_events saved ON saved.event_id=p.event_id"
                + " JOIN world_history_events current_event ON current_event.event_id=saved.event_id AND " + SnapshotSourceHashSql("current_event") + "=" + SnapshotSourceHashSql("saved")
                + " WHERE p.timeline_id=$ancestor AND saved.timeline_id=$ancestor AND p.world_day<=$day AND saved.world_day<=$day"
                + " AND p.sequence<=$sequence AND saved.sequence<=$sequence;", parameters);
            if (candidates.Count == 0) return;
            string eligible = " WHERE EXISTS(SELECT 1 FROM character_performance_events p WHERE p.event_id=source.event_id AND p.timeline_id=$ancestor AND p.world_day<=$day AND p.sequence<=$sequence)";
            var entities = QuerySql(connection, "SELECT source.* FROM " + schema + ".world_history_entities source" + eligible + " ORDER BY source.event_id,source.ordinal;", parameters)
                .ToLookup(r => ReadString(r, "event_id", ""));
            var rules = QuerySql(connection, "SELECT source.event_id,source.audience_type,source.audience_id,source.available_day,source.acquisition_mode FROM "
                + schema + ".world_history_knowledge_rules source" + eligible + ";", parameters).ToLookup(r => ReadString(r, "event_id", ""));
            var proven = new List<Dictionary<string, object>>();
            foreach (var group in candidates.GroupBy(r => ReadString(r, "event_id", "")))
            {
                var eventRules = rules[group.Key].Select(r => r.Where(k => k.Key != "event_id").ToDictionary(k => k.Key, k => k.Value)).ToList();
                var expected = BuildPlayerPerformanceReceipts(TryParseJsonObject(ReadString(group.First(), "native_source_json", "{}")), entities[group.Key].ToList(), eventRules);
                proven.AddRange(group.Where(row => expected.Any(receipt => LegacyPerformanceReceiptMatches(row, receipt))));
            }
            // Membership only: neither source receipts nor native effects are recreated.
            foreach (var batch in proven.Chunk(100))
            {
                var values = new List<string>(); var args = TestDict("generation", generation);
                for (int i = 0; i < batch.Length; i++)
                {
                    values.Add("('character_performance_events',$id" + i + ",$generation,$hash" + i + ")");
                    args["id" + i] = ReadString(batch[i], "receipt_id", ""); args["hash" + i] = ReadString(batch[i], "restored_content_hash", "");
                }
                ExecuteSql(connection, "INSERT INTO memory_restored_sources(source_table,source_id,restore_generation,content_hash) VALUES " + string.Join(",", values)
                    + " ON CONFLICT(source_table,source_id,restore_generation) DO NOTHING;", args);
            }
        }

        private static bool IsUnsupportedRecallAbsenceState(string value) => Regex.Matches(value ?? "",
            @"\b(?:no|without|lack(?:s|ing)?(?:\s+any)?)\s+(?:memory|memories|recollection)\b|\b(?:do(?:es)?\s+not|don['’]t|doesn['’]t|can(?:not|['’]t)|unable\s+to)\s+(?:remember|recall)\b|\b(?:memory|recall)\s+(?:gap|failure|loss)\b|\bgap\s+in\s+(?:my\s+|her\s+|his\s+)?memor(?:y|ies)\b|\b(?:recover(?:ing|ed|s)?|reconstruct(?:ing|ed|s)?)\b[^;.!?\r\n]{0,100}\b(?:memor(?:y|ies)|recall)\b|\b(?:lost|missing|inaccessible|forgotten)\s+memor(?:y|ies)\b|\bmemor(?:y|ies)\b[^;.!?\r\n]{0,40}\b(?:lost|missing|inaccessible)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Cast<Match>().Any(m => !NegatedRecallLossMatch(value ?? "", m))
            || BroadRecallLossNarrative(value).Length > 0;

        private static bool IsUnsupportedRecallAbsenceDerived(Dictionary<string, object> row,
            ReignDbConnection connection = null, KnowledgeAccessContext knowledge = null) => row != null
            && (row.ContainsKey("memory_id") || row.ContainsKey("belief_id") || row.ContainsKey("comprehension_id")
                || row.ContainsKey("assertion_id") && IsPrivateMentalLayer(ReadString(row, "assertion_kind", "")))
            && IsUnsupportedRecallAbsenceState(ReadFirstString(row, "claim", "text", "summary"))
            && !HasStoredRecallLossSupport(connection, knowledge, ReadFirstString(row, "claim", "text", "summary"));

        // A derived statement cannot establish its own exception. Read only
        // canonical player sources, with the normal observer and time gates.
        private static bool HasStoredRecallLossSupport(ReignDbConnection connection, KnowledgeAccessContext knowledge, string claim)
        {
            string cause = RecallLossCause(claim);
            if (connection == null || knowledge == null || cause.Length == 0) return false;
            string needle = cause == "head_injury" ? "head" : cause;
            var sources = QuerySql(connection, @"SELECT t.* FROM conversation_turns t
WHERE t.role='player' AND t.status='active' AND t.text ILIKE $cause AND t.text ~* 'memor|amnesia'
AND t.world_day<=$day AND t.accepted_sequence<=$sequence AND t.ts<=$timestamp
AND (t.speaker_id=$observer OR t.participants_json::jsonb ? $observer)
ORDER BY t.accepted_sequence DESC,t.ts DESC,t.turn_order DESC LIMIT 128;",
                TestDict("cause", "%" + needle + "%", "day", knowledge.WorldDay, "sequence", knowledge.AsOfAcceptedSequence,
                    "timestamp", knowledge.AsOfTimestamp, "observer", knowledge.NpcId));
            foreach (var source in sources.Where(s => PrecisionTurnVisible(s, knowledge.NpcId, knowledge, connection)))
            {
                var name = QuerySql(connection, @"SELECT speaker_name FROM conversation_turns
WHERE session_id=$session AND role='npc' AND speaker_id=$observer AND status='active'
AND world_day<=$day AND accepted_sequence<=$sequence AND ts<=$timestamp
ORDER BY turn_order LIMIT 1;", TestDict("session", ReadString(source, "session_id", ""), "observer", knowledge.NpcId,
                    "day", knowledge.WorldDay, "sequence", knowledge.AsOfAcceptedSequence, "timestamp", knowledge.AsOfTimestamp)).FirstOrDefault();
                string rendered = "Source session metadata: participants="
                    + string.Join(", ", TextListFromJson(ReadString(source, "participants_json", "[]"))) + ";\n"
                    + RenderExactHistoryTurn(source, knowledge);
                if (HasIndependentRecallLossEvent(rendered, knowledge.NpcId, ReadString(name, "speaker_name", ""), claim)) return true;
            }
            return false;
        }

        private static bool HasKnownRecallLossSupport(string claim, Dictionary<string, object> payload, string observer, string campaign)
        {
            if (RecallLossCause(claim).Length == 0 || payload == null || string.IsNullOrWhiteSpace(observer)
                || string.IsNullOrWhiteSpace(campaign)) return false;
            var access = BuildKnowledgeAccessContext(observer, ReadFirstString(payload, "playerHeroStringId", "playerId"), "", payload);
            using var connection = OpenCampaignConnection(campaign);
            return HasStoredRecallLossSupport(connection, access, claim);
        }

        // Quarantine only unsupported derived-state clauses on read. Original
        // files and accepted dialogue stay available for audit. A lookup failure
        // cannot outweigh an available original conversation.
        private static Dictionary<string, object> WithoutRecallAbsenceState(Dictionary<string, object> state,
            string campaign = "", string observer = "", Dictionary<string, object> payload = null)
        {
            var clean = new Dictionary<string, object>(state ?? new(), StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "mood", "currentPlan", "currentCrisis" })
            {
                string value = ReadString(clean, key, "");
                bool Unsupported(string text) => IsUnsupportedRecallAbsenceState(text) && !HasKnownRecallLossSupport(text, payload, observer, campaign);
                if (!Unsupported(value)) continue;
                var kept = new List<string>();
                foreach (string clause in value.Split(';'))
                {
                    if (!Unsupported(clause)) { if (clause.Trim().Length > 0) kept.Add(clause.Trim()); continue; }
                    var parts = Regex.Split(clause, @"\s+[—–]\s+");
                    if (parts.Length > 1) kept.AddRange(parts.Where(p => !Unsupported(p) && p.Trim().Length > 0).Select(p => p.Trim()));
                }
                clean[key] = string.Join("; ", kept);
            }
            return clean;
        }
    }
}
