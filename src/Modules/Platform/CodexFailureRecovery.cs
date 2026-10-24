using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private sealed class CodexGenerationException : InvalidOperationException
        {
            internal readonly Dictionary<string, object> Failure;
            internal CodexGenerationException(CodexTurnAssembly assembly) : base(assembly.Error)
            {
                var error = assembly.ProviderError ?? TestDict("message", assembly.Error);
                var info = ReadDictionary(error, "codexErrorInfo") ?? TestDict();
                int status = ReadInt(error, "httpStatusCode", 0);
                foreach (var detail in info.Values.OfType<Dictionary<string, object>>())
                    status = Math.Max(status, ReadInt(detail, "httpStatusCode", 0));
                string kind = info.Keys.FirstOrDefault() ?? ReadString(error, "codexErrorInfo", "unknown");
                bool exhausted = kind.IndexOf("UsageLimitExceeded", StringComparison.OrdinalIgnoreCase) >= 0;
                Failure = TestDict("schema", "reign-codex-generation-failure-v1", "kind", kind,
                    "httpStatusCode", status, "terminal", assembly.Completed && assembly.Status == "failed",
                    "partialOutput", assembly.FirstTextUtc != default(DateTime) || !string.IsNullOrWhiteSpace(assembly.Text),
                    "usageLimitExceeded", exhausted, "retryAfterSeconds", Math.Max(0, ReadInt(error, "retryAfterSeconds", 0)),
                    "additionalDetails", DiagnosticRedact(LimitText(ReadString(error, "additionalDetails", ""), 1200)),
                    "message", DiagnosticRedact(LimitText(assembly.Error, 1200)));
                Data["codexFailure"] = Failure;
            }
        }

        private static readonly object CodexFailureStateLock = new object();
        private static Dictionary<string, object> CodexLastGenerationFailure = TestDict();
        private static Dictionary<string, object> CodexLastRateLimits = TestDict();

        // Only public limit counters are retained. No account identity, credentials,
        // credit balance or provider-returned dialogue is copied into status.
        private static Dictionary<string, object> SafeCodexRateLimits(Dictionary<string, object> source)
        {
            source = source ?? TestDict();
            var result = TestDict();
            foreach (string key in new[] { "ordinaryUsageAllowed", "rateLimitReachedType", "spendControlReached" })
                if (source.ContainsKey(key)) result[key] = source[key];
            var limits = ReadDictionary(source, "rateLimitsByLimitId");
            var selected = limits == null ? ReadDictionary(source, "rateLimits") : ReadDictionary(limits, "codex");
            if (selected == null && source.ContainsKey("limitId")) selected = source;
            if (selected != null)
            {
                var counter = TestDict();
                foreach (string key in new[] { "limitId", "limitName", "rateLimitReachedType", "spendControlReached" })
                    if (selected.ContainsKey(key)) counter[key] = selected[key];
                foreach (string window in new[] { "primary", "secondary" })
                {
                    var original = ReadDictionary(selected, window);
                    if (original == null) continue;
                    var safe = TestDict();
                    foreach (string key in new[] { "usedPercent", "windowDurationMins", "resetsAt" })
                        if (original.ContainsKey(key)) safe[key] = original[key];
                    counter[window] = safe;
                }
                result["rateLimits"] = counter;
            }
            return result;
        }

        private static void ObserveCodexRateLimits(Dictionary<string, object> source)
        {
            var safe = SafeCodexRateLimits(source);
            safe["observedUtc"] = DateTime.UtcNow.ToString("o");
            lock (CodexFailureStateLock) CodexLastRateLimits = safe;
        }

        private static Dictionary<string, object> CodexFailureStatus()
        {
            lock (CodexFailureStateLock)
                return TestDict("lastGenerationFailure", CloneDictionary(CodexLastGenerationFailure),
                    "rateLimits", DeepCloneProfileDictionary(CodexLastRateLimits));
        }

        private static bool CodexQuotaBlocksRetry(Dictionary<string, object> limits)
        {
            var counter = ReadDictionary(limits, "rateLimits");
            return limits != null && (limits.ContainsKey("ordinaryUsageAllowed") && !ReadBool(limits, "ordinaryUsageAllowed", true)
                || ReadBool(limits, "spendControlReached", false) || ReadBool(counter, "spendControlReached", false)
                || !string.IsNullOrWhiteSpace(ReadString(limits, "rateLimitReachedType", ""))
                || !string.IsNullOrWhiteSpace(ReadString(counter, "rateLimitReachedType", "")));
        }

        private static bool CanRetryTerminalCodexFailure(CodexGenerationException failure, int attempt, int maximum,
            Dictionary<string, object> limits) => failure != null && attempt < maximum
                && ReadInt(failure.Failure, "httpStatusCode", 0) == 429
                && ReadBool(failure.Failure, "terminal", false)
                && !ReadBool(failure.Failure, "partialOutput", true)
                && !ReadBool(failure.Failure, "usageLimitExceeded", true)
                && ReadInt(failure.Failure, "retryAfterSeconds", 0) <= 30
                && !CodexQuotaBlocksRetry(limits);

        private static string ExecuteCodexTerminalRetry(Func<string> dispatch, Func<Dictionary<string, object>> readLimits,
            Action<int> wait, Action checkCurrent, int maximum, Dictionary<string, object> recovery)
        {
            maximum = Math.Max(1, Math.Min(2, maximum));
            for (int attempt = 1; ; attempt++)
            {
                checkCurrent();
                recovery["attemptCount"] = attempt;
                try { return dispatch(); }
                catch (CodexGenerationException failure)
                {
                    var limits = readLimits();
                    failure.Failure["rateLimits"] = limits;
                    failure.Failure["attemptCount"] = attempt;
                    recovery["lastFailure"] = DeepCloneProfileDictionary(failure.Failure);
                    if (!CanRetryTerminalCodexFailure(failure, attempt, maximum, limits)) throw;
                    int delay = Math.Max(2000, ReadInt(failure.Failure, "retryAfterSeconds", 0) * 1000);
                    recovery["retryDelayMs"] = delay;
                    checkCurrent();
                    try { wait(delay); }
                    catch (TimeoutException) { throw failure; }
                }
            }
        }

        private static string CodexChatCompletionWithRecovery(Dictionary<string, object> requestBody,
            Dictionary<string, object> settings, string model, Dictionary<string, object> context,
            Action<string> contentDelta, Func<bool> canceled)
        {
            var timer = Stopwatch.StartNew();
            int timeout = Math.Max(CodexRpcTimeout(settings), ReadInt(settings, "providerRequestTimeoutMs", 120000));
            int maximum = Math.Max(1, Math.Min(2, ReadInt(settings, "llmTransientMaxAttempts", 4)));
            var recovery = TestDict("schema", "reign-codex-terminal-retry-v1", "maximumAttempts", maximum);
            void Current()
            {
                ThrowIfCampaignRequestReplaced();
                if (canceled?.Invoke() == true) throw new OperationCanceledException("Codex request was canceled.");
                if (timer.ElapsedMilliseconds >= timeout) throw new TimeoutException("Codex provider deadline expired; retry was not started.");
            }
            Dictionary<string, object> Limits()
            {
                try { ObserveCodexRateLimits(CodexRpc("account/rateLimits/read", TestDict(), 2000)); }
                catch { /* Missing limit metadata is disclosed, never treated as quota exhaustion. */ }
                lock (CodexFailureStateLock)
                {
                    DateTime observed;
                    if (!DateTime.TryParse(ReadString(CodexLastRateLimits, "observedUtc", ""), out observed)
                        || DateTime.UtcNow - observed.ToUniversalTime() > TimeSpan.FromMinutes(1))
                        return TestDict("available", false);
                    return DeepCloneProfileDictionary(CodexLastRateLimits);
                }
            }
            try
            {
                string json = ExecuteCodexTerminalRetry(() =>
                {
                    var bounded = CloneDictionary(settings);
                    bounded["providerRequestTimeoutMs"] = Math.Max(1, timeout - (int)timer.ElapsedMilliseconds);
                    return CodexChatCompletionJson(requestBody, bounded, model, context, contentDelta, canceled);
                }, Limits, delay =>
                {
                    if (timer.ElapsedMilliseconds + delay + 5000 >= timeout) throw new TimeoutException("Codex retry delay exceeds the remaining provider deadline.");
                    for (int remaining = delay; remaining > 0; remaining -= 100)
                    { Current(); Thread.Sleep(Math.Min(100, remaining)); }
                }, Current, maximum, recovery);
                var response = TryParseJsonObject(json) ?? TestDict();
                var diagnostics = ReadDictionary(response, "codexDiagnostics") ?? TestDict();
                diagnostics["terminalRetry"] = recovery;
                response["codexDiagnostics"] = diagnostics;
                lock (CodexFailureStateLock)
                    if (CodexLastGenerationFailure.Count > 0) CodexLastGenerationFailure["recovered"] = true;
                return Json.Serialize(response);
            }
            catch (CodexGenerationException failure)
            {
                var diagnostics = failure.Data["codexDiagnostics"] as Dictionary<string, object> ?? TestDict();
                diagnostics["terminalRetry"] = recovery;
                diagnostics["failure"] = failure.Failure;
                failure.Data["codexDiagnostics"] = diagnostics;
                lock (CodexFailureStateLock) CodexLastGenerationFailure = DeepCloneProfileDictionary(failure.Failure);
                throw;
            }
        }

        private static List<Dictionary<string, object>> RunCodexFailureRecoverySelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string name, bool passed) => rows.Add(TestDict("id", "codex_recovery_" + name, "passed", passed, "summary", name));
            CodexGenerationException Failure(int status = 429, bool partial = false, string kind = "responseTooManyFailedAttempts", int after = 0) =>
                new CodexGenerationException(new CodexTurnAssembly { Completed = true, Status = "failed", Error = "provider failure",
                    Text = partial ? "unfinished" : "", ProviderError = TestDict("message", "provider failure", "retryAfterSeconds", after,
                        "codexErrorInfo", TestDict(kind, TestDict("httpStatusCode", status))) });
            int attempts = 0, waited = 0;
            var receipt = TestDict();
            string value = ExecuteCodexTerminalRetry(() => { if (++attempts == 1) throw Failure(); return "complete unchanged request"; },
                () => TestDict(), delay => waited += delay, () => { }, 4, receipt);
            Check("terminal_429_recovers_once_with_backoff", value == "complete unchanged request" && attempts == 2 && waited == 2000);
            attempts = 0;
            try { ExecuteCodexTerminalRetry(() => { attempts++; throw Failure(); }, () => TestDict(), _ => { }, () => { }, 4, TestDict()); } catch (CodexGenerationException) { }
            Check("repeated_429_has_two_attempt_hard_cap", attempts == 2);
            foreach (var failure in new[] { Failure(500), Failure(429, true), Failure(429, false, "usageLimitExceeded"), Failure(429, false, after: 31) })
                Check("unsafe_or_nontransient_" + rows.Count, !CanRetryTerminalCodexFailure(failure, 1, 2, TestDict()));
            Check("explicit_quota_does_not_generate_again", !CanRetryTerminalCodexFailure(Failure(), 1, 2, TestDict("ordinaryUsageAllowed", false)));
            Check("single_attempt_budget_remains_single", !CanRetryTerminalCodexFailure(Failure(), 1, 1, TestDict()));
            attempts = 0;
            try { ExecuteCodexTerminalRetry(() => { attempts++; throw new TimeoutException("ambiguous"); }, () => TestDict(), _ => { }, () => { }, 2, TestDict()); } catch (TimeoutException) { }
            Check("ambiguous_timeout_is_never_replayed", attempts == 1);
            bool canceled = false; attempts = 0;
            try { ExecuteCodexTerminalRetry(() => { attempts++; throw Failure(); }, () => TestDict(), _ => canceled = true,
                () => { if (canceled) throw new OperationCanceledException(); }, 2, TestDict()); } catch (OperationCanceledException) { }
            Check("cancellation_during_backoff_stops_next_dispatch", attempts == 1);
            var safe = SafeCodexRateLimits(TestDict("accountId", "private", "rateLimits", TestDict("limitId", "codex", "primary", TestDict("usedPercent", 14), "credits", TestDict("balance", "private"))));
            Check("limit_status_has_no_account_or_balance", !Json.Serialize(safe).Contains("private") && ReadInt(ReadDictionary(ReadDictionary(safe, "rateLimits"), "primary"), "usedPercent", 0) == 14);
            var source = TestDict("globalPrefixChars", 400000, "layoutVersion", 12,
                "continuityPreflight", TestDict("capacityVerified", true, "requiredSources", new string('x', 600000)),
                "canonicalTranscript", TestDict("original", new string('x', 600000)));
            string summary = Json.Serialize(PromptCachePrefixStatus(source));
            Check("cache_status_does_not_render_private_full_context", summary.Length < 4096
                && ReadInt(PromptCachePrefixStatus(source), "globalPrefixChars", 0) == 400000
                && ReadString(ReadDictionary(source, "canonicalTranscript"), "original", "").Length == 600000);
            return rows;
        }
    }
}
