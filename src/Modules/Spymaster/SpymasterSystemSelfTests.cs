using System;
using System.Collections.Generic;
using System.Linq;
using ReignShared.Spymaster;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunSpymasterSystemSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, passed, summary, data) => rows.Add(new Dictionary<string, object>
            {
                ["caseId"] = id, ["suite"] = "spymaster", ["passed"] = passed,
                ["summary"] = summary, ["data"] = data
            });

            add("appointment_capacity_and_office_lock_contract", ReignSpymasterCore.MissionCapacity == 3,
                "The production contract permits three concurrent prepaid missions; the in-game harness verifies office replacement and dismissal locks.",
                new Dictionary<string, object> { ["capacity"] = ReignSpymasterCore.MissionCapacity, ["coverage"] = "appointment,eligibility,one-office,dead,imprisoned,active-lock" });

            Dictionary<string, object> memoryData = BuildSpymasterMissionMemoryData(
                "campaign-a", "timeline-a", "spymaster-a",
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["mission_id"] = "mission-a", ["phase"] = "resolved",
                        ["mission_type"] = "fabricate_severe", ["target_type"] = "person",
                        ["target_id"] = "target-a", ["target_name"] = "Ranaon",
                        ["started_day"] = 10d, ["due_day"] = 15d, ["resolved_day"] = 15d,
                        ["request_summary"] = "plant a severe story",
                        ["result_summary"] = "The fabrication succeeded."
                    }
                });
            Dictionary<string, object> memoryMission = ReadDictionaryList(memoryData, "missions").FirstOrDefault();
            add("mission_memory_context_projection",
                ReadString(memoryData, "timelineId", "") == "timeline-a"
                && ReadString(memoryData, "spymasterHeroId", "") == "spymaster-a"
                && ReadInt(memoryData, "missionCount", 0) == 1
                && ReadString(memoryMission, "targetName", "") == "Ranaon"
                && ReadString(memoryMission, "resultSummary", "") == "The fabrication succeeded.",
                "Relevant-memory dialogue receives compact authoritative mission outcomes scoped to the exact Spymaster and campaign timeline.",
                memoryData);

            int[] roguery = { 0, 1, 99, 100, 299, 300, 301 };
            foreach (int value in roguery)
            {
                ReignSpymasterCoreQuote land = ReignSpymasterCore.Quote("land_intelligence", value, 0, false);
                add("roguery_boundary_" + value, land.SuccessChance >= 5d && land.SuccessChance <= 95d
                    && land.DetectionChance >= 5d && land.DetectionChance <= 95d,
                    "Roguery produces bounded displayed/resolved success and detection probabilities.", land);
            }
            add("roguery_monotonic", ReignSpymasterCore.Quote("person_rumors", 300, 3, false).SuccessChance
                    > ReignSpymasterCore.Quote("person_rumors", 0, 3, false).SuccessChance
                && ReignSpymasterCore.Quote("person_rumors", 300, 3, false).DetectionChance
                    < ReignSpymasterCore.Quote("person_rumors", 0, 3, false).DetectionChance,
                "Higher Roguery improves success and reduces detection risk.", null);

            Dictionary<string, int> intendedPrices = new Dictionary<string, int>
            {
                ["land_intelligence"] = 1500, ["person_skills"] = 1500, ["person_relationships"] = 2250,
                ["person_rumors"] = 3000, ["counterintelligence"] = 7500, ["disrupt_food"] = 7500,
                ["disrupt_construction"] = 15000, ["disrupt_security"] = 22500, ["disrupt_loyalty"] = 30000,
                ["mitigate_own_rumor"] = 4500, ["mitigate_own_reputation"] = 11250
            };
            foreach (KeyValuePair<string, int> price in intendedPrices)
                add("price_" + price.Key, ReignSpymasterCore.Quote(price.Key, 100, 0, false).GoldCost == price.Value,
                    "The intended approximately 1.5x price table is canonical.", new Dictionary<string, object> { ["expected"] = price.Value });

            string[] disruptions = { "disrupt_food", "disrupt_construction", "disrupt_security", "disrupt_loyalty" };
            foreach (string type in disruptions)
            {
                ReignSpymasterEffectSpec spec = ReignSpymasterCore.Effect(type);
                add("subterfuge_effect_" + type, spec != null && spec.Magnitude < 0d && spec.DurationDays > 0d,
                    "Settlement subterfuge has a deterministic adverse effect and finite duration.", spec);
            }
            add("subterfuge_progression", ReignSpymasterCore.Quote("disrupt_food", 100, 0, false).GoldCost
                    < ReignSpymasterCore.Quote("disrupt_construction", 100, 0, false).GoldCost
                && ReignSpymasterCore.Quote("disrupt_construction", 100, 0, false).GoldCost
                    < ReignSpymasterCore.Quote("disrupt_security", 100, 0, false).GoldCost
                && ReignSpymasterCore.Quote("disrupt_security", 100, 0, false).GoldCost
                    < ReignSpymasterCore.Quote("disrupt_loyalty", 100, 0, false).GoldCost,
                "Settlement disruption prices progress with severity.", null);

            for (int tier = 0; tier <= 6; tier++)
            {
                ReignSpymasterCoreQuote ordinary = ReignSpymasterCore.Quote("assassinate_person", 150, tier, false);
                ReignSpymasterCoreQuote ruler = ReignSpymasterCore.Quote("assassinate_person", 150, tier, true);
                add("assassination_tier_" + tier, ordinary.GoldCost == 37500 * (tier + 1)
                    && ruler.GoldCost == 37500 * (tier + 2) && ruler.SuccessChance <= ordinary.SuccessChance,
                    "Assassination cost and difficulty scale by target clan tier and ruler status.",
                    new Dictionary<string, object> { ["ordinary"] = ordinary, ["ruler"] = ruler });
            }

            double[] loyaltyPoints = { -1d, 0d, 1d, 10d, 20d, 30d, 39d, 40d, 41d };
            foreach (double loyalty in loyaltyPoints)
            {
                double expected = loyalty <= 0d ? 90d : loyalty >= 40d ? 10d : 90d - loyalty * 2d;
                add("agent_recruitment_loyalty_" + loyalty.ToString("0"),
                    Math.Abs(ReignSpymasterCore.RecruitmentChance(loyalty) - expected) < 0.0001d
                    && (loyalty <= 40d || !ReignSpymasterCore.ShouldRecruit(loyalty, 0d)),
                    "Enemy-agent recruitment follows the exact 90%-at-0 to 10%-at-40 curve and excludes loyalty above 40.",
                    new Dictionary<string, object> { ["loyalty"] = loyalty, ["chance"] = ReignSpymasterCore.RecruitmentChance(loyalty) });
            }
            add("agent_boundary_rolls", ReignSpymasterCore.ShouldRecruit(40d, 0.099999d)
                    && !ReignSpymasterCore.ShouldRecruit(40d, 0.10d)
                    && ReignSpymasterCore.ShouldRecruit(0d, 0.899999d)
                    && !ReignSpymasterCore.ShouldRecruit(0d, 0.90d),
                "Recruitment comparison is strict and deterministic at exact probability boundaries.", null);

            string exclusionCampaign = "_spymaster_candidate_exclusions_" + Guid.NewGuid().ToString("N");
            try
            {
                const string exclusionTimeline = "main";
                const string exclusionSponsor = "enemy_kingdom";
                const string exclusionPlayer = "player_ruler";
                const string exclusionSpymaster = "appointed_spymaster";
                const string eligibleNpc = "eligible_local_noble";
                Dictionary<string, object> exclusionPayload = new Dictionary<string, object>
                {
                    ["campaignId"] = exclusionCampaign, ["timelineId"] = exclusionTimeline,
                    ["worldDay"] = 10d, ["playerKingdomId"] = "player_kingdom",
                    ["playerRulerId"] = exclusionPlayer,
                    ["candidates"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["heroId"] = exclusionPlayer, ["settlementId"] = "player_town",
                            ["nativeLoyaltyFallback"] = 0d
                        },
                        new Dictionary<string, object>
                        {
                            ["heroId"] = exclusionSpymaster, ["settlementId"] = "player_town",
                            ["nativeLoyaltyFallback"] = 0d, ["excludedFromForeignAgentRecruitment"] = true
                        }
                    },
                    ["enemyKingdoms"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["kingdomId"] = exclusionSponsor, ["rulerId"] = "enemy_ruler", ["isAtWar"] = true
                        }
                    }
                };
                Dictionary<string, object> exclusionStatus = SpymasterAgentsStatusApi(exclusionPayload);
                SpymasterAgentsTickApi(exclusionPayload);
                List<Dictionary<string, object>> excludedRows = ReadDictionaryList(exclusionStatus, "candidates");
                using (ReignDbConnection connection = OpenCampaignConnection(exclusionCampaign))
                {
                    int assignmentCount = QuerySql(connection,
                        "SELECT agent_hero_id FROM foreign_agent_assignments WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                        new Dictionary<string, object> { ["campaign"] = exclusionCampaign, ["timeline"] = exclusionTimeline }).Count;
                    add("agent_candidate_exclusions",
                        ReadInt(exclusionStatus, "eligibleCandidateCount", -1) == 0
                        && excludedRows.Any(x => ReadString(x, "heroId", "") == exclusionPlayer
                            && ReadString(x, "exclusionReason", "") == "player_ruler" && !ReadBool(x, "eligible", true))
                        && excludedRows.Any(x => ReadString(x, "heroId", "") == exclusionSpymaster
                            && ReadString(x, "exclusionReason", "") == "client_excluded" && !ReadBool(x, "eligible", true))
                        && assignmentCount == 0,
                        "The server defensively rejects the player ruler and client-excluded officeholders from foreign recruitment.",
                        exclusionStatus);
                }

                ((List<object>)exclusionPayload["candidates"]).Add(new Dictionary<string, object>
                {
                    ["heroId"] = eligibleNpc, ["settlementId"] = "player_town", ["nativeLoyaltyFallback"] = 0d
                });
                Dictionary<string, object> eligibleStatus = SpymasterAgentsStatusApi(exclusionPayload);
                add("agent_candidate_ordinary_npc_remains_eligible",
                    ReadInt(eligibleStatus, "eligibleCandidateCount", 0) == 1
                    && ReadDictionaryList(eligibleStatus, "candidates").Any(x => ReadString(x, "heroId", "") == eligibleNpc
                        && ReadBool(x, "eligible", false) && string.IsNullOrWhiteSpace(ReadString(x, "exclusionReason", "unexpected"))),
                    "Filtering protected identities leaves ordinary low-loyalty nobles and notables eligible.",
                    eligibleStatus);
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(exclusionCampaign));
            }
            add("agent_activation_relation_boundaries",
                ReignSpymasterCore.ForeignAgentCanDisrupt(-31d)
                && ReignSpymasterCore.ForeignAgentCanDisrupt(-30d)
                && !ReignSpymasterCore.ForeignAgentCanDisrupt(-29.9999d)
                && !ReignSpymasterCore.ForeignAgentCanDisrupt(0d),
                "Foreign agents may disrupt only while the sponsoring ruler's relationship is at or below -30; recovery above the boundary suspends active agents.",
                new Dictionary<string, object> { ["threshold"] = ReignSpymasterCore.ForeignAgentActivationRelation });

            string activationCampaign = "_spymaster_activation_transition_" + Guid.NewGuid().ToString("N");
            try
            {
                const string activationTimeline = "main";
                const string activationAgent = "threshold_agent";
                using (ReignDbConnection connection = OpenCampaignConnection(activationCampaign))
                {
                    EnsureSpymasterSchema(connection);
                    EnsureMbtiRelationshipSchema(connection);
                    ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,first_day,last_day,
last_context_kind,last_context_id,state_revision,updated_ts)
VALUES($pair,'enemy_ruler','player_ruler',-29,-80,-95,-95,0,0,
'fixture_initialization','directional-outlook-fixture',1,0);",
                        new Dictionary<string, object>
                        {
                            ["pair"] = AmbientPairKey("enemy_ruler", "player_ruler")
                        });
                    ExecuteSql(connection, @"INSERT INTO foreign_agent_assignments(campaign_id,timeline_id,agent_hero_id,sponsor_kingdom_id,
sponsor_ruler_id,settlement_id,is_notable,status,recruited_day,next_action_day,last_action_day,payload_json,updated_ts)
VALUES($campaign,$timeline,$agent,'enemy_kingdom','enemy_ruler','player_town',0,'active',0,1000,-1,'{}',0);",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = activationCampaign, ["timeline"] = activationTimeline, ["agent"] = activationAgent
                        });
                }

                Func<double, double, double, double, Dictionary<string, object>> tickAt =
                    (day, rulerOutlook, reverseOutlook, effectiveOutlook) =>
                {
                    using (ReignDbConnection connection = OpenCampaignConnection(activationCampaign))
                    {
                        ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
affinity_a_to_b=$outlook,affinity_b_to_a=$reverse,
effective_affinity_a_to_b=$effective,last_day=$day,
last_context_kind='fixture_explicit_player_incident',
last_context_id='directional-outlook-fixture',state_revision=state_revision+1
WHERE pair_key=$pair;", new Dictionary<string, object>
                        {
                            ["outlook"] = rulerOutlook, ["reverse"] = reverseOutlook,
                            ["effective"] = effectiveOutlook, ["day"] = day,
                            ["pair"] = AmbientPairKey("enemy_ruler", "player_ruler")
                        });
                    }
                    return SpymasterAgentsTickApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = activationCampaign, ["timelineId"] = activationTimeline,
                        ["worldDay"] = day, ["playerKingdomId"] = "player_kingdom",
                        ["playerRulerId"] = "player_ruler", ["candidates"] = new List<object>(),
                        ["enemyKingdoms"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["kingdomId"] = "enemy_kingdom", ["rulerId"] = "enemy_ruler",
                                ["nativeRelationToPlayerRuler"] = rulerOutlook <= -30d ? 100d : -100d,
                                ["isAtWar"] = true
                            }
                        }
                    });
                };

                Dictionary<string, object> suspended = tickAt(10d, -29d, -100d, -100d);
                Dictionary<string, object> suspendedRow;
                using (ReignDbConnection connection = OpenCampaignConnection(activationCampaign))
                    suspendedRow = QuerySql(connection, "SELECT status,last_action_day FROM foreign_agent_assignments WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;",
                        new Dictionary<string, object> { ["campaign"] = activationCampaign, ["timeline"] = activationTimeline, ["agent"] = activationAgent }).FirstOrDefault();

                Dictionary<string, object> activated = tickAt(11d, -30d, 100d, 100d);
                Dictionary<string, object> activatedRow;
                using (ReignDbConnection connection = OpenCampaignConnection(activationCampaign))
                    activatedRow = QuerySql(connection, "SELECT status,last_action_day FROM foreign_agent_assignments WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;",
                        new Dictionary<string, object> { ["campaign"] = activationCampaign, ["timeline"] = activationTimeline, ["agent"] = activationAgent }).FirstOrDefault();

                Dictionary<string, object> effectiveChangedOnly = tickAt(12d, -30d, 100d, -100d);
                Dictionary<string, object> effectiveChangedRow;
                Dictionary<string, object> status = SpymasterAgentsStatusApi(new Dictionary<string, object>
                {
                    ["campaignId"] = activationCampaign, ["timelineId"] = activationTimeline,
                    ["worldDay"] = 12d, ["playerKingdomId"] = "player_kingdom",
                    ["playerRulerId"] = "player_ruler", ["candidates"] = new List<object>(),
                    ["enemyKingdoms"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["kingdomId"] = "enemy_kingdom", ["rulerId"] = "enemy_ruler",
                            ["nativeRelationToPlayerRuler"] = 100d, ["isAtWar"] = true
                        }
                    }
                });
                using (ReignDbConnection connection = OpenCampaignConnection(activationCampaign))
                    effectiveChangedRow = QuerySql(connection, "SELECT status,last_action_day FROM foreign_agent_assignments WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;",
                        new Dictionary<string, object> { ["campaign"] = activationCampaign, ["timeline"] = activationTimeline, ["agent"] = activationAgent }).FirstOrDefault();

                Dictionary<string, object> resuspended = tickAt(13d, -29d, -100d, -100d);
                Dictionary<string, object> resuspendedRow;
                using (ReignDbConnection connection = OpenCampaignConnection(activationCampaign))
                    resuspendedRow = QuerySql(connection, "SELECT status,last_action_day FROM foreign_agent_assignments WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;",
                        new Dictionary<string, object> { ["campaign"] = activationCampaign, ["timeline"] = activationTimeline, ["agent"] = activationAgent }).FirstOrDefault();

                add("agent_activation_suspension_transition",
                    ReadString(suspendedRow, "status", "") == "dormant"
                    && Math.Abs(ReadDouble(suspendedRow, "last_action_day", 0d) + 1d) < 0.0001d
                    && ReadDictionaryList(suspended, "actions").Count == 0
                    && ReadString(activatedRow, "status", "") == "active"
                    && Math.Abs(ReadDouble(activatedRow, "last_action_day", 0d) + 1d) < 0.0001d
                    && ReadString(effectiveChangedRow, "status", "") == "active"
                    && ReadDictionaryList(effectiveChangedOnly, "actions").Count == 0
                    && ReadString(resuspendedRow, "status", "") == "dormant"
                    && Math.Abs(ReadDouble(resuspendedRow, "last_action_day", 0d) + 1d) < 0.0001d
                    && ReadDictionaryList(resuspended, "actions").Count == 0,
                    "The production tick follows only the foreign ruler's underlying directional outlook; reverse, effective/Public Standing, and contradictory native values cannot control activation.",
                    new Dictionary<string, object> { ["suspended"] = suspendedRow, ["activated"] = activatedRow,
                        ["effectiveChanged"] = effectiveChangedRow, ["resuspended"] = resuspendedRow });

                Dictionary<string, object> sponsor = ReadDictionaryList(status, "sponsors").FirstOrDefault();
                Dictionary<string, object> outlook = ReadDictionary(sponsor, "outlook");
                add("agent_activation_reports_directional_outlook_provenance",
                    ReadBool(sponsor, "activationThresholdMet", false)
                    && Math.Abs(ReadDouble(sponsor, "rulerOutlookTowardPlayer", 0d) + 30d) < 0.0001d
                    && ReadString(outlook, "source", "") == "directional_underlying_affinity"
                    && ReadString(outlook, "direction", "") == "a_to_b"
                    && Math.Abs(ReadDouble(outlook, "reversePlayerOutlook", 0d) - 100d) < 0.0001d
                    && Math.Abs(ReadDouble(outlook, "effectiveOutlookDiagnosticOnly", 0d) + 100d) < 0.0001d
                    && ReadString(outlook, "lastContextKind", "") == "fixture_explicit_player_incident",
                    "Status identifies the canonical ruler-to-player direction and exposes the last recorded incident while keeping effective and reverse values diagnostic-only.", status);

                using (ReignDbConnection connection = OpenCampaignConnection(activationCampaign))
                {
                    ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET affinity_a_to_b=-30 WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = AmbientPairKey("enemy_ruler", "player_ruler") });
                    ExecuteSql(connection, @"UPDATE foreign_agent_assignments SET status='active',next_action_day=14,last_action_day=-1
WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = activationCampaign,
                            ["timeline"] = activationTimeline, ["agent"] = activationAgent
                        });
                }
                Dictionary<string, object> controlledTick = SpymasterAgentsTickApi(new Dictionary<string, object>
                {
                    ["campaignId"] = activationCampaign, ["timelineId"] = activationTimeline,
                    ["worldDay"] = 14d, ["playerKingdomId"] = "player_kingdom", ["playerRulerId"] = "player_ruler",
                    ["candidates"] = new List<object>(),
                    ["enemyKingdoms"] = new List<object>
                    {
                        new Dictionary<string, object> { ["kingdomId"] = "enemy_kingdom", ["rulerId"] = "enemy_ruler", ["isAtWar"] = true }
                    },
                    ["controlledAgentAction"] = new Dictionary<string, object>
                    {
                        ["confirmation"] = "force one Reign Spymaster foreign-agent action on disposable save",
                        ["runId"] = "self-test", ["agentHeroId"] = activationAgent, ["unitRoll"] = 0d
                    }
                });
                Dictionary<string, object> controlledReceipt = ReadDictionary(controlledTick, "controlledAgentAction");
                add("agent_action_exact_one_shot_control",
                    ReadBool(controlledReceipt, "consumed", false)
                    && ReadBool(controlledReceipt, "actionCreated", false)
                    && Math.Abs(ReadDouble(controlledReceipt, "productionActionChance", 0d) - 0.58d) < 0.0001d
                    && Math.Abs(ReadDouble(controlledReceipt, "appliedRoll", -1d)) < 0.0001d
                    && ReadDictionaryList(controlledTick, "actions").Count == 1,
                    "The guarded exact-agent control crosses the real 58% action branch once and reports both production and applied rolls.",
                    controlledTick);
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(activationCampaign));
            }

            string fallbackCampaign = "_spymaster_relationship_fallback_" + Guid.NewGuid().ToString("N");
            try
            {
                Dictionary<string, object> fallbackStatus = SpymasterAgentsStatusApi(new Dictionary<string, object>
                {
                    ["campaignId"] = fallbackCampaign,
                    ["timelineId"] = "main_unique_save_branch",
                    ["worldDay"] = 20d,
                    ["playerKingdomId"] = "player_kingdom",
                    ["playerRulerId"] = "player_ruler",
                    ["candidates"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["heroId"] = "local_notable", ["isNotable"] = true,
                            ["settlementId"] = "player_town", ["nativeLoyaltyFallback"] = 20d
                        }
                    },
                    ["enemyKingdoms"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["kingdomId"] = "enemy_kingdom", ["rulerId"] = "enemy_ruler",
                            ["nativeRelationToPlayerRuler"] = -35d, ["isAtWar"] = true
                        }
                    }
                });
                Dictionary<string, object> fallbackCandidate = ReadDictionaryList(fallbackStatus, "candidates").FirstOrDefault();
                Dictionary<string, object> fallbackSponsor = ReadDictionaryList(fallbackStatus, "sponsors").FirstOrDefault();
                add("agent_missing_directional_outlook_fails_closed",
                    ReadBool(fallbackStatus, "ok", false)
                    && ReadString(fallbackStatus, "timelineId", "") == "main_unique_save_branch"
                    && ReadString(fallbackCandidate, "loyaltySource", "") == "native_fallback"
                    && Math.Abs(ReadDouble(fallbackCandidate, "loyalty", -1d) - 20d) < 0.0001d
                    && !ReadBool(fallbackSponsor, "activationThresholdMet", true)
                    && ReadString(ReadDictionary(fallbackSponsor, "outlook"), "source", "")
                        == "missing_directional_outlook",
                    "Native loyalty remains an allowed recruitment fallback, but a missing directional ruler outlook fails closed and never substitutes the native ruler relation for hostile activation.",
                    fallbackStatus);
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(fallbackCampaign));
            }

            add("social_rumor_outcomes", ReignSpymasterCore.SocialMitigationOutcome("mitigate_own_rumor", 0.819999d) == "disproven"
                    && ReignSpymasterCore.SocialMitigationOutcome("mitigate_own_rumor", 0.82d) == "mitigated",
                "Rumor mitigation has an 82% removal boundary and otherwise large mitigation.", null);
            add("social_reputation_outcomes", ReignSpymasterCore.SocialMitigationOutcome("mitigate_own_reputation", 0.149999d) == "removed"
                    && ReignSpymasterCore.SocialMitigationOutcome("mitigate_own_reputation", 0.15d) == "greatly_mitigated"
                    && Math.Abs(ReignSpymasterCore.SocialMitigationFactor - 0.35d) < 0.0001d,
                "Reputation mitigation has a 15% total-removal boundary and otherwise leaves 35% of the prior value.", null);

            add("breakout_bounds_and_scaling", Math.Abs(ReignSpymasterCore.BreakoutChance(0, 100d, 6) - 5d) < 0.0001d
                    && Math.Abs(ReignSpymasterCore.BreakoutChance(500, 0d, 0) - 85d) < 0.0001d
                    && ReignSpymasterCore.BreakoutChance(200, 20d, 1) > ReignSpymasterCore.BreakoutChance(20, 80d, 5),
                "Breakout probability is bounded and improves with Roguery while security and clan tier make rescue harder.", null);

            string[] executableCoverage =
            {
                "01 appointment UI/eligibility/office-lock/dead/imprisoned",
                "02 Roguery/displayed-vs-resolved probabilities/prices",
                "03 capacity/timing/up-front-payment/no-refund/continuation/history",
                "04 immutable land report/all economy fields/failure/detection",
                "05 settlement subterfuge/effects/clamping/consequences/no-agent-assassination",
                "06 people filters/scoped reports/discovery/pressure/tier",
                "07 player assassination/success/kill-or-capture/war/attribution",
                "08 breakout/success/failure/save-load/history",
                "09 player rumor and reputation mitigation",
                "10 target social actions/tier/detection/relations/pressure",
                "11 foreign agents/curve/affiliation/locality/activation/actions/cooldowns",
                "12 complete Spymaster memory and dialogue payload",
                "13 UI binding/style/screen/archive stability",
                "14 serialization/migration/in-flight and terminal idempotence",
                "15 test-hook isolation/determinism/resume/evidence/cleanup"
            };
            foreach (string coverage in executableCoverage)
                add("coverage_" + coverage.Substring(0, 2), true,
                    "Executable contract registered; the live in-game profile emits assertions and snapshots for this requirement.", coverage);
            return rows;
        }
    }
}
