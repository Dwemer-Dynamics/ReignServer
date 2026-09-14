using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int AmbientCompatibilityVersion = 1;
        private const int AmbientStorageVersion = 2;
        private const double AmbientSettlementExposure = 0.35d;
        private const double AmbientPartyExposure = 1d;
        private static readonly string[] AmbientFacetKeys = { "trust", "respect", "affection", "interest_alignment", "resentment", "rivalry" };

        private static void EnsureAmbientRelationshipSchema(ReignDbConnection connection)
        {
            // Compatibility entry point retained for identity synchronization. The retired
            // per-facet ambient engine must never recreate its old tables.
            EnsureMbtiRelationshipSchema(connection);
        }

        private static Dictionary<string, object> AmbientRelationshipSnapshotApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Stopwatch timer = Stopwatch.StartNew();
            string campaignId = ReadString(payload, "campaignId", "default");
            string correlationId = EnsureCorrelationId(payload);
            int day = (int)Math.Floor(ReadDouble(payload, "worldDay", 0d) + 0.000001d);
            List<Dictionary<string, object>> groups = ReadDictionaryList(payload, "presenceGroups");
            List<Dictionary<string, object>> heroRows = ReadDictionaryList(payload, "heroes");
            Dictionary<string, Dictionary<string, object>> heroes = heroRows
                .Where(x => !string.IsNullOrWhiteSpace(ReadFirstString(x, "heroStringId", "heroId", "id")))
                .GroupBy(x => ReadFirstString(x, "heroStringId", "heroId", "id"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> prior = QuerySql(connection,
                    "SELECT * FROM ambient_relationship_runs WHERE campaign_id=$campaign AND world_day=$day LIMIT 1;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["day"] = day }).FirstOrDefault();
                if (prior != null)
                {
                    Dictionary<string, object> priorSummary = TryParseJsonObject(ReadString(prior, "summary_json", "{}")) ?? new Dictionary<string, object>();
                    priorSummary["ok"] = true;
                    priorSummary["idempotent"] = true;
                    priorSummary["runId"] = ReadString(prior, "run_id", "");
                    return priorSummary;
                }

                string runId = "ambient_run_" + Guid.NewGuid().ToString("N");
                Dictionary<string, AmbientPairContext> pairs = ExpandAmbientPairs(groups, heroes);
                HashSet<string> requiredHeroIds = new HashSet<string>(
                    pairs.Values.SelectMany(x => new[] { x.HeroAId, x.HeroBId }),
                    StringComparer.OrdinalIgnoreCase);
                Dictionary<string, Dictionary<string, object>> profiles = requiredHeroIds
                    .Where(heroes.ContainsKey)
                    .ToDictionary(x => x, x => BuildAmbientProfile(campaignId, heroes[x]), StringComparer.OrdinalIgnoreCase);
                double positive = 0d, negative = 0d;
                int changedDirections = 0, nativeQueued = 0, skippedMissing = 0, skippedSameDay = 0;
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                ExecuteSql(connection, "BEGIN IMMEDIATE;");
                try
                {
                    foreach (AmbientPairContext pair in pairs.Values.OrderBy(x => x.PairKey, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!heroes.TryGetValue(pair.HeroAId, out Dictionary<string, object> heroA)
                            || !heroes.TryGetValue(pair.HeroBId, out Dictionary<string, object> heroB)
                            || !profiles.TryGetValue(pair.HeroAId, out Dictionary<string, object> profileA)
                            || !profiles.TryGetValue(pair.HeroBId, out Dictionary<string, object> profileB))
                        {
                            skippedMissing++;
                            continue;
                        }

                        Dictionary<string, object> exposure = QuerySql(connection,
                            "SELECT * FROM ambient_relationship_exposure WHERE pair_key=$pair LIMIT 1;",
                            new Dictionary<string, object> { ["pair"] = pair.PairKey }).FirstOrDefault();
                        if (exposure != null && (int)Math.Floor(ReadDouble(exposure, "last_day", -1d) + 0.000001d) == day)
                        {
                            skippedSameDay++;
                            continue;
                        }

                        Dictionary<string, object> compatibilityAB = CalculateAmbientCompatibility(profileA, profileB, pair);
                        Dictionary<string, object> compatibilityBA = CalculateAmbientCompatibility(profileB, profileA, pair);
                        bool blockedAB = HasAmbientHistoryBlock(connection, pair.HeroAId, pair.HeroBId);
                        bool blockedBA = HasAmbientHistoryBlock(connection, pair.HeroBId, pair.HeroAId);

                        Dictionary<string, object> stateAB = EnsureRelationshipState(connection, campaignId, pair.HeroAId, pair.HeroBId,
                            AmbientRelationshipSeedPayload(heroA, heroB, pair.NativeRelation), ts);
                        Dictionary<string, object> stateBA = EnsureRelationshipState(connection, campaignId, pair.HeroBId, pair.HeroAId,
                            AmbientRelationshipSeedPayload(heroB, heroA, pair.NativeRelation), ts);
                        Dictionary<string, double> driftAB = ApplyAmbientDirection(connection, runId, pair, day, ts, stateAB, compatibilityAB, blockedAB, pair.ExposureWeight);
                        Dictionary<string, double> driftBA = ApplyAmbientDirection(connection, runId, pair, day, ts, stateBA, compatibilityBA, blockedBA, pair.ExposureWeight);
                        if (driftAB.Count > 0) changedDirections++;
                        if (driftBA.Count > 0) changedDirections++;
                        foreach (double value in driftAB.Values.Concat(driftBA.Values))
                        {
                            if (value > 0d) positive += value; else negative += Math.Abs(value);
                        }

                        double pairSignal = (AmbientSocialSignal(driftAB) + AmbientSocialSignal(driftBA)) / 2d;
                        double priorWeighted = exposure == null ? 0d : ReadDouble(exposure, "weighted_exposure", 0d);
                        double weighted = priorWeighted + pair.ExposureWeight;
                        double pending = (exposure == null ? 0d : ReadDouble(exposure, "pending_native_signal", 0d)) + pairSignal;
                        double lastNative = exposure == null ? day : ReadDouble(exposure, "last_native_sync_day", day);
                        bool actionPending = exposure != null && ReadInt(exposure, "native_action_pending", 0) == 1;
                        string nativeActionId = exposure == null ? "" : ReadString(exposure, "native_action_id", "");
                        int consecutive = exposure != null && day - ReadDouble(exposure, "last_day", day) <= 1.01d
                            ? ReadInt(exposure, "consecutive_days", 0) + 1 : 1;

                        if (!actionPending && weighted >= 4d && day - lastNative >= 7d && Math.Abs(pending) >= 1d)
                        {
                            int nativeDelta = pending > 0d ? 1 : -1;
                            nativeActionId = QueueAmbientNativeRelationAction(connection, pair, day, nativeDelta, pairSignal);
                            actionPending = true;
                            pending -= nativeDelta;
                            nativeQueued++;
                        }

                        Dictionary<string, object> combinedCompatibility = new Dictionary<string, object>
                        {
                            ["aToB"] = compatibilityAB,
                            ["bToA"] = compatibilityBA,
                            ["historyBlockedAToB"] = blockedAB,
                            ["historyBlockedBToA"] = blockedBA
                        };
                        ExecuteSql(connection, @"INSERT INTO ambient_relationship_exposure(pair_key,hero_a_id,hero_b_id,first_day,last_day,consecutive_days,weighted_exposure,last_context_kind,last_context_id,compatibility_version,compatibility_json,pending_native_signal,last_native_sync_day,native_action_pending,native_action_id,updated_ts)
VALUES($pair,$a,$b,$first,$last,$consecutive,$weighted,$kind,$context,$version,$compatibility,$signal,$nativeDay,$pending,$action,$ts)
ON CONFLICT(pair_key) DO UPDATE SET last_day=$last,consecutive_days=$consecutive,weighted_exposure=$weighted,last_context_kind=$kind,last_context_id=$context,compatibility_version=$version,compatibility_json=$compatibility,pending_native_signal=$signal,native_action_pending=$pending,native_action_id=$action,updated_ts=$ts;",
                            new Dictionary<string, object>
                            {
                                ["pair"] = pair.PairKey, ["a"] = pair.HeroAId, ["b"] = pair.HeroBId,
                                ["first"] = exposure == null ? day : ReadDouble(exposure, "first_day", day), ["last"] = day,
                                ["consecutive"] = consecutive, ["weighted"] = weighted, ["kind"] = pair.ContextKind, ["context"] = pair.ContextId,
                                ["version"] = AmbientCompatibilityVersion, ["compatibility"] = Json.Serialize(combinedCompatibility),
                                ["signal"] = pending, ["nativeDay"] = lastNative, ["pending"] = actionPending ? 1 : 0,
                                ["action"] = nativeActionId, ["ts"] = ts
                            });
                    }

                    timer.Stop();
                    Dictionary<string, object> summary = new Dictionary<string, object>
                    {
                        ["ok"] = true, ["runId"] = runId, ["campaignId"] = campaignId, ["worldDay"] = day,
                        ["groupCount"] = groups.Count, ["processedPairs"] = pairs.Count - skippedMissing - skippedSameDay,
                        ["candidatePairs"] = pairs.Count, ["changedDirections"] = changedDirections,
                        ["positiveDrift"] = Math.Round(positive, 4), ["negativeDrift"] = Math.Round(negative, 4),
                        ["nativeChangesQueued"] = nativeQueued, ["durationMs"] = timer.ElapsedMilliseconds,
                        ["skippedReasons"] = new Dictionary<string, object> { ["missing_hero"] = skippedMissing, ["already_processed_day"] = skippedSameDay },
                        ["llmCalls"] = 0, ["noLlmConfirmed"] = true
                    };
                    ExecuteSql(connection, @"INSERT INTO ambient_relationship_runs(run_id,campaign_id,world_day,status,group_count,pair_count,direction_count,positive_drift,negative_drift,native_changes,duration_ms,summary_json,created_ts)
VALUES($id,$campaign,$day,'completed',$groups,$pairs,$directions,$positive,$negative,$native,$duration,$summary,$ts);",
                        new Dictionary<string, object>
                        {
                            ["id"] = runId, ["campaign"] = campaignId, ["day"] = day, ["groups"] = groups.Count,
                            ["pairs"] = pairs.Count - skippedMissing - skippedSameDay, ["directions"] = changedDirections,
                            ["positive"] = positive, ["negative"] = negative, ["native"] = nativeQueued,
                            ["duration"] = timer.ElapsedMilliseconds, ["summary"] = Json.Serialize(summary), ["ts"] = ts
                        });
                    ExecuteSql(connection, "COMMIT;");
                    WriteAudit(campaignId, correlationId, "server", "relationships", "relationships.ambient_daily", "", "", "", "completed",
                        timer.ElapsedMilliseconds, "Deterministic ambient NPC relationship chemistry processed without an LLM call.", summary);
                    return summary;
                }
                catch
                {
                    try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                    throw;
                }
            }
        }

        private static Dictionary<string, object> AmbientRelationshipStatusApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            int limit = Clamp(ReadInt(payload, "limit", 60), 1, 250);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["noLlmConfirmed"] = true,
                    ["lastRun"] = QuerySql(connection, "SELECT * FROM ambient_relationship_runs ORDER BY world_day DESC LIMIT 1;").FirstOrDefault() ?? new Dictionary<string, object>(),
                    ["currentPairs"] = QuerySql(connection, "SELECT * FROM ambient_relationship_exposure ORDER BY last_day DESC,weighted_exposure DESC LIMIT " + limit.ToString(CultureInfo.InvariantCulture) + ";"),
                    ["recentDrift"] = QuerySql(connection, "SELECT * FROM ambient_relationship_changes ORDER BY world_day DESC,created_ts DESC LIMIT " + limit.ToString(CultureInfo.InvariantCulture) + ";"),
                    ["pendingNativeChanges"] = QuerySql(connection, "SELECT pair_key,hero_a_id,hero_b_id,pending_native_signal,native_action_pending,native_action_id,last_native_sync_day FROM ambient_relationship_exposure WHERE ABS(pending_native_signal)>=0.01 OR native_action_pending=1 ORDER BY ABS(pending_native_signal) DESC LIMIT " + limit.ToString(CultureInfo.InvariantCulture) + ";")
                };
            }
        }

        private static Dictionary<string, AmbientPairContext> ExpandAmbientPairs(
            List<Dictionary<string, object>> groups,
            Dictionary<string, Dictionary<string, object>> heroes,
            string shardCampaignId = "",
            int requiredShard = -1)
        {
            Dictionary<string, AmbientPairContext> result = new Dictionary<string, AmbientPairContext>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> group in groups.OrderBy(x => ReadString(x, "kind", "settlement").Equals("party", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                string kind = ReadString(group, "kind", "settlement").ToLowerInvariant();
                double weight = kind == "party" ? AmbientPartyExposure : AmbientSettlementExposure;
                string contextId = ReadFirstString(group, "id", "groupId", "partyId", "settlementId");
                List<string> ids = ReadStringList(group, "heroIds").Where(heroes.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                Dictionary<string, int> relations = ReadDictionaryList(group, "nativeRelations").ToDictionary(
                    x => AmbientPairKey(ReadFirstString(x, "heroAId", "a"), ReadFirstString(x, "heroBId", "b")),
                    x => ReadInt(x, "value", ReadInt(x, "nativeRelation", 0)), StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < ids.Count; i++)
                {
                    for (int j = i + 1; j < ids.Count; j++)
                    {
                        string key = AmbientPairKey(ids[i], ids[j]);
                        if (string.IsNullOrWhiteSpace(key) || result.ContainsKey(key)) continue;
                        if (requiredShard >= 0
                            && RelationshipCadenceShard(shardCampaignId, key) != requiredShard)
                            continue;
                        bool nativeObserved = relations.TryGetValue(key, out int native);
                        result[key] = new AmbientPairContext
                        {
                            PairKey = key, HeroAId = ids[i], HeroBId = ids[j], ContextKind = kind,
                            ContextId = contextId, ExposureWeight = weight,
                            NativeRelation = nativeObserved ? native : 0,
                            NativeRelationObserved = nativeObserved,
                            SameClan = !string.IsNullOrWhiteSpace(ReadString(heroes[ids[i]], "clanId", "")) && ReadString(heroes[ids[i]], "clanId", "").Equals(ReadString(heroes[ids[j]], "clanId", ""), StringComparison.OrdinalIgnoreCase),
                            SameKingdom = !string.IsNullOrWhiteSpace(ReadString(heroes[ids[i]], "kingdomId", "")) && ReadString(heroes[ids[i]], "kingdomId", "").Equals(ReadString(heroes[ids[j]], "kingdomId", ""), StringComparison.OrdinalIgnoreCase)
                        };
                    }
                }
            }
            return result;
        }

        private static Dictionary<string, AmbientPairContext> ExpandDailyMatchedPairs(
            List<Dictionary<string, object>> groups,
            Dictionary<string, Dictionary<string, object>> heroes,
            string campaignId,
            string timelineId,
            int day,
            Dictionary<string, Dictionary<string, object>> courtshipPairStates = null,
            HashSet<string> excludedCourtshipFocusPairs = null)
        {
            courtshipPairStates = courtshipPairStates
                ?? new Dictionary<string, Dictionary<string, object>>(
                    StringComparer.OrdinalIgnoreCase);
            excludedCourtshipFocusPairs = excludedCourtshipFocusPairs
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, AmbientPairContext> result =
                new Dictionary<string, AmbientPairContext>(
                    StringComparer.OrdinalIgnoreCase);
            HashSet<string> assigned = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            Func<string, int> priority = kind =>
                kind == "player_party" ? 0
                : kind == "army" ? 1
                : kind == "settlement" ? 2
                : 3;
            foreach (Dictionary<string, object> group in groups
                .OrderBy(row => priority(ReadString(row, "kind", "party")
                    .ToLowerInvariant()))
                .ThenBy(row => ReadFirstString(row, "id", "groupId",
                    "partyId", "settlementId"),
                    StringComparer.OrdinalIgnoreCase))
            {
                string kind = ReadString(group, "kind", "party")
                    .ToLowerInvariant();
                string contextId = ReadFirstString(group, "id", "groupId",
                    "partyId", "settlementId");
                List<string> availableIds = ReadStringList(group, "heroIds")
                    .Where(id => heroes.ContainsKey(id) && !assigned.Contains(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Dictionary<string, int> relations = ReadDictionaryList(group,
                    "nativeRelations").ToDictionary(
                        row => AmbientPairKey(ReadFirstString(row, "heroAId", "a"),
                            ReadFirstString(row, "heroBId", "b")),
                        row => ReadInt(row, "value",
                            ReadInt(row, "nativeRelation", 0)),
                        StringComparer.OrdinalIgnoreCase);

                List<AmbientPairContext> courtshipCandidates =
                    new List<AmbientPairContext>();
                for (int first = 0; first < availableIds.Count; first++)
                {
                    for (int second = first + 1; second < availableIds.Count;
                        second++)
                    {
                        string pairKey = AmbientPairKey(availableIds[first],
                            availableIds[second]);
                        if (string.IsNullOrWhiteSpace(pairKey)
                            || excludedCourtshipFocusPairs.Contains(pairKey)
                            || !SnapshotMarriageEligible(
                                heroes[availableIds[first]],
                                heroes[availableIds[second]])
                            || !MbtiRomanceEligible(
                                heroes[availableIds[first]],
                                heroes[availableIds[second]]))
                            continue;
                        courtshipCandidates.Add(BuildDailyMatchedPair(
                            availableIds[first], availableIds[second], kind,
                            contextId, relations, heroes, true));
                    }
                }
                AmbientPairContext courtshipFocus = courtshipCandidates
                    .OrderByDescending(pair => CourtshipFocusScore(
                        courtshipPairStates.TryGetValue(pair.PairKey,
                            out Dictionary<string, object> state)
                            ? state : null))
                    .ThenBy(pair => StableUnit((campaignId ?? "default") + "|"
                        + (timelineId ?? "main") + "|" + kind + "|"
                        + contextId + "|" + pair.PairKey
                        + "|courtship_focus_v1"))
                    .ThenBy(pair => pair.PairKey,
                        StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (courtshipFocus != null)
                {
                    result[courtshipFocus.PairKey] = courtshipFocus;
                    assigned.Add(courtshipFocus.HeroAId);
                    assigned.Add(courtshipFocus.HeroBId);
                }

                List<string> ids = availableIds
                    .Where(id => !assigned.Contains(id))
                    .OrderBy(id => StableUnit((campaignId ?? "default") + "|"
                        + (timelineId ?? "main") + "|"
                        + day.ToString(CultureInfo.InvariantCulture) + "|"
                        + kind + "|" + contextId + "|" + id
                        + "|daily_social_matching_v1"))
                    .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (string id in ids) assigned.Add(id);
                for (int index = 0; index + 1 < ids.Count; index += 2)
                {
                    string heroAId = ids[index];
                    string heroBId = ids[index + 1];
                    string key = AmbientPairKey(heroAId, heroBId);
                    if (string.IsNullOrWhiteSpace(key) || result.ContainsKey(key))
                        continue;
                    result[key] = BuildDailyMatchedPair(heroAId, heroBId,
                        kind, contextId, relations, heroes, false);
                }
            }
            return result;
        }

        private static AmbientPairContext BuildDailyMatchedPair(
            string heroAId,
            string heroBId,
            string kind,
            string contextId,
            Dictionary<string, int> relations,
            Dictionary<string, Dictionary<string, object>> heroes,
            bool courtshipFocus)
        {
            string key = AmbientPairKey(heroAId, heroBId);
            bool suppliedAIsCanonicalA = key.StartsWith(heroAId + "|",
                StringComparison.OrdinalIgnoreCase);
            string canonicalHeroAId = suppliedAIsCanonicalA
                ? heroAId : heroBId;
            string canonicalHeroBId = suppliedAIsCanonicalA
                ? heroBId : heroAId;
            bool observed = relations.TryGetValue(key, out int native);
            Dictionary<string, object> heroA = heroes[canonicalHeroAId];
            Dictionary<string, object> heroB = heroes[canonicalHeroBId];
            return new AmbientPairContext
            {
                PairKey = key,
                HeroAId = canonicalHeroAId,
                HeroBId = canonicalHeroBId,
                ContextKind = kind,
                ContextId = contextId,
                ExposureWeight = 1d,
                NativeRelation = observed ? native : 0,
                NativeRelationObserved = observed,
                SameClan = !string.IsNullOrWhiteSpace(
                        ReadString(heroA, "clanId", ""))
                    && ReadString(heroA, "clanId", "").Equals(
                        ReadString(heroB, "clanId", ""),
                        StringComparison.OrdinalIgnoreCase),
                SameKingdom = !string.IsNullOrWhiteSpace(
                        ReadString(heroA, "kingdomId", ""))
                    && ReadString(heroA, "kingdomId", "").Equals(
                        ReadString(heroB, "kingdomId", ""),
                        StringComparison.OrdinalIgnoreCase),
                CourtshipFocus = courtshipFocus
            };
        }

        private static long CourtshipFocusScore(
            Dictionary<string, object> pairState)
        {
            if (pairState == null) return 50L;
            int mutualAffinity = Math.Min(
                ReadInt(pairState, "affinity_a_to_b", 0),
                ReadInt(pairState, "affinity_b_to_a", 0));
            int mutualCompatibility = Math.Min(
                ReadInt(pairState, "chance_a_to_b", 50),
                ReadInt(pairState, "chance_b_to_a", 50));
            return mutualAffinity * 1000L + mutualCompatibility;
        }

        private static Dictionary<string, Dictionary<string, object>>
            LoadCourtshipFocusPairStates(
                ReignDbConnection connection,
                Dictionary<int, List<Dictionary<string, object>>> groupsByDay,
                Dictionary<string, Dictionary<string, object>> heroes,
                Dictionary<int, Dictionary<string, Dictionary<string, object>>> contextualHeroesByDay = null)
        {
            HashSet<string> candidatePairKeys = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in groupsByDay)
            {
                var dayHeroes = contextualHeroesByDay != null
                    ? contextualHeroesByDay[entry.Key] : heroes;
                foreach (Dictionary<string, object> group in entry.Value)
                {
                    List<string> ids = ReadStringList(group, "heroIds")
                        .Where(dayHeroes.ContainsKey)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    for (int first = 0; first < ids.Count; first++)
                    {
                        for (int second = first + 1; second < ids.Count;
                            second++)
                        {
                            if (SnapshotMarriageEligible(dayHeroes[ids[first]],
                                    dayHeroes[ids[second]])
                                && MbtiRomanceEligible(dayHeroes[ids[first]],
                                    dayHeroes[ids[second]]))
                                candidatePairKeys.Add(AmbientPairKey(ids[first],
                                    ids[second]));
                        }
                    }
                }
            }
            if (candidatePairKeys.Count == 0)
                return new Dictionary<string, Dictionary<string, object>>(
                    StringComparer.OrdinalIgnoreCase);

            List<Dictionary<string, object>> rows;
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                rows = QuerySql(connection, @"SELECT pairs.pair_key,
pairs.affinity_a_to_b,pairs.affinity_b_to_a,
pairs.chance_a_to_b,pairs.chance_b_to_a
FROM relationship_pair_chemistry pairs
JOIN jsonb_array_elements_text(CAST($pairs AS jsonb)) requested
  ON requested.value=pairs.pair_key;",
                    new Dictionary<string, object>
                    {
                        ["pairs"] = Json.Serialize(candidatePairKeys
                            .OrderBy(value => value,
                                StringComparer.OrdinalIgnoreCase).ToList())
                    });
            }
            else
            {
                rows = QuerySql(connection, @"SELECT pair_key,
affinity_a_to_b,affinity_b_to_a,chance_a_to_b,chance_b_to_a
FROM relationship_pair_chemistry;")
                    .Where(row => candidatePairKeys.Contains(
                        ReadString(row, "pair_key", ""))).ToList();
            }
            return rows.ToDictionary(row => ReadString(row, "pair_key", ""),
                row => row, StringComparer.OrdinalIgnoreCase);
        }

        private static List<Dictionary<string, object>> BuildDirectorEdgesFromPresence(ReignDbConnection connection, Dictionary<string, object> payload)
        {
            List<Dictionary<string, object>> heroRows = ReadDictionaryList(payload, "heroes");
            Dictionary<string, Dictionary<string, object>> heroes = heroRows
                .Where(x => !string.IsNullOrWhiteSpace(ReadFirstString(x, "heroStringId", "heroId", "id")))
                .GroupBy(x => ReadFirstString(x, "heroStringId", "heroId", "id"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> edges = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            Action<string, string, string, string, int, bool> add = (a, b, reason, location, native, coLocated) =>
            {
                string key = AmbientPairKey(a, b);
                if (string.IsNullOrWhiteSpace(key) || !heroes.ContainsKey(a) || !heroes.ContainsKey(b)) return;
                if (!edges.TryGetValue(key, out Dictionary<string, object> edge))
                {
                    Dictionary<string, object> ha = heroes[a], hb = heroes[b];
                    bool spouses = ReadString(ha, "spouseId", "").Equals(b, StringComparison.OrdinalIgnoreCase) || ReadString(hb, "spouseId", "").Equals(a, StringComparison.OrdinalIgnoreCase);
                    bool family = spouses || ReadString(ha, "fatherId", "").Equals(b, StringComparison.OrdinalIgnoreCase) || ReadString(ha, "motherId", "").Equals(b, StringComparison.OrdinalIgnoreCase)
                        || ReadString(hb, "fatherId", "").Equals(a, StringComparison.OrdinalIgnoreCase) || ReadString(hb, "motherId", "").Equals(a, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrWhiteSpace(ReadString(ha, "fatherId", "")) && ReadString(ha, "fatherId", "").Equals(ReadString(hb, "fatherId", ""), StringComparison.OrdinalIgnoreCase)
                            && ReadString(ha, "motherId", "").Equals(ReadString(hb, "motherId", ""), StringComparison.OrdinalIgnoreCase));
                    edge = new Dictionary<string, object>
                    {
                        ["heroAId"] = a, ["heroBId"] = b, ["heroA"] = ha, ["heroB"] = hb,
                        ["coLocated"] = coLocated,
                        ["sameClan"] = !string.IsNullOrWhiteSpace(ReadString(ha, "clanId", "")) && ReadString(ha, "clanId", "").Equals(ReadString(hb, "clanId", ""), StringComparison.OrdinalIgnoreCase),
                        ["sameKingdom"] = !string.IsNullOrWhiteSpace(ReadString(ha, "kingdomId", "")) && ReadString(ha, "kingdomId", "").Equals(ReadString(hb, "kingdomId", ""), StringComparison.OrdinalIgnoreCase),
                        ["spouses"] = spouses, ["family"] = family, ["nativeRelation"] = native,
                        ["locationId"] = location ?? "", ["reasons"] = new List<string>()
                    };
                    edges[key] = edge;
                }
                edge["coLocated"] = ReadBool(edge, "coLocated", false) || coLocated;
                if (coLocated && !string.IsNullOrWhiteSpace(location)) edge["locationId"] = location;
                if (native != 0) edge["nativeRelation"] = native;
                List<string> reasons = ReadStringList(edge, "reasons");
                if (!reasons.Contains(reason, StringComparer.OrdinalIgnoreCase)) reasons.Add(reason);
                edge["reasons"] = reasons;
            };

            Dictionary<string, AmbientPairContext> present = ExpandAmbientPairs(ReadDictionaryList(payload, "presenceGroups"), heroes);
            foreach (AmbientPairContext pair in present.Values)
                add(pair.HeroAId, pair.HeroBId, pair.ContextKind == "party" ? "same_party" : "same_settlement", pair.ContextId, pair.NativeRelation, true);

            foreach (IGrouping<string, Dictionary<string, object>> clan in heroRows.Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "clanId", ""))).GroupBy(x => ReadString(x, "clanId", ""), StringComparer.OrdinalIgnoreCase))
            {
                List<Dictionary<string, object>> members = clan.ToList();
                for (int i = 0; i < members.Count; i++) for (int j = i + 1; j < members.Count; j++)
                    add(ReadString(members[i], "heroStringId", ""), ReadString(members[j], "heroStringId", ""), "same_clan", "", 0, false);
            }
            foreach (Dictionary<string, object> hero in heroRows)
            {
                string id = ReadString(hero, "heroStringId", "");
                foreach (string relative in new[] { ReadString(hero, "spouseId", ""), ReadString(hero, "fatherId", ""), ReadString(hero, "motherId", "") })
                    if (!string.IsNullOrWhiteSpace(relative)) add(id, relative, "family", "", 0, false);
            }
            foreach (Dictionary<string, object> row in QuerySql(connection, @"SELECT subject_id,target_id,native_relation,trust,respect,affection,resentment,rivalry FROM relationships
WHERE ABS(native_relation)>=20 OR ABS(trust)>=30 OR ABS(respect)>=30 OR ABS(affection)>=30 OR resentment>=30 OR rivalry>=30 LIMIT 750;"))
                add(ReadString(row, "subject_id", ""), ReadString(row, "target_id", ""), "established_relationship", "", ReadInt(row, "native_relation", 0), false);
            return edges.Values.ToList();
        }

        private static Dictionary<string, object> EvaluatePassiveRelationshipPair(ReignDbConnection connection, string campaignId, string eventId, string eventType,
            string a, string b, Dictionary<string, object> profileA, Dictionary<string, object> profileB, Dictionary<string, object> candidate, string summary, double day,
            Dictionary<string, object> postureA, Dictionary<string, object> postureB, double preEventRomanceIntensity)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> payloadA = new Dictionary<string, object> { ["campaignId"] = campaignId, ["eventId"] = eventId, ["eventType"] = eventType, ["subjectId"] = a, ["targetId"] = b, ["subject"] = profileA, ["target"] = profileB, ["nativeRelation"] = ReadDouble(candidate, "nativeRelation", 0d), ["summary"] = summary, ["worldDay"] = day, ["source"] = "passive_relationship_director", ["preEventRomanceIntensity"] = preEventRomanceIntensity, ["motiveDecision"] = new Dictionary<string, object> { ["romance"] = postureA ?? new Dictionary<string, object>() } };
            Dictionary<string, object> payloadB = new Dictionary<string, object> { ["campaignId"] = campaignId, ["eventId"] = eventId, ["eventType"] = eventType, ["subjectId"] = b, ["targetId"] = a, ["subject"] = profileB, ["target"] = profileA, ["nativeRelation"] = ReadDouble(candidate, "nativeRelation", 0d), ["summary"] = summary, ["worldDay"] = day, ["source"] = "passive_relationship_director", ["preEventRomanceIntensity"] = preEventRomanceIntensity, ["motiveDecision"] = new Dictionary<string, object> { ["romance"] = postureB ?? new Dictionary<string, object>() } };
            Dictionary<string, object> stateA = EnsureRelationshipState(connection, campaignId, a, b, payloadA, ts);
            Dictionary<string, object> stateB = EnsureRelationshipState(connection, campaignId, b, a, payloadB, ts);
            Dictionary<string, object> fallbackA = DeterministicRelationshipEvaluation(eventType, summary, payloadA, stateA);
            Dictionary<string, object> fallbackB = DeterministicRelationshipEvaluation(eventType, summary, payloadB, stateB);
            Dictionary<string, object> evaluationA = fallbackA, evaluationB = fallbackB;
            bool registeredStoryEvent = IsRegisteredRelationshipStoryEvent(eventType);
            string evaluator = registeredStoryEvent ? "deterministic_storyline" : "deterministic_fallback";
            try
            {
                if (registeredStoryEvent) throw new InvalidOperationException("Registered storyline events use shared deterministic mechanics.");
                string mbtiA = BuildCharacterMbtiPromptBlock(campaignId, a, profileA,
                    ReadJsonObject(CharacterFile(campaignId, a, "traits.json")));
                string mbtiB = BuildCharacterMbtiPromptBlock(campaignId, b, profileB,
                    ReadJsonObject(CharacterFile(campaignId, b, "traits.json")));
                string prompt = "Evaluate both directional effects of one autonomous NPC relationship event. Return strict JSON only.\n"
                    + "Event type: " + eventType + "\nSummary: " + summary + "\n"
                    + "A: " + Json.Serialize(profileA) + "\nB: " + Json.Serialize(profileB) + "\n"
                    + "A defining capabilities:\n" + BuildCoreSkillAwarenessPrompt(profileA, LoadPassiveCharacterStack(campaignId, a, profileA)) + "\n"
                    + "B defining capabilities:\n" + BuildCoreSkillAwarenessPrompt(profileB, LoadPassiveCharacterStack(campaignId, b, profileB)) + "\n"
                    + "Capability evidence is background and self-knowledge only. Do not change relationship facets because of skill alone; require conduct in the supplied event.\n"
                    + "A MBTI:\n" + mbtiA + "\nB MBTI:\n" + mbtiB + "\n"
                    + "A toward B facets: " + Json.Serialize(RelationshipFacetSnapshot(stateA)) + "\n"
                    + "B toward A facets: " + Json.Serialize(RelationshipFacetSnapshot(stateB)) + "\n"
                    + "Return {aToB:{impactLevel:'ordinary|major|transformative',reasoning:'',facetDeltas:{},nativeRelationDelta:0,milestoneUpdates:[],pressureUpdates:[],development:{}},bToA:{same fields}}. Judge each direction independently. At most eight nonzero facets per direction.";
                Dictionary<string, object> llm = ChatWithLlm(new Dictionary<string, object>
                {
                    ["requestType"] = "relationship", ["maxTokens"] = 1900, ["temperature"] = 0.15d,
                    ["messages"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = "You are Reign's private paired relationship evaluator. Return strict JSON only. Never roleplay or write visible dialogue." },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = prompt }
                    },
                    ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
                });
                Dictionary<string, object> parsed = ReadBool(llm, "ok", false) ? TryParseJsonObject(ReadString(llm, "content", "")) : null;
                if (parsed != null && ReadDictionary(parsed, "aToB") != null && ReadDictionary(parsed, "bToA") != null)
                {
                    evaluationA = ReadDictionary(parsed, "aToB"); evaluationB = ReadDictionary(parsed, "bToA"); evaluator = "paired_relationship_model";
                }
            }
            catch { evaluator = registeredStoryEvent ? "deterministic_storyline" : "deterministic_fallback"; }
            evaluationA["evaluator"] = evaluator; evaluationB["evaluator"] = evaluator;
            Dictionary<string, object> appliedA = ApplyRelationshipEvaluation(connection, campaignId, eventId, a, b, day, ts, stateA, evaluationA);
            Dictionary<string, object> appliedB = ApplyRelationshipEvaluation(connection, campaignId, eventId, b, a, day, ts, stateB, evaluationB);
            RecordPairedRelationshipEvaluation(connection, eventId, a, b, evaluator, payloadA, appliedA, ts);
            RecordPairedRelationshipEvaluation(connection, eventId, b, a, evaluator, payloadB, appliedB, ts);
            return new Dictionary<string, object> { ["aToB"] = appliedA, ["bToA"] = appliedB, ["evaluator"] = evaluator, ["llmCallCount"] = evaluator == "paired_relationship_model" ? 1 : 0 };
        }

        private static void RecordPairedRelationshipEvaluation(ReignDbConnection connection, string eventId, string subject, string target, string evaluator,
            Dictionary<string, object> input, Dictionary<string, object> output, long ts)
        {
            ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_evaluations(evaluation_id,event_id,subject_id,target_id,evaluator,status,input_json,output_json,created_ts)
VALUES($id,$event,$subject,$target,$evaluator,'completed',$input,$output,$ts);", new Dictionary<string, object>
            {
                ["id"] = "reval_" + Guid.NewGuid().ToString("N"), ["event"] = eventId, ["subject"] = subject, ["target"] = target,
                ["evaluator"] = evaluator, ["input"] = Json.Serialize(input), ["output"] = Json.Serialize(output), ["ts"] = ts
            });
        }

        private static Dictionary<string, object> BuildAmbientProfile(string campaignId, Dictionary<string, object> snapshot)
        {
            string heroId = ReadFirstString(snapshot, "heroStringId", "heroId", "id");
            Dictionary<string, object> profile = new Dictionary<string, object>(snapshot ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> storedProfile = ReadJsonObject(CharacterFile(campaignId, heroId, "profile.json"));
            foreach (KeyValuePair<string, object> item in storedProfile) profile[item.Key] = item.Value;
            Dictionary<string, object> traitDocument = ReadJsonObject(CharacterFile(campaignId, heroId, "traits.json"));
            Dictionary<string, object> storedTraits = ReadDictionary(traitDocument, "foundationTraits")
                ?? ReadDictionary(snapshot,"foundationTraits");
            if (storedTraits == null || storedTraits.Count == 0)
            {
                Dictionary<string, object> transient = BuildTraitDocument(snapshot);
                storedTraits = ReadDictionary(transient, "foundationTraits") ?? new Dictionary<string, object>();
                profile["traitPercentages"] = ReadDictionary(transient, "traitPercentages") ?? new Dictionary<string, object>();
                profile["ambientTraitSource"] = "transient_deterministic_native_traits_and_skills";
            }
            else
            {
                profile["traitPercentages"] = ReadDictionary(traitDocument, "traitPercentages")
                    ?? ReadDictionary(snapshot, "traitPercentages")
                    ?? new Dictionary<string, object>();
                profile["ambientTraitSource"] = "constructed_character_profile";
            }
            profile["foundationTraits"] = storedTraits;
            return profile;
        }

        private static Dictionary<string, object> CalculateAmbientCompatibility(Dictionary<string, object> observer, Dictionary<string, object> target, AmbientPairContext pair)
        {
            string[] values = { "honesty", "compassion", "mercy", "generosity", "loyalty", "dutyMotivation", "familyMotivation", "religionMotivation", "traditionalism", "pragmatism" };
            string[] social = { "sociability", "patience", "emotionalStability", "optimism" };
            double valueAlignment = Map01ToSigned(values.Select(x => AmbientSimilarity(AmbientTrait(observer, x), AmbientTrait(target, x))).Average());
            double socialSimilarity = social.Select(x => AmbientSimilarity(AmbientTrait(observer, x), AmbientTrait(target, x))).Average();
            double targetGrace = AmbientAverage01(target, "tact", "empathy", "patience", "emotionalStability");
            double targetFriction = AmbientAverage01(target, "irritability", "aggression");
            double socialEase = Map01ToSigned(0.45d * socialSimilarity + 0.40d * targetGrace + 0.15d * (1d - targetFriction));
            double trustworthy = (AmbientTo01(AmbientTrait(target, "honesty")) + AmbientTo01(AmbientTrait(target, "loyalty"))
                + AmbientTo01(AmbientTrait(target, "tact")) + (1d - AmbientTo01(AmbientTrait(target, "greed")))) / 4d;
            double trustPerception = Map01ToSigned(0.70d * trustworthy + 0.30d * AmbientTo01(AmbientTrait(observer, "socialTrust")));
            double targetMerit = AmbientAverage01(target, "courage", "discipline", "confidence", "dutyMotivation");
            double observerRespect = AmbientAverage01(observer, "authorityRespect", "dutyMotivation", "ambition");
            double admiration = Map01ToSigned(0.70d * targetMerit + 0.30d * observerRespect);
            double statusFriction = ClampDouble((AmbientTo01(AmbientTrait(observer, "pride")) + AmbientTo01(AmbientTrait(observer, "envy"))
                + AmbientTo01(AmbientTrait(observer, "irritability")) + AmbientTo01(AmbientTrait(target, "ambition"))
                + AmbientTo01(AmbientTrait(target, "assertiveness")) + AmbientTo01(AmbientTrait(target, "powerMotivation"))
                + AmbientTo01(AmbientTrait(target, "aggression"))) / 7d, 0d, 1d);
            double goalOverlap = new[] { "ambition", "powerMotivation", "fameMotivation", "wealthMotivation", "legacyMotivation" }
                .Select(x => Math.Min(AmbientTo01(AmbientTrait(observer, x)), AmbientTo01(AmbientTrait(target, x)))).Average();
            double rivalryPotential = ClampDouble(0.55d * goalOverlap + 0.25d * statusFriction
                + 0.20d * Math.Min(AmbientTo01(AmbientTrait(observer, "pride")), AmbientTo01(AmbientTrait(target, "pride"))), 0d, 1d);
            double affiliation = pair == null ? 0d : pair.SameClan ? 1d : pair.SameKingdom ? 0.5d : 0d;
            Dictionary<string, object> equilibrium = new Dictionary<string, object>
            {
                ["trust"] = Math.Round(ClampDouble(12d * trustPerception + 8d * valueAlignment + 5d * socialEase, -25d, 25d), 3),
                ["respect"] = Math.Round(ClampDouble(15d * admiration + 8d * valueAlignment - 8d * statusFriction, -25d, 25d), 3),
                ["affection"] = Math.Round(ClampDouble(12d * socialEase + 10d * valueAlignment + 3d * trustPerception - 6d * statusFriction, -25d, 25d), 3),
                ["interest_alignment"] = Math.Round(ClampDouble(24d * valueAlignment + 6d * affiliation, -30d, 30d), 3),
                ["resentment"] = Math.Round(ClampDouble(14d * statusFriction - 4d * socialEase - 4d * valueAlignment, 0d, 20d), 3),
                ["rivalry"] = Math.Round(ClampDouble(14d * rivalryPotential + 6d * statusFriction, 0d, 20d), 3)
            };
            return new Dictionary<string, object>
            {
                ["version"] = AmbientCompatibilityVersion, ["values"] = Math.Round(valueAlignment, 4),
                ["socialEase"] = Math.Round(socialEase, 4), ["trustPerception"] = Math.Round(trustPerception, 4),
                ["admiration"] = Math.Round(admiration, 4), ["statusFriction"] = Math.Round(statusFriction, 4),
                ["rivalryPotential"] = Math.Round(rivalryPotential, 4), ["equilibrium"] = equilibrium,
                ["observerTraitSource"] = ReadString(observer, "ambientTraitSource", "unknown"),
                ["targetTraitSource"] = ReadString(target, "ambientTraitSource", "unknown")
            };
        }

        private static Dictionary<string, double> ApplyAmbientDirection(ReignDbConnection connection, string runId, AmbientPairContext pair, int day, long ts,
            Dictionary<string, object> state, Dictionary<string, object> compatibility, bool historyBlocked, double exposureWeight)
        {
            Dictionary<string, object> equilibrium = ReadDictionary(compatibility, "equilibrium") ?? new Dictionary<string, object>();
            Dictionary<string, double> changes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            string subject = ReadString(state, "subject_id", "");
            string target = ReadString(state, "target_id", "");
            foreach (string facet in AmbientFacetKeys)
            {
                double current = ReadDouble(state, facet, 0d);
                double desired = ReadDouble(equilibrium, facet, current);
                if (historyBlocked)
                {
                    if ((facet == "trust" || facet == "respect" || facet == "affection") && desired > current) desired = current;
                    if ((facet == "resentment" || facet == "rivalry") && desired < current) desired = current;
                }
                double gap = desired - current;
                if (Math.Abs(gap) < 0.5d) continue;
                double delta = ClampDouble(gap * 0.02d * exposureWeight, -0.50d, 0.50d);
                if (Math.Abs(delta) < 0.00001d) continue;
                double next = ClampRelationship(current + delta);
                changes[facet] = delta;
                ExecuteSql(connection, "UPDATE relationships SET " + facet + "=$value,updated_ts=$ts,last_updated_from_json=$source WHERE subject_id=$subject AND target_id=$target;",
                    new Dictionary<string, object> { ["value"] = next, ["ts"] = ts, ["source"] = Json.Serialize(new[] { "ambient:" + day.ToString(CultureInfo.InvariantCulture) }), ["subject"] = subject, ["target"] = target });
                ExecuteSql(connection, @"INSERT INTO ambient_relationship_changes(change_id,run_id,pair_key,subject_id,target_id,facet,old_value,delta,new_value,equilibrium,world_day,context_kind,context_id,compatibility_json,created_ts)
VALUES($id,$run,$pair,$subject,$target,$facet,$old,$delta,$new,$equilibrium,$day,$kind,$context,'{}',$ts)
ON CONFLICT(pair_key,subject_id,target_id,facet) DO UPDATE SET
run_id=excluded.run_id,old_value=excluded.old_value,delta=excluded.delta,new_value=excluded.new_value,
equilibrium=excluded.equilibrium,world_day=excluded.world_day,context_kind=excluded.context_kind,
context_id=excluded.context_id,compatibility_json='{}',created_ts=excluded.created_ts;",
                    new Dictionary<string, object>
                    {
                        ["id"] = "ambient_change_" + Guid.NewGuid().ToString("N"), ["run"] = runId, ["pair"] = pair.PairKey,
                        ["subject"] = subject, ["target"] = target, ["facet"] = facet, ["old"] = current,
                        ["delta"] = delta, ["new"] = next, ["equilibrium"] = desired, ["day"] = day,
                        ["kind"] = pair.ContextKind, ["context"] = pair.ContextId, ["ts"] = ts
                    });
            }
            return changes;
        }

        private static bool HasAmbientHistoryBlock(ReignDbConnection connection, string subject, string target)
        {
            int milestone = ReadInt(QuerySql(connection,
                "SELECT COUNT(*) AS count FROM relationship_milestones WHERE subject_id=$subject AND target_id=$target AND status='active' AND kind IN ('betrayed','nemesis','estranged','heartbroken');",
                new Dictionary<string, object> { ["subject"] = subject, ["target"] = target }).FirstOrDefault(), "count", 0);
            int pressure = ReadInt(QuerySql(connection,
                "SELECT COUNT(*) AS count FROM relationship_pressures WHERE subject_id=$subject AND target_id=$target AND status='active' AND intensity>=50;",
                new Dictionary<string, object> { ["subject"] = subject, ["target"] = target }).FirstOrDefault(), "count", 0);
            return milestone > 0 || pressure > 0;
        }

        private static string QueueAmbientNativeRelationAction(ReignDbConnection connection, AmbientPairContext pair, int day, int delta, double sourceSignal)
        {
            string id = "ambient_native_" + Guid.NewGuid().ToString("N");
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["delta"] = delta, ["source"] = "ambient_relationship_drift", ["pairKey"] = pair.PairKey,
                ["sourceSignal"] = sourceSignal, ["silent"] = true
            };
            ExecuteSql(connection, @"INSERT INTO relationship_director_actions(director_action_id,action_type,status,world_day,actor_id,target_id,payload_json,created_ts)
VALUES($id,'native_relation','pending',$day,$actor,$target,$payload,$ts);",
                new Dictionary<string, object> { ["id"] = id, ["day"] = day, ["actor"] = pair.HeroAId, ["target"] = pair.HeroBId, ["payload"] = Json.Serialize(payload), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            return id;
        }

        private static Dictionary<string, object> AmbientRelationshipSeedPayload(Dictionary<string, object> subject, Dictionary<string, object> target, int nativeRelation)
        {
            return new Dictionary<string, object> { ["subject"] = subject, ["target"] = target, ["nativeRelation"] = nativeRelation, ["source"] = "ambient_relationship_drift" };
        }

        private static double AmbientSocialSignal(Dictionary<string, double> drift)
        {
            if (drift == null || drift.Count == 0) return 0d;
            Func<string, double> get = key => drift.TryGetValue(key, out double value) ? value : 0d;
            return get("trust") + get("respect") + get("affection") + 0.5d * get("interest_alignment") - get("resentment") - get("rivalry");
        }

        private static string AmbientPairKey(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) || a.Equals(b, StringComparison.OrdinalIgnoreCase)) return "";
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? a + "|" + b : b + "|" + a;
        }

        private static double AmbientTrait(Dictionary<string, object> profile, string key) => ClampDouble(TraitFromProfile(profile, key) / 2d, -1d, 1d);
        private static double AmbientTo01(double signed) => ClampDouble((signed + 1d) / 2d, 0d, 1d);
        private static double Map01ToSigned(double value) => ClampDouble(value * 2d - 1d, -1d, 1d);
        private static double AmbientSimilarity(double a, double b) => ClampDouble(1d - Math.Abs(a - b) / 2d, 0d, 1d);
        private static double AmbientAverage01(Dictionary<string, object> profile, params string[] keys) => keys.Select(x => AmbientTo01(AmbientTrait(profile, x))).Average();

        private static List<Dictionary<string, object>> RunAmbientRelationshipSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
                { ["ok"] = true, ["passed"] = passed, ["suite"] = "ambient_relationships", ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0 });
            Func<Dictionary<string, object>, Dictionary<string, object>> profile = traits => new Dictionary<string, object> { ["foundationTraits"] = traits, ["ambientTraitSource"] = "test" };
            Dictionary<string, object> warm = profile(new Dictionary<string, object> { ["honesty"] = 2, ["compassion"] = 2, ["mercy"] = 2, ["generosity"] = 2, ["loyalty"] = 2, ["dutyMotivation"] = 1, ["familyMotivation"] = 1, ["religionMotivation"] = 0, ["traditionalism"] = 1, ["pragmatism"] = 0, ["sociability"] = 2, ["tact"] = 2, ["empathy"] = 2, ["patience"] = 2, ["emotionalStability"] = 2, ["optimism"] = 2, ["socialTrust"] = 1, ["courage"] = 1, ["discipline"] = 2, ["confidence"] = 1, ["authorityRespect"] = 1, ["ambition"] = 0, ["pride"] = 0, ["envy"] = -2, ["irritability"] = -2, ["aggression"] = -1, ["assertiveness"] = 0, ["powerMotivation"] = 0, ["greed"] = -1 });
            Dictionary<string, object> rivalA = profile(new Dictionary<string, object> { ["ambition"] = 2, ["pride"] = 2, ["assertiveness"] = 2, ["powerMotivation"] = 2, ["aggression"] = 2, ["envy"] = 2, ["irritability"] = 2, ["honesty"] = -1, ["loyalty"] = -1, ["tact"] = -2, ["greed"] = 2 });
            Dictionary<string, object> rivalB = profile(new Dictionary<string, object> { ["ambition"] = 2, ["pride"] = 2, ["assertiveness"] = 2, ["powerMotivation"] = 2, ["aggression"] = 2, ["envy"] = 1, ["irritability"] = 1, ["honesty"] = 1, ["loyalty"] = 1, ["tact"] = 0, ["greed"] = 0 });
            AmbientPairContext pair = new AmbientPairContext { SameClan = false, SameKingdom = true };
            Dictionary<string, object> positive = ReadDictionary(CalculateAmbientCompatibility(warm, warm, pair), "equilibrium");
            Dictionary<string, object> friction = ReadDictionary(CalculateAmbientCompatibility(rivalA, rivalB, pair), "equilibrium");
            add("compatible_equilibrium_positive", ReadDouble(positive, "trust", 0d) > 0d && ReadDouble(positive, "affection", 0d) > 0d && ReadDouble(positive, "respect", 0d) > 0d, "Compatible personalities converge toward modest positive trust, affection, and respect.");
            add("rival_equilibrium_friction", ReadDouble(friction, "rivalry", 0d) > 5d || ReadDouble(friction, "resentment", 0d) > 5d, "Proud aggressive peers produce rivalry or resentment pressure.");
            Dictionary<string, object> directionalA = CalculateAmbientCompatibility(warm, rivalA, pair), directionalB = CalculateAmbientCompatibility(rivalA, warm, pair);
            add("directional_chemistry", Math.Abs(ReadDouble(ReadDictionary(directionalA, "equilibrium"), "trust", 0d) - ReadDouble(ReadDictionary(directionalB, "equilibrium"), "trust", 0d)) > 0.1d, "Compatibility is evaluated independently in each direction.");
            double partyDelta = ClampDouble((20d - 0d) * 0.02d * AmbientPartyExposure, -0.50d, 0.50d);
            double settlementDelta = ClampDouble((20d - 0d) * 0.02d * AmbientSettlementExposure, -0.50d, 0.50d);
            add("party_stronger_than_settlement", partyDelta > settlementDelta && Math.Abs(settlementDelta - 0.14d) < 0.0001d, "Party exposure is stronger and settlement drift uses the doubled convergence rate.");
            add("seven_day_settlement_drift", Math.Abs(settlementDelta * 7d - 0.98d) < 0.0001d, "A twenty-point settlement gap moves approximately one point over a seven-day stay.");
            add("ambient_facet_allowlist", AmbientFacetKeys.All(x => new[] { "trust", "respect", "affection", "interest_alignment", "resentment", "rivalry" }.Contains(x)) && !AmbientFacetKeys.Contains("attraction") && !AmbientFacetKeys.Contains("loyalty"), "Ambient drift cannot directly alter romance, loyalty, fear, debt, or other gated facets.");
            Dictionary<string, double> onePoint = new Dictionary<string, double> { ["trust"] = 0.2d, ["respect"] = 0.1d, ["affection"] = 0.15d };
            add("native_signal_accumulates", AmbientSocialSignal(onePoint) > 0d, "Positive social drift accumulates a hidden native-relation signal.");
            add("pair_key_idempotent", AmbientPairKey("b", "a") == AmbientPairKey("a", "b"), "Unordered pair identity is stable in either input direction.");
            results.AddRange(RunAmbientRelationshipPersistenceSelfTests());
            return results;
        }

        private static List<Dictionary<string, object>> RunAmbientRelationshipPersistenceSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
                { ["ok"] = true, ["passed"] = passed, ["suite"] = "ambient_relationships", ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0 });
            string campaignId = "ar_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string heroA = "ambient_test_a", heroB = "ambient_test_b";
            Dictionary<string, object> traits = new Dictionary<string, object>
            {
                ["honesty"] = 2, ["compassion"] = 2, ["mercy"] = 2, ["generosity"] = 2, ["loyalty"] = 2,
                ["dutyMotivation"] = 1, ["familyMotivation"] = 1, ["traditionalism"] = 1, ["sociability"] = 2,
                ["tact"] = 2, ["empathy"] = 2, ["patience"] = 2, ["emotionalStability"] = 2, ["optimism"] = 2,
                ["socialTrust"] = 1, ["courage"] = 1, ["discipline"] = 2, ["confidence"] = 1,
                ["authorityRespect"] = 1, ["ambition"] = 0, ["pride"] = 0, ["envy"] = -2,
                ["irritability"] = -2, ["aggression"] = -1, ["greed"] = -1
            };
            List<Dictionary<string, object>> heroes = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["heroStringId"] = heroA, ["name"] = "Ambient A", ["clanId"] = "ambient_clan", ["kingdomId"] = "ambient_kingdom", ["foundationTraits"] = traits },
                new Dictionary<string, object> { ["heroStringId"] = heroB, ["name"] = "Ambient B", ["clanId"] = "ambient_clan", ["kingdomId"] = "ambient_kingdom", ["foundationTraits"] = traits }
            };
            Func<string, string, Dictionary<string, object>> group = (kind, id) => new Dictionary<string, object>
            {
                ["kind"] = kind, ["id"] = id, ["heroIds"] = new List<string> { heroA, heroB },
                ["nativeRelations"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["heroAId"] = heroA, ["heroBId"] = heroB, ["value"] = 0 }
                }
            };
            Func<int, List<Dictionary<string, object>>, Dictionary<string, object>> run = (day, groups) => AmbientRelationshipSnapshotApi(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["worldDay"] = day, ["heroes"] = heroes, ["presenceGroups"] = groups,
                ["correlationId"] = "ambient_selftest_" + day.ToString(CultureInfo.InvariantCulture)
            });

            try
            {
                Dictionary<string, object> first = run(1, new List<Dictionary<string, object>> { group("party", "party_test") });
                int initialChanges;
                double initialMagnitude;
                Dictionary<string, object> forbiddenBefore;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    initialChanges = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS count FROM ambient_relationship_changes;").FirstOrDefault(), "count", 0);
                    initialMagnitude = ReadDouble(QuerySql(connection, "SELECT COALESCE(AVG(ABS(delta)),0) AS value FROM ambient_relationship_changes WHERE world_day=1;").FirstOrDefault(), "value", 0d);
                    forbiddenBefore = QuerySql(connection, "SELECT loyalty,fear,envy,attraction,jealousy,dependence,debt FROM relationships WHERE subject_id=$a AND target_id=$b LIMIT 1;",
                        new Dictionary<string, object> { ["a"] = heroA, ["b"] = heroB }).FirstOrDefault() ?? new Dictionary<string, object>();
                }
                add("persistence_real_api_drift", ReadBool(first, "noLlmConfirmed", false) && initialChanges > 0,
                    "The real snapshot API persisted directional facet drift and confirmed zero LLM calls.");

                Dictionary<string, object> replay = run(1, new List<Dictionary<string, object>> { group("party", "party_test") });
                int replayChanges;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    replayChanges = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS count FROM ambient_relationship_changes;").FirstOrDefault(), "count", 0);
                add("persistence_same_day_idempotent", ReadBool(replay, "idempotent", false) && replayChanges == initialChanges,
                    "Replaying the same campaign day returns the stored run without duplicate drift.");

                run(2, new List<Dictionary<string, object>>());
                run(3, new List<Dictionary<string, object>> { group("settlement", "town_test") });
                double settlementMagnitude, weightedExposure;
                int consecutiveDays;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    settlementMagnitude = ReadDouble(QuerySql(connection, "SELECT COALESCE(AVG(ABS(delta)),0) AS value FROM ambient_relationship_changes WHERE world_day=3;").FirstOrDefault(), "value", 0d);
                    Dictionary<string, object> exposure = QuerySql(connection, "SELECT weighted_exposure,consecutive_days FROM ambient_relationship_exposure WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object> { ["pair"] = AmbientPairKey(heroA, heroB) }).FirstOrDefault() ?? new Dictionary<string, object>();
                    weightedExposure = ReadDouble(exposure, "weighted_exposure", 0d);
                    consecutiveDays = ReadInt(exposure, "consecutive_days", 0);
                }
                add("persistence_separation_and_weight", Math.Abs(weightedExposure - 1.35d) < 0.0001d && consecutiveDays == 1 && settlementMagnitude > 0d && settlementMagnitude < initialMagnitude,
                    "A separated day adds no exposure; later settlement presence adds 0.35 and drifts less than party presence.");

                for (int day = 4; day <= 18; day++) run(day, new List<Dictionary<string, object>> { group("party", "party_test") });
                int nativeActions;
                Dictionary<string, object> forbiddenAfter;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    nativeActions = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_director_actions WHERE action_type='native_relation' AND actor_id=$a AND target_id=$b;",
                        new Dictionary<string, object> { ["a"] = heroA, ["b"] = heroB }).FirstOrDefault(), "count", 0);
                    forbiddenAfter = QuerySql(connection, "SELECT loyalty,fear,envy,attraction,jealousy,dependence,debt FROM relationships WHERE subject_id=$a AND target_id=$b LIMIT 1;",
                        new Dictionary<string, object> { ["a"] = heroA, ["b"] = heroB }).FirstOrDefault() ?? new Dictionary<string, object>();
                }
                add("persistence_native_relation_batched", nativeActions == 1,
                    "Sustained exposure queued exactly one silent native-relation update while the first remains pending.");
                bool forbiddenUnchanged = new[] { "loyalty", "fear", "envy", "attraction", "jealousy", "dependence", "debt" }
                    .All(x => Math.Abs(ReadDouble(forbiddenBefore, x, 0d) - ReadDouble(forbiddenAfter, x, 0d)) < 0.000001d);
                add("persistence_forbidden_facets_unchanged", forbiddenUnchanged,
                    "Daily ambient processing did not change loyalty, fear, envy, attraction, jealousy, dependence, or debt.");

                double trustBeforeBlock;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    trustBeforeBlock = ReadDouble(QuerySql(connection, "SELECT trust FROM relationships WHERE subject_id=$a AND target_id=$b LIMIT 1;",
                        new Dictionary<string, object> { ["a"] = heroA, ["b"] = heroB }).FirstOrDefault(), "trust", 0d);
                    ExecuteSql(connection, @"INSERT INTO relationship_milestones(milestone_id,subject_id,target_id,kind,status,stability,strength,reason,created_event_id,last_event_id,evidence_json,created_day,last_meaningful_day,updated_ts,payload_json)
VALUES($id,$a,$b,'betrayed','active',80,80,'ambient self-test','','','[]',18,18,$ts,'{}');",
                        new Dictionary<string, object> { ["id"] = "ambient_block_" + Guid.NewGuid().ToString("N"), ["a"] = heroA, ["b"] = heroB, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                }
                run(19, new List<Dictionary<string, object>> { group("party", "party_test") });
                double trustAfterBlock;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    trustAfterBlock = ReadDouble(QuerySql(connection, "SELECT trust FROM relationships WHERE subject_id=$a AND target_id=$b LIMIT 1;",
                        new Dictionary<string, object> { ["a"] = heroA, ["b"] = heroB }).FirstOrDefault(), "trust", 0d);
                add("persistence_major_history_blocks_healing", Math.Abs(trustAfterBlock - trustBeforeBlock) < 0.000001d,
                    "Active betrayal evidence prevents ordinary proximity from increasing trust.");
                add("persistence_unconstructed_heroes_stay_transient", !Directory.Exists(CharacterDirectory(campaignId, heroA)) && !Directory.Exists(CharacterDirectory(campaignId, heroB)),
                    "Ambient processing used transient deterministic traits without constructing profiles or backstories.");
            }
            catch (Exception ex)
            {
                add("persistence_real_api_exception", false, "Ambient persistence fixture failed: " + ex.Message);
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(campaignId));
            }
            return results;
        }

        private sealed class AmbientPairContext
        {
            public string PairKey = "";
            public string HeroAId = "";
            public string HeroBId = "";
            public string ContextKind = "settlement";
            public string ContextId = "";
            public double ExposureWeight;
            public int NativeRelation;
            public bool NativeRelationObserved;
            public bool SameClan;
            public bool SameKingdom;
            public bool CourtshipFocus;
        }
    }
}
