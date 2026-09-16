using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunFinalConversationGauntletEvidenceSelfTests()
        {
            List<Dictionary<string, object>> checks =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, message) =>
                checks.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["passed"] = passed, ["message"] = message
                });
            string campaignId = "gauntlet-evidence-" + Guid.NewGuid().ToString("N");
            string correlation = "corr-evidence";
            try
            {
                string auditPath = CampaignFile(campaignId, "audit", "audit.jsonl");
                AppendJsonLineToPath(auditPath, new Dictionary<string, object>
                {
                    ["correlationId"] = correlation,
                    ["phase"] = "prompt.built",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["fullPrompt"] = "SYSTEM\nFULL UNTRUNCATED PROMPT",
                        ["promptSections"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["name"] = "identity", ["chars"] = 100
                            }
                        },
                        ["retrievalCandidates"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["sourceId"] = "memory-1", ["score"] = 0.9,
                                ["selected"] = true, ["ownerHeroId"] = "npc"
                            },
                            new Dictionary<string, object>
                            {
                                ["sourceId"] = "memory-2", ["score"] = 0.8,
                                ["selected"] = false, ["rejectionReason"] = "private"
                            }
                        }
                    }
                });
                AppendJsonLineToPath(auditPath, new Dictionary<string, object>
                {
                    ["correlationId"] = correlation,
                    ["phase"] = "llm.response",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["rawResponse"] = "{\"reply\":\"Good day.\"}",
                        ["parsedResponse"] = new Dictionary<string, object>
                        {
                            ["reply"] = "Good day."
                        },
                        ["provider"] = "fixture-provider",
                        ["model"] = "fixture-model",
                        ["inputTokens"] = 1000,
                        ["outputTokens"] = 20,
                        ["retryNumber"] = 1
                    }
                });
                AppendJsonLineToPath(auditPath, new Dictionary<string, object>
                {
                    ["correlationId"] = correlation,
                    ["phase"] = "action.validation",
                    ["data"] = new Dictionary<string, object>
                    {
                        ["resolverCandidates"] = new List<object> { "hero_a" },
                        ["validatorResult"] = "rejected",
                        ["actionArguments"] = new Dictionary<string, object>()
                    }
                });
                MethodInfo build = typeof(Program).GetMethod(
                    "BuildFinalGauntletEvidenceBundle",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Dictionary<string, object> bundle = build?.Invoke(
                    null,
                    new object[]
                    {
                        campaignId,
                        "run-a",
                        "CON-001",
                        new[] { correlation },
                        new Dictionary<string, object> { ["gold"] = 100 },
                        new Dictionary<string, object> { ["gold"] = 100 }
                    }) as Dictionary<string, object>;
                add(
                    "evidence_v3_captures_complete_production_seams",
                    bundle != null
                        && ReadString(bundle, "schema", "")
                            == "reign-final-gauntlet-evidence-v3"
                        && Json.Serialize(bundle).Contains("FULL UNTRUNCATED PROMPT")
                        && Json.Serialize(bundle).Contains("memory-1")
                        && Json.Serialize(bundle).Contains("memory-2")
                        && Json.Serialize(bundle).Contains("fixture-provider")
                        && Json.Serialize(bundle).Contains("resolverCandidates")
                        && Json.Serialize(bundle).Contains("promptSectionFingerprints")
                        && ReadString(bundle, "stateBeforeFingerprint", "").Length == 64
                        && ReadString(bundle, "stateAfterFingerprint", "").Length == 64,
                    "Evidence v3 preserves full prompt, retrieval decisions, model, resolver, validator, and before/after fingerprints.");
                add(
                    "evidence_does_not_leak_credentials",
                    bundle != null
                        && Json.Serialize(bundle)
                            .IndexOf("apiKey", StringComparison.OrdinalIgnoreCase) < 0
                        && Json.Serialize(bundle)
                            .IndexOf("authorization", StringComparison.OrdinalIgnoreCase) < 0,
                    "Persisted evidence redacts credentials without truncating behavioral evidence.");
                Dictionary<string, object> replay = ReplayCorpusCaptureApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["correlationId"] = correlation,
                        ["caseId"] = "CON-001-evidence-v3",
                        ["evidenceSchemaVersion"] = 3,
                        ["evidenceBundle"] = bundle
                    });
                Dictionary<string, object> replayCase =
                    ReadDictionary(replay, "case")
                    ?? new Dictionary<string, object>();
                add(
                    "replay_corpus_accepts_evidence_v3",
                    ReadBool(replay, "ok", false)
                        && ReadLong(replayCase, "version", 0) == 3
                        && ReadString(
                            ReadDictionary(replayCase, "evidenceBundle"),
                            "schema",
                            "") == "reign-final-gauntlet-evidence-v3"
                        && Json.Serialize(replayCase)
                            .Contains("FULL UNTRUNCATED PROMPT"),
                    "Replay capture preserves complete evidence-v3 artifacts while remaining a normal replay case.");
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(campaignId));
            }
            return checks;
        }
    }
}
