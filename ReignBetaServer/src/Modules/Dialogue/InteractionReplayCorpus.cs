using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string ReplayCorpusDir(string campaignId)
        {
            string path = CampaignFile(campaignId, "audit", "test-data", "replay-corpus");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string ReplayCorpusCasePath(string campaignId, string caseId)
        {
            string safe = SafePathSegment(caseId, "case");
            if (safe.Length > 72)
            {
                safe = "replay_" + PromptHash(safe).Substring(0, 20).ToLowerInvariant();
            }
            return Path.Combine(ReplayCorpusDir(campaignId), safe + ".json");
        }

        private static Dictionary<string, object> ReplayCorpusCaptureApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", ResolveLogCampaignId(""));
            string correlationId = ReadString(payload, "correlationId", "");
            if (string.IsNullOrWhiteSpace(correlationId)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "correlationId is required." };
            List<Dictionary<string, object>> entries = ReadJsonLinesFromPath(CampaignFile(campaignId, "audit", "audit.jsonl"))
                .Where(row => string.Equals(ReadString(row, "correlationId", ""), correlationId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (entries.Count == 0) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "No audit evidence was found for the correlation." };

            Dictionary<string, object> responseEntry = entries.LastOrDefault(row => ReadString(row, "phase", "").EndsWith(".response", StringComparison.OrdinalIgnoreCase)) ?? new Dictionary<string, object>();
            Dictionary<string, object> llmEntry = entries.LastOrDefault(row => ReadString(row, "phase", "").Equals("llm.response", StringComparison.OrdinalIgnoreCase)) ?? new Dictionary<string, object>();
            Dictionary<string, object> promptEntry = entries.LastOrDefault(row => ReadString(row, "phase", "").Equals("prompt.built", StringComparison.OrdinalIgnoreCase)) ?? new Dictionary<string, object>();
            Dictionary<string, object> requestEntry = entries.FirstOrDefault(row => ReadString(row, "phase", "").EndsWith(".request", StringComparison.OrdinalIgnoreCase)) ?? entries.First();
            string caseId = FirstNonEmpty(ReadString(payload, "caseId", ""), "replay_" + SafePathSegment(correlationId, "case"));
            Dictionary<string, object> evidenceBundle =
                ReadDictionary(payload, "evidenceBundle");
            bool evidenceV3 = ReadLong(payload, "evidenceSchemaVersion", 1) == 3
                && string.Equals(
                    ReadString(evidenceBundle, "schema", ""),
                    "reign-final-gauntlet-evidence-v3",
                    StringComparison.Ordinal);
            Dictionary<string, object> replayCase = new Dictionary<string, object>
            {
                ["version"] = evidenceV3 ? 3 : 1, ["caseId"] = caseId, ["campaignId"] = campaignId, ["correlationId"] = correlationId,
                ["mode"] = ReadString(responseEntry, "mode", ReadString(requestEntry, "mode", "dialogue")),
                ["capturedUtc"] = DateTime.UtcNow.ToString("o"), ["label"] = ReadString(payload, "label", caseId),
                ["request"] = ReadDictionary(requestEntry, "data") ?? new Dictionary<string, object>(),
                ["promptEvidence"] = ReadDictionary(promptEntry, "data") ?? new Dictionary<string, object>(),
                ["modelEvidence"] = ReadDictionary(llmEntry, "data") ?? new Dictionary<string, object>(),
                ["responseEvidence"] = ReadDictionary(responseEntry, "data") ?? new Dictionary<string, object>(),
                ["assertions"] = ReadDictionary(payload, "assertions") ?? new Dictionary<string, object>(),
                ["sourceAuditEntries"] = entries
            };
            if (evidenceV3)
                replayCase["evidenceBundle"] =
                    RedactGauntletEvidence(evidenceBundle);
            Dictionary<string, object> evaluation = EvaluateReplayCorpusCase(replayCase);
            replayCase["baselineEvaluation"] = evaluation;
            string path = ReplayCorpusCasePath(campaignId, caseId);
            WriteJsonObject(path, replayCase);
            return new Dictionary<string, object> { ["ok"] = true, ["path"] = path, ["case"] = replayCase, ["evaluation"] = evaluation };
        }

        private static Dictionary<string, object> ReplayCorpusListApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.TryGetValue("campaignId", out string campaign) ? campaign : ResolveLogCampaignId("");
            List<Dictionary<string, object>> cases = Directory.GetFiles(ReplayCorpusDir(campaignId), "*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(500).Select(path =>
                {
                    Dictionary<string, object> item = ReadJsonObject(path);
                    return new Dictionary<string, object>
                    {
                        ["caseId"] = ReadString(item, "caseId", Path.GetFileNameWithoutExtension(path)), ["mode"] = ReadString(item, "mode", ""),
                        ["correlationId"] = ReadString(item, "correlationId", ""), ["capturedUtc"] = ReadString(item, "capturedUtc", ""),
                        ["passed"] = ReadBool(ReadDictionary(item, "baselineEvaluation"), "passed", false), ["path"] = path
                    };
                }).ToList();
            return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaignId, ["count"] = cases.Count, ["cases"] = cases };
        }

        private static Dictionary<string, object> ReplayCorpusEvaluateApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> replayCase = ReadDictionary(payload, "case");
            if (replayCase == null)
            {
                string campaignId = ReadString(payload, "campaignId", ResolveLogCampaignId(""));
                replayCase = ReadJsonObject(ReplayCorpusCasePath(campaignId, ReadString(payload, "caseId", "")));
            }
            if (replayCase == null || replayCase.Count == 0) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Replay case was not found." };
            Dictionary<string, object> evaluation = EvaluateReplayCorpusCase(replayCase);
            string campaign = ReadString(replayCase, "campaignId", ResolveLogCampaignId(""));
            string runId = "replay_eval_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Dictionary<string, object> run = new Dictionary<string, object>
            {
                ["runId"] = runId, ["caseId"] = ReadString(replayCase, "caseId", ""), ["campaignId"] = campaign,
                ["evaluatedUtc"] = DateTime.UtcNow.ToString("o"), ["evaluation"] = evaluation, ["case"] = replayCase
            };
            string runDir = CampaignFile(campaign, "audit", "test-data", "replay-runs"); Directory.CreateDirectory(runDir);
            string path = Path.Combine(runDir, runId + ".json"); WriteJsonObject(path, run);
            return new Dictionary<string, object> { ["ok"] = true, ["runId"] = runId, ["path"] = path, ["evaluation"] = evaluation };
        }

        private static Dictionary<string, object> EvaluateReplayCorpusCase(Dictionary<string, object> replayCase)
        {
            Dictionary<string, object> assertions = ReadDictionary(replayCase, "assertions") ?? new Dictionary<string, object>();
            Dictionary<string, object> response = ReadDictionary(replayCase, "responseEvidence") ?? new Dictionary<string, object>();
            Dictionary<string, object> model = ReadDictionary(replayCase, "modelEvidence") ?? new Dictionary<string, object>();
            Dictionary<string, object> prompt = ReadDictionary(replayCase, "promptEvidence") ?? new Dictionary<string, object>();
            string reply = FirstNonEmpty(ReadFirstString(response, "reply", "response", "text"), ReadString(model, "content", ""));
            string promptJson = Json.Serialize(prompt);
            List<string> failures = new List<string>();
            foreach (string value in ReadStringList(assertions, "replyContains")) if (reply.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0) failures.Add("Reply did not contain: " + value);
            foreach (string value in ReadStringList(assertions, "replyExcludes")) if (reply.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0) failures.Add("Reply contained forbidden text: " + value);
            if (ReadBool(assertions, "requiresIdentityRecognition", false)
                && !LiveTestReplyRecognizesIdentity(response,
                    ReadString(assertions, "expectedIdentityName", ""),
                    ReadString(assertions, "expectedIdentityState", ""),
                    ReadString(assertions, "expectedIdentitySource", "")))
            {
                failures.Add("The production identity view did not prove the expected recognized name, state, and provenance.");
            }
            foreach (string value in ReadStringList(assertions, "promptContains")) if (promptJson.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0) failures.Add("Prompt evidence did not contain: " + value);
            foreach (string value in ReadStringList(assertions, "promptExcludes")) if (promptJson.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0) failures.Add("Prompt evidence contained forbidden text: " + value);
            foreach (string value in ReadStringList(assertions, "requiredSourceIds")) if (promptJson.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0) failures.Add("Required retrieval source was absent: " + value);
            Dictionary<string, object> promptProof = ReadDictionary(prompt, "promptEvidence")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> contextPullProof = ReadDictionary(promptProof, "contextPulls")
                ?? new Dictionary<string, object>();
            List<string> selectedContextPulls = contextPullProof
                .Where(entry => ReadBool(contextPullProof, entry.Key, false))
                .Select(entry => entry.Key)
                .ToList();
            foreach (string requiredPull in ReadStringList(assertions, "requiredContextPulls"))
            {
                if (!selectedContextPulls.Contains(requiredPull, StringComparer.OrdinalIgnoreCase))
                    failures.Add("Required context pull was absent from the final prompt: " + requiredPull);
            }
            long maxPrompt = ReadLong(assertions, "maxPromptBuildMs", 0); long promptMs = ReadLong(ReadDictionary(response, "timing"), "promptBuildMs", 0);
            if (maxPrompt > 0 && promptMs > maxPrompt) failures.Add("Prompt build " + promptMs + "ms exceeded " + maxPrompt + "ms.");
            long maxPromptChars = ReadLong(assertions, "maxPromptChars", 0);
            long promptChars = Math.Max(ReadLong(prompt, "promptChars", 0),
                ReadLong(ReadDictionary(response, "timing"), "promptChars", 0));
            if (maxPromptChars > 0 && (promptChars <= 0 || promptChars > maxPromptChars))
                failures.Add("Prompt characters " + promptChars + " exceeded " + maxPromptChars + ".");
            long maxTotal = ReadLong(assertions, "maxTotalMs", 0); long totalMs = ReadLong(ReadDictionary(response, "timing"), "totalMs", 0);
            if (maxTotal > 0 && totalMs > maxTotal) failures.Add("Total latency " + totalMs + "ms exceeded " + maxTotal + "ms.");
            if (ReadBool(assertions, "requiresGroupAwareness", false))
            {
                List<string> otherNames = ReadStringList(assertions, "otherNpcNames");
                bool aware = otherNames.Any(name => reply.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    || !string.IsNullOrWhiteSpace(ReadFirstString(response, "reactionTargetHeroStringId", "reactionTarget"));
                if (!aware) failures.Add("No evidence that the reply engaged another NPC in the shared scene.");
            }
            if (ReadBool(assertions, "requiresDynamicCharacteristicWrite", false))
            {
                Dictionary<string, object> store = ReadDictionary(response, "dynamicCharacteristicsStore")
                    ?? new Dictionary<string, object>();
                int stored = ReadInt(store, "storedCount", 0);
                if (stored <= 0)
                    failures.Add("The reply established a required stable self-detail but no active Dynamic Characteristic was stored.");
            }
            if (ReadBool(assertions, "requiresDynamicCharacteristicIntegrity", false))
            {
                Dictionary<string, object> store = ReadDictionary(response, "dynamicCharacteristicsStore")
                    ?? new Dictionary<string, object>();
                int stored = ReadInt(store, "storedCount", 0);
                bool reiteratesInjected = LiveTestDynamicCharacteristicPromptLines(prompt)
                    .Any(line => HasDynamicTextEvidence(line, reply));
                if (stored <= 0 && ReplyLikelyIntroducesDynamicCharacteristic(reply) && !reiteratesInjected)
                    failures.Add("The reply introduced an eligible stable self-detail but no active Dynamic Characteristic was stored.");
            }
            if (ReadBool(assertions, "requiresDynamicCharacteristicRecall", false))
            {
                List<string> injected = LiveTestDynamicCharacteristicPromptLines(prompt);
                if (injected.Count == 0 || !injected.Any(line => HasDynamicTextEvidence(line, reply)))
                    failures.Add("No injected Dynamic Characteristic was visibly recalled in the response.");
            }
            if (ReadBool(assertions, "allowsDynamicCharacteristicRefusal", false))
            {
                Dictionary<string, object> store = ReadDictionary(response, "dynamicCharacteristicsStore")
                    ?? new Dictionary<string, object>();
                int stored = ReadInt(store, "storedCount", 0);
                if (ReplyLikelyIntroducesDynamicCharacteristic(reply))
                    failures.Add("The purported refusal actually introduced an eligible stable self-detail.");
                if (stored > 0)
                    failures.Add("A refusal with no eligible self-detail manufactured a Dynamic Characteristic.");
            }
            if (ReadBool(assertions, "forbidRomanticAction", false))
            {
                Dictionary<string, object> outcome = ReadDictionary(response, "motiveOutcome")
                    ?? ReadDictionary(ReadDictionary(response, "rawResponse"), "motiveOutcome")
                    ?? new Dictionary<string, object>();
                if (ReadBool(outcome, "romanticActionDetected", false))
                    failures.Add("A non-romantic reply was falsely classified as a romantic action.");
            }
            if (ReadBool(assertions, "requiresNonRomanticPosture", false))
            {
                Dictionary<string, object> brief = ReadDictionary(response, "decisionBrief")
                    ?? ReadDictionary(ReadDictionary(response, "rawResponse"), "decisionBrief")
                    ?? new Dictionary<string, object>();
                if (!ReadString(brief, "postureAlignment", "").Equals("not_applicable", StringComparison.OrdinalIgnoreCase))
                    failures.Add("A non-romantic turn carried a romance-specific posture.");
            }
            if (ReadBool(assertions, "requiresRecoverableCompleteModelJson", false))
            {
                Dictionary<string, object> recovered = TryParseJsonObject(ReadString(model, "content", ""));
                bool complete = recovered != null
                    && !string.IsNullOrWhiteSpace(ReadFirstString(recovered, "reply", "response", "text"))
                    && ReadDictionary(recovered, "decisionBrief") != null
                    && ReadDictionary(recovered, "actionGate") != null;
                if (!complete)
                    failures.Add("A later complete provider JSON object could not be recovered from an abandoned prefix.");
            }
            if (ReadBool(assertions, "forbidMemorySummaryAsReply", false)
                || ReadBool(assertions, "replyMustBeVisibleDialogue", false))
            {
                Dictionary<string, object> original = TryParseJsonObject(ReadString(model, "content", ""));
                bool visible = ConversationStructuredObjectHasUsableVisibleReply(original)
                    && (ReadDictionary(original, "actionGate") != null
                        || ReadDictionary(original, "action_gate") != null);
                if (!visible)
                    failures.Add("A nested memory-write object was not a usable visible dialogue response.");
            }
            if (ReadBool(assertions, "requiresPersonalityEvidence", false))
            {
                Dictionary<string, object> brief = ReadDictionary(response, "decisionBrief")
                    ?? ReadDictionary(ReadDictionary(response, "rawResponse"), "decisionBrief")
                    ?? new Dictionary<string, object>();
                if (ReadStringList(brief, "goals").Count == 0
                    || ReadStringList(brief, "constraints").Count == 0)
                    failures.Add("The response lacked complete motive/personality decision evidence.");
            }
            return new Dictionary<string, object>
            {
                ["passed"] = failures.Count == 0, ["failureCount"] = failures.Count, ["failures"] = failures,
                ["reply"] = reply, ["promptBuildMs"] = promptMs, ["promptChars"] = promptChars, ["totalMs"] = totalMs,
                ["summary"] = failures.Count == 0 ? "Replay evaluation passed." : string.Join(" ", failures)
            };
        }

        private static void ScheduleReplayCorpusCapture(string campaignId, string correlationId)
        {
            if (!ReadBool(LoadSettings(), "autoCaptureReplayCorpus", true)) return;
            EnqueuePriorityBackgroundWork("replay.capture", "maintenance", () => ReplayCorpusCaptureApi(new Dictionary<string, object>
            { ["campaignId"] = campaignId, ["correlationId"] = correlationId, ["label"] = "Automatic production capture" }));
        }
    }
}
