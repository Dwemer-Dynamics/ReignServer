using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string ConversationAssessmentAbsenceReason(Dictionary<string, object> parsed)
        {
            string status = ReadString(parsed, "relationshipAssessmentStatus", "");
            if (status == "cleared_after_failed_repair")
                return "relationship_assessment_intentionally_cleared";
            if (status == "missing" || status == "invalid") return "relationship_assessment_" + status;
            if (parsed == null || (!parsed.ContainsKey("relationshipAssessments") && !parsed.ContainsKey("relationship_assessments")))
                return "relationship_assessment_missing";
            object value = parsed.ContainsKey("relationshipAssessments") ? parsed["relationshipAssessments"] : parsed["relationship_assessments"];
            if (!IsConversationObjectArray(value) || ReadDictionaryList(parsed, parsed.ContainsKey("relationshipAssessments")
                ? "relationshipAssessments" : "relationship_assessments").Count > 0)
                return "relationship_assessment_invalid";
            return "relationship_assessment_explicitly_empty";
        }

        private static string StructuredConversationFailureKind(Dictionary<string, object> response)
        {
            string content = ReadString(response, "content", "").Trim();
            bool length = ReadString(response, "finishReason", "").Equals("length", StringComparison.OrdinalIgnoreCase);
            var parsed = TryParseJsonObject(content);
            if (parsed != null && parsed.Count == 0) return length ? "empty_object_at_output_limit" : "empty_object";
            if (content.Length == 0) return length ? "empty_content_at_output_limit" : "empty_content";
            return length ? "incomplete_at_output_limit" : "invalid_structure_or_visible_reply";
        }

        private static Dictionary<string, object> StructuredConversationFailureEvidence(Dictionary<string, object> response, Dictionary<string, object> request)
        {
            var raw = ReadDictionary(response, "raw");
            var choices = ReadDictionaryList(raw, "choices");
            return new Dictionary<string, object> {
                ["failureKind"] = StructuredConversationFailureKind(response), ["finishReason"] = ReadString(response, "finishReason", "unknown"),
                ["model"] = ReadString(response, "model", ReadString(request, "model", "")), ["requestType"] = ReadString(request, "requestType", ""),
                ["outputLimit"] = ReadInt(request, "maxTokens", 0), ["usage"] = ReadDictionary(raw, "usage") ?? new Dictionary<string, object>(),
                ["responseFormat"] = ReadString(ReadDictionary(request, "response_format"), "type", "unspecified"),
                ["choiceCount"] = choices.Count, ["contentCharacters"] = ReadString(response, "content", "").Length,
                ["schemaApplied"] = ReadBool(ReadDictionary(ReadDictionary(raw, "codexDiagnostics"), "request"), "schemaApplied", false),
                ["diagnosis"] = ReadString(response, "finishReason", "") == "length" ? "provider_output_limit"
                    : StructuredConversationFailureKind(response) == "empty_object" ? "provider_completed_empty_json_object" : "inspect_envelope_and_format"
            };
        }

        private static void PreserveFormatRepairMetadata(Dictionary<string, object> original, Dictionary<string, object> repaired)
        {
            if (original == null || repaired == null || !ConversationStructuredObjectHasUsableVisibleReply(original)) return;
            foreach (string field in new[] { "relationshipAssessments", "memoryWrites", "beliefWrites", "relationshipUpdates",
                "obligationWrites", "comprehensionWrites", "dynamicCharacteristicWrites", "continuityWrites", "proposalDecisions",
                "identityIntroductions", "socialSignals", "drinkActions", "drinkingEvents", "drinkConsumptions", "drinkSceneUpdates", "gossipSignals", "raisedGossip" })
                if (original.TryGetValue(field, out object value) && IsConversationObjectArray(value)) repaired[field] = value;
            foreach (string field in new[] { "decisionBrief", "actionGate", "conceptionGate", "stateUpdates" })
                if (ReadDictionary(original, field) is Dictionary<string, object> value) repaired[field] = value;
            CarryConversationRepairState(original, repaired);
        }

        private static bool IsConversationObjectArray(object value)
        {
            if (!(value is IEnumerable sequence) || value is string || value is IDictionary) return false;
            foreach (object item in sequence)
                if (!(item is Dictionary<string, object>)) return false;
            return true;
        }

        private static bool TryCompleteOrdinaryConversationAgency(
            Dictionary<string, object> parsed, Dictionary<string, object> context)
        {
            if (parsed.ContainsKey("proposalDecisions")) return false;
            var gate = ReadDictionary(parsed, "actionGate");
            string intent = ReadString(gate, "intent", "");
            if (ActionGateShouldPlan(gate)
                || (!string.IsNullOrWhiteSpace(intent) && intent != "none" && intent != "roleplay_only")
                || ReadBool(ReadDictionary(parsed, "conceptionGate"), "needed", false)
                || ReadBool(ReadDictionary(parsed, "conceptionGate"), "completed", false)
                || ReadDictionaryList(parsed, "suggestedActions").Count > 0
                || ReadDictionaryList(parsed, "obligationWrites").Count > 0
                || ReadDictionaryList(parsed, "continuityWrites").Count > 0
                || ReadDictionaryList(context, "records").Count > 0)
                return false;
            // This only supplies a missing empty collection. Ambiguous offers,
            // boundaries and commitments still take the bounded negotiation path.
            string text = ReadString(context, "playerText", "") + "\n" + ReadFirstString(parsed, "reply", "body");
            if (Regex.IsMatch(text,
                @"\b(?:join|recruit|hire|serve|travel|accompany|escort|marry|marriage|kiss|sex|intima\w*|bed|"
                + @"pay|paid|payment|gold|denars?|bribe|sell|buy|trade|caravan|release|surrender|alliance|"
                + @"promise|agree|agreement|accept|refuse|refusal|condition|offer|demand|order|command)\b"
                + @"|\b(?:will|would|can|could)\s+you\s+(?:come|go|give|take|leave|stay|follow|bring|show)\b"
                + @"|\b(?:give|bring|show|tell)\s+me\b|\b(?:i|we)\s+(?:will|shall|agree|promise)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
            parsed["proposalDecisions"] = new List<Dictionary<string, object>>();
            return true;
        }

        private static Dictionary<string, object> ApplyAgencyMetadataPatch(
            Dictionary<string, object> original, Dictionary<string, object> patch)
        {
            if (patch == null || !patch.TryGetValue("proposalDecisions", out object proposed)
                || !IsConversationObjectArray(proposed)) return null;
            foreach (string key in new[] { "reply", "body" })
                if (patch.ContainsKey(key) && ReadString(patch, key, "") != ReadFirstString(original, key, "reply", "body"))
                    return null;
            var merged = CloneDictionary(original);
            var before = ReadDictionaryList(original, "proposalDecisions");
            var after = ReadDictionaryList(patch, "proposalDecisions");
            if (original.ContainsKey("proposalDecisions"))
            {
                if (before.Count != after.Count) return null;
                var corrected = new List<Dictionary<string, object>>();
                for (int i = 0; i < before.Count; i++)
                {
                    if (ReadString(before[i], "topic", "") != ReadString(after[i], "topic", "")
                        || ReadString(before[i], "objectId", "") != ReadString(after[i], "objectId", "")) return null;
                    var entry = CloneDictionary(before[i]);
                    foreach (string key in new[] { "playerQuote", "replyQuote", "evidenceKeys" })
                        if (after[i].ContainsKey(key)) entry[key] = after[i][key];
                    corrected.Add(entry);
                }
                merged["proposalDecisions"] = corrected;
            }
            else merged["proposalDecisions"] = after;
            // A metadata correction cannot erase an assessment, upgrade consent,
            // replace a refusal, or add a memory by returning a fresh whole object.
            return merged;
        }

        private static bool IsConversationStyleWarning(Dictionary<string, object> violation)
        {
            return new[] { "repeated_distinctive_phrase", "repeated_wording_lecture", "repeated_conversation_accounting",
                "group_conversation_accounting_echo", "group_rhetorical_convergence" }
                .Contains(ReadString(violation, "type", ""));
        }

        private static void CarryConversationRepairState(Dictionary<string, object> original,
            Dictionary<string, object> candidate)
        {
            if (original == null || candidate == null) return;
            candidate.Remove("repairStatus");
            var status = ReadDictionary(original, "repairStatus");
            if (status != null) candidate["repairStatus"] = CloneDictionary(status);
            else if (ReadFirstString(original, "reply", "body").TrimEnd().EndsWith(".,", StringComparison.Ordinal))
                candidate["repairStatus"] = new Dictionary<string, object>
                { ["allChecksCleared"] = false, ["stageCount"] = 1 };
        }

        private static bool ConversationRepairFailed(Dictionary<string, object> parsed, string fallbackText = "")
        {
            var status = ReadDictionary(parsed, "repairStatus");
            return status != null ? !ReadBool(status, "allChecksCleared", true)
                : ReadFirstString(parsed, "reply", "body", "text").Length > 0
                    ? ReadFirstString(parsed, "reply", "body", "text").TrimEnd().EndsWith(".,", StringComparison.Ordinal)
                    : (fallbackText ?? "").TrimEnd().EndsWith(".,", StringComparison.Ordinal);
        }

        private static void PreserveConversationVerification(Dictionary<string, object> payload, Dictionary<string, object> llm)
        {
            var repair = ReadDictionary(TryParseJsonObject(ReadString(llm, "content", "")), "repairStatus");
            if (repair != null) payload["repairStatus"] = repair;
        }

        private static bool IsDescriptiveConversationVenue(string place)
        {
            return Regex.IsMatch((place ?? "").Trim(),
                @"\A(?:(?:(?:party|roadside|wilderness)\s+)?camp(?:\s+(?:on\s+the\s+(?:open\s+)?road|in\s+the\s+wilderness))?"
                + @"|(?:the\s+)?(?:open\s+road|wilderness|roadside|forest\s+clearing|tavern|lord'?s\s+hall|keep|salon))\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
