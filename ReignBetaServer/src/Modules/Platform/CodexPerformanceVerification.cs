using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string CodexPerformanceUsageConfirmation = "run bounded Codex performance comparison";

        private static Dictionary<string, object> ValidateCodexPerformanceRun(Dictionary<string, object> options)
        {
            var configuration = ReadDictionary(options, "codexPerformance");
            var plan = BuildCodexPerformancePlan(configuration);
            if (ReadString(options, "tier", "") != "live-llm")
                throw new ArgumentException("Codex performance provider comparisons require the explicit live-llm tier.");
            if (ReadString(configuration, "confirmation", "") != CodexPerformanceUsageConfirmation)
                throw new ArgumentException("Codex performance requires explicit usage confirmation: " + CodexPerformanceUsageConfirmation);
            int cap = ReadInt(plan, "totalProviderCallCap", 0);
            if (cap < 1 || cap > 1000) throw new ArgumentException("totalProviderCallCap must be between 1 and 1000; it includes every physical turn start.");
            var settings = LoadSettings();
            if (!UsesCodexSubscription(settings)) throw new ArgumentException("Select Codex before running codex_performance; the suite cannot switch providers.");
            string validationId = ReadString(configuration, "validationRunId", "");
            if (!Regex.IsMatch(validationId, "^[0-9]{8}-[0-9]{6}-[a-f0-9]{8}$"))
                throw new ArgumentException("An exact successful Release validationRunId is required for artifact provenance.");
            string workspace = ReadString(configuration, "validationWorkspaceRoot", FindVerificationSourceRoot());
            if (string.IsNullOrWhiteSpace(workspace) || !File.Exists(Path.Combine(workspace, "reign.repository.json"))
                || !Directory.Exists(Path.Combine(workspace, "ReignBetaServer")))
                throw new ArgumentException("The validation workspace must be the exact Reign source workspace containing the successful report.");
            string validationRoot = Path.Combine(Path.GetFullPath(workspace), ".codex-build", "reign-mcp", "validation", validationId);
            string reportPath = Path.Combine(validationRoot, "validation-report.json");
            var validation = ReadJsonObject(reportPath);
            string validatedServer = Path.Combine(validationRoot, "server", "out", "ReignBetaServer.exe");
            string runningServer = typeof(Program).Assembly.Location;
            if (!ReadBool(validation, "Ok", false) || ReadString(ReadDictionary(validation, "Plan"), "Configuration", "") != "Release"
                || !File.Exists(validatedServer) || FileSha256(validatedServer) != FileSha256(runningServer))
                throw new ArgumentException("The running server must match the selected successful Release validation artifact.");
            plan["sourceFingerprint"] = ReadString(validation, "SourceFingerprintSha256", "unknown");
            plan["validationFingerprint"] = FileSha256(reportPath);
            plan["validationRunId"] = validationId;
            plan["serverArtifactFingerprint"] = FileSha256(runningServer);
            string runtimeExecutable = ReadString(settings, "codexExecutable", "");
            plan["runtimeExecutableFingerprint"] = File.Exists(runtimeExecutable) ? FileSha256(runtimeExecutable) : "unknown";
            return plan;
        }

        private static void RunCodexPerformanceLiveVerification(List<Dictionary<string, object>> checks, string sandbox, Dictionary<string, object> options)
        {
            var plan = ValidateCodexPerformanceRun(options);
            var budget = ActiveCodexPerformanceBudget.Value;
            if (budget == null) throw new InvalidOperationException("A run-wide Codex provider-call budget is required.");
            string model = ReadString(plan, "model", "");
            var selectedIds = ReadStringList(plan, "caseIds");
            var allCases = LoadTestCases("");
            var selected = new List<Dictionary<string, object>>();
            foreach (string id in selectedIds)
            {
                var matches = allCases.Where(row => ReadString(row, "caseId", "") == id).ToList();
                if (matches.Count != 1) throw new ArgumentException("Case ID must resolve uniquely before generation: " + id);
                string mode = ReadString(matches[0], "mode", "dialogue");
                if (!new[] { "dialogue", "social_event", "party_chat", "castle_chat" }.Contains(mode))
                    throw new ArgumentException("This production comparison route requires an individual or group conversation fixture: " + id);
                var snapshot = ResolveTestSnapshot(matches[0]);
                var casePayload = BuildTestPayload(matches[0], snapshot, ReadDictionary(matches[0], "input") ?? new Dictionary<string, object>());
                string speakerId = ReadFirstString(casePayload, "heroStringId", "speakerHeroStringId", "heroId");
                var stack = ReadDictionary(snapshot, "characteristics") ?? snapshot;
                var prepared = ReadDictionary(snapshot, "constructed") ?? ReadDictionary(stack, "constructed");
                var traits = ReadDictionary(snapshot, "traits") ?? ReadDictionary(stack, "traits");
                var snapshotHero = ReadDictionary(casePayload, "hero") ?? ReadDictionary(casePayload, "speaker") ?? ReadDictionary(snapshot, "profile");
                bool narrativeRequired = ReadInt(snapshotHero, "narrativeVersion", 0) >= 5
                    || ReadString(prepared, "narrativeSchema", "") == NarrativeSchema;
                if (!LoadCharacterProfileLibrary().ContainsKey(speakerId)
                    && (!IsCharacterConstructionReady(prepared) || !TraitDocumentReady(traits)
                        || !ReadBool(prepared, "llmUsed", false)
                        || (narrativeRequired && !NarrativeDocumentReady(ReadDictionary(snapshot, "narrative") ?? ReadDictionary(stack, "narrative")))))
                    throw new ArgumentException("Case " + id + " requires a shipped character or an explicitly completed character snapshot with constructed and traits documents; comparison runs do not author fixture personalities.");
                selected.Add(matches[0]);
            }
            var originalSettings = LoadSettings();
            var samples = new List<Dictionary<string, object>>();
            int expectedSamples = selected.Sum(source => Math.Max(1, ReadDictionaryList(ReadDictionary(source, "input"), "turns").Count)) * 2;
            string runToken = Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                foreach (var source in selected)
                {
                    var snapshot = ResolveTestSnapshot(source);
                    var input = ReadDictionary(source, "input") ?? new Dictionary<string, object>();
                    for (int arm = 0; arm < 2; arm++)
                    {
                        if (ShouldCancelVerification() || budget.Used >= budget.Maximum) break;
                        string variant = arm == 0 ? "baseline" : "variant";
                        var settings = DeepCloneProfileDictionary(originalSettings);
                        settings["codexOptions"] = DeepCloneProfileDictionary(ReadDictionary(plan, variant == "baseline" ? "baselineOptions" : "variantOptions"));
                        NormalizeCodexOptions(settings);
                        foreach (string key in ChatModelKeys) settings[key] = model;
                        CodexPerformanceSettings.Value = settings;
                        var payload = BuildTestPayload(source, snapshot, input);
                        string campaignId = "codexperf_" + runToken + "_" + samples.Count;
                        payload["campaignId"] = campaignId;
                        payload["correlationId"] = "codexperf_" + Guid.NewGuid().ToString("N");
                        payload["conversationSessionId"] = "comparison_session";
                        string heroId = ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId");
                        if (string.IsNullOrWhiteSpace(heroId)) throw new ArgumentException("Comparison case is missing speaker identity.");
                        var hero = DeepCloneProfileDictionary(ReadDictionary(payload, "hero") ?? ReadDictionary(payload, "speaker") ?? ReadDictionary(snapshot, "hero") ?? new Dictionary<string, object>());
                        hero["heroStringId"] = heroId;
                        payload["hero"] = hero;
                        // Seed each arm independently. This runs beneath Verification Lab's isolated campaign root.
                        if (LoadCharacterProfileLibrary().ContainsKey(heroId)) MaterializeCharacterProfile(campaignId, heroId, hero, false, false);
                        else ConstructCharacterFiles(campaignId, heroId, hero, true, false, "codex_performance_fixture");
                        MarkCharacterConstructionCompletedOnInteraction(campaignId, heroId, "codex_performance_fixture");
                        var characterStack = ReadDictionary(snapshot, "characteristics") ?? snapshot;
                        foreach (var mapping in new[] { new[] { "profile", "profile.json" }, new[] { "state", "state.json" },
                            new[] { "relationships", "memory/relationships.json" }, new[] { "memorySummary", "memory/summary.json" },
                            new[] { "traits", "traits.json" }, new[] { "background", "background.json" }, new[] { "voice", "voice.json" },
                            new[] { "motivations", "motivations.json" }, new[] { "secrets", "secrets.json" },
                            new[] { "hiddenHistory", "hidden_history.json" }, new[] { "saveQuirks", "save_quirks.json" },
                            new[] { "narrative", "narrative.json" }, new[] { "constructed", "constructed.json" } })
                        {
                            var document = ReadDictionary(snapshot, mapping[0]) ?? ReadDictionary(characterStack, mapping[0]);
                            if (document != null) WriteJsonObject(CharacterFile(campaignId, heroId, mapping[1]), DeepCloneProfileDictionary(document));
                        }
                        foreach (var memory in ReadDictionaryList(snapshot, "memories"))
                            AppendJsonLineToPath(CharacterFile(campaignId, heroId, "memory", "memories.jsonl"), memory);
                        using (var connection = OpenCampaignConnection(campaignId))
                        {
                            int ordinal = 0;
                            foreach (var line in ReadDictionaryList(snapshot, "priorLines"))
                            {
                                ordinal++;
                                InsertConversationTurn(connection, "fixture_" + ordinal, "comparison_session", "", ordinal, "fixture_exchange",
                                    ReadString(line, "role", "assistant"), ReadString(line, "speakerId", heroId), ReadString(line, "speaker", heroId),
                                    ReadString(line, "text", ""), ReadString(line, "channel", "in_person"), ReadDouble(line, "worldDay", 0),
                                    ReadLong(line, "ts", ordinal), ReadString(line, "locationId", ""), new List<string> { heroId }, line);
                            }
                        }
                        InvalidateCodexConversationTimeline(campaignId);
                        var turns = ReadDictionaryList(input, "turns");
                        if (turns.Count == 0) turns.Add(new Dictionary<string, object>());
                        for (int turnIndex = 0; turnIndex < turns.Count; turnIndex++)
                        {
                        if (ShouldCancelVerification() || budget.Used >= budget.Maximum) break;
                        var turnPayload = DeepCloneProfileDictionary(payload);
                        foreach (string key in new[] { "playerText", "text", "message", "sceneContext", "worldDay" })
                            if (turns[turnIndex].ContainsKey(key)) turnPayload[key] = turns[turnIndex][key];
                        turnPayload["correlationId"] = "codexperf_" + Guid.NewGuid().ToString("N");
                        int callsBefore = budget.Used;
                        var timer = Stopwatch.StartNew();
                        string mode = ReadString(source, "mode", "dialogue");
                        Dictionary<string, object> response = mode == "dialogue" ? DialogueRespond(turnPayload) : SocialEventRespond(turnPayload);
                        timer.Stop();
                        var diagnostics = ReadDictionary(response, "codexDiagnostics") ?? new Dictionary<string, object>();
                        var sample = new Dictionary<string, object> {
                            ["caseId"] = ReadString(source, "caseId", ""), ["variant"] = variant, ["model"] = model,
                            ["turnIndex"] = turnIndex,
                            ["settingsFingerprint"] = PromptHash(Json.Serialize(ReadDictionary(settings, "codexOptions"))),
                            ["fixtureFingerprint"] = PromptHash(Json.Serialize(snapshot) + Json.Serialize(input)),
                            ["durationMs"] = timer.ElapsedMilliseconds, ["providerCalls"] = budget.Used - callsBefore,
                            ["outcome"] = ReadString(diagnostics, "outcome", "unusable_response"),
                            ["stages"] = ReadDictionary(diagnostics, "stages") ?? new Dictionary<string, object>(),
                            ["usage"] = ReadDictionary(diagnostics, "usage"),
                            ["diagnostics"] = diagnostics,
                            ["acceptedReply"] = ReadBool(response, "ok", false) ? ReadString(response, "reply", "") : "",
                            ["error"] = ReadString(response, "error", "")
                        };
                        samples.Add(sample);
                        }
                    }
                }
            }
            finally { CodexPerformanceSettings.Value = null; InvalidateCodexThreads("performance_comparison_completed"); }
            var actualCalls = samples.SelectMany(sample => ReadDictionaryList(ReadDictionary(sample, "diagnostics"), "calls")).ToList();
            var runtimeVersions = actualCalls.Select(call => ReadString(ReadDictionary(ReadDictionary(call, "runtime"), "protocol"), "runtimeVersion", "unknown")).Distinct().ToList();
            plan["runtimeVersion"] = runtimeVersions.Count == 1 ? runtimeVersions[0] : runtimeVersions.Count == 0 ? "unknown" : "mixed";
            plan["settingsFingerprint"] = PromptHash(Json.Serialize(new[] { ReadDictionary(plan, "baselineOptions"), ReadDictionary(plan, "variantOptions") }));
            plan["schemaFingerprint"] = actualCalls.Count == 0 ? "unknown" : PromptHash(Json.Serialize(actualCalls.Select(call => ReadString(call, "schemaFingerprint", "unknown")).ToList()));
            plan["promptFingerprint"] = actualCalls.Count == 0 ? "unknown" : PromptHash(Json.Serialize(actualCalls.Select(call => ReadDictionaryList(call, "promptSegments")).ToList()));
            var report = BuildCodexPerformanceReport(plan, samples, budget.Used);
            report["samples"] = samples;
            report["runtimeExecutableFingerprint"] = plan["runtimeExecutableFingerprint"];
            report["serverArtifactFingerprint"] = plan["serverArtifactFingerprint"];
            report["validationRunId"] = plan["validationRunId"];
            report["dispatches"] = budget.Calls.ToList();
            report["completedAllArms"] = samples.Count == expectedSamples;
            string reportPath = Path.Combine(sandbox, "codex-performance-" + runToken + ".json");
            WriteJsonObject(reportPath, report);
            AddVerificationCheck(checks, "codex_performance.comparison", "codex_performance", samples.Count == expectedSamples,
                "Codex comparison completed " + samples.Count + " samples; human roleplay acceptance remains required.",
                new Dictionary<string, object> { ["reportPath"] = reportPath, ["report"] = report });
        }

        private static Dictionary<string, object> RunCodexPerformanceIntegrationSelfTests()
        {
            var settings = new Dictionary<string, object> { ["llmProvider"] = CodexSubscriptionProvider, ["reasoningMode"] = "selective", ["reasoningEffort"] = "high" };
            NormalizeCodexOptions(settings);
            var options = ReadDictionary(settings, "codexOptions");
            bool defaults = !ReadBool(options, "fastMode", true) && CodexExperimentKeys.All(key => !ReadBool(options, key, true));
            settings["reasoningEffort"] = "low";
            NormalizeCodexOptions(settings);
            bool migratedOnce = ReadString(options, "reasoningEffort", "") == "high" && ReadString(settings, "reasoningEffort", "") == "low";
            bool selective = EffectiveCodexReasoningEffort(settings, new Dictionary<string, object>(), "dialogue") == "high"
                && EffectiveCodexReasoningEffort(settings, new Dictionary<string, object>(), "memory") == "low"
                && EffectiveCodexReasoningEffort(settings, new Dictionary<string, object>(), "dialogue_continuity_repair") == "none";
            var previous = ActiveCodexPerformanceBudget.Value;
            bool bounded = false;
            try
            {
                ActiveCodexPerformanceBudget.Value = new CodexPerformanceBudget { Maximum = 2 };
                ReserveCodexPerformanceCall(null); ReserveCodexPerformanceCall(null);
                try { ReserveCodexPerformanceCall(null); } catch (InvalidOperationException) { bounded = true; }
                bounded &= ActiveCodexPerformanceBudget.Value.Used == 2;
            }
            finally { ActiveCodexPerformanceBudget.Value = previous; }
            var production = RunCodexProductionPathSelfTests();
            return new Dictionary<string, object> { ["ok"] = defaults && migratedOnce && selective && bounded && ReadBool(production, "ok", false),
                ["defaultsOff"] = defaults, ["migrationOnce"] = migratedOnce, ["selectiveRouting"] = selective,
                ["physicalDispatchCap"] = bounded, ["production"] = production, ["providerCalls"] = 0 };
        }

        private static Dictionary<string, object> RunCodexProductionPathSelfTests()
        {
            var previousSettings = CodexPerformanceSettings.Value;
            var previousTransport = CodexRpcTransportOverride;
            var previousProtocol = CodexRuntimeProtocol;
            var checks = new List<Dictionary<string, object>>();
            Action<string, bool, object> add = (id, ok, evidence) => checks.Add(new Dictionary<string, object>
                { ["id"] = id, ["passed"] = ok, ["evidence"] = evidence });
            string campaign = "_codex_contract_" + Guid.NewGuid().ToString("N").Substring(0, 10);
            int calls = 0;
            var efforts = new List<string>();
            var tiers = new List<string>();
            var models = new List<string>();
            bool malformedFirst = false;
            bool ambiguousFailure = false;
            bool rejectFastOnce = false;
            var responseContract = new Dictionary<string, object> {
                ["reply"] = "Good evening.", ["emotion"] = "calm", ["intent"] = "greet", ["relationshipSignal"] = "unchanged",
                ["relationshipAssessments"] = new List<object>(),
                ["decisionBrief"] = new Dictionary<string, object> { ["facts"] = new List<string>(), ["goals"] = new List<string>(),
                    ["constraints"] = new List<string>(), ["decision"] = "Return the greeting.", ["confidence"] = 0.9 },
                ["politicalConduct"] = new Dictionary<string, object> { ["addressMode"] = "neutral", ["defianceTier"] = 0,
                    ["stance"] = "guarded_neutrality", ["appliedEvidenceKeys"] = new List<string>() },
                ["actionGate"] = new Dictionary<string, object> { ["needed"] = false, ["commitment"] = "roleplay_only",
                    ["intent"] = "", ["confidence"] = 1.0, ["reason"] = "A greeting requires no game action." }
            };
            try
            {
                CodexRuntimeProtocol = new CodexProtocolState { SupportsOutputSchema = true, SupportsFastMode = true,
                    FastModeField = "serviceTierForTurn", ThreadServiceTierField = "serviceTier", InspectionState = "offline_fixture" };
                CodexRpcTransportOverride = (method, parameters, timeout) =>
                {
                    if (method == "thread/start") return new Dictionary<string, object> { ["thread"] = new Dictionary<string, object> { ["id"] = "contract-thread-" + Guid.NewGuid().ToString("N") } };
                    if (method == "thread/delete" || method == "turn/interrupt") return new Dictionary<string, object>();
                    if (method != "turn/start") throw new InvalidOperationException("Unexpected offline transport method: " + method);
                    calls++;
                    efforts.Add(ReadString(parameters, "effort", ""));
                    tiers.Add(ReadString(parameters, "serviceTierForTurn", ""));
                    models.Add(ReadString(parameters, "model", ""));
                    if (rejectFastOnce) { rejectFastOnce = false; throw new CodexRpcException("turn/start", -32602, "Unsupported serviceTierForTurn fast."); }
                    if (ambiguousFailure) throw new TimeoutException("Injected serviceTierForTurn fast timeout after dispatch; generation state is unknown.");
                    string id = "contract-turn-" + Guid.NewGuid().ToString("N");
                    var waiter = GetOrCreateCodexTurn(id, ReadString(parameters, "threadId", ""));
                    string candidate = malformedFirst ? "{\"reply\":\"unfinished" : Json.Serialize(responseContract);
                    malformedFirst = false;
                    var complete = new Dictionary<string, object> { ["method"] = "item/completed", ["params"] = new Dictionary<string, object> {
                        ["turnId"] = id, ["threadId"] = ReadString(parameters, "threadId", ""),
                        ["item"] = new Dictionary<string, object> { ["id"] = "final", ["type"] = "agentMessage", ["phase"] = "final_answer", ["text"] = candidate } } };
                    waiter.Accept(complete); waiter.Accept(complete); // A repeated runtime event must not duplicate the response or state effects.
                    waiter.Accept(new Dictionary<string, object> { ["method"] = "turn/completed", ["params"] = new Dictionary<string, object> {
                        ["turnId"] = id, ["turn"] = new Dictionary<string, object> { ["id"] = id, ["status"] = "completed",
                            ["usage"] = new Dictionary<string, object> { ["inputTokens"] = 10, ["outputTokens"] = 8, ["totalTokens"] = 18 } } } });
                    return new Dictionary<string, object> { ["turn"] = new Dictionary<string, object> { ["id"] = id } };
                };
                var fixtureSettings = DeepCloneProfileDictionary(LoadSettings());
                fixtureSettings["llmProvider"] = CodexSubscriptionProvider;
                fixtureSettings["codexOptions"] = new Dictionary<string, object> { ["reasoningMode"] = "selective", ["reasoningEffort"] = "high" };
                NormalizeCodexOptions(fixtureSettings);
                foreach (string key in ChatModelKeys) fixtureSettings[key] = "gpt-offline-contract";
                CodexPerformanceSettings.Value = fixtureSettings;
                var request = new Dictionary<string, object> { ["campaignId"] = campaign, ["requestType"] = "dialogue", ["prompt"] = "Good evening.", ["correlationId"] = "primary" };
                int before = calls;
                var primary = ChatWithLlm(request);
                add("primary_single_generation", ReadBool(primary, "ok", false) && calls - before == 1 && efforts.Last() == "high", calls - before);
                ReadDictionary(fixtureSettings, "codexOptions")["fastMode"] = true;
                before = calls;
                var fast = ChatWithLlm(request);
                add("fast_changes_tier_only", ReadBool(fast, "ok", false) && calls - before == 1
                    && tiers.Last() == "fast" && efforts.Last() == "high" && models.Last() == "gpt-offline-contract", calls - before);
                rejectFastOnce = true; before = calls;
                var fallback = ChatWithLlm(request);
                add("explicit_fast_rejection_retries_same_model_and_effort", ReadBool(fallback, "ok", false) && calls - before == 2
                    && tiers[tiers.Count - 2] == "fast" && tiers.Last() == "default"
                    && efforts.Skip(efforts.Count - 2).All(effort => effort == "high")
                    && models.Skip(models.Count - 2).All(model => model == "gpt-offline-contract"), calls - before);
                request["requestType"] = "memory";
                var memory = ChatWithLlm(request);
                add("memory_policy_reaches_transport", ReadBool(memory, "ok", false) && efforts.Last() == "low", efforts.Last());
                request["requestType"] = "dialogue_continuity_repair";
                var repair = ChatWithLlm(request);
                add("repair_disabled_intent_reaches_transport", ReadBool(repair, "ok", false) && efforts.Last() == "none" && tiers.Last() == "fast", efforts.Last());
                request["requestType"] = "dialogue";
                malformedFirst = true; before = calls;
                var malformed = ChatWithLlm(request);
                var repaired = RetryMalformedStructuredResponse(malformed, request, campaign, "repair-contract", "dialogue", "fixture", "");
                add("format_repair_order_and_call_count", ReadBool(repaired, "ok", false) && StructuredResponseIsComplete(ReadString(repaired, "content", ""), "dialogue") && calls - before == 2,
                    new Dictionary<string, object> { ["calls"] = calls - before, ["repairMarkers"] = repaired.Keys.Where(key => key.IndexOf("repair", StringComparison.OrdinalIgnoreCase) >= 0).ToList() });
                var outerBudget = ActiveCodexPerformanceBudget.Value;
                before = calls;
                try
                {
                    ActiveCodexPerformanceBudget.Value = new CodexPerformanceBudget { Maximum = 2 };
                    var firstBounded = ChatWithLlm(request);
                    var secondBounded = ChatWithLlm(request);
                    var overBudget = ChatWithLlm(request);
                    add("provider_cap_blocks_physical_dispatch", ReadBool(firstBounded, "ok", false) && ReadBool(secondBounded, "ok", false)
                        && !ReadBool(overBudget, "ok", true) && calls - before == 2 && ActiveCodexPerformanceBudget.Value.Used == 2, calls - before);
                }
                finally { ActiveCodexPerformanceBudget.Value = outerBudget; }
                ambiguousFailure = true; before = calls;
                var failed = ChatWithLlm(request);
                add("ambiguous_generation_not_replayed", !ReadBool(failed, "ok", true) && calls - before == 1, calls - before);
                ambiguousFailure = false;
                ReadDictionary(fixtureSettings, "codexOptions")["fastMode"] = false;
                var fixtureProfile = LoadCharacterProfileLibrary().OrderBy(pair => pair.Key, StringComparer.Ordinal).First();
                string fixtureHeroId = fixtureProfile.Key;
                var hero = DeepCloneProfileDictionary(ReadDictionary(fixtureProfile.Value, "sourceFacts") ?? new Dictionary<string, object>());
                hero["heroStringId"] = fixtureHeroId;
                hero["name"] = ReadString(fixtureProfile.Value, "name", fixtureHeroId);
                MaterializeCharacterProfile(campaign, fixtureHeroId, hero, false, false);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    malformedFirst = attempt == 1;
                    before = calls;
                    var dialoguePayload = new Dictionary<string, object> { ["campaignId"] = campaign, ["heroStringId"] = fixtureHeroId, ["hero"] = hero,
                        ["playerText"] = "Good evening.", ["playerName"] = "Traveler", ["playerHeroStringId"] = "fixture_player",
                        ["conversationSessionId"] = "dialogue_fixture_" + attempt, ["correlationId"] = "dialogue_fixture_" + attempt,
                        ["sceneContext"] = "A quiet roadside meeting. No other characters are present.", ["worldDay"] = 1 };
                    var dialogue = DialogueRespond(dialoguePayload);
                    var lines = ReadDictionaryList(dialogue, "lines");
                    var diag = ReadDictionary(dialogue, "codexDiagnostics");
                    string finalSession = ReadString(ReadDictionary(dialogue, "conversationExchange"), "sessionId", "");
                    add(attempt == 0 ? "dialogue_one_primary_final_effect" : "dialogue_repair_one_final_effect",
                        ReadBool(dialogue, "ok", false) && ReadString(dialogue, "reply", "") == "Good evening."
                        && calls - before == attempt + 1 && finalSession.Length > 0
                        && lines.Count(line => ReadString(line, "sessionId", "") == finalSession && ReadString(line, "text", "") == "Good evening.") == 2,
                        new Dictionary<string, object> { ["calls"] = calls - before, ["lineCount"] = lines.Count,
                            ["ok"] = ReadBool(dialogue, "ok", false), ["error"] = ReadString(dialogue, "error", ""), ["diagnostics"] = diag });
                }
                foreach (string provider in new[] { NanoGptProvider, OpenRouterProvider, OpenAiCompatibleProvider })
                {
                    var settings = DeepCloneProfileDictionary(fixtureSettings);
                    settings["llmProvider"] = provider;
                    settings["reasoningMode"] = "selective"; settings["reasoningEffort"] = "medium";
                    string url = provider == NanoGptProvider ? "https://nano-gpt.com/api/v1/chat/completions" : provider == OpenRouterProvider ? "https://openrouter.ai/api/v1/chat/completions" : "https://custom.invalid/v1/chat/completions";
                    var messageList = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["role"] = "user", ["content"] = "Same unchanged request." } };
                    Func<string> build = () => {
                        var body = BuildChatRequestBody(settings, request, messageList, "unchanged-model");
                        ConfigurePromptCacheRouting(settings, request, body, url, "unchanged-model", "dialogue");
                        ConfigureReasoningRouting(settings, request, body, url, "unchanged-model", "dialogue");
                        return Json.Serialize(body);
                    };
                    settings.Remove("codexOptions"); string original = build();
                    settings["codexOptions"] = new Dictionary<string, object> { ["fastMode"] = true, ["reasoningEffort"] = "ultra" };
                    foreach (string key in CodexExperimentKeys) ReadDictionary(settings, "codexOptions")[key] = true;
                    add("non_codex_request_parity_" + provider, original == build() && BuildCodexGenerationContext(settings, request, "dialogue", "unchanged-model", "test") == null, null);
                }
            }
            catch (Exception ex) { add("production_fixture_exception", false, ex.ToString()); }
            finally
            {
                CodexPerformanceSettings.Value = previousSettings;
                CodexRpcTransportOverride = previousTransport;
                CodexRuntimeProtocol = previousProtocol;
            }
            return new Dictionary<string, object> { ["ok"] = checks.All(check => ReadBool(check, "passed", false)), ["checks"] = checks, ["injectedDispatches"] = calls, ["providerCalls"] = 0 };
        }
    }
}
