using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // The database is still read on every day. Only parsing is cached, keyed
        // by the exact stored document, so edits, rollback and restore cannot
        // return a stale profile. Callers receive an independent deep copy.
        private sealed class RelationshipHeroDocumentCache
        {
            private readonly object gate = new object();
            private readonly int capacity;
            private readonly int characterBudget;
            private int characters;
            private readonly LinkedList<KeyValuePair<string, Dictionary<string, object>>> recent =
                new LinkedList<KeyValuePair<string, Dictionary<string, object>>>();
            private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Dictionary<string, object>>>> entries =
                new Dictionary<string, LinkedListNode<KeyValuePair<string, Dictionary<string, object>>>>(StringComparer.Ordinal);

            public RelationshipHeroDocumentCache(int capacity = 8192, int characterBudget = 8000000)
            { this.capacity = capacity; this.characterBudget = characterBudget; }

            public void Clear()
            {
                lock (gate) { entries.Clear(); recent.Clear(); characters = 0; }
            }

            public void Remember(string document, Dictionary<string, object> normalizedProfile)
            {
                // Ingestion has already normalized this exact serialized document.
                // Keep our own copy so later caller edits cannot poison the cache.
                if (capacity <= 0 || document.Length > characterBudget) return;
                lock (gate)
                {
                    if (entries.ContainsKey(document)) return;
                    while (entries.Count >= capacity || characters + document.Length > characterBudget)
                    {
                        var oldest = recent.Last;
                        characters -= oldest.Value.Key.Length;
                        entries.Remove(oldest.Value.Key);
                        recent.RemoveLast();
                    }
                    var copy = (Dictionary<string, object>)CopyRelationshipHeroValue(normalizedProfile);
                    var node = recent.AddFirst(new KeyValuePair<string, Dictionary<string, object>>(document, copy));
                    entries.Add(document, node);
                    characters += document.Length;
                }
            }

            public Dictionary<string, object> Read(string document, out bool hit)
            {
                document = document ?? "{}";
                Dictionary<string, object> profile;
                lock (gate)
                {
                    hit = entries.TryGetValue(document, out var node);
                    if (hit)
                    {
                        recent.Remove(node);
                        recent.AddFirst(node);
                        profile = node.Value.Value;
                    }
                    else profile = null;
                }
                if (!hit)
                {
                    var parsed = TryParseJsonObject(document);
                    profile = parsed == null ? null : NormalizeAmbientLifecycleHero(parsed);
                    if (document.Length <= characterBudget && capacity > 0)
                        lock (gate)
                        {
                            if (!entries.ContainsKey(document))
                            {
                                while (entries.Count >= capacity || characters + document.Length > characterBudget)
                                {
                                    var oldest = recent.Last;
                                    characters -= oldest.Value.Key.Length;
                                    entries.Remove(oldest.Value.Key);
                                    recent.RemoveLast();
                                }
                                var node = recent.AddFirst(new KeyValuePair<string, Dictionary<string, object>>(document, profile));
                                entries.Add(document, node);
                                characters += document.Length;
                            }
                        }
                }
                return (Dictionary<string, object>)CopyRelationshipHeroValue(profile);
            }
        }

        private static readonly RelationshipHeroDocumentCache RelationshipHeroDocuments = new RelationshipHeroDocumentCache();

        private static object CopyRelationshipHeroValue(object value)
        {
            if (value is Dictionary<string, object> dictionary)
            {
                var copy = new Dictionary<string, object>(dictionary.Count, dictionary.Comparer);
                foreach (var item in dictionary) copy.Add(item.Key, CopyRelationshipHeroValue(item.Value));
                return copy;
            }
            if (value is object[] array) return array.Select(CopyRelationshipHeroValue).ToArray();
            if (value is List<object> list) return list.Select(CopyRelationshipHeroValue).ToList();
            if (value is System.Collections.ArrayList arrayList)
                return new System.Collections.ArrayList(arrayList.Cast<object>().Select(CopyRelationshipHeroValue).ToArray());
            return value;
        }

        private static List<Dictionary<string, object>> LoadRelationshipLifecycleWorkset(
            ReignDbConnection connection, IEnumerable<string> pairKeys)
        {
            if (RelationshipThroughputOriginalReadPath || !ReignPostgreSqlDialect.IsPostgreSql(connection))
                return QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle;");
            return QuerySql(connection, @"SELECT lifecycle.* FROM relationship_pair_lifecycle lifecycle
JOIN jsonb_array_elements_text(CAST($pairs AS jsonb)) requested ON requested.value=lifecycle.pair_key;",
                new Dictionary<string, object> { ["pairs"] = Json.Serialize(pairKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList()) });
        }

        private static readonly object RelationshipReadyGate = new object();
        private static readonly Dictionary<string, int> RelationshipReadyCampaigns =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> RelationshipReadyVersions =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static DateTime RelationshipReadyReconciledUtc;
        private static readonly HashSet<string> RelationshipRecoveredCampaigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> RelationshipCampaignGenerations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string RelationshipCampaignGeneration(string campaignId)
        {
            lock (RelationshipReadyGate)
            {
                if (!RelationshipCampaignGenerations.TryGetValue(campaignId, out string generation))
                    RelationshipCampaignGenerations[campaignId] = generation = Guid.NewGuid().ToString("N");
                return generation;
            }
        }

        private static void ResetRelationshipCampaignScheduling(string campaignId)
        {
            lock (RelationshipReadyGate)
            {
                RelationshipCampaignGenerations[campaignId] = Guid.NewGuid().ToString("N");
                RelationshipRecoveredCampaigns.Remove(campaignId);
                MarkRelationshipCampaignReady(campaignId);
            }
        }

        private static void RecoverInterruptedRelationshipDays(ReignDbConnection connection, string campaignId)
        {
            // Called only by the sole sequential worker under CampaignDataGate.
            // On startup/restore, no prior worker can still own a processing row.
            bool initial;
            lock (RelationshipReadyGate) initial = !RelationshipRecoveredCampaigns.Contains(campaignId);
            ExecuteSql(connection, @"UPDATE relationship_daily_inputs
SET status='pending',last_error='worker_recovered_interrupted_day'
WHERE status='processing' AND ($initial=1 OR started_ts<$cutoff);",
                new Dictionary<string, object> { ["initial"] = initial ? 1 : 0,
                    ["cutoff"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds() });
            lock (RelationshipReadyGate) RelationshipRecoveredCampaigns.Add(campaignId);
        }

        private static void MarkRelationshipCampaignReady(string campaignId)
        {
            lock (RelationshipReadyGate)
            {
                RelationshipReadyCampaigns[campaignId] = Math.Max(1,
                    RelationshipReadyCampaigns.TryGetValue(campaignId, out int count) ? count : 0);
                RelationshipReadyVersions[campaignId] = RelationshipReadyVersion(campaignId) + 1;
            }
            ContinuousRelationshipSignal.Set();
        }

        private static long RelationshipReadyVersion(string campaignId)
        {
            lock (RelationshipReadyGate)
                return RelationshipReadyVersions.TryGetValue(campaignId, out long version) ? version : 0;
        }

        private static void ObserveRelationshipCampaignPending(string campaignId, int count, long version)
        {
            lock (RelationshipReadyGate)
                if (version == RelationshipReadyVersion(campaignId))
                    RelationshipReadyCampaigns[campaignId] = Math.Max(0, count);
        }

        private static string[] RelationshipCampaignCandidates(string root)
        {
            lock (RelationshipReadyGate)
            {
                // Durable queues are authoritative. Reconciliation also recovers lost
                // notifications and Save Sync replacements without persisting a second queue.
                if ((DateTime.UtcNow - RelationshipReadyReconciledUtc).TotalSeconds >= 30)
                {
                    foreach (string directory in Directory.GetDirectories(root))
                    {
                        string campaign = Path.GetFileName(directory);
                        RelationshipReadyCampaigns[campaign] = Math.Max(1,
                            RelationshipReadyCampaigns.TryGetValue(campaign, out int count) ? count : 0);
                    }
                    RelationshipReadyReconciledUtc = DateTime.UtcNow;
                }
                return RelationshipReadyCampaigns.Where(x => x.Value > 0)
                    .Select(x => Path.Combine(root, x.Key)).Where(Directory.Exists).ToArray();
            }
        }

        private static int CachedQueuedRelationshipDays()
        {
            lock (RelationshipReadyGate) return RelationshipReadyCampaigns.Values.Sum();
        }

        private static int CountUnrepresentedNativeFlags(ReignDbConnection connection, string player)
        {
            return ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count FROM relationship_pair_chemistry pair
WHERE pair.native_action_pending=1 AND ($player='' OR (pair.hero_a_id<>$player AND pair.hero_b_id<>$player))
AND NOT EXISTS (SELECT 1 FROM relationship_native_targets target WHERE target.pair_key=pair.pair_key);",
                new Dictionary<string, object> { ["player"] = player ?? "" }).FirstOrDefault(), "count", 0);
        }

        private static Dictionary<string, object> ClanConflictRollCoverage(
            IEnumerable<Dictionary<string, object>> sweeps, IEnumerable<Dictionary<string, object>> rolls)
        {
            Func<int, string, string> key = (day, kingdom) => Json.Serialize(new object[] { day, kingdom });
            var expected = new HashSet<string>(sweeps.SelectMany(sweep =>
                ReadJsonStringList(ReadString(sweep, "eligible_kingdom_ids_json", "[]"))
                    .Select(kingdom => key(ReadInt(sweep, "day_key", -1), kingdom))), StringComparer.OrdinalIgnoreCase);
            var actual = rolls.Select(roll => key(ReadInt(roll, "day_key", -1), ReadString(roll, "kingdom_id", ""))).ToList();
            var unique = new HashSet<string>(actual, StringComparer.OrdinalIgnoreCase);
            return new Dictionary<string, object>
            {
                ["missing"] = expected.Except(unique, StringComparer.OrdinalIgnoreCase).Count(),
                ["unexpected"] = unique.Except(expected, StringComparer.OrdinalIgnoreCase).Count(),
                ["duplicates"] = actual.Count - unique.Count,
                ["matched"] = expected.Intersect(unique, StringComparer.OrdinalIgnoreCase).Count()
            };
        }

        private static List<Dictionary<string, object>> LoadCoPresentLifecycleRows(
            ReignDbConnection connection, int day,
            Dictionary<int, List<Dictionary<string, object>>> groupsByDay)
        {
            var membership = new List<Dictionary<string, object>>();
            if (RelationshipThroughputOriginalReadPath)
                return QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle WHERE last_processed_day<$day;",
                    new Dictionary<string, object> { ["day"] = day });
            int groupKey = 0;
            foreach (var entry in groupsByDay.OrderBy(x => x.Key))
                foreach (var group in entry.Value)
                {
                    groupKey++;
                    foreach (string id in ReadStringList(group, "heroIds").Distinct(StringComparer.OrdinalIgnoreCase))
                        membership.Add(new Dictionary<string, object> { ["hero"] = id.ToLowerInvariant(), ["group_key"] = groupKey });
                }
            if (membership.Count == 0) return new List<Dictionary<string, object>>();
            // Eligibility here is co-presence, not romance state. A quiet historical
            // pair must still be able to reactivate under the original daily rules.
            return QuerySql(connection, @"WITH presence AS MATERIALIZED (
 SELECT hero,group_key FROM jsonb_to_recordset(CAST($presence AS jsonb)) AS p(hero text,group_key integer)
)
SELECT lifecycle.* FROM relationship_pair_lifecycle lifecycle
WHERE last_processed_day<$day AND EXISTS (
 SELECT 1 FROM presence a JOIN presence b ON a.group_key=b.group_key
 WHERE a.hero=lower(lifecycle.hero_a_id) AND b.hero=lower(lifecycle.hero_b_id)
);", new Dictionary<string, object> { ["day"] = day, ["presence"] = Json.Serialize(membership) });
        }

        private static HashSet<string> LoadHistoricalCourtshipExclusions(ReignDbConnection connection, int day)
        {
            return new HashSet<string>(QuerySql(connection, @"SELECT pair_key,romance_stage FROM relationship_pair_lifecycle
WHERE last_processed_day<$day AND flirt_attempted=1 AND flirt_passed=0
AND lover_active=0;",
                new Dictionary<string, object> { ["day"] = day })
                .Where(row => string.IsNullOrWhiteSpace(ReadString(row, "romance_stage", "")))
                .Select(row => ReadString(row, "pair_key", ""))
                .Where(key => !string.IsNullOrWhiteSpace(key)), StringComparer.OrdinalIgnoreCase);
        }
    }
}
