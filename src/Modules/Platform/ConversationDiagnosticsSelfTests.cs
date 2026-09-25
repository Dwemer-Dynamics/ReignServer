using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationDiagnosticsSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> check = (id, pass, data) => results.Add(TestDict("caseId", "diagnostic_" + id,
                "passed", pass, "summary", id, "data", data));
            string previousRoot = DiagnosticRootOverride.Value, previousError = DiagnosticLastError;
            var previousTrace = DiagnosticTrace.Value;
            var previousRequest = DiagnosticRequest.Value;
            var previousStep = DiagnosticStep.Value;
            string root = Path.Combine(VerificationDir, "conversation-diagnostics", Guid.NewGuid().ToString("N"));
            DiagnosticRootOverride.Value = root;
            string campaign = "diagnostic_test_" + Guid.NewGuid().ToString("N");
            try
            {
                check("default_off", !DiagnosticFullCaptureEnabled(), null);
                var off = new ConversationDiagnosticTrace(campaign, "off", "dialogue", "corein", TestDict("playerText", "Hello."));
                off.Record("provider_attempt", "Provider attempt", "received", "Summary only.", 3, "private uncaptured original"); off.Finish("completed");
                check("off_has_no_payload", !Directory.Exists(Path.Combine(off.Root, "payloads")) && !File.ReadAllText(Path.Combine(off.Root, "steps.jsonl")).Contains("private uncaptured"), null);
                ConversationDiagnosticsCaptureApi(TestDict("enabled", true));
                check("capture_persists", DiagnosticFullCaptureEnabled() && ReadBool(ConversationDiagnosticsCaptureApi(), "survivesRestart", false), null);

                ConversationDiagnosticTrace full;
                string secret = "fixture-" + "credential-value";
                string original = "{\"reasoning\":\"Consider her position.\"}";
                string cleaned = SanitizeReasoningContent(original);
                using (var requestScope = new ConversationDiagnosticRequest("/social-event/respond", TestDict("heroName", "Corein", "playerText", "I have won several tournaments.", "conversationSessionId", "fixture-evening", "timelineId", "fixture-timeline")))
                {
                    full = EnsureDiagnosticTrace(campaign, "fixture-response", "social_event", "corein");
                    using (var call = BeginDiagnosticLlm(TestDict("campaignId", campaign, "correlationId", "fixture-response", "requestType", "social_event", "model", "fixture-model", "messages", new[] { TestDict("role", "user", "content", "Retain <script>unsafe()</script> as text. " + secret) }), secret))
                    {
                        using (var attempt = BeginDiagnosticAttempt("fixture-provider", "{\"model\":\"fixture-model\"}", secret))
                            attempt.End("failed", "HTTP 503: temporary provider failure.", TestDict("httpStatus", 503, "responseBody", "provider temporarily unavailable"));
                        using (var attempt = BeginDiagnosticAttempt("fixture-provider", "{\"model\":\"fixture-model\"}", secret))
                            attempt.End("received", "Original provider response before cleanup.", TestDict("httpStatus", 200, "responseBody", Json.Serialize(TestDict("choices", new[] { TestDict("message", TestDict("content", original, "reasoning_content", "A private fictional thought.")) }, "usage", TestDict("prompt_tokens", 34000))), "authorization", "Bearer " + secret));
                        full.Record("cleanup", "Extract and clean provider content", "changed", "Reasoning-only JSON became an empty object.", 0, TestDict("extracted", original, "cleaned", cleaned), call.Id);
                        call.End("completed", "Transport succeeded; answer requires validation.", TestDict("content", cleaned));
                    }
                    check("reasoning_only_becomes_empty", TryParseJsonObject(cleaned)?.Count == 0 && DiagnosticStructuredIssues(cleaned, "social_event", TestDict()).Any(x => x.Contains("empty JSON")), null);
                    check("literal_empty_rejected", DiagnosticStructuredIssues("{}", "social_event", TestDict()).Any(x => x.Contains("empty JSON")), null);
                    var missing = AgencyTestReply(null); missing.Remove("proposalDecisions");
                    using (var repair = new ConversationDiagnosticStep(full, "repair", "Repair the answer format", TestDict("received", cleaned)))
                    {
                        DiagnosticValidation("Check answer structure", false, DiagnosticStructuredIssues(cleaned, "social_event", TestDict()), cleaned);
                        using (var replacement = BeginDiagnosticLlm(TestDict("campaignId", campaign, "correlationId", "fixture-response-format", "requestType", "format_repair", "messages", new[] { TestDict("role", "system", "content", "Repair the empty answer and retain the character's voice.") })))
                        {
                            using (var attempt = BeginDiagnosticAttempt("fixture-provider", "{\"repair\":true}")) attempt.End("received", "Replacement answer received.", Json.Serialize(missing));
                            replacement.End("completed", "Replacement provided.", TestDict("content", Json.Serialize(missing)));
                        }
                        repair.End("changed", "The replacement has dialogue; negotiation metadata is checked next.", TestDict("received", cleaned, "result", missing));
                        ObserveDiagnosticAudit(campaign, "fixture-response", "social_event", "llm.format_retry", "corein", "completed", 0, "The empty answer was replaced.", TestDict("retryOk", true));
                    }
                    int repairCalls = 0;
                    var llm = TestDict("ok", true, "content", Json.Serialize(missing));
                    var repaired = WithDiagnosticTransformation("Check negotiation metadata", llm, () => EnforceConversationAgencyResponse(llm,
                        TestDict("requestType", "social_event"), TestDict("conversationAgency", AgencyTestContext()), TestDict(), campaign, "fixture-response", "social_event", "corein",
                        input => { repairCalls++; using var call = BeginDiagnosticLlm(input); var result = TestDict("ok", true, "content", Json.Serialize(AgencyTestReply(null))); call.End("completed", "Injected repair.", result); return result; }, false));
                    check("agency_reason_and_revalidation", repairCalls == 1 && ReadBool(ReadDictionary(repaired, "conversationAgency"), "accepted", false), null);
                    // Toggling off affects the next response, not this in-flight capture.
                    ConversationDiagnosticsCaptureApi(TestDict("enabled", false));
                    string longText = new string('x', 12000 - 1) + "\U0001F451" + new string('y', 14500) + " secret=fictional-royal-plan " + secret;
                    full.Record("evidence", "Large complete evidence", "received", "Paging fixture.", 0, longText);
                    ObserveDiagnosticAudit(campaign, "fixture-response", "social_event", "social_event.response", "corein", "completed", 88159, "Final response.",
                        TestDict("reply", "Your tournament victories have earned notice. What brings you here?", "conversationExchange", TestDict("sessionId", "fixture-evening")));
                    requestScope.Delivered(200);
                }
                check("inflight_capture_finishes", full.FullCapture && !DiagnosticFullCaptureEnabled() && full.Closed, null);
                var trace = ConversationDiagnosticsTraceApi(new Dictionary<string, string> { ["traceId"] = full.Id });
                var steps = ReadDictionaryList(trace, "steps");
                check("exact_parent_binding", ReadInt(full.Meta, "providerAttempts", 0) == 3 && steps.Count(x => ReadString(x, "kind", "") == "llm_call") == 3
                    && steps.Where(x => ReadString(x, "parentStepId", "").Length > 0).All(x => steps.Any(p => ReadString(p, "stepId", "") == ReadString(x, "parentStepId", ""))), steps.Count);
                check("actual_failed_rule_retained", steps.Any(x => ReadString(x, "explanation", "").Contains("proposalDecisions")) && steps.Any(x => ReadString(x, "title", "") == "Recheck negotiation repair" && ReadString(x, "status", "") == "passed"), null);
                var texts = new Dictionary<string, object>();
                bool secretsAbsent = true, paged = false, hashMatches = true;
                foreach (var evidence in steps.SelectMany(x => ReadDictionaryList(x, "evidence")))
                {
                    string id = ReadString(evidence, "payloadId", "");
                    var content = new StringBuilder(); int offset = 0; Dictionary<string, object> page;
                    do
                    {
                        page = ConversationDiagnosticsPayloadApi(new Dictionary<string, string> { ["traceId"] = full.Id, ["payloadId"] = id, ["offset"] = offset.ToString(), ["length"] = "12000" });
                        string part = ReadString(page, "content", "");
                        hashMatches &= part.Length == 0 || (!char.IsLowSurrogate(part[0]) && !char.IsHighSurrogate(part[part.Length - 1]));
                        content.Append(part); offset = ReadInt(page, "nextOffset", 0); paged |= ReadBool(page, "hasMore", false);
                    } while (ReadBool(page, "hasMore", false));
                    string retained = content.ToString(); var metadata = ReadDictionary(page, "metadata");
                    hashMatches &= PromptContentHash(retained) == ReadString(metadata, "sha256", ""); secretsAbsent &= !retained.Contains(secret);
                    texts[id] = TestDict("content", retained, "metadata", metadata);
                }
                check("credential_exclusion", secretsAbsent, null);
                check("immutable_unicode_paging_hash", paged && hashMatches, null);
                check("original_reasoning_preserved", texts.Values.Cast<Dictionary<string, object>>().Any(x => ReadString(x, "content", "").Contains("private fictional thought")), null);
                check("fictional_secrets_preserved", texts.Values.Cast<Dictionary<string, object>>().Any(x => ReadString(x, "content", "").Contains("secret=fictional-royal-plan")), null);
                check("summary_has_no_originals", !Json.Serialize(DiagnosticMeta(full.Id)).Contains("private fictional thought"), null);
                var list = ConversationDiagnosticsListApi(new Dictionary<string, string> { ["campaignId"] = campaign, ["conversationKey"] = DiagnosticConversationKey(full.Meta), ["limit"] = "1" });
                check("conversation_paging", ReadDictionaryList(list, "responses").Count == 1 && ReadDictionaryList(list, "responses").Single()["traceId"].ToString() == full.Id, null);
                check("persisted_trace_reload", !ActiveDiagnosticTraces.ContainsKey(full.Id) && ReadString(ReadDictionary(trace, "response"), "status", "") == "completed", null);
                using (var zipBytes = new MemoryStream())
                {
                    WriteConversationDiagnosticArchive(zipBytes, new List<Dictionary<string, object>> { DiagnosticMeta(full.Id) });
                    zipBytes.Position = 0;
                    using var archive = new System.IO.Compression.ZipArchive(zipBytes, System.IO.Compression.ZipArchiveMode.Read);
                    check("export_complete_and_confined", archive.GetEntry(full.Id + "/manifest.json") != null && archive.GetEntry(full.Id + "/steps.jsonl") != null
                        && archive.Entries.Count(x => x.FullName.EndsWith(".txt.gz")) == texts.Count && archive.Entries.All(x => x.FullName == "export.json" || x.FullName.StartsWith(full.Id + "/")), null);
                }
                DiagnosticWriteJson(Path.Combine(root, "browser-fixture.json"), TestDict("list", ConversationDiagnosticsListApi(new Dictionary<string, string> { ["campaignId"] = campaign }), "responseList", list, "trace", trace, "payloads", texts));
                check("browser_fixture", true, Path.Combine(root, "browser-fixture.json"));
                File.AppendAllText(Path.Combine(full.Root, "steps.jsonl"), "{\"incomplete\":");
                var damaged = ConversationDiagnosticsTraceApi(new Dictionary<string, string> { ["traceId"] = full.Id });
                check("damaged_tail_preserves_valid_steps", ReadDictionaryList(damaged, "steps").Count == steps.Count && !ReadBool(damaged, "complete", true)
                    && ReadString(ReadDictionary(damaged, "response"), "captureError", "").Contains("partial"), null);

                var parallelIds = new System.Collections.Concurrent.ConcurrentBag<string>();
                ConversationDiagnosticsCaptureApi(TestDict("enabled", true));
                var earlyRequest = new ConversationDiagnosticRequest("/dialogue", TestDict());
                var lateCall = BeginDiagnosticLlm(TestDict("campaignId", campaign, "correlationId", "outliving-call", "requestType", "memory"));
                earlyRequest.Delivered(200); earlyRequest.Dispose();
                bool stillRecording = !lateCall.Trace.Closed;
                lateCall.End("completed", "Background result after server delivery.", "late captured result"); lateCall.Dispose();
                var lateTrace = ConversationDiagnosticsTraceApi(new Dictionary<string, string> { ["traceId"] = lateCall.Trace.Id });
                check("background_outlives_delivery", stillRecording && lateCall.Trace.Closed && ReadDictionaryList(lateTrace, "steps").Any(x => ReadString(x, "explanation", "").Contains("Background result") && ReadDictionaryList(x, "evidence").Count > 0)
                    && ReadLong(lateCall.Trace.Meta, "elapsedMs", -1) == ReadLong(lateCall.Trace.Meta, "responseElapsedMs", -2), null);
                ConversationDiagnosticsCaptureApi(TestDict("enabled", false));
                using (var request = new ConversationDiagnosticRequest("/dialogue", TestDict()))
                {
                    using var call = BeginDiagnosticLlm(TestDict("campaignId", campaign, "correlationId", "codex-fixture", "requestType", "dialogue"));
                    string thread = "diagnostic-fixture-" + Guid.NewGuid().ToString("N");
                    ConversationDiagnosticStep attempt;
                    using (attempt = BeginDiagnosticCodexRpc("turn/start", TestDict("threadId", thread, "input", "Fixture request"))) { }
                    var beforeEnd = ConversationDiagnosticsTraceApi(new Dictionary<string, string> { ["traceId"] = call.Trace.Id });
                    var completion = TestDict("method", "turn/completed", "params", TestDict("threadId", thread, "turn", TestDict("id", "fixture-turn", "status", "completed")));
                    ObserveDiagnosticCodexFrame(Json.Serialize(completion), completion);
                    var afterEnd = ConversationDiagnosticsTraceApi(new Dictionary<string, string> { ["traceId"] = call.Trace.Id });
                    check("codex_attempt_lasts_until_completion", ReadDictionaryList(beforeEnd, "steps").Any(x => ReadString(x, "stepId", "") == attempt.Id && ReadString(x, "status", "") == "in_progress")
                        && ReadDictionaryList(afterEnd, "steps").Any(x => ReadString(x, "stepId", "") == attempt.Id && ReadString(x, "status", "") == "received"), null);
                    DiagnosticCodexThreads.TryRemove(thread, out _);
                    call.End("completed", "Protocol fixture finished."); request.Delivered(200);
                }
                Task.WaitAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => {
                    using var request = new ConversationDiagnosticRequest("/dialogue", TestDict("conversationSessionId", "parallel-" + i));
                    using var call = BeginDiagnosticLlm(TestDict("campaignId", campaign, "correlationId", "same-correlation", "requestType", "dialogue"));
                    parallelIds.Add(call.Trace.Id); call.End("completed", "Independent response."); request.Delivered(200);
                })).ToArray());
                check("concurrent_same_id_isolation", parallelIds.Distinct().Count() == 6 && parallelIds.Select(DiagnosticMeta).Select(DiagnosticConversationKey).Distinct().Count() == 6, null);
                var interrupted = new ConversationDiagnosticTrace(campaign, "aborted", "other", "", TestDict());
                interrupted.Record("provider_attempt", "Provider attempt", "in_progress", "Started", 0); ActiveDiagnosticTraces.TryRemove(interrupted.Id, out _);
                check("restart_marks_interrupted", ReadString(DiagnosticMeta(interrupted.Id), "status", "") == "interrupted", null);
                interrupted.Finish("interrupted");
                bool traversalBlocked = false, activeBlocked = false;
                try { ConversationDiagnosticsTraceApi(new Dictionary<string, string> { ["traceId"] = "../../campaigns" }); } catch (ArgumentException) { traversalBlocked = true; }
                var active = new ConversationDiagnosticTrace(campaign, "active", "other", "", TestDict());
                try { ConversationDiagnosticsClearApi(TestDict("traceIds", new[] { active.Id }, "confirmation", "clear selected diagnostic captures")); } catch (InvalidOperationException) { activeBlocked = true; }
                active.Finish("completed");
                check("confined_clear_guard", traversalBlocked && activeBlocked, null);
                var cleared = ConversationDiagnosticsClearApi(TestDict("traceIds", new[] { active.Id }, "confirmation", "clear selected diagnostic captures"));
                check("only_selected_capture_deleted", ReadInt(cleared, "deletedCaptures", 0) == 1 && Directory.Exists(full.Root) && File.Exists(Path.Combine(root, "capture.json")), null);
                string blocker = Path.Combine(root, "blocked"); File.WriteAllText(blocker, "file, not directory"); DiagnosticRootOverride.Value = blocker;
                var failed = new ConversationDiagnosticTrace(campaign, "disk-failure", "other", "", TestDict()); failed.Record("test", "Recording failure", "completed", "Game response must continue.", 0, TestDict()); failed.Finish("completed");
                check("storage_failure_nonfatal_visible", !string.IsNullOrWhiteSpace(failed.CaptureError) && !string.IsNullOrWhiteSpace(DiagnosticLastError), null);
            }
            catch (Exception ex) { check("unexpected_exception", false, ex.ToString()); }
            finally { DiagnosticRootOverride.Value = previousRoot; DiagnosticLastError = previousError; DiagnosticTrace.Value = previousTrace; DiagnosticRequest.Value = previousRequest; DiagnosticStep.Value = previousStep; }
            return results;
        }
    }
}
