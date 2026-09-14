using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunFinalConversationGauntletLifecycleSelfTests()
        {
            List<Dictionary<string, object>> checks =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, message) =>
                checks.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["passed"] = passed, ["message"] = message
                });
            Type policy = Type.GetType(
                "ReignBetaServer.FinalConversationGauntletLifecyclePolicy");
            add("lifecycle_policy_exists", policy != null,
                "The gauntlet exposes an explicit save and pause policy.");
            if (policy == null) return checks;
            string[] saves = (string[])policy.GetMethod(
                "RotatingSaveNames").Invoke(null, null);
            add("only_two_recognized_derivative_saves",
                saves.Length == 2
                    && saves[0] == "ConvTest_Gauntlet_A"
                    && saves[1] == "ConvTest_Gauntlet_B",
                "Only two ConvTest derivatives are eligible for rotation.");
            var protectedMethod = policy.GetMethod("IsProtectedSave");
            add("protected_saves_never_mutate",
                Convert.ToBoolean(protectedMethod.Invoke(
                    null, new object[] { "BaseOne" }))
                    && Convert.ToBoolean(protectedMethod.Invoke(
                        null, new object[] { "BaseTwo" }))
                    && Convert.ToBoolean(protectedMethod.Invoke(
                        null, new object[] { "BaseThree" }))
                    && Convert.ToBoolean(protectedMethod.Invoke(
                    null, new object[] { "ConvTest" })),
                "The baseline and three unrelated saves are protected.");

            Func<string, string, string,
                Dictionary<string, object>> censusHero =
                (heroId, clanName, clanId) =>
                    new Dictionary<string, object>
                    {
                        ["heroId"] = heroId,
                        ["characterObjectId"] = heroId + "_character",
                        ["name"] = heroId,
                        ["clanId"] = clanId,
                        ["clanName"] = clanName,
                        ["clanLeaderId"] = "hero_a",
                        ["isClanLeader"] = heroId == "hero_a",
                        ["kingdomId"] = "kingdom_a",
                        ["cultureId"] = "empire",
                        ["clanTier"] = 3,
                        ["isLord"] = true,
                        ["isRuler"] = false,
                        ["isNotable"] = false,
                        ["isWanderer"] = false,
                        ["isAlive"] = true,
                        ["isAdult"] = true,
                        ["occupation"] = "Lord",
                        ["governorOfSettlementId"] = ""
                    };
            Dictionary<string, object> heroA =
                censusHero("hero_a", "Correct Clan", "clan_a");
            Dictionary<string, object> heroB =
                censusHero("hero_b", "Correct Clan", "clan_a");
            Dictionary<string, object> orderedFingerprint =
                FinalGauntletStateFingerprintApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = "campaign-a",
                        ["targets"] = new List<object>
                        {
                            heroA, heroB
                        }
                    });
            Dictionary<string, object> reversedFingerprint =
                FinalGauntletStateFingerprintApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = "campaign-a",
                        ["targets"] = new List<object>
                        {
                            heroB, heroA
                        }
                    });
            Dictionary<string, object> changedClanFingerprint =
                FinalGauntletStateFingerprintApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = "campaign-a",
                        ["targets"] = new List<object>
                        {
                            censusHero(
                                "hero_a",
                                "Corrected Clan Name",
                                "clan_a"),
                            censusHero(
                                "hero_b",
                                "Corrected Clan Name",
                                "clan_a")
                        }
                    });
            Dictionary<string, object> invalidClanCensus =
                FinalGauntletStateFingerprintApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = "campaign-a",
                        ["targets"] = new List<object>
                        {
                            censusHero("hero_a", "", "")
                        }
                    });
            add(
                "clan_census_fingerprint_is_order_stable",
                ReadBool(orderedFingerprint, "ok", false)
                    && ReadString(
                        orderedFingerprint,
                        "fingerprint",
                        "") == ReadString(
                        reversedFingerprint,
                        "fingerprint",
                        ""),
                "Target enumeration order cannot change the authoritative baseline fingerprint.");
            add(
                "clan_registration_changes_invalidate_fingerprint",
                ReadString(
                    orderedFingerprint,
                    "fingerprint",
                    "") != ReadString(
                    changedClanFingerprint,
                    "fingerprint",
                    ""),
                "Changing an authoritative clan name invalidates prior readiness evidence.");
            add(
                "invalid_noble_clan_registration_fails_closed",
                !ReadBool(invalidClanCensus, "ok", true),
                "A noble missing authoritative clan registration cannot establish a gauntlet baseline.");

            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                ResetFinalGauntletSelfTestRuns(connection, "pause-run");
                FinalGauntletCaseDescriptor descriptor =
                    FinalConversationGauntletReadinessSelection
                        .SelectMissingReadinessCases(
                            FinalConversationGauntletCatalog
                                .BuildExecutableCatalog(new[] { "a" }),
                            new Dictionary<string, object>(),
                            "b", 40)["descriptors"]
                        is List<FinalGauntletCaseDescriptor> selected
                            ? selected[0] : null;
                Dictionary<string, object> started =
                    FinalConversationGauntletScheduler.StartStageA(
                        connection, "pause-run",
                        "lifecycle-pause-campaign", "build",
                        new List<FinalGauntletCaseDescriptor> { descriptor },
                        "manifest", "state-a", "ConvTest");
                Dictionary<string, object> paused =
                    FinalConversationGauntletScheduler.RequestPause(
                        connection, "pause-run");
                Dictionary<string, object> lease =
                    FinalConversationGauntletScheduler.LeaseNext(
                        connection, "pause-run", "controller",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 60);
                add("pause_stops_new_leases",
                    ReadBool(started, "ok", false)
                        && ReadBool(paused, "ok", false)
                        && !ReadBool(lease, "ok", true)
                        && ReadString(lease, "error", "")
                            .IndexOf("paused",
                                StringComparison.OrdinalIgnoreCase) >= 0,
                    "A requested pause prevents any new case lease.");
            }
            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                ResetFinalGauntletSelfTestRuns(
                    connection, "pause-active-run");
                FinalGauntletCaseDescriptor descriptor =
                    FinalGauntletSchedulerDescriptor(
                        "PAUSE-ACTIVE", true);
                FinalConversationGauntletScheduler.StartStageA(
                    connection, "pause-active-run",
                    "lifecycle-active-pause-campaign",
                    "build",
                    new List<FinalGauntletCaseDescriptor> { descriptor },
                    "manifest", "state-a", "ConvTest");
                Dictionary<string, object> activeLease =
                    FinalConversationGauntletScheduler.LeaseNext(
                        connection, "pause-active-run", "controller-a",
                        1000L, 60L);
                Dictionary<string, object> pauseActive =
                    FinalConversationGauntletScheduler.RequestPause(
                        connection, "pause-active-run");
                Dictionary<string, object> pausedSchedule =
                    FinalConversationGauntletScheduler.LoadSchedule(
                        connection, "pause-active-run");
                Dictionary<string, object> staleResume =
                    FinalConversationGauntletScheduler.Resume(
                        connection,
                        "pause-active-run",
                        "state-from-another-campaign");
                FinalConversationGauntletScheduler.Resume(
                    connection, "pause-active-run", "state-a");
                Dictionary<string, object> resumedLease =
                    FinalConversationGauntletScheduler.LeaseNext(
                        connection, "pause-active-run", "controller-b",
                        1061L, 60L);
                add(
                    "active_case_pause_is_resumable",
                    ReadBool(activeLease, "ok", false)
                        && ReadString(pausedSchedule, "state", "")
                            == "interrupted"
                        && ReadString(pauseActive, "state", "")
                            == "interrupted"
                        && !ReadBool(staleResume, "ok", true)
                        && ReadString(staleResume, "error", "")
                            .IndexOf(
                                "differs",
                                StringComparison.OrdinalIgnoreCase) >= 0
                        && ReadBool(resumedLease, "ok", false)
                        && ReadBool(resumedLease, "recovered", false)
                        && ReadString(activeLease, "caseInstanceId", "")
                            == ReadString(
                                resumedLease, "caseInstanceId", ""),
                    "Pausing with an active or retry-pending case interrupts immediately and resumes the exact leased case.");
                add(
                    "resume_rejects_stale_clan_census",
                    !ReadBool(staleResume, "ok", true)
                        && ReadString(pausedSchedule, "state", "")
                            == "interrupted",
                    "A paused gauntlet cannot resume against a different authoritative campaign/clan census.");
            }
            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                ResetFinalGauntletSelfTestRuns(
                    connection, "pause-heartbeat-run");
                FinalConversationGauntletScheduler.StartStageA(
                    connection, "pause-heartbeat-run",
                    "lifecycle-heartbeat-pause-campaign",
                    "build",
                    new List<FinalGauntletCaseDescriptor>
                    {
                        FinalGauntletSchedulerDescriptor(
                            "LIVE-SINGLE-031::stage-b", true),
                        FinalGauntletSchedulerDescriptor(
                            "PENDING::stage-b", false)
                    },
                    "manifest", "state-a", "ConvTest");
                Dictionary<string, object> heartbeatLease =
                    FinalConversationGauntletScheduler.LeaseNext(
                        connection, "pause-heartbeat-run", "controller",
                        2000L, 60L);
                FinalConversationGauntletScheduler.RecordResult(
                    connection,
                    "pause-heartbeat-run",
                    ReadString(heartbeatLease, "caseInstanceId", ""),
                    "corr-heartbeat",
                    false,
                    false,
                    "The campaign heartbeat remained offline across 12 consecutive qualification checks.");
                using (ReignDbCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"INSERT INTO final_gauntlet_evidence(
run_id,case_instance_id,evidence_key,schema_version,payload_json,created_ts)
VALUES('pause-heartbeat-run','LIVE-SINGLE-031::stage-b',
'production_result',4,'{""liveRun"":{}}',1);";
                    command.ExecuteNonQuery();
                }
                using (ReignDbCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE final_gauntlet_runs
SET stage='ExhaustiveReview',state='completed_with_failures'
WHERE run_id='pause-heartbeat-run';
UPDATE final_gauntlet_schedule
SET state='completed_with_failures',pause_requested=0
WHERE run_id='pause-heartbeat-run';";
                    command.ExecuteNonQuery();
                }
                Dictionary<string, object> heartbeatResume =
                    FinalConversationGauntletScheduler.Resume(
                        connection,
                        "pause-heartbeat-run",
                        "state-a",
                        new[]
                        {
                            "individual_chat", "party_chat",
                            "social_event", "wilderness_event"
                        });
                Dictionary<string, object> heartbeatRun =
                    FinalGauntletStore.LoadRun(
                        connection, "pause-heartbeat-run");
                Dictionary<string, object> heartbeatCase =
                    ReadDictionaryList(heartbeatRun, "cases")
                        .Find(row => ReadString(
                            row, "caseInstanceId", "")
                            == "LIVE-SINGLE-031::stage-b");
                add(
                    "pause_heartbeat_without_production_execution_requeues_once",
                    ReadBool(heartbeatResume, "ok", false)
                        && ReadInt(
                            heartbeatResume,
                            "infrastructureCasesRequeued",
                            0) == 1
                        && ReadString(
                            heartbeatResume,
                            "state", "") == "running_stage_b"
                        && ReadString(
                            FinalConversationGauntletScheduler.LoadSchedule(
                                connection, "pause-heartbeat-run"),
                            "state", "") == "running_stage_b"
                        && ReadString(
                            heartbeatCase, "state", "") == "Queued",
                    "A completed-with-failures run returns to running Stage B when its heartbeat-only production-result envelope proves that no live run started, without replaying any semantic execution.");
                Dictionary<string, object> cancelledLease =
                    FinalConversationGauntletScheduler.LeaseNext(
                        connection, "pause-heartbeat-run", "controller",
                        3000L, 60L);
                FinalConversationGauntletScheduler.RecordResult(
                    connection,
                    "pause-heartbeat-run",
                    ReadString(cancelledLease, "caseInstanceId", ""),
                    "corr-cancelled-transport",
                    false,
                    false,
                    "A task was canceled.");
                using (ReignDbCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE final_gauntlet_runs
SET stage='ExhaustiveReview',state='completed_with_failures'
WHERE run_id='pause-heartbeat-run';
UPDATE final_gauntlet_schedule
SET state='completed_with_failures',pause_requested=0
WHERE run_id='pause-heartbeat-run';";
                    command.ExecuteNonQuery();
                }
                Dictionary<string, object> cancelledResume =
                    FinalConversationGauntletScheduler.Resume(
                        connection,
                        "pause-heartbeat-run",
                        "state-a",
                        new[]
                        {
                            "individual_chat", "party_chat",
                            "social_event", "wilderness_event"
                        });
                Dictionary<string, object> cancelledRun =
                    FinalGauntletStore.LoadRun(
                        connection, "pause-heartbeat-run");
                Dictionary<string, object> cancelledCase =
                    ReadDictionaryList(cancelledRun, "cases")
                        .Find(row => ReadString(
                            row, "caseInstanceId", "")
                            == "LIVE-SINGLE-031::stage-b");
                add(
                    "ambiguous_target_search_without_live_run_requeues_second_setup_attempt",
                    ReadBool(cancelledResume, "ok", false)
                        && ReadInt(
                            cancelledResume,
                            "infrastructureCasesRequeued",
                            0) == 1
                        && ReadString(
                            cancelledCase, "state", "") == "Queued",
                    "A transport cancellation may receive another bounded setup attempt when the persisted production envelope explicitly proves no live run began; the semantic case still executes at most once.");
                Dictionary<string, object> thirdSetupLease =
                    FinalConversationGauntletScheduler.LeaseNext(
                        connection, "pause-heartbeat-run", "controller",
                        4000L, 60L);
                FinalConversationGauntletScheduler.RecordResult(
                    connection,
                    "pause-heartbeat-run",
                    ReadString(thirdSetupLease, "caseInstanceId", ""),
                    "corr-third-setup-heartbeat",
                    false,
                    false,
                    "The campaign heartbeat remained offline across 150 consecutive qualification checks.");
                using (ReignDbCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE final_gauntlet_runs
SET stage='ExhaustiveReview',state='completed_with_failures'
WHERE run_id='pause-heartbeat-run';
UPDATE final_gauntlet_schedule
SET state='completed_with_failures',pause_requested=0
WHERE run_id='pause-heartbeat-run';";
                    command.ExecuteNonQuery();
                }
                Dictionary<string, object> fourthSetupResume =
                    FinalConversationGauntletScheduler.Resume(
                        connection,
                        "pause-heartbeat-run",
                        "state-a",
                        new[]
                        {
                            "individual_chat", "party_chat",
                            "social_event", "wilderness_event"
                        });
                Dictionary<string, object> fourthSetupRun =
                    FinalGauntletStore.LoadRun(
                        connection, "pause-heartbeat-run");
                Dictionary<string, object> fourthSetupCase =
                    ReadDictionaryList(fourthSetupRun, "cases")
                        .Find(row => ReadString(
                            row, "caseInstanceId", "")
                            == "LIVE-SINGLE-031::stage-b");
                add(
                    "heartbeat_gap_without_live_run_requeues_fourth_setup_attempt",
                    ReadBool(fourthSetupResume, "ok", false)
                        && ReadInt(
                            fourthSetupResume,
                            "infrastructureCasesRequeued",
                            0) == 1
                        && ReadString(
                            fourthSetupCase, "state", "") == "Queued",
                    "A third setup-only heartbeat failure can receive a fourth bounded attempt only when the durable evidence still proves that no production live run began.");
                Dictionary<string, object> semanticLease =
                    FinalConversationGauntletScheduler.LeaseNext(
                        connection, "pause-heartbeat-run", "controller",
                        5000L, 60L);
                Dictionary<string, object> semanticResult =
                    FinalConversationGauntletScheduler.RecordResult(
                        connection,
                        "pause-heartbeat-run",
                        ReadString(semanticLease, "caseInstanceId", ""),
                        "corr-semantic-terminal-after-setup",
                        false,
                        false,
                        "The one semantic execution completed with a recorded assertion failure.");
                Dictionary<string, object> semanticRun =
                    FinalGauntletStore.LoadRun(
                        connection, "pause-heartbeat-run");
                Dictionary<string, object> semanticCase =
                    ReadDictionaryList(semanticRun, "cases")
                        .Find(row => ReadString(
                            row, "caseInstanceId", "")
                            == "LIVE-SINGLE-031::stage-b");
                add(
                    "setup_attempts_do_not_exhaust_stage_b_terminal_result",
                    ReadBool(semanticLease, "ok", false)
                        && ReadBool(semanticResult, "ok", false)
                        && ReadInt(semanticResult, "attempt", 0) == 4
                        && ReadString(
                            semanticCase, "state", "") == "Failed",
                    "Bounded setup-only attempts remain auditable but cannot prevent the case's first and only semantic execution from being committed as its terminal result.");
            }
            string postgresCampaign = "verification-gauntlet-control-"
                + Guid.NewGuid().ToString("N").Substring(0, 10);
            string postgresRun = "pg-run-"
                + Guid.NewGuid().ToString("N");
            string postgresSnapshot = "pg-save-"
                + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                using (ReignDbConnection campaignConnection =
                    ReignPostgreSqlStorage.OpenCampaignConnection(
                        postgresCampaign))
                {
                    ExecuteSql(
                        campaignConnection,
                        @"CREATE TABLE IF NOT EXISTS gauntlet_rollback_probe(
id TEXT PRIMARY KEY,value TEXT NOT NULL);
INSERT INTO gauntlet_rollback_probe(id,value)
VALUES('state','before')
ON CONFLICT(id) DO UPDATE SET value=excluded.value;");
                }
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(
                    postgresCampaign, postgresSnapshot);

                Dictionary<string, object> started;
                Dictionary<string, object> leased;
                using (ReignDbConnection control =
                    FinalGauntletStore.OpenControlConnection())
                {
                    string schema = ReadString(
                        QuerySql(
                            control,
                            "SELECT current_schema() AS schema;")
                            .FirstOrDefault(),
                        "schema", "");
                    FinalGauntletCaseDescriptor descriptor =
                        FinalGauntletSchedulerDescriptor(
                            "PG-CONTROL", true);
                    started =
                        FinalConversationGauntletScheduler.StartStageA(
                            control,
                            postgresRun,
                            postgresCampaign,
                            "build",
                            new List<FinalGauntletCaseDescriptor>
                            {
                                descriptor
                            },
                            "manifest",
                            "state-pg",
                            "ConvTest");
                    leased =
                        FinalConversationGauntletScheduler.LeaseNext(
                            control,
                            postgresRun,
                            "controller-a",
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            60);
                    string correlation = "pg-correlation-"
                        + Guid.NewGuid().ToString("N");
                    FinalConversationGauntletProviderBudget
                        .RegisterCorrelation(
                            control,
                            postgresRun,
                            "PG-CONTROL",
                            correlation);
                    FinalConversationGauntletProviderBudget
                        .ClearRegisteredScopesForTests();
                    Dictionary<string, object> reservation =
                        FinalConversationGauntletProviderBudget
                            .TryReserveRegisteredPhysicalDispatch(
                                correlation + "-dialogue",
                                "dialogue",
                                "test-model",
                                1);
                    FinalConversationGauntletProviderBudget
                        .CompleteRegisteredPhysicalDispatch(
                            reservation, true, "");
                    add(
                        "postgresql_gauntlet_uses_metadata_schema",
                        schema == "reign_meta"
                            && ReadBool(started, "ok", false)
                            && ReadBool(leased, "ok", false),
                        "Production gauntlet control rows live in reign_meta rather than a Save Sync campaign schema.");
                    add(
                        "postgresql_provider_scope_survives_process_cache_loss",
                        ReadBool(reservation, "scoped", false)
                            && ReadBool(reservation, "allowed", false)
                            && ReadInt(reservation, "ordinal", 0) == 1,
                        "A cleared in-process correlation cache recovers its provider budget from PostgreSQL metadata.");
                }

                using (ReignDbConnection campaignConnection =
                    ReignPostgreSqlStorage.OpenCampaignConnection(
                        postgresCampaign))
                    ExecuteSql(
                        campaignConnection,
                        @"UPDATE gauntlet_rollback_probe
SET value='after' WHERE id='state';");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(
                    postgresCampaign, postgresSnapshot);
                using (ReignDbConnection control =
                    FinalGauntletStore.OpenControlConnection())
                using (ReignDbConnection campaignConnection =
                    ReignPostgreSqlStorage.OpenCampaignConnection(
                        postgresCampaign))
                {
                    long runRows = ReadLong(
                        QuerySql(
                            control,
                            @"SELECT COUNT(*) AS count
FROM final_gauntlet_runs WHERE run_id=$run;",
                            new Dictionary<string, object>
                            {
                                ["run"] = postgresRun
                            }).FirstOrDefault(),
                        "count", 0);
                    long providerRows = ReadLong(
                        QuerySql(
                            control,
                            @"SELECT COUNT(*) AS count
FROM final_gauntlet_provider_calls WHERE run_id=$run;",
                            new Dictionary<string, object>
                            {
                                ["run"] = postgresRun
                            }).FirstOrDefault(),
                        "count", 0);
                    string restoredValue = ReadString(
                        QuerySql(
                            campaignConnection,
                            @"SELECT value FROM gauntlet_rollback_probe
WHERE id='state';").FirstOrDefault(),
                        "value", "");
                    long campaignControlTables = ReadLong(
                        QuerySql(
                            campaignConnection,
                            @"SELECT COUNT(*) AS count
FROM information_schema.tables
WHERE table_schema=current_schema()
AND table_name LIKE 'final_gauntlet_%';")
                            .FirstOrDefault(),
                        "count", 0);
                    add(
                        "save_rollback_preserves_gauntlet_control_plane",
                        restoredValue == "before"
                            && runRows == 1
                            && providerRows == 1
                            && campaignControlTables == 0,
                        "Campaign rollback rewinds production state while the run, lease, and physical-call ledger remain forward-only in reign_meta.");
                }
            }
            catch (Exception ex)
            {
                add(
                    "postgresql_gauntlet_control_plane_fixture",
                    false,
                    "PostgreSQL control-plane fixture failed: "
                        + ex.Message);
            }
            finally
            {
                try
                {
                    using (ReignDbConnection control =
                        FinalGauntletStore.OpenControlConnection())
                        ExecuteSql(
                            control,
                            @"DELETE FROM final_gauntlet_runs
WHERE run_id=$run;",
                            new Dictionary<string, object>
                            {
                                ["run"] = postgresRun
                            });
                }
                catch
                {
                }
                try
                {
                    ReignPostgreSqlStorage.DropSnapshot(
                        postgresCampaign, postgresSnapshot);
                }
                catch
                {
                }
                try
                {
                    ReignPostgreSqlStorage.DropCampaign(
                        postgresCampaign);
                }
                catch
                {
                }
            }
            return checks;
        }
    }
}
