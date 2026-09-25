using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // A separate opt-in evidence store. Never pass its payloads through the normal
        // audit/reasoning sanitizer or include them in campaign snapshots/support logs.
        private const string ConversationDiagnosticSchema = "reign-conversation-diagnostics-v1";
        private static readonly string DiagnosticProcessId = Guid.NewGuid().ToString("N");
        private static readonly AsyncLocal<string> DiagnosticRootOverride = new AsyncLocal<string>();
        private static readonly AsyncLocal<ConversationDiagnosticRequest> DiagnosticRequest = new AsyncLocal<ConversationDiagnosticRequest>();
        private static readonly AsyncLocal<ConversationDiagnosticTrace> DiagnosticTrace = new AsyncLocal<ConversationDiagnosticTrace>();
        private static readonly AsyncLocal<ConversationDiagnosticStep> DiagnosticStep = new AsyncLocal<ConversationDiagnosticStep>();
        private static readonly ConcurrentDictionary<string, ConversationDiagnosticTrace> ActiveDiagnosticTraces = new ConcurrentDictionary<string, ConversationDiagnosticTrace>();
        private static readonly object DiagnosticSettingsLock = new object();
        private static readonly object DiagnosticExportLock = new object();
        private static string DiagnosticLastError = "";
        private static readonly ConcurrentDictionary<string, ConversationDiagnosticStep> DiagnosticCodexThreads = new ConcurrentDictionary<string, ConversationDiagnosticStep>();
        private static string DiagnosticRoot => DiagnosticRootOverride.Value ?? Path.Combine(DataDir, "diagnostics", "conversations");

        private static string DiagnosticRedact(string text, IEnumerable<string> credentials = null)
        {
            string clean = text ?? "";
            foreach (string credential in credentials ?? Array.Empty<string>())
                if (!string.IsNullOrEmpty(credential) && credential.Length >= 4) clean = clean.Replace(credential, "[REDACTED]");
            clean = Regex.Replace(clean, @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+", "Bearer [REDACTED]");
            // Credential fields only: a character's 'secrets', 'thoughts' and the
            // provider's reasoning are evidence, not authentication material.
            clean = Regex.Replace(clean, @"(?i)(""(?:api[_-]?key|access[_-]?token|refresh[_-]?token|password|client[_-]?secret|authorization)""\s*:\s*)""(?:[^""\\]|\\.)*""", "$1\"[REDACTED]\"");
            clean = Regex.Replace(clean, @"(?i)\b(api[_-]?key|access[_-]?token|refresh[_-]?token|password|client[_-]?secret)=([^\s&""'<>]+)", "$1=[REDACTED]");
            return Regex.Replace(clean, @"(https?://)[^\s/@]+:[^\s/@]+@", "$1[REDACTED]@");
        }

        private static void DiagnosticFailure(Exception ex)
        {
            DiagnosticLastError = DiagnosticRedact(ex.GetType().Name + ": " + ex.Message);
            var trace = DiagnosticTrace.Value;
            if (trace != null) trace.CaptureError = DiagnosticLastError;
        }

        private static Dictionary<string, object> DiagnosticReadJson(string path)
        {
            if (!File.Exists(path)) return new Dictionary<string, object>();
            return Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
        }

        private static void DiagnosticWriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, Json.Serialize(value), new UTF8Encoding(false));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static bool DiagnosticFullCaptureEnabled()
        {
            try { lock (DiagnosticSettingsLock) return ReadBool(DiagnosticReadJson(Path.Combine(DiagnosticRoot, "capture.json")), "enabled", false); }
            catch (Exception ex) { DiagnosticFailure(ex); return false; }
        }

        private sealed class ConversationDiagnosticRequest : IDisposable
        {
            internal readonly Dictionary<string, ConversationDiagnosticTrace> Traces = new Dictionary<string, ConversationDiagnosticTrace>(StringComparer.Ordinal);
            internal readonly Dictionary<string, object> Input;
            internal readonly string Path;
            private readonly ConversationDiagnosticRequest previousRequest;
            private readonly ConversationDiagnosticTrace previousTrace;
            private readonly ConversationDiagnosticStep previousStep;
            internal bool Closed;
            private bool delivered;
            internal ConversationDiagnosticRequest(string path, Dictionary<string, object> input)
            {
                Path = path ?? ""; Input = input ?? new Dictionary<string, object>();
                previousRequest = DiagnosticRequest.Value; previousTrace = DiagnosticTrace.Value; previousStep = DiagnosticStep.Value;
                DiagnosticRequest.Value = this; DiagnosticTrace.Value = null; DiagnosticStep.Value = null;
            }
            internal void Delivered(int status)
            {
                delivered = status < 400;
                foreach (var trace in Snapshot())
                {
                    lock (trace.Gate) trace.Meta["responseElapsedMs"] = trace.Timer.ElapsedMilliseconds;
                    trace.Record("delivery", "Server response delivered", delivered ? "passed" : "failed",
                        "HTTP " + status + ". This confirms the server response, not native screen rendering.", 0,
                        new Dictionary<string, object> { ["httpStatus"] = status, ["nativeDisplayConfirmed"] = false });
                }
            }
            internal List<ConversationDiagnosticTrace> Snapshot() { lock (Traces) return Traces.Values.Distinct().ToList(); }
            public void Dispose()
            {
                Closed = true;
                foreach (var trace in Snapshot()) trace.Finish(delivered ? "completed" : "interrupted");
                DiagnosticRequest.Value = previousRequest; DiagnosticTrace.Value = previousTrace; DiagnosticStep.Value = previousStep;
            }
        }

        private sealed class ConversationDiagnosticTrace
        {
            internal readonly string Id = Guid.NewGuid().ToString("N");
            internal readonly string Root;
            internal readonly bool FullCapture;
            internal readonly object Gate = new object();
            internal readonly Dictionary<string, object> Meta;
            internal readonly Stopwatch Timer = Stopwatch.StartNew();
            internal readonly List<string> Credentials = new List<string>();
            internal string CaptureError = "";
            internal bool Closed;
            private int sequence;
            private int activeCalls;
            private string pendingFinish;
            internal ConversationDiagnosticTrace(string campaign, string correlation, string mode, string hero, Dictionary<string, object> input)
            {
                Root = Path.Combine(DiagnosticRoot, "traces", Id);
                FullCapture = DiagnosticFullCaptureEnabled();
                Meta = new Dictionary<string, object> {
                    ["schema"] = ConversationDiagnosticSchema, ["traceId"] = Id, ["processId"] = DiagnosticProcessId,
                    ["campaignId"] = campaign ?? "", ["correlationId"] = correlation ?? "", ["mode"] = mode ?? "",
                    ["heroId"] = hero ?? "", ["heroName"] = ReadFirstString(input, "heroName", "npcName", "speakerName"),
                    ["timelineId"] = ReadString(input, "timelineId", ""),
                    ["conversationId"] = ReadFirstString(input, "conversationSessionId", "sessionId", "conversationId", "eventId"),
                    ["playerText"] = DiagnosticRedact(ReadFirstString(input, "playerText", "message", "text")), ["reply"] = "",
                    ["startedUtc"] = DateTimeOffset.UtcNow.ToString("o"), ["status"] = "in_progress",
                    ["fullCapture"] = FullCapture, ["stepCount"] = 0, ["providerAttempts"] = 0, ["repairCount"] = 0,
                    ["captureError"] = "", ["nativeDisplayConfirmed"] = false
                };
                ActiveDiagnosticTraces[Id] = this;
                SafeSave();
            }
            internal void SafeSave()
            {
                try
                {
                    lock (Gate)
                    {
                        Meta["elapsedMs"] = ReadLong(Meta, "responseElapsedMs", Timer.ElapsedMilliseconds);
                        Meta["captureElapsedMs"] = Timer.ElapsedMilliseconds;
                        Meta["captureError"] = CaptureError;
                        DiagnosticWriteJson(System.IO.Path.Combine(Root, "manifest.json"), Meta);
                    }
                }
                catch (Exception ex) { CaptureError = DiagnosticRedact(ex.Message); DiagnosticFailure(ex); }
            }
            internal string Payload(object value, string id)
            {
                if (!FullCapture || value == null) return "";
                string original = value as string ?? Json.Serialize(value);
                string text = DiagnosticRedact(original, Credentials);
                string directory = System.IO.Path.Combine(Root, "payloads");
                Directory.CreateDirectory(directory);
                string path = System.IO.Path.Combine(directory, id + ".txt.gz");
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                using (var writer = new StreamWriter(gzip, new UTF8Encoding(false))) writer.Write(text);
                DiagnosticWriteJson(System.IO.Path.Combine(directory, id + ".json"), new Dictionary<string, object> {
                    ["characters"] = text.Length, ["originalCharacters"] = original.Length,
                    ["sha256"] = PromptContentHash(text), ["credentialRedacted"] = text != original,
                    ["hashScope"] = "retained credential-redacted UTF-8 text", ["complete"] = true
                });
                return id;
            }
            internal string Record(string kind, string title, string status, string explanation, long duration, object details = null, string parentId = "", string stepId = "")
            {
                string id = string.IsNullOrWhiteSpace(stepId) ? Guid.NewGuid().ToString("N") : stepId;
                try
                {
                    lock (Gate)
                    {
                        if (Closed) return id;
                        string payloadId = "";
                        try { payloadId = Payload(details, Guid.NewGuid().ToString("N")); }
                        catch (Exception ex) { CaptureError = DiagnosticRedact(ex.Message); DiagnosticFailure(ex); }
                        var row = new Dictionary<string, object> {
                            ["stepId"] = id, ["parentStepId"] = parentId ?? "", ["sequence"] = ++sequence,
                            ["kind"] = kind, ["title"] = title, ["status"] = status, ["timestamp"] = DateTimeOffset.UtcNow.ToString("o"),
                            ["explanation"] = DiagnosticRedact(explanation, Credentials), ["durationMs"] = duration,
                            ["payloadId"] = payloadId, ["payloadAvailability"] = payloadId.Length > 0 ? "captured" : FullCapture && details != null ? "capture_failed" : "not_captured"
                        };
                        Directory.CreateDirectory(Root);
                        File.AppendAllText(System.IO.Path.Combine(Root, "steps.jsonl"), Json.Serialize(row) + "\n", new UTF8Encoding(false));
                        Meta["stepCount"] = sequence;
                        SafeSave();
                    }
                }
                catch (Exception ex) { CaptureError = DiagnosticRedact(ex.Message); DiagnosticFailure(ex); }
                return id;
            }
            internal void Finish(string status)
            {
                lock (Gate)
                {
                    if (Closed) return;
                    if (activeCalls > 0)
                    {
                        pendingFinish = status;
                        Meta["captureInProgress"] = true;
                        SafeSave();
                        return;
                    }
                    Timer.Stop();
                    Meta["captureInProgress"] = false;
                    if (ReadString(Meta, "status", "") == "in_progress") Meta["status"] = status;
                    Meta["completedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                    SafeSave(); Closed = true;
                }
                ActiveDiagnosticTraces.TryRemove(Id, out _);
            }
            internal void HoldCall() { lock (Gate) activeCalls++; }
            internal void ReleaseCall()
            {
                lock (Gate)
                {
                    activeCalls = Math.Max(0, activeCalls - 1);
                    if (activeCalls == 0 && pendingFinish != null) Finish(pendingFinish);
                }
            }
        }

        private static ConversationDiagnosticTrace EnsureDiagnosticTrace(string campaign, string correlation, string mode, string hero, Dictionary<string, object> input = null)
        {
            var context = DiagnosticRequest.Value;
            if (context != null && !context.Closed)
            {
                lock (context.Traces)
                {
                    if (!context.Traces.TryGetValue(correlation ?? "", out var trace))
                    {
                        var effectiveInput = new Dictionary<string, object>(context.Input);
                        if (input != null) foreach (var pair in input) effectiveInput[pair.Key] = pair.Value;
                        trace = new ConversationDiagnosticTrace(campaign, correlation, mode, hero, effectiveInput);
                        context.Traces[correlation ?? ""] = trace;
                    }
                    DiagnosticTrace.Value = trace;
                    return trace;
                }
            }
            return DiagnosticTrace.Value != null && !DiagnosticTrace.Value.Closed ? DiagnosticTrace.Value : null;
        }

        private sealed class ConversationDiagnosticStep : IDisposable
        {
            internal readonly ConversationDiagnosticTrace Trace;
            internal readonly string Id = Guid.NewGuid().ToString("N");
            internal readonly string Parent;
            internal readonly string Kind;
            internal readonly string Title;
            private readonly ConversationDiagnosticStep previous;
            private readonly ConversationDiagnosticTrace previousTrace;
            private readonly Stopwatch timer = Stopwatch.StartNew();
            private readonly bool ownsTrace;
            private bool ended;
            internal bool CompletionDeferred;
            private string endStatus = "interrupted";
            internal ConversationDiagnosticStep(ConversationDiagnosticTrace trace, string kind, string title, object input = null, bool own = false)
            {
                Trace = trace; Kind = kind; Title = title; ownsTrace = own;
                previous = DiagnosticStep.Value; previousTrace = DiagnosticTrace.Value;
                Parent = previous != null && previous.Trace == trace ? previous.Id : "";
                DiagnosticStep.Value = this; DiagnosticTrace.Value = trace;
                if (kind == "llm_call") Trace?.HoldCall();
                Trace?.Record(kind, title, "in_progress", "Started.", 0, input, Parent, Id);
            }
            internal void End(string status, string explanation, object details = null)
            {
                if (ended) return;
                ended = true; endStatus = status; timer.Stop();
                Trace?.Record(Kind, Title, status, explanation, timer.ElapsedMilliseconds, details, Parent, Id);
            }
            public void Dispose()
            {
                if (!CompletionDeferred) End("interrupted", "This step ended without a completion receipt. Inspect the parent error.");
                DiagnosticStep.Value = previous; DiagnosticTrace.Value = previousTrace;
                if (Kind == "llm_call") Trace?.ReleaseCall();
                if (ownsTrace) Trace?.Finish(endStatus == "failed" || endStatus == "interrupted" ? endStatus : "completed");
            }
        }

        private static ConversationDiagnosticStep BeginDiagnosticLlm(Dictionary<string, object> request, string credential = "")
        {
            try
            {
                string correlation = ReadString(request, "correlationId", Guid.NewGuid().ToString("N"));
                string mode = ReadString(request, "requestType", "llm");
                var trace = DiagnosticTrace.Value;
                bool owns = false;
                if (trace == null || trace.Closed)
                {
                    trace = EnsureDiagnosticTrace(ReadString(request, "campaignId", ""), correlation, mode, ReadString(request, "heroStringId", ""), request);
                    if (trace == null) { trace = new ConversationDiagnosticTrace(ReadString(request, "campaignId", ""), correlation, mode, ReadString(request, "heroStringId", ""), request); owns = true; }
                }
                if (DiagnosticRequest.Value != null && !DiagnosticRequest.Value.Closed)
                    lock (DiagnosticRequest.Value.Traces) DiagnosticRequest.Value.Traces[correlation] = trace;
                lock (trace.Gate)
                {
                    if (!string.IsNullOrEmpty(credential)) trace.Credentials.Add(credential);
                    trace.Meta["model"] = ReadString(request, "model", "");
                }
                return new ConversationDiagnosticStep(trace, "llm_call", mode.Contains("repair") ? "Repair model call" : "Model call", request, owns);
            }
            catch (Exception ex) { DiagnosticFailure(ex); return new ConversationDiagnosticStep(null, "llm_call", "Model call"); }
        }

        private static ConversationDiagnosticStep BeginDiagnosticAttempt(string provider, string requestJson, string apiKey = "")
        {
            var trace = DiagnosticTrace.Value;
            if (trace != null)
            {
                lock (trace.Gate)
                {
                    if (!string.IsNullOrEmpty(apiKey)) trace.Credentials.Add(apiKey);
                    trace.Meta["providerAttempts"] = ReadInt(trace.Meta, "providerAttempts", 0) + 1;
                }
            }
            return new ConversationDiagnosticStep(trace, "provider_attempt", "Provider attempt", new Dictionary<string, object> { ["provider"] = DiagnosticRedact(provider), ["requestBody"] = requestJson });
        }

        private static ConversationDiagnosticStep BeginDiagnosticCodexRpc(string method, Dictionary<string, object> parameters)
        {
            if (DiagnosticTrace.Value == null || !(method.StartsWith("turn/") || method.StartsWith("thread/"))) return null;
            var body = new Dictionary<string, object> { ["method"] = method, ["params"] = parameters };
            var step = method == "turn/start"
                ? BeginDiagnosticAttempt("codex-app-server", Json.Serialize(body))
                : new ConversationDiagnosticStep(DiagnosticTrace.Value, "provider_protocol", "Provider protocol: " + method, body);
            string thread = ReadString(parameters, "threadId", "");
            if (method == "turn/start" && thread.Length > 0)
            {
                step.CompletionDeferred = true;
                DiagnosticCodexThreads[thread] = step;
            }
            return step;
        }

        private static void ObserveDiagnosticCodexFrame(string original, Dictionary<string, object> message)
        {
            try
            {
                ConversationDiagnosticStep owner = null;
                long id = ReadLong(message, "id", 0);
                if (id > 0)
                {
                    lock (CodexStateLock) { if (CodexPending.TryGetValue(id, out var waiter)) owner = waiter.Diagnostic; }
                }
                else
                {
                    var parameters = ReadDictionary(message, "params");
                    var turn = ReadDictionary(parameters, "turn");
                    string thread = FirstNonEmpty(ReadString(parameters, "threadId", ""), ReadString(turn, "threadId", ""));
                    if (thread.Length == 0)
                    {
                        string turnId = FirstNonEmpty(ReadString(parameters, "turnId", ""), ReadString(turn, "id", ""));
                        lock (CodexStateLock) { if (CodexOwnedTurnThreads.TryGetValue(turnId, out var known)) thread = known; }
                    }
                    DiagnosticCodexThreads.TryGetValue(thread, out owner);
                }
                owner?.Trace?.Record("provider_event", "Provider event: " + ReadString(message, "method", "RPC response"), "received",
                    "Original provider frame before response assembly. Only events associated with this generation are included.", 0, original, owner.Id);
                if (owner != null && ReadString(message, "method", "") == "turn/completed")
                {
                    var turn = ReadDictionary(ReadDictionary(message, "params"), "turn");
                    string status = ReadString(turn, "status", "completed");
                    owner.End(status == "completed" ? "received" : "failed", "Provider turn " + status + ". Timing includes generation through this completion event.", turn);
                }
            }
            catch (Exception ex) { DiagnosticFailure(ex); }
        }

        private static void DiagnosticValidation(string title, bool accepted, IEnumerable<string> issues, object before = null, object after = null)
        {
            var trace = DiagnosticTrace.Value;
            if (trace == null) return;
            var reasons = (issues ?? Array.Empty<string>()).ToList();
            trace.Record("validation", title, accepted ? "passed" : "rejected", accepted ? "The candidate passed this check." : string.Join(" ", reasons), 0,
                new Dictionary<string, object> { ["issues"] = reasons, ["received"] = before, ["result"] = after }, DiagnosticStep.Value?.Id ?? "");
        }

        private static List<string> DiagnosticStructuredIssues(string content, string mode, Dictionary<string, object> request)
        {
            var issues = new List<string>();
            var parsed = TryParseJsonObject(content);
            if (parsed == null) { issues.Add("The response is not a parseable JSON object."); return issues; }
            if (parsed.Count == 0) { issues.Add("The response is an empty JSON object ({}). It has no dialogue or required decision fields."); return issues; }
            if (IsConversationStructuredMode(NormalizeLookup(mode).Replace(' ', '_')))
            {
                if (!ConversationVisibleReplyPassesRequestQualityGate(content, request, mode))
                    issues.Add(ConversationStructuredObjectHasUsableVisibleReply(parsed) ? "The visible reply only repeats a quoted fragment of the player's message." : "No usable visible reply was supplied.");
                foreach (var aliases in new[] { new[] { "emotion" }, new[] { "intent" }, new[] { "relationshipSignal", "relationship_signal" }, new[] { "relationshipAssessments", "relationship_assessments" } })
                    if (!aliases.Any(parsed.ContainsKey)) issues.Add("Missing required field: " + aliases[0] + ".");
                if (ReadDictionary(parsed, "actionGate") == null && ReadDictionary(parsed, "action_gate") == null) issues.Add("Missing or invalid actionGate object.");
                if (ReadDictionary(parsed, "decisionBrief") == null && ReadDictionary(parsed, "decision_brief") == null) issues.Add("Missing or invalid decisionBrief object.");
                if (!ConversationStructuredCollectionsAreValid(parsed)) issues.Add("One or more structured collections have an invalid type or shape.");
                if (mode != "dialogue" && !parsed.ContainsKey("participation")) issues.Add("Missing participation field for the event response.");
            }
            if (issues.Count == 0 && !StructuredResponseIsComplete(content, mode)) issues.Add("The response does not contain the fields required for " + mode + ".");
            return issues;
        }

        private static Dictionary<string, object> WithDiagnosticTransformation(string title, Dictionary<string, object> input, Func<Dictionary<string, object>> action)
        {
            using var step = new ConversationDiagnosticStep(DiagnosticTrace.Value, "validation", title, new Dictionary<string, object> { ["received"] = ReadString(input, "content", "") });
            string before = ReadString(input, "content", "");
            try
            {
                var result = action();
                string after = ReadString(result, "content", "");
                bool ok = ReadBool(result, "ok", false);
                step.End(ok ? before == after ? "passed" : "changed" : "failed",
                    ok ? before == after ? "The candidate passed without changes." : "The candidate changed. Expand the child decisions and compare received and result below." : ReadString(result, "error", "The candidate did not pass."),
                    new Dictionary<string, object> { ["received"] = before, ["result"] = after, ["outcome"] = result });
                return result;
            }
            catch (Exception ex) { step.End("failed", ex.Message); throw; }
        }

        private static void ObserveDiagnosticAudit(string campaign, string correlation, string mode, string phase, string hero, string status, long duration, string summary, Dictionary<string, object> data)
        {
            try
            {
                // An audit replay/query must never generate new evidence. Normal HTTP
                // processing owns a request scope; background LLM calls own a call scope.
                if (DiagnosticRequest.Value == null && DiagnosticTrace.Value == null) return;
                var trace = EnsureDiagnosticTrace(campaign, correlation, mode, hero);
                if (trace == null) return;
                lock (trace.Gate)
                {
                    string model = ReadString(data, "model", "");
                    if (model.Length > 0) trace.Meta["model"] = model;
                    var payload = ReadDictionary(data, "payload") ?? data;
                    string player = ReadFirstString(payload, "playerText", "message");
                    if (player.Length > 0) trace.Meta["playerText"] = DiagnosticRedact(player, trace.Credentials);
                    string name = FirstNonEmpty(ReadFirstString(payload, "heroName", "npcName", "speakerName"), ReadString(ReadDictionary(data, "npcLine"), "speaker", ""));
                    if (name.Length > 0) trace.Meta["heroName"] = name;
                    var exchange = ReadDictionary(data, "conversationExchange");
                    string session = FirstNonEmpty(ReadString(exchange, "sessionId", ""), ReadFirstString(payload, "conversationSessionId", "sessionId", "conversationId"));
                    if (session.Length > 0) trace.Meta["conversationId"] = session;
                    if (phase.EndsWith(".response", StringComparison.Ordinal) && !phase.StartsWith("llm.", StringComparison.Ordinal))
                    {
                        string reply = ReadString(data, "reply", "");
                        if (reply.Length > 0) trace.Meta["reply"] = DiagnosticRedact(reply, trace.Credentials);
                        trace.Meta["status"] = status.Contains("fail") ? "failed" : "completed";
                    }
                    if ((phase.Contains("repair") || phase == "llm.format_retry" || phase == "llm.schema_completion") && !phase.EndsWith(".request") && !phase.EndsWith(".response"))
                        trace.Meta["repairCount"] = ReadInt(trace.Meta, "repairCount", 0) + 1;
                }
                string explanation = DiagnosticAuditExplanation(phase, summary, data);
                trace.Record(phase.Contains("repair") || phase == "llm.format_retry" ? "repair" : "audit", DiagnosticPhaseTitle(phase), status, explanation, duration, data, DiagnosticStep.Value?.Id ?? "");
            }
            catch (Exception ex) { DiagnosticFailure(ex); }
        }

        private static string DiagnosticAuditExplanation(string phase, string summary, Dictionary<string, object> data)
        {
            var detected = ReadDictionaryList(data, "detected");
            if (detected.Count > 0) return string.Join(" ", detected.Select(x => ReadString(x, "detail", ReadString(x, "type", ""))));
            var issues = ReadStringList(data, "issues");
            if (issues.Count > 0) return string.Join(" ", issues);
            if (phase == "llm.format_retry") return "A replacement was requested after the structured-answer check rejected the previous candidate. The validation step records the exact missing or invalid fields.";
            return summary ?? "";
        }

        private static string DiagnosticPhaseTitle(string phase)
        {
            switch (phase)
            {
                case "context.loaded": return "Load character and scene context";
                case "context.select": return "Select relevant context";
                case "prompt.built": return "Assemble the prompt";
                case "llm.request": return "Prepared model request";
                case "llm.response": return "Processed model result";
                case "llm.format_retry": return "Repair the answer format";
                case "llm.schema_completion": return "Complete missing metadata locally";
                case "llm.agency_repair": return "Repair negotiation metadata";
                case "llm.roleplay_continuity_repair": return "Repair continuity or repeated narration";
                case "model.parsed": return "Parse the accepted answer";
                case "action.auto_queue": return "Decide whether to queue an action";
                case "drinking.receipt": return "Check narrated drinking against game effects";
                default: return (phase ?? "Step").Replace('.', ' ').Replace('_', ' ');
            }
        }
    }
}
