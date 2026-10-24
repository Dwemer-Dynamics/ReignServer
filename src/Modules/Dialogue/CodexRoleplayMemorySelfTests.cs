using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunCodexRoleplayMemorySelfTests()
        {
            var tests = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, string summary) => tests.Add(TestDict("id", "roleplay_" + id, "passed", pass, "summary", summary));
            bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } }
            Check("lookup_defaults", NormalizeCodexLookupMode("Direct") == "direct" && NormalizeCodexLookupMode("untrusted") == "auto"
                && ReadString(CodexConversationContracts.NormalizeOptions(TestDict("lookupMode", "direct")), "lookupMode", "") == "direct",
                "Auto is the default; Direct survives nested option normalization.");
            var fake = new CodexRoleplayMemoryAccess { CharacterReference = "Full supplied character reference", FixtureRecords = new()
                { TestDict("id", "turn_own", "kind", "turn", "text", "Mira asked the player to bring the amber token.", "worldDay", 2d) } };
            var found = ReadDictionaryList(fake.Execute("search_character_record", TestDict("query", "amber token")), "records");
            var original = fake.Execute("read_character_record", TestDict("id", "turn_own"));
            Check("exact_source_and_scope", found.Count == 1 && ReadString(original, "text", "").Contains("Mira asked")
                && Reject(() => fake.Execute("read_character_record", TestDict("id", "foreign_campaign"))),
                "Search returns opaque scoped IDs; reading retains original attribution and refuses foreign IDs.");
            fake.Execute("read_character_record", TestDict("id", "character"));
            Check("bounded_calls", Reject(() => fake.Execute("search_character_record", TestDict("query", "amber"))),
                "Four total attempts include malformed/foreign lookups; the fifth is rejected without retrieval.");
            var badArgs = new CodexRoleplayMemoryAccess { FixtureRecords = new() };
            Check("arguments_cannot_change_owner", Reject(() => badArgs.Execute("search_character_record", TestDict("query", "amber", "campaignId", "foreign")))
                && Reject(() => badArgs.Execute("shell", TestDict())), "Models cannot select campaigns, observers, paths, SQL or other tools.");

            var first = BuildCodexRuntimeSelfTestContext("persistent-fixture", "accepted-history", "first");
            first.PersistentRoleplay = true;
            first.IncrementalContext = "CURRENT ENGINE UPDATE: new scene, newly selected memory, latest contribution";
            var pool = new CodexThreadReusePool();
            var lease = pool.RegisterNew(first, "persistent-thread");
            pool.RecordAcceptedHistorySignature(lease, "accepted-history");
            Check("large_foundation_retained", pool.Finalize(lease, true, false, 40000, true) == "",
                "A 40k-token character foundation can remain warm; capacity uses the current input rather than summing repeated context counts.");
            var second = BuildCodexRuntimeSelfTestContext("persistent-fixture", "accepted-history", "second");
            second.PersistentRoleplay = true; second.IncrementalContext = first.IncrementalContext;
            var warm = pool.Acquire(second);
            Check("current_context_delta", pool.TryGetAppendOnlyPrompt(warm, second, out string delta) && delta == second.IncrementalContext,
                "Warm turns carry current engine context and memory, not only player text.");
            pool.ObserveCompaction("persistent-thread");
            Check("compaction_forces_fresh", pool.Finalize(warm, true, false, 45000, true) == "persistent-thread" && pool.Acquire(second) == null,
                "Observed compaction retires the thread; the next turn must use a newly assembled game prompt.");

            var previousScope = ActiveCodexConversationScope.Value;
            string campaign = "codex_roleplay_" + Guid.NewGuid().ToString("N");
            try
            {
                var settings = TestDict("codexOptions", TestDict("lookupMode", "direct"));
                ActiveCodexConversationScope.Value = new CodexConversationScope { Settings = settings };
                var messages = new List<Dictionary<string, object>> {
                    TestDict("role", "system", "content", "Unchanged game rules and response contract"),
                    TestDict("role", "user", "content", "CHARACTER REFERENCE CONTEXT — complete supplied foundation"),
                    TestDict("role", "user", "content", "REFERENCE CONTEXT — current engine state, motives, actions and selected memories"),
                    TestDict("role", "assistant", "content", "HISTORICAL RESPONDING NPC TURN: accepted response"),
                    TestDict("role", "user", "content", "LATEST PLAYER TURN {\"speaker\":\"Player\"}\nWhat did you ask?") };
                var request = TestDict("messages", messages);
                string before = Json.Serialize(messages);
                var metadata = TestDict("sessionId", "test-session", "timelineId", CodexConversationTimeline(campaign));
                var source = TestDict("worldDay", 5d, "memoryAsOfWorldDay", 3d, "playerHeroStringId", "player");
                AttachCodexRoleplayContext(request, source, metadata, campaign, "npc", TestDict("knowsIdentity", true), TestDict());
                Check("game_prompt_parity", Json.Serialize(messages) == before && ReadBool(metadata, "persistentRoleplay", false)
                    && !ReadString(metadata, "incrementalContext", "").Contains("HISTORICAL RESPONDING")
                    && ReadString(metadata, "incrementalContext", "").Contains("motives, actions and selected memories"),
                    "Attaching persistence leaves every game message unchanged and omits only accepted history from the warm delta.");
                string initialAuthority = ReadString(metadata, "authorityHash", "");
                messages[2]["content"] = "REFERENCE CONTEXT — updated world and selected memory";
                AttachCodexRoleplayContext(request, source, metadata, campaign, "npc", TestDict("knowsIdentity", true), TestDict());
                Check("volatile_context_keeps_thread", initialAuthority == ReadString(metadata, "authorityHash", ""),
                    "New engine state and retrieval selections travel in the update without discarding the stable character foundation.");
                var changedIdentity = TestDict("knowsIdentity", true, "encounterCount", 40, "recognitionAttempts", 12, "confidence", 0.9d);
                AttachCodexRoleplayContext(request, source, metadata, campaign, "npc", changedIdentity, TestDict("updatedUtc", "changed each turn"));
                Check("bookkeeping_keeps_thread", initialAuthority == ReadString(metadata, "authorityHash", "")
                    && CanonicalJson(CodexIdentityPermissions(changedIdentity)) == CanonicalJson(CodexIdentityPermissions(TestDict("knowsIdentity", true))),
                    "Per-turn profile timestamps and recognition counters cannot discard a stable foundation or change visibility permissions.");
                AttachCodexRoleplayContext(request, source, metadata, campaign, "npc", TestDict("knowsIdentity", false), TestDict());
                Check("visibility_changes_invalidate", initialAuthority != ReadString(metadata, "authorityHash", ""),
                    "A change in identity permissions still discards the old foundation.");
                messages[0]["content"] = "Changed game rules";
                AttachCodexRoleplayContext(request, source, metadata, campaign, "npc", TestDict("knowsIdentity", true), TestDict());
                Check("changed_rules_invalidate", initialAuthority != ReadString(metadata, "authorityHash", ""),
                    "Changed game rules, character reference or visibility create a different thread identity.");
                var access = ActiveCodexConversationScope.Value.MemoryAccess;
                Check("default_timeline_and_time_fence", access.Knowledge.TimelineId == "main" && access.Knowledge.WorldDay == 3d,
                    "Missing native timeline means main, never all branches; the supplied memory time fence remains authoritative.");
                using (var connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,participants_json,payload_json)
VALUES('direct-session',$campaign,'npc','player','in_person','closed',10,'[""npc"",""player""]','{""timelineId"":""main""}');", TestDict("campaign", campaign));
                    void Turn(string id, int index, string text, double day, List<string> audience, string timeline) =>
                        InsertConversationTurn(connection, id, "direct-session", id, index, id, "player", "player", "Player", text,
                            "in_person", day, 10L + index, "", audience, TestDict("timelineId", timeline));
                    Turn("visible", 0, "The amber token belongs by the old gate.", 2d, new() { "npc", "player" }, "main");
                    Turn("future", 1, "Future amber secret", 4d, new() { "npc", "player" }, "main");
                    Turn("unheard", 2, "Unheard amber secret", 2d, new() { "another-npc", "player" }, "main");
                    Turn("branch", 3, "Other branch amber secret", 2d, new() { "npc", "player" }, "another-branch");
                }
                var hits = ReadDictionaryList(access.Execute("search_character_record", TestDict("query", "amber", "limit", 8)), "records");
                Check("database_visibility", hits.Count == 1 && ReadString(hits[0], "excerpt", "").Contains("old gate"),
                    "Full original-record lookup excludes future events, unheard conversations and another timeline before ranking.");
                InvalidateCodexConversationTimeline(campaign);
                Check("load_invalidates_tools", Reject(() => access.Execute("read_character_record", TestDict("id", ReadString(hits.FirstOrDefault(), "id", "")))),
                    "A save load invalidates even an already cached direct record snapshot.");
                metadata["timelineId"] = CodexConversationTimeline(campaign);
                source["guardedPromptOverride"] = true;
                AttachCodexRoleplayContext(request, source, metadata, campaign, "npc", TestDict("knowsIdentity", true), TestDict());
                var excluded = ActiveCodexConversationScope.Value.MemoryAccess.Execute("search_character_record", TestDict("query", "amber"));
                Check("guarded_archive_isolation", ReadString(excluded, "coverage", "") == "archive_unavailable"
                    && ReadDictionaryList(excluded, "records").Count == 0,
                    "Guarded certification contexts cannot regain excluded historical evidence through direct tools.");
                source.Remove("guardedPromptOverride"); source.Remove("worldDay"); source.Remove("memoryAsOfWorldDay");
                AttachCodexRoleplayContext(request, source, metadata, campaign, "npc", TestDict("knowsIdentity", true), TestDict());
                Check("missing_time_is_unavailable", ReadString(ActiveCodexConversationScope.Value.MemoryAccess.Execute("search_character_record", TestDict("query", "amber")), "coverage", "") == "archive_unavailable",
                    "Missing campaign time reports unavailable archive coverage rather than exposing future records.");
            }
            catch (Exception ex) { Check("suite_exception", false, LimitText(ex.ToString(), 1800)); }
            finally
            {
                ActiveCodexConversationScope.Value = previousScope;
                ReignPostgreSqlStorage.DropCampaign(campaign);
                lock (CodexTimelineGate) CodexConversationTimelines.Remove(campaign);
            }
            tests.Add(RunCodexPersistentAdapterSelfTest());
            tests.Add(RunCodexMemoryCallbackSelfTest());
            tests.Add(RunCodexReasoningWireMatrixSelfTest());
            return tests;
        }

        private static Dictionary<string, object> RunCodexPersistentAdapterSelfTest()
        {
            var previousTransport = CodexRpcTransportOverride;
            var captured = new List<string>();
            int starts = 0, resumes = 0, turns = 0;
            string thread = "";
            bool passed = false;
            string summary;
            try
            {
                CodexRpcTransportOverride = (method, parameters, timeout) =>
                {
                    if (method == "thread/start") { thread = "roleplay-adapter-" + ++starts; return TestDict("thread", TestDict("id", thread)); }
                    if (method == "thread/resume") { if (++resumes == 2) throw new InvalidOperationException("Fixture thread expired"); return TestDict("thread", TestDict("id", thread)); }
                    if (method == "thread/delete") return TestDict();
                    if (method != "turn/start") throw new InvalidOperationException("Unexpected method " + method);
                    string turn = "roleplay-turn-" + ++turns;
                    captured.Add(string.Join("\n", ReadDictionaryList(parameters, "input").Select(row => ReadString(row, "text", ""))));
                    var waiter = GetOrCreateCodexTurn(turn, thread);
                    waiter.Accept(TestDict("method", "item/completed", "params", TestDict("threadId", thread, "turnId", turn,
                        "item", TestDict("type", "agentMessage", "id", "answer", "phase", "final_answer", "text", "{\"response\":\"accepted\"}"))));
                    waiter.Accept(TestDict("method", "thread/tokenUsage/updated", "params", TestDict("threadId", thread, "turnId", turn,
                        "tokenUsage", TestDict("last", TestDict("inputTokens", 40000, "outputTokens", 10, "totalTokens", 40010)))));
                    if (turns == 2) HandleCodexMessage(TestDict("method", "item/completed", "params", TestDict("threadId", thread,
                        "turnId", turn, "item", TestDict("type", "contextCompaction"))));
                    waiter.Accept(TestDict("method", "turn/completed", "params", TestDict("threadId", thread, "turnId", turn,
                        "turn", TestDict("id", turn, "status", "completed"))));
                    return TestDict("turn", TestDict("id", turn));
                };
                var settings = TestDict("providerRequestTimeoutMs", 1000, "codexOptions", TestDict("reasoningMode", "off"));
                for (int i = 0; i < 4; i++)
                {
                    var source = TestDict("campaignId", "adapter-fixture", "timelineId", "load-generation", "sessionId", "session", "speakerId", "npc",
                        "conversationMode", "dialogue", "visibilityScope", "visible", "authorityHash", "stable", "promptContractHash", "stable-contract",
                        "canonicalHistorySignature", "history-" + i, "currentTurnText", "player-" + i, "persistentRoleplay", true,
                        "incrementalContext", "CURRENT UPDATED ENGINE STATE " + i, "correlationId", "adapter-corr-" + i, "deferFinalization", true);
                    var payload = TestDict("messages", new[] { TestDict("role", "system", "content", "Full current game foundation " + i),
                        TestDict("role", "user", "content", "Full accepted canonical transcript " + i) });
                    var raw = TryParseJsonObject(CodexChatCompletionJson(payload, settings, "fixture-model", source));
                    // Production ChatWithLlm places usage under raw, rather than at the result root.
                    FinalizeCodexThreadRequest(TestDict("raw", raw, "accepted", true, "substantivelyChanged", false,
                        "acceptedHistorySignature", "history-" + (i + 1)), source);
                }
                passed = starts == 3 && resumes == 2 && turns == 4
                    && captured[0].Contains("Full current game foundation 0")
                    && captured[1] == "CURRENT UPDATED ENGINE STATE 1"
                    && captured[2].Contains("Full current game foundation 2") && captured[2].Contains("Full accepted canonical transcript 2")
                    && captured[3].Contains("Full current game foundation 3") && captured[3].Contains("Full accepted canonical transcript 3");
                summary = "Production adapter retained a 40k-token foundation, sent current context on the warm turn, and rebuilt the latest full prompt after compaction and an expired resume, with nested usage receipts.";
            }
            catch (Exception ex) { summary = LimitText(ex.ToString(), 1800); }
            finally
            {
                foreach (string id in CodexRuntimeThreads.InvalidateAll()) { }
                lock (CodexRuntimeStateLock) CodexPendingThreadLeases.Clear();
                CodexRpcTransportOverride = previousTransport;
            }
            return TestDict("id", "roleplay_persistent_adapter", "passed", passed, "summary", summary);
        }

        private static Dictionary<string, object> RunCodexMemoryCallbackSelfTest()
        {
            var previousWrite = CodexWriteTransportOverride;
            using var received = new ManualResetEventSlim();
            Dictionary<string, object> reply = null;
            string thread = "roleplay-callback-fixture";
            try
            {
                lock (CodexStateLock) CodexMemoryTurns[thread] = new CodexRoleplayMemoryAccess
                { FixtureRecords = new() { TestDict("id", "turn_callback", "text", "Mira asked for the amber token.") } };
                CodexWriteTransportOverride = message => { reply = message; received.Set(); };
                HandleCodexMessage(TestDict("id", "string-request-id", "method", "item/tool/call", "params",
                    TestDict("threadId", thread, "turnId", "callback-turn", "tool", "search_character_record", "arguments", TestDict("query", "amber"))));
                bool complete = received.Wait(2000);
                return TestDict("id", "roleplay_dynamic_tool_callback", "passed", complete
                    && ReadString(reply, "id", "") == "string-request-id" && ReadBool(ReadDictionary(reply, "result"), "success", false)
                    && Json.Serialize(reply).Contains("turn_callback"),
                    "summary", "The production stdout dispatcher handles a string JSON-RPC request ID and returns the scoped lookup result without blocking its reader.");
            }
            finally
            {
                lock (CodexStateLock) CodexMemoryTurns.Remove(thread);
                CodexWriteTransportOverride = previousWrite;
            }
        }

        private static Dictionary<string, object> RunCodexReasoningWireMatrixSelfTest()
        {
            var previousTransport = CodexRpcTransportOverride;
            List<Dictionary<string, object>> previousModels;
            lock (CodexStateLock) previousModels = CodexModels.ToList();
            var order = new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra" };
            bool passed = true;
            int calls = 0;
            string expectedModel = "", expectedEffort = "";
            string summary;
            try
            {
                var models = new List<Dictionary<string, object>> {
                    TestDict("model", "fixture-all", "supportedReasoningEfforts", order),
                    TestDict("model", "fixture-limited", "supportedReasoningEfforts", new[] { "low", "high" }),
                    TestDict("model", "fixture-unknown") };
                lock (CodexStateLock) { CodexModels.Clear(); CodexModels.AddRange(models); }
                CodexRpcTransportOverride = (method, parameters, timeout) =>
                {
                    if (method == "thread/start") return TestDict("thread", TestDict("id", "reasoning-thread-" + calls));
                    if (method == "thread/delete") return TestDict();
                    if (method != "turn/start") throw new InvalidOperationException("Unexpected reasoning test method " + method);
                    passed &= ReadString(parameters, "model", "") == expectedModel && ReadString(parameters, "effort", "") == expectedEffort;
                    string turn = "reasoning-turn-" + ++calls;
                    string thread = ReadString(parameters, "threadId", "");
                    var waiter = GetOrCreateCodexTurn(turn, thread);
                    waiter.Accept(TestDict("method", "item/completed", "params", TestDict("threadId", thread, "turnId", turn,
                        "item", TestDict("type", "agentMessage", "id", "answer", "phase", "final_answer", "text", "{\"response\":\"accepted\"}"))));
                    waiter.Accept(TestDict("method", "turn/completed", "params", TestDict("threadId", thread, "turnId", turn,
                        "turn", TestDict("id", turn, "status", "completed"))));
                    return TestDict("turn", TestDict("id", turn));
                };
                foreach (var model in models)
                foreach (string effort in order)
                foreach (string mode in new[] { "all", "off", "selective" })
                foreach (string requestType in new[] { "dialogue", "memory", "repair" })
                {
                    expectedModel = ReadString(model, "model", "");
                    string requested = mode == "off" || mode == "selective" && requestType == "repair" ? "none"
                        : requestType == "memory" && Array.IndexOf(order, effort) >= 3 ? "low" : effort;
                    expectedEffort = expectedModel == "fixture-limited" ? Array.IndexOf(order, requested) >= 4 ? "high" : "low" : requested;
                    var settings = TestDict("llmProvider", "codex_subscription", "providerRequestTimeoutMs", 1000,
                        "codexOptions", TestDict("reasoningMode", mode, "reasoningEffort", effort));
                    var payload = TestDict("messages", new[] { TestDict("role", "user", "content", "Fixture response only") });
                    var context = BuildCodexGenerationContext(settings, payload, requestType, expectedModel, "reasoning-fixture-" + calls);
                    CodexChatCompletionJson(payload, settings, expectedModel, context);
                }
                summary = "Checked " + calls + " injected production turns: every effort, model capability shape and disabled-reasoning fallback reached turn/start with its exact resolved model/effort. No provider calls.";
            }
            catch (Exception ex) { passed = false; summary = LimitText(ex.ToString(), 1800); }
            finally
            {
                CodexRpcTransportOverride = previousTransport;
                lock (CodexStateLock) { CodexModels.Clear(); CodexModels.AddRange(previousModels); }
            }
            return TestDict("id", "roleplay_reasoning_wire_matrix", "passed", passed && calls == 216, "summary", summary);
        }
    }
}
