using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class LlmProviderService
    {
        internal const string OpenAiCompatibleProvider = "openai_compatible";
        internal const string CodexSubscriptionProvider = "codex_subscription";

        internal sealed class CodexRpcWaiter
        {
            public ConversationResponseService.ConversationDiagnosticStep Diagnostic;
            public readonly ManualResetEventSlim Completed = new ManualResetEventSlim(false);
            public Dictionary<string, object> Result;
            public string Error = "";
            public int ErrorCode;
        }

        internal sealed class CodexTurnWaiter
        {
            public readonly string TurnId;
            public string ThreadId { get; private set; }
            public readonly ManualResetEventSlim Completed = new ManualResetEventSlim(false);
            public readonly StringBuilder Text = new StringBuilder();
            public readonly CodexTurnEventAssembler Assembly;
            public string Status = "running";
            public string Error = "";
            public string AuthoritativeModel = "";
            public string ReportedModel = "";
            public string ServedModel = "";
            public string ModelEvidence = "";
            public DateTime FirstTextUtc;
            public DateTime CompletedUtc;
            public Dictionary<string, object> Usage;

            public CodexTurnWaiter(string turnId, string threadId = "")
            {
                TurnId = turnId ?? "";
                ThreadId = threadId ?? "";
                Assembly = new CodexTurnEventAssembler(TurnId, ThreadId);
            }

            public void SetThreadId(string threadId)
            {
                if (string.IsNullOrWhiteSpace(threadId)) return;
                ThreadId = threadId;
                Assembly.SetThreadId(threadId);
            }

            public void Accept(Dictionary<string, object> message)
            {
                Assembly.Accept(message);
                CodexTurnAssembly snapshot = Assembly.Snapshot();
                lock (this)
                {
                    Text.Clear();
                    Text.Append(snapshot.Text ?? "");
                    Status = snapshot.Status;
                    Error = snapshot.Error;
                    AuthoritativeModel = snapshot.AuthoritativeModel;
                    ReportedModel = snapshot.ReportedModel;
                    ServedModel = snapshot.ServedModel;
                    ModelEvidence = snapshot.ModelEvidence;
                    FirstTextUtc = snapshot.FirstTextUtc;
                    CompletedUtc = snapshot.CompletedUtc;
                    Usage = snapshot.Usage;
                    if (snapshot.Completed) Completed.Set();
                }
            }
        }

        private static readonly object CodexLifecycleLock = new object();
        internal static readonly object CodexStateLock = new object();
        private static readonly object CodexWriteLock = new object();
        internal static readonly Dictionary<long, CodexRpcWaiter> CodexPending = new Dictionary<long, CodexRpcWaiter>();
        private static readonly Dictionary<string, CodexTurnWaiter> CodexTurns = new Dictionary<string, CodexTurnWaiter>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> CodexRetiredTurns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> CodexPendingTurnThreads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static readonly Dictionary<string, string> CodexOwnedTurnThreads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Process CodexProcess;
        private static StreamWriter CodexInput;
        private static long CodexNextRequestId;
        private static string CodexLastError = "";
        private static string CodexLastStderr = "";
        private static string CodexLastTransportMethod = "";
        private static int CodexLastTransportCharacters;
        private static int CodexLastTransportUtf8Bytes;
        private static DateTime CodexLastTransportUtc;
        private static DateTime CodexStartedUtc;
        // Offline contract tests inject a deterministic JSON-RPC transport here. Production
        // leaves this null and always uses the owned official app-server process.
        internal static Func<string, Dictionary<string, object>, int, Dictionary<string, object>> CodexRpcTransportOverride;
        internal static Action<Dictionary<string, object>> CodexWriteTransportOverride;
        private static readonly Encoding CodexTransportUtf8 = new UTF8Encoding(false, true);
        private static Dictionary<string, object> CodexAccount = new Dictionary<string, object>();
        private static bool CodexAccountIdentityVerificationRequired;
        private static long CodexAccountNotificationGeneration;
        internal static List<Dictionary<string, object>> CodexModels = new List<Dictionary<string, object>>();

        internal static string NormalizeLlmProvider(string value)
        {
            string normalized = (value ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            if (normalized == "codex" || normalized == "chatgpt" || normalized == "chatgpt_codex" || normalized == CodexSubscriptionProvider)
            {
                return CodexSubscriptionProvider;
            }
            if (normalized == NanoGptProvider || normalized == "nano_gpt") return NanoGptProvider;
            if (normalized == OpenRouterProvider || normalized == "open_router") return OpenRouterProvider;
            if (normalized == OllamaCloudProvider || normalized == "ollama") return OllamaCloudProvider;
            return OpenAiCompatibleProvider;
        }

        internal static bool NormalizeLlmProviderSettings(Dictionary<string, object> settings)
        {
            if (settings == null) return false;
            string before = ReadString(settings, "llmProvider", OpenAiCompatibleProvider);
            string after = NormalizeLlmProvider(before);
            string executable = ReadString(settings, "codexExecutable", "codex").Trim();
            if (string.IsNullOrWhiteSpace(executable)) executable = "codex";
            bool changed = !string.Equals(before, after, StringComparison.Ordinal) || !settings.ContainsKey("codexExecutable") || !settings.ContainsKey("codexHome");
            settings["llmProvider"] = after;
            settings["codexExecutable"] = executable;
            if (!settings.ContainsKey("codexHome")) settings["codexHome"] = "";
            bool retiredMemoryChanged = RemoveRetiredMemorySettings(settings);
            return NormalizeChatProviderProfiles(settings) || retiredMemoryChanged || changed;
        }

        // Settings that choose a process image or its environment (Codex app-server/schema/version launches, Codex image
        // jobs, the native portrait generator). An HTTP save may keep the stored value or return to the official default;
        // any other value is operator-owned and is written to settings.json outside the HTTP API. Every launch site reads
        // the stored settings, so guarding this single write path covers them all.
        private static readonly (string Key, string Default, string Label)[] OperatorOwnedProcessSettings =
        {
            ("codexExecutable", "codex", "Codex executable"), ("codexHome", "", "CODEX_HOME"),
            ("portraitNativeSourceGeneratorPath", "", "native portrait generator path")
        };

        internal static string OperatorOwnedProcessSettingRefusal(Dictionary<string, object> stored, Dictionary<string, object> incoming)
        {
            if (incoming == null) return null;
            var refused = new List<string>();
            foreach (var (key, fallback, label) in OperatorOwnedProcessSettings)
            {
                if (!incoming.TryGetValue(key, out object value)) continue;
                string requested = value == null ? "" : value is string text ? text.Trim() : "\u0000";
                string current = ReadString(stored, key, fallback).Trim();
                if (fallback.Length > 0)
                {
                    if (requested.Length == 0) requested = fallback;
                    if (current.Length == 0) current = fallback;
                }
                if (requested != current && requested != fallback) refused.Add(label + " (" + key + ")");
            }
            if (refused.Count == 0) return null;
            return "Not saved: " + string.Join(", ", refused) + " can only be set offline. "
                + "Reload to keep the current value or clear the field for the default. "
                + "To set it: sudo reignctl stop, edit " + Program.SettingsPath + ", then sudo reignctl start.";
        }

        internal static bool UsesCodexSubscription(Dictionary<string, object> settings)
        {
            return NormalizeLlmProvider(ReadString(settings, "llmProvider", OpenAiCompatibleProvider)) == CodexSubscriptionProvider;
        }

        internal static bool LlmProviderConfigured(Dictionary<string, object> settings)
        {
            settings = settings ?? new Dictionary<string, object>();
            if (UsesCodexSubscription(settings))
            {
                return !string.IsNullOrWhiteSpace(ReadString(settings, "codexExecutable", "codex"));
            }
            string url = LlmApiUrl(settings);
            if (string.IsNullOrWhiteSpace(url)) return false;
            bool local = url.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0 || url.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0;
            // Local endpoints are keyless-capable: decide before anything that may consult the global key store.
            if (local) return true;
            return SettingsService.ProviderKeyConfigured(settings, SettingsService.LlmApiKeySlot(settings));
        }

        internal static Dictionary<string, object> CodexStatusApi()
        {
            Dictionary<string, object> settings = SettingsService.LoadSettings();
            bool cleanupFallback = false;
            DrainCodexThreadEvictions(settings, ref cleanupFallback);
            lock (CodexStateLock)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["provider"] = CodexSubscriptionProvider,
                    ["selected"] = UsesCodexSubscription(settings),
                    ["configured"] = LlmProviderConfigured(settings),
                    ["executable"] = ReadString(settings, "codexExecutable", "codex"),
                    ["codexHome"] = ReadString(settings, "codexHome", ""),
                    ["running"] = IsCodexRunningLocked(),
                    ["processId"] = IsCodexRunningLocked() ? CodexProcess.Id : 0,
                    ["startedUtc"] = CodexStartedUtc == default(DateTime) ? "" : CodexStartedUtc.ToString("o"),
                    ["account"] = SafeCodexAccount(CodexAccount),
                    ["modelCount"] = CodexModels.Count,
                    ["models"] = CodexModels.Select(SafeCodexModel).ToList(),
                    ["catalog"] = CodexCatalogDiagnostics(CodexModels),
                    ["gpt54Verification"] = GetCodexModelVerificationStatus("gpt-5.4"),
                    ["protocol"] = CodexRuntimeProtocol.ToDictionary(),
                    ["capabilities"] = CodexCapabilityStatusSnapshot(),
                    ["capabilityStatus"] = CodexCapabilityStatusSnapshot(),
                    ["threadReuse"] = CodexRuntimeThreads.Diagnostics(),
                    ["threadCleanup"] = CodexRuntimeCleanup.Diagnostics(),
                    ["lastError"] = DialogueService.LimitText(CodexLastError, 1200),
                    ["generationFailure"] = CodexFailureStatus(),
                    ["lastStderr"] = DialogueService.LimitText(CodexLastStderr, 1200),
                    ["transport"] = new Dictionary<string, object>
                    {
                        ["framing"] = "ndjson_ascii_safe_utf8_read",
                        ["standardOutputEncoding"] = CodexTransportUtf8.WebName,
                        ["strictDecoding"] = true,
                        ["lastMethod"] = CodexLastTransportMethod,
                        ["lastCharacters"] = CodexLastTransportCharacters,
                        ["lastUtf8Bytes"] = CodexLastTransportUtf8Bytes,
                        ["lastWriteUtc"] = CodexLastTransportUtc == default(DateTime) ? "" : CodexLastTransportUtc.ToString("o")
                    },
                    ["credentials"] = "Managed only by the official Codex runtime; Reign never reads or stores ChatGPT tokens."
                };
            }
        }

        internal static Dictionary<string, object> StartCodexAppServerApi()
        {
            try
            {
                EnsureCodexAppServer(SettingsService.LoadSettings());
                RefreshCodexAccount();
                return CodexStatusApi();
            }
            catch (Exception ex)
            {
                SetCodexError(ex.Message);
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message, ["status"] = CodexStatusApi() };
            }
        }

        internal static Dictionary<string, object> StopCodexAppServerApi()
        {
            StopCodexAppServer();
            return CodexStatusApi();
        }

        internal static Dictionary<string, object> StartCodexLoginApi(Dictionary<string, object> payload)
        {
            try
            {
                EnsureCodexAppServer(SettingsService.LoadSettings());
                Dictionary<string, object> result = CodexRpc("account/login/start", new Dictionary<string, object>
                {
                    ["type"] = "chatgpt",
                    ["useHostedLoginSuccessPage"] = true,
                    ["appBrand"] = "codex"
                }, CodexRpcTimeout(SettingsService.LoadSettings()));
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["loginId"] = ReadString(result, "loginId", ""),
                    ["authUrl"] = ReadString(result, "authUrl", ReadString(result, "url", "")),
                    ["status"] = CodexStatusApi()
                };
            }
            catch (Exception ex)
            {
                SetCodexError(ex.Message);
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message };
            }
        }

        internal static Dictionary<string, object> CodexLogoutApi()
        {
            try
            {
                EnsureCodexAppServer(SettingsService.LoadSettings());
                CodexRpc("account/logout", new Dictionary<string, object>(), CodexRpcTimeout(SettingsService.LoadSettings()));
                lock (CodexStateLock) CodexAccount = new Dictionary<string, object>();
                return CodexStatusApi();
            }
            catch (Exception ex)
            {
                SetCodexError(ex.Message);
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message };
            }
        }

        internal static Dictionary<string, object> RefreshCodexModelsApi()
        {
            try
            {
                Dictionary<string, object> settings = SettingsService.LoadSettings();
                EnsureCodexAppServer(settings);
                List<Dictionary<string, object>> models = new List<Dictionary<string, object>>();
                int pageCount = 0;
                string cursor = "";
                HashSet<string> seenCursors = new HashSet<string>(StringComparer.Ordinal);
                bool paginationIncomplete = false;
                string paginationError = "";
                do
                {
                    if (!string.IsNullOrWhiteSpace(cursor) && !seenCursors.Add(cursor))
                    {
                        paginationIncomplete = true;
                        paginationError = "Codex model/list repeated a pagination cursor; discovery was not marked fresh.";
                        break;
                    }
                    Dictionary<string, object> parameters = new Dictionary<string, object> { ["includeHidden"] = true, ["limit"] = 100 };
                    if (!string.IsNullOrWhiteSpace(cursor)) parameters["cursor"] = cursor;
                    Dictionary<string, object> page = CodexRpc("model/list", parameters, CodexRpcTimeout(settings));
                    pageCount++;
                    models.AddRange(ReadDictionaryList(page, "data"));
                    cursor = ReadString(page, "nextCursor", "");
                    if (pageCount >= 100 && !string.IsNullOrWhiteSpace(cursor))
                    {
                        paginationIncomplete = true;
                        paginationError = "Codex model/list exceeded the bounded 100-page discovery limit; results are incomplete.";
                        break;
                    }
                }
                while (!string.IsNullOrWhiteSpace(cursor));

                models = models
                    .Where(model => !string.IsNullOrWhiteSpace(CodexModelId(model)))
                    .GroupBy(CodexModelId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(model => ReadString(model, "displayName", CodexModelId(model)), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                lock (CodexStateLock) CodexModels = models;
                if (paginationIncomplete) RecordCodexCatalogFailure(paginationError, false);
                else RecordCodexCatalogDiscovery(models, CodexRuntimeProtocol.RuntimeVersion, pageCount);
                return new Dictionary<string, object>
                {
                    ["ok"] = !paginationIncomplete,
                    ["count"] = models.Count,
                    ["models"] = models.Select(SafeCodexModel).ToList(),
                    ["error"] = paginationIncomplete ? paginationError : "",
                    ["catalog"] = CodexCatalogDiagnostics(models),
                    ["verification"] = GetCodexModelVerificationStatus("gpt-5.4"),
                    ["status"] = CodexStatusApi()
                };
            }
            catch (Exception ex)
            {
                SetCodexError(ex.Message);
                RecordCodexCatalogFailure(ex.Message, ex is TimeoutException);
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message, ["models"] = new List<object>() };
            }
        }

        internal static string CodexChatCompletionJson(Dictionary<string, object> requestBody, Dictionary<string, object> settings, string model)
        {
            return CodexChatCompletionJson(requestBody, settings, model, null);
        }

        internal static string CodexChatCompletionJson(Dictionary<string, object> requestBody, Dictionary<string, object> settings, string model,
            Dictionary<string, object> codexContext, Action<string> contentDelta = null, Func<bool> canceled = null)
        {
            if (canceled?.Invoke() == true) throw new OperationCanceledException("Banter was canceled before provider startup.");
            Program.ProviderTestGuard.BeforeDispatch("codex_text");
            EnsureCodexAppServer(settings);
            CodexRuntimeRequestContext context = CaptureCodexRequestContext(settings, requestBody, ReadString(codexContext, "requestType", "dialogue"), model,
                ReadString(codexContext, "correlationId", ""), codexContext);
            List<Dictionary<string, object>> requestMessages = ReadDictionaryList(requestBody, "messages");
            string prompt = BuildCodexPrompt(requestMessages);
            if (context.MemoryAccess != null) prompt = DialogueService.CodexDirectInstructions + "\n\n" + DialogueService.BuildCodexMessageBlocks(requestMessages);
            string fullPrompt = prompt;
            context.PromptText = prompt;
            context.PromptHash = HashCodexText(prompt);
            CodexPromptMapping promptMapping = BuildCodexPromptMapping(context, requestMessages);
            if (context.MemoryAccess != null)
            {
                promptMapping.BaseInstructions = promptMapping.BaseInstructions.Replace("Do not use tools, shell commands, files, or network access.", "Use only the supplied character record lookup tools. Never use shell, files, web or other tools.");
                if (!promptMapping.Applied) promptMapping.TurnInput = new List<Dictionary<string, object>> { TestDict("type", "text", "text", prompt) };
            }
            context.StablePromptMappingRequested = promptMapping.Requested;
            context.StablePromptMappingApplied = promptMapping.Applied;
            context.StablePromptMappingState = promptMapping.State;
            context.StablePromptMappingReason = promptMapping.Reason;
            context.StableBaseInstructions = promptMapping.BaseInstructions;
            context.StableTurnInput = promptMapping.TurnInput;
            AugmentCodexPromptContractHash(context);
            int timeout = Math.Max(CodexRpcTimeout(settings), ReadInt(settings, "providerRequestTimeoutMs", 120000));
            var deadline = Stopwatch.StartNew();
            int RemainingTimeout() => Math.Max(1, timeout - (int)Math.Min(int.MaxValue, deadline.ElapsedMilliseconds));
            EnsureCodexAccountIdentityCurrent();
            CodexRequestTimings timings = new CodexRequestTimings();
            bool cleanupFallback = false;
            CodexThreadLease lease = CodexRuntimeThreads.Acquire(context);
            DrainCodexThreadEvictions(settings, ref cleanupFallback);
            bool reusedThread = lease != null;
            bool succeeded = false;
            string threadId = lease == null ? "" : lease.ThreadId;
            string turnId = "";
            string servingTurnDigest = "";
            CodexTurnWaiter waiter = null;
            CodexTurnAssembly assembly = null;
            Dictionary<string, object> response = null;
            List<Dictionary<string, object>> turnInput = promptMapping.TurnInput;
            Exception pendingException = null;
            bool turnStarted = false;
            try
            {
                // A reusable thread may receive only a proven append-only suffix. If the
                // canonical transcript is not an exact prefix, discard the lease and start
                // fresh rather than duplicating or omitting authoritative context.
                if (reusedThread)
                {
                    string delta;
                    if (!CodexRuntimeThreads.TryGetAppendOnlyPrompt(lease, context, out delta))
                    {
                        string invalid = CodexRuntimeThreads.Abandon(lease);
                        lease = null;
                        reusedThread = false;
                        QueueCodexThreadCleanup(invalid, settings, ref cleanupFallback);
                        threadId = "";
                        prompt = fullPrompt;
                        turnInput = promptMapping.TurnInput;
                    }
                    else
                    {
                        prompt = delta;
                        turnInput = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object> { ["type"] = "text", ["text"] = prompt }
                        };
                    }
                }

                Dictionary<string, object> thread;
                if (!reusedThread)
                {
                    timings.ThreadStartUtc = DateTime.UtcNow;
                    Dictionary<string, object> threadParameters = new Dictionary<string, object>
                    {
                        ["model"] = model,
                        ["cwd"] = CodexWorkingDirectory(),
                        ["approvalPolicy"] = "never",
                        ["sandbox"] = "read-only"
                    };
                    CodexFastMode.ApplyThreadScope(threadParameters, context.Fast, CodexRuntimeProtocol);
                    ApplyCodexPromptMapping(context, threadParameters);
                    DialogueService.ConfigureCodexRoleplayThread(threadParameters, context);
                    try
                    {
                        thread = CodexRpc("thread/start", threadParameters, RemainingTimeout());
                    }
                    catch (CodexRpcException fastError) when (CodexFastMode.IsExplicitThreadScopeRejection(fastError, context.Fast)
                        && context.Fast.Requested && context.Fast.State == "requested")
                    {
                        context.Fast.State = "unsupported";
                        context.Fast.Applied = "standard";
                        context.Fast.Reason = "The runtime explicitly rejected Fast before generation; Standard was retried on the same model.";
                        CodexFastMode.ApplyThreadScope(threadParameters, context.Fast, CodexRuntimeProtocol);
                        thread = CodexRpc("thread/start", threadParameters, RemainingTimeout());
                    }
                    threadId = ReadNestedId(thread, "thread");
                    if (string.IsNullOrWhiteSpace(threadId)) throw new InvalidOperationException("Codex app-server did not return a thread id.");
                    if (context.Options.ReuseThreads) lease = CodexRuntimeThreads.RegisterNew(context, threadId);
                    DrainCodexThreadEvictions(settings, ref cleanupFallback);
                }
                else
                {
                    timings.ThreadStartUtc = DateTime.UtcNow;
                    Dictionary<string, object> resumeParameters = new Dictionary<string, object> { ["threadId"] = threadId };
                    CodexFastMode.ApplyThreadScope(resumeParameters, context.Fast, CodexRuntimeProtocol);
                    ApplyCodexPromptMapping(context, resumeParameters);
                    try
                    {
                        thread = CodexRpc("thread/resume", resumeParameters, RemainingTimeout());
                    }
                    catch (CodexRpcException fastError) when (CodexFastMode.IsExplicitThreadScopeRejection(fastError, context.Fast)
                        && context.Fast.Requested && context.Fast.State == "requested")
                    {
                        context.Fast.State = "unsupported";
                        context.Fast.Applied = "standard";
                        context.Fast.Reason = "The runtime explicitly rejected Fast before generation; Standard was retried on the same model.";
                        CodexFastMode.ApplyThreadScope(resumeParameters, context.Fast, CodexRuntimeProtocol);
                        thread = CodexRpc("thread/resume", resumeParameters, RemainingTimeout());
                    }
                    catch
                    {
                        string invalid = CodexRuntimeThreads.Abandon(lease);
                        lease = null;
                        reusedThread = false;
                        QueueCodexThreadCleanup(invalid, settings, ref cleanupFallback);
                        threadId = "";
                        prompt = fullPrompt;
                        timings.ThreadStartUtc = DateTime.UtcNow;
                        Dictionary<string, object> threadParameters = new Dictionary<string, object>
                        {
                            ["model"] = model,
                            ["cwd"] = CodexWorkingDirectory(),
                            ["approvalPolicy"] = "never",
                            ["sandbox"] = "read-only"
                        };
                        CodexFastMode.ApplyThreadScope(threadParameters, context.Fast, CodexRuntimeProtocol);
                        ApplyCodexPromptMapping(context, threadParameters);
                        DialogueService.ConfigureCodexRoleplayThread(threadParameters, context);
                        try
                        {
                            thread = CodexRpc("thread/start", threadParameters, RemainingTimeout());
                        }
                        catch (CodexRpcException fastError) when (CodexFastMode.IsExplicitThreadScopeRejection(fastError, context.Fast)
                            && context.Fast.Requested && context.Fast.State == "requested")
                        {
                            context.Fast.State = "unsupported";
                            context.Fast.Applied = "standard";
                            context.Fast.Reason = "The runtime explicitly rejected Fast before generation; Standard was retried on the same model.";
                            CodexFastMode.ApplyThreadScope(threadParameters, context.Fast, CodexRuntimeProtocol);
                            thread = CodexRpc("thread/start", threadParameters, RemainingTimeout());
                        }
                        threadId = ReadNestedId(thread, "thread");
                        if (string.IsNullOrWhiteSpace(threadId)) throw new InvalidOperationException("Codex app-server did not return a thread id.");
                        if (context.Options.ReuseThreads) lease = CodexRuntimeThreads.RegisterNew(context, threadId);
                        turnInput = promptMapping.TurnInput;
                        DrainCodexThreadEvictions(settings, ref cleanupFallback);
                    }
                }
                timings.ThreadReadyUtc = DateTime.UtcNow;

                Dictionary<string, object> turnParameters = new Dictionary<string, object>
                {
                    ["threadId"] = threadId,
                    ["input"] = turnInput ?? new List<Dictionary<string, object>> { new Dictionary<string, object> { ["type"] = "text", ["text"] = prompt } },
                    ["cwd"] = CodexWorkingDirectory(),
                    ["approvalPolicy"] = "never",
                    ["sandboxPolicy"] = new Dictionary<string, object> { ["type"] = "readOnly" },
                    ["model"] = model,
                    ["effort"] = context.ResolvedReasoningEffort
                };
                if (context.SchemaApplied && context.OutputSchema != null)
                    turnParameters["outputSchema"] = context.OutputSchema;
                CodexFastMode.Apply(turnParameters, context.Fast);
                BeginCodexTurnAttempt(threadId);
                if (context.MemoryAccess != null) lock (CodexStateLock) DialogueService.CodexMemoryTurns[threadId] = context.MemoryAccess;
                ReserveCodexPerformanceCall(context.ToDictionary());
                timings.TurnStartUtc = DateTime.UtcNow;
                Dictionary<string, object> turn;
                try
                {
                    turn = CodexRpc("turn/start", turnParameters, RemainingTimeout());
                }
                catch (CodexRpcException fastError) when (CodexFastMode.IsExplicitPreStartRejection(fastError, context.Fast) && context.Fast.Requested && context.Fast.State == "requested")
                {
                    context.Fast.State = "unsupported";
                    context.Fast.Applied = "standard";
                    context.Fast.Reason = "The runtime explicitly rejected Fast before generation; Standard was retried on the same model.";
                    CodexFastMode.ApplyStandard(turnParameters, context.Fast);
                    ReserveCodexPerformanceCall(context.ToDictionary());
                    turn = CodexRpc("turn/start", turnParameters, RemainingTimeout());
                }
                turnId = ReadNestedId(turn, "turn");
                if (string.IsNullOrWhiteSpace(turnId)) throw new InvalidOperationException("Codex app-server did not return a turn id.");
                if (ReadBool(codexContext, "captureServingTurnDigest", false) && CodexRpcTransportOverride == null)
                    servingTurnDigest = DialogueService.ConversationExactTextHash(Json.Serialize(turnParameters));
                turnStarted = true;
                RegisterCodexTurn(turnId, threadId);
                waiter = GetOrCreateCodexTurn(turnId, threadId);
                if (waiter == null) throw new InvalidOperationException("Codex turn was already retired.");
                WaitForCodexTurn(waiter, RemainingTimeout(), false, canceled);
                assembly = waiter.Assembly.Snapshot();
                if (!string.IsNullOrWhiteSpace(assembly.Error)) throw new CodexGenerationException(assembly);
                if (assembly.Status != "completed") throw new InvalidOperationException("Codex turn ended with status " + assembly.Status + ".");
                string content = (assembly.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("Codex app-server completed without an assistant message.");
                timings.FirstTextUtc = assembly.FirstTextUtc;
                timings.TurnCompletedUtc = assembly.CompletedUtc == default(DateTime) ? DateTime.UtcNow : assembly.CompletedUtc;
                response = new Dictionary<string, object>
                {
                    ["id"] = turnId,
                    ["model"] = model,
                    ["choices"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 0,
                            ["finish_reason"] = assembly.Status == "completed" ? "stop" : assembly.Status,
                            ["message"] = new Dictionary<string, object> { ["role"] = "assistant", ["content"] = content }
                        }
                    }
                };
                if (assembly.Usage != null) response["usage"] = assembly.Usage;
                succeeded = true;
                if (lease != null)
                {
                    CodexRuntimeThreads.RecordPrompt(lease, context);
                    lock (CodexRuntimeStateLock) CodexPendingThreadLeases[context.CorrelationId] = lease;
                    bool deferredFinalization = context.DeferFinalization;
                    if (!deferredFinalization)
                    {
                        context.FinalizationApproved = true;
                        context.Repaired = false;
                        FinalizeCodexThreadRequest(new Dictionary<string, object> { ["accepted"] = true, ["substantiveChanged"] = false, ["usage"] = assembly.Usage ?? new Dictionary<string, object>() }, context.ToDictionary());
                    }
                }
            }
            catch (Exception ex)
            {
                pendingException = ex;
            }
            finally
            {
                if (waiter != null)
                {
                    CodexTurnAssembly finalAssembly = waiter.Assembly.Snapshot();
                    assembly = finalAssembly;
                    if (timings.FirstTextUtc == default(DateTime)) timings.FirstTextUtc = finalAssembly.FirstTextUtc;
                    if (timings.TurnCompletedUtc == default(DateTime)) timings.TurnCompletedUtc = finalAssembly.CompletedUtc;
                }
                if (turnStarted && !succeeded)
                {
                    InterruptCodexTurn(threadId, turnId, settings);
                }
                RetireCodexTurn(turnId);
                lock (CodexStateLock) DialogueService.CodexMemoryTurns.Remove(threadId);
                if (string.IsNullOrWhiteSpace(turnId)) EndCodexTurnAttempt(threadId);
                if (lease != null && !succeeded)
                {
                    string invalid = CodexRuntimeThreads.Abandon(lease);
                    lock (CodexRuntimeStateLock) CodexPendingThreadLeases.Remove(context.CorrelationId);
                    timings.CleanupQueuedUtc = DateTime.UtcNow;
                    QueueCodexThreadCleanup(invalid, settings, ref cleanupFallback);
                    timings.CleanupCompletedUtc = DateTime.UtcNow;
                }
                else if (lease == null && !string.IsNullOrWhiteSpace(threadId))
                {
                    timings.CleanupQueuedUtc = DateTime.UtcNow;
                    QueueCodexThreadCleanup(threadId, settings, ref cleanupFallback);
                    timings.CleanupCompletedUtc = DateTime.UtcNow;
                }
                if (pendingException != null)
                {
                    try { pendingException.Data["codexDiagnostics"] = CodexRuntimeDiagnostics(context, timings, assembly, cleanupFallback); }
                    catch { }
                }
            }
            if (pendingException != null) throw pendingException;
            if (response == null) throw new InvalidOperationException("Codex app-server did not produce a completed response.");
            DrainCodexThreadEvictions(settings, ref cleanupFallback);
            var diagnostics = CodexRuntimeDiagnostics(context, timings, assembly, cleanupFallback);
            if (servingTurnDigest.Length > 0) diagnostics["servingTurnRequestSha256"] = servingTurnDigest;
            response["codexDiagnostics"] = diagnostics;
            return Json.Serialize(response);
        }

        private static string PostLlmProviderJson(ILlmProvider selectedAdapter, string apiUrl, string apiKey, string outboundJson,
            Dictionary<string, object> requestBody, Dictionary<string, object> settings, Dictionary<string, object> cacheRouting,
            string correlationId, string requestType, string model, Dictionary<string, object> codexContext,
            Action<string> contentDelta = null, Func<bool> canceled = null)
        {
            return selectedAdapter.PostChat(new LlmProviderRequest
            {
                ApiUrl = apiUrl, ApiKey = apiKey, OutboundJson = outboundJson, RequestBody = requestBody, Settings = settings,
                CacheRouting = cacheRouting, CorrelationId = correlationId, RequestType = requestType, Model = model,
                CodexContext = codexContext, ContentDelta = contentDelta, Canceled = canceled
            });
        }

        internal static string BuildCodexPrompt(List<Dictionary<string, object>> messages)
        {
            StringBuilder prompt = new StringBuilder();
            prompt.AppendLine("You are the text-generation provider inside Bannerlord Reign. Do not use tools, shell commands, files, or network access. Return only the response requested by the conversation below.");
            prompt.AppendLine();
            foreach (Dictionary<string, object> message in messages ?? new List<Dictionary<string, object>>())
            {
                string role = ReadString(message, "role", "user").Trim().ToUpperInvariant();
                prompt.AppendLine("--- " + role + " ---");
                prompt.AppendLine(ReadString(message, "content", ""));
            }
            return prompt.ToString().Trim();
        }

        internal static void EnsureCodexAppServer(Dictionary<string, object> settings)
        {
            if (CodexRpcTransportOverride != null) return;
            lock (CodexLifecycleLock)
            {
                lock (CodexStateLock)
                {
                    if (IsCodexRunningLocked()) return;
                }

                string executable = ReadString(settings, "codexExecutable", "codex").Trim();
                if (string.IsNullOrWhiteSpace(executable)) executable = "codex";
                Directory.CreateDirectory(CodexWorkingDirectory());
                ProcessStartInfo start = CreateCodexAppServerStartInfo(executable, CodexWorkingDirectory());
                string codexHome = ReadString(settings, "codexHome", "").Trim();
                if (!string.IsNullOrWhiteSpace(codexHome)) start.EnvironmentVariables["CODEX_HOME"] = codexHome;
                Process process = new Process { StartInfo = start, EnableRaisingEvents = true };
                process.Exited += (sender, args) => FailCodexConnection("Codex app-server exited unexpectedly.");
                try
                {
                    if (!process.Start()) throw new InvalidOperationException("The Codex app-server process did not start.");
                    lock (CodexStateLock)
                    {
                        CodexProcess = process;
                        CodexInput = process.StandardInput;
                        CodexInput.AutoFlush = true;
                        CodexInput.NewLine = "\n";
                        CodexStartedUtc = DateTime.UtcNow;
                        CodexLastError = "";
                    }
                    new Thread(() => ReadCodexStdout(process)) { IsBackground = true, Name = "ReignCodexAppServerStdout" }.Start();
                    new Thread(() => ReadCodexStderr(process)) { IsBackground = true, Name = "ReignCodexAppServerStderr" }.Start();
                    Dictionary<string, object> initializeResult = CodexRpc("initialize", new Dictionary<string, object>
                    {
                        ["clientInfo"] = new Dictionary<string, object> { ["name"] = "bannerlord-reign", ["title"] = "Bannerlord Reign", ["version"] = "1.0" },
                        ["capabilities"] = new Dictionary<string, object> { ["experimentalApi"] = true }
                    }, CodexRpcTimeout(settings));
                    InspectCodexProtocolFromBundle(executable, settings, initializeResult);
                    CodexNotify("initialized", new Dictionary<string, object>());
                }
                catch
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    lock (CodexStateLock)
                    {
                        CodexProcess = null;
                        CodexInput = null;
                    }
                    throw;
                }
            }
        }

        internal static ProcessStartInfo CreateCodexAppServerStartInfo(string executable, string workingDirectory)
        {
            return new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "app-server --listen stdio://",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = CodexTransportUtf8,
                StandardErrorEncoding = CodexTransportUtf8,
                CreateNoWindow = true
            };
        }

        internal static Dictionary<string, object> CodexRpc(string method, Dictionary<string, object> parameters, int timeoutMs)
        {
            using var diagnosticRpc = ConversationResponseService.BeginDiagnosticCodexRpc(method, parameters);
            Func<string, Dictionary<string, object>, int, Dictionary<string, object>> transport = CodexRpcTransportOverride;
            if (transport != null)
            {
                var injectedResult = transport(method, parameters ?? new Dictionary<string, object>(), timeoutMs) ?? new Dictionary<string, object>();
                if (diagnosticRpc?.CompletionDeferred != true) diagnosticRpc?.End("acknowledged", "Injected provider transport returned.", injectedResult);
                return injectedResult;
            }
            long id = Interlocked.Increment(ref CodexNextRequestId);
            CodexRpcWaiter waiter = new CodexRpcWaiter();
            waiter.Diagnostic = diagnosticRpc;
            lock (CodexStateLock) CodexPending[id] = waiter;
            try
            {
                WriteCodexMessage(new Dictionary<string, object> { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new Dictionary<string, object>() });
                if (!waiter.Completed.Wait(timeoutMs)) throw new TimeoutException("Codex app-server request '" + method + "' timed out after " + timeoutMs + " ms.");
                if (!string.IsNullOrWhiteSpace(waiter.Error)) throw new CodexRpcException(method, waiter.ErrorCode, waiter.Error);
                if (diagnosticRpc?.CompletionDeferred != true) diagnosticRpc?.End("acknowledged", "Provider acknowledged this protocol request.", waiter.Result);
                return waiter.Result ?? new Dictionary<string, object>();
            }
            catch (Exception ex)
            {
                diagnosticRpc?.End("failed", ex.Message, new Dictionary<string, object> { ["error"] = ex.Message });
                throw;
            }
            finally
            {
                lock (CodexStateLock) CodexPending.Remove(id);
            }
        }

        private static void CodexNotify(string method, Dictionary<string, object> parameters)
        {
            WriteCodexMessage(new Dictionary<string, object> { ["method"] = method, ["params"] = parameters ?? new Dictionary<string, object>() });
        }

        internal static void WriteCodexMessage(Dictionary<string, object> message)
        {
            if (CodexWriteTransportOverride != null) { CodexWriteTransportOverride(message); return; }
            string frame = SerializeCodexTransportFrame(message);
            string method = ReadString(message, "method", "response");
            if (method.StartsWith("turn/", StringComparison.Ordinal) || method.StartsWith("thread/", StringComparison.Ordinal))
                ConversationResponseService.DiagnosticTrace.Value?.Record("provider_request", "Original provider protocol request", "prepared",
                    "Exact protocol frame before transmission.", 0, frame, ConversationResponseService.DiagnosticStep.Value?.Id ?? "");
            lock (CodexWriteLock)
            {
                StreamWriter input;
                lock (CodexStateLock) input = CodexInput;
                if (input == null) throw new InvalidOperationException("Codex app-server is not running.");
                input.Write(frame);
                input.Write('\n');
                input.Flush();
            }
            lock (CodexStateLock)
            {
                CodexLastTransportMethod = method;
                CodexLastTransportCharacters = frame.Length;
                CodexLastTransportUtf8Bytes = Encoding.UTF8.GetByteCount(frame);
                CodexLastTransportUtc = DateTime.UtcNow;
            }
        }

        internal static string SerializeCodexTransportFrame(Dictionary<string, object> message)
        {
            string json = Json.Serialize(message ?? new Dictionary<string, object>());
            StringBuilder ascii = null;
            for (int index = 0; index < json.Length; index++)
            {
                char value = json[index];
                if (value <= 0x7f) continue;
                if (ascii == null)
                {
                    ascii = new StringBuilder(json.Length + 64);
                    ascii.Append(json, 0, index);
                }
                ascii.Append("\\u");
                ascii.Append(((int)value).ToString("x4"));
                int next = index + 1;
                while (next < json.Length && json[next] <= 0x7f)
                {
                    ascii.Append(json[next]);
                    next++;
                }
                index = next - 1;
            }
            return ascii == null ? json : ascii.ToString();
        }

        private static void ReadCodexStdout(Process process)
        {
            try
            {
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    Dictionary<string, object> message;
                    try { message = Json.Deserialize<Dictionary<string, object>>(line); }
                    catch { continue; }
                    ConversationResponseService.ObserveDiagnosticCodexFrame(line, message);
                    HandleCodexMessage(message);
                }
            }
            catch (Exception ex) { FailCodexConnection(ex.Message); }
        }

        private static void ReadCodexStderr(Process process)
        {
            try
            {
                string line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    lock (CodexStateLock) CodexLastStderr = DialogueService.LimitText(line, 1200);
                }
            }
            catch { }
        }

        internal static void HandleCodexMessage(Dictionary<string, object> message)
        {
            long id = ReadLong(message, "id", 0);
            string method = ReadString(message, "method", "");
            if (id > 0 && string.IsNullOrWhiteSpace(method))
            {
                CodexRpcWaiter waiter;
                lock (CodexStateLock) CodexPending.TryGetValue(id, out waiter);
                if (waiter == null) return;
                Dictionary<string, object> error = ReadDictionary(message, "error");
                waiter.Error = error == null ? "" : ReadString(error, "message", Json.Serialize(error));
                waiter.ErrorCode = error == null ? 0 : ReadInt(error, "code", 0);
                waiter.Result = ReadDictionary(message, "result") ?? new Dictionary<string, object>();
                waiter.Completed.Set();
                return;
            }

            if (message.ContainsKey("id") && !string.IsNullOrWhiteSpace(method))
            {
                if (DialogueService.HandleCodexMemoryRequest(message)) return;
                WriteCodexMessage(new Dictionary<string, object>
                {
                    ["id"] = message["id"],
                    ["error"] = new Dictionary<string, object> { ["code"] = -32601, ["message"] = "Reign does not permit interactive tool or approval requests." }
                });
                return;
            }

            Dictionary<string, object> parameters = ReadDictionary(message, "params") ?? new Dictionary<string, object>();
            if (method == "account/rateLimits/updated")
            {
                ObserveCodexRateLimits(parameters);
                return;
            }
            var item = ReadDictionary(parameters, "item");
            if (method == "thread/compacted" || ReadString(item, "type", "") == "contextCompaction")
                CodexRuntimeThreads.ObserveCompaction(ReadString(parameters, "threadId", ""));
            if (method == "account/updated")
            {
                ObserveCodexAccountUpdate(parameters);
                return;
            }

            string turnId = ReadString(parameters, "turnId", "");
            Dictionary<string, object> turn = ReadDictionary(parameters, "turn");
            if (string.IsNullOrWhiteSpace(turnId) && turn != null) turnId = ReadString(turn, "id", "");
            if (string.IsNullOrWhiteSpace(turnId)) return;
            string eventThreadId = FirstNonEmpty(ReadString(parameters, "threadId", ""), ReadString(turn, "threadId", ""));
            lock (CodexStateLock)
            {
                // A notification that arrives after completion must not recreate a waiter or
                // resurrect state for a request that has already been finalized.
                if (CodexRetiredTurns.Contains(turnId)) return;
                string ownedThread;
                bool owned = CodexOwnedTurnThreads.TryGetValue(turnId, out ownedThread)
                    && (string.IsNullOrWhiteSpace(eventThreadId) || ownedThread.Equals(eventThreadId, StringComparison.OrdinalIgnoreCase));
                bool pending = !string.IsNullOrWhiteSpace(eventThreadId) && CodexPendingTurnThreads.Contains(eventThreadId);
                if (!owned && !pending) return;
            }
            CodexTurnWaiter turnWaiter = GetOrCreateCodexTurn(turnId, eventThreadId);
            if (turnWaiter == null) return;
            turnWaiter.Accept(message);
        }

        private static void RefreshCodexAccount(int timeoutMs = 0)
        {
            long generation;
            lock (CodexStateLock) generation = CodexAccountNotificationGeneration;
            Dictionary<string, object> result = CodexRpc("account/read", new Dictionary<string, object> { ["refreshToken"] = false },
                timeoutMs > 0 ? timeoutMs : CodexRpcTimeout(SettingsService.LoadSettings()));
            Dictionary<string, object> account = ReadDictionary(result, "account") ?? new Dictionary<string, object>();
            ApplyCodexAccountSnapshot(account, generation);
        }

        // account/updated contains authMode and planType, not an account ID or
        // email. Missing identity fields must not erase the verified snapshot.
        // Recheck the official account metadata before the next thread acquisition.
        private static void ObserveCodexAccountUpdate(Dictionary<string, object> update)
        {
            var full = ReadDictionary(update, "account");
            if (full != null)
            {
                long generation;
                lock (CodexStateLock) generation = ++CodexAccountNotificationGeneration;
                ApplyCodexAccountSnapshot(full, generation);
                return;
            }
            bool signedOut = update.ContainsKey("authMode") && update["authMode"] == null;
            bool changedMode;
            lock (CodexStateLock)
            {
                CodexAccountNotificationGeneration++;
                string mode = ReadString(update, "authMode", "");
                if (mode == "chatgptAuthTokens") mode = "chatgpt";
                string previous = ReadString(CodexAccount, "type", "");
                changedMode = signedOut || mode.Length > 0 && previous.Length > 0 && mode != previous;
                var next = changedMode ? TestDict() : CharactersService.DeepCloneProfileDictionary(CodexAccount);
                if (!signedOut && mode.Length > 0) next["type"] = mode;
                if (!signedOut && update.ContainsKey("planType")) next["planType"] = update["planType"];
                CodexAccount = next;
                CodexAccountIdentityVerificationRequired = true;
            }
            if (changedMode)
            {
                InvalidateCodexThreads("Codex authentication mode changed");
                lock (CodexFailureStateLock) { CodexLastRateLimits = TestDict(); CodexLastGenerationFailure = TestDict(); }
            }
        }

        private static void ApplyCodexAccountSnapshot(Dictionary<string, object> account, long generation)
        {
            bool changed;
            lock (CodexStateLock)
            {
                string previous = CodexAccountIdentityFingerprint(CodexAccount);
                changed = previous.Length > 0 && previous != CodexAccountIdentityFingerprint(account);
                CodexAccount = CharactersService.DeepCloneProfileDictionary(account);
                CodexAccountIdentityVerificationRequired = generation != CodexAccountNotificationGeneration;
            }
            if (changed)
            {
                InvalidateCodexThreads("Codex verified account identity changed");
                lock (CodexFailureStateLock) { CodexLastRateLimits = TestDict(); CodexLastGenerationFailure = TestDict(); }
            }
        }

        private static void EnsureCodexAccountIdentityCurrent()
        {
            for (int read = 0; read < 2; read++)
            {
                lock (CodexStateLock) if (!CodexAccountIdentityVerificationRequired) return;
                RefreshCodexAccount(2000);
                bool settled, signedIn, identified;
                lock (CodexStateLock)
                {
                    settled = !CodexAccountIdentityVerificationRequired;
                    signedIn = !string.IsNullOrWhiteSpace(ReadString(CodexAccount, "type", ""));
                    identified = !string.IsNullOrWhiteSpace(ReadFirstString(CodexAccount, "accountId", "id", "email"));
                }
                if (!signedIn) throw new InvalidOperationException("Codex is signed out. Sign in again, then select Retry.");
                if (!settled) continue;
                if (!identified) InvalidateCodexThreads("Codex account identity unavailable; using a fresh thread");
                return;
            }
            throw new InvalidOperationException("Codex account changed while checking its identity. Select Retry after sign-in settles.");
        }

        internal static void StopCodexAppServer()
        {
            Process process;
            lock (CodexLifecycleLock)
            {
                lock (CodexStateLock)
                {
                    process = CodexProcess;
                    CodexProcess = null;
                    CodexInput = null;
                    CodexStartedUtc = default(DateTime);
                }
                FailCodexConnection("Codex app-server stopped.");
                StopCodexRuntime();
                try { if (process != null && !process.HasExited) process.Kill(); } catch { }
                try { process?.Dispose(); } catch { }
            }
        }

        private static void FailCodexConnection(string error)
        {
            List<CodexRpcWaiter> pending;
            List<CodexTurnWaiter> turns;
            lock (CodexStateLock)
            {
                CodexLastError = DialogueService.LimitText(error, 1200);
                pending = CodexPending.Values.ToList();
                turns = CodexTurns.Values.ToList();
            }
            foreach (CodexRpcWaiter waiter in pending) { waiter.Error = CodexLastError; waiter.Completed.Set(); }
            foreach (CodexTurnWaiter waiter in turns) { waiter.Error = CodexLastError; waiter.Completed.Set(); }
        }

        private static bool IsCodexRunningLocked()
        {
            if (CodexProcess == null) return false;
            try { return !CodexProcess.HasExited; } catch { return false; }
        }

        internal static int CodexRpcTimeout(Dictionary<string, object> settings)
        {
            return Math.Max(5000, Math.Min(600000, ReadInt(settings, "providerRequestTimeoutMs", 120000)));
        }

        internal static string CodexWorkingDirectory()
        {
            return Path.Combine(Program.DataDir, "codex-provider");
        }

        internal static void SetCodexError(string error)
        {
            lock (CodexStateLock) CodexLastError = DialogueService.LimitText(error, 1200);
        }

        internal static CodexTurnWaiter GetOrCreateCodexTurn(string turnId, string threadId = "")
        {
            lock (CodexStateLock)
            {
                CodexTurnWaiter waiter;
                if (!CodexTurns.TryGetValue(turnId, out waiter))
                {
                    if (CodexRetiredTurns.Contains(turnId)) return null;
                    string ownedThread;
                    bool owned = CodexOwnedTurnThreads.TryGetValue(turnId, out ownedThread)
                        && (string.IsNullOrWhiteSpace(threadId) || ownedThread.Equals(threadId, StringComparison.OrdinalIgnoreCase));
                    bool pending = !string.IsNullOrWhiteSpace(threadId) && CodexPendingTurnThreads.Contains(threadId);
                    if (!owned && !pending) return null;
                    waiter = new CodexTurnWaiter(turnId, threadId);
                    CodexTurns[turnId] = waiter;
                }
                else waiter.SetThreadId(threadId);
                return waiter;
            }
        }

        private static void RetireCodexTurn(string turnId)
        {
            if (string.IsNullOrWhiteSpace(turnId)) return;
            lock (CodexStateLock)
            {
                CodexTurns.Remove(turnId);
                string threadId;
                if (CodexOwnedTurnThreads.TryGetValue(turnId, out threadId))
                {
                    CodexOwnedTurnThreads.Remove(turnId);
                    if (!string.IsNullOrWhiteSpace(threadId)) CodexPendingTurnThreads.Remove(threadId);
                }
                CodexRetiredTurns.Add(turnId);
                if (CodexRetiredTurns.Count > 512)
                {
                    string oldest = CodexRetiredTurns.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(oldest)) CodexRetiredTurns.Remove(oldest);
                }
            }
        }

        private static void BeginCodexTurnAttempt(string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return;
            lock (CodexStateLock) CodexPendingTurnThreads.Add(threadId);
        }

        private static void RegisterCodexTurn(string turnId, string threadId)
        {
            if (string.IsNullOrWhiteSpace(turnId)) return;
            lock (CodexStateLock)
            {
                CodexOwnedTurnThreads[turnId] = threadId ?? "";
                if (!string.IsNullOrWhiteSpace(threadId)) CodexPendingTurnThreads.Remove(threadId);
            }
        }

        private static void EndCodexTurnAttempt(string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return;
            if (ConversationResponseService.DiagnosticCodexThreads.TryRemove(threadId, out var diagnosticAttempt))
                diagnosticAttempt.End("interrupted", "Provider turn ended without a captured completion event. Inspect the model-call outcome.");
            lock (CodexStateLock) CodexPendingTurnThreads.Remove(threadId);
        }

        private static string CodexAccountIdentityFingerprint(Dictionary<string, object> account)
        {
            if (account == null || account.Count == 0) return "";
            return string.Join("|", new[]
            {
                ReadString(account, "type", ReadString(account, "authMode", "")),
                ReadFirstString(account, "accountId", "id", "email"),
                ReadString(account, "email", "")
            }.Select(value => (value ?? "").Trim().ToLowerInvariant()));
        }

        internal static string ReadNestedId(Dictionary<string, object> source, string key)
        {
            Dictionary<string, object> nested = ReadDictionary(source, key);
            return nested == null ? ReadString(source, key + "Id", ReadString(source, "id", "")) : ReadString(nested, "id", "");
        }

        private static string ExtractCodexItemText(Dictionary<string, object> item)
        {
            string text = ReadString(item, "text", ReadString(item, "content", ""));
            if (!string.IsNullOrWhiteSpace(text)) return text;
            IList content = item.ContainsKey("content") ? item["content"] as IList : null;
            if (content == null) return "";
            StringBuilder result = new StringBuilder();
            foreach (object value in content)
            {
                Dictionary<string, object> part = value as Dictionary<string, object>;
                if (part != null) result.Append(ReadString(part, "text", ""));
            }
            return result.ToString();
        }

        private static string CodexModelId(Dictionary<string, object> model)
        {
            return ReadFirstString(model, "model", "id", "slug");
        }

        private static Dictionary<string, object> SafeCodexModel(Dictionary<string, object> model)
        {
            return CodexRuntimeSafeModel(model);
        }

        private static Dictionary<string, object> SafeCodexAccount(Dictionary<string, object> account)
        {
            account = account ?? new Dictionary<string, object>();
            string type = ReadString(account, "type", ReadString(account, "authMode", ""));
            return new Dictionary<string, object>
            {
                ["signedIn"] = !string.IsNullOrWhiteSpace(type) && !type.Equals("none", StringComparison.OrdinalIgnoreCase),
                ["type"] = type,
                ["email"] = ReadString(account, "email", ""),
                ["planType"] = ReadString(account, "planType", ReadString(account, "plan", ""))
            };
        }

