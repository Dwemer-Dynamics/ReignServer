using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> BuildFinalGauntletEvidenceBundle(
            string campaignId,
            string runId,
            string caseInstanceId,
            IEnumerable<string> correlationIds,
            Dictionary<string, object> authoritativeBefore,
            Dictionary<string, object> authoritativeAfter)
        {
            HashSet<string> correlations = new HashSet<string>(
                (correlationIds ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> entries =
                ReadJsonLinesFromPath(CampaignFile(
                    campaignId,
                    "audit",
                    "audit.jsonl"))
                .Where(row => correlations.Contains(
                    ReadString(row, "correlationId", "")))
                .Select(row => RedactGauntletEvidence(row)
                    as Dictionary<string, object>
                    ?? new Dictionary<string, object>())
                .ToList();
            Dictionary<string, object> before =
                RedactGauntletEvidence(
                    authoritativeBefore ?? new Dictionary<string, object>())
                as Dictionary<string, object>
                ?? new Dictionary<string, object>();
            Dictionary<string, object> after =
                RedactGauntletEvidence(
                    authoritativeAfter ?? new Dictionary<string, object>())
                as Dictionary<string, object>
                ?? new Dictionary<string, object>();

            List<Dictionary<string, object>> promptEntries =
                EntriesForPhase(entries, "prompt.");
            foreach (Dictionary<string, object> promptEntry in promptEntries)
                AddGauntletPromptSectionFingerprints(promptEntry);
            List<Dictionary<string, object>> modelEntries =
                EntriesForPhase(entries, "llm.");
            List<Dictionary<string, object>> actionEntries =
                entries.Where(row =>
                    ReadString(row, "phase", "")
                        .IndexOf("action", StringComparison.OrdinalIgnoreCase) >= 0
                    || ReadString(row, "phase", "")
                        .IndexOf("resolver", StringComparison.OrdinalIgnoreCase) >= 0
                    || ReadString(row, "phase", "")
                        .IndexOf("validation", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            List<Dictionary<string, object>> persistenceEntries =
                entries.Where(row =>
                    ReadString(row, "phase", "")
                        .IndexOf("memory", StringComparison.OrdinalIgnoreCase) >= 0
                    || ReadString(row, "phase", "")
                        .IndexOf("persist", StringComparison.OrdinalIgnoreCase) >= 0
                    || ReadString(row, "phase", "")
                        .IndexOf("relationship", StringComparison.OrdinalIgnoreCase) >= 0
                    || ReadString(row, "phase", "")
                        .IndexOf("rumor", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            Dictionary<string, object> bundle =
                new Dictionary<string, object>
                {
                    ["schema"] = "reign-final-gauntlet-evidence-v3",
                    ["campaignId"] = campaignId ?? string.Empty,
                    ["runId"] = runId ?? string.Empty,
                    ["caseInstanceId"] = caseInstanceId ?? string.Empty,
                    ["correlationIds"] = correlations.Cast<object>().ToList(),
                    ["capturedUtc"] = DateTime.UtcNow.ToString("o"),
                    ["buildVersion"] = CurrentConversationBuildVersion(),
                    ["authoritativeBefore"] = before,
                    ["authoritativeAfter"] = after,
                    ["stateBeforeFingerprint"] =
                        PromptHash(CanonicalJson(before)).ToLowerInvariant(),
                    ["stateAfterFingerprint"] =
                        PromptHash(CanonicalJson(after)).ToLowerInvariant(),
                    ["promptEvidence"] = promptEntries,
                    ["modelEvidence"] = modelEntries,
                    ["actionResolverValidationEvidence"] = actionEntries,
                    ["persistenceEvidence"] = persistenceEntries,
                    ["timingAndRetryEvidence"] = entries.Where(row =>
                        ReadDictionary(row, "data")?.ContainsKey("timing") == true
                        || ReadDictionary(row, "data")?.ContainsKey("durationMs") == true
                        || ReadDictionary(row, "data")?.ContainsKey("retryNumber") == true)
                        .ToList(),
                    ["probabilityRollEvidence"] = entries.Where(row =>
                        ReadString(row, "phase", "")
                            .IndexOf("roll", StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList(),
                    ["sourceAuditEntries"] = entries
                };
            string directory = CampaignFile(
                campaignId,
                "audit",
                "test-data",
                "final-gauntlet",
                SafePathSegment(runId, "run"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(
                directory,
                SafePathSegment(caseInstanceId, "case") + ".evidence-v3.json");
            WriteJsonObject(path, bundle);
            bundle["artifactPath"] = path;
            return bundle;
        }

        private static List<Dictionary<string, object>> EntriesForPhase(
            List<Dictionary<string, object>> entries,
            string prefix)
        {
            return (entries ?? new List<Dictionary<string, object>>())
                .Where(row => ReadString(row, "phase", "")
                    .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static object RedactGauntletEvidence(object value)
        {
            if (value == null) return null;
            if (value is IDictionary dictionary)
            {
                Dictionary<string, object> result =
                    new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    string key = Convert.ToString(entry.Key) ?? string.Empty;
                    if (IsGauntletSecretKey(key))
                        continue;
                    result[key] = RedactGauntletEvidence(entry.Value);
                }
                return result;
            }
            if (value is IEnumerable enumerable && !(value is string))
                return enumerable.Cast<object>()
                    .Select(RedactGauntletEvidence)
                    .ToList();
            return value;
        }

        private static bool IsGauntletSecretKey(string key)
        {
            string normalized = (key ?? string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .ToLowerInvariant();
            return normalized == "apikey"
                || normalized == "authorization"
                || normalized == "accesstoken"
                || normalized == "refreshtoken"
                || normalized == "providersecret"
                || normalized == "clientsecret";
        }

        private static void AddGauntletPromptSectionFingerprints(
            Dictionary<string, object> promptEntry)
        {
            Dictionary<string, object> data =
                ReadDictionary(promptEntry, "data");
            if (data == null) return;
            List<object> sections = ReadObjectList(data, "promptSections");
            List<object> fingerprints = new List<object>();
            for (int index = 0; index < sections.Count; index++)
            {
                object section = sections[index];
                Dictionary<string, object> sectionObject =
                    section as Dictionary<string, object>;
                fingerprints.Add(new Dictionary<string, object>
                {
                    ["index"] = index,
                    ["name"] = sectionObject == null
                        ? "section-" + index
                        : ReadString(
                            sectionObject,
                            "name",
                            "section-" + index),
                    ["sha256"] = PromptHash(CanonicalJson(section))
                        .ToLowerInvariant()
                });
            }
            data["promptSectionFingerprints"] = fingerprints;
            string fullPrompt = ReadString(data, "fullPrompt", "");
            if (!string.IsNullOrEmpty(fullPrompt))
                data["fullPromptFingerprint"] =
                    PromptHash(fullPrompt).ToLowerInvariant();
        }
    }
}
