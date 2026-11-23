using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class LlmProviderService
    {
        internal static CodexRuntimeRequestContext BuildCodexRuntimeSelfTestContext(string sessionId, string historySignature, string currentTurn)
        {
            Dictionary<string, object> options = new Dictionary<string, object>
            {
                ["schema"] = CodexOptionsSchema,
                ["reasoningMode"] = "selective",
                ["reasoningEffort"] = "high",
                ["reuseThreads"] = true,
                ["fastMode"] = false,
                ["structuredOutputs"] = false,
                ["compactMetadata"] = false,
                ["stablePromptMapping"] = false,
                ["parallelContextPreparation"] = false,
                ["asyncThreadCleanup"] = false
            };
            Dictionary<string, object> source = new Dictionary<string, object>
            {
                ["options"] = options,
                ["campaignId"] = "selftest-campaign",
                ["timelineId"] = "selftest-load",
                ["sessionId"] = sessionId,
                ["speakerId"] = "selftest-speaker",
                ["conversationMode"] = "individual",
                ["visibilityScope"] = "private",
                ["authorityHash"] = "selftest-authority",
                ["schemaId"] = "selftest-schema",
                ["promptContractHash"] = "selftest-prompt-contract",
                ["canonicalHistorySignature"] = historySignature,
                ["currentTurnText"] = currentTurn,
                ["finalizationApproved"] = true
            };
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["messages"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = currentTurn }
                }
            };
            return CaptureCodexRequestContext(new Dictionary<string, object>(), payload, "dialogue", "gpt-selftest", "selftest-" + sessionId, source);
        }

