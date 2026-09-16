using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private sealed class ProviderState
        {
            public readonly object Gate = new object();
            public readonly int MaxConcurrency;
            public int ActiveCount;
            public int WaitingInteractive;
            public int WaitingBackground;
            public int ConsecutiveFailures;
            public DateTime CircuitOpenUntilUtc;
            public long Requests;
            public long Successes;
            public long Failures;
            public long CacheHits;
            public long TotalDurationMs;
            public string LastError = "";
            public ProviderState(int concurrency) { MaxConcurrency = concurrency; }
        }

        private sealed class ProviderCachedResponse { public string Response; public DateTime ExpiresUtc; }
        private static readonly object ProviderMiddlewareLock = new object();
        private static readonly Dictionary<string, ProviderState> ProviderStates = new Dictionary<string, ProviderState>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ProviderCachedResponse> ProviderResponses = new Dictionary<string, ProviderCachedResponse>(StringComparer.Ordinal);

        private static string ProviderPostJson(string apiUrl, string apiKey, string requestJson, Dictionary<string, object> settings,
            Dictionary<string, object> diagnostics, string correlationId, string requestType, string model)
        {
            string providerKey = NormalizeLookup(apiUrl) + "|" + NormalizeLookup(model);
            string responseKey = PromptHash(providerKey + "|" + (correlationId ?? "") + "|" + requestJson);
            ProviderState state;
            lock (ProviderMiddlewareLock)
            {
                PruneProviderResponseCacheLocked();
                if (ProviderResponses.TryGetValue(responseKey, out ProviderCachedResponse cached) && cached.ExpiresUtc > DateTime.UtcNow)
                {
                    state = GetProviderStateLocked(providerKey, settings);
                    state.CacheHits++;
                    if (diagnostics != null) diagnostics["idempotencyCacheHit"] = true;
                    return cached.Response;
                }
                state = GetProviderStateLocked(providerKey, settings);
                if (state.CircuitOpenUntilUtc > DateTime.UtcNow)
                    throw new InvalidOperationException("Provider circuit is open until " + state.CircuitOpenUntilUtc.ToString("o") + ". Last error: " + state.LastError);
            }

            int queueTimeout = Math.Max(1000, ReadInt(settings, "providerQueueTimeoutMs", 30000));
            Stopwatch timer = Stopwatch.StartNew();
            bool interactive = ProviderRequestIsInteractive(requestType);
            if (!AcquireProviderSlot(state, interactive, queueTimeout)) throw new TimeoutException("Provider priority queue timed out.");
            try
            {
                lock (ProviderMiddlewareLock) state.Requests++;
                string response = PostJsonToLlmWithTransientRetry(
                    apiUrl, apiKey, requestJson, settings, diagnostics,
                    correlationId, requestType, model);
                timer.Stop();
                lock (ProviderMiddlewareLock)
                {
                    state.Successes++; state.ConsecutiveFailures = 0; state.CircuitOpenUntilUtc = DateTime.MinValue; state.TotalDurationMs += timer.ElapsedMilliseconds;
                    ProviderResponses[responseKey] = new ProviderCachedResponse { Response = response, ExpiresUtc = DateTime.UtcNow.AddMinutes(10) };
                }
                if (diagnostics != null)
                {
                    diagnostics["providerQueueAndCallMs"] = timer.ElapsedMilliseconds;
                    diagnostics["idempotencyKey"] = responseKey.Substring(0, 16);
                }
                return response;
            }
            catch (Exception ex)
            {
                timer.Stop();
                lock (ProviderMiddlewareLock)
                {
                    state.Failures++; state.ConsecutiveFailures++; state.TotalDurationMs += timer.ElapsedMilliseconds; state.LastError = LimitText(ex.Message, 800);
                    int threshold = Math.Max(2, ReadInt(settings, "providerCircuitFailureThreshold", 4));
                    if (state.ConsecutiveFailures >= threshold)
                        state.CircuitOpenUntilUtc = DateTime.UtcNow.AddSeconds(Math.Max(5, ReadInt(settings, "providerCircuitBreakSeconds", 30)));
                }
                throw;
            }
            finally { ReleaseProviderSlot(state); }
        }

        private static bool ProviderRequestIsInteractive(string requestType)
        {
            string value = NormalizeLookup(requestType);
            return value == "dialogue" || value == "social_event" || value == "party_chat"
                || value == "generated_wilderness_event" || value == "correspondence";
        }

        private static bool AcquireProviderSlot(ProviderState state, bool interactive, int timeoutMs)
        {
            Stopwatch wait = Stopwatch.StartNew();
            lock (state.Gate)
            {
                if (interactive) state.WaitingInteractive++; else state.WaitingBackground++;
                try
                {
                    while (state.ActiveCount >= state.MaxConcurrency || !interactive && state.WaitingInteractive > 0)
                    {
                        int remaining = timeoutMs - (int)wait.ElapsedMilliseconds;
                        if (remaining <= 0 || !Monitor.Wait(state.Gate, remaining)) return false;
                    }
                    state.ActiveCount++;
                    return true;
                }
                finally
                {
                    if (interactive) state.WaitingInteractive--; else state.WaitingBackground--;
                }
            }
        }

        private static void ReleaseProviderSlot(ProviderState state)
        {
            lock (state.Gate) { state.ActiveCount = Math.Max(0, state.ActiveCount - 1); Monitor.PulseAll(state.Gate); }
        }

        private static ProviderState GetProviderStateLocked(string key, Dictionary<string, object> settings)
        {
            if (!ProviderStates.TryGetValue(key, out ProviderState state))
            {
                state = new ProviderState(Math.Max(1, Math.Min(16, ReadInt(settings, "providerMaxConcurrency", 4))));
                ProviderStates[key] = state;
            }
            return state;
        }

        private static void PruneProviderResponseCacheLocked()
        {
            if (ProviderResponses.Count < 500) return;
            foreach (string key in ProviderResponses.Where(pair => pair.Value.ExpiresUtc <= DateTime.UtcNow).Select(pair => pair.Key).ToList()) ProviderResponses.Remove(key);
            foreach (string key in ProviderResponses.Take(Math.Max(0, ProviderResponses.Count - 500)).Select(pair => pair.Key).ToList()) ProviderResponses.Remove(key);
        }

        private static Dictionary<string, object> ProviderMiddlewareStatus()
        {
            lock (ProviderMiddlewareLock)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["idempotencyEntries"] = ProviderResponses.Count,
                    ["providers"] = ProviderStates.Select(pair => new Dictionary<string, object>
                    {
                        ["provider"] = pair.Key, ["requests"] = pair.Value.Requests, ["successes"] = pair.Value.Successes,
                        ["failures"] = pair.Value.Failures, ["cacheHits"] = pair.Value.CacheHits,
                        ["averageDurationMs"] = pair.Value.Requests == 0 ? 0 : pair.Value.TotalDurationMs / pair.Value.Requests,
                        ["consecutiveFailures"] = pair.Value.ConsecutiveFailures,
                        ["maxConcurrency"] = pair.Value.MaxConcurrency, ["active"] = pair.Value.ActiveCount,
                        ["waitingInteractive"] = pair.Value.WaitingInteractive, ["waitingBackground"] = pair.Value.WaitingBackground,
                        ["circuitOpenUntilUtc"] = pair.Value.CircuitOpenUntilUtc == DateTime.MinValue ? "" : pair.Value.CircuitOpenUntilUtc.ToString("o"),
                        ["lastError"] = pair.Value.LastError
                    }).ToList()
                };
            }
        }
    }
}
