using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void RunPromptSizeContract(List<Dictionary<string, object>> checks)
        {
            AddSubsystemListSummary(checks, "contracts.wilderness_variety", "prompt_efficiency", WorldSimulationService.RunWildernessVarietySelfTests());
            int commoner = DialogueService.BuildDialogueGlobalPrefix(false, false, new List<Dictionary<string, object>>()).Length;
            int noble = DialogueService.BuildDialogueGlobalPrefix(false, true, new List<Dictionary<string, object>>()).Length;
            int maximum = Math.Max(commoner, noble);
            AddVerificationCheck(checks, "contracts.dialogue_prompt_budget", "prompt_efficiency", commoner > 0 && noble > 0 && maximum <= 45000,
                "Assembled ordinary-dialogue system prefixes: commoner=" + commoner + ", noble=" + noble + " characters.",
                new Dictionary<string, object> { ["commonerCharacters"] = commoner, ["nobleCharacters"] = noble, ["maximumChars"] = 45000 });
        }

        private static void RunProductionPipelineChecks(List<Dictionary<string, object>> checks, string requestedSuite, bool failFast)
        {
            Action<string> trace = stage =>
            {
                if (string.Equals(Environment.GetEnvironmentVariable("REIGN_VERIFICATION_TRACE"), "1", StringComparison.Ordinal))
                    Console.Error.WriteLine("[verification:pipeline] " + stage);
            };
            trace("canned:start");
            List<Dictionary<string, object>> loadedCases = LoadTestCases("");
            List<Dictionary<string, object>> skippedUnreplayable = loadedCases
                .Where(testCase => !IsReplayableCannedVerificationCase(testCase))
                .ToList();
            List<Dictionary<string, object>> cases = loadedCases
                .Where(IsReplayableCannedVerificationCase)
                .ToList();
            int passed = 0;
            List<Dictionary<string, object>> failures = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> testCase in cases)
            {
                if (ShouldCancelVerification()) break;
                Dictionary<string, object> row = RunPipelineTestCase(testCase, "canned");
                if (ReadBool(row, "passed", false)) passed++;
                else
                {
                    Dictionary<string, object> failure = CompactVerificationFailure(row);
                    string assertion = ReadString(ReadDictionary(row, "assertion"), "summary", "");
                    if (!string.IsNullOrWhiteSpace(assertion))
                        failure["summary"] = ReadString(failure, "summary", "") + " " + LimitText(assertion, 500);
                    failures.Add(failure);
                }
                if (failFast && failures.Count > 0) break;
            }
            Dictionary<string, object> atomicPlannerChoices = VerifyAtomicActionPlannerChoices();
            AddVerificationCheck(checks, "pipeline.canned_cases", "pipeline", failures.Count == 0 && passed == cases.Count && ReadBool(atomicPlannerChoices, "passed", false), "Production pipeline canned cases: " + passed + "/" + cases.Count + " passed; atomic package routing " + (ReadBool(atomicPlannerChoices, "passed", false) ? "passed" : "failed") + ".", new Dictionary<string, object>
            {
                ["failures"] = failures,
                ["legacyPromptOverrideCases"] = CountLegacyPromptOverrideCases(cases),
                ["skippedUnreplayableCaptures"] = skippedUnreplayable.Select(testCase => new Dictionary<string, object>
                {
                    ["suite"] = ReadString(testCase, "suite", ""),
                    ["caseId"] = ReadString(testCase, "caseId", ""),
                    ["reason"] = "Captured evidence has no model output or direct action and therefore cannot be replayed deterministically."
                }).ToList(),
                ["atomicPlannerChoices"] = atomicPlannerChoices
            });
            Dictionary<string, object> settlementAuthority = VerifyDialogueSettlementAuthority();
            AddVerificationCheck(checks, "pipeline.settlement_authority", "pipeline",
                ReadBool(settlementAuthority, "passed", false),
                "Dialogue settlement transfers are bound to current clan-owner or kingdom-ruler authority.",
                settlementAuthority);

            trace("relationships:start");
            AddSubsystemListSummary(checks, "pipeline.mbti_relationship_lifecycle", "memory_relationships", RelationshipsService.RunCurrentRelationshipSelfTests());
            trace("notable_mbti:start");
            AddSubsystemListSummary(checks, "pipeline.notable_mbti", "memory_relationships", RelationshipsService.RunNotableMbtiSelfTests());
            trace("rumors:start");
            AddSubsystemListSummary(checks, "pipeline.rumors", "knowledge_identity", ReputationService.RunRumorSubsystemSelfTests());
            trace("character_editor:start");
            AddSubsystemListSummary(checks, "pipeline.character_editor", "character_construction", CharactersService.RunCharacterEditorSubsystemSelfTests());
            trace("categorized_memory:start");
            AddSubsystemListSummary(checks, "pipeline.categorized_memory", "memory_relationships", DialogueService.RunCategorizedMemorySubsystemSelfTests());
            trace("dynamic_characteristics:start");
            AddSubsystemListSummary(checks, "pipeline.dynamic_characteristics", "memory_relationships", CharactersService.RunDynamicCharacteristicsSelfTests());
            trace("npc_dialogue_audit:start");
            AddSubsystemListSummary(checks, "pipeline.npc_dialogue_audit", "memory_relationships", DialogueService.RunNpcDialogueAuditSelfTests());
            trace("party_dialogue_audit:start");
            AddSubsystemListSummary(checks, "pipeline.party_dialogue_audit", "memory_relationships", DialogueService.RunPartyDialogueAuditSelfTests());
            trace("live_interaction:start");
            AddSubsystemListSummary(checks, "pipeline.live_interaction", "conversation_modes", WorldSimulationService.RunLiveInteractionTestSelfTests());
            trace("conversation_readiness:start");
            AddSubsystemListSummary(checks, "pipeline.conversation_readiness_evaluation", "conversation_modes", DialogueService.RunConversationReadinessEvaluationSelfTests());
            trace("final_conversation_gauntlet_models:start");
            AddSubsystemListSummary(checks, "pipeline.final_conversation_gauntlet_models", "conversation_modes", DialogueService.RunFinalConversationGauntletModelSelfTests());
            trace("semantic_memory:start");
            AddSubsystemListSummary(checks, "pipeline.semantic_memory", "memory_relationships", DialogueService.RunSemanticMemorySelfTests());
            trace("character_profiles:start");
            AddSubsystemListSummary(checks, "pipeline.character_profiles", "character_construction", CharactersService.RunCharacterProfileLibrarySelfTests(includeCourt: false));
            trace("identity:start");
            AddSubsystemListSummary(checks, "pipeline.identity", "knowledge_identity", CharactersService.RunIdentitySubsystemSelfTests());
            trace("campaign_backups:start");
            AddSubsystemListSummary(checks, "pipeline.postgresql_storage", "server_lifecycle", PersistenceService.RunPostgreSqlStorageSelfTests());
            AddSubsystemListSummary(checks, "pipeline.campaign_backups", "server_lifecycle", PersistenceService.RunCampaignBackupSelfTests());
            trace("save_sync:start");
            AddSubsystemListSummary(checks, "pipeline.save_sync", "server_lifecycle", PersistenceService.RunSaveSyncSelfTests());
            trace("prompt_caching:start");
            AddSubsystemListSummary(checks, "pipeline.prompt_caching", "prompt_efficiency", DialogueService.RunPromptCachingSelfTests());
            trace("complete");
        }

        private static bool IsReplayableCannedVerificationCase(Dictionary<string, object> testCase)
        {
            testCase = testCase ?? new Dictionary<string, object>();
            if (testCase.ContainsKey("replayable") && !ReadBool(testCase, "replayable", true))
                return false;

            string mode = ReadString(testCase, "mode", "").Trim().ToLowerInvariant();
            if (mode != "dialogue" && mode != "individual_chat" && mode != "party_chat"
                && mode != "social_event" && mode != "wilderness_event" && mode != "correspondence")
                return true;

            Dictionary<string, object> input = ReadDictionary(testCase, "input") ?? new Dictionary<string, object>();
            if ((ReadDictionary(input, "action") ?? new Dictionary<string, object>()).Count > 0
                || ReadDictionaryList(input, "actions").Count > 0)
                return true;

            Dictionary<string, object> llm = ReadDictionary(testCase, "llm") ?? new Dictionary<string, object>();
            return !string.IsNullOrWhiteSpace(ReadString(llm, "cannedContent", ""));
        }

        private static Dictionary<string, object> VerifyAtomicActionPlannerChoices()
        {
            Dictionary<string, object> snapshot = DefaultActionTestSnapshot();
            Dictionary<string, object> payload = ReadDictionary(snapshot, "payload") ?? new Dictionary<string, object>();
            Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>();
            Dictionary<string, object> settings = LoadSettings();

            Func<string, string, List<string>> choices = (playerText, intent) =>
            {
                Dictionary<string, object> gate = new Dictionary<string, object>
                {
                    ["needed"] = true,
                    ["commitment"] = "accepted",
                    ["intent"] = intent,
                    ["confidence"] = 0.9d
                };
                return BuildAllowedActionPlannerChoices(settings, payload, hero, gate, playerText, "I accept and finalize those exact terms.", 10)
                    .Select(x => CanonicalCommand(ReadString(x, "command", "")))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            };

            List<string> ransom = choices(
                "Release Prisoner Lord to me for twenty thousand denars now. Accept and finalize the ransom.",
                "Rhagaea accepts the ransom of Prisoner Lord to the player for twenty thousand denars.");
            List<string> trade = choices(
                "Our trade is final: take forty Grain from me and pay me 2,200 denars.",
                "Rhagaea accepts receiving 40 grain from the player and paying 2,200 denars to the player.");

            HashSet<string> ransomComponents = new HashSet<string>(new[] { "trade_package", "diplomatic_package", "transfer_prisoner", "give_gold_to_player", "transfer_gold" }, StringComparer.OrdinalIgnoreCase);
            HashSet<string> tradeComponents = new HashSet<string>(new[] { "diplomatic_package", "transfer_prisoner", "transfer_item", "give_gold_to_player", "transfer_gold" }, StringComparer.OrdinalIgnoreCase);
            bool passed = ransom.Contains("ransom_package", StringComparer.OrdinalIgnoreCase)
                && trade.Contains("trade_package", StringComparer.OrdinalIgnoreCase)
                && !ransom.Any(ransomComponents.Contains)
                && !trade.Any(tradeComponents.Contains);
            return new Dictionary<string, object>
            {
                ["passed"] = passed,
                ["ransomAllowed"] = ransom,
                ["tradeAllowed"] = trade
            };
        }

        private static Dictionary<string, object> VerifyDialogueSettlementAuthority()
        {
            List<Dictionary<string, object>> cases = new List<Dictionary<string, object>>();
            Func<string, string, string, string, string, Dictionary<string, object>> run =
                (caseId, command, speakerHeroId, settlementId, sourceKingdomId) =>
            {
                Dictionary<string, object> snapshot = DefaultActionTestSnapshot();
                Dictionary<string, object> payload = ReadDictionary(snapshot, "payload")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> index = ReadDictionary(payload,
                    "actionResolutionIndex") ?? new Dictionary<string, object>();
                Dictionary<string, object> speaker = FindIndexedRow(index, "heroes",
                    "heroStringId", speakerHeroId) ?? new Dictionary<string, object>();
                payload["speakerHeroStringId"] = speakerHeroId;
                payload["speakerKingdomId"] = ReadString(speaker, "kingdomId", "");
                payload["speakerClanId"] = ReadString(speaker, "clanId", "");
                payload["hero"] = speaker;
                payload["correlationId"] = "authority-" + caseId;

                Dictionary<string, object> action = new Dictionary<string, object>
                {
                    ["command"] = command,
                    ["source"] = "test_lab_action_gate",
                    ["requiresAcceptance"] = false,
                    ["reason"] = "Settlement authority verification " + caseId + "."
                };
                string resolutionText;
                if (string.Equals(command, "trade_package", StringComparison.OrdinalIgnoreCase))
                {
                    action["FromHero"] = speakerHeroId;
                    action["ToHero"] = "player";
                    action["TargetSettlement"] = settlementId;
                    action["terms"] = TestDict(
                        "settlementToHeroStringId", "player",
                        "targetSettlementStringId", settlementId,
                        "gold", 100,
                        "goldFromHeroStringId", "player",
                        "goldToHeroStringId", speakerHeroId);
                    resolutionText = "I will pay you 100 denars for " + settlementId
                        + ". You accept these exact terms.";
                }
                else
                {
                    action["actorKingdomId"] = "new_kingdom";
                    action["targetKingdomId"] = sourceKingdomId;
                    action["TargetSettlement"] = settlementId;
                    action["terms"] = TestDict("settlementIds",
                        TestList(settlementId));
                    resolutionText = "You accept peace and surrender " + settlementId + ".";
                }

                Dictionary<string, object> record = NormalizeActionCommand(action,
                    "test_campaign", out List<string> errors, payload, resolutionText);
                return new Dictionary<string, object>
                {
                    ["caseId"] = caseId,
                    ["accepted"] = record != null && errors.Count == 0,
                    ["record"] = record ?? new Dictionary<string, object>(),
                    ["errors"] = errors
                };
            };

            Dictionary<string, object> ownerTrade = run("owner_trade", "trade_package",
                "rhagaea", "town_ES4", "empire_s");
            Dictionary<string, object> unrelatedTrade = run("unrelated_trade",
                "trade_package", "mengus", "town_ES4", "empire_s");
            Dictionary<string, object> rulerSellingVassalFief = run("ruler_vassal_fief",
                "trade_package", "caladog", "castle_battania_1", "battania");
            Dictionary<string, object> clanOwnerTrade = run("clan_owner_trade",
                "trade_package", "mengus", "castle_battania_1", "battania");
            Dictionary<string, object> rulerPeace = run("ruler_peace",
                "demand_settlement_peace", "rhagaea", "town_ES4", "empire_s");
            Dictionary<string, object> nonRulerPeace = run("non_ruler_peace",
                "demand_settlement_peace", "prisoner_lord", "town_ES4", "empire_s");
            cases.AddRange(new[] { ownerTrade, unrelatedTrade, rulerSellingVassalFief,
                clanOwnerTrade, rulerPeace, nonRulerPeace });

            Dictionary<string, object> boundTrade = ReadDictionary(ownerTrade, "record")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> boundTerms = TryParseJsonObject(
                ReadString(boundTrade, "termsJson", "")) ?? new Dictionary<string, object>();
            string originalHash = ReadString(boundTrade, "termsHash", "");
            boundTerms["gold"] = 101;
            bool tamperDetected = !string.IsNullOrWhiteSpace(originalHash)
                && !string.Equals(originalHash, ComputeActionTermsHash("trade_package",
                    boundTerms), StringComparison.OrdinalIgnoreCase);
            bool canonicalEscaping = string.Equals(CanonicalActionTerms(TestDict(
                    "packageText", "I'll take <Lycaron>.")),
                "{\"packageText\":\"I'll take <Lycaron>.\"}",
                StringComparison.Ordinal);
            bool metadataBound = string.Equals(ReadString(boundTrade, "authorizationMode", ""),
                    "dialogue_acceptance", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadString(boundTrade, "acceptedByHeroStringId", ""),
                    "rhagaea", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadString(boundTerms, "settlementAuthorityKind", ""),
                    "owner_clan_leader", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(ReadString(boundTrade, "negotiationId", ""))
                && !string.IsNullOrWhiteSpace(originalHash);
            bool passed = ReadBool(ownerTrade, "accepted", false)
                && !ReadBool(unrelatedTrade, "accepted", false)
                && !ReadBool(rulerSellingVassalFief, "accepted", false)
                && ReadBool(clanOwnerTrade, "accepted", false)
                && ReadBool(rulerPeace, "accepted", false)
                && !ReadBool(nonRulerPeace, "accepted", false)
                && metadataBound && tamperDetected && canonicalEscaping;
            return new Dictionary<string, object>
            {
                ["passed"] = passed,
                ["metadataBound"] = metadataBound,
                ["tamperDetected"] = tamperDetected,
                ["canonicalEscaping"] = canonicalEscaping,
                ["cases"] = cases
            };
        }

        private static void RunProviderFaultChecks(List<Dictionary<string, object>> checks)
        {
            Dictionary<string, object> secrets = new Dictionary<string, object>
            {
                ["apiKey"] = "sk-secret-value",
                ["portraitNanoGptApiKey"] = "portrait-secret",
                ["portraitAtlasApiKey"] = "atlas-secret",
                ["nanoGptApiKey"] = "nano-fixture",
                ["openRouterApiKey"] = "router-fixture",
                ["QDRANT_API_KEY"] = "vector-fixture",
                ["profiles"] = new object[] { TestDict("custom-api-key", "custom-fixture", "clientSecret", "client-fixture", "maxTokens", 1200) },
                ["safe"] = "visible"
            };
            Dictionary<string, object> redacted = RedactSensitive(secrets);
            string safeText = Json.Serialize(redacted);
            var hidden = new[] { "secret-value", "portrait-secret", "atlas-secret", "nano-fixture", "router-fixture", "vector-fixture", "custom-fixture", "client-fixture" };
            bool secretsSafe = hidden.All(value => !safeText.Contains(value)) && ReadString(redacted, "safe", "") == "visible"
                && ReadInt(ReadDictionaryList(redacted, "profiles").Single(), "maxTokens", 0) == 1200
                && ReadString(secrets, "nanoGptApiKey", "") == "nano-fixture";
            AddVerificationCheck(checks, "faults.secret_redaction", "provider_faults", secretsSafe, "API secrets are redacted from diagnostics.", redacted);
            string historical = Json.Serialize(TestDict("eventType", "settings.save", "data", secrets)) + "\n";
            string exported = RedactDiagnosticLogContent(historical);
            AddVerificationCheck(checks, "faults.historical_log_redaction", "provider_faults",
                hidden.All(value => !exported.Contains(value)) && exported.Contains("visible") && exported.Contains("1200")
                    && historical.Contains("nano-fixture"),
                "Read/export redaction covers historical named-provider fields without modifying the original log.", null);

            string[] faults = { "timeout", "401", "429", "500", "malformed_json", "truncated_output", "context_overflow", "missing_model", "vector_unavailable", "corrupt_image", "interrupted_write", "retry_limit" };
            foreach (string fault in faults)
            {
                Dictionary<string, object> classified = ClassifySyntheticProviderFault(fault);
                bool terminal = ReadBool(classified, "terminal", false);
                bool retryable = ReadBool(classified, "retryable", false);
                bool expected = fault == "timeout" || fault == "429" || fault == "500" || fault == "vector_unavailable" ? retryable : terminal;
                AddVerificationCheck(checks, "faults." + fault, "provider_faults", expected, "Fault classified as " + ReadString(classified, "failureKind", "unknown") + ".", classified);
            }
            bool transientClassifier = IsTransientLlmFailure("{\"error\":\"Billing reservation failed\",\"status\":503}")
                && IsTransientLlmFailure("The remote server returned an error: (429) Too Many Requests.")
                && !IsTransientLlmFailure("The remote server returned an error: (401) Unauthorized.");
            AddVerificationCheck(checks, "faults.llm_transient_retry_classifier", "provider_faults", transientClassifier,
                "Transient billing, throttling, and provider failures retry while authentication failures remain terminal.", null);
        }

        private static void RunShadowWorldChecks(List<Dictionary<string, object>> checks, string sandbox, int seed)
        {
            ReignShadowWorld world = ReignShadowWorld.Create(seed);
            string root = FindVerificationSourceRoot();
            List<string> actions = DiscoverClientActionNames(string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "ReignBeta")).Where(x => !x.Equals("Unknown", StringComparison.OrdinalIgnoreCase)).ToList();
            List<string> failures = new List<string>();
            foreach (string action in actions)
            {
                ReignShadowWorld before = world.Clone();
                ShadowExecutionResult execution = world.Execute(action, "shadow-" + action, false);
                bool retired = action == "DiplomacySignTemporaryTruce" || action == "DiplomacyTradeEmbargo";
                bool pass = retired ? !execution.Success && execution.Terminal : execution.Success && (execution.Changed || execution.Pending || execution.LedgerOnly);
                if (!pass) failures.Add(action + ": " + execution.Message);

                ReignShadowWorld afterFirst = world.Clone();
                ShadowExecutionResult duplicate = world.Execute(action, "shadow-" + action, false);
                bool idempotent = world.StructuralEquals(afterFirst) && (retired ? duplicate.Terminal && !duplicate.Success : duplicate.NoOp);
                if (!idempotent) failures.Add(action + ": duplicate execution was not idempotent");

                ShadowExecutionResult invalid = before.Execute(action, "invalid-" + action, true);
                if (!invalid.Terminal || invalid.Success || !before.StructuralEquals(before.Clone())) failures.Add(action + ": invalid action did not fail terminally");
            }

            string persisted = Path.Combine(sandbox, "shadow-world.json");
            WriteJsonObject(persisted, world.ToDictionary());
            ReignShadowWorld reloaded = ReignShadowWorld.FromDictionary(ReadJsonObject(persisted));
            if (!world.StructuralEquals(reloaded)) failures.Add("shadow world did not survive save/reload");
            AddVerificationCheck(checks, "shadow_world.all_actions", "actions_trade", failures.Count == 0, "Shadow world executed, rejected, persisted, and replay-protected " + actions.Count + " actions.", new Dictionary<string, object> { ["actionCount"] = actions.Count, ["failures"] = failures });

            ReignShadowWorld tradeWorld = ReignShadowWorld.Create(seed + 1);
            string beforeTrade = tradeWorld.Fingerprint();
            ShadowExecutionResult rejectedTrade = tradeWorld.ExecuteTrade(5000, 20, 2, true);
            bool atomicReject = !rejectedTrade.Success && tradeWorld.Fingerprint() == beforeTrade;
            ShadowExecutionResult acceptedTrade = tradeWorld.ExecuteTrade(5000, 20, 2, false);
            bool exactTrade = acceptedTrade.Success && tradeWorld.PlayerGold == 45000 && tradeWorld.PlayerGrain == 80 && tradeWorld.PlayerHorses == 8 && tradeWorld.NpcGold == 15000 && tradeWorld.NpcGrain == 40 && tradeWorld.NpcHorses == 6;
            AddVerificationCheck(checks, "shadow_world.atomic_trade", "actions_trade", atomicReject && exactTrade, "Trade packages reject atomically and apply exact multi-term effects.", new Dictionary<string, object> { ["rejected"] = rejectedTrade.ToDictionary(), ["accepted"] = acceptedTrade.ToDictionary(), ["world"] = tradeWorld.ToDictionary() });
        }

        private static void RunExtendedRebellionChecks(List<Dictionary<string, object>> checks, string sandbox, int seed)
        {
            ReignShadowRebellion rebellion = new ReignShadowRebellion(seed);
            List<Dictionary<string, object>> rows = rebellion.RunLifecycleMatrix();
            int passed = rows.Count(CheckPassed);
            AddVerificationCheck(checks, "rebellion.lifecycle_matrix", "rebellion", passed == rows.Count && rows.Count >= 25, "Extended rebellion lifecycle: " + passed + "/" + rows.Count + " checks passed.", new Dictionary<string, object> { ["results"] = rows });

            string path = Path.Combine(sandbox, "rebellion-checkpoint.json");
            Dictionary<string, object> snapshot = rebellion.ToDictionary();
            WriteJsonObject(path, snapshot);
            ReignShadowRebellion restored = ReignShadowRebellion.FromDictionary(ReadJsonObject(path));
            AddVerificationCheck(checks, "rebellion.save_reload", "rebellion", rebellion.Fingerprint() == restored.Fingerprint(), "Rebellion state is idempotent across save/reload.", new Dictionary<string, object> { ["path"] = path });
        }

        private static void RunLongRunChecks(List<Dictionary<string, object>> checks, string sandbox, int seed)
        {
            Random random = new Random(seed);
            ReignShadowWorld world = ReignShadowWorld.Create(seed);
            ReignShadowRebellion rebellion = new ReignShadowRebellion(seed);
            List<string> violations = new List<string>();
            int days = 5 * 126;
            for (int day = 1; day <= days; day++)
            {
                if (ShouldCancelVerification()) break;
                world.AdvanceDay(random);
                rebellion.AdvanceDay(day, random);
                if (day % 63 == 0)
                {
                    Dictionary<string, object> checkpoint = new Dictionary<string, object> { ["day"] = day, ["world"] = world.ToDictionary(), ["rebellion"] = rebellion.ToDictionary() };
                    string path = Path.Combine(sandbox, "long-run-" + day + ".json");
                    WriteJsonObject(path, checkpoint);
                    Dictionary<string, object> restored = ReadJsonObject(path);
                    ReignShadowWorld restoredWorld = ReignShadowWorld.FromDictionary(ReadDictionary(restored, "world"));
                    ReignShadowRebellion restoredRebellion = ReignShadowRebellion.FromDictionary(ReadDictionary(restored, "rebellion"));
                    if (!world.StructuralEquals(restoredWorld)) violations.Add("world checkpoint mismatch at day " + day);
                    if (rebellion.Fingerprint() != restoredRebellion.Fingerprint()) violations.Add("rebellion checkpoint mismatch at day " + day);
                }
                violations.AddRange(world.ValidateInvariants().Select(x => "day " + day + ": " + x));
                violations.AddRange(rebellion.ValidateInvariants().Select(x => "day " + day + ": " + x));
                if (violations.Count > 100) break;
            }
            AddVerificationCheck(checks, "long_run.five_years", "long_run", violations.Count == 0 && !ShouldCancelVerification(), "Seeded shadow campaign ran for " + days + " days (five 126-day years).", new Dictionary<string, object> { ["seed"] = seed, ["days"] = days, ["violations"] = violations });
        }

        private static void RunLiveLlmVerificationChecks(List<Dictionary<string, object>> checks, string sandbox, string requestedSuite, Dictionary<string, object> options)
        {
            if (string.Equals(requestedSuite, "codex_performance", StringComparison.OrdinalIgnoreCase))
            {
                RunCodexPerformanceLiveVerification(checks, sandbox, options);
                return;
            }
            if (string.Equals(requestedSuite, "codex_images", StringComparison.OrdinalIgnoreCase))
            {
                PortraitsService.RunCodexImageLiveVerification(checks, sandbox, options);
                return;
            }
            Dictionary<string, object> settings = LoadSettings();
            if (!LlmProviderConfigured(settings))
            {
                AddVerificationCheck(checks, "live_llm.configuration", "live_llm", false, "Live LLM verification requires a configured selected provider.", new Dictionary<string, object>
                {
                    ["provider"] = NormalizeLlmProvider(ReadString(settings, "llmProvider", OpenAiCompatibleProvider)),
                    ["apiKeyPresent"] = ProviderKeyConfigured(settings, LlmApiKeySlot(settings)),
                    ["apiUrlPresent"] = !string.IsNullOrWhiteSpace(LlmApiUrl(settings)),
                    ["codexExecutablePresent"] = !string.IsNullOrWhiteSpace(ReadString(settings, "codexExecutable", ""))
                }, "blocked");
                return;
            }

            if (string.Equals(requestedSuite, "campaign_command", StringComparison.OrdinalIgnoreCase))
            {
                WorldSimulationService.RunCampaignCommandLlmMatrixChecks(checks, sandbox, options);
                return;
            }

            int cap = Math.Max(1, ReadInt(options, "liveCaseCap", 20));
            List<Dictionary<string, object>> sourceCases = LoadTestCases("")
                .Where(row => ReadString(row, "suite", "").Equals("action_reliability", StringComparison.OrdinalIgnoreCase) || ReadString(row, "suite", "").Equals("action_gate", StringComparison.OrdinalIgnoreCase))
                .Take(cap)
                .ToList();
            int passed = 0;
            List<Dictionary<string, object>> failures = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> source in sourceCases)
            {
                if (ShouldCancelVerification()) break;
                Dictionary<string, object> liveCase = BuildLiveVerificationCase(source);
                Dictionary<string, object> row = RunPipelineTestCase(liveCase, "live");
                if (ReadBool(row, "passed", false)) passed++;
                else failures.Add(CompactVerificationFailure(row));
            }

            double score = sourceCases.Count == 0 ? 0d : (double)passed / sourceCases.Count;
            AddVerificationCheck(checks, "live_llm.varied_actions", "live_llm", sourceCases.Count > 0 && score >= 0.90d, "Live production-gate action score: " + passed + "/" + sourceCases.Count + " (" + (score * 100d).ToString("0.0", CultureInfo.InvariantCulture) + "%).", new Dictionary<string, object> { ["caseCap"] = cap, ["score"] = score, ["failures"] = failures });
        }

        private static void RunGameVerificationBridge(List<Dictionary<string, object>> checks, string sandbox, Dictionary<string, object> options)
        {
            EnsureVerificationDirectories();
            string commandId = "game-verify-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            Dictionary<string, object> command = new Dictionary<string, object>
            {
                ["commandId"] = commandId,
                ["command"] = "run",
                ["suite"] = "all",
                ["restoreDisposableSave"] = true,
                ["createdUtc"] = DateTime.UtcNow.ToString("o"),
                ["sandboxPath"] = sandbox
            };
            WriteJsonObject(VerificationGameCommandPath, command);
            int timeoutSeconds = Math.Max(30, ReadInt(options, "gameTimeoutSeconds", 900));
            Stopwatch wait = Stopwatch.StartNew();
            Dictionary<string, object> status = new Dictionary<string, object>();
            while (wait.Elapsed.TotalSeconds < timeoutSeconds && !ShouldCancelVerification())
            {
                if (File.Exists(VerificationGameStatusPath))
                {
                    status = ReadJsonObject(VerificationGameStatusPath);
                    if (ReadString(status, "commandId", "") == commandId)
                    {
                        string state = ReadString(status, "state", "");
                        if (state == "completed" || state == "failed" || state == "cancelled") break;
                    }
                }
                Thread.Sleep(1000);
            }
            bool passed = ReadString(status, "commandId", "") == commandId && ReadString(status, "state", "") == "completed" && ReadInt(status, "failed", 1) == 0;
            string summary = passed ? "Native Bannerlord game gauntlet completed without mechanical failures." : wait.Elapsed.TotalSeconds >= timeoutSeconds ? "Game tier timed out waiting for Bannerlord. Load the disposable verification save and leave the game running." : "Native game gauntlet did not complete cleanly.";
            AddVerificationCheck(checks, "game.native_gauntlet", "game", passed, summary, new Dictionary<string, object> { ["command"] = command, ["status"] = status, ["waitMs"] = wait.ElapsedMilliseconds }, passed ? "passed" : "blocked");
        }

        private static string WriteVerificationGameCancelCommand(string reason)
        {
            EnsureVerificationDirectories();
            string commandId = "game-cancel-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            WriteJsonObject(VerificationGameCommandPath, new Dictionary<string, object>
            {
                ["commandId"] = commandId,
                ["command"] = "cancel",
                ["createdUtc"] = DateTime.UtcNow.ToString("o"),
                ["reason"] = reason ?? string.Empty
            });
            return commandId;
        }

        private static Dictionary<string, object> BuildVerificationCoverageManifest()
        {
            string root = FindVerificationSourceRoot();
            string clientRoot = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "ReignBeta");
            string serverRoot = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "ReignServer");
            List<Dictionary<string, object>> features = new List<Dictionary<string, object>>();
            Action<string, string, string[]> add = (id, suite, tests) => features.Add(new Dictionary<string, object>
            {
                ["id"] = id,
                ["implemented"] = true,
                ["suite"] = suite,
                ["tests"] = tests,
                ["dimensions"] = new[] { "success", "failure", "persistence", "recovery" }
            });

            add("server.lifecycle", "server_lifecycle", new[] { "contracts.http_routes", "pipeline.campaign_backups", "pipeline.save_sync", "faults.interrupted_write" });
            add("characters.construction", "character_construction", new[] { "pipeline.character_profiles", "pipeline.character_editor" });
            add("characters.narrative", "character_narrative", new[] { "contracts.character_narrative" });
            add("world.rulers", "rulers", new[] { "contracts.rulers" });
            add("characters.npc_lives", "npc_lives", new[] { "contracts.npc_lives" });
            add("knowledge.identity", "knowledge_identity", new[] { "pipeline.identity", "pipeline.rumors" });
            add("memory.relationships", "memory_relationships", new[] { "pipeline.mbti_relationship_lifecycle", "pipeline.categorized_memory", "pipeline.semantic_memory" });
            add("memory.mbti_relationship_lifecycle", "memory_relationships", new[] { "contracts.mbti_relationship_lifecycle", "pipeline.mbti_relationship_lifecycle" });
            add("conversation.individual", "conversation_modes", new[] { "pipeline.canned_cases", "contracts.dialogue_prompt_budget" });
            add("conversation.party", "conversation_modes", new[] { "pipeline.canned_cases", "pipeline.party_dialogue_audit", "contracts.gui_xml" });
            add("conversation.correspondence", "conversation_modes", new[] { "pipeline.canned_cases", "contracts.gui_xml" });
            add("events.social_wilderness_tournament", "conversation_modes", new[] { "pipeline.canned_cases", "contracts.gui_xml", "contracts.social_event_player_exclusion", "contracts.social_event_encyclopedia_handoff", "contracts.social_event_local_first_roster", "contracts.social_event_persistent_group", "contracts.social_event_group_turn" });
            add("prompts.efficiency_cache", "prompt_efficiency", new[] { "contracts.dialogue_prompt_budget", "pipeline.prompt_caching" });
            add("memory.precision", "memory_precision", new[] { "pipeline.memory_precision" });
            add("portraits.images", "portraits_images", new[] { "contracts.gui_xml", "contracts.portrait_identity_metadata", "contracts.image_generation_profiles", "faults.corrupt_image" });
            add("chat_view", "portraits_images", new[] { "contracts.chat_view" });
            add("actions.trade", "actions_trade", new[] { "pipeline.canned_cases", "pipeline.settlement_authority", "shadow_world.all_actions", "shadow_world.atomic_trade" });
            add("diplomacy.world", "world_diplomacy", new[] { "baseline.diplomacy", "contracts.random_diplomacy_popup_debug", "long_run.five_years" });
            add("history.world", "world_history", new[] { "baseline.world_history", "pipeline.rumors" });
            add("rebellion.lifecycle", "rebellion", new[] { "baseline.rebellion", "rebellion.lifecycle_matrix", "rebellion.save_reload", "long_run.five_years" });
            add("government.cultural_people_pressure", "government", new[] { "contracts.government", "game.government_feature_harness" });
            add("ui.contracts", "ui_contracts", new[] { "contracts.gui_xml", "game.native_gauntlet" });
            add("calendar.126_day", "contracts", new[] { "contracts.calendar_126", "long_run.five_years" });
            add("audit.result_objects", "actions_trade", new[] { "pipeline.canned_cases", "shadow_world.all_actions" });
            add("campaign.backup_isolation", "server_lifecycle", new[] { "pipeline.campaign_backups", "long_run.five_years" });
            add("campaign.save_sync", "server_lifecycle", new[] { "pipeline.save_sync", "contracts.http_routes" });

            foreach (string action in DiscoverClientActionNames(clientRoot).Where(x => x != "Unknown"))
            {
                add("action." + ToSnakeCase(action), "actions_trade", new[] { "pipeline.canned_cases", "shadow_world.all_actions" });
            }

            List<string> behaviors = new List<string>();
            string subModuleText = VerificationSourceLocator.ReadClientTypeSourceText(clientRoot, "ReignBeta.SubModule");
            if (subModuleText.Length > 0) behaviors = Regex.Matches(subModuleText, "new\\s+([A-Za-z0-9_]+CampaignBehavior)\\s*\\(").Cast<Match>().Select(x => x.Groups[1].Value).Where(x => !IsExcludedCourtVerificationArtifact(x)).Distinct().OrderBy(x => x).ToList();
            List<string> routes = new List<string>();
            // Exact routes come from the registered route table (RouteTable.cs), not from the router's source text.
            routes = ServerRoutes.Value.Routes.Where(route => route.Match == RouteMatch.Exact).Select(route => route.Path).Where(x => !x.StartsWith("/court/", StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(x => x).ToList();
            List<string> guiMovies = string.IsNullOrWhiteSpace(clientRoot) || !Directory.Exists(Path.Combine(clientRoot, "GUI", "Prefabs")) ? new List<string>() : Directory.GetFiles(Path.Combine(clientRoot, "GUI", "Prefabs"), "*.xml").Where(path => !IsExcludedCourtVerificationArtifact(path)).Select(Path.GetFileNameWithoutExtension).OrderBy(x => x).ToList();
            List<string> prompts = PromptFileNames.Where(x => !IsExcludedCourtVerificationArtifact(x)).OrderBy(x => x).ToList();
            List<string> uncovered = features.Where(row => ReadBool(row, "implemented", false) && ReadStringArray(row, "tests").Count == 0).Select(row => ReadString(row, "id", "")).ToList();
            return new Dictionary<string, object>
            {
                ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["sourceRoot"] = root,
                ["features"] = features,
                ["featureCount"] = features.Count,
                ["uncoveredImplementedFeatures"] = uncovered,
                ["behaviors"] = behaviors,
                ["routes"] = routes,
                ["prompts"] = prompts,
                ["guiMovies"] = guiMovies,
                ["settings"] = DiscoverSettings(clientRoot).Where(x => !IsExcludedCourtVerificationArtifact(x)).ToList(),
                ["saveTypes"] = DiscoverSaveTypes(clientRoot).Where(x => !IsExcludedCourtVerificationArtifact(x)).ToList()
            };
        }

        private static void AddCoverageChecks(List<Dictionary<string, object>> checks, Dictionary<string, object> coverage, string requestedSuite)
        {
            if (!ShouldRunVerificationSuite(requestedSuite, "coverage")) return;
            List<string> uncovered = ReadStringArray(coverage, "uncoveredImplementedFeatures");
            int featureCount = ReadInt(coverage, "featureCount", 0);
            AddVerificationCheck(checks, "coverage.implemented_features", "coverage", featureCount > 0 && uncovered.Count == 0, featureCount + " implemented features have assigned verification coverage.", new Dictionary<string, object> { ["uncovered"] = uncovered });
            AddVerificationCheck(checks, "coverage.runtime_inventory", "coverage", ReadStringArray(coverage, "routes").Count >= 60 && ReadStringArray(coverage, "prompts").Count >= 60 && ReadStringArray(coverage, "guiMovies").Count >= 5, "Runtime route, prompt, and GUI inventories were generated.", new Dictionary<string, object> { ["routeCount"] = ReadStringArray(coverage, "routes").Count, ["promptCount"] = ReadStringArray(coverage, "prompts").Count, ["guiMovieCount"] = ReadStringArray(coverage, "guiMovies").Count });
        }

        private static void AddSelfTestSummary(List<Dictionary<string, object>> checks, string id, string suite, Dictionary<string, object> result)
        {
            bool passed = ReadBool(result, "passed", ReadBool(result, "ok", false));
            List<Dictionary<string, object>> rows = ReadDictionaryList(result, "tests");
            if (rows.Count == 0) rows = ReadDictionaryList(result, "assertions");
            if (rows.Count == 0) rows = ReadDictionaryList(result, "results");
            int total = ReadInt(result, "totalCount", ReadInt(result, "total", rows.Count));
            int passedCount = ReadInt(result, "passedCount", rows.Count > 0 ? rows.Count(CheckPassed) : passed ? total : 0);
            AddVerificationCheck(checks, id, suite, passed, id + ": " + passedCount + "/" + total + " baseline checks passed.", CompactSelfTest(result));
        }

        private static void AddSubsystemListSummary(List<Dictionary<string, object>> checks, string id, string suite, List<Dictionary<string, object>> rows, int caseResultLimit=0)
        {
            rows = rows ?? new List<Dictionary<string, object>>();
            int passed = rows.Count(CheckPassed);
            var evidence=new Dictionary<string,object> { ["failures"] = rows.Where(x => !CheckPassed(x)).Select(CompactVerificationFailure).ToList() };
            if(caseResultLimit>0)
            {
                if(caseResultLimit>512)throw new ArgumentOutOfRangeException(nameof(caseResultLimit));
                var ids=new HashSet<string>(StringComparer.Ordinal);
                bool invalid=false;
                var caseResults=new List<Dictionary<string,object>>();
                foreach(var row in rows.Take(caseResultLimit))
                {
                    string caseId=ReadFirstString(row,"caseId","id","name");
                    bool valid=caseId.Length>0 && caseId.Length<=192 && caseId.All(c=>char.IsLetterOrDigit(c)||c=='_'||c=='.'||c=='-'||c==':') && ids.Add(ReadString(row,"suite","")+"\0"+caseId);
                    invalid|=!valid;
                    caseResults.Add(new Dictionary<string,object>{{"caseId",valid?caseId:""},{"passed",CheckPassed(row)}});
                }
                evidence["caseResults"]=caseResults;
                evidence["caseResultsTotal"]=rows.Count;
                evidence["caseResultsTruncated"]=rows.Count>caseResultLimit;
                evidence["caseResultsInvalidIds"]=invalid;
            }
            AddVerificationCheck(checks, id, suite, rows.Count > 0 && passed == rows.Count, id + ": " + passed + "/" + rows.Count + " checks passed.", evidence);
        }

        private static Dictionary<string, object> CompactSelfTest(Dictionary<string, object> result)
        {
            Dictionary<string, object> compact = new Dictionary<string, object>();
            foreach (string key in new[] { "ok", "passed", "passedCount", "failedCount", "totalCount", "total", "summary" })
            {
                if (result != null && result.TryGetValue(key, out object value)) compact[key] = value;
            }
            List<Dictionary<string, object>> tests = ReadDictionaryList(result, "tests");
            if (tests.Count == 0) tests = ReadDictionaryList(result, "assertions");
            if (tests.Count == 0) tests = ReadDictionaryList(result, "results");
            if (tests.Count > 0) compact["failures"] = tests.Where(x => !CheckPassed(x)).Select(CompactVerificationFailure).ToList();
            return compact;
        }

        private static Dictionary<string, object> CompactVerificationFailure(Dictionary<string, object> row)
        {
            return new Dictionary<string, object>
            {
                ["suite"] = ReadString(row, "suite", ""),
                ["caseId"] = ReadFirstString(row, "caseId", "id", "name"),
                ["summary"] = ReadFirstString(row, "summary", "error", "message"),
                ["runId"] = ReadString(row, "runId", "")
            };
        }

        private static int CountLegacyPromptOverrideCases(List<Dictionary<string, object>> cases)
        {
            return (cases ?? new List<Dictionary<string, object>>()).Count(row => Json.Serialize(row).IndexOf("prompt_override", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static Dictionary<string, object> ClassifySyntheticProviderFault(string fault)
        {
            bool retryable = fault == "timeout" || fault == "429" || fault == "500" || fault == "vector_unavailable";
            return new Dictionary<string, object>
            {
                ["fault"] = fault,
                ["retryable"] = retryable,
                ["terminal"] = !retryable,
                ["failureKind"] = retryable ? "transient_provider" : fault == "401" ? "authentication" : fault == "context_overflow" ? "prompt_size" : fault == "corrupt_image" ? "image_decode" : "invalid_provider_response",
                ["maxAttempts"] = retryable ? 2 : 1
            };
        }

        private static Dictionary<string, object> BuildLiveVerificationCase(Dictionary<string, object> source)
        {
            Dictionary<string, object> testCase = CloneDictionary(source);
            testCase["suite"] = "verification_live_llm";
            testCase["caseId"] = "live_" + ReadString(source, "caseId", Guid.NewGuid().ToString("N"));
            testCase["mode"] = "dialogue";
            testCase["llm"] = new Dictionary<string, object> { ["mode"] = "live" };
            Dictionary<string, object> input = ReadDictionary(testCase, "input") ?? new Dictionary<string, object>();
            string text = ReadFirstString(input, "playerText", "text", "message").Replace("@", "");
            input["playerText"] = text;
            testCase["input"] = input;
            return testCase;
        }

        private static Dictionary<string, object> NormalizeVerificationOptions(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string requestedTier =
                ReadString(payload, "tier", "offline").ToLowerInvariant();
            string tier = requestedTier;
            if (tier == "commit") tier = "quick";
            else if (tier == "full-regression") tier = "offline";
            else if (tier == "release-candidate") tier = "all";
            else if (tier == "exhaustive-final-review") tier = "offline";
            else if (tier == "human-immersion") tier = "quick";
            if (!(new[] { "quick", "offline", "live-llm", "game", "all" }).Contains(tier)) tier = "offline";
            return new Dictionary<string, object>
            {
                ["tier"] = tier,
                ["requestedTier"] = requestedTier,
                ["finalGauntletHandoff"] =
                    requestedTier == "exhaustive-final-review"
                        ? "Run ReignLiveTest.exe gauntlet start after this deterministic prerequisite passes."
                        : requestedTier == "human-immersion"
                            ? "Export the completed gauntlet review pack and score it blindly."
                            : "",
                ["suite"] = ReadString(payload, "suite", ""),
                ["codexPerformance"] = ReadDictionary(payload, "codexPerformance"),
                ["seed"] = ReadInt(payload, "seed", 1337),
                ["repeat"] = Math.Max(1, ReadInt(payload, "repeat", 1)),
                ["failFast"] = ReadBool(payload, "failFast", false),
                ["liveCaseCap"] = Math.Max(1, Math.Min(
                    string.Equals(ReadString(payload, "suite", ""), "campaign_command", StringComparison.OrdinalIgnoreCase)
                        ? 600 : 100,
                    ReadInt(payload, "liveCaseCap", 20))),
                ["gameTimeoutSeconds"] = Math.Max(30, ReadInt(payload, "gameTimeoutSeconds", 900)),
                ["replayOf"] = ReadString(payload, "replayOf", "")
            };
        }

        internal static void AddVerificationCheck(List<Dictionary<string, object>> checks, string id, string suite, bool passed, string summary, Dictionary<string, object> data, string status = null)
        {
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                ["id"] = id,
                ["suite"] = suite,
                ["passed"] = passed,
                ["status"] = status ?? (passed ? "passed" : "failed"),
                ["summary"] = summary ?? "",
                ["timestampUtc"] = DateTime.UtcNow.ToString("o"),
                ["data"] = data ?? new Dictionary<string, object>()
            };
            checks.Add(row);
            int passedCount = checks.Count(CheckPassed);
            SetVerificationStatus(ReadString(VerificationStatus, "runId", ""), "running", summary, passedCount, checks.Count - passedCount, checks.Count);
        }

        internal static bool CheckPassed(Dictionary<string, object> row)
        {
            return ReadBool(row, "passed", false);
        }

        private static bool ShouldStop(List<Dictionary<string, object>> checks, bool failFast)
        {
            return ShouldCancelVerification() || (failFast && checks.Any(x => !CheckPassed(x)));
        }

        internal static bool ShouldCancelVerification()
        {
            return VerificationCancelRequested || File.Exists(VerificationCancelPath);
        }

        private static bool ShouldRunVerificationSuite(string requested, string suite)
        {
            if (string.IsNullOrWhiteSpace(requested)) return true;
            return requested.Equals(suite, StringComparison.OrdinalIgnoreCase)
                || (requested.Equals("rebellion_system", StringComparison.OrdinalIgnoreCase) && suite.Equals("rebellion", StringComparison.OrdinalIgnoreCase))
                || (requested.Equals("actions", StringComparison.OrdinalIgnoreCase) && suite.Equals("shadow_world", StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object> NewVerificationStatus(string state, string message)
        {
            return new Dictionary<string, object>
            {
                ["runId"] = "",
                ["state"] = state,
                ["message"] = message,
                ["passedCount"] = 0,
                ["failedCount"] = 0,
                ["totalCount"] = 0,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            };
        }

        private static void SetVerificationStatus(string runId, string state, string message, int passed, int failed, int total)
        {
            lock (VerificationLock)
            {
                VerificationStatus = new Dictionary<string, object>
                {
                    ["runId"] = runId ?? "",
                    ["state"] = state ?? "",
                    ["message"] = message ?? "",
                    ["passedCount"] = passed,
                    ["failedCount"] = failed,
                    ["totalCount"] = total,
                    ["cancelRequested"] = VerificationCancelRequested,
                    ["updatedUtc"] = DateTime.UtcNow.ToString("o")
                };
            }
        }

        private static void EnsureVerificationDirectories()
        {
            Directory.CreateDirectory(VerificationDir);
            Directory.CreateDirectory(VerificationRunsDir);
            Directory.CreateDirectory(VerificationFailuresDir);
            Directory.CreateDirectory(VerificationSandboxesDir);
        }

        private static void SaveVerificationResult(Dictionary<string, object> result)
        {
            EnsureVerificationDirectories();
            string runId = SafePathSegment(ReadString(result, "runId", "verification"), "verification");
            bool passed = ReadBool(result, "passed", false);
            string directory = passed ? VerificationRunsDir : VerificationFailuresDir;
            string path = Path.Combine(directory, runId + ".json");
            if (!passed)
            {
                result["replayBundle"] = new Dictionary<string, object>
                {
                    ["version"] = 1,
                    ["runId"] = runId,
                    ["options"] = ReadDictionary(result, "options") ?? new Dictionary<string, object>(),
                    ["failedChecks"] = ReadDictionaryList(result, "checks").Where(x => !CheckPassed(x)).ToList(),
                    ["sandboxPath"] = ReadString(result, "sandboxPath", "")
                };
            }
            WriteJsonObject(path, result);
            PruneSuccessfulVerificationRuns();
        }

        private static void PruneSuccessfulVerificationRuns()
        {
            foreach (string path in Directory.GetFiles(VerificationRunsDir, "*.json", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc).Skip(20))
            {
                TryDelete(path);
            }
            HashSet<string> retained = new HashSet<string>(Directory.GetFiles(VerificationRunsDir, "*.json").Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
            retained.UnionWith(Directory.GetFiles(VerificationFailuresDir, "*.json").Select(Path.GetFileNameWithoutExtension));
            retained.UnionWith(VerificationReferencedSandboxNames(VerificationSandboxesDir,
                Directory.GetFiles(VerificationRunsDir, "*.json").Concat(Directory.GetFiles(VerificationFailuresDir, "*.json"))
                    .Select(ReadJsonObject)));
            foreach (string path in Directory.GetDirectories(VerificationSandboxesDir))
            {
                if (!retained.Contains(Path.GetFileName(path))
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0
                    && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(VerificationSandboxesDir), StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(path, true); } catch { }
                }
            }
        }

        internal static HashSet<string> VerificationReferencedSandboxNames(string root, IEnumerable<Dictionary<string, object>> reports)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var report in reports)
            {
                string candidate = ReadString(report, "sandboxPath", "");
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    string full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(Path.GetDirectoryName(full), fullRoot, StringComparison.OrdinalIgnoreCase))
                        names.Add(Path.GetFileName(full));
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
            }
            return names;
        }

        private static string FindVerificationRunPath(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return "";
            string safe = SafePathSegment(runId, "");
            string success = Path.Combine(VerificationRunsDir, safe + ".json");
            if (File.Exists(success)) return success;
            string failure = Path.Combine(VerificationFailuresDir, safe + ".json");
            return File.Exists(failure) ? failure : "";
        }

        internal static string FindVerificationSourceRoot()
        {
            // The build snapshot is the evidence for this binary. Prefer it over
            // an ancestor checkout, which may have changed or use source junctions.
            string packagedContracts = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification_contracts", "workspace");
            if (Directory.Exists(Path.Combine(packagedContracts, "ReignBeta")) && Directory.Exists(Path.Combine(packagedContracts, "ReignServer"))) return packagedContracts;
            string configured = Environment.GetEnvironmentVariable("REIGN_SOURCE_ROOT") ?? "";
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(Path.Combine(configured, "ReignBeta")) && Directory.Exists(Path.Combine(configured, "ReignServer"))) return Path.GetFullPath(configured);
            Dictionary<string, object> settings = LoadSettings();
            configured = ReadString(settings, "verificationSourceRoot", "");
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(Path.Combine(configured, "ReignBeta")) && Directory.Exists(Path.Combine(configured, "ReignServer"))) return Path.GetFullPath(configured);

            DirectoryInfo cursor = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; cursor != null && i < 8; i++, cursor = cursor.Parent)
            {
                if (Directory.Exists(Path.Combine(cursor.FullName, "ReignBeta")) && Directory.Exists(Path.Combine(cursor.FullName, "ReignServer"))) return cursor.FullName;
            }
            return "";
        }

        private static List<string> DiscoverClientActionNames(string clientRoot)
        {
            string enumText = VerificationSourceLocator.ReadClientTypeSourceText(clientRoot, "ReignWorldActionType");
            return enumText.Length == 0 ? ActionNamesFallback() : ParseActionNames(enumText);
        }

        internal static List<string> DiscoverActionNames(string enumPath)
        {
            if (!File.Exists(enumPath)) return ActionNamesFallback();
            return ParseActionNames(File.ReadAllText(enumPath));
        }

        private static List<string> ActionNamesFallback()
        {
            return ActionPromptFileNames.Select(PromptFileToActionFallback).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        }

        private static List<string> ParseActionNames(string enumText)
        {
            Match body = Regex.Match(enumText, "enum\\s+ReignWorldActionType\\s*\\{(?<body>[\\s\\S]*?)\\}");
            if (!body.Success) return new List<string>();
            return Regex.Matches(body.Groups["body"].Value, "^\\s*([A-Za-z][A-Za-z0-9_]*)\\s*=", RegexOptions.Multiline).Cast<Match>().Select(x => x.Groups[1].Value).Distinct().ToList();
        }

        private static string PromptFileToActionFallback(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file ?? "").Replace("action_", "");
            return string.Concat(name.Split('_').Select(part => part.Length == 0 ? "" : char.ToUpperInvariant(part[0]) + part.Substring(1)));
        }

        private static List<string> DiscoverSettings(string clientRoot)
        {
            string settingsText = VerificationSourceLocator.ReadClientTypeSourceText(clientRoot, "ReignBetaSettings");
            if (settingsText.Length == 0) return new List<string>();
            return Regex.Matches(settingsText, "public\\s+[A-Za-z0-9_<>?]+\\s+([A-Za-z0-9_]+)\\s*\\{").Cast<Match>().Select(x => x.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
        }

        private static List<string> DiscoverSaveTypes(string clientRoot)
        {
            string saveDefinerText = VerificationSourceLocator.ReadClientTypeSourceText(clientRoot, "ReignBetaSaveDefiner");
            if (saveDefinerText.Length == 0) return new List<string>();
            return Regex.Matches(saveDefinerText, "typeof\\(([A-Za-z0-9_]+)\\)").Cast<Match>().Select(x => x.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
        }

        private static bool IsExcludedCourtVerificationArtifact(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.IndexOf("Court", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("AmbassadorPosting", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("IntelligenceOperation", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static List<string> ReadStringArray(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null) return new List<string>();
            if (value is string text) return new List<string> { text };
            if (value is IEnumerable enumerable) return enumerable.Cast<object>().Where(x => x != null).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture) ?? "").Where(x => x.Length > 0).ToList();
            return new List<string>();
        }

        private static string ToSnakeCase(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";
            return Regex.Replace(value, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
        }
    }
}