#if !REIGN_EXCLUDE_VERIFICATION
        internal static List<Dictionary<string, object>> RunCodexRuntimeSelfTests()
        {
            List<Dictionary<string, object>> tests = new List<Dictionary<string, object>>();
            tests.AddRange(DialogueService.RunCodexRoleplayMemorySelfTests());
            tests.AddRange(RunCodexFailureRecoverySelfTests());
            Action<string, bool, string> add = (id, passed, summary) => tests.Add(new Dictionary<string, object> { ["id"] = id, ["passed"] = passed, ["summary"] = summary });
            DialogueService.RunCodexConversationCapacitySelfTests((id, passed, evidence) => add(id, passed, "Exact-model Codex context metadata and complete serialized input capacity contract."));
            Dictionary<string, object> settings = new Dictionary<string, object> { ["reasoningMode"] = "selective", ["reasoningEffort"] = "high" };
            Dictionary<string, object> options = new Dictionary<string, object>
            {
                ["schema"] = CodexOptionsSchema, ["reasoningMode"] = "selective", ["reasoningEffort"] = "high", ["fastMode"] = false,
                ["structuredOutputs"] = false, ["compactMetadata"] = false, ["stablePromptMapping"] = false,
                ["parallelContextPreparation"] = false, ["reuseThreads"] = false, ["asyncThreadCleanup"] = false
            };
            settings["codexOptions"] = options;
            CodexRuntimeRequestContext context = CaptureCodexRequestContext(settings, new Dictionary<string, object>(), "dialogue", "gpt-test", "corr-test", new Dictionary<string, object> { ["options"] = options });
            add("runtime_options_defaults", context.Options.Schema == CodexOptionsSchema && context.Options.ReasoningEffort == "high" && !context.Options.FastMode && !context.Options.StructuredOutputs,
                "Codex options are captured once with all optional experiments disabled by default.");

            CodexProtocolState supported = new CodexProtocolState { SupportsFastMode = true, FastModeField = "serviceTier" };
            CodexFastResolution fast = CodexFastMode.Resolve(true, supported);
            Dictionary<string, object> turn = new Dictionary<string, object> { ["model"] = "gpt-test", ["effort"] = "high" };
            CodexFastMode.Apply(turn, fast);
            add("fast_same_model_effort", fast.State == "requested" && ReadString(turn, "model", "") == "gpt-test" && ReadString(turn, "effort", "") == "high" && ReadString(turn, "serviceTier", "") == "fast",
                "Fast mode adds only the runtime tier field and preserves model and reasoning effort.");

            CodexProtocolState unknown = new CodexProtocolState();
            CodexFastResolution unknownFast = CodexFastMode.Resolve(true, unknown);
            add("fast_unknown_is_explicit", unknownFast.State == "unknown" && unknownFast.Applied == "unknown",
                "Fast mode remains explicitly unknown until the runtime reports support.");

            CodexTurnEventAssembler assembler = new CodexTurnEventAssembler("turn-test");
            assembler.Accept(new Dictionary<string, object> { ["method"] = "item/agentMessage/delta", ["params"] = new Dictionary<string, object> { ["turnId"] = "turn-test", ["itemId"] = "item-1", ["delta"] = "bad" } });
            assembler.Accept(new Dictionary<string, object> { ["method"] = "item/agentMessage/delta", ["params"] = new Dictionary<string, object> { ["turnId"] = "turn-test", ["itemId"] = "item-1", ["delta"] = " draft" } });
            assembler.Accept(new Dictionary<string, object> { ["method"] = "item/completed", ["params"] = new Dictionary<string, object> { ["turnId"] = "turn-test", ["item"] = new Dictionary<string, object> { ["id"] = "item-1", ["type"] = "agentMessage", ["phase"] = "commentary", ["text"] = "rejected" } } });
            assembler.Accept(new Dictionary<string, object> { ["method"] = "item/completed", ["params"] = new Dictionary<string, object> { ["turnId"] = "turn-test", ["item"] = new Dictionary<string, object> { ["id"] = "item-2", ["type"] = "agentMessage", ["phase"] = "final_answer", ["text"] = "authoritative" } } });
            assembler.Accept(new Dictionary<string, object> { ["method"] = "model/rerouted", ["params"] = new Dictionary<string, object> { ["turnId"] = "turn-test", ["fromModel"] = "gpt-test", ["toModel"] = "gpt-test", ["reason"] = "fixture" } });
            assembler.Accept(new Dictionary<string, object> { ["method"] = "thread/tokenUsage/updated", ["params"] = new Dictionary<string, object>
            {
                ["threadId"] = "thread-test", ["turnId"] = "turn-test",
                ["tokenUsage"] = new Dictionary<string, object>
                {
                    ["last"] = new Dictionary<string, object>
                    {
                        ["cachedInputTokens"] = 5, ["inputTokens"] = 12, ["outputTokens"] = 4,
                        ["reasoningOutputTokens"] = 2, ["totalTokens"] = 18, ["cacheWriteInputTokens"] = 1
                    },
                    ["total"] = new Dictionary<string, object>
                    {
                        ["cachedInputTokens"] = 7, ["inputTokens"] = 100, ["outputTokens"] = 20,
                        ["reasoningOutputTokens"] = 5, ["totalTokens"] = 125, ["cacheWriteInputTokens"] = 3
                    }
                }
            } });
            assembler.Accept(new Dictionary<string, object> { ["method"] = "turn/completed", ["params"] = new Dictionary<string, object> { ["turnId"] = "turn-test", ["turn"] = new Dictionary<string, object> { ["id"] = "turn-test", ["status"] = "completed", ["model"] = "gpt-test" } } });
            CodexTurnAssembly assembled = assembler.Snapshot();
            Dictionary<string, object> assembledDetails = ReadDictionary(assembled.Usage, "prompt_tokens_details");
            Dictionary<string, object> assembledTotal = ReadDictionary(assembled.Usage, "codex_thread_total");
            add("event_final_item_authority", assembled.Text == "authoritative" && assembled.AuthoritativeModel == "gpt-test" && assembled.ReportedModel == "gpt-test" && assembled.ModelEvidence == "model/rerouted" && assembled.Usage != null && ReadLong(assembled.Usage, "prompt_tokens", 0) == 12 && ReadLong(assembled.Usage, "completion_tokens", 0) == 4 && ReadLong(assembled.Usage, "total_tokens", 0) == 18 && ReadLong(assembledDetails, "cached_tokens", 0) == 5 && ReadLong(assembledTotal, "total_tokens", 0) == 125 && assembled.Completed,
                "Fragmented deltas and multiple items are reconciled to the final completed agent item; exact thread usage keeps per-turn last fields, cached input, and cumulative totals.");

            CodexTurnEventAssembler lateUsageAssembler = new CodexTurnEventAssembler("usage-turn");
            lateUsageAssembler.Accept(new Dictionary<string, object> { ["method"] = "turn/completed", ["params"] = new Dictionary<string, object> { ["turnId"] = "usage-turn", ["turn"] = new Dictionary<string, object> { ["id"] = "usage-turn", ["status"] = "completed" } } });
            lateUsageAssembler.Accept(new Dictionary<string, object> { ["method"] = "thread/tokenUsage/updated", ["params"] = new Dictionary<string, object>
            {
                ["threadId"] = "thread-test", ["turnId"] = "usage-turn",
                ["tokenUsage"] = new Dictionary<string, object>
                {
                    ["last"] = new Dictionary<string, object> { ["cached_input_tokens"] = 2, ["input_tokens"] = 9, ["output_tokens"] = 3, ["reasoning_output_tokens"] = 1, ["total_tokens"] = 13 },
                    ["total"] = new Dictionary<string, object> { ["cached_input_tokens"] = 2, ["input_tokens"] = 9, ["output_tokens"] = 3, ["reasoning_output_tokens"] = 1, ["total_tokens"] = 13 }
                }
            } });
            Dictionary<string, object> lateUsage = lateUsageAssembler.Snapshot().Usage;
            add("thread_usage_after_completed", lateUsage != null && ReadLong(lateUsage, "prompt_tokens", 0) == 9 && ReadLong(lateUsage, "completion_tokens", 0) == 3,
                "A tokenUsage update arriving after turn/completed updates usage without reopening or changing completed text.");

            CodexRuntimeRequestContext isolated = CaptureCodexRequestContext(new Dictionary<string, object>(), new Dictionary<string, object>(), "dialogue", "gpt-test", "corr-thread", new Dictionary<string, object>
            {
                ["options"] = new Dictionary<string, object> { ["reuseThreads"] = true }, ["campaignId"] = "campaign", ["timelineId"] = "load-1", ["sessionId"] = "session", ["speakerId"] = "speaker", ["conversationMode"] = "individual", ["visibilityScope"] = "private", ["authorityHash"] = "authority", ["finalizationApproved"] = true,
                ["canonicalHistorySignature"] = "history-1", ["currentTurnText"] = "A trusted current turn."
            });
            CodexThreadIdentity identity;
            add("thread_isolation_requires_identity", CodexThreadIdentity.TryCreate(isolated, out identity) && !string.IsNullOrWhiteSpace(identity.Key),
                "Thread reuse requires campaign, load/timeline, session, speaker, mode, visibility and authority identity.");
            add("verification_is_bounded", ReadInt(BuildCodexModelVerificationPlan("gpt-5.4", new Dictionary<string, object>()), "providerCallCap", 0) == 1 && !ReadBool(BuildCodexModelVerificationPlan("gpt-5.4", new Dictionary<string, object>()), "execute", true),
                "GPT-5.4 verification is represented as an explicit bounded plan and never runs during discovery.");
            tests.AddRange(RunCodexRuntimeBoundarySelfTests());
            tests.Add(RunCodexInjectedAdapterTransportSelfTest());
            return tests;
        }
