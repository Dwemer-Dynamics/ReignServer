using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ReignShared.Spymaster;

namespace ReignBetaServer
{
    internal static partial class SpymasterService
    {
        internal static List<Dictionary<string, object>> RunSpymasterSystemSelfTests()
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
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(exclusionCampaign))
                {
                    int assignmentCount = SpymasterSelfTestStore.SelectForeignAgentAssignments(connection, new Dictionary<string, object> { ["campaign"] = exclusionCampaign, ["timeline"] = exclusionTimeline }).Count;
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
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(exclusionCampaign));
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
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(activationCampaign))
                {
                    EnsureSpymasterSchema(connection);
                    RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                    SpymasterSelfTestStore.InsertRelationshipPairChemistry(connection, new Dictionary<string, object>
                        {
                            ["pair"] = RelationshipsService.AmbientPairKey("enemy_ruler", "player_ruler")
                        });
                    SpymasterSelfTestStore.InsertForeignAgentAssignments(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = activationCampaign, ["timeline"] = activationTimeline, ["agent"] = activationAgent
                        });
                    // Recreate a pre-v4 fixture schema around the existing assignment.
                    ReignDatabase.ExecuteSql(connection, "DROP TABLE foreign_agent_disruption_receipts;");
                    ReignDatabase.ExecuteSql(connection, "ALTER TABLE foreign_agent_assignments DROP COLUMN assignment_id;");
                    ReignDatabase.ExecuteSql(connection, "UPDATE schema_meta SET value=$value WHERE key=$key;",
                        new Dictionary<string, object>
                        {
                            ["key"] = PersistenceService.CampaignMigrationVersionKey,
                            ["value"] = PersistenceService.CorrespondenceArrivalHistoryMigrationVersion.ToString(CultureInfo.InvariantCulture)
                        });
                }
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(activationCampaign))
                {
                    var migrated = SpymasterStore.SelectForeignAgentAssignments5(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = activationCampaign, ["timeline"] = activationTimeline
                    }).Single();
                    add("agent_occurrence_migration_preserves_existing_assignment",
                        ReadString(migrated, "agent_hero_id", "") == activationAgent
                        && ReadString(migrated, "status", "") == "active"
                        && ReadString(migrated, "assignment_id", "missing") == "",
                        "Opening an older campaign upgrades its real assignment table without replacing its existing rows.", null);
                }

                Func<double, double, double, double, Dictionary<string, object>> tickAt =
                    (day, rulerOutlook, reverseOutlook, effectiveOutlook) =>
                {
                    using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(activationCampaign))
                    {
                        SpymasterSelfTestStore.UpdateRelationshipPairChemistry(connection, new Dictionary<string, object>
                        {
                            ["outlook"] = rulerOutlook, ["reverse"] = reverseOutlook,
                            ["effective"] = effectiveOutlook, ["day"] = day,
                            ["pair"] = RelationshipsService.AmbientPairKey("enemy_ruler", "player_ruler")
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
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(activationCampaign))
                    suspendedRow = SpymasterSelfTestStore.SelectForeignAgentAssignments2(connection, new Dictionary<string, object> { ["campaign"] = activationCampaign, ["timeline"] = activationTimeline, ["agent"] = activationAgent }).FirstOrDefault();

                Dictionary<string, object> activated = tickAt(11d, -30d, 100d, 100d);
                Dictionary<string, object> activatedRow;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(activationCampaign))
                    activatedRow = SpymasterSelfTestStore.SelectForeignAgentAssignments2(connection, new Dictionary<string, object> { ["campaign"] = activationCampaign, ["timeline"] = activationTimeline, ["agent"] = activationAgent }).FirstOrDefault();

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
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(activationCampaign))
                    effectiveChangedRow = SpymasterSelfTestStore.SelectForeignAgentAssignments2(connection, new Dictionary<string, object> { ["campaign"] = activationCampaign, ["timeline"] = activationTimeline, ["agent"] = activationAgent }).FirstOrDefault();

                Dictionary<string, object> resuspended = tickAt(13d, -29d, -100d, -100d);
                Dictionary<string, object> resuspendedRow;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(activationCampaign))
                    resuspendedRow = SpymasterSelfTestStore.SelectForeignAgentAssignments2(connection, new Dictionary<string, object> { ["campaign"] = activationCampaign, ["timeline"] = activationTimeline, ["agent"] = activationAgent }).FirstOrDefault();

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

                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(activationCampaign))
                {
                    SpymasterSelfTestStore.UpdateRelationshipPairChemistry2(connection, new Dictionary<string, object> { ["pair"] = RelationshipsService.AmbientPairKey("enemy_ruler", "player_ruler") });
                    SpymasterSelfTestStore.UpdateForeignAgentAssignments(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = activationCampaign,
                            ["timeline"] = activationTimeline, ["agent"] = activationAgent
                        });
                }
                string generation;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(activationCampaign))
                    generation = ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "");
                var controlledPayload = new Dictionary<string, object>
                {
                    ["campaignId"] = activationCampaign, ["timelineId"] = activationTimeline,
                    ["worldDay"] = 14d, ["playerKingdomId"] = "player_kingdom", ["playerRulerId"] = "player_ruler",
                    ["restoreGeneration"] = generation,
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
                };
                var disruptionScope = new Dictionary<string, object>
                {
                    ["campaign"] = activationCampaign, ["timeline"] = activationTimeline
                };
                bool schedulingRejected = false;
                List<Dictionary<string, object>> cancelledReceipts;
                Dictionary<string, object> cancelledAssignment;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(activationCampaign))
                    SpymasterSelfTestStore.SetDisruptionScheduleFailure(connection, true);
                try
                {
                    SpymasterAgentsTickForClientApi(controlledPayload);
                }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.CheckViolation
                    && ex.ConstraintName == "foreign_agent_disruption_fixture_schedule")
                {
                    schedulingRejected = true;
                }
                finally
                {
                    using ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(activationCampaign);
                    SpymasterSelfTestStore.SetDisruptionScheduleFailure(connection, false);
                    cancelledReceipts = SpymasterSelfTestStore.ReadDisruptionReceipts(connection, disruptionScope);
                    cancelledAssignment = SpymasterStore.SelectForeignAgentAssignments5(connection, disruptionScope).Single();
                }
                add("agent_disruption_receipt_rolls_back_with_schedule",
                    schedulingRejected && cancelledReceipts.Count == 0
                    && ReadDouble(cancelledAssignment, "next_action_day", -1d) == 13.75d,
                    "If the production scheduling write fails, the preceding issuance insert rolls back and the action stays due.", null);

                Dictionary<string, object> controlledTick = SpymasterAgentsTickForClientApi(controlledPayload);
                Dictionary<string, object> controlledReceipt = ReadDictionary(controlledTick, "controlledAgentAction");
                add("agent_action_exact_one_shot_control",
                    ReadBool(controlledReceipt, "consumed", false)
                    && ReadBool(controlledReceipt, "actionCreated", false)
                    && Math.Abs(ReadDouble(controlledReceipt, "productionActionChance", 0d) - 0.58d) < 0.0001d
                    && Math.Abs(ReadDouble(controlledReceipt, "appliedRoll", -1d)) < 0.0001d
                    && ReadDictionaryList(controlledTick, "actions").Count == 1,
                    "The guarded exact-agent control crosses the real 58% action branch once and reports both production and applied rolls.",
                    controlledTick);

                var initiallyActive = ReadDictionaryList(controlledTick, "agents").Single();
                string occurrence = ReadString(initiallyActive, "assignmentId", "");
                var issuedAction = ReadDictionaryList(controlledTick, "actions").Single();
                // Model losing the first reply by issuing the same tick with a fresh body.
                var retryPayload = new Dictionary<string, object>(controlledPayload);
                retryPayload.Remove("_controlledAgentActionReceipt");
                var retried = SpymasterAgentsTickForClientApi(retryPayload);
                var stale = SpymasterAgentsTickForClientApi(new Dictionary<string, object>(retryPayload)
                    { ["restoreGeneration"] = generation + "_stale" });
                List<Dictionary<string, object>> issuedReceipts;
                Dictionary<string, object> advancedAssignment;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(activationCampaign))
                {
                    issuedReceipts = SpymasterSelfTestStore.ReadDisruptionReceipts(connection, disruptionScope);
                    advancedAssignment = SpymasterStore.SelectForeignAgentAssignments5(connection, disruptionScope).Single();
                }
                var issuance = issuedReceipts.SingleOrDefault();
                add("agent_disruption_receipt_binds_original_window_and_payload",
                    ReadString(issuance, "timeline_id", "") == activationTimeline
                    && ReadString(issuance, "restore_generation", "<missing>") == generation
                    && ReadString(issuance, "action_id", "") == ReadString(issuedAction, "actionId", "")
                    && ReadString(issuance, "assignment_id", "") == occurrence
                    && ReadDouble(issuance, "scheduled_due_day", -1d) == 13.75d
                    && ReadDouble(issuance, "issued_day", -1d) == 14d
                    && ReadDouble(issuance, "expires_day", -1d) == 14f + (float)ReadDouble(issuedAction, "durationDays", -1d)
                    && ReadString(issuance, "action_json", "") == Json.Serialize(issuedAction)
                    && ReadDouble(advancedAssignment, "next_action_day", -1d) > 14d,
                    "One immutable receipt stores the typed action, assignment, generation and native expiry before advancing the schedule.", issuance);
                add("agent_disruption_retry_keeps_receipt_without_redelivery",
                    ReadBool(retried, "ok", false) && ReadDictionaryList(retried, "actions").Count == 0
                    && ReadString(stale, "error", "") == "assignment_scope_changed" && issuedReceipts.Count == 1,
                    "Losing a reply leaves durable issuance evidence but does not redeliver on retry or accept a stale generation.", null);
                var replayPayload = new Dictionary<string, object>(retryPayload)
                {
                    ["disruptionReplay"] = ForeignAgentDisruptionReplaySchema,
                    ["worldDay"] = 14.25d
                };
                replayPayload.Remove("controlledAgentAction");
                var delayed = SpymasterAgentsTickForClientApi(replayPayload);
                var delayedAction = ReadDictionaryList(delayed, "actions").SingleOrDefault();
                var wrongGeneration = SpymasterAgentsTickForClientApi(new Dictionary<string, object>(replayPayload)
                    { ["restoreGeneration"] = generation + "_stale" });
                add("agent_disruption_opt_in_replays_original_window_once_per_reply",
                    ReadString(delayed, "disruptionReplay", "") == ForeignAgentDisruptionReplaySchema
                    && ReadString(delayedAction, "actionId", "") == ReadString(issuedAction, "actionId", "")
                    && ReadString(delayedAction, "assignmentId", "") == occurrence
                    && ReadDouble(delayedAction, "issuedDay", -1d) == 14d
                    && ReadDouble(delayedAction, "expiresDay", -1d) == ReadDouble(issuance, "expires_day", -2d)
                    && ReadString(wrongGeneration, "error", "") == "assignment_scope_changed",
                    "An eligible replay returns the original committed identity and timing. A different restore generation is rejected.", null);
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(activationCampaign))
                using (var transaction = connection.BeginTransaction())
                {
                    var activeAtExpiry = SpymasterStore.SelectForeignAgentAssignments5(connection, disruptionScope, transaction);
                    var expired = ReadUnexpiredForeignAgentDisruptions(connection, transaction, activationCampaign, activationTimeline,
                        generation, ReadDouble(issuance, "expires_day", -1d), activeAtExpiry,
                        ReadDictionaryList(replayPayload, "enemyKingdoms"), "player_kingdom");
                    add("agent_disruption_replay_excludes_exact_expiry",
                        expired.Count == 0 && activeAtExpiry.Count == 1,
                        "The production replay selector stops delivering at the original expiry boundary.", null);
                }

                var exposure = new Dictionary<string, object>
                {
                    ["campaignId"] = activationCampaign, ["timelineId"] = activationTimeline,
                    ["agentHeroId"] = activationAgent, ["assignmentId"] = occurrence,
                    ["restoreGeneration"] = generation, ["exposureRevision"] = 0,
                    ["worldDay"] = 14d, ["reason"] = "fixture_confirmed"
                };
                var wrongOccurrence = new Dictionary<string, object>(exposure) { ["assignmentId"] = "other" };
                var missing = new Dictionary<string, object>(exposure) { ["agentHeroId"] = "not_present" };
                var replaced = new Dictionary<string, object>(exposure) { ["restoreGeneration"] = generation + "_stale" };
                add("agent_exposure_requires_exact_occurrence_and_scope",
                    !string.IsNullOrEmpty(occurrence)
                    && ReadInt(initiallyActive, "exposureRevision", -1) == 0
                    && ReadString(SpymasterAgentExposeApi(wrongOccurrence), "error", "") == "assignment_not_found"
                    && ReadString(SpymasterAgentExposeApi(missing), "error", "") == "assignment_not_found"
                    && ReadString(SpymasterAgentExposeApi(replaced), "error", "") == "assignment_scope_changed",
                    "Exposure rejects a missing row, another occurrence, or a replaced restore generation.", null);

                var oldExposure = new Dictionary<string, object>
                {
                    ["campaignId"] = activationCampaign, ["timelineId"] = activationTimeline,
                    ["agentHeroId"] = activationAgent, ["reason"] = "legacy_unversioned"
                };
                var oldExposeResponse = RejectLegacyForeignAgentMutation(oldExposure);
                var oldTickResponse = SpymasterAgentsTickForClientApi(oldExposure);
                var typedTickResponse = SpymasterAgentsTickForClientApi(new Dictionary<string, object>
                {
                    ["campaignId"] = activationCampaign, ["timelineId"] = activationTimeline,
                    ["worldDay"] = 14d, ["restoreGeneration"] = generation
                });
                add("agent_legacy_mutation_pauses_without_reviving_exposure",
                    ReadString(oldExposeResponse, "error", "") == "paired_spymaster_update_required"
                    && ReadString(oldTickResponse, "error", "") == "paired_spymaster_update_required"
                    && !ReadBool(oldExposeResponse, "ok", true) && !ReadBool(oldTickResponse, "ok", true)
                    && ReadDictionaryList(oldTickResponse, "agents").Count == 0
                    && ReadDictionaryList(oldTickResponse, "actions").Count == 0
                    && ReadBool(typedTickResponse, "ok", false)
                    && ReadString(ReadDictionaryList(typedTickResponse, "agents").Single(), "assignmentId", "") == occurrence,
                    "An unversioned client gets an explicit pause and no active rows or effects, while the exact typed tick still admits the legacy assignment.",
                    typedTickResponse);

                var arrestEvidence = new Dictionary<string, object>
                {
                    ["schema"] = "reign-arrest-evidence-request-v2", ["campaignId"] = activationCampaign,
                    ["timelineId"] = activationTimeline, ["accusedHeroId"] = activationAgent,
                    ["chargeCategory"] = "espionage"
                };
                var hiddenArrestResult = WorldSimulationService.ArrestEvidenceEvaluateApi(arrestEvidence);
                var receipt = SpymasterAgentExposeApi(exposure);
                var retry = new Dictionary<string, object>(exposure) { ["worldDay"] = 15d, ["reason"] = "fixture_retry" };
                var replay = SpymasterAgentExposeApi(retry);
                var nextTick = tickAt(16d, -30d, 100d, 100d);
                var replayAfterExposure = SpymasterAgentsTickForClientApi(replayPayload);
                add("agent_disruption_replay_does_not_reactivate_exposed_assignment",
                    ReadBool(replayAfterExposure, "ok", false)
                    && ReadDictionaryList(replayAfterExposure, "actions").Count == 0,
                    "A retained receipt for a now-exposed assignment never reattaches its old disruption.", null);
                var terminal = ReadDictionaryList(nextTick, "exposedAgents").SingleOrDefault();
                add("agent_exposure_receipt_is_terminal_and_replayable",
                    ReadBool(receipt, "ok", false) && Json.Serialize(receipt) == Json.Serialize(replay)
                    && ReadString(receipt, "campaignId", "") == activationCampaign
                    && ReadString(receipt, "timelineId", "") == activationTimeline
                    && ReadString(receipt, "restoreGeneration", "") == generation
                    && ReadString(receipt, "assignmentId", "") == occurrence
                    && ReadInt(receipt, "exposureRevision", -1) == 1
                    && ReadDictionaryList(nextTick, "agents").Count == 0
                    && ReadDictionaryList(nextTick, "actions").Count == 0
                    && ReadString(terminal, "assignmentId", "") == occurrence
                    && ReadInt(terminal, "exposureRevision", -1) == 1
                    && ReadDouble(terminal, "exposedDay", -1d) == 14d,
                    "A real legacy SQL assignment gives the same exact exposure receipt on retry, stays exposed on the next hostile tick and retains its original exposure day.",
                    nextTick);

                var typedArrestResult = WorldSimulationService.ArrestEvidenceEvaluateApi(arrestEvidence);
                var evidence = ReadDictionaryList(typedArrestResult, "evidence").SingleOrDefault();
                var legacyRequest = new Dictionary<string, object>(arrestEvidence);
                legacyRequest.Remove("schema");
                var legacyArrestResult = WorldSimulationService.ArrestEvidenceEvaluateApi(legacyRequest);
                add("arrest_evidence_v2_requires_disclosed_exact_agent",
                    ReadBool(hiddenArrestResult, "ok", false) && !ReadBool(hiddenArrestResult, "causeEstablished", true)
                    && ReadDictionaryList(hiddenArrestResult, "evidence").Count == 0
                    && ReadBool(typedArrestResult, "causeEstablished", false)
                    && ReadString(typedArrestResult, "schema", "") == "reign-arrest-evidence-response-v2"
                    && ReadString(typedArrestResult, "campaignId", "") == activationCampaign
                    && ReadString(typedArrestResult, "timelineId", "") == activationTimeline
                    && ReadString(typedArrestResult, "chargeCategory", "") == "espionage"
                    && ReadString(typedArrestResult, "accusedHeroId", "") == activationAgent
                    && ReadString(evidence, "kind", "") == "foreign_agent_exposure"
                    && ReadString(evidence, "subjectRole", "") == "agent"
                    && ReadString(evidence, "subjectHeroId", "") == activationAgent
                    && ReadString(evidence, "evidenceId", "").StartsWith("foreign_agent:" + activationAgent + ":", StringComparison.Ordinal)
                    && ReadBool(legacyArrestResult, "causeEstablished", false)
                    && !legacyArrestResult.ContainsKey("schema") && !legacyArrestResult.ContainsKey("evidence")
                    && ReadStringList(legacyArrestResult, "evidenceIds").SequenceEqual(ReadStringList(typedArrestResult, "evidenceIds")),
                    "The same scoped SQL assignment supplies typed cause only after exposure and preserves legacy caller fields.", null);
                foreach (var change in new[] { (Field: "timelineId", Value: activationTimeline + "_other"),
                    (Field: "accusedHeroId", Value: activationAgent + "_other"), (Field: "chargeCategory", Value: "murder") })
                {
                    var result = WorldSimulationService.ArrestEvidenceEvaluateApi(
                        new Dictionary<string, object>(arrestEvidence) { [change.Field] = change.Value });
                    add("arrest_evidence_v2_rejects_" + change.Field,
                        ReadBool(result, "ok", false) && !ReadBool(result, "causeEstablished", true)
                        && ReadDictionaryList(result, "evidence").Count == 0
                        && ReadString(result, change.Field, "") == change.Value,
                        "Another timeline, actor or charge cannot consume the exposed agent record.", null);
                }
                var malformedArrestRequest = WorldSimulationService.ArrestEvidenceEvaluateApi(
                    new Dictionary<string, object>(arrestEvidence) { ["accusedHeroId"] = activationAgent + "\n" });
                add("arrest_evidence_v2_rejects_nonexact_identity",
                    !ReadBool(malformedArrestRequest, "ok", true),
                    "Versioned scope rejects whitespace variants instead of silently normalizing them.", null);
            }
            finally
            {
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(activationCampaign));
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
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(fallbackCampaign));
            }

            add("social_rumor_outcomes", ReignSpymasterCore.SocialMitigationOutcome("mitigate_own_rumor", 0.819999d) == "disproven"
                    && ReignSpymasterCore.SocialMitigationOutcome("mitigate_own_rumor", 0.82d) == "mitigated",
                "Rumor mitigation has an 82% removal boundary and otherwise large mitigation.", null);
            add("social_reputation_outcomes", ReignSpymasterCore.SocialMitigationOutcome("mitigate_own_reputation", 0.149999d) == "removed"
                    && ReignSpymasterCore.SocialMitigationOutcome("mitigate_own_reputation", 0.15d) == "greatly_mitigated"
                    && Math.Abs(ReignSpymasterCore.SocialMitigationFactor - 0.35d) < 0.0001d,
                "Reputation mitigation has a 15% total-removal boundary and otherwise leaves 35% of the prior value.", null);
            RunSpymasterMitigationSelfTests(add);

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

        private static void RunSpymasterMitigationSelfTests(Action<string, bool, string, object> add)
        {
            string campaign = "_spymaster_mitigation_" + Guid.NewGuid().ToString("N");
            const string timeline = "main_fixture";
            const string type = "mitigate_own_rumor";
            const double day = 30d;
            string PartialId(string prefix, double threshold) => Enumerable.Range(0, 512)
                .Select(i => prefix + i)
                .First(id => RelationshipsService.StableUnit(string.Join("|", campaign, timeline, id, "social_apply")) >= threshold);
            Dictionary<string, object> Payload(string id, string tag = "claim", string subject = "hero",
                string branch = timeline, string operation = type) => new Dictionary<string, object>
            {
                ["campaignId"] = campaign, ["timelineId"] = branch, ["sessionId"] = "capital_" + branch,
                ["mission"] = new Dictionary<string, object>
                {
                    ["MissionId"] = id, ["MissionType"] = operation, ["TargetStringId"] = subject,
                    ["SocialItemId"] = tag, ["StartedDay"] = 24d, ["DueDay"] = day, ["ResolvedDay"] = day
                }
            };
            Dictionary<string, object> Apply(Dictionary<string, object> request)
                => CourtService.CapitalCourtApi(request, SpymasterSocialApplyApi);
            Dictionary<string, object> Status(Dictionary<string, object> request)
                => CourtService.CapitalCourtApi(
                    new Dictionary<string, object>(request) { ["readMitigationReceipt"] = true }, SpymasterSocialStatusApi);
            try
            {
                using var fixture = ReignDatabase.OpenCampaignConnection(campaign);
                ReputationService.EnsureSocialReputationSchema(fixture);
                void OpenTimeline(string branch) => WorldSimulationService.WorldHistoryOpenTimelineApi(
                    new Dictionary<string, object> { ["campaignId"] = campaign, ["timelineId"] = branch });
                void OpenCourt(string branch)
                {
                    var opened = CourtService.CourtSessionOpenApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaign, ["timelineId"] = branch, ["commandId"] = "open_" + branch,
                        ["expectedRevision"] = 0L, ["sessionId"] = "capital_" + branch, ["authority"] = "royal",
                        ["playerIsKingdomRuler"] = true, ["playerHeroStringId"] = "player",
                        ["hostSettlementStringId"] = "town_capital", ["capitalSettlementStringId"] = "town_capital",
                        ["scope"] = "capital", ["worldDay"] = day
                    });
                    if (!ReadBool(opened, "ok", false)) throw new InvalidOperationException("The mitigation fixture could not open capital court.");
                }
                OpenTimeline(timeline);
                OpenCourt(timeline);
                OpenCourt("other_fixture");
                void Seed(string occurrence, string branch, double started, double expires,
                    string tag = "claim", string subject = "hero", int value = -20, string owner = null)
                {
                    var fields = new Dictionary<string, object>
                    {
                        ["occurrence"] = occurrence, ["campaign"] = owner ?? campaign, ["timeline"] = branch,
                        ["thread"] = "fixture", ["day"] = started, ["expires"] = expires, ["participants"] = "[]",
                        ["snapshot"] = "{}", ["ts"] = 0L, ["subject"] = subject, ["tag"] = tag,
                        ["description"] = "Fixture claim", ["value"] = value
                    };
                    SpymasterStore.UpsertRumorOccurrences(fixture, fields);
                    SpymasterStore.UpsertRumorSubjectTags(fixture, fields);
                }
                int Value(string id) => SpymasterSelfTestStore.ReadMitigationFixtureValue(fixture, id);
                Seed("older_live", timeline, 19d, 40d);
                Seed("latest_live", timeline, 28d, 40d, value: 0);
                Seed("expired_at_resolution", timeline, 23d, day);
                Seed("foreign_timeline", "other_fixture", 24d, 40d);
                Seed("foreign_campaign", timeline, 24d, 40d, owner: "other_campaign_fixture");
                Seed("later_occurrence", timeline, day + 1d, 60d);
                Seed("different_subject", timeline, 24d, 40d, subject: "other_hero");
                Seed("different_tag", timeline, 24d, 40d, tag: "other_claim");

                string id = PartialId("scoped_partial_", 0.82d);
                var request = Payload(id);
                request["restoreGeneration"] = ReadString(DialogueService.ReadMemoryPrecisionState(fixture), "restore_generation", "");
                add("mitigation_missing_id_or_receipt",
                    ReadString(Apply(Payload("")), "error", "") == "invalid_mitigation_terms"
                    && ReadString(Status(request), "error", "") == "mitigation_receipt_missing" && Value("older_live") == -20,
                    "A missing stable ID is rejected, and a status lookup alone leaves rumor rows untouched.", null);
                var result = Apply(request);
                string original = Json.Serialize(result);
                Seed("later_backfilled", timeline, 29d, 50d);
                var replay = Apply(request);
                var recovered = Status(request);
                add("mitigation_frozen_tag_scope_and_exact_replay",
                    ReadString(result, "outcome", "") == "mitigated" && ReadInt(result, "matchedCount", -1) == 2
                    && ReadInt(result, "changedCount", -1) == 1
                    && Json.Serialize(result["matchedOccurrenceIds"]) == "[\"latest_live\",\"older_live\"]"
                    && Json.Serialize(result["changedOccurrenceIds"]) == "[\"older_live\"]"
                    && Json.Serialize(replay) == original && Json.Serialize(recovered) == original
                    && Value("older_live") == -7 && Value("latest_live") == 0 && Value("later_backfilled") == -20,
                    "Partial mitigation changes a live score once and returns the same frozen scope after a lost response.", result);
                add("mitigation_delayed_and_cross_timeline_guards",
                    Value("expired_at_resolution") == -20 && Value("foreign_timeline") == -20
                    && Value("foreign_campaign") == -20 && Value("later_occurrence") == -20
                    && Value("different_subject") == -20 && Value("different_tag") == -20
                    && ReadString(Apply(Payload(id, branch: "other_fixture")), "error", "") == "mitigation_terms_conflict"
                    && ReadString(Status(Payload(id, branch: "other_fixture")), "error", "") == "mitigation_terms_conflict"
                    && ReadString(Apply(Payload(id, tag: "other_claim")), "error", "") == "mitigation_terms_conflict",
                    "The first apply ignores expired, future, unrelated, and foreign rows. A conflicting replay cannot mutate or recover someone else's scope.", null);

                var missingRequest = Payload("no_match", "missing_claim");
                var missing = Apply(missingRequest);
                Seed("missing_claim_added_later", timeline, 26d, 40d, tag: "missing_claim");
                Seed("already_zero", timeline, 26d, 40d, tag: "zero_claim", value: 0);
                var zero = Apply(Payload(PartialId("zero_", 0.82d), "zero_claim"));
                add("mitigation_missing_and_valid_zero_distinguished",
                    ReadString(missing, "disposition", "") == "no_match"
                    && ReadString(Apply(missingRequest), "disposition", "") == "no_match"
                    && Value("missing_claim_added_later") == -20
                    && ReadString(zero, "disposition", "") == "no_change" && ReadInt(zero, "matchedCount", -1) == 1,
                    "A missing record and a real zero-value record get different durable dispositions.", null);

                Seed("rollback_rumor", timeline, 26d, 40d, subject: "blocked_fixture");
                var failure = Payload(PartialId("rollback_", 0.82d), subject: "blocked_fixture");
                SpymasterSelfTestStore.SetMitigationProjectionFailure(fixture, true);
                bool rejected = false;
                try { Apply(failure); }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.CheckViolation) { rejected = true; }
                finally { SpymasterSelfTestStore.SetMitigationProjectionFailure(fixture, false); }
                bool rolledBack = Value("rollback_rumor") == -20
                    && ReadString(Status(failure), "error", "") == "mitigation_receipt_missing";
                var repaired = Apply(failure);
                add("mitigation_projection_failure_rolls_back_effect_and_receipt",
                    rejected && rolledBack && ReadString(repaired, "disposition", "") == "applied" && Value("rollback_rumor") == -7,
                    "A failed standing reconciliation leaves no committed rumor change or receipt. A fresh identical apply then changes the value once.", repaired);

                SpymasterStore.UpsertCharacterReputations(fixture, new Dictionary<string, object>
                {
                    ["campaign"] = campaign, ["timeline"] = timeline, ["subject"] = "hero", ["tag"] = "reputation",
                    ["archetype"] = "fixture", ["description"] = "Fixture reputation", ["value"] = -20,
                    ["day"] = 25d, ["snapshot"] = "{}", ["ts"] = 0L
                });
                var reputation = Payload(PartialId("reputation_", 0.15d), "reputation", operation: "mitigate_target_reputation");
                var reputationTasks = new[]
                {
                    System.Threading.Tasks.Task.Run(() => Apply(reputation)),
                    System.Threading.Tasks.Task.Run(() => Apply(reputation))
                };
                System.Threading.Tasks.Task.WaitAll(reputationTasks);
                var projected = ReputationService.ReadPublicStandingTrait(fixture, campaign, timeline, "hero", day);
                var projectedReputation = ReadDictionaryList(projected, "sources")
                    .FirstOrDefault(source => ReadString(source, "tagId", "") == "reputation");
                add("mitigation_concurrent_reputation_applies_once",
                    ReadString(reputationTasks[0].Result, "outcome", "") == "greatly_mitigated"
                    && ReadInt(reputationTasks[0].Result, "changedCount", -1) == 1
                    && Json.Serialize(reputationTasks[0].Result) == Json.Serialize(reputationTasks[1].Result)
                    && ReadInt(projectedReputation, "rawValue", 0) == -7,
                    "Concurrent partial reputation mitigation returns one original receipt and publishes one standing change.", projectedReputation);

                var staleGeneration = Payload("first_apply_from_replaced_generation");
                staleGeneration["restoreGeneration"] = ReadString(DialogueService.ReadMemoryPrecisionState(fixture), "restore_generation", "");
                DialogueService.AdvanceMemoryRestoreGeneration(campaign);
                var generationRejected = Apply(staleGeneration);
                add("mitigation_first_apply_fenced_after_same_timeline_restore",
                    ReadString(generationRejected, "error", "") == "mitigation_generation_changed"
                    && Value("older_live") == -7 && Json.Serialize(Status(request)) == original,
                    "A new-client request from a replaced generation cannot mutate the same timeline. Receipt lookup still returns the original result.", generationRejected);

                OpenTimeline("other_fixture");
                var stale = Apply(Payload("first_apply_after_switch"));
                add("mitigation_first_apply_fenced_after_timeline_switch",
                    ReadString(stale, "error", "") == "mitigation_inactive_timeline"
                    && Value("older_live") == -7
                    && Json.Serialize(Status(request)) == original,
                    "A request first reaching the old branch after a switch cannot mutate it. A committed receipt remains readable.", stale);
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaign));
            }
        }
    }
}