#if !REIGN_EXCLUDE_VERIFICATION
        internal static List<Dictionary<string, object>> RunCodexAppServerProviderSelfTests()
        {
            List<Dictionary<string, object>> tests = new List<Dictionary<string, object>>();
            tests.AddRange(RunCodexRuntimeSelfTests());
            // Fixture suites must not pick up a real installation's automatic global key; the bridge suite uses its fake store.
            bool previousAutoSelection = SettingsService.GlobalKeyAutoSelectionOffForTests;
            SettingsService.GlobalKeyAutoSelectionOffForTests = true;
            try
            {
                tests.AddRange(RunOpenRouterProviderContractTests());
                tests.AddRange(RunOllamaCloudProviderContractTests());
            }
            finally { SettingsService.GlobalKeyAutoSelectionOffForTests = previousAutoSelection; }
            tests.AddRange(SettingsService.RunGlobalKeyBridgeContractTests());
            Action<string, bool, string> add = (id, passed, summary) => tests.Add(new Dictionary<string, object>
            {
                ["id"] = id, ["passed"] = passed, ["summary"] = summary
            });
            add("provider_aliases", NormalizeLlmProvider("codex") == CodexSubscriptionProvider && NormalizeLlmProvider("NanoGPT") == NanoGptProvider,
                "Provider aliases distinguish NanoGPT, OpenRouter, custom APIs and Codex.");
            add("configuration_contract",
                LlmProviderConfigured(new Dictionary<string, object> { ["llmProvider"] = CodexSubscriptionProvider, ["codexExecutable"] = "codex" })
                && !LlmProviderConfigured(new Dictionary<string, object> { ["llmProvider"] = OpenAiCompatibleProvider, ["apiUrl"] = "https://example.invalid", ["apiKey"] = "" }),
                "Codex uses the official runtime configuration while OpenAI-compatible providers retain URL/key validation.");
            string prompt = BuildCodexPrompt(new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["role"] = "system", ["content"] = "strict-json" },
                new Dictionary<string, object> { ["role"] = "user", ["content"] = "hello" }
            });
            add("prompt_roles", prompt.Contains("--- SYSTEM ---") && prompt.Contains("strict-json") && prompt.Contains("--- USER ---") && prompt.Contains("hello"),
                "Chat-completion roles are preserved in the app-server turn input.");
            string unicodePrompt = string.Concat(Enumerable.Repeat("A ruler said — “confession.” 雪 😀\r\n", 2048));
            Dictionary<string, object> transportMessage = new Dictionary<string, object>
            {
                ["id"] = 7,
                ["method"] = "turn/start",
                ["params"] = new Dictionary<string, object>
                {
                    ["threadId"] = "thread-test",
                    ["input"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["type"] = "text", ["text"] = unicodePrompt }
                    }
                }
            };
            string transportFrame = SerializeCodexTransportFrame(transportMessage);
            Dictionary<string, object> transportRoundTrip = Json.Deserialize<Dictionary<string, object>>(transportFrame);
            List<Dictionary<string, object>> transportInput = ReadDictionaryList(ReadDictionary(transportRoundTrip, "params"), "input");
            add("transport_large_unicode_frame",
                transportFrame.Length > 65536
                && transportFrame.All(character => character <= 0x7f && character != '\r' && character != '\n')
                && transportInput.Count == 1
                && ReadString(transportInput[0], "text", "") == unicodePrompt,
                "Large multilingual prompts use one ASCII-safe NDJSON frame and round-trip without changing provider input.");
            ProcessStartInfo transportStart = CreateCodexAppServerStartInfo("codex", CodexWorkingDirectory());
            string unicodeReply = "The clerk’s oath — 雪 😀";
            bool rejectedInvalidUtf8 = false;
            try { transportStart.StandardOutputEncoding.GetString(new byte[] { 0xc3, 0x28 }); }
            catch (DecoderFallbackException) { rejectedInvalidUtf8 = true; }
            add("transport_strict_utf8_output",
                transportStart.StandardOutputEncoding.WebName == "utf-8"
                && transportStart.StandardErrorEncoding.WebName == "utf-8"
                && transportStart.StandardOutputEncoding.GetString(Encoding.UTF8.GetBytes(unicodeReply)) == unicodeReply
                && rejectedInvalidUtf8,
                "Provider stdout and stderr decode strict UTF-8, preserving punctuation and multilingual replies while rejecting corrupt bytes.");
            Dictionary<string, object> safe = SafeCodexModel(new Dictionary<string, object> { ["model"] = "gpt-test", ["displayName"] = "GPT Test", ["hidden"] = true });
            add("model_catalog", ReadString(safe, "id", "") == "gpt-test" && ReadBool(safe, "hidden", false),
                "The full app-server model catalog, including hidden available entries, is represented safely for UI selection.");
            string controlCenter = Program.ControlCenterHtml();
            add("control_center_selection",
                controlCenter.Contains("value='nanogpt'>NanoGPT")
                && controlCenter.Contains("value='openrouter'>OpenRouter")
                && controlCenter.Contains("value='ollama_cloud'>Ollama Cloud")
                && controlCenter.Contains("value='openai_compatible'>Custom OpenAI-compatible API")
                && controlCenter.Contains("value='codex_subscription'>ChatGPT Codex subscription")
                && controlCenter.Contains("/api/codex/models/refresh")
                && controlCenter.Contains("id='llmModelOptions'"),
                "The Reign Control Center exposes saved API providers and the account-backed GPT model catalog.");
            return tests;
        }
#endif
    }
}
