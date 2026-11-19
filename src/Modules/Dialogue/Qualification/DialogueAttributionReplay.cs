using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private const string DialogueAttributionReplaySchema = "reign-dialogue-attribution-replay-v1";

        // A bounded guard trace, not a substitute for the original native payload or
        // a publication certificate. This entry has no campaign/settings/path knobs.
        private static string DialogueAttributionReplayError(Dictionary<string, object> input)
        {
            if (input == null || !IsConversationObjectArray(input.GetValueOrDefault("cases")) || !input.Keys.ToHashSet().SetEquals(new[] { "schema", "cases" })
                || ReadString(input, "schema", "") != DialogueAttributionReplaySchema) return "invalid_header";
            var cases = ReadDictionaryList(input, "cases");
            if (cases.Count is < 1 or > 64 || cases.Count != cases.Select(c => ReadString(c, "caseId", "")).Distinct().Count()) return "invalid_cases";
            foreach (var item in cases)
            {
                if (!item.Keys.Where(k => k != "answerSupportCaptures" && k != "lateRepairDraft").ToHashSet().SetEquals(new[] { "caseId", "observerId", "peerId", "query", "worldDay", "asOfSequence", "sources", "profiles", "participants", "draft" })
                    || !IsConversationObjectArray(item.GetValueOrDefault("sources")) || !IsConversationObjectArray(item.GetValueOrDefault("profiles"))
                    || ReadString(item, "observerId", "") == ReadString(item, "peerId", "")
                    || ReadString(item, "caseId", "").Length is < 1 or > 160 || ReadString(item, "query", "").Length is < 1 or > 24000
                    || ReadString(item, "observerId", "").Length is < 1 or > 160 || ReadString(item, "peerId", "").Length is < 1 or > 160
                    || !double.IsFinite(ReadDouble(item, "worldDay", double.NaN)) || ReadLong(item, "asOfSequence", -1) < 0
                    || ReadDictionary(item, "draft") == null || Json.Serialize(item).Length > 1024 * 1024
                    || item.ContainsKey("lateRepairDraft") && ReadDictionary(item, "lateRepairDraft") == null) return "invalid_case";
                if (item.ContainsKey("answerSupportCaptures") && (!IsConversationObjectArray(item["answerSupportCaptures"])
                    || ReadDictionaryList(item, "answerSupportCaptures").Count > AnswerSupportMaxCalls
                    || ReadDictionaryList(item, "answerSupportCaptures").Any(c => !c.Keys.ToHashSet().SetEquals(new[] { "requestHash", "response" })
                        || !System.Text.RegularExpressions.Regex.IsMatch(ReadString(c, "requestHash", ""), "^[A-Fa-f0-9]{64}$")
                        || ReadDictionary(c, "response") == null || Json.Serialize(c).Length > 64000))) return "invalid_answer_support_capture";
                var profiles = ReadDictionaryList(item, "profiles");
                var actors = profiles.Select(p => ReadString(p, "id", "")).ToHashSet();
                if (profiles.Count is < 2 or > 24 || actors.Count != profiles.Count || actors.Contains("")
                    || profiles.Any(p => p.Keys.Any(k => k != "id" && k != "name") || ReadString(p, "id", "").Length > 160
                        || ReadString(p, "name", "").Length is < 1 or > 160)
                    || !actors.Contains(ReadString(item, "observerId", "")) || !actors.Contains(ReadString(item, "peerId", ""))) return "invalid_profiles";
                var participants = ReadStringList(item, "participants");
                if (participants.Count is < 2 or > 24 || participants.Count != participants.Distinct().Count() || participants.Any(p => !actors.Contains(p))
                    || !participants.Contains(ReadString(item, "observerId", "")) || !participants.Contains(ReadString(item, "peerId", ""))) return "invalid_participants";
                var sources = ReadDictionaryList(item, "sources");
                if (sources.Count is < 1 or > 512 || sources.Count != sources.Select(s => ReadString(s, "sourceId", "")).Distinct().Count()) return "invalid_sources";
                foreach (var source in sources)
                    if (source.Keys.Any(k => !new[] { "sourceId", "speakerId", "text", "role", "sequence", "worldDay", "audience", "sessionId" }.Contains(k))
                        || ReadString(source, "sourceId", "").Length is < 1 or > 200 || !actors.Contains(ReadString(source, "speakerId", ""))
                        || !new[] { "player", "npc" }.Contains(ReadString(source, "role", ""))
                        || ReadString(source, "text", "").Length is < 1 or > 24000
                        || ReadLong(source, "sequence", -1) < 0 || ReadLong(source, "sequence", long.MaxValue) > ReadLong(item, "asOfSequence", 0)
                        || !double.IsFinite(ReadDouble(source, "worldDay", double.NaN)) || ReadDouble(source, "worldDay", double.MaxValue) > ReadDouble(item, "worldDay", 0)
                        || !ReadStringList(source, "audience").Contains(ReadString(item, "observerId", ""))
                        || ReadStringList(source, "audience").Any(a => !actors.Contains(a))) return "invalid_source";
            }
            return "";
        }

        private static Dictionary<string, object> AttributionReplayPayload(Dictionary<string, object> item)
        {
            return TestDict("playerHeroStringId", item["peerId"], "playerText", item["query"], "worldDay", item["worldDay"],
                "participants", item["participants"], "participantHeroIds", item["participants"], "participantProfiles", ReadDictionaryList(item, "profiles")
                    .Select(p => TestDict("heroStringId", p["id"], "name", p["name"])).ToList());
        }

        private static Dictionary<string, object> TraceDialogueAttributionCase(Dictionary<string, object> item)
        {
            var observer = ReadString(item, "observerId", "");
            var payload = AttributionReplayPayload(item);
            var prior = ReadDictionaryList(item, "sources").Select(s => TestDict("id", s["sourceId"], "turnId", s["sourceId"],
                "speakerId", s["speakerId"], "role", s["role"], "text", s["text"], "worldDay", s["worldDay"],
                "acceptedSequence", s["sequence"], "audienceIds", s["audience"], "sessionId", ReadString(s, "sessionId", ""))).ToList();
            var parsed = CloneDictionary(ReadDictionary(item, "draft"));
            var plan = BuildDialogueClaimPlan(parsed, payload, prior, observer);
            var issues = FindDialogueIntegrityViolations(parsed, payload, prior, observer);
            int repairs = 0;
            var supportRequests = new List<Dictionary<string, object>>();
            var captures = ReadDictionaryList(item, "answerSupportCaptures");
            var replayRequest = TestDict("requestType", "dialogue", "messages", new List<Dictionary<string, object>>());
            Dictionary<string, object> Respond(Dictionary<string, object> check)
            {
                    if (ReadString(check, "answerSupportSchema", "") == DialogueAnswerSupportSchema)
                    {
                        string beforeAdapter = PromptHash(CanonicalJson(check));
                        PrepareCountedRouteProviderRequest(check, replayRequest);
                        string hash = PromptHash(CanonicalJson(check));
                        var wireSettings = TestDict("reasoningMode", "selective", "reasoningEffort", "high");
                        var wire = LlmProviderService.BuildChatRequestBody(wireSettings, check, ReadDictionaryList(check, "messages"), "");
                        ConfigurePromptCacheRouting(wireSettings, check, wire, PortraitsService.NanoGptChatCompletionsUrl, "", "dialogue_answer_support");
                        ConfigureReasoningRouting(wireSettings, check, wire, PortraitsService.NanoGptChatCompletionsUrl, "", "dialogue_answer_support");
                        supportRequests.Add(TestDict("requestHash", hash, "request", check, "wireTemplate", wire,
                            "nativeReferenceSealed", IsSealedAnswerSupportAudit(check), "routeAdapterPreservedRequest", beforeAdapter == hash,
                            "wireTemplateScope", "isolated_NanoGPT_HIGH_template_model_is_bound_by_subscription_dispatcher"));
                        var match = captures.Where(c => ReadString(c, "requestHash", "") == hash).ToList();
                        return match.Count == 1 ? CloneDictionary(ReadDictionary(match[0], "response"))
                            : TestDict("ok", false, "errorCode", "answer_support_capture_missing");
                    }
                    repairs++; return TestDict("ok", false, "error", "offline_repair_capture_missing");
            }
            bool lateApplied = false; int earlyAudits = 0;
            var result = FinishDialogueRepairsWithAnswerSupport(() =>
            {
                var initial = RetryDialogueIntegrityViolationCore(TestDict("ok", true, "content", Json.Serialize(parsed)), replayRequest, payload, prior,
                    "", "isolated-attribution", "dialogue", observer, "attribution-trace", Respond, persist: false);
                earlyAudits = supportRequests.Count;
                if (!ReadBool(initial, "ok", false) || !item.ContainsKey("lateRepairDraft")) return initial;
                lateApplied = true;
                return TestDict("ok", true, "content", Json.Serialize(CloneDictionary(ReadDictionary(item, "lateRepairDraft"))));
            }, replayRequest, payload, prior, observer, Respond);
            return TestDict("caseId", item["caseId"], "inputDraftHash", MemorySelectionTextHash(Json.Serialize(parsed)),
                "claimPlan", plan, "integrityViolations", issues, "repairRequests", repairs, "answerSupportRequests", supportRequests, "result", result,
                "lateRepairApplied", lateApplied, "sourceAuditsBeforeLateRepair", earlyAudits,
                "lateRepairScope", "controlled_draft_injection_not_full_native_route_pipeline",
                "providerCalls", 0, "publicationPerformed", false, "nextReadPerformed", false,
                "coverage", "reconstructed_observer_wire_sources_not_native_payload_parity", "certificateEligible", false);
        }

        private static Dictionary<string, object> RunDialogueAttributionReplay(Dictionary<string, object> input, string hash)
        {
            if (Program.ActiveServerPort > 0 || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") != "1"
                || !Program.HasArg(Environment.GetCommandLineArgs(), "--run-verification") || string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value))
                throw new InvalidOperationException("Attribution trace requires isolated artifact-bound verification.");
            string error = DialogueAttributionReplayError(input);
            if (error.Length > 0) throw new InvalidOperationException("Invalid attribution trace: " + error);
            var oldHelper = DialogueClaimHelperReplay.Value;
            var oldExtractor = DialogueClaimExtractorReplay.Value;
            try
            {
                // Isolated CompareDialogueRecallClaims already refuses dispatch
                // without captures. Do not inherit a caller's test helper either.
                DialogueClaimHelperReplay.Value = null;
                DialogueClaimExtractorReplay.Value = null;
                var rows = ReadDictionaryList(input, "cases").Select(TraceDialogueAttributionCase).ToList();
                var report = TestDict("schema", DialogueAttributionReplaySchema, "ok", true, "inputSha256", hash,
                    "cases", rows, "providerCalls", 0, "publicationPerformed", false, "certificateEligible", false);
                string path = Path.Combine(Program.TestsDir, "memory-accuracy", "attribution-trace-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                CampaignFileService.WriteJsonObject(path, report);
                return TestDict("ok", true, "resultPath", path, "cases", rows.Count, "providerCalls", 0, "certificateEligible", false);
            }
            finally { DialogueClaimHelperReplay.Value = oldHelper; DialogueClaimExtractorReplay.Value = oldExtractor; }
        }
    }
}
