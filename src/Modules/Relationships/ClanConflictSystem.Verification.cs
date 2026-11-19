using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Reign.Core.Contracts.Relationships;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        internal static Dictionary<string, object> BuildWorldTestClanConflicts(
            ReignDbConnection connection, string campaignId, string timelineId,
            double latestDay)
        {
            EnsureClanConflictSchema(connection);
            Dictionary<string, object> sweeps = RelationshipsStore.CountClanConflictDaySweeps(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> rolls = RelationshipsStore.CountClanConflictDailyRolls(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> incidents = RelationshipsStore.CountClanConflictIncidents(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> effects = RelationshipsStore.CountClanConflictRelationshipEffects(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> sweepDetails = RelationshipsStore.SelectClanConflictDaySweeps(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                });
            List<Dictionary<string, object>> rollDetails = RelationshipsStore.SelectClanConflictDailyRolls(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                });
            Dictionary<string, int> expectedByKingdom = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> sweep in sweepDetails)
            foreach (string kingdomId in ReadJsonStringList(ReadString(sweep,
                "eligible_kingdom_ids_json", "[]")))
                expectedByKingdom[kingdomId] = expectedByKingdom.TryGetValue(kingdomId,
                    out int count) ? count + 1 : 1;
            Dictionary<string, Dictionary<string, object>> rollsByKingdom = rollDetails
                .ToDictionary(x => ReadString(x, "kingdom_id", ""), x => x,
                    StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> perKingdom = expectedByKingdom.Keys
                .Union(rollsByKingdom.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(kingdomId =>
                {
                    rollsByKingdom.TryGetValue(kingdomId, out Dictionary<string, object> row);
                    row = row ?? new Dictionary<string, object>();
                    int kingdomExpected = expectedByKingdom.TryGetValue(kingdomId,
                        out int value) ? value : 0;
                    int observed = ReadInt(row, "observed", 0);
                    return new Dictionary<string, object>
                    {
                        ["kingdomId"] = kingdomId,
                        ["kingdomName"] = ReadString(row, "kingdom_name", ""),
                        ["expectedRolls"] = kingdomExpected,
                        ["observedRolls"] = observed,
                        ["missingRolls"] = Math.Max(0, kingdomExpected - observed),
                        ["coveragePercent"] = kingdomExpected == 0 ? 100d
                            : Math.Round(observed * 100d / kingdomExpected, 2),
                        ["passedRolls"] = ReadInt(row, "passed", 0),
                        ["incidents"] = ReadInt(row, "incidents", 0),
                        ["firstDay"] = ReadInt(row, "first_day", -1),
                        ["latestDay"] = ReadInt(row, "latest_day", -1)
                    };
                }).ToList();
            List<Dictionary<string, object>> incidentDetails = RelationshipsStore.SelectClanConflictIncidents(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                });
            List<Dictionary<string, object>> voteDetails = incidentDetails
                .SelectMany(x => ReadDictionaryList(TryParseJsonObject("{\"votes\":"
                    + ReadString(x, "trait_votes_json", "[]") + "}"), "votes"))
                .ToList();
            List<Dictionary<string, object>> voteTraits = voteDetails
                .GroupBy(x => ReadString(x, "trait", ""), StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new Dictionary<string, object>
                {
                    ["trait"] = group.Key, ["votes"] = group.Count(),
                    ["sideA"] = group.Count(x => ReadString(x, "vote", "") == "a"),
                    ["sideB"] = group.Count(x => ReadString(x, "vote", "") == "b"),
                    ["minimumRelationModifier"] = group.Min(x => ReadInt(x, "relationModifier", 0)),
                    ["maximumRelationModifier"] = group.Max(x => ReadInt(x, "relationModifier", 0)),
                    ["averageRelationModifier"] = Math.Round(group.Average(x =>
                        ReadDouble(x, "relationModifier", 0d)), 2)
                }).ToList();
            List<Dictionary<string, object>> participantDetails = incidentDetails.Select(x =>
            {
                List<string> sideA = ReadJsonStringList(ReadString(x, "involved_a_json", "[]"));
                List<string> sideB = ReadJsonStringList(ReadString(x, "involved_b_json", "[]"));
                return new Dictionary<string, object>
                {
                    ["incidentId"] = ReadString(x, "incident_id", ""),
                    ["sideA"] = sideA.Count, ["sideB"] = sideB.Count,
                    ["total"] = sideA.Count + sideB.Count,
                    ["additionalLords"] = Math.Max(0, sideA.Count - 1)
                        + Math.Max(0, sideB.Count - 1)
                };
            }).ToList();
            List<Dictionary<string, object>> recentIncidents = incidentDetails.Take(25)
                .Select(x => new Dictionary<string, object>
                {
                    ["incidentId"] = ReadString(x, "incident_id", ""),
                    ["worldDay"] = ReadDouble(x, "world_day", 0d),
                    ["kingdomId"] = ReadString(x, "kingdom_id", ""),
                    ["kingdomName"] = ReadString(x, "kingdom_name", ""),
                    ["rulerId"] = ReadString(x, "ruler_id", ""),
                    ["rulerName"] = ReadString(x, "ruler_name", ""),
                    ["clanAId"] = ReadString(x, "clan_a_id", ""),
                    ["clanAName"] = ReadString(x, "clan_a_name", ""),
                    ["clanBId"] = ReadString(x, "clan_b_id", ""),
                    ["clanBName"] = ReadString(x, "clan_b_name", ""),
                    ["participantsA"] = ReadJsonStringList(ReadString(x,
                        "involved_a_names_json", "[]")).Cast<object>().ToList(),
                    ["participantsB"] = ReadJsonStringList(ReadString(x,
                        "involved_b_names_json", "[]")).Cast<object>().ToList(),
                    ["category"] = ReadString(x, "category", ""),
                    ["headline"] = ReadString(x, "headline", ""),
                    ["votes"] = ReadDictionaryList(TryParseJsonObject("{\"votes\":"
                        + ReadString(x, "trait_votes_json", "[]") + "}"), "votes")
                        .Cast<object>().ToList(),
                    ["winningSide"] = ReadString(x, "winning_side", ""),
                    ["charmSkill"] = ReadInt(x, "charm_skill", 0),
                    ["charmChanceBasisPoints"] = ReadInt(x, "charm_chance_bp", 0),
                    ["charmRoll"] = ReadInt(x, "charm_roll", 0),
                    ["mediationSucceeded"] = ReadBool(x, "mediation_succeeded", false),
                    ["effectCount"] = ReadInt(x, "relationship_effect_count", 0),
                    ["noticeStatus"] = ReadString(x, "notice_status", ""),
                    ["status"] = ReadString(x, "status", ""),
                    ["lastError"] = ReadString(x, "last_error", "")
                }).ToList();
            List<Dictionary<string, object>> effectKinds = RelationshipsStore.SelectClanConflictRelationshipEffects(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                });
            int duplicateEffects = ReadInt(RelationshipsStore.CountClanConflictRelationshipEffects2(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault(), "count", 0);
            int simultaneousDays = incidentDetails.GroupBy(x => ReadInt(x, "day_key", -1))
                .Count(x => x.Count() > 1);
            int maximumSimultaneous = incidentDetails.GroupBy(x => ReadInt(x, "day_key", -1))
                .Select(x => x.Count()).DefaultIfEmpty(0).Max();
            List<string> anomalies = new List<string>();
            int expected = ReadInt(sweeps, "expected", 0);
            int processed = ReadInt(sweeps, "processed", 0);
            int totalRolls = ReadInt(rolls, "total", 0);
            var rollIdentityRows = RelationshipsStore.SelectClanConflictDailyRolls2(connection, new Dictionary<string, object>
                { ["campaign"] = campaignId, ["timeline"] = timelineId });
            var rollCoverage = ClanConflictRollCoverage(sweepDetails, rollIdentityRows);
            int missingRolls = ReadInt(rollCoverage, "missing", 0);
            int unexpectedRolls = ReadInt(rollCoverage, "unexpected", 0);
            int duplicateRolls = ReadInt(rollCoverage, "duplicates", 0);
            if (missingRolls > 0) anomalies.Add(missingRolls + " per-kingdom clan-conflict rolls are missing.");
            if (unexpectedRolls > 0) anomalies.Add(unexpectedRolls + " clan-conflict rolls have no matching eligible kingdom/day.");
            if (duplicateRolls > 0) anomalies.Add(duplicateRolls + " duplicate clan-conflict kingdom/day rolls exist.");
            if (expected != processed) anomalies.Add("Clan-conflict sweep expected and processed counts disagree.");
            int badChance = ReadInt(RelationshipsStore.CountClanConflictDailyRolls2(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault(), "count", 0);
            if (badChance > 0) anomalies.Add(badChance + " rolls do not use the configured 8% chance.");
            int incomplete = ReadInt(sweeps, "incomplete", 0);
            if (incomplete > 0) anomalies.Add(incomplete + " day sweeps are incomplete.");
            int pendingEffects = ReadInt(effects, "pending", 0);
            int failedEffects = ReadInt(effects, "failed", 0);
            int failedIncidents = ReadInt(incidents, "failed", 0);
            if (pendingEffects > 0) anomalies.Add(pendingEffects + " relationship effects are pending.");
            if (failedEffects > 0) anomalies.Add(failedEffects + " relationship effects failed.");
            if (failedIncidents > 0) anomalies.Add(failedIncidents + " clan-conflict incidents failed.");
            if (duplicateEffects > 0) anomalies.Add(duplicateEffects
                + " duplicate directed relationship effects exist.");
            int ruleViolations = CountClanConflictRuleViolations(connection,
                campaignId, timelineId);
            if (ruleViolations > 0) anomalies.Add(ruleViolations + " clan-conflict records violate outcome rules.");
            string status = anomalies.Count == 0 ? "healthy"
                : failedEffects > 0 || failedIncidents > 0 || ruleViolations > 0
                    ? "error" : "warning";
            return new Dictionary<string, object>
            {
                ["name"] = "NPC Clan Conflicts",
                ["status"] = status,
                ["latestSuccessfulDay"] = Math.Max(ReadDouble(sweeps, "latest", -1d),
                    ReadDouble(incidents, "latest", -1d)),
                ["dailySweeps"] = ReadInt(sweeps, "total", 0),
                ["expectedKingdomRolls"] = expected,
                ["processedKingdomRolls"] = processed,
                ["kingdomRolls"] = totalRolls,
                ["kingdomRollsMissing"] = missingRolls,
                ["kingdomRollsUnexpected"] = unexpectedRolls,
                ["kingdomRollsDuplicate"] = duplicateRolls,
                ["rollCoveragePercent"] = expected == 0 ? 100d
                    : Math.Round(ReadInt(rollCoverage, "matched", 0) * 100d / expected, 2),
                ["configuredChancePercent"] = ClanConflictIncidentChance,
                ["passedRolls"] = ReadInt(rolls, "passed", 0),
                ["failedRolls"] = ReadInt(rolls, "failed", 0),
                ["noEligiblePairRolls"] = ReadInt(rolls, "no_pair", 0),
                ["perKingdomRolls"] = perKingdom.Cast<object>().ToList(),
                ["incidentCount"] = ReadInt(incidents, "total", 0),
                ["simultaneousIncidentDays"] = simultaneousDays,
                ["maximumSimultaneousIncidents"] = maximumSimultaneous,
                ["mediationSuccesses"] = ReadInt(incidents, "mediated", 0),
                ["failedMediations"] = ReadInt(incidents, "feuds", 0),
                ["participants"] = new Dictionary<string, object>
                {
                    ["incidents"] = participantDetails.Count,
                    ["total"] = participantDetails.Sum(x => ReadInt(x, "total", 0)),
                    ["maximumPerIncident"] = participantDetails.Select(x =>
                        ReadInt(x, "total", 0)).DefaultIfEmpty(0).Max(),
                    ["additionalLords"] = participantDetails.Sum(x =>
                        ReadInt(x, "additionalLords", 0)),
                    ["incidentsWithAdditionalLords"] = participantDetails.Count(x =>
                        ReadInt(x, "additionalLords", 0) > 0)
                },
                ["votes"] = new Dictionary<string, object>
                {
                    ["total"] = voteDetails.Count,
                    ["sideA"] = voteDetails.Count(x => ReadString(x, "vote", "") == "a"),
                    ["sideB"] = voteDetails.Count(x => ReadString(x, "vote", "") == "b"),
                    ["traits"] = voteTraits.Cast<object>().ToList(),
                    ["minimumRelationModifier"] = voteDetails.Select(x =>
                        ReadInt(x, "relationModifier", 0)).DefaultIfEmpty(0).Min(),
                    ["maximumRelationModifier"] = voteDetails.Select(x =>
                        ReadInt(x, "relationModifier", 0)).DefaultIfEmpty(0).Max()
                },
                ["charmResults"] = new Dictionary<string, object>
                {
                    ["attempts"] = incidentDetails.Count,
                    ["successes"] = ReadInt(incidents, "mediated", 0),
                    ["failures"] = ReadInt(incidents, "feuds", 0),
                    ["minimumSkill"] = incidentDetails.Select(x =>
                        ReadInt(x, "charm_skill", 0)).DefaultIfEmpty(0).Min(),
                    ["maximumSkill"] = incidentDetails.Select(x =>
                        ReadInt(x, "charm_skill", 0)).DefaultIfEmpty(0).Max(),
                    ["minimumChanceBasisPoints"] = incidentDetails.Select(x =>
                        ReadInt(x, "charm_chance_bp", 0)).DefaultIfEmpty(0).Min(),
                    ["maximumChanceBasisPoints"] = incidentDetails.Select(x =>
                        ReadInt(x, "charm_chance_bp", 0)).DefaultIfEmpty(0).Max()
                },
                ["acknowledgedNotices"] = ReadInt(incidents, "acknowledged", 0),
                ["pendingNotices"] = ReadInt(incidents, "pending_notices", 0),
                ["incidentFailures"] = failedIncidents,
                ["relationshipEffects"] = ReadInt(effects, "total", 0),
                ["relationshipEffectsApplied"] = ReadInt(effects, "applied", 0),
                ["relationshipEffectsPending"] = pendingEffects,
                ["relationshipEffectsFailed"] = failedEffects,
                ["relationshipEffectsByKind"] = effectKinds.Cast<object>().ToList(),
                ["duplicateEffects"] = duplicateEffects,
                ["ruleViolations"] = ruleViolations,
                ["catalogSize"] = ClanConflictCatalog.Count,
                ["recentIncidents"] = recentIncidents.Cast<object>().ToList(),
                ["anomalies"] = anomalies.Cast<object>().ToList()
            };
        }

        private static int CountClanConflictRuleViolations(ReignDbConnection connection,
            string campaignId, string timelineId)
        {
            int violations = 0;
            List<Dictionary<string, object>> incidents = RelationshipsStore.SelectClanConflictIncidents2(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                });
            foreach (Dictionary<string, object> incident in incidents)
            {
                List<Dictionary<string, object>> votes = ReadDictionaryList(
                    TryParseJsonObject("{\"votes\":" + ReadString(incident, "trait_votes_json", "[]") + "}"), "votes");
                if (votes.Count != 3 || votes.Count(x => ReadString(x, "trait", "") == "judgment") != 1
                    || votes.Any(x => Math.Abs(ReadInt(x, "relationModifier", 0)) > ClanConflictRelationBiasCap))
                    violations++;
                int expectedBp = ClanConflictCharmChanceBasisPoints(ReadInt(incident, "charm_skill", 0));
                if (ReadInt(incident, "charm_chance_bp", 0) != expectedBp) violations++;
                List<string> a = ReadJsonStringList(ReadString(incident, "involved_a_json", "[]"));
                List<string> b = ReadJsonStringList(ReadString(incident, "involved_b_json", "[]"));
                if (!a.Contains(ReadString(incident, "leader_a_id", ""), StringComparer.OrdinalIgnoreCase)
                    || !b.Contains(ReadString(incident, "leader_b_id", ""), StringComparer.OrdinalIgnoreCase)
                    || a.Count > 6 || b.Count > 6) violations++;
            }
            List<Dictionary<string, object>> effects = RelationshipsStore.SelectClanConflictRelationshipEffects2(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                });
            foreach (Dictionary<string, object> effect in effects)
            {
                string kind = ReadString(effect, "effect_kind", "");
                int amount = ReadInt(effect, "amount", 0);
                bool valid = kind == "mediation_mutual" ? amount >= 1 && amount <= 25
                    : kind == "feud_mutual" ? amount <= -1 && amount >= -25
                    : kind == "winner_to_ruler" ? amount >= 1 && amount <= 15
                    : kind == "loser_to_ruler" && amount <= -1 && amount >= -15;
                if (!valid) violations++;
            }
            return violations;
        }

        internal static List<Dictionary<string, object>> RunClanConflictSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => rows.Add(
                new Dictionary<string, object>
                {
                    ["ok"] = true, ["passed"] = passed,
                    ["suite"] = "clan_conflicts", ["caseId"] = id,
                    ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
                });
            add("per_kingdom_chance_is_eight_percent",
                ClanConflictIncidentChance == 8,
                "Each eligible NPC kingdom independently rolls at the configured 8% daily chance.");
            add("catalog_contains_128_pregenerated_cases",
                ClanConflictCatalog.Count >= 128
                && ClanConflictCatalog.All(x => x.TraitA != "judgment"
                    && x.TraitB != "judgment"),
                "The pregenerated catalog contains at least 128 cases and reserves Judgment as the mandatory third trait.");
            add("relation_bias_is_one_fifth_capped_twenty",
                ClanConflictRelationModifier(40, 0) == 8
                && ClanConflictRelationModifier(100, -100) == 20
                && ClanConflictRelationModifier(-100, 100) == -20,
                "Every trait threshold receives one fifth of the ruler's relation advantage, capped at plus or minus 20.");
            add("charm_curve_uses_exact_endpoints",
                ClanConflictCharmChanceBasisPoints(0) == 500
                && ClanConflictCharmChanceBasisPoints(50) == 500
                && ClanConflictCharmChanceBasisPoints(125) == 1750
                && ClanConflictCharmChanceBasisPoints(200) == 3000
                && ClanConflictCharmChanceBasisPoints(300) == 3000,
                "Charm 50 and 200 map exactly to 5% and 30%, with linear interpolation and hard caps.");
            add("daily_catch_up_is_lossless",
                ClanConflictCatchUpDayKeys(100, 103).SequenceEqual(new[] { 101, 102, 103 })
                && ClanConflictCatchUpDayKeys(7, 7).SequenceEqual(new[] { 7 }),
                "The per-kingdom producer processes every elapsed day idempotently.");
            List<ClanConflictEffectSpec> success = BuildClanConflictEffects(
                "campaign", "main", 10, "incident", "ruler",
                new List<string> { "a1", "a2" }, new List<string> { "b1" }, "a", true);
            List<ClanConflictEffectSpec> failure = BuildClanConflictEffects(
                "campaign", "main", 10, "incident2", "ruler",
                new List<string> { "a1", "a2" }, new List<string> { "b1" }, "a", false);
            add("outcome_graph_matches_contract",
                success.Count == 12
                && success.All(x => x.Kind == "mediation_mutual" && x.Amount >= 1 && x.Amount <= 25)
                && failure.Count == 7
                && failure.Count(x => x.Kind == "feud_mutual") == 4
                && failure.Count(x => x.Kind == "winner_to_ruler") == 2
                && failure.Count(x => x.Kind == "loser_to_ruler") == 1
                && !failure.Any(x => x.ObserverId == "ruler"),
                "Mediation rewards the full mutual clique; failed mediation creates only the mutual cross-clan feud and directional lord-to-ruler effects.");
            RunClanConflictNoticeContractSelfTests(add);
            string persistenceCampaign = "__clan_conflict_contract_"
                + Guid.NewGuid().ToString("N").Substring(0, 8);
            const string persistenceTimeline = "main";
            double persistenceWorldDay = 0d;
            try
            {
                const string persistenceKingdom = "kingdom_contract";
                int persistenceDay = Enumerable.Range(6, 512).First(day =>
                    DiplomacyService.StableRulerD100(persistenceCampaign, persistenceTimeline, day,
                        "clan-conflict:daily:" + persistenceKingdom)
                    <= ClanConflictIncidentChance);
                persistenceWorldDay = persistenceDay;
                Dictionary<string, object> fixture = new Dictionary<string, object>
                {
                    ["kingdoms"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["kingdomId"] = persistenceKingdom,
                            ["name"] = "Contract Kingdom",
                            ["leaderHeroId"] = "contract_ruler",
                            ["leaderName"] = "Contract Ruler",
                            ["rulingClanId"] = "contract_ruling_clan",
                            ["isPlayerKingdom"] = false,
                            ["isRebelRealm"] = false,
                            ["rulerSkills"] = new Dictionary<string, object> { ["charm"] = 125 }
                        }
                    },
                    ["clans"] = new List<Dictionary<string, object>>
                    {
                        ClanConflictSelfTestClan(persistenceKingdom, "contract_clan_a",
                            "Contract Clan A", "contract_lord_a", "Contract Lord A"),
                        ClanConflictSelfTestClan(persistenceKingdom, "contract_clan_b",
                            "Contract Clan B", "contract_lord_b", "Contract Lord B")
                    }
                };
                ProcessClanConflictDay(persistenceCampaign, persistenceTimeline,
                    persistenceDay, fixture);
                ProcessClanConflictDay(persistenceCampaign, persistenceTimeline,
                    persistenceDay, fixture);
                Dictionary<string, object> persistenceOverview;
                int rollCount;
                int incidentCount;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(persistenceCampaign))
                {
                    rollCount = ReadInt(RelationshipsStore.CountClanConflictDailyRolls3(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = persistenceCampaign,
                            ["timeline"] = persistenceTimeline
                        }).FirstOrDefault(), "count", 0);
                    incidentCount = ReadInt(RelationshipsStore.CountClanConflictIncidents2(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = persistenceCampaign,
                            ["timeline"] = persistenceTimeline
                        }).FirstOrDefault(), "count", 0);
                    persistenceOverview = BuildWorldTestClanConflicts(connection,
                        persistenceCampaign, persistenceTimeline, persistenceDay);
                }
                add("persistence_and_resume_are_idempotent",
                    rollCount == 1 && incidentCount == 1
                    && ReadInt(persistenceOverview, "kingdomRollsMissing", -1) == 0
                    && ReadInt(persistenceOverview, "duplicateEffects", -1) == 0
                    && ReadInt(persistenceOverview, "relationshipEffectsPending", -1) == 0
                    && ReadInt(persistenceOverview, "relationshipEffectsFailed", -1) == 0
                    && ReadInt(persistenceOverview, "ruleViolations", -1) == 0,
                    "A passed roll persists its complete incident and effect provenance exactly once, resumes safely, and reports lossless World Test coverage.");
            }
            catch (Exception ex)
            {
                add("persistence_and_resume_are_idempotent", false,
                    "Clan-conflict persistence contract failed: " + ex.Message);
            }
            try
            {
                Dictionary<string, object> fetched = NextClanConflictNotices(new Dictionary<string, string>
                {
                    ["campaignId"] = persistenceCampaign, ["timelineId"] = persistenceTimeline, ["limit"] = "10"
                });
                List<Dictionary<string, object>> fetchedRows = ReadDictionaryList(fetched, "notices");
                string fetchedIncident = ReadString(fetchedRows.FirstOrDefault(), "incident_id", "");
                string fetchedJson = Json.Serialize(fetched);
                string legacyFetchedJson = Json.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true, ["count"] = fetchedRows.Count,
                    ["notices"] = fetchedRows.Cast<object>().ToList()
                });
                string acknowledgedJson = Json.Serialize(AcknowledgeClanConflictNotice(new Dictionary<string, object>
                {
                    ["campaignId"] = persistenceCampaign, ["timelineId"] = persistenceTimeline,
                    ["incidentId"] = fetchedIncident, ["worldDay"] = persistenceWorldDay
                }));
                string missingJson = Json.Serialize(AcknowledgeClanConflictNotice(new Dictionary<string, object>
                {
                    ["campaignId"] = persistenceCampaign, ["timelineId"] = persistenceTimeline,
                    ["incidentId"] = "missing_incident"
                }));
                string drainedJson = Json.Serialize(NextClanConflictNotices(new Dictionary<string, string>
                {
                    ["campaignId"] = persistenceCampaign, ["timelineId"] = persistenceTimeline
                }));
                add("notice_routes_keep_legacy_wire_bytes",
                    fetchedRows.Count == 1 && fetchedIncident.Length > 0
                    && fetchedJson == legacyFetchedJson
                    && fetchedJson.StartsWith("{\"ok\":true,\"count\":1,\"notices\":[{", StringComparison.Ordinal)
                    && fetchedRows[0].ContainsKey(ClanConflictNoticeFields.NewsDescription)
                    && acknowledgedJson == "{\"ok\":true,\"incidentId\":" + Json.Serialize(fetchedIncident) + "}"
                    && missingJson == "{\"ok\":false,\"error\":\"Clan conflict incident was not found.\"}"
                    && drainedJson == "{\"ok\":true,\"count\":0,\"notices\":[]}",
                    "The typed notice and acknowledgement routes return the exact legacy JSON for a fetched, acknowledged, unknown and drained notice.");
            }
            catch (Exception ex)
            {
                add("notice_routes_keep_legacy_wire_bytes", false,
                    "Clan-conflict notice route contract failed: " + ex.Message);
            }
            // Both blocks above catch everything, so the fixture is always removed. The suite also runs outside the
            // verification sandbox (--run-relationship-tests, /tests runs).
            try { ReignPostgreSqlStorage.DropCampaign(persistenceCampaign); } catch { }
            PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(persistenceCampaign));
            return rows;
        }

        // Byte equality of the typed clan-conflict contract against the pre-contract Dictionary shapes.
        private static void RunClanConflictNoticeContractSelfTests(Action<string, bool, string> add)
        {
            try
            {
                var rows = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["incident_id"] = "clan_conflict_a", ["campaign_id"] = "c", ["timeline_id"] = "main",
                        ["day_key"] = 12L, ["world_day"] = 12.25d, ["kingdom_id"] = "k", ["charm_skill"] = 125L,
                        ["charm_chance_bp"] = 1750L, ["mediation_succeeded"] = 1L, ["headline"] = "Quote \" and <tag>",
                        ["involved_a_names_json"] = "[\"A\"]", ["news_description"] = ""
                    },
                    new Dictionary<string, object> { ["incident_id"] = "clan_conflict_b", ["world_day"] = 3d, ["narrative"] = null }
                };
                bool noticesEqual = true;
                foreach (List<Dictionary<string, object>> sample in new[] { rows, new List<Dictionary<string, object>>() })
                {
                    string legacy = Json.Serialize(new Dictionary<string, object>
                    {
                        ["ok"] = true, ["count"] = sample.Count, ["notices"] = sample.Cast<object>().ToList()
                    });
                    string typed = Json.Serialize(new ClanConflictNoticesResponse(sample).ToDictionary());
                    ClanConflictNoticesResponse parsed = ClanConflictNoticesResponse.FromDictionary(
                        Json.Deserialize<Dictionary<string, object>>(typed));
                    noticesEqual &= typed == legacy && parsed != null && parsed.Count == sample.Count
                        && Json.Serialize(parsed.ToDictionary()) == legacy;
                }
                string acknowledgedLegacy = Json.Serialize(new Dictionary<string, object> { ["ok"] = true, ["incidentId"] = "clan_conflict_a" });
                string missingLegacy = Json.Serialize(new Dictionary<string, object> { ["ok"] = false, ["error"] = "Clan conflict incident was not found." });
                ClanConflictAcknowledgeResponse acknowledgedParsed = ClanConflictAcknowledgeResponse.FromDictionary(
                    Json.Deserialize<Dictionary<string, object>>(acknowledgedLegacy));
                ClanConflictAcknowledgeResponse missingParsed = ClanConflictAcknowledgeResponse.FromDictionary(
                    Json.Deserialize<Dictionary<string, object>>(missingLegacy));
                bool acknowledgeEqual = Json.Serialize(ClanConflictAcknowledgeResponse.Acknowledged("clan_conflict_a").ToDictionary()) == acknowledgedLegacy
                    && Json.Serialize(ClanConflictAcknowledgeResponse.NotFound().ToDictionary()) == missingLegacy
                    && Json.Serialize(acknowledgedParsed.ToDictionary()) == acknowledgedLegacy
                    && Json.Serialize(missingParsed.ToDictionary()) == missingLegacy;
                add("notice_contract_responses_match_legacy_bytes", noticesEqual && acknowledgeEqual,
                    "Typed notice and acknowledgement responses serialize byte-identically to the legacy dictionaries and round-trip.");
            }
            catch (Exception ex)
            {
                add("notice_contract_responses_match_legacy_bytes", false, "Clan-conflict notice response contract failed: " + ex.Message);
            }

            try
            {
                bool requestsEqual = true;
                foreach (string body in new[]
                {
                    "{\"campaignId\":\"camp\",\"timelineId\":\"tl\",\"incidentId\":\"clan_conflict_a\",\"worldDay\":12.5}",
                    "{\"campaignId\":\"camp\",\"timelineId\":\"main\",\"incidentId\":\"clan_conflict_a\"}",
                    "{\"campaignId\":42,\"timelineId\":null,\"incidentId\":7}",
                    "{}"
                })
                {
                    Dictionary<string, object> payload = Json.Deserialize<Dictionary<string, object>>(body);
                    ClanConflictAcknowledgeRequest request = ClanConflictAcknowledgeRequest.FromDictionary(payload);
                    requestsEqual &= request.CampaignId == ReadString(payload, "campaignId", "default")
                        && request.TimelineId == ReadString(payload, "timelineId", "main")
                        && request.IncidentId == ReadString(payload, "incidentId", "");
                }
                string clientBody = "{\"campaignId\":\"camp\",\"timelineId\":\"tl\",\"incidentId\":\"clan_conflict_a\",\"worldDay\":12.5}";
                requestsEqual &= Json.Serialize(ClanConflictAcknowledgeRequest.FromDictionary(
                    Json.Deserialize<Dictionary<string, object>>(clientBody)).ToDictionary()) == clientBody;
                ClanConflictAcknowledgeRequest nullRequest = ClanConflictAcknowledgeRequest.FromDictionary(null);
                requestsEqual &= nullRequest.CampaignId == "default" && nullRequest.TimelineId == "main"
                    && nullRequest.IncidentId == "" && !nullRequest.WorldDay.HasValue;

                foreach (Dictionary<string, string> query in new[]
                {
                    null, new Dictionary<string, string>(),
                    new Dictionary<string, string> { ["limit"] = "0" },
                    new Dictionary<string, string> { ["limit"] = "99" },
                    new Dictionary<string, string> { ["limit"] = "abc" },
                    new Dictionary<string, string> { ["campaignId"] = "", ["timelineId"] = "tl", ["limit"] = "7" }
                })
                {
                    Dictionary<string, string> legacyQuery = query ?? new Dictionary<string, string>();
                    string legacyCampaign = legacyQuery.TryGetValue("campaignId", out string campaign) ? campaign : "default";
                    string legacyTimeline = legacyQuery.TryGetValue("timelineId", out string timeline) ? timeline : "main";
                    int legacyLimit = Math.Max(1, Math.Min(20, legacyQuery.TryGetValue("limit", out string rawLimit)
                        && int.TryParse(rawLimit, out int parsedLimit) ? parsedLimit : 5));
                    ClanConflictNoticesRequest request = ClanConflictNoticesRequest.FromQuery(query);
                    requestsEqual &= request.CampaignId == legacyCampaign && request.TimelineId == legacyTimeline
                        && request.Limit == legacyLimit;
                }
                requestsEqual &= new ClanConflictNoticesRequest { CampaignId = "my camp", TimelineId = "main", Limit = 10 }.ToRelativeUri()
                    == "/clan-conflicts/notices?campaignId=my%20camp&timelineId=main&limit=10";
                add("notice_contract_requests_match_legacy_parsing", requestsEqual,
                    "Typed notice query and acknowledgement body parsing equals the legacy server reads, and the client body round-trips byte-identically.");
            }
            catch (Exception ex)
            {
                add("notice_contract_requests_match_legacy_parsing", false, "Clan-conflict notice request contract failed: " + ex.Message);
            }
        }

        private static Dictionary<string, object> ClanConflictSelfTestClan(
            string kingdomId, string clanId, string clanName,
            string leaderId, string leaderName)
        {
            return new Dictionary<string, object>
            {
                ["kingdomId"] = kingdomId, ["clanId"] = clanId,
                ["name"] = clanName, ["leaderHeroId"] = leaderId,
                ["leaderName"] = leaderName, ["leaderIsAlive"] = true,
                ["leaderIsChild"] = false, ["isMercenary"] = false,
                ["isRebelClan"] = false,
                ["members"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["heroId"] = leaderId, ["name"] = leaderName,
                        ["isAlive"] = true, ["isChild"] = false, ["isLord"] = true
                    }
                }
            };
        }
    }
}