#endif

        private static List<Dictionary<string, object>> RunCodexRuntimeBoundarySelfTests()
        {
            List<Dictionary<string, object>> tests = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => tests.Add(new Dictionary<string, object> { ["id"] = id, ["passed"] = passed, ["summary"] = summary });

            CodexFastResolution fast = CodexFastMode.Resolve(true, new CodexProtocolState { SupportsFastMode = true, FastModeField = "serviceTier" });
            CodexRpcException rejected = new CodexRpcException("turn/start", 400, "service tier fast is unsupported");
            add("fast_rejection_is_retryable_only_before_generation", CodexFastMode.IsExplicitPreStartRejection(rejected, fast)
                && !CodexFastMode.IsExplicitPreStartRejection(new CodexRpcException("turn/start", 408, "fast tier request timed out"), fast)
                && !CodexFastMode.IsExplicitPreStartRejection(new CodexRpcException("turn/start", 500, "fast service failed"), fast),
                "Only an explicit pre-generation Fast-tier rejection qualifies for the same-model Standard fallback; an ambiguous timeout does not.");
            tests.Add(RunCodexCatalogBoundarySelfTest());

            List<Dictionary<string, object>> previousModels;
            CodexCatalogState previousCatalog;
            lock (CodexStateLock)
            {
                previousModels = CodexModels;
                previousCatalog = CloneCodexCatalogState(CodexRuntimeCatalog);
                CodexModels = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["model"] = "gpt-selftest-capability",
                        ["supportedReasoningEfforts"] = new List<object>
                        {
                            new Dictionary<string, object> { ["reasoningEffort"] = "low", ["description"] = "low" }
                        }
                    }
                };
            }
            try
            {
                bool known;
                string resolution;
                string disabled = ResolveRequestedReasoningEffort("gpt-selftest-capability", "none", true, out known, out resolution);
                add("unsupported_disabled_reasoning_uses_lowest", known && disabled == "low" && resolution.Contains("cannot disable", StringComparison.OrdinalIgnoreCase),
                    "Disabled-reasoning intent resolves to the lowest advertised effort when the selected model cannot disable reasoning.");
            }
            finally
            {
                lock (CodexStateLock)
                {
                    CodexModels = previousModels;
                    CodexRuntimeCatalog = previousCatalog;
                }
            }

            CodexTurnEventAssembler negativeAssembler = new CodexTurnEventAssembler("negative-turn", "negative-thread");
            negativeAssembler.Accept(new Dictionary<string, object>
            {
                ["method"] = "item/agentMessage/delta",
                ["params"] = new Dictionary<string, object> { ["threadId"] = "other-thread", ["turnId"] = "negative-turn", ["itemId"] = "wrong", ["delta"] = "ignored" }
            });
            negativeAssembler.Accept(new Dictionary<string, object>
            {
                ["method"] = "item/agentMessage/delta",
                ["params"] = new Dictionary<string, object> { ["threadId"] = "negative-thread", ["turnId"] = "negative-turn", ["itemId"] = "answer", ["delta"] = "refusal" }
            });
            negativeAssembler.Accept(new Dictionary<string, object>
            {
                ["method"] = "turn/completed",
                ["params"] = new Dictionary<string, object>
                {
                    ["threadId"] = "negative-thread", ["turnId"] = "negative-turn",
                    ["turn"] = new Dictionary<string, object>
                    {
                        ["id"] = "negative-turn", ["status"] = "cancelled",
                        ["error"] = new Dictionary<string, object> { ["message"] = "cancelled by test" }
                    }
                }
            });
            negativeAssembler.Accept(new Dictionary<string, object>
            {
                ["method"] = "item/agentMessage/delta",
                ["params"] = new Dictionary<string, object> { ["threadId"] = "negative-thread", ["turnId"] = "negative-turn", ["itemId"] = "answer", ["delta"] = " late" }
            });
            CodexTurnAssembly negative = negativeAssembler.Snapshot();
            add("event_thread_turn_phase_isolation", negative.Completed && negative.Status == "cancelled"
                && negative.Error == "cancelled by test" && negative.Text == "refusal",
                "Wrong-thread events and late post-completion deltas are ignored while cancellation/refusal status remains authoritative.");

            CodexRuntimeRequestContext first = BuildCodexRuntimeSelfTestContext("session-reuse", "history-1", "first turn");
            CodexThreadReusePool pool = new CodexThreadReusePool();
            CodexThreadLease firstLease = pool.RegisterNew(first, "thread-reuse");
            pool.RecordPrompt(firstLease, first);
            pool.RecordAcceptedHistorySignature(firstLease, "history-1");
            string firstCleanup = pool.Finalize(firstLease, true, false, 12, true);
            CodexRuntimeRequestContext next = BuildCodexRuntimeSelfTestContext("session-reuse", "history-1", "second turn");
            CodexThreadLease reused = pool.Acquire(next);
            string delta;
            bool appendOnly = pool.TryGetAppendOnlyPrompt(reused, next, out delta) && delta.Contains("second turn", StringComparison.Ordinal);
            string repairedCleanup = pool.Finalize(reused, true, true, 12, true);
            add("thread_reuse_requires_trusted_append", firstLease != null && string.IsNullOrWhiteSpace(firstCleanup) && reused != null && appendOnly && repairedCleanup == "thread-reuse",
                "A reusable thread is acquired only when the canonical history signature matches, and a substantively repaired response invalidates it.");

            CodexThreadReusePool exactPool = new CodexThreadReusePool();
            CodexThreadLease exactLease = exactPool.RegisterNew(first, "thread-exact");
            CodexThreadLease wrongLease = new CodexThreadLease { IdentityKey = exactLease.IdentityKey, ThreadId = "thread-other" };
            string wrongCleanup = exactPool.Finalize(wrongLease, true, false, 1, true);
            bool stillLeased = exactPool.Acquire(next) == null;
            exactPool.Abandon(exactLease);
            add("thread_lease_exact_match", wrongCleanup == "thread-other" && stillLeased,
                "Finalization checks both identity and leased thread id so a stale completion cannot modify a replacement entry.");

            CodexThreadReusePool invalidationPool = new CodexThreadReusePool();
            CodexThreadLease leased = invalidationPool.RegisterNew(first, "thread-invalidate");
            List<string> unleasedInvalidated = invalidationPool.InvalidateAll();
            string leasedCleanup = invalidationPool.Finalize(leased, true, false, 1, true);
            add("invalidate_preserves_leased", !unleasedInvalidated.Contains("thread-invalidate", StringComparer.OrdinalIgnoreCase) && leasedCleanup == "thread-invalidate",
                "Save rollback, runtime stop, and provider changes mark leased entries invalid without deleting them until finalization.");

            CodexThreadReusePool boundedPool = new CodexThreadReusePool();
            for (int index = 0; index < 70; index++)
            {
                CodexRuntimeRequestContext boundedContext = BuildCodexRuntimeSelfTestContext("bounded-" + index, "history-" + index, "turn-" + index);
                CodexThreadLease boundedLease = boundedPool.RegisterNew(boundedContext, "thread-bounded-" + index);
                if (boundedLease != null) boundedPool.Finalize(boundedLease, true, false, 1, true);
            }
            Dictionary<string, object> boundedDiagnostics = boundedPool.Diagnostics();
            List<string> evicted = boundedPool.TakeEvictedThreadIds();
            add("thread_pool_bound_and_eviction", ReadInt(boundedDiagnostics, "count", 65) <= 64 && evicted.Count >= 6,
                "Thread reuse retains at most 64 idle entries and exposes evicted ids for bounded cleanup.");

            CodexThreadCleanupQueue failingCleanup = new CodexThreadCleanupQueue();
            int cleanupAttempts = 0;
            failingCleanup.Enqueue("cleanup-failure", id => { Interlocked.Increment(ref cleanupAttempts); return false; });
            Stopwatch cleanupWait = Stopwatch.StartNew();
            while (Interlocked.CompareExchange(ref cleanupAttempts, 0, 0) < 2 && cleanupWait.ElapsedMilliseconds < 2000)
                Thread.Sleep(5);
            failingCleanup.Stop();
            add("cleanup_failure_is_bounded", cleanupAttempts == 2 && ReadInt(failingCleanup.Diagnostics(), "queued", -1) == 0,
                "A failed deletion receives at most two attempts on the single owned cleanup worker and leaves no queued work behind.");

            BeginCodexTurnAttempt("late-thread");
            bool lateCreated = GetOrCreateCodexTurn("late-selftest", "late-thread") != null;
            RetireCodexTurn("late-selftest");
            HandleCodexMessage(new Dictionary<string, object>
            {
                ["method"] = "item/agentMessage/delta",
                ["params"] = new Dictionary<string, object> { ["threadId"] = "late-thread", ["turnId"] = "late-selftest", ["itemId"] = "late", ["delta"] = "late" }
            });
            lock (CodexStateLock) add("late_turn_tombstone", lateCreated && !CodexTurns.ContainsKey("late-selftest"),
                "Retired turn ids reject late notifications instead of recreating completed request state.");
            for (int index = 0; index < 520; index++)
            {
                string retiredId = "retired-selftest-" + index;
                string retiredThread = "retired-thread-" + index;
                BeginCodexTurnAttempt(retiredThread);
                RegisterCodexTurn(retiredId, retiredThread);
                RetireCodexTurn(retiredId);
            }
            HandleCodexMessage(new Dictionary<string, object>
            {
                ["method"] = "item/agentMessage/delta",
                ["params"] = new Dictionary<string, object>
                {
                    ["threadId"] = "retired-thread-0", ["turnId"] = "retired-selftest-0",
                    ["itemId"] = "late", ["delta"] = "must-not-recreate"
                }
            });
            lock (CodexStateLock) add("late_turn_gate_after_tombstone_eviction", !CodexTurns.ContainsKey("retired-selftest-0"),
                "Unknown late notifications remain gated by an active owned turn window after the bounded tombstone set evicts old ids.");
            return tests;
        }

        private static Dictionary<string, object> RunCodexCatalogBoundarySelfTest()
        {
            Func<string, Dictionary<string, object>, int, Dictionary<string, object>> previousTransport = CodexRpcTransportOverride;
            List<Dictionary<string, object>> previousModels;
            CodexCatalogState previousCatalog;
            lock (CodexStateLock)
            {
                previousModels = CodexModels;
                previousCatalog = CloneCodexCatalogState(CodexRuntimeCatalog);
            }
            bool passed = false;
            string summary = "";
            try
            {
                int pageCalls = 0;
                bool loopPages = false;
                CodexRpcTransportOverride = (method, parameters, timeout) =>
                {
                    if (method != "model/list") throw new InvalidOperationException("Unexpected catalog selftest method: " + method);
                    pageCalls++;
                    string cursor = ReadString(parameters, "cursor", "");
                    if (loopPages)
                    {
                        return new Dictionary<string, object>
                        {
                            ["data"] = new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object> { ["model"] = "gpt-loop" }
                            },
                            ["nextCursor"] = "same-cursor"
                        };
                    }
                    return new Dictionary<string, object>
                    {
                        ["data"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["model"] = string.IsNullOrWhiteSpace(cursor) ? "gpt-5.5" : "gpt-5.3",
                                ["hidden"] = !string.IsNullOrWhiteSpace(cursor),
                                ["supportedReasoningEfforts"] = new List<object> { "low", "medium", "high" }
                            }
                        },
                        ["nextCursor"] = string.IsNullOrWhiteSpace(cursor) ? "page-2" : ""
                    };
                };
                Dictionary<string, object> paged = RefreshCodexModelsApi();
                Dictionary<string, object> missing = GetCodexModelVerificationStatus("gpt-5.4");
                bool pagedCorrectly = ReadBool(paged, "ok", false) && pageCalls == 2
                    && ReadInt(paged, "count", 0) == 2 && !ReadBool(missing, "listed", true)
                    && ReadString(missing, "status", "") == "unverified";
                loopPages = true;
                pageCalls = 0;
                Dictionary<string, object> repeated = RefreshCodexModelsApi();
                Dictionary<string, object> stale = CodexCatalogDiagnostics(CodexModels);
                Dictionary<string, object> substitution = EvaluateCodexModelVerification("gpt-5.4", "gpt-5.5", null, false, 1);
                Dictionary<string, object> inconclusive = EvaluateCodexModelVerification("gpt-5.4", "", null, false, 0);
                passed = pagedCorrectly && !ReadBool(repeated, "ok", true)
                    && ReadString(stale, "freshness", "fresh") == "stale"
                    && ReadString(substitution, "status", "") == "unavailable"
                    && ReadString(inconclusive, "status", "") == "inconclusive";
                summary = "Paginated catalog discovery, missing-model state, stale pagination failure, substitution, and inconclusive verification remain distinct.";
            }
            catch (Exception ex)
            {
                summary = "Catalog boundary selftest failed: " + DialogueService.LimitText(ex.Message, 500);
            }
            finally
            {
                CodexRpcTransportOverride = previousTransport;
                lock (CodexStateLock)
                {
                    CodexModels = previousModels;
                    CodexRuntimeCatalog = previousCatalog;
                }
            }
            return new Dictionary<string, object> { ["id"] = "catalog_pagination_and_verification", ["passed"] = passed, ["summary"] = summary };
        }

        /// <summary>
        /// Exercises the production adapter path with an injected JSON-RPC transport. No Codex
        /// process, network request, account, or provider usage is involved. This covers request
        /// construction, explicit Standard tier, fragmented Unicode events, final-item authority,
        /// known usage, and bounded cleanup.
        /// </summary>
        private static Dictionary<string, object> RunCodexInjectedAdapterTransportSelfTest()
        {
            Func<string, Dictionary<string, object>, int, Dictionary<string, object>> previousTransport = CodexRpcTransportOverride;
            CodexProtocolState previousProtocol = CodexRuntimeProtocol;
            List<string> calls = new List<string>();
            Dictionary<string, object> capturedThread = null;
            Dictionary<string, object> capturedTurn = null;
            bool passed = false;
            string summary = "";
            try
            {
                CodexRuntimeProtocol = new CodexProtocolState
                {
                    SupportsOutputSchema = true,
                    SupportsFastMode = true,
                    FastModeField = "serviceTierForTurn",
                    SupportsBaseInstructions = true,
                    BaseInstructionsField = "baseInstructions",
                    ThreadServiceTierField = "serviceTier",
                    SupportsThreadResume = true,
                    InspectionState = "offline_fixture"
                };
                CodexRpcTransportOverride = (method, parameters, timeout) =>
                {
                    calls.Add(method);
                    if (method == "thread/start")
                    {
                        capturedThread = parameters;
                        return new Dictionary<string, object> { ["thread"] = new Dictionary<string, object> { ["id"] = "offline-thread" } };
                    }
                    if (method == "turn/start")
                    {
                        capturedTurn = parameters;
                        CodexTurnWaiter turn = GetOrCreateCodexTurn("offline-turn", "offline-thread");
                        turn.Accept(new Dictionary<string, object> { ["method"] = "item/agentMessage/delta", ["params"] = new Dictionary<string, object>
                        {
                            ["turnId"] = "offline-turn", ["itemId"] = "draft", ["delta"] = "draft"
                        }});
                        turn.Accept(new Dictionary<string, object> { ["method"] = "item/completed", ["params"] = new Dictionary<string, object>
                        {
                            ["turnId"] = "offline-turn", ["item"] = new Dictionary<string, object>
                            {
                                ["id"] = "draft", ["type"] = "agentMessage", ["phase"] = "commentary", ["text"] = "rejected"
                            }
                        }});
                        foreach (string delta in new[] { "The ", "ruler’s ", "oath — 雪 😀" })
                            turn.Accept(new Dictionary<string, object> { ["method"] = "item/agentMessage/delta", ["params"] = new Dictionary<string, object>
                            {
                                ["turnId"] = "offline-turn", ["itemId"] = "answer", ["delta"] = delta
                            }});
                        turn.Accept(new Dictionary<string, object> { ["method"] = "item/completed", ["params"] = new Dictionary<string, object>
                        {
                            ["turnId"] = "offline-turn", ["item"] = new Dictionary<string, object>
                            {
                                ["id"] = "answer", ["type"] = "agentMessage", ["phase"] = "final_answer", ["text"] = "The ruler’s oath — 雪 😀"
                            }
                        }});
                        turn.Accept(new Dictionary<string, object> { ["method"] = "thread/tokenUsage/updated", ["params"] = new Dictionary<string, object>
                        {
                            ["threadId"] = "offline-thread", ["turnId"] = "offline-turn",
                            ["tokenUsage"] = new Dictionary<string, object>
                            {
                                ["last"] = new Dictionary<string, object>
                                {
                                    ["cachedInputTokens"] = 4, ["inputTokens"] = 17, ["outputTokens"] = 9,
                                    ["reasoningOutputTokens"] = 3, ["totalTokens"] = 26, ["cacheWriteInputTokens"] = 1
                                },
                                ["total"] = new Dictionary<string, object>
                                {
                                    ["cachedInputTokens"] = 4, ["inputTokens"] = 117, ["outputTokens"] = 29,
                                    ["reasoningOutputTokens"] = 8, ["totalTokens"] = 154, ["cacheWriteInputTokens"] = 2
                                }
                            }
                        }});
                        turn.Accept(new Dictionary<string, object> { ["method"] = "model/rerouted", ["params"] = new Dictionary<string, object>
                        {
                            ["turnId"] = "offline-turn", ["fromModel"] = "gpt-test", ["toModel"] = "gpt-test", ["reason"] = "offline_fixture"
                        }});
                        turn.Accept(new Dictionary<string, object> { ["method"] = "turn/completed", ["params"] = new Dictionary<string, object>
                        {
                            ["turnId"] = "offline-turn", ["turn"] = new Dictionary<string, object>
                            {
                                ["id"] = "offline-turn", ["status"] = "completed", ["model"] = "gpt-test"
                            }
                        }});
                        return new Dictionary<string, object> { ["turn"] = new Dictionary<string, object> { ["id"] = "offline-turn" } };
                    }
                    if (method == "thread/delete") return new Dictionary<string, object>();
                    throw new InvalidOperationException("Unexpected offline Codex method: " + method);
                };
                Dictionary<string, object> settings = new Dictionary<string, object>
                {
                    ["llmProvider"] = CodexSubscriptionProvider,
                    ["providerRequestTimeoutMs"] = 1000,
                    ["codexOptions"] = new Dictionary<string, object>
                    {
                        ["schema"] = CodexOptionsSchema, ["reasoningMode"] = "selective", ["reasoningEffort"] = "high",
                        ["fastMode"] = false, ["structuredOutputs"] = false, ["compactMetadata"] = false,
                        ["stablePromptMapping"] = true,
                        ["parallelContextPreparation"] = false,
                        ["reuseThreads"] = false, ["asyncThreadCleanup"] = false
                    }
                };
                Dictionary<string, object> body = new Dictionary<string, object>
                {
                    ["messages"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = "Shared rules." },
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = "Character rules." },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = "Return a Unicode-safe answer." }
                    }
                };
                Dictionary<string, object> context = new Dictionary<string, object>
                {
                    ["requestType"] = "dialogue", ["model"] = "gpt-test", ["correlationId"] = "offline-correlation",
                    ["requestedReasoningEffort"] = "high",
                    ["promptSegments"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["name"] = "sharedRules", ["role"] = "system", ["content"] = "Shared rules." },
                        new Dictionary<string, object> { ["name"] = "character", ["role"] = "system", ["content"] = "Character rules." },
                        new Dictionary<string, object> { ["name"] = "currentTurn", ["role"] = "user", ["content"] = "Return a Unicode-safe answer." }
                    }
                };
                Dictionary<string, object> response = Json.Deserialize<Dictionary<string, object>>(CodexChatCompletionJson(body, settings, "gpt-test", context));
                Dictionary<string, object> choice = ReadDictionaryList(response, "choices").FirstOrDefault();
                Dictionary<string, object> message = ReadDictionary(choice, "message");
                Dictionary<string, object> usage = ReadDictionary(response, "usage");
                Dictionary<string, object> diagnostics = ReadDictionary(response, "codexDiagnostics");
                Dictionary<string, object> timing = ReadDictionary(diagnostics, "timings");
                Dictionary<string, object> stages = ReadDictionary(timing, "stages");
                List<Dictionary<string, object>> mappedInput = ReadDictionaryList(capturedTurn, "input");
                passed = calls.Count(method => method == "turn/start") == 1
                    && calls.Count(method => method == "thread/start") == 1
                    && calls.Count(method => method == "thread/delete") == 1
                    && ReadString(capturedThread, "serviceTier", "") == "default"
                    && ReadString(capturedThread, "baseInstructions", "").Contains("Shared rules.", StringComparison.Ordinal)
                    && !ReadString(capturedThread, "baseInstructions", "").Contains("Character rules.", StringComparison.Ordinal)
                    && ReadString(capturedTurn, "serviceTierForTurn", "") == "default"
                    && mappedInput.Count == 2
                    && !mappedInput.Any(item => ReadString(item, "text", "").Contains("Shared rules.", StringComparison.Ordinal))
                    && mappedInput.Any(item => ReadString(item, "text", "").Contains("Character rules.", StringComparison.Ordinal))
                    && ReadString(message, "content", "") == "The ruler’s oath — 雪 😀"
                    && usage != null && ReadLong(usage, "prompt_tokens", 0) == 17
                    && stages != null && stages.ContainsKey("cleanup")
                    && ReadBool(diagnostics, "effectiveModelKnown", true);
                summary = "Injected production adapter transport assembled fragmented Unicode events, used the authoritative final item, preserved one generation, and completed bounded cleanup.";
            }
            catch (Exception ex)
            {
                summary = "Injected adapter transport contract failed: " + DialogueService.LimitText(ex.Message, 500);
            }
            finally
            {
                CodexRpcTransportOverride = previousTransport;
                CodexRuntimeProtocol = previousProtocol;
                lock (CodexStateLock)
                {
                    CodexTurns.Clear();
                    CodexRetiredTurns.Clear();
                    CodexPendingTurnThreads.Clear();
                    CodexOwnedTurnThreads.Clear();
                }
            }
            return new Dictionary<string, object> { ["id"] = "injected_adapter_transport", ["passed"] = passed, ["summary"] = summary };
        }
    }
}
