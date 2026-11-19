using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Reign.Relationships;
using ReignBeta.Shared;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private static int CourtPopularityQualification(int affinity)
        {
            return affinity > 30 ? 1 : affinity < -30 ? -1 : 0;
        }

        private static Dictionary<string, object> BuildRelationshipNativeSyncPlan(
            ReignDbConnection connection, string campaignId, string timelineId, int day)
        {
            string planId = "native_sync_" + campaignId + "_" + timelineId + "_"
                + day.ToString(CultureInfo.InvariantCulture);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<Dictionary<string, object>> targets = ReignDatabase.QuerySql(connection, @"
SELECT pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,world_day,
attempt_count,revision
FROM relationship_native_targets
WHERE status IN ('pending','claimed','failed')
ORDER BY ABS(target_relation-observed_relation) DESC,pair_key;")
                .Select(row => new Dictionary<string, object>
                {
                    ["pairKey"] = ReadString(row, "pair_key", ""),
                    ["heroAId"] = ReadString(row, "hero_a_id", ""),
                    ["heroBId"] = ReadString(row, "hero_b_id", ""),
                    ["targetRelation"] = ReadInt(row, "target_relation", 0),
                    ["observedRelation"] = ReadInt(row, "observed_relation", 0),
                    ["sourceDay"] = ReadDouble(row, "world_day", day),
                    ["attemptCount"] = ReadInt(row, "attempt_count", 0),
                    ["revision"] = ReadInt(row, "revision", 1)
                }).ToList();
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_native_sync_batches(
plan_id,campaign_id,timeline_id,world_day,target_count,issued_ts,client_remaining)
VALUES($plan,$campaign,$timeline,$day,$count,$ts,$count)
ON CONFLICT(plan_id) DO UPDATE SET target_count=$count,issued_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["plan"] = planId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["day"] = (double)day, ["count"] = targets.Count, ["ts"] = ts
                });
            return new Dictionary<string, object>
            {
                ["planId"] = planId,
                ["campaignId"] = campaignId,
                ["timelineId"] = timelineId,
                ["worldDay"] = day,
                ["targetCount"] = targets.Count,
                ["targets"] = targets,
                ["exactProjection"] = true,
                ["projectionRule"] = "round((affinityAtoB+affinityBtoA)/2)"
            };
        }

        internal static Dictionary<string, object> RelationshipNativeSyncReportApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string planId = ReadString(payload, "planId", "");
            if (string.IsNullOrWhiteSpace(planId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "planId is required." };
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                Dictionary<string, object> batch = ReignDatabase.QuerySql(connection,
                    "SELECT * FROM relationship_native_sync_batches WHERE plan_id=$plan AND timeline_id=$timeline LIMIT 1;",
                    new Dictionary<string, object> { ["plan"] = planId, ["timeline"] = timelineId }).FirstOrDefault();
                if (batch == null)
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false, ["planId"] = planId,
                        ["error"] = "The native relationship sync plan is no longer active for this timeline."
                    };

                List<string> obsolete = ReadStringList(payload, "obsoletePairKeys")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (string pairKey in obsolete)
                {
                    ReignDatabase.ExecuteSql(connection, "DELETE FROM relationship_native_targets WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = pairKey });
                    ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET native_action_pending=0,native_action_id='',updated_ts=$ts WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = pairKey, ["ts"] = ts });
                }

                List<Dictionary<string, object>> failures = ReadDictionaryList(payload, "failedPairs");
                foreach (Dictionary<string, object> failure in failures)
                {
                    string pairKey = ReadString(failure, "pairKey", "");
                    if (string.IsNullOrWhiteSpace(pairKey)) continue;
                    ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_native_targets
SET status='failed',attempt_count=attempt_count+1,claimed_ts=0,last_error=$error,updated_ts=$ts
WHERE pair_key=$pair AND status<>'failed';",
                        new Dictionary<string, object>
                        {
                            ["pair"] = pairKey,
                            ["error"] = Program.LimitText(ReadString(failure, "error", "Native relation projection failed."), 500),
                            ["ts"] = ts
                        });
                }

                int appliedCount = Math.Max(0, ReadInt(payload, "appliedCount", 0));
                int alignedCount = Math.Max(0, ReadInt(payload, "alreadyAlignedCount", 0));
                int obsoleteCount = Math.Max(0, ReadInt(payload, "obsoleteCount", obsolete.Count));
                int failedCount = Math.Max(0, ReadInt(payload, "failedCount", failures.Count));
                int remainingCount = Math.Max(0, ReadInt(payload, "remainingCount", 0));
                int targetCount = Math.Max(0, ReadInt(batch, "target_count", 0));
                int terminalCount = appliedCount + alignedCount + obsoleteCount + failedCount;
                bool completeReport = remainingCount == 0
                    && terminalCount >= targetCount
                    && failedCount == failures.Count;
                if (completeReport)
                {
                    // Applying a target is followed by an immediate native
                    // GetRelation verification in the game client. A complete
                    // progress report is therefore authoritative confirmation;
                    // requiring the pair to be co-present in a later daily
                    // snapshot strands successful transient encounters forever.
                    //
                    // Reset prior retry failures covered by this plan, then
                    // restore only failures explicitly reported for this
                    // attempt. Newer targets are protected by world_day.
                    double batchDay = ReadDouble(batch, "world_day", 0d);
                    ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_native_targets
SET status='pending',claimed_ts=0,last_error='',updated_ts=$ts
WHERE world_day<=$day;",
                        new Dictionary<string, object> { ["day"] = batchDay, ["ts"] = ts });
                    foreach (Dictionary<string, object> failure in failures)
                    {
                        string pairKey = ReadString(failure, "pairKey", "");
                        if (string.IsNullOrWhiteSpace(pairKey)) continue;
                        ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_native_targets
SET status='failed',claimed_ts=0,last_error=$error,updated_ts=$ts
WHERE pair_key=$pair AND world_day<=$day;",
                            new Dictionary<string, object>
                            {
                                ["pair"] = pairKey,
                                ["day"] = batchDay,
                                ["error"] = Program.LimitText(ReadString(failure, "error",
                                    "Native relation projection failed."), 500),
                                ["ts"] = ts
                            });
                    }
                    ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET native_action_pending=0,native_action_id='',last_native_sync_day=$day,updated_ts=$ts
