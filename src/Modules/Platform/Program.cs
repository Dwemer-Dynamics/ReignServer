using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        internal static readonly object FileLock = new object();
        internal static readonly ReaderWriterLockSlim CampaignDataGate = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

        internal enum CampaignRequestAccess
        {
            None,
            Read,
            Write
        }
        internal static readonly string DataDir = ResolveRuntimeDataDirectory(
            Environment.GetEnvironmentVariable("REIGN_DATA_ROOT"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"));
        internal static readonly string LogsDir = Path.Combine(DataDir, "logs");
        internal static readonly string PromptsDir = Path.Combine(DataDir, "prompts");
        internal static readonly string CharacterTemplatesDir = Path.Combine(DataDir, "character_templates");
        internal static readonly string TestsDir = Path.Combine(DataDir, "tests");
        internal static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");
        private static readonly Lazy<NativeProviderTestGuard> NativeProviderTestGuardInstance =
            new Lazy<NativeProviderTestGuard>(() => new NativeProviderTestGuard(DataDir));
        internal static NativeProviderTestGuard ProviderTestGuard => NativeProviderTestGuardInstance.Value;

        internal static string ResolveRuntimeDataDirectory(
            string configuredRoot,
            string fallbackRoot)
        {
            string candidate = string.IsNullOrWhiteSpace(configuredRoot)
                ? fallbackRoot
                : Environment.ExpandEnvironmentVariables(configuredRoot.Trim());
            if (string.IsNullOrWhiteSpace(candidate))
                throw new InvalidDataException("Reign runtime data root is empty.");
            return Path.GetFullPath(candidate);
        }
        internal const string CharacterSchemaVersion = "reign_character_v2";
        internal const int CharacterConstructionEngineVersion = 5;
        internal const int CharacterConstructionTokenCeiling = 50000;
        internal const int AuditMaxStringChars = 16000;
        internal const int AuditMaxListItems = 120;
        // Prompt evidence contains data -> promptEnvelope ->
        // npcRelationshipPrompt -> targets -> target -> history-id list. Keep
        // enough structural depth to preserve those stable lineage IDs while
        // the independent string, list-item, and file-size caps still bound
        // audit growth.
        internal const int AuditMaxDepth = 12;
        internal static readonly object CampaignSchemaLock = new object();
        internal static readonly HashSet<string> CampaignSchemaReady =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static readonly ConcurrentDictionary<string, byte>
            PostgreSqlComponentSchemaFastPaths =
                new ConcurrentDictionary<string, byte>(
                    StringComparer.OrdinalIgnoreCase);
        internal static volatile bool ShutdownRequested;
        internal static volatile bool DeleteAllCampaignDataPending;
        internal static readonly ManualResetEventSlim DeleteAllCampaignResponseSent = new ManualResetEventSlim(true);
        // Innermost lock (after CampaignDataGate, then CampaignBackupMutationLock). Guards only the
        // pending delete-all owner, its captured settings and the listener's closed flag; it is
        // never held across I/O, a wait, a shutdown request or a deletion phase.
        internal static readonly object DeleteAllScheduleLock = new object();
        internal static bool DeleteAllSchedulingClosed;
        internal static Dictionary<string, object> DeleteAllScheduledSettings;
        internal static readonly AsyncLocal<string> CampaignsRootOverride = new AsyncLocal<string>();
        internal static int InteractiveRequestCount;
        internal static long LastInteractiveRequestCompletedUtcTicks;
        internal static readonly object CharacterEnrichmentLock = new object();
        internal static readonly HashSet<string> PendingCharacterEnrichments =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static TcpListener ActiveListener;
        internal static readonly string[] ActionPromptFileNames =
        {
            "action_declare_war.txt",
            "action_make_peace.txt",
            "action_offer_tribute_peace.txt",
            "action_record_promise.txt",
            "action_demand_reparations_peace.txt",
            "action_demand_settlement_peace.txt",
            "action_demand_surrender_peace.txt",
            "action_sign_trade_agreement.txt",
            "action_sign_non_aggression_pact.txt",
            "action_sign_alliance.txt",
            "action_sign_defensive_pact.txt",
            "action_break_treaty.txt",
            "action_exchange_prisoners.txt",
            "action_ransom_package.txt",
            "action_hostage_guarantee.txt",
            "action_war_indemnity.txt",
            "action_recognize_conquest.txt",
            "action_return_occupied_settlement.txt",
            "action_demilitarized_border.txt",
            "action_caravan_protection_agreement.txt",
            "action_supply_agreement.txt",
            "action_loan_or_subsidy.txt",
            "action_pay_to_stay_neutral.txt",
            "action_pay_to_join_war.txt",
            "action_guarantee_independence.txt",
            "action_protectorate_or_vassalage.txt",
            "action_diplomatic_package.txt",
            "action_trade_package.txt",
            "action_recruit_and_recover.txt",
            "action_form_army.txt",
            "action_attack_settlement.txt",
            "action_capture_settlement_plan.txt",
            "action_start_ruling_clan_rebellion.txt",
            "action_recruit_lord_to_rebellion.txt",
            "action_join_rebellion.txt",
            "action_surrender_rebellion.txt",
            "action_install_ruling_clan.txt",
            "action_marriage_alliance.txt",
            "action_support_claimant.txt",
            "action_encourage_clan_defection.txt",
            "action_exile_clan.txt",
            "action_restore_exiled_clan.txt",
            "action_mediate_clan_dispute.txt",
            "action_follow.txt",
            "action_stop_following.txt",
            "action_go_to_settlement.txt",
            "action_patrol_around_settlement.txt",
            "action_wait_near_settlement.txt",
            "action_raid_village.txt",
            "action_besiege_settlement.txt",
            "action_create_party.txt",
            "action_show_the_way.txt",
            "action_attack_party.txt",
            "action_attack_player_party.txt",
            "action_surrender_to_player.txt",
            "action_leave_player_alone.txt",
            "action_kill_character.txt",
            "action_duel_player.txt",
            "action_give_gold_to_player.txt",
            "action_transfer_gold.txt",
            "action_transfer_item.txt",
            "action_transfer_workshop.txt",
            "action_transfer_prisoner.txt",
            "action_hire_player_as_mercenary.txt",
            "action_dismiss_player_mercenary.txt",
            "action_offer_player_vassalage.txt",
            "action_dismiss_player_vassal.txt",
            "action_join_clan.txt",
            "action_leave_clan.txt",
            "action_join_kingdom.txt",
            "action_leave_kingdom.txt",
            "action_hire_mercenary_clan.txt"
        };
        internal static string[] PromptFileNames
        {
            get { return new[]
            {
            "world_tone.txt",
            "global_response_override.txt",
            "noble_prompt.txt",
            "commoner_prompt.txt",
            "ambassador_role.txt",
            "context_pull_check_inventory_appearance.txt",
            "context_pull_check_player_appearance_status.txt",
            "context_pull_nearby_settlements.txt",
            "context_pull_nearby_bandit_parties.txt",
            "context_pull_nearby_lord_parties.txt",
            "context_pull_current_settlement_facts.txt",
            "context_pull_kingdom_diplomacy_status.txt",
            "context_pull_clan_wealth_and_influence.txt",
            "context_pull_appraise_trade_offer.txt",
            "context_pull_verify_world_history.txt",
            "context_pull_relevant_memory.txt",
            "context_pull_relationship_history.txt",
            "dialogue_system.txt",
            "dialogue_live_turn_template.txt",
            "dialogue_internal_posture.txt",
            "jealousy_approach.txt",
            "rescue_opening.txt",
            "dialogue_output_schema.json",
            "pull_out.txt",
            "action_planner_system.txt",
            "action_planner_live_turn_template.txt",
            "action_planner_output_schema.json",
            "event_system.txt",
            "event_live_turn_template.txt",
            "event_output_schema.json",
            "action_suggestion_rules.txt",
            "memory_write_rules.txt",
            "correspondence_system.txt",
            "correspondence_live_turn_template.txt",
            "portrait_identity.txt",
            "portrait_body_appearance.txt",
            "portrait_composition.txt",
            "portrait_prompt_style.txt",
            "portrait_output_rules.txt"
		    }.Concat(RelationshipsService.RelationshipPromptFileNames()).Concat(PortraitsService.PortraitClothingPromptFileNames).Concat(PortraitsService.PortraitPhysicalConfidencePromptFileNames).Concat(ActionPromptFileNames).Concat(CourtService.CastleChatPromptFileNames()).Concat(CharactersService.TavernHousePromptFileNames).ToArray(); }
        }
        internal static readonly string[] ContextPullIds =
        {
            "check_inventory_appearance",
            "check_player_appearance_status",
            "nearby_settlements",
            "nearby_bandit_parties",
            "nearby_lord_parties",
            "current_settlement_facts",
            "kingdom_diplomacy_status",
            "clan_wealth_and_influence",
            "appraise_trade_offer",
            "verify_world_history",
            "relevant_memory",
            "relationship_history"
        };

#if !REIGN_EXCLUDE_VERIFICATION
        internal static Dictionary<string, object> PersistBoundedMotiveCliReport(Dictionary<string, object> result, string evidenceDirectory)
        {
            Directory.CreateDirectory(evidenceDirectory);
            string fullJson = Json.Serialize(result);
            string path = Path.Combine(evidenceDirectory, "motive-conversation-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, fullJson, new UTF8Encoding(false));
            var rows = ReadDictionaryList(result, "results");
            var failed = rows.Where(row => !VerificationService.CheckPassed(row)).ToList();
            return TestDict("schema", "reign-motive-cli-summary-v1", "ok", ReadBool(result, "ok", false) && rows.Count > 0 && failed.Count == 0,
                "passedCount", rows.Count - failed.Count, "failedCount", failed.Count, "totalCount", rows.Count,
                "failures", failed.Take(32).Select(row => TestDict("caseId", DialogueService.LimitText(ReadFirstString(row, "caseId", "id", "name"), 160),
                    "summary", DialogueService.LimitText(ReadFirstString(row, "summary", "error", "message"), 512))).ToList(),
                "failureDetailsLimited", failed.Count > 32, "resultsPath", path,
                "resultsSha256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fullJson))).ToLowerInvariant());
        }
#endif

        private static void Main(string[] args)
        {
            if (PortraitsService.TryRunPreparedTavernSourceCommand(args ?? new string[0])) return;
            ConfigureVerificationDatabaseIsolation(args ?? new string[0]);
            try
            {
                // Activation must not initialize, clean up or connect to campaign
                // storage before deciding whether a server already owns it.
                if (HasArg(args, "--activate-only"))
                {
                    Environment.ExitCode = ActivateExistingServerFromLauncher(ReadPort(args, SettingsService.LoadSettings()), args);
                    return;
                }
                if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("ReignServer runs inside its own Linux WSL distro.");
                if (HasArg(args, "--continuity-repair"))
                {
                    var repair = DialogueService.RunConversationContinuityRepairCli(args);
                    Console.WriteLine(Json.Serialize(repair));
                    Environment.ExitCode = ReadBool(repair, "ok", false) ? 0 : 1;
                    return;
                }
                RunServer(args ?? new string[0]);
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                Console.WriteLine("Bannerlord Reign Server could not start cleanly.");
                Console.WriteLine(ex.ToString());
                Console.WriteLine();
                Console.WriteLine("Use the ReignServer launcher to restart ReignServer.");
                OperationsService.LogOperational("server.unhandled_startup_exception", new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["type"] = ex.GetType().FullName ?? ""
                });
            }
        }

        private static void ConfigureVerificationDatabaseIsolation(string[] args)
        {
            if (!HasArg(args, "--run-verification"))
                return;

            // A CLI verification process is disposable and must never share the
            // authoritative database used by the visible game server. Enforce
            // this in the executable as well as in MCP so older launchers and
            // hand-run verification commands cannot delete live campaign schemas.
            Environment.SetEnvironmentVariable("REIGN_VALIDATION_MODE", "1");
            Environment.SetEnvironmentVariable(
                "REIGN_DB_NAME",
                ReignPostgreSqlOptions.ValidationDatabaseName);
        }

        private static void RunServer(string[] args)
        {
            if (HasArg(args, "--author-character-narratives"))
            {
                var result = CharactersService.RunNarrativeAuthoringCli(args);
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }
            if (HasArg(args, "--complete-delete-all"))
            {
                Environment.ExitCode = PersistenceService.CompleteDeferredDeleteAllCampaignData(args ?? new string[0]);
                return;
            }

            // Pure catalog authoring must not initialize or clean campaign/database storage.
            if (HasArg(args, "--generate-profile-library"))
            {
                Console.WriteLine(Json.Serialize(CharactersService.GenerateCharacterProfileLibrary(args ?? new string[0])));
                return;
            }
            SettingsService.MigrateLegacyRuntimeDataIfNeeded();
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(PromptsDir);
            Directory.CreateDirectory(CharacterTemplatesDir);
            Directory.CreateDirectory(TestsDir);
            CampaignFileService.CompactOperationalLogsAtStartup();
            PersistenceService.PurgeLegacySaveSyncSnapshotsAtStartup();
            PersistenceService.CleanupAllSaveSyncRecoveryAtStartup();
            CampaignFileService.CleanupInternalCampaignArtifactsAtStartup();
            VerificationService.PruneTestRuns(100);
            PromptService.EnsurePromptTemplates();
            DialogueService.EnsurePromptCacheLayoutMigration();
            VerificationService.EnsureDefaultTestCases();

#if !REIGN_EXCLUDE_VERIFICATION
            if (HasArg(args, "--run-verification") || HasArg(args, "--cancel-verification"))
            {
                string[] verificationArgs = HasArg(args, "--cancel-verification") && !HasArg(args, "--cancel")
                    ? args.Concat(new[] { "--cancel" }).ToArray()
                    : args;
                Dictionary<string, object> result = VerificationService.RunVerificationCli(verificationArgs);
                Console.WriteLine(HasArg(args, "--json") ? Json.Serialize(result) : Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "passed", ReadBool(result, "ok", false)) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-diplomacy-tests"))
            {
                Dictionary<string, object> result = DiplomacyService.RunWorldDiplomacySelfTests();
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "passed", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-world-history-tests"))
            {
                Dictionary<string, object> result = WorldSimulationService.RunWorldHistorySelfTests();
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "passed", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-world-history-stress-tests"))
            {
                Dictionary<string, object> result = WorldSimulationService.RunWorldHistoryStressTests();
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "passed", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-rebellion-tests"))
            {
                Dictionary<string, object> result = RebellionService.RunRebellionSelfTests();
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-world-test-tests"))
            {
                List<Dictionary<string, object>> rows = WorldSimulationService.RunWorldTestSelfTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-social-balance-harness-tests"))
            {
                Dictionary<string, object> result = ReputationService.RunSocialBalanceHarnessSelfTests();
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "passed", false) ? 0 : 1;
                return;
            }

#if !REIGN_EXCLUDE_COURT
            if (HasArg(args, "--run-court-tests"))
            {
                List<Dictionary<string, object>> rows = CourtService.RunCourtSelfTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passed"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "passed", false) ? 0 : 1;
                return;
            }
#endif

            if (HasArg(args, "--run-trait-percentage-tests"))
            {
                List<Dictionary<string, object>> rows = CharactersService.RunTraitPercentageSelfTests();
                rows.AddRange(CourtService.RunCourtCharacterSelfTests());
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-motive-conversation-tests"))
            {
                List<Dictionary<string, object>> rows = DialogueService.RunMotiveAwareConversationSelfTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)), ["totalCount"] = rows.Count, ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(HasArg(args, "--json")
                    ? PersistBoundedMotiveCliReport(result, Path.Combine(TestsDir, "cli-reports")) : result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args,
                    "--run-sovereign-demeanor-provider-tests"))
            {
                Dictionary<string, object> result =
                    DialogueService.RunSovereignDemeanorProviderQualification(args);
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false)
                    ? 0 : 1;
                return;
            }

            if (HasArg(args,
                    "--reclassify-sovereign-demeanor-report"))
            {
                Dictionary<string, object> result =
                    DialogueService.ReclassifySovereignDemeanorProviderReport(args);
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false)
                    ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-prompt-caching-tests"))
            {
                List<Dictionary<string, object>> rows = DialogueService.RunPromptCachingSelfTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-conversation-scene-state-tests"))
            {
                List<Dictionary<string, object>> rows = DialogueService.RunConversationSceneStateSelfTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-rumor-tests"))
            {
                List<Dictionary<string, object>> rows = ReputationService.RunRumorSubsystemSelfTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count, ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-relationship-tests"))
            {
                List<Dictionary<string, object>> rows = RelationshipsService.RunCurrentRelationshipSelfTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-pregnancy-choice-tests"))
            {
                List<Dictionary<string, object>> rows = RelationshipsService.RunPregnancyChoiceSelfTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-notable-mbti-tests"))
            {
                List<Dictionary<string, object>> rows = RelationshipsService.RunNotableMbtiSelfTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-relationship-simulation-tests"))
            {
                List<Dictionary<string, object>> rows = RelationshipsService.RunCurrentRelationshipSelfTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-relationship-simulation-audit"))
            {
                Dictionary<string,object> result = new Dictionary<string, object>
                {
                    ["ok"] = true, ["retired"] = true,
                    ["replacement"] = "directional_mbti_native_relation",
                    ["message"] = "The old facet simulation audit is retired. Use --run-relationship-tests."
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = 0;
                return;
            }

            if (HasArg(args, "--run-save-sync-tests"))
            {
                List<Dictionary<string, object>> rows = PersistenceService.RunSaveSyncSelfTests();
                rows.AddRange(PersistenceService.RunCampaignBackupSelfTests());
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-character-editor-tests"))
            {
                List<Dictionary<string, object>> rows = CharactersService.RunCharacterEditorSubsystemSelfTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-portrait-derivative-tests"))
            {
                List<Dictionary<string, object>> rows = PortraitsService.RunPortraitDerivativeSelfTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-shared-portrait-generation-tests"))
            {
                List<Dictionary<string, object>> rows = PortraitsService.RunSharedPortraitGenerationSelfTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--run-portrait-provider-adapter-tests"))
            {
                List<Dictionary<string, object>> rows = PortraitsService.RunPortraitProviderAdapterContractTests();
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = rows.Count > 0 && rows.All(x => ReadBool(x, "passed", false)),
                    ["passedCount"] = rows.Count(x => ReadBool(x, "passed", false)),
                    ["totalCount"] = rows.Count,
                    ["results"] = rows
                };
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }
#else
            if (args.Any(x => x.StartsWith("--run-", StringComparison.OrdinalIgnoreCase))
                || HasArg(args, "--cancel-verification")
                || HasArg(args, "--reclassify-sovereign-demeanor-report"))
            {
                Console.WriteLine(Json.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "This ReignServer build excludes verification and self-test commands."
                }));
                Environment.ExitCode = 1;
                return;
            }
#endif

            if (HasArg(args, "--audit-foundation-traits-vnext"))
            {
                Dictionary<string, object> result = CharactersService.AuditFoundationTraitRangeVNext(args ?? new string[0]);
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

            if (HasArg(args, "--validate-profile-library"))
            {
                Console.WriteLine(Json.Serialize(CharactersService.ValidateCharacterProfileLibrary()));
                return;
            }

            if (HasArg(args, "--migrate-character-profiles") || HasArg(args, "--regenerate-character-personalities"))
            {
                Dictionary<string, object> result = CharactersService.MigrateCharacterProfilesToCurrentModels(args ?? new string[0]);
                result["operation"] = HasArg(args, "--regenerate-character-personalities") ? "regenerate_character_personalities" : "migrate_character_profiles";
                Console.WriteLine(Json.Serialize(result));
                Environment.ExitCode = ReadBool(result, "ok", false) ? 0 : 1;
                return;
            }

#if !REIGN_EXCLUDE_VERIFICATION
            if (HasArg(args, "--run-tests"))
            {
                Dictionary<string, object> cliResult = VerificationService.RunTestsCli(args ?? new string[0]);
                Console.WriteLine(HasArg(args, "--json") ? Json.Serialize(cliResult) : VerificationService.FormatTestCliResult(cliResult));
                return;
            }
#endif

            Dictionary<string, object> settings = SettingsService.LoadSettings();
            int port = ReadPort(args, settings);
            ReignPostgreSqlStorage.EnsureInfrastructure();
            Console.WriteLine("ReignServer database versioning check completed. Schema version "
                + ReignPostgreSqlStorage.DatabaseSchemaVersion + ".");
            CharactersService.InitializeCharacterProfileLibrary();
            Directory.CreateDirectory(DataDir);
            ActiveServerPort = port;

            TcpListener listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                HandlePortAlreadyInUse(port, args, ex);
                return;
            }

            OperationsService.InitializeReignTelemetry(settings);
            ShutdownRequested = false;
            OperationsService.StartPriorityBackgroundScheduler();
            // Startup maintenance is admitted first, ahead of every other producer and memory fan-out.
            ReputationService.ResumePendingReputationReasonJobs();
            RelationshipsService.StartContinuousRelationshipWorker();
            WorldSimulationService.StartWorldTestRollupWorker();
            DialogueService.ResumePendingMemoryBackgroundJobs();
            DialogueService.StartSemanticMemorySubsystem(settings);
            PersistenceService.ProcessPendingCampaignRetirementsAtStartup();
            PersistenceService.ResumePendingSaveSyncOptimizations();

            Console.WriteLine("Bannerlord Reign Server is running.");
            Console.WriteLine("Listening on http://127.0.0.1:" + port);
            Console.WriteLine("Managed by ReignServer.");
            Console.WriteLine();

            OperationsService.LogOperational("server.start", new Dictionary<string, object>
            {
                ["port"] = port,
                ["dataDir"] = DataDir,
                ["settingsPath"] = SettingsPath
            });

            PersistenceService.StartCampaignRetirementMonitor();
            ActiveListener = listener;
            using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGTERM,
                context => { context.Cancel = true; RequestServerShutdown("dwemerdistro"); });
            using var interrupt = System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGINT,
                context => { context.Cancel = true; RequestServerShutdown("dwemerdistro"); });

            try
            {
                while (!ShutdownRequested)
                {
                    try
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        // Bounded concurrency: past the cap a connection is answered inline and closed, never queued.
                        if (Interlocked.Increment(ref ActiveIngressConnections) > IngressMaxConcurrentConnections)
                        {
                            Interlocked.Decrement(ref ActiveIngressConnections);
                            RejectBusyConnection(client);
                            continue;
                        }
                        Task.Run(() =>
                        {
                            try { HandleClient(client); }
                            finally { Interlocked.Decrement(ref ActiveIngressConnections); }
                        });
                    }
                    catch (SocketException) when (ShutdownRequested)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (ShutdownRequested)
                    {
                        break;
                    }
                }
            }
            finally
            {
                bool requestedFromUi = ShutdownRequested;
                ActiveListener = null;
                try { listener.Stop(); } catch { }
                // Atomically close delete-all scheduling and claim its one pending owner. A request
                // that has not published by now is refused rather than promised a deletion.
                bool deleteAllClaimed;
                Dictionary<string, object> deleteAllSettings;
                lock (DeleteAllScheduleLock)
                {
                    DeleteAllSchedulingClosed = true;
                    deleteAllClaimed = DeleteAllCampaignDataPending;
                    deleteAllSettings = DeleteAllScheduledSettings;
                    DeleteAllScheduledSettings = null;
                }
                // Idempotent; also closes admission when the listener exited exceptionally.
                Task backgroundFence = OperationsService.StopPriorityBackgroundScheduler("listener_closed");
                // Published before any quiescence snapshot so the semantic pump observes it.
                ShutdownRequested = true;
                if (deleteAllClaimed)
                {
                    try
                    {
                        DeleteAllCampaignResponseSent.Wait(5000);
                        // No campaign, backup or schedule lock is held across any wait or deletion phase.
                        Dictionary<string, object> quiescence = WaitForDeleteAllQuiescence(backgroundFence, 5000);
                        PersistenceService.CompleteScheduledDeleteAllCampaignData(deleteAllSettings, quiescence,
                            PersistenceService.DeleteAllPreDeleteBlockReason, PersistenceService.DeleteExternalSemanticCollection,
                            () =>
                            {
                                // The stop captures the exact owned worker with the pump under
                                // SemanticWorkerLock before any wait, kills and awaits only that process
                                // with no lock held, and returns true only for proven exit with no newer
                                // tracked worker. Anything else throws here and blocks local deletion.
                                if (!DialogueService.StopSemanticMemorySubsystem(5000))
                                    throw new InvalidOperationException(
                                        "the owned vector worker exit was not proven within 5000 ms, or a newer worker is tracked");
                            },
                            () =>
                            {
                                CampaignDataGate.EnterWriteLock();
                                try { return PersistenceService.CompleteDeleteAllCampaignData(); }
                                finally { CampaignDataGate.ExitWriteLock(); }
                            },
                            PersistenceService.WriteDeleteAllFailureReceipt);
                    }
                    catch (Exception ex)
                    {
                        PersistenceService.WriteDeleteAllFailureReceipt(new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["completedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                            ["error"] = ex.Message
                        });
                    }
                    finally
                    {
                        DeleteAllCampaignDataPending = false;
                        DeleteAllCampaignResponseSent.Set();
                    }
                }
                else DialogueService.StopSemanticMemorySubsystem();
                OperationsService.LogOperational("server.stop", new Dictionary<string, object>
                {
                    ["port"] = port, ["requestedFromUi"] = requestedFromUi, ["backgroundSettled"] = backgroundFence.IsCompleted
                });
                ActiveServerPort = 0;
            }
        }

        // Delete-all only. One bounded budget (one Stopwatch) shared by the scheduler stop fence, the
        // accepted-handler count and the existing semantic pump task. Called from the accept loop's own
        // finally after listener.Stop(), so ActiveIngressConnections can only fall: each accepted
        // handler decrements it in its Task.Run finally. In-flight handlers (settings save, Save Sync)
        // can restart the pump, so the pump is snapshotted under SemanticWorkerLock only after ingress
        // reaches zero, and awaited outside it and every campaign/backup lock. Task faults are
        // observed, never rethrown into shutdown. Unrelated workers are not covered.
        private static Dictionary<string, object> WaitForDeleteAllQuiescence(Task backgroundFence, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            int Remaining() => (int)Math.Max(0L, timeoutMs - watch.ElapsedMilliseconds);
            bool WaitObserved(Task task)
            {
                if (task == null) return true;
                bool done = Task.WaitAny(new[] { task }, Remaining()) == 0;
                if (done) _ = task.Exception;
                return done;
            }
            bool schedulerSettled = WaitObserved(backgroundFence);
            bool ingressSettled = Volatile.Read(ref ActiveIngressConnections) <= 0;
            while (!ingressSettled && Remaining() > 0)
            {
                Thread.Sleep(Math.Min(20, Remaining()));
                ingressSettled = Volatile.Read(ref ActiveIngressConnections) <= 0;
            }
            bool pumpSettled = false;
            if (ingressSettled)
            {
                Task pump;
                lock (DialogueService.SemanticWorkerLock) pump = DialogueService.SemanticPumpTask;
                pumpSettled = WaitObserved(pump);
            }
            // A replaced pump can already be inside a campaign read lease. Enter and immediately exit
            // the write lock on the same remaining budget: prior read leases finish first, and later
            // stale acquisitions see ShutdownRequested and refuse. Nothing else runs while it is held.
            bool campaignReadLeasesDrained = false;
            if (schedulerSettled && ingressSettled && pumpSettled)
            {
                try
                {
                    if (CampaignDataGate.TryEnterWriteLock(Remaining()))
                    {
                        CampaignDataGate.ExitWriteLock();
                        campaignReadLeasesDrained = true;
                    }
                }
                catch (Exception) { campaignReadLeasesDrained = false; }
            }
            return new Dictionary<string, object>
            {
                ["settled"] = schedulerSettled && ingressSettled && pumpSettled && campaignReadLeasesDrained,
                ["campaignReadLeasesDrained"] = campaignReadLeasesDrained,
                ["schedulerSettled"] = schedulerSettled,
                ["ingressSettled"] = ingressSettled,
                ["activeIngressConnections"] = Volatile.Read(ref ActiveIngressConnections),
                ["semanticPumpSettled"] = pumpSettled,
                ["waitedMs"] = watch.ElapsedMilliseconds
            };
        }

        internal static Dictionary<string, object> RequestServerShutdown(string source = "control_center")
        {
            // Close background admission before the accept loop can observe shutdown. This never
            // waits for the active callback, but the first caller settles queued never-started
            // releases synchronously, so no caller may hold a campaign or backup lock here.
            OperationsService.StopPriorityBackgroundScheduler(source ?? "control_center");
            if (ShutdownRequested)
            {
                return new Dictionary<string, object> { ["ok"] = true, ["status"] = "stopping" };
            }

            ShutdownRequested = true;
            LlmProviderService.StopCodexAppServer();
            OperationsService.LogOperational("server.shutdown_requested", new Dictionary<string, object> { ["source"] = source ?? "control_center" });
            TcpListener listener = ActiveListener;
            IPEndPoint endpoint = listener?.LocalEndpoint as IPEndPoint;
            Thread shutdownThread = new Thread(() =>
            {
                Thread.Sleep(100);
                if (endpoint != null)
                {
                    try
                    {
                        using (TcpClient wake = new TcpClient()) wake.Connect(IPAddress.Loopback, endpoint.Port);
                    }
                    catch { }
                }
                try { listener?.Server?.Close(); } catch { }
                try { listener?.Stop(); } catch { }
            }) { IsBackground = true, Name = "ReignServerShutdown" };
            shutdownThread.Start();
            return new Dictionary<string, object> { ["ok"] = true, ["status"] = "stopping" };
        }

        private static void HandlePortAlreadyInUse(int port, string[] args, SocketException ex)
        {
            string url = "http://127.0.0.1:" + port + "/";
            bool healthy = IsExistingServerHealthy(port);
            bool activated = TryActivateExistingServer(port, args, healthy);
            Console.WriteLine("Bannerlord Reign Server did not start a second copy.");
            Console.WriteLine("Port " + port + " is already in use.");
            if (healthy)
            {
                Console.WriteLine("An existing Bannerlord Reign server is already running.");
                Console.WriteLine("Opened the existing control center and brought its server window forward:");
                Console.WriteLine(url);
                OperationsService.LogOperational("server.already_running", new Dictionary<string, object>
                {
                    ["port"] = port,
                    ["url"] = url,
                    ["activated"] = activated
                });
                return;
            }

            if (activated)
            {
                Console.WriteLine("The existing Reign server is not answering health checks.");
                Console.WriteLine("Its terminal window was brought forward so it can be inspected or closed.");
                Console.WriteLine("The Control Center address is:");
                Console.WriteLine(url);
                OperationsService.LogOperational("server.unresponsive_instance_activated", new Dictionary<string, object>
                {
                    ["port"] = port,
                    ["url"] = url
                });
                return;
            }

            Console.WriteLine("Something else is using this port, or the existing server is not responding.");
            Console.WriteLine("Close the other process or change the server port in settings.");
            Console.WriteLine("Socket error: " + ex.Message);
            OperationsService.LogOperational("server.port_in_use", new Dictionary<string, object>
            {
                ["port"] = port,
                ["error"] = ex.Message
            });
        }

        private static bool IsExistingServerHealthy(int port)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/health");
                    request.Method = "GET";
                    request.Timeout = 1200;
                    request.ReadWriteTimeout = 1200;
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                    {
                        string body = reader.ReadToEnd();
                        return response.StatusCode == HttpStatusCode.OK
                            && body.IndexOf("BannerlordReignServer", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
                catch
                {
                    if (attempt < 2) Thread.Sleep(150);
                }
            }

            return false;
        }

        private static int ReadPort(string[] args, Dictionary<string, object> settings)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("--port", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int port))
                {
                    return port;
                }
            }

            return ReadInt(settings, "port", 5101);
        }

        internal static bool HasArg(string[] args, string value)
        {
            return args.Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
        }

        private static void HandleClient(TcpClient client)
        {
            CampaignRequestAccess campaignAccess = CampaignRequestAccess.None;
            RouteContext routeContext = new RouteContext();
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                HttpRequest request = null;
                ConversationResponseService.ConversationDiagnosticRequest diagnosticRequest = null;
                try
                {
                    // Access policy and framing limits are enforced inside ReadRequest, before the body, any campaign lock
                    // or any route side effect.
                    request = ReadRequest(stream, (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "");
                    if (request == null)
                    {
                        return;
                    }

                    if (!string.IsNullOrEmpty(request.ReignProtocol)
                        && request.ReignProtocol != Reign.Core.Contracts.Platform.ReignInstallation.SupportedProtocol.ToString(CultureInfo.InvariantCulture))
                        throw new InvalidDataException("Reign and ReignServer use different protocols. Install the matching Reign package.");
                    campaignAccess = EnterCampaignRequestAccess(request.Path);
                    diagnosticRequest = new ConversationResponseService.ConversationDiagnosticRequest(request.Path, request.JsonBody);

                    object response;
                    int status = 200;

                    routeContext.Stream = stream;
                    if (TryDispatchRegisteredRoute(request, routeContext, out response))
                    {
                        if (ReferenceEquals(response, RouteResponseWritten)) return;
                    }
                    else
                    {
                        status = 404;
                        response = new Dictionary<string, object> { ["ok"] = false, ["error"] = "Unknown route." };
                    }

                    WriteJson(stream, status, response, request.Method, request.Path);
                    diagnosticRequest?.Delivered(status);
                    OperationsService.LogRequest(request, status);
                    if (routeContext.DeleteAllResponsePending) DeleteAllCampaignResponseSent.Set();
                }
                catch (HttpIngressException ex)
                {
                    OperationsService.LogOperational(ex.Status == 403 ? "http.forbidden" : "http.rejected", new Dictionary<string, object>
                    {
                        ["status"] = ex.Status,
                        ["path"] = DialogueService.LimitText(ex.RequestPath, 200),
                        ["reason"] = ex.Reason
                    });
                    WriteIngressRejection(client, stream, ex.Status, ex.Message);
                }
                catch (DirectoryNotFoundException ex)
                {
                    // DirectoryNotFoundException derives from IOException, but it
                    // represents a server-side storage fault rather than a client
                    // disconnect. Preserve the full diagnostic and return a normal
                    // failure response so callers do not see a misleading socket
                    // reset with no actionable cause.
                    OperationsService.LogOperational("server.error", new Dictionary<string, object>
                    {
                        ["method"] = request?.Method ?? string.Empty,
                        ["path"] = request?.Path ?? string.Empty,
                        ["exceptionType"] = ex.GetType().Name,
                        ["error"] = ex.ToString()
                    });
                    try
                    {
                        WriteJson(stream, 500, new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["error"] = ex.Message
                        });
                    }
                    catch (IOException writeException)
                    {
                        OperationsService.LogOperational("server.client_disconnect", new Dictionary<string, object>
                        {
                            ["method"] = request?.Method ?? string.Empty,
                            ["path"] = request?.Path ?? string.Empty,
                            ["exceptionType"] = writeException.GetType().Name,
                            ["duringErrorResponse"] = true
                        });
                    }
                    if (routeContext.DeleteAllResponsePending) DeleteAllCampaignResponseSent.Set();
                }
                catch (IOException ex)
                {
                    OperationsService.LogOperational("server.client_disconnect", new Dictionary<string, object>
                    {
                        ["method"] = request?.Method ?? string.Empty,
                        ["path"] = request?.Path ?? string.Empty,
                        ["exceptionType"] = ex.GetType().Name
                    });
                    if (routeContext.DeleteAllResponsePending) DeleteAllCampaignResponseSent.Set();
                }
                catch (SocketException ex)
                {
                    OperationsService.LogOperational("server.client_disconnect", new Dictionary<string, object>
                    {
                        ["method"] = request?.Method ?? string.Empty,
                        ["path"] = request?.Path ?? string.Empty,
                        ["exceptionType"] = ex.GetType().Name
                    });
                    if (routeContext.DeleteAllResponsePending) DeleteAllCampaignResponseSent.Set();
                }
                catch (Exception ex)
                {
                    OperationsService.LogOperational("server.error", new Dictionary<string, object> { ["error"] = ex.ToString() });
                    try
                    {
                        WriteJson(stream, 500, new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message });
                    }
                    catch (IOException writeException)
                    {
                        OperationsService.LogOperational("server.client_disconnect", new Dictionary<string, object>
                        {
                            ["method"] = request?.Method ?? string.Empty,
                            ["path"] = request?.Path ?? string.Empty,
                            ["exceptionType"] = writeException.GetType().Name,
                            ["duringErrorResponse"] = true
                        });
                    }
                    if (routeContext.DeleteAllResponsePending) DeleteAllCampaignResponseSent.Set();
                }
                finally
                {
                    diagnosticRequest?.Dispose();
                    ExitCampaignRequestAccess(campaignAccess);
                    // The import route consumes its upload; any upload left by a refused or failed request is removed here,
                    // before the staging slot and the JSON input reservation are returned (every exit path, early returns too).
                    if (!string.IsNullOrEmpty(request?.UploadFilePath)) PersistenceService.TryDeleteFile(request.UploadFilePath);
                    ReleaseIngressReservations(request);
                }
            }
        }

        internal static CampaignRequestAccess EnterCampaignRequestAccess(string path)
        {
            path = path ?? string.Empty;
            // Exemptions are declared on the routes (WithoutCampaignGate) and as exempt areas in each module's
            // Program.<Module>Routes.cs. They apply to the path for every method, registered or not.
            if (IsCampaignGateExemptPath(path))
                return CampaignRequestAccess.None;

            CampaignDataGate.EnterReadLock();
            PersistenceService.CurrentCampaignRequestLease = new PersistenceService.CampaignRequestLease
            {
                Generation = Interlocked.Read(ref PersistenceService.CampaignReplacementGeneration)
            };
            return CampaignRequestAccess.Read;
        }

        internal static void ExitCampaignRequestAccess(CampaignRequestAccess access)
        {
            if (access == CampaignRequestAccess.Write) CampaignDataGate.ExitWriteLock();
            else if (access == CampaignRequestAccess.Read) CampaignDataGate.ExitReadLock();
            PersistenceService.CurrentCampaignRequestLease = null;
        }

        internal static Dictionary<string, object> RunInteractiveRequest(Func<Dictionary<string, object>> request)
        {
            Interlocked.Increment(ref InteractiveRequestCount);
            OperationsService.ReignTraceScope trace = OperationsService.BeginReignSpan("interaction.production", new Dictionary<string, object>
            { ["activeRequests"] = Volatile.Read(ref InteractiveRequestCount) });
            try
            {
                return request();
            }
            catch (Exception ex)
            {
                trace.Fail(ex);
                throw;
            }
            finally
            {
                trace.Dispose();
                Interlocked.Exchange(ref LastInteractiveRequestCompletedUtcTicks, DateTime.UtcNow.Ticks);
                Interlocked.Decrement(ref InteractiveRequestCount);
            }
        }

        internal static bool InteractiveRequestActiveOrRecent(int quietPeriodMilliseconds = 2000)
        {
            if (Volatile.Read(ref InteractiveRequestCount) > 0)
            {
                return true;
            }

            long completedTicks = Interlocked.Read(ref LastInteractiveRequestCompletedUtcTicks);
            if (completedTicks <= 0)
            {
                return false;
            }

            long elapsedTicks = DateTime.UtcNow.Ticks - completedTicks;
            return elapsedTicks >= 0 && elapsedTicks < TimeSpan.FromMilliseconds(Math.Max(0, quietPeriodMilliseconds)).Ticks;
        }

        internal static void WaitForInteractiveRequests()
        {
            bool announced = false;
            while (Volatile.Read(ref InteractiveRequestCount) > 0)
            {
                if (!announced)
                {
                    announced = true;
                    OperationsService.LogOperational("characters.construct_batch_paused", new Dictionary<string, object>
                    {
                        ["reason"] = "interactive_request_active",
                        ["activeInteractiveRequests"] = Volatile.Read(ref InteractiveRequestCount)
                    });
                }

                Thread.Sleep(250);
            }
        }

        internal static string ControlCenterHtml()
        {
            string html = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "index.html")).Replace("@IMAGE_PROFILE_CARDS@", PortraitsService.ImageProfileCards())
                .Replace("@CHAT_PROVIDER_KEYS@", LlmProviderService.ChatProviderKeysHtml()).Replace("@CHAT_PROVIDER_SCRIPT@", LlmProviderService.ChatProviderScript())
                .Replace("@CODEX_PERFORMANCE_CONTROLS@", LlmProviderService.CodexPerformanceControlCenterHtml())
                .Replace("@CODEX_MODEL_AVAILABILITY@", LlmProviderService.CodexModelAvailabilityHtml())
                .Replace("@CODEX_PERFORMANCE_SCRIPT@", LlmProviderService.CodexPerformanceControlCenterScript() + SettingsService.ReignXpOptionsScript() + CharactersService.NpcStoryEditorScript())
                .Replace("@REIGN_XP_OPTIONS@", SettingsService.ReignXpOptionsHtml())
                .Replace("@NPC_LIVES_DIAGNOSTICS@", SettingsService.NpcLivesDiagnosticsHtml())
                .Replace("@SERVER_CONTROL_SCRIPT@", File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "server-control.js")))
                .Replace("@CONVERSATION_DIAGNOSTICS_SCRIPT@", File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "conversation-diagnostics.js")));
            html = html.Replace("Unified Control Center is always attached", "Managed by ReignServer")
                .Replace("Normal launches always open one dedicated Reign window. Closing it shuts down the server and helper worker; shutting down the server closes it.",
                    "Open this page from the ReignServer launcher. Closing the browser leaves Reign running; use the launcher to manage the service.");
            return html;
        }

        internal sealed class HttpRequest
        {
            public string ReignProtocol;
            public string Method;
            public string Path;
            public Dictionary<string, string> Query;
            public Dictionary<string, object> JsonBody;
            public string UploadFilePath;
            public long BodyLength;
            public string ContentType;
            // Identity headers are null when absent (see IngressIdentityHeader).
            public string Host, Origin, ProxyPeer, ProxyHost, FetchSite, KeysHeader, RemoteAddress;
            // X-Forwarded-For was present. A proxy marker only: its value is never parsed or trusted.
            public bool ProxyForwarded;
            // Ingress reservations held for the request's lifetime (see ReleaseIngressReservations).
            public long ReservedJsonBytes;
            public int HoldsUploadSlot;
        }
    }
}
