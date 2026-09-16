using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<string> FinalGauntletRubricDimensions()
        {
            return new List<string>
            {
                "factualGrounding",
                "epistemicRealism",
                "characterFidelity",
                "socialAwareness",
                "emotionalContinuity",
                "agency",
                "situationalEmbodiment",
                "memoryIntegration",
                "responsiveness",
                "linguisticNaturalness",
                "specificity",
                "nonRepetition"
            };
        }

        private static Dictionary<string, object>
            BuildFinalGauntletSemanticRubricCase(
                Dictionary<string, object> fixture,
                Dictionary<string, object> evidence)
        {
            fixture = fixture ?? new Dictionary<string, object>();
            evidence = evidence ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["schema"] = "reign-final-gauntlet-semantic-case-v1",
                ["caseId"] = ReadString(fixture, "caseId", ""),
                ["authoritative"] = false,
                ["dimensions"] = FinalGauntletRubricDimensions()
                    .Cast<object>().ToList(),
                ["behavioralRequirement"] = ReadString(
                    ReadDictionary(fixture, "expected"),
                    "behavioralRequirement",
                    ""),
                ["speakerEvidence"] =
                    ReadDictionary(fixture, "speaker")
                    ?? new Dictionary<string, object>(),
                ["campaignState"] =
                    ReadDictionary(fixture, "campaignState")
                    ?? new Dictionary<string, object>(),
                ["retrievedContext"] =
                    ReadDictionary(fixture, "retrievedContext")
                    ?? new Dictionary<string, object>(),
                ["playerMessage"] = ReadFirstString(
                    ReadDictionary(fixture, "input")
                        ?? new Dictionary<string, object>(),
                    "playerMessage",
                    "text"),
                ["reply"] = FinalGauntletVisibleReply(evidence),
                ["instructions"] =
                    "Score each dimension from 0 through 4 using only supplied evidence. "
                    + "Natural variation is allowed. Never infer missing facts. "
                    + "This semantic judgment cannot override a hard assertion."
            };
        }
    }
}