WHERE pair_key IN (
    SELECT pair_key FROM relationship_native_targets
    WHERE world_day<=$day AND status<>'failed'
);",
                        new Dictionary<string, object>
                        {
                            ["day"] = batchDay,
                            ["ts"] = ts
                        });
                    ReignDatabase.ExecuteSql(connection, @"DELETE FROM relationship_native_targets
WHERE world_day<=$day AND status<>'failed';",
                        new Dictionary<string, object> { ["day"] = batchDay });
                }

                ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_native_sync_batches SET
client_applied=$applied,client_already_aligned=$aligned,client_obsolete=$obsolete,
client_failed=$failed,client_remaining=$remaining,client_duration_ms=$duration,
last_report_day=$day,last_report_ts=$ts,last_error=$error
WHERE plan_id=$plan AND timeline_id=$timeline;",
                    new Dictionary<string, object>
                    {
                        ["applied"] = appliedCount,
                        ["aligned"] = alignedCount,
                        ["obsolete"] = obsoleteCount,
                        ["failed"] = failedCount,
                        ["remaining"] = remainingCount,
                        ["duration"] = Math.Max(0, ReadInt(payload, "durationMs", 0)),
                        ["day"] = ReadDouble(payload, "worldDay", ReadDouble(batch, "world_day", 0d)),
                        ["ts"] = ts,
                        ["error"] = Program.LimitText(ReadString(payload, "lastError", ""), 1000),
                        ["plan"] = planId,
                        ["timeline"] = timelineId
                    });
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["planId"] = planId, ["reportedCount"] =
                        ReadInt(payload, "appliedCount", 0)
                        + ReadInt(payload, "alreadyAlignedCount", 0)
                        + ReadInt(payload, "obsoleteCount", 0)
                        + ReadInt(payload, "failedCount", 0),
                    ["remainingCount"] = Math.Max(0, ReadInt(payload, "remainingCount", 0))
                };
            }
        }

        internal static Dictionary<string, object> MbtiRelationshipStatusApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            int limit = Clamp(ReadInt(payload, "limit", 60), 1, 250);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["campaignId"] = campaignId, ["noLlmConfirmed"] = true,
                    ["model"] = "directional_jobcannon_mbti_19_81_v7",
                    ["rollRule"] = "Roll 1-100 independently in each direction. At or below adjusted compatibility is positive; above is negative. Magnitude is 1d15, or 1d20 once the pair has the lovers tag.",
                    ["nativeRelationRule"] = "Bannerlord native relation converges to the rounded average of the two authoritative Reign directional affinities.",
                    ["lastProcessedDay"] = RelationshipLastProcessedDay(connection,
                        ReadString(payload, "timelineId", "main")),
                    ["latestIngestedDay"] = ReadDouble(ReignDatabase.QuerySql(connection,
                        "SELECT MAX(world_day) AS day FROM relationship_daily_inputs;").FirstOrDefault(),
                        "day", -1d),
                    ["pendingDailyInputCount"] = ReadInt(ReignDatabase.QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_daily_inputs WHERE status IN ('pending','processing');")
                        .FirstOrDefault(), "count", 0),
                    ["oldestPendingDailyInputDay"] = ReadDouble(ReignDatabase.QuerySql(connection,
                        "SELECT MIN(world_day) AS day FROM relationship_daily_inputs WHERE status='pending';")
                        .FirstOrDefault(), "day", -1d),
                    ["partialDay"] = ReignDatabase.QuerySql(connection, @"SELECT day_key,pair_cursor,candidate_pairs,processed_pairs,
started_ts,last_error FROM relationship_daily_inputs WHERE status='processing'
ORDER BY day_key LIMIT 1;").FirstOrDefault() ?? new Dictionary<string, object>(),
                    ["worker"] = ContinuousRelationshipWorkerStatus(),
                    ["mbtiInitializationError"] = ReadString(ReignDatabase.QuerySql(connection,
                        "SELECT value FROM schema_meta WHERE key='relationship_mbti_error' LIMIT 1;")
                        .FirstOrDefault(), "value", ""),
                    ["currentPairs"] = ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry ORDER BY last_day DESC,weighted_exposure DESC LIMIT " + limit.ToString(CultureInfo.InvariantCulture) + ";"),
                    ["pendingNativeChanges"] = ReignDatabase.QuerySql(connection, "SELECT pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,projected_native_relation,native_action_pending,native_action_id FROM relationship_pair_chemistry WHERE native_action_pending=1 ORDER BY last_day DESC LIMIT " + limit.ToString(CultureInfo.InvariantCulture) + ";"),
                    ["retiredDailyFacetRows"] = 0
                };
            }
        }

        internal static Dictionary<string, object> DeriveMbtiAxes(Dictionary<string, object> profile)
        {
            double ei = WeightedMbtiAxis(profile,
                new[] { "sociability", "assertiveness", "confidence", "flirtatiousness", "optimism", "shame" },
                new[] { 4d, 2d, 2d, 1d, 1d, -1d });
            double ns = WeightedMbtiAxis(profile,
                new[] { "curiosity", "knowledgeMotivation", "legacyMotivation", "ambition", "pragmatism", "discipline", "dutyMotivation", "survivalMotivation", "traditionalism" },
                new[] { 3d, 2d, 1d, 1d, -2d, -2d, -2d, -1d, -2d });
            double tf = WeightedMbtiAxis(profile,
                new[] { "pragmatism", "discipline", "emotionalStability", "assertiveness", "knowledgeMotivation", "empathy", "compassion", "mercy", "generosity" },
                new[] { 3d, 2d, 1d, 1d, 1d, -3d, -3d, -2d, -1d });
            double jp = WeightedMbtiAxis(profile,
                new[] { "discipline", "patience", "dutyMotivation", "traditionalism", "emotionalStability", "impulsiveness", "riskTolerance", "curiosity" },
                new[] { 3d, 2d, 3d, 2d, 1d, -3d, -2d, -1d });
            string type = (ei >= 0d ? "E" : "I") + (ns >= 0d ? "N" : "S")
                + (tf >= 0d ? "T" : "F") + (jp >= 0d ? "J" : "P");
            return new Dictionary<string, object>
            {
                ["type"] = type, ["extraversion"] = Math.Round(ei, 4),
                ["intuition"] = Math.Round(ns, 4), ["thinking"] = Math.Round(tf, 4),
                ["judging"] = Math.Round(jp, 4),
                ["source"] = ReadString(profile, "ambientTraitSource", "transient_deterministic")
            };
        }

        internal static int RelationshipLastProcessedDay(ReignDbConnection connection, string timelineId)
        {
            Dictionary<string, object> row = ReignDatabase.QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key=$key LIMIT 1;",
                new Dictionary<string, object>
                {
                    ["key"] = "mbti_relationship_last_processed_day:"
                        + (string.IsNullOrWhiteSpace(timelineId) ? "main" : timelineId)
                }).FirstOrDefault();
            if (row != null) return ReadInt(row, "value", -1);
            return ReadInt(ReignDatabase.QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='mbti_relationship_last_processed_day' LIMIT 1;")
                .FirstOrDefault(), "value", -1);
        }

        private static double WeightedMbtiAxis(Dictionary<string, object> profile, string[] keys, double[] weights)
        {
            double sum = 0d, denominator = 0d;
            for (int i = 0; i < keys.Length && i < weights.Length; i++)
            {
                double signed = (MbtiTraitPercent(profile, keys[i]) - 50d) / 50d;
                sum += signed * weights[i];
                denominator += Math.Abs(weights[i]);
            }
            return denominator <= 0d ? 0d : Program.ClampDouble(sum / denominator, -1d, 1d);
        }

        private static double MbtiTraitPercent(Dictionary<string, object> profile, string key)
        {
            Dictionary<string, object> percentages = ReadDictionary(profile, "traitPercentages") ?? new Dictionary<string, object>();
            if (percentages.ContainsKey(key)) return Program.ClampDouble(ReadDouble(percentages, key, 50d), 0d, 100d);
            Dictionary<string, object> foundation = ReadDictionary(profile, "foundationTraits") ?? new Dictionary<string, object>();
            return Program.ClampDouble(50d + 20d * ReadDouble(foundation, key, 0d), 0d, 100d);
        }

        private static int MbtiCompatibility(string observerType, string targetType)
        {
            return RelationshipCompatibilityPolicy.BaseScore(
                observerType, targetType);
        }

        private static int StableDie(string seed, int sides)
        {
            return Clamp(1 + (int)Math.Floor(StableUnit(seed) * sides), 1, Math.Max(1, sides));
        }

        private static int RelationshipCadenceShard(string campaignId, string pairKey)
        {
            return StableDie((campaignId ?? "default") + "|" + (pairKey ?? "")
                + "|relationship_cadence_shard_v1", RelationshipCadenceShardCount) - 1;
        }

        private static DailyPairDice BuildDailyPairDice(
            string campaignId,
            AmbientPairContext pair,
            int day)
        {
            double magnitudeAtoB = StableUnit(campaignId + "|"
                + pair.PairKey + "|"
                + day.ToString(CultureInfo.InvariantCulture)
                + "|a_to_b|magnitude_v2");
            double magnitudeBtoA = StableUnit(campaignId + "|"
                + pair.PairKey + "|"
                + day.ToString(CultureInfo.InvariantCulture)
                + "|b_to_a|magnitude_v2");
            return new DailyPairDice
            {
                PairKey = pair.PairKey,
                // Selection into the daily matching is the exposure gate.
                SettlementEligible = true,
                RollAtoB = StableDie(campaignId + "|" + pair.PairKey + "|"
                    + day.ToString(CultureInfo.InvariantCulture) + "|a_to_b|d100", 100),
                RollBtoA = StableDie(campaignId + "|" + pair.PairKey + "|"
                    + day.ToString(CultureInfo.InvariantCulture) + "|b_to_a|d100", 100),
                OrdinaryMagnitudeAtoB = StableDieFromUnit(magnitudeAtoB,
                    OrdinaryRelationshipMagnitudeSides),
                OrdinaryMagnitudeBtoA = StableDieFromUnit(magnitudeBtoA,
                    OrdinaryRelationshipMagnitudeSides),
                LoverMagnitudeAtoB = StableDieFromUnit(magnitudeAtoB,
                    LoverRelationshipMagnitudeSides),
                LoverMagnitudeBtoA = StableDieFromUnit(magnitudeBtoA,
                    LoverRelationshipMagnitudeSides)
            };
        }

        private static int StableDieFromUnit(double unit, int sides)
        {
            return Clamp(1 + (int)Math.Floor(
                Program.ClampDouble(unit, 0d, 0.9999999999999999d) * sides),
                1, Math.Max(1, sides));
        }

        private static bool UsesLoverRelationshipMagnitude(
            Dictionary<string, object> lifecycleRow)
        {
            return lifecycleRow != null
                && ReadInt(lifecycleRow, "lover_active", 0) == 1;
        }

        private static bool AreRelationshipHeroesCoPresent(
            IEnumerable<Dictionary<string, object>> groups,
            string heroAId,
            string heroBId)
        {
            if (string.IsNullOrWhiteSpace(heroAId)
                || string.IsNullOrWhiteSpace(heroBId)) return false;
            foreach (Dictionary<string, object> group in groups
                ?? Enumerable.Empty<Dictionary<string, object>>())
            {
                List<string> ids = ReadStringList(group, "heroIds");
                if (ids.Contains(heroAId, StringComparer.OrdinalIgnoreCase)
                    && ids.Contains(heroBId, StringComparer.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static Dictionary<string, HashSet<int>> BuildRelationshipCoPresenceIndex(
            IEnumerable<Dictionary<string, object>> groups)
        {
            Dictionary<string, HashSet<int>> index = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            int groupIndex = 0;
            foreach (Dictionary<string, object> group in groups ?? Enumerable.Empty<Dictionary<string, object>>())
            {
                foreach (string id in ReadStringList(group, "heroIds"))
                {
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (!index.TryGetValue(id, out HashSet<int> memberships))
                        index[id] = memberships = new HashSet<int>();
                    memberships.Add(groupIndex);
                }
                groupIndex++;
            }
            return index;
        }

        private static bool RelationshipHeroesShareGroup(Dictionary<string, HashSet<int>> index,
            string heroAId, string heroBId)
        {
            return index != null && !string.IsNullOrWhiteSpace(heroAId) && !string.IsNullOrWhiteSpace(heroBId)
                && index.TryGetValue(heroAId, out HashSet<int> a)
                && index.TryGetValue(heroBId, out HashSet<int> b)
                && (a.Count <= b.Count ? a.Overlaps(b) : b.Overlaps(a));
        }

        private sealed class RelationshipPairDay
        {
            public int Day;
            public AmbientPairContext Pair;
            public Dictionary<string, object> HeroA;
            public Dictionary<string, object> HeroB;
            public DailyPairDice Dice;
        }

        private sealed class DailyPairDice
        {
            public string PairKey = "";
            public bool SettlementEligible;
            public int RollAtoB;
            public int RollBtoA;
            public int OrdinaryMagnitudeAtoB;
            public int OrdinaryMagnitudeBtoA;
            public int LoverMagnitudeAtoB;
            public int LoverMagnitudeBtoA;
        }

        private sealed class OpeningSeedComputation
        {
            public string TypeA = "";
            public string TypeB = "";
            public int ChanceAB;
            public int ChanceBA;
            public int Baseline;
            public bool Eligible;
            public int SignAB;
            public int SignBA;
            public int MagnitudeAB;
            public int MagnitudeBA;
            public int DeltaAB;
            public int DeltaBA;
        }

        internal static int RoundAwayFromZero(double value)
        {
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        internal static string DirectionalRelationshipTag(string campaignId, string pairKey, string direction, int affinity, int reverseAffinity)
        {
            string[] pool;
            if (affinity <= -70) pool = new[] { "nemesis", "hated_enemy" };
            else if (affinity <= -50) pool = new[] { "enemy", "contemptuous" };
            else if (affinity <= -30) pool = new[] { "rival", "resentful" };
            else if (affinity <= -10) pool = new[] { "irritant", "suspicious", "dismissive" };
            else if (affinity <= 9) pool = new[] { "neutral", "unfamiliar" };
            else if (affinity <= 29) pool = new[] { "acquaintance", "cordial" };
            else if (affinity <= 49) pool = new[] { "friend", "admirer", "trusted_associate" };
            else if (affinity <= 69) pool = new[] { "close_friend", "confidant", "best_friend" };
            else if (affinity <= 84) pool = new[] { "devoted_companion", "deep_attachment" };
            else pool = new[] { "sworn_companion", "bonded" };
            int index = StableDie(campaignId + "|" + pairKey + "|" + direction + "|" + RelationshipBand(affinity), pool.Length) - 1;
            return pool[index];
        }

        private static string SharedRelationshipTag(string campaignId, string pairKey, int affinityAB, int affinityBA,
            Dictionary<string, object> heroA, Dictionary<string, object> heroB)
        {
            if (affinityAB >= 50 && affinityBA >= 50)
                return StableUnit(campaignId + "|" + pairKey + "|shared_friendship") < 0.5d ? "best_friends" : "confidants";
            if (affinityAB <= -70 && affinityBA <= -70) return "nemeses";
            if (affinityAB <= -50 && affinityBA <= -50) return "enemies";
            if (affinityAB <= -30 && affinityBA <= -30) return "rivals";
            return "";
        }

        private static bool MbtiRomanceEligible(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            string aId = ReadFirstString(a, "heroStringId", "heroId", "id");
            string bId = ReadFirstString(b, "heroStringId", "heroId", "id");
            if (string.IsNullOrWhiteSpace(aId) || string.IsNullOrWhiteSpace(bId)) return false;
            if (ReadDouble(a, "age", 18d) < 18d || ReadDouble(b, "age", 18d) < 18d) return false;
            if (ReadString(a, "fatherId", "").Equals(bId, StringComparison.OrdinalIgnoreCase)
                || ReadString(a, "motherId", "").Equals(bId, StringComparison.OrdinalIgnoreCase)
                || ReadString(b, "fatherId", "").Equals(aId, StringComparison.OrdinalIgnoreCase)
                || ReadString(b, "motherId", "").Equals(aId, StringComparison.OrdinalIgnoreCase)) return false;
            string aFather = ReadString(a, "fatherId", ""), bFather = ReadString(b, "fatherId", "");
            string aMother = ReadString(a, "motherId", ""), bMother = ReadString(b, "motherId", "");
            if ((!string.IsNullOrWhiteSpace(aFather) && aFather.Equals(bFather, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(aMother) && aMother.Equals(bMother, StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }

        internal static int InitialReignAffinity(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            string aId = ReadFirstString(a, "heroStringId", "heroId", "id");
            string bId = ReadFirstString(b, "heroStringId", "heroId", "id");
            bool spouses = ReadString(a, "spouseId", "").Equals(bId, StringComparison.OrdinalIgnoreCase)
                && ReadString(b, "spouseId", "").Equals(aId, StringComparison.OrdinalIgnoreCase);
            bool immediateFamily = ReadString(a, "fatherId", "").Equals(bId, StringComparison.OrdinalIgnoreCase)
                || ReadString(a, "motherId", "").Equals(bId, StringComparison.OrdinalIgnoreCase)
                || ReadString(b, "fatherId", "").Equals(aId, StringComparison.OrdinalIgnoreCase)
                || ReadString(b, "motherId", "").Equals(aId, StringComparison.OrdinalIgnoreCase);
            string aFather = ReadString(a, "fatherId", ""), bFather = ReadString(b, "fatherId", "");
            string aMother = ReadString(a, "motherId", ""), bMother = ReadString(b, "motherId", "");
            immediateFamily = immediateFamily
                || (!string.IsNullOrWhiteSpace(aFather) && aFather.Equals(bFather, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(aMother) && aMother.Equals(bMother, StringComparison.OrdinalIgnoreCase));
            bool sameKingdom = !string.IsNullOrWhiteSpace(ReadString(a, "kingdomId", ""))
                && ReadString(a, "kingdomId", "").Equals(ReadString(b, "kingdomId", ""), StringComparison.OrdinalIgnoreCase);
            bool lordRulerPair = sameKingdom
                && ((ReadBool(a, "isRuler", false) && ReadBool(b, "isLord", false))
                    || (ReadBool(b, "isRuler", false) && ReadBool(a, "isLord", false)));
            int baseline = ReignRelationshipBaselinePolicy.ResolveStartingBaseline(spouses, immediateFamily, lordRulerPair);
            return baseline == ReignRelationshipBaselinePolicy.NoBaseline ? 0 : baseline;
        }

        internal static string RelationshipBand(int value)
        {
            if (value <= -70) return "nemesis";
            if (value <= -50) return "enemy";
            if (value <= -30) return "rival";
            if (value <= -10) return "irritant";
            if (value <= 9) return "neutral";
            if (value <= 29) return "acquaintance";
            if (value <= 49) return "friend";
            if (value <= 69) return "close_friend";
            if (value <= 84) return "devoted";
            return "bonded";
        }

        internal static string RelationshipTagForObserver(ReignDbConnection connection, string campaignId, string observerId, string targetId, int nativeRelation)
        {
            string pairKey = AmbientPairKey(observerId, targetId);
            if (!string.IsNullOrWhiteSpace(pairKey))
            {
                Dictionary<string, object> row = ReignDatabase.QuerySql(connection,
                    "SELECT hero_a_id,hero_b_id,tag_a_to_b,tag_b_to_a FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                    new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                if (row != null)
                {
                    Dictionary<string, object> lifecycle = RelationshipLifecycleView(connection, observerId, targetId);
                    if (ReadBool(lifecycle, "divorced", false)) return "divorced";
                    if (ReadBool(lifecycle, "estranged", false)) return "estranged";
                    if (ReadBool(lifecycle, "lovers", false)) return ReadBool(lifecycle, "activeAffair", false) ? "lover_in_affair" : "lover";
                    return ReadString(row, "hero_a_id", "").Equals(observerId, StringComparison.OrdinalIgnoreCase)
                        ? ReadString(row, "tag_a_to_b", "neutral")
                        : ReadString(row, "tag_b_to_a", "neutral");
                }
            }
            // Bannerlord relation is a projection of Reign, never a source for
            // relationship meaning. An unobserved pair therefore has no Reign
            // affinity yet and must be treated as neutral.
            return DirectionalRelationshipTag(campaignId, pairKey, "observer_to_target", 0, 0);
        }

        private static Dictionary<string, object> ResolvePermanentRelationshipMbti(
            ReignDbConnection connection,
            string campaignId,
            int day,
            Dictionary<string, object> hero,
            Dictionary<string, Dictionary<string, object>> notableMbti)
        {
            string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
            if (notableMbti != null
                && notableMbti.TryGetValue(heroId, out Dictionary<string, object> authoritativeNotable))
            {
                var sourceRow = ReadDictionary(authoritativeNotable, "relationshipSourceRow");
                if (sourceRow != null) authoritativeNotable = NotableMbtiRowToProfile(sourceRow);
                string notableType = ReadString(authoritativeNotable, "type", "XXXX");
                if (!MbtiDefinitions.TryGetValue(notableType, out MbtiDefinition notableDefinition))
                    return new Dictionary<string, object>
                    {
                        ["type"] = "XXXX",
                        ["source"] = "invalid_notable_mbti_template"
                    };
                Dictionary<string, object> notableTraits = new Dictionary<string, object>
                {
                    ["foundationTraits"] = ReadDictionary(authoritativeNotable, "foundationTraits")
                        ?? new Dictionary<string, object>(),
                    ["traitPercentages"] = ReadDictionary(authoritativeNotable, "traitPercentages")
                        ?? new Dictionary<string, object>()
                };
                long notableTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,template_version,traits_json,created_ts,updated_ts)
VALUES($hero,$type,$title,$description,'notable_mbti_template',$day,$version,$traits,$ts,$ts)
ON CONFLICT(hero_id) DO UPDATE SET
mbti_type=excluded.mbti_type,title=excluded.title,description=excluded.description,
source=excluded.source,assignment_day=excluded.assignment_day,
template_version=excluded.template_version,traits_json=excluded.traits_json,updated_ts=excluded.updated_ts
WHERE relationship_personalities.mbti_type<>excluded.mbti_type
OR relationship_personalities.title<>excluded.title
OR relationship_personalities.description<>excluded.description
OR relationship_personalities.source<>excluded.source
OR relationship_personalities.assignment_day<>excluded.assignment_day
OR relationship_personalities.template_version<>excluded.template_version
OR relationship_personalities.traits_json<>excluded.traits_json;",
                    new Dictionary<string, object>
                    {
                        ["hero"] = heroId,
                        ["type"] = notableType,
                        ["title"] = notableDefinition.Title,
                        ["description"] = notableDefinition.Description,
                        ["day"] = ReadDouble(authoritativeNotable, "assignedDay", day),
                        ["version"] = ReadInt(authoritativeNotable, "templateVersion",
                            NotableMbtiTemplateVersion),
                        ["traits"] = Json.Serialize(notableTraits),
                        ["ts"] = notableTs
                    });
                Dictionary<string, object> resolvedNotable = new Dictionary<string, object>
                {
                    ["type"] = notableType,
                    ["title"] = notableDefinition.Title,
                    ["description"] = notableDefinition.Description,
                    ["source"] = "notable_mbti_template"
                };
                CachePermanentRelationshipMbti(campaignId, heroId,
                    resolvedNotable);
                return resolvedNotable;
            }
            Dictionary<string, object> stored = ReignDatabase.QuerySql(connection,
                "SELECT * FROM relationship_personalities WHERE hero_id=$hero LIMIT 1;",
                new Dictionary<string, object> { ["hero"] = heroId }).FirstOrDefault();
            if (stored != null)
            {
                string storedType = ReadString(stored, "mbti_type", "XXXX");
                Dictionary<string, object> resolvedStored = new Dictionary<string, object>
                {
                    ["type"] = storedType,
                    ["title"] = ReadString(stored, "title", ""),
                    ["description"] = ReadString(stored, "description", ""),
                    ["source"] = ReadString(stored, "source", "persisted")
                };
                CachePermanentRelationshipMbti(campaignId, heroId,
                    resolvedStored);
                return resolvedStored;
            }

            Dictionary<string, object> mbti = null;
            Dictionary<string, object> traits = null;
            string source;
            double assignmentDay;
            Dictionary<string, Dictionary<string, object>> catalog =
                RelationshipStandingContext.FilesFor(connection, campaignId)?.Catalog ?? CharactersService.LoadCharacterProfileLibrary();
            if (catalog.TryGetValue(heroId, out Dictionary<string, object> pregenerated))
            {
                mbti = ReadDictionary(pregenerated, "mbtiProfile")
                    ?? ReadDictionary(ReadDictionary(pregenerated, "traits"), "mbtiProfile");
                source = "pregenerated_noble_catalog";
                assignmentDay = -1d;
                if (mbti == null || !MbtiDefinitions.ContainsKey(ReadString(mbti, "type", "")))
                    return new Dictionary<string, object> { ["type"] = "XXXX", ["source"] = "invalid_pregenerated_noble_catalog" };
            }
            else
            {
                string type = MbtiTypes[CharactersService.StableIndex(campaignId + "|runtime_mbti|" + heroId, MbtiTypes.Length)];
                MbtiDefinition definition = MbtiDefinitions[type];
                traits = BuildNotableMbtiTraitDocument(campaignId, heroId,
                    ReadString(hero, "name", heroId), type);
                mbti = new Dictionary<string, object>
                {
                    ["type"] = type,
                    ["title"] = definition.Title,
                    ["description"] = definition.Description,
                    ["templateVersion"] = NotableMbtiTemplateVersion
                };
                source = "runtime_character_mbti_template";
                assignmentDay = day;
            }

            string resolvedType = ReadString(mbti, "type", "XXXX");
            if (!MbtiDefinitions.TryGetValue(resolvedType, out MbtiDefinition resolvedDefinition))
                return new Dictionary<string, object> { ["type"] = "XXXX", ["source"] = source };
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,template_version,traits_json,created_ts,updated_ts)
VALUES($hero,$type,$title,$description,$source,$day,$version,$traits,$ts,$ts)
ON CONFLICT(hero_id) DO NOTHING;",
                new Dictionary<string, object>
                {
                    ["hero"] = heroId,
                    ["type"] = resolvedType,
                    ["title"] = resolvedDefinition.Title,
                    ["description"] = resolvedDefinition.Description,
                    ["source"] = source,
                    ["day"] = assignmentDay,
                    ["version"] = ReadInt(mbti, "templateVersion", NotableMbtiTemplateVersion),
                    ["traits"] = Json.Serialize(traits ?? new Dictionary<string, object>()),
                    ["ts"] = ts
                });
            Dictionary<string, object> resolvedProfile = new Dictionary<string, object>
            {
                ["type"] = resolvedType,
                ["title"] = resolvedDefinition.Title,
                ["description"] = resolvedDefinition.Description,
                ["source"] = source
            };
            CachePermanentRelationshipMbti(campaignId, heroId,
                resolvedProfile);
            return resolvedProfile;
        }

        private static Dictionary<string, Dictionary<string, object>>
            LoadPermanentRelationshipMbti(ReignDbConnection connection,
                string campaignId, IEnumerable<string> heroIds)
        {
            string campaignKey = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            ConcurrentDictionary<string, Dictionary<string, object>> cached =
                RelationshipPermanentMbtiCache.GetOrAdd(campaignKey,
                    _ => new ConcurrentDictionary<string,
                        Dictionary<string, object>>(
                            StringComparer.OrdinalIgnoreCase));
            HashSet<string> required = new HashSet<string>(
                (heroIds ?? Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            List<string> missing = required
                .Where(value => !cached.ContainsKey(value))
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missing.Count > 0)
            {
                string requestedJson = Json.Serialize(missing);
                List<Dictionary<string, object>> rows =
                    ReignPostgreSqlDialect.IsPostgreSql(connection)
                        ? ReignDatabase.QuerySql(connection, @"SELECT personalities.hero_id,
personalities.mbti_type,personalities.title,personalities.description,
personalities.source
FROM relationship_personalities personalities
JOIN jsonb_array_elements_text(CAST($heroes AS jsonb)) requested
  ON requested.value=personalities.hero_id;",
                            new Dictionary<string, object>
                            {
                                ["heroes"] = requestedJson
                            })
                        : ReignDatabase.QuerySql(connection, @"SELECT hero_id,mbti_type,title,
description,source FROM relationship_personalities;")
                            .Where(row => required.Contains(
                                ReadString(row, "hero_id", ""))).ToList();
                foreach (Dictionary<string, object> row in rows)
                {
                    string heroId = ReadString(row, "hero_id", "");
                    string type = ReadString(row, "mbti_type", "XXXX");
                    if (string.IsNullOrWhiteSpace(heroId)
                        || !MbtiDefinitions.ContainsKey(type))
                        continue;
                    CachePermanentRelationshipMbti(campaignKey, heroId,
                        new Dictionary<string, object>
                        {
                            ["type"] = type,
                            ["title"] = ReadString(row, "title", ""),
                            ["description"] = ReadString(row,
                                "description", ""),
                            ["source"] = ReadString(row, "source",
                                "persisted")
                        });
                }
            }
            return required.Where(cached.ContainsKey).ToDictionary(
                value => value,
                value => new Dictionary<string, object>(cached[value],
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        }

        private static void CachePermanentRelationshipMbti(
            string campaignId, string heroId,
            Dictionary<string, object> profile)
        {
            if (string.IsNullOrWhiteSpace(heroId) || profile == null
                || !MbtiDefinitions.ContainsKey(
                    ReadString(profile, "type", "")))
                return;
            string campaignKey = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            ConcurrentDictionary<string, Dictionary<string, object>> cached =
                RelationshipPermanentMbtiCache.GetOrAdd(campaignKey,
                    _ => new ConcurrentDictionary<string,
                        Dictionary<string, object>>(
                            StringComparer.OrdinalIgnoreCase));
            cached[heroId] = new Dictionary<string, object>(profile,
                StringComparer.OrdinalIgnoreCase);
        }

        internal static void InvalidateRelationshipPermanentMbtiCache(
            string campaignId)
        {
            string key = string.IsNullOrWhiteSpace(campaignId)
                ? "default" : campaignId;
            RelationshipPermanentMbtiCache.TryRemove(key, out _);
        }
    }
}
