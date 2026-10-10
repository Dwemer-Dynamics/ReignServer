using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.IO;
using System.Net;
using System.Text;

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
            Dictionary<string, object> diagnostics, string correlationId, string requestType, string model,
            Action<string> contentDelta = null, Func<bool> canceled = null)
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
                    if (diagnostics != null) { diagnostics["idempotencyCacheHit"] = true; RecordProviderTimings(diagnostics, 0, 0, "cache_hit"); }
                    return cached.Response;
                }
                state = GetProviderStateLocked(providerKey, settings);
                if (state.CircuitOpenUntilUtc > DateTime.UtcNow)
                    throw new InvalidOperationException("Provider circuit is open until " + state.CircuitOpenUntilUtc.ToString("o") + ". Last error: " + state.LastError);
            }

            int queueTimeout = Math.Max(1000, ReadInt(settings, "providerQueueTimeoutMs", 30000));
            Stopwatch timer = Stopwatch.StartNew();
            bool interactive = ProviderRequestIsInteractive(requestType);
            if (!AcquireProviderSlot(state, interactive, queueTimeout))
            {
                RecordProviderTimings(diagnostics, timer.ElapsedMilliseconds, 0, "queue_timeout");
                throw new TimeoutException("Provider priority queue timed out.");
            }
            long queueMs = timer.ElapsedMilliseconds;
            var callTimer = Stopwatch.StartNew();
            try
            {
                lock (ProviderMiddlewareLock) state.Requests++;
                if (canceled?.Invoke() == true) throw new OperationCanceledException();
                string response = contentDelta != null ? PostStreamingProviderJson(apiUrl, apiKey, requestJson, settings, contentDelta, canceled)
                    : PostJsonToLlmWithTransientRetry(
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
                    RecordProviderTimings(diagnostics, queueMs, callTimer.ElapsedMilliseconds, "completed");
                    diagnostics["idempotencyKey"] = responseKey.Substring(0, 16);
                }
                return response;
            }
            catch (Exception) when (canceled?.Invoke() == true)
            {
                timer.Stop();
                RecordProviderTimings(diagnostics, queueMs, callTimer.ElapsedMilliseconds, "canceled");
                // Ending an automatic exchange is expected lifecycle cleanup.
                // It must not trip the model's shared provider failure circuit.
                throw new OperationCanceledException();
            }
            catch (Exception ex)
            {
                timer.Stop();
                RecordProviderTimings(diagnostics, queueMs, callTimer.ElapsedMilliseconds, "failed");
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
            // Purpose identifiers are machine keys. Natural-language lookup
            // normalization removes underscores and would demote group/helper
            // calls to background priority.
            string value = (requestType ?? "").Trim().ToLowerInvariant();
            return value == "dialogue" || value == "social_event" || value == "party_chat"
                || value == "generated_wilderness_event" || value == "correspondence";
        }

        // Streaming uses the same provider slots, circuit, diagnostics and credentials
        // as complete responses. Endpoints returning ordinary JSON fall back in-place,
        // with no second paid request. Reasoning deltas never enter the spoken stream.
        private static string PostStreamingProviderJson(string url, string key, string json, Dictionary<string, object> settings,
            Action<string> delta, Func<bool> canceled)
        {
            using var attempt = BeginDiagnosticAttempt(url, json, key);
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST"; request.ContentType = "application/json"; request.Accept = "text/event-stream, application/json";
            request.Timeout = request.ReadWriteTimeout = Math.Clamp(ReadInt(settings, "providerRequestTimeoutMs", 120000), 15000, 600000);
            if (!string.IsNullOrWhiteSpace(key)) request.Headers["Authorization"] = "Bearer " + key;
            string site = ReadString(settings, "siteUrl", ""), title = ReadString(settings, "appTitle", "ReignBeta");
            if (site.Length > 0) { request.Headers["HTTP-Referer"] = site; request.Headers["Referer"] = site; }
            if (title.Length > 0) { request.Headers["X-Title"] = title; if (IsOpenRouterApiUrl(url)) request.Headers["X-OpenRouter-Title"] = title; }
            using var cancelTimer = new Timer(_ => { if (canceled?.Invoke() == true) request.Abort(); }, null, 200, 200);
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json); request.ContentLength = bytes.Length;
                using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                using var response = (HttpWebResponse)request.GetResponse();
                using var reader = new StreamReader(response.GetResponseStream() ?? Stream.Null, Encoding.UTF8);
                if ((response.ContentType ?? "").IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    string body = reader.ReadToEnd();
                    attempt.End("received", "Complete JSON fallback for streaming request.", TestDict("httpStatus", (int)response.StatusCode, "responseBody", body));
                    return body;
                }
                var text = new StringBuilder(); var usage = TestDict(); string finish = "";
                var eventData = new StringBuilder();
                void Dispatch()
                {
                    string data = eventData.ToString().Trim(); eventData.Clear();
                    if (data.Length == 0 || data == "[DONE]") return;
                    var raw = TryParseJsonObject(data); if (raw == null) return;
                    var suppliedUsage = ReadDictionary(raw, "usage"); if (suppliedUsage != null) usage = suppliedUsage;
                    var choice = ReadDictionaryList(raw, "choices").FirstOrDefault();
                    string fragment = ReadString(ReadDictionary(choice, "delta"), "content", "");
                    finish = FirstNonEmpty(ReadString(choice, "finish_reason", ""), finish);
                    if (fragment.Length > 0) { text.Append(fragment); if (text.Length > 200000) throw new InvalidOperationException("Banter stream exceeded its bounded output."); delta(fragment); }
                }
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (canceled?.Invoke() == true) throw new OperationCanceledException();
                    if (line.Length == 0) Dispatch();
                    else if (line.StartsWith("data:", StringComparison.Ordinal)) { if (eventData.Length > 0) eventData.Append('\n'); eventData.Append(line.Substring(5).TrimStart()); }
                }
                Dispatch();
                string bodyJson = Json.Serialize(TestDict("choices", new[] { TestDict("message", TestDict("content", text.ToString()), "finish_reason", finish) }, "usage", usage));
                attempt.End("received", "Streamed assistant content captured before acceptance.", TestDict("httpStatus", (int)response.StatusCode, "responseBody", bodyJson));
                return bodyJson;
            }
            catch (WebException ex)
            {
                if (canceled?.Invoke() == true) throw new OperationCanceledException("Banter was interrupted.", ex);
                string body = ""; if (ex.Response != null) { using var reader = new StreamReader(ex.Response.GetResponseStream() ?? Stream.Null); body = reader.ReadToEnd(); }
                attempt.End("failed", ex.Message, TestDict("responseBody", body, "transportStatus", ex.Status.ToString()));
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? ex.Message : body, ex);
            }
        }

        private static void RecordProviderTimings(Dictionary<string, object> diagnostics, long queueMs, long callMs, string outcome)
        {
            if (diagnostics == null) return;
            diagnostics["providerQueueWaitMs"] = queueMs;
            diagnostics["providerCallMs"] = callMs;
            diagnostics["providerQueueAndCallMs"] = queueMs + callMs;
            diagnostics["providerTimingOutcome"] = outcome;
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
