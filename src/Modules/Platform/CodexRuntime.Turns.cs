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
    internal static partial class Program
    {
        internal sealed class CodexTurnAssembly
        {
            public string Text = "";
            public string Status = "running";
            public string Error = "";
            public Dictionary<string, object> ProviderError;
            public string AuthoritativeModel = "";
            public string ReportedModel = "";
            public string ServedModel = "";
            public string ModelEvidence = "";
            public DateTime FirstTextUtc;
            public DateTime CompletedUtc;
            public Dictionary<string, object> Usage;
            public bool Completed;
        }

        private sealed class CodexItemBuffer
        {
            public string ItemId;
            public string Phase = "";
            public string Text = "";
            public bool Completed;
            public DateTime CompletedUtc;
        }

        /// <summary>
        /// Event assembly is scoped by turn and item. Deltas are useful for timing only; the final completed
        /// agent message is authoritative, so incomplete or rejected drafts are never returned to Reign.
        /// </summary>
        internal sealed class CodexTurnEventAssembler
        {
            private readonly object Sync = new object();
            private readonly string TurnId;
            private string ThreadId;
            private readonly Dictionary<string, CodexItemBuffer> Items = new Dictionary<string, CodexItemBuffer>(StringComparer.OrdinalIgnoreCase);
            private string LastCompletedAgentItemId = "";
            private string FinalAgentItemId = "";
            private string AccumulatedText = "";
            private string FinalText = "";
            private string Status = "running";
            private string Error = "";
            private Dictionary<string, object> ProviderError;
            private string ReportedModel = "";
            private string ServedModel = "";
            private string ModelEvidence = "";
            private DateTime FirstTextUtc;
            private DateTime CompletedUtc;
            private Dictionary<string, object> Usage;
            private bool UsageFromThreadTokenUpdate;
            private bool Completed;

            public CodexTurnEventAssembler(string turnId, string threadId = "")
            {
                TurnId = turnId ?? "";
                ThreadId = threadId ?? "";
            }

            public void SetThreadId(string threadId)
            {
                if (string.IsNullOrWhiteSpace(threadId)) return;
                lock (Sync)
                {
                    if (string.IsNullOrWhiteSpace(ThreadId)) ThreadId = threadId;
                }
            }

            public void Accept(Dictionary<string, object> message)
            {
                if (message == null) return;
                string method = ReadString(message, "method", "");
                Dictionary<string, object> parameters = ReadDictionary(message, "params") ?? new Dictionary<string, object>();
                Dictionary<string, object> turn = ReadDictionary(parameters, "turn");
                string eventTurnId = FirstNonEmpty(ReadString(parameters, "turnId", ""), ReadString(turn, "id", ""));
                if (!string.IsNullOrWhiteSpace(eventTurnId) && !string.IsNullOrWhiteSpace(TurnId)
                    && !eventTurnId.Equals(TurnId, StringComparison.OrdinalIgnoreCase)) return;
                string eventThreadId = FirstNonEmpty(ReadString(parameters, "threadId", ""), ReadString(turn, "threadId", ""));
                lock (Sync)
                {
                    if (!string.IsNullOrWhiteSpace(eventThreadId) && !string.IsNullOrWhiteSpace(ThreadId)
                        && !eventThreadId.Equals(ThreadId, StringComparison.OrdinalIgnoreCase)) return;

                    // The app-server reports usage on its own thread/tokenUsage/updated
                    // notification.  It can arrive before or after turn/completed, so keep
                    // this branch ahead of the Completed guard and retain the per-turn
                    // "last" breakdown while preserving the cumulative "total" breakdown
                    // for diagnostics.
                    if (method == "thread/tokenUsage/updated" || method == "thread/token_usage/updated")
                    {
                        Dictionary<string, object> updatedUsage = ReadUsage(parameters);
                        if (updatedUsage != null)
                        {
                            Usage = updatedUsage;
                            UsageFromThreadTokenUpdate = true;
                        }
                        return;
                    }
                    if (Completed) return;
                    if (method == "turn/started")
                    {
                        ReportedModel = FirstNonEmpty(ReadString(turn, "model", ""), ReadString(parameters, "model", ""), ReportedModel);
                        return;
                    }
                    if (method == "model/rerouted")
                    {
                        string fromModel = ReadString(parameters, "fromModel", "");
                        string toModel = ReadString(parameters, "toModel", "");
                        if (!string.IsNullOrWhiteSpace(toModel))
                        {
                            ServedModel = toModel;
                            ModelEvidence = "model/rerouted";
                        }
                        if (!string.IsNullOrWhiteSpace(fromModel)) ReportedModel = FirstNonEmpty(ReportedModel, fromModel);
                        return;
                    }
                    if (method == "item/agentMessage/delta")
                    {
                        string itemId = FirstNonEmpty(ReadString(parameters, "itemId", ""), ReadString(ReadDictionary(parameters, "item"), "id", ""), "agent-message");
                        CodexItemBuffer item = GetItem(itemId);
                        string delta = ReadString(parameters, "delta", "");
                        if (delta.Length > 0 && FirstTextUtc == default(DateTime)) FirstTextUtc = DateTime.UtcNow;
                        item.Text += delta;
                        AccumulatedText += delta;
                        return;
                    }
                    if (method == "item/completed")
                    {
                        Dictionary<string, object> itemObject = ReadDictionary(parameters, "item") ?? parameters;
                        string type = ReadString(itemObject, "type", "");
                        if (!type.Equals("agentMessage", StringComparison.OrdinalIgnoreCase)) return;
                        string itemId = FirstNonEmpty(ReadString(itemObject, "id", ""), ReadString(parameters, "itemId", ""), "agent-message");
                        CodexItemBuffer item = GetItem(itemId);
                        string completedText = ExtractCodexItemText(itemObject);
                        // Some runtimes emit a compact item/completed record without the
                        // already-streamed text. Preserve the accumulated delta in that case;
                        // a non-empty completed payload remains authoritative.
                        if (!string.IsNullOrWhiteSpace(completedText) || string.IsNullOrWhiteSpace(item.Text))
                            item.Text = completedText;
                        if (!string.IsNullOrWhiteSpace(item.Text) && FirstTextUtc == default(DateTime))
                            FirstTextUtc = DateTime.UtcNow;
                        item.Phase = ReadString(itemObject, "phase", "");
                        item.Completed = true;
                        item.CompletedUtc = DateTime.UtcNow;
                        LastCompletedAgentItemId = itemId;
                        ReportedModel = FirstNonEmpty(ReadString(itemObject, "model", ""), ReportedModel);
                        Dictionary<string, object> itemUsage = ReadUsage(itemObject);
                        if (itemUsage != null && !UsageFromThreadTokenUpdate) Usage = itemUsage;
                        if (item.Phase.Equals("final_answer", StringComparison.OrdinalIgnoreCase))
                        {
                            FinalAgentItemId = itemId;
                            FinalText = item.Text;
                        }
                        else if (string.IsNullOrWhiteSpace(FinalAgentItemId))
                        {
                            AccumulatedText = completedText;
                        }
                        return;
                    }
                    if (method == "turn/completed")
                    {
                        Status = turn == null ? ReadString(parameters, "status", "completed") : ReadString(turn, "status", "completed");
                        Dictionary<string, object> error = turn == null ? ReadDictionary(parameters, "error") : ReadDictionary(turn, "error");
                        ProviderError = error == null ? null : CharactersService.DeepCloneProfileDictionary(error);
                        Error = error == null ? "" : ReadString(error, "message", Json.Serialize(error));
                        Dictionary<string, object> completedUsage = turn == null ? ReadUsage(parameters) : ReadUsage(turn);
                        if (completedUsage != null && !UsageFromThreadTokenUpdate) Usage = completedUsage;
                        ReportedModel = FirstNonEmpty(turn == null ? "" : ReadString(turn, "model", ""), ReadString(parameters, "model", ""), ReportedModel);
                        if (!Status.Equals("completed", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(Error))
                            Error = "Codex turn ended with status '" + Status + "'.";
                        Completed = true;
                        CompletedUtc = DateTime.UtcNow;
                    }
                }
            }

            public CodexTurnAssembly Snapshot()
            {
                lock (Sync)
                {
                    string final = !string.IsNullOrWhiteSpace(FinalAgentItemId) ? FinalText
                        : !string.IsNullOrWhiteSpace(LastCompletedAgentItemId) ? Items[LastCompletedAgentItemId].Text
                        : AccumulatedText;
                    return new CodexTurnAssembly
                    {
                        Text = final ?? "",
                        Status = Status,
                        Error = Error,
                        ProviderError = ProviderError == null ? null : CharactersService.DeepCloneProfileDictionary(ProviderError),
                        AuthoritativeModel = ServedModel,
                        ReportedModel = ReportedModel,
                        ServedModel = ServedModel,
                        ModelEvidence = ModelEvidence,
                        FirstTextUtc = FirstTextUtc,
                        CompletedUtc = CompletedUtc,
                        Usage = Usage == null ? null : new Dictionary<string, object>(Usage),
                        Completed = Completed
                    };
                }
            }

            private CodexItemBuffer GetItem(string itemId)
            {
                CodexItemBuffer item;
                if (!Items.TryGetValue(itemId, out item))
                {
                    item = new CodexItemBuffer { ItemId = itemId };
                    Items[itemId] = item;
                }
                return item;
            }
        }

        private static Dictionary<string, object> ReadUsage(Dictionary<string, object> source)
        {
            if (source == null) return null;
            Dictionary<string, object> usage = ReadDictionary(source, "usage") ?? ReadDictionary(source, "tokenUsage") ?? ReadDictionary(source, "token_usage");
            if (usage == null) return NormalizeKnownUsage(source);

            // ThreadTokenUsageUpdatedNotification contains both a per-turn `last`
            // breakdown and a cumulative `total` breakdown.  Reign's standard usage
            // contract must describe this turn, so expose `last` as the standard fields
            // and retain the cumulative values under an explicitly namespaced diagnostic
            // field.  A plain usage object (turn/completed or item/completed) continues
            // to normalize directly.
            Dictionary<string, object> last = ReadDictionary(usage, "last");
            Dictionary<string, object> total = ReadDictionary(usage, "total");
            Dictionary<string, object> result = NormalizeKnownUsage(last ?? usage);
            if (last != null && total != null)
            {
                Dictionary<string, object> normalizedTotal = NormalizeKnownUsage(total);
                if (normalizedTotal != null)
                {
                    if (result == null) result = new Dictionary<string, object>();
                    result["codex_thread_total"] = normalizedTotal;
                    object totalTokens;
                    if (normalizedTotal.TryGetValue("total_tokens", out totalTokens) && totalTokens != null)
                        result["codex_thread_total_tokens"] = totalTokens;
                }
            }
            return result;
        }

        private static Dictionary<string, object> NormalizeKnownUsage(Dictionary<string, object> usage)
        {
            if (usage == null) return null;
            Dictionary<string, object> result = new Dictionary<string, object>();
            CopyUsageNumber(usage, result, "prompt_tokens", "prompt_tokens", "input_tokens", "inputTokens", "promptTokens");
            CopyUsageNumber(usage, result, "completion_tokens", "completion_tokens", "output_tokens", "outputTokens", "completionTokens");
            CopyUsageNumber(usage, result, "total_tokens", "total_tokens", "totalTokens");
            CopyUsageNumber(usage, result, "reasoning_tokens", "reasoning_tokens", "reasoningTokens", "reasoningOutputTokens", "reasoning_output_tokens");

            Dictionary<string, object> details = new Dictionary<string, object>();
            CopyUsageNumber(usage, details, "cached_tokens", "cached_tokens", "cachedInputTokens", "cached_input_tokens");
            CopyUsageNumber(usage, details, "cache_write_tokens", "cache_write_tokens", "cacheWriteInputTokens", "cache_write_input_tokens");
            if (details.Count > 0) result["prompt_tokens_details"] = details;
            return result.Count == 0 ? null : result;
        }

        private static bool ShouldCancelCodexWait(bool verification)
        {
            if (ShutdownRequested) return true;
#if !REIGN_EXCLUDE_VERIFICATION
            return verification && ActiveCodexPerformanceBudget.Value != null && ShouldCancelVerification();
#else
            return false;
#endif
        }

        private static void WaitForCodexTurn(CodexTurnWaiter waiter, int timeoutMs, bool verification, Func<bool> canceled = null)
        {
            if (waiter == null) throw new InvalidOperationException("Codex turn waiter was not created.");
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (!waiter.Completed.Wait(250))
            {
                if (canceled?.Invoke() == true || ShouldCancelCodexWait(verification))
                    throw new OperationCanceledException(verification ? "Codex verification was cancelled." : "Codex request was cancelled.");
                if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                    throw new TimeoutException("Codex app-server turn timed out after " + timeoutMs + " ms.");
            }
        }

        internal static void InterruptCodexTurn(string threadId, string turnId, Dictionary<string, object> settings)
        {
            if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId)) return;
            try
            {
                CodexRpc("turn/interrupt", new Dictionary<string, object>
                {
                    ["threadId"] = threadId,
                    ["turnId"] = turnId
                }, Math.Min(CodexRpcTimeout(settings), 10000));
            }
            catch { }
        }

        private static void CopyUsageNumber(Dictionary<string, object> source, Dictionary<string, object> target, string targetKey, params string[] keys)
        {
            foreach (string key in keys ?? new string[0])
            {
                if (source != null && source.ContainsKey(key) && source[key] != null)
                {
                    target[targetKey] = source[key];
                    return;
                }
            }
        }
    }
}
