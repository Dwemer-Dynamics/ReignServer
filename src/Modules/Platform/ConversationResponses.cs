using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class ConversationResponseService
    {
        internal static Dictionary<string, object> RetryMalformedStructuredResponse(
            Dictionary<string, object> llm,
            Dictionary<string, object> request,
            string campaignId,
            string correlationId,
            string auditMode,
            string heroId,
            string eventId,
            Func<Dictionary<string, object>, Dictionary<string, object>> responder = null)
        {
            if (!ReadBool(llm, "ok", false))
                return llm;
            llm = RecoverConversationResponseEnvelope(llm, request, auditMode, campaignId, correlationId, heroId, eventId);
            if (!ReadBool(llm, "ok", false)) return llm;
            bool regenerateEnvelope = ReadBool(llm, "responseRecoveryRequiresRegeneration", false);
            string initialContent = ReadString(llm, "content", "");
            bool initialAccepted = StructuredResponseIsComplete(initialContent, auditMode)
                && DialogueService.StructuredActionResponseHasDecision(initialContent, request)
                && ConversationVisibleReplyPassesRequestQualityGate(
                    initialContent, request, auditMode);
            DiagnosticValidation("Check answer structure", initialAccepted,
                DiagnosticStructuredIssues(initialContent, auditMode, request), initialContent);
            if (initialAccepted)
                return llm;

            if (!regenerateEnvelope)
                llm = DialogueService.RepairMissingStructuredActionResponseDecision(llm, request, campaignId,
                    correlationId, auditMode, heroId, eventId, responder);
            if (!ReadBool(llm, "ok", false)) return llm;
            initialContent = ReadString(llm, "content", "");

            string normalizedMode = ActionPipelineService.NormalizeLookup(auditMode).Replace(' ', '_');
            Dictionary<string, object> initiallyParsed = regenerateEnvelope ? null : TryParseJsonObject(initialContent);
            if (IsConversationStructuredMode(normalizedMode)
                && TryRepairShiftedConversationReplyKeys(initiallyParsed, out Dictionary<string, object> repairedReplyObject)
                && ConversationVisibleReplyPassesRequestQualityGate(
                    Json.Serialize(repairedReplyObject), request, auditMode))
            {
                List<string> repairedFields;
                Dictionary<string, object> completed = CompleteConversationStructuredResponse(
                    repairedReplyObject, normalizedMode, out repairedFields);
                llm["content"] = Json.Serialize(completed);
                llm["deterministicSchemaCompletion"] = new Dictionary<string, object>
                {
                    ["completed"] = true,
                    ["fields"] = repairedFields,
                    ["repairedShiftedReplyKeys"] = true,
                    ["reason"] = "The provider used an unambiguous whitespace-named reply property; the visible reply was restored without resending the prompt."
                };
                OperationsService.WriteAudit(campaignId, correlationId, "server", auditMode, "llm.schema_completion", heroId, "", eventId,
                    "completed", 0, "A provider-corrupted reply key was repaired deterministically without resending the full prompt.",
                    ReadDictionary(llm, "deterministicSchemaCompletion"));
                return DialogueService.RepairMissingStructuredActionResponseDecision(llm, request, campaignId,
                    correlationId, auditMode, heroId, eventId, responder);
            }
            if (IsConversationStructuredMode(normalizedMode)
                && initiallyParsed != null
                && ConversationStructuredObjectHasUsableVisibleReply(initiallyParsed)
                && ConversationVisibleReplyPassesRequestQualityGate(
                    initialContent, request, auditMode))
            {
                List<string> completedFields;
                Dictionary<string, object> completed = CompleteConversationStructuredResponse(initiallyParsed, normalizedMode, out completedFields);
                llm["content"] = Json.Serialize(completed);
                llm["deterministicSchemaCompletion"] = new Dictionary<string, object>
                {
                    ["completed"] = true,
                    ["fields"] = completedFields,
                    ["reason"] = "The provider returned a usable visible reply but omitted private structured metadata; conservative defaults were supplied without another provider call."
                };
                OperationsService.WriteAudit(campaignId, correlationId, "server", auditMode, "llm.schema_completion", heroId, "", eventId,
                    "completed", 0, "Incomplete private response metadata was completed deterministically without resending the prompt.",
                    ReadDictionary(llm, "deterministicSchemaCompletion"));
                return DialogueService.RepairMissingStructuredActionResponseDecision(llm, request, campaignId,
                    correlationId, auditMode, heroId, eventId, responder);
            }

            List<Dictionary<string, object>> attempts = new List<Dictionary<string, object>>();
            Dictionary<string, object> diagnostics = new Dictionary<string, object>
            {
                ["attempted"] = true,
                ["initialContentChars"] = ReadString(llm, "content", "").Length,
                ["initialFinishReason"] = ReadString(llm, "finishReason", ""),
                ["initialFailureKind"] = DialogueService.StructuredConversationFailureKind(llm),
                ["initialFailureEvidence"] = DialogueService.StructuredConversationFailureEvidence(llm, request),
                ["attempts"] = attempts
            };
            // An empty typed-action answer has no metadata to repair. Permit one
            // regeneration with the complete evidence, then fail without effects;
            // do not pay for repeated full-context calls to repair the same {}.
            int maximumAttempts = regenerateEnvelope || DialogueService.UsesStructuredActions(request) && initiallyParsed != null && initiallyParsed.Count == 0 ? 1 : 2;
            diagnostics["maximumAttempts"] = maximumAttempts;
            for (int attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                Dictionary<string, object> retryRequest = VerificationService.CloneDictionary(request ?? new Dictionary<string, object>());
                List<Dictionary<string, object>> retryMessages = ReadDictionaryList(request, "messages")
                    .Select(VerificationService.CloneDictionary)
                    .ToList();
                if (retryMessages.Count == 0)
                {
                    retryMessages.Add(new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] = ReadString(request, "system", "You are Bannerlord Reign. Return strict JSON only.")
                    });
                    retryMessages.Add(new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = ReadString(request, "prompt", ReadString(request, "text", ""))
                    });
                }
                bool correspondenceMode = normalizedMode.Contains("correspondence");
                string structuredFieldGuidance = correspondenceMode
                    ? " Include every required correspondence field: shouldReply, body, reason, actionGate, rebellionDecision, and campaignOrder. The body must contain the complete in-character written reply when shouldReply is true."
                    : " Include every required field, including reply and actionGate when this is dialogue. The reply field must contain a complete in-character reaction; never return only a quoted fragment copied from the latest player message, and never put a participation enum such as speak, agree, disagree, or quiet in reply.";
                if (DialogueService.UsesStructuredActions(request))
                    structuredFieldGuidance += " Include the required structuredActionDecision with kind, proposalId, revision, termsHash, reason and clauses. Preserve exact pending proposal identity; a native receipt acknowledgment with no pending proposal uses none, empty identifiers and revision 0. Never substitute legacy actionGate for this decision.";
                // The original messages already include the character context. When the
                // provider returned no usable answer, repeating the context can push a
                // near-capacity prompt over the safety ceiling before either retry runs.
                bool repeatCharacterContext = FormatRetryNeedsCharacterContext(initialContent);
                retryMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = "system",
                    ["content"] = "FORMAT REPAIR ATTEMPT " + attempt.ToString(CultureInfo.InvariantCulture)
                        + (ConversationStructuredObjectHasUsableVisibleReply(initiallyParsed)
                            ? ": Correct the rejected response below. Preserve every usable part of its in-character answer, personality, tone, decisions, and structured effects."
                            : ": The previous output had no usable answer. Answer the latest player turn using the original character and scene instructions already supplied above.")
                        + " Return exactly one complete valid JSON object matching the required response schema. Write the complete reply field first, then its grounded private metadata. Do not return an empty object. Do not use Markdown or code fences." + structuredFieldGuidance
                        + "\n\nREJECTED ORIGINAL RESPONSE TO CORRECT:\n"
                        + DialogueService.LimitText(initialContent, 16000)
                        + (repeatCharacterContext
                            ? "\n\nCOMPACT CHARACTER AND MOTIVE CONTEXT:\n"
                                + Json.Serialize(DialogueService.DialogueValidationRepairCharacterContext(request))
                            : "")
                });
                retryRequest["messages"] = retryMessages;
                retryRequest["promptCacheEligible"] = false;
                retryRequest["reasoningDisabled"] = true;
                retryRequest["temperature"] = attempt == 1 ? 0.05d : 0d;
                retryRequest["maxTokens"] = Math.Max(regenerateEnvelope ? 16000 : 2200, ReadInt(retryRequest, "maxTokens", 2200));
                Dictionary<string, object> retry = responder == null ? LlmProviderService.ChatWithLlm(retryRequest) : responder(retryRequest);
                retry = RecoverConversationResponseEnvelope(retry, request, auditMode, campaignId, correlationId, heroId, eventId);
                if (ReadBool(retry, "responseRecoveryRequiresRegeneration", false))
                {
                    retry["ok"] = false;
                    retry["errorCode"] = "response_envelope_incomplete";
                }
                var incompleteRetry = TryParseJsonObject(ReadString(retry, "content", ""));
                if (ReadBool(retry, "ok", false) && IsConversationStructuredMode(normalizedMode)
                    && !StructuredResponseIsComplete(ReadString(retry, "content", ""), auditMode)
                    && ConversationStructuredObjectHasUsableVisibleReply(incompleteRetry)
                    && ConversationVisibleReplyPassesRequestQualityGate(ReadString(retry, "content", ""), request, auditMode))
                    retry["content"] = Json.Serialize(CompleteConversationStructuredResponse(incompleteRetry, normalizedMode, out _));
                retry = DialogueService.RepairMissingStructuredActionResponseDecision(retry, request, campaignId,
                    correlationId, auditMode, heroId, eventId, responder);
                bool retryStructurallyUsable = ReadBool(retry, "ok", false)
                    && StructuredResponseIsComplete(
                        ReadString(retry, "content", ""), auditMode)
                    && DialogueService.StructuredActionResponseHasDecision(ReadString(retry, "content", ""), request);
                bool retryPassedQualityGate = retryStructurallyUsable
                    && ConversationVisibleReplyPassesRequestQualityGate(
                        ReadString(retry, "content", ""), request, auditMode);
                DiagnosticValidation("Recheck repaired answer", retryPassedQualityGate,
                    DiagnosticStructuredIssues(ReadString(retry, "content", ""), auditMode, request), ReadString(retry, "content", ""));
                Dictionary<string, object> retryObject = retryStructurallyUsable
                    ? TryParseJsonObject(ReadString(retry, "content", ""))
                    : null;
                if (retryStructurallyUsable && retryObject != null)
                {
                    DialogueService.PreserveFormatRepairMetadata(initiallyParsed, retryObject);
                    DialogueService.MarkRepairedVisibleResponse(
                        retryObject, retryPassedQualityGate, "format_recovery");
                    retry["content"] = Json.Serialize(retryObject);
                }
                Dictionary<string, object> attemptDiagnostics = new Dictionary<string, object>
                {
                    ["attempt"] = attempt,
                    ["retryOk"] = ReadBool(retry, "ok", false),
                    ["retryParsed"] = retryStructurallyUsable,
                    ["revalidationCleared"] = retryPassedQualityGate,
                    ["secondAttemptReturned"] = retryStructurallyUsable,
                    ["visibleRepairMarker"] = retryStructurallyUsable
                        ? (retryPassedQualityGate ? ".." : ".,")
                        : "",
                    ["retryContentChars"] = ReadString(retry, "content", "").Length,
                    ["retryError"] = ReadString(retry, "error", ""),
                    ["finishReason"] = ReadString(retry, "finishReason", ""),
                    ["failureKind"] = retryPassedQualityGate ? "" : DialogueService.StructuredConversationFailureKind(retry),
                    ["originalResponseProvided"] = true,
                    ["characterContextProvided"] = repeatCharacterContext
                };
                attemptDiagnostics["requestCharacters"] = retryMessages.Sum(message => ReadString(message, "content", "").Length);
                attemptDiagnostics["callKind"] = ConversationStructuredObjectHasUsableVisibleReply(initiallyParsed) ? "format_repair" : "full_regeneration";
                attemptDiagnostics["usage"] = ReadDictionary(retry, "usage") ?? new Dictionary<string, object>();
                attempts.Add(attemptDiagnostics);
                OperationsService.WriteAudit(campaignId, correlationId, "server", auditMode, "llm.format_retry", heroId, "", eventId,
                    !retryStructurallyUsable ? "failed"
                        : retryPassedQualityGate ? "completed"
                        : "completed_with_revalidation_override",
                    ReadLong(retry, "durationMs", 0),
                    !retryStructurallyUsable
                        ? "Structured LLM format retry did not produce a complete JSON response."
                        : retryPassedQualityGate
                            ? "Incomplete structured LLM output was repaired by an uncached retry."
                            : "The structurally complete format repair remained quality-gate rejected but was returned without a canned fallback.",
                    attemptDiagnostics);
                if (retryStructurallyUsable)
                {
                    if (regenerateEnvelope)
                    {
                        var recoveredEvidence = VerificationService.CloneDictionary(ReadDictionary(llm, "responseRecovery"));
                        recoveredEvidence["regenerated"] = true;
                        recoveredEvidence["additionalProviderCalls"] = 1
                            + ReadInt(ReadDictionary(retry, "structuredActionMetadataRepair"), "providerCalls", 0);
                        recoveredEvidence["acceptedResponseSha256"] = DialogueService.ConversationExactTextHash(ReadString(retry, "content", ""));
                        retry["responseRecovery"] = recoveredEvidence;
                        var acceptedEnvelope = TryParseJsonObject(ReadString(retry, "content", ""));
                        acceptedEnvelope["conversationResponseRecovery"] = recoveredEvidence;
                        retry["content"] = Json.Serialize(acceptedEnvelope);
                    }
                    diagnostics["completedAttempt"] = attempt;
                    retry["formatRetry"] = diagnostics;
                    return retry;
                }
            }

            llm["formatRetry"] = diagnostics;
            llm["ok"] = false;
            llm["errorCode"] = "structured_response_incomplete_after_retries";
            llm["error"] = "The provider did not return a complete dialogue response after " + maximumAttempts.ToString(CultureInfo.InvariantCulture)
                + " bounded recovery attempt(s). No transcript or memory was written from the partial output.";
            return llm;
        }

        internal static bool FormatRetryNeedsCharacterContext(string rejectedContent)
        {
            string trimmed = (rejectedContent ?? "").Trim();
            return trimmed.Length > 2 && !string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsConversationStructuredMode(string normalizedMode)
        {
            return normalizedMode == "dialogue"
                || normalizedMode.Contains("social_event")
                || normalizedMode.Contains("party_chat")
                || normalizedMode.Contains("castle_chat");
        }

        internal static bool ConversationVisibleReplyPassesRequestQualityGate(
            string content,
            Dictionary<string, object> request,
            string auditMode)
        {
            string normalizedMode = ActionPipelineService.NormalizeLookup(auditMode).Replace(' ', '_');
            if (!IsConversationStructuredMode(normalizedMode)) return true;
            Dictionary<string, object> parsed = TryParseJsonObject(content);
            if (parsed == null) return false;
            bool eventMode = normalizedMode.Contains("social_event")
                || normalizedMode.Contains("party_chat")
                || normalizedMode.Contains("castle_chat");
            string visibleReply = ReadFirstString(
                parsed, "reply", "response", "text", "content");
            if (eventMode
                && WorldSimulationService.NormalizeSocialEventParticipation(parsed) == "quiet"
                && string.IsNullOrWhiteSpace(visibleReply))
                return true;
            if (!ConversationStructuredObjectHasUsableVisibleReply(parsed) || ContainsProviderDialogueOutcomeNotice(visibleReply))
                return false;
            return !IsBareQuotedLatestPlayerEcho(
                visibleReply,
                ReadString(request, "playerText", ""));
        }

        private static bool IsBareQuotedLatestPlayerEcho(
            string visibleReply,
            string latestPlayerText)
        {
            string trimmed = (visibleReply ?? "").Trim();
            if (trimmed.Length < 3 || trimmed.Length > 240
                || string.IsNullOrWhiteSpace(latestPlayerText))
                return false;
            char first = trimmed[0];
            char last = trimmed[trimmed.Length - 1];
            bool quotePair = (first == '\'' && last == '\'')
                || (first == '"' && last == '"')
                || (first == '\u2018' && last == '\u2019')
                || (first == '\u201C' && last == '\u201D');
            if (!quotePair) return false;
            string inner = NormalizeConversationEchoText(
                trimmed.Substring(1, trimmed.Length - 2));
            if (string.IsNullOrWhiteSpace(inner)
                || inner.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length > 24)
                return false;
            string prompt = NormalizeConversationEchoText(latestPlayerText);
            return (" " + prompt + " ").Contains(" " + inner + " ");
        }

        private static string NormalizeConversationEchoText(string value)
        {
            string withoutApostrophes = Regex.Replace(
                (value ?? "").Normalize(NormalizationForm.FormKC),
                @"['\u2018\u2019]",
                "");
            return Regex.Replace(
                    withoutApostrophes,
                    @"[^\p{L}\p{N}]+",
                    " ")
                .Trim()
                .ToLowerInvariant();
        }

        internal static bool ConversationStructuredObjectHasUsableVisibleReply(
            Dictionary<string, object> parsed)
        {
            if (parsed == null || parsed.Count == 0) return false;
            string visibleReply = ReadFirstString(
                parsed, "reply", "response", "content");
            if (!string.IsNullOrWhiteSpace(visibleReply))
                return !IsConversationParticipationControlToken(visibleReply);
            string text = ReadString(parsed, "text", "");
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (IsConversationParticipationControlToken(text)) return false;
            bool carriesConversationShape = parsed.ContainsKey("emotion")
                || parsed.ContainsKey("intent")
                || parsed.ContainsKey("relationshipSignal")
                || parsed.ContainsKey("relationship_signal")
                || parsed.ContainsKey("relationshipAssessments")
                || parsed.ContainsKey("relationship_assessments")
                || ReadDictionary(parsed, "actionGate") != null
                || ReadDictionary(parsed, "action_gate") != null
                || ReadDictionary(parsed, "decisionBrief") != null
                || ReadDictionary(parsed, "decision_brief") != null
                || parsed.ContainsKey("participation");
            // A bare text field is also used by private comprehension, memory and
            // evidence records. Only a positively identified legacy conversation
            // envelope may use this alias; private-key blacklists are insufficient.
            return carriesConversationShape;
        }

        private static bool IsConversationParticipationControlToken(
            string value)
        {
            string normalized = ActionPipelineService.NormalizeLookup(value).Replace(' ', '_');
            return normalized == "speak"
                || normalized == "agree"
                || normalized == "disagree"
                || normalized == "quiet";
        }

        internal static bool TryRepairShiftedConversationReplyKeys(
            Dictionary<string, object> parsed,
            out Dictionary<string, object> repaired)
        {
            repaired = null;
            if (parsed == null || parsed.Count < 3 || parsed.ContainsKey("reply")
                || ConversationStructuredObjectHasUsableVisibleReply(parsed))
                return false;

            string markerKey = null;
            string contentKey = null;
            string visibleReply = "";
            foreach (KeyValuePair<string, object> pair in parsed)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) || !(pair.Value is string value))
                    continue;
                if (value.Trim().Equals("reply", StringComparison.OrdinalIgnoreCase))
                {
                    if (markerKey != null) return false;
                    markerKey = pair.Key;
                }
                else if (!string.IsNullOrWhiteSpace(value))
                {
                    if (contentKey != null) return false;
                    contentKey = pair.Key;
                    visibleReply = value;
                }
            }

            bool carriesConversationShape = ReadDictionary(parsed, "actionGate") != null
                || ReadDictionary(parsed, "action_gate") != null
                || ReadDictionary(parsed, "decisionBrief") != null
                || ReadDictionary(parsed, "decision_brief") != null
                || parsed.ContainsKey("relationshipAssessments")
                || parsed.ContainsKey("relationship_assessments");
            // A single whitespace-named value is also recoverable when the
            // surrounding typed dialogue metadata identifies it unambiguously.
            if (contentKey == null || string.IsNullOrWhiteSpace(visibleReply)
                || !carriesConversationShape)
                return false;

            repaired = new Dictionary<string, object>(parsed, StringComparer.OrdinalIgnoreCase);
            if (markerKey != null) repaired.Remove(markerKey);
            repaired.Remove(contentKey);
            repaired["reply"] = visibleReply;
            return true;
        }

        internal static Dictionary<string, object> CompleteConversationStructuredResponse(
            Dictionary<string, object> parsed, string normalizedMode, out List<string> completedFields)
        {
            Dictionary<string, object> completed = new Dictionary<string, object>(parsed ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            List<string> fields = new List<string>();
            Action<string, object> add = (key, value) =>
            {
                if (completed.ContainsKey(key)) return;
                completed[key] = value;
                fields.Add(key);
            };
            Action<string, string[]> normalizeObjectArray = (canonicalKey, aliases) =>
            {
                List<string> keys = new[] { canonicalKey }.Concat(aliases ?? new string[0])
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                bool found = false;
                bool malformed = false;
                List<Dictionary<string, object>> valid = new List<Dictionary<string, object>>();
                foreach (string key in keys)
                {
                    if (!completed.TryGetValue(key, out object raw))
                        continue;
                    found = true;
                    if (!StructuredCollectionIsDictionaryArray(raw))
                        malformed = true;
                    valid.AddRange(ActionPipelineService.ObjectsFromValue(raw).OfType<Dictionary<string, object>>());
                    if (!key.Equals(canonicalKey, StringComparison.OrdinalIgnoreCase))
                        completed.Remove(key);
                }
                if (!found || malformed || !completed.ContainsKey(canonicalKey))
                {
                    completed[canonicalKey] = valid;
                    if (!fields.Contains(canonicalKey, StringComparer.OrdinalIgnoreCase))
                        fields.Add(canonicalKey);
                }
            };

            if (normalizedMode.Contains("social_event")
                || normalizedMode.Contains("party_chat")
                || normalizedMode.Contains("castle_chat"))
            {
                add("participation", "speak");
                add("reactionTargetHeroStringId", "");
            }
            add("emotion", "guarded");
            add("intent", "respond_in_character");
            add("relationshipSignal", "unchanged");
            string assessmentAbsence = DialogueService.ConversationAssessmentAbsenceReason(completed);
            if (assessmentAbsence == "relationship_assessment_missing") completed["relationshipAssessmentStatus"] = "missing";
            else if (assessmentAbsence == "relationship_assessment_invalid"
                && !StructuredObjectArrayFieldsAreValid(completed, false, "relationshipAssessments", "relationship_assessments"))
                completed["relationshipAssessmentStatus"] = "invalid";
            normalizeObjectArray("relationshipAssessments", new[] { "relationship_assessments" });
            Dictionary<string, object> decisionBrief = ReadDictionary(completed, "decisionBrief")
                ?? ReadDictionary(completed, "decision_brief");
            if (decisionBrief == null)
            {
                decisionBrief = new Dictionary<string, object>();
                fields.Add("decisionBrief");
            }
            else
            {
                decisionBrief = new Dictionary<string, object>(decisionBrief, StringComparer.OrdinalIgnoreCase);
            }
            foreach (string listField in new[] { "facts", "goals", "constraints", "activeDomains", "appliedEvidenceKeys" })
            {
                if (!decisionBrief.TryGetValue(listField, out object rawList)
                    || !StructuredCollectionIsStringArray(rawList))
                {
                    decisionBrief[listField] = ActionPipelineService.ObjectsFromValue(rawList)
                        .OfType<string>()
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                    if (!fields.Contains("decisionBrief." + listField, StringComparer.OrdinalIgnoreCase))
                        fields.Add("decisionBrief." + listField);
                }
            }
            if (string.IsNullOrWhiteSpace(ReadString(decisionBrief, "decision", "")))
                decisionBrief["decision"] = "Respond in character without claiming an unsupported mechanical action.";
            if (!decisionBrief.ContainsKey("confidence"))
                decisionBrief["confidence"] = 0.25d;
            completed.Remove("decision_brief");
            completed["decisionBrief"] = decisionBrief;
            add("actionGate", new Dictionary<string, object>
            {
                ["needed"] = false, ["commitment"] = "roleplay_only", ["intent"] = "", ["confidence"] = 0.5d,
                ["reason"] = "The provider omitted the private action gate, so no native action may be inferred from the visible reply."
            });
            foreach (string arrayField in new[]
            {
                "memoryWrites", "beliefWrites", "relationshipUpdates",
                "obligationWrites", "comprehensionWrites", "dynamicCharacteristicWrites",
                "suggestedActions"
            })
            {
                string snake = Regex.Replace(arrayField, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
                normalizeObjectArray(arrayField, new[] { snake });
            }
            Dictionary<string, object> stateUpdates = ReadDictionary(completed, "stateUpdates")
                ?? ReadDictionary(completed, "state_updates");
            if (stateUpdates == null)
            {
                completed["stateUpdates"] = new Dictionary<string, object>();
                if (!fields.Contains("stateUpdates", StringComparer.OrdinalIgnoreCase))
                    fields.Add("stateUpdates");
            }
            completed.Remove("state_updates");
            completedFields = fields;
            return completed;
        }

        internal static bool StructuredCollectionIsDictionaryArray(object value)
        {
            if (value is ArrayList array)
                return array.Cast<object>().All(item => item is Dictionary<string, object>);
            if (value is IEnumerable<object> enumerable)
                return enumerable.All(item => item is Dictionary<string, object>);
            return false;
        }

        private static bool StructuredCollectionIsStringArray(object value)
        {
            if (value is ArrayList array)
                return array.Cast<object>().All(item => item is string);
            if (value is IEnumerable<object> enumerable)
                return enumerable.All(item => item is string);
            return false;
        }

        private static bool StructuredObjectArrayFieldsAreValid(
            Dictionary<string, object> parsed,
            bool required,
            params string[] keys)
        {
            bool found = false;
            foreach (string key in keys ?? new string[0])
            {
                if (parsed == null || !parsed.TryGetValue(key, out object value))
                    continue;
                found = true;
                if (!StructuredCollectionIsDictionaryArray(value))
                    return false;
            }
            return found || !required;
        }

        private static bool ConversationStructuredCollectionsAreValid(
            Dictionary<string, object> parsed)
        {
            if (!StructuredObjectArrayFieldsAreValid(parsed, true,
                    "relationshipAssessments", "relationship_assessments"))
                return false;
            foreach (string[] keys in new[]
            {
                new[] { "memoryWrites", "memory_writes" },
                new[] { "beliefWrites", "belief_writes" },
                new[] { "relationshipUpdates", "relationship_updates" },
                new[] { "obligationWrites", "obligation_writes" },
                new[] { "comprehensionWrites", "comprehension_writes" },
                new[] { "continuityWrites" },
                new[] { "dynamicCharacteristicWrites", "dynamic_characteristic_writes" },
                new[] { "suggestedActions", "suggested_actions" }
            })
            {
                if (!StructuredObjectArrayFieldsAreValid(parsed, false, keys))
                    return false;
            }
            Dictionary<string, object> brief = ReadDictionary(parsed, "decisionBrief")
                ?? ReadDictionary(parsed, "decision_brief");
            if (brief == null)
                return false;
            foreach (string key in new[] { "facts", "goals", "constraints", "activeDomains", "appliedEvidenceKeys" })
            {
                if (brief.TryGetValue(key, out object value)
                    && !StructuredCollectionIsStringArray(value))
                    return false;
            }
            return true;
        }

        internal static bool StructuredResponseIsComplete(string content, string auditMode)
        {
            Dictionary<string, object> parsed = TryParseJsonObject(content);
            if (parsed == null || parsed.Count == 0)
            {
                return false;
            }

            string mode = ActionPipelineService.NormalizeLookup(auditMode).Replace(' ', '_');
            if (mode == "action_planner")
            {
                return parsed.ContainsKey("actions");
            }
            if (mode == "generated_wilderness_event")
            {
                return !string.IsNullOrWhiteSpace(ReadFirstString(parsed, "openingText", "opening_text", "event_opening_text", "opening"));
            }
            if (mode == "diplomacy_decision")
            {
                string decision = ActionPipelineService.NormalizeLookup(ReadString(parsed, "decision", ""));
                return decision == "none" || decision == "unilateral" || decision == "proposal";
            }
            if (mode == "diplomacy_response")
            {
                string response = ActionPipelineService.NormalizeLookup(ReadString(parsed, "response", ""));
                return response == "accept" || response == "refuse" || response == "counter";
            }
            if (mode == "dialogue"
                || mode.Contains("social_event")
                || mode.Contains("party_chat")
                || mode.Contains("castle_chat"))
            {
                bool eventMode = mode.Contains("social_event")
                    || mode.Contains("party_chat")
                    || mode.Contains("castle_chat");
                string eventParticipation = eventMode
                    ? WorldSimulationService.NormalizeSocialEventParticipation(parsed)
                    : "speak";
                bool visibleReplyComplete = eventMode
                    && eventParticipation == "quiet"
                    ? string.IsNullOrWhiteSpace(ReadFirstString(
                        parsed, "reply", "response", "text", "content"))
                    : ConversationStructuredObjectHasUsableVisibleReply(parsed);
                return visibleReplyComplete
                    && (ReadDictionary(parsed, "actionGate") != null || ReadDictionary(parsed, "action_gate") != null)
                    && parsed.ContainsKey("emotion") && parsed.ContainsKey("intent")
                    && (parsed.ContainsKey("relationshipSignal") || parsed.ContainsKey("relationship_signal"))
                    && (parsed.ContainsKey("relationshipAssessments") || parsed.ContainsKey("relationship_assessments"))
                    && (ReadDictionary(parsed, "decisionBrief") != null || ReadDictionary(parsed, "decision_brief") != null)
                    && ConversationStructuredCollectionsAreValid(parsed)
                    && (!eventMode || parsed.ContainsKey("participation"));
            }
            if (mode.Contains("correspondence"))
            {
                if (!parsed.ContainsKey("shouldReply")) return false;
                bool shouldReply = ReadBool(parsed, "shouldReply", false);
                return (!shouldReply || !string.IsNullOrWhiteSpace(ReadFirstString(parsed, "body", "letter", "reply", "response")))
                    && (ReadDictionary(parsed, "actionGate") != null || ReadDictionary(parsed, "action_gate") != null)
                    && parsed.ContainsKey("rebellionDecision")
                    && ReadDictionary(parsed, "campaignOrder") != null;
            }

            return true;
        }

        internal static string ExtractVisibleReply(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            string extracted = ExtractJsonStringField(content, "reply");
            if (string.IsNullOrWhiteSpace(extracted))
            {
                extracted = ExtractJsonStringField(content, "response");
            }

            if (string.IsNullOrWhiteSpace(extracted))
            {
                extracted = content;
            }

            return SanitizeVisibleReply(extracted);
        }

        private static string ExtractJsonStringField(string text, string fieldName)
        {
            var parsed = TryParseStrictJsonObject(text);
            if (parsed != null) return ReadString(parsed, fieldName, "");
            var envelope = ReadConversationResponseEnvelope(text);
            return envelope.Error.Length == 0 ? ReadString(envelope.Fields, fieldName, "") : "";
        }

        internal static string SanitizeVisibleReply(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string cleaned = text.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\t", " ").Replace("\\\"", "\"").Trim();
            if (cleaned.StartsWith("{", StringComparison.Ordinal))
            {
                string extracted = ExtractJsonStringField(cleaned, "reply");
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    cleaned = extracted.Trim();
                }
            }

            foreach (string marker in new[]
            {
                "\"internalThoughts\"",
                "\"internal_thoughts\"",
                "internalThoughts:",
                "\"decisionBrief\"",
                "\"decision_brief\"",
                "FACT CHECK:",
                "\"memoryWrites\"",
                "\"beliefWrites\"",
                "\"obligationWrites\"",
                "\"comprehensionWrites\"",
                "\"stateUpdates\"",
                "\"suggestedActions\"",
                "\"relationshipSignal\"",
                "\"relationshipAssessments\"",
                "\"emotion\"",
                "\"intent\""
            })
            {
                int index = cleaned.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    cleaned = cleaned.Substring(0, index);
                }
            }

            cleaned = StripVisibleReplySectionLabels(cleaned);
            bool failedRepairMarker = cleaned.TrimEnd().EndsWith(".,", StringComparison.Ordinal);
            // A leading quote is often the first character of legitimate spoken
            // dialogue while its matching quote ends before trailing narration.
            // Trimming quote characters from the whole payload produced visible
            // fragments such as `Rhovarion, then."`; only structural JSON braces
            // and commas should be removed here.
            cleaned = cleaned.Trim().Trim(',', '{', '}').Trim();
            if (ContainsPrivateReplyLeak(cleaned))
            {
                return "[Bannerlord Reign: the model returned malformed JSON, so the visible reply was hidden. Please try again.]";
            }

            return failedRepairMarker ? DialogueService.MarkRepairedVisibleText(cleaned, false) : cleaned;
        }

        private static string StripVisibleReplySectionLabels(string text)
        {
            string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            bool inTimeSection = false;
            bool inActionSection = false;
            List<string> kept = new List<string>();

            foreach (string line in lines)
            {
                string label = VisibleReplySectionLabel(line);
                if (label == "time extraction" || label == "time & place")
                {
                    inTimeSection = true;
                    inActionSection = false;
                    continue;
                }

                if (label == "dialogue")
                {
                    if (inTimeSection && kept.Count > 0 && !string.IsNullOrWhiteSpace(kept[kept.Count - 1]))
                    {
                        kept.Add(string.Empty);
                    }

                    inTimeSection = false;
                    inActionSection = false;
                    continue;
                }

                if (label == "action")
                {
                    if (inTimeSection && kept.Count > 0 && !string.IsNullOrWhiteSpace(kept[kept.Count - 1]))
                    {
                        kept.Add(string.Empty);
                    }

                    inTimeSection = false;
                    inActionSection = true;
                    continue;
                }

                if (inTimeSection)
                {
                    string timeLine = line.Trim();
                    if (!string.IsNullOrWhiteSpace(timeLine) && !IsUnavailableTimeLine(timeLine))
                    {
                        kept.Add(timeLine);
                    }

                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    if (kept.Count > 0 && !string.IsNullOrWhiteSpace(kept[kept.Count - 1]))
                    {
                        kept.Add(string.Empty);
                    }

                    continue;
                }

                string keptLine = line.TrimEnd();
                if (inActionSection && !IsAsteriskWrappedAction(keptLine))
                {
                    keptLine = "*" + keptLine.Trim() + "*";
                }

                kept.Add(keptLine);
            }

            while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[kept.Count - 1]))
            {
                kept.RemoveAt(kept.Count - 1);
            }

            return EnsureActionParagraphSpacing(kept);
        }

        private static string VisibleReplySectionLabel(string line)
        {
            return (line ?? string.Empty)
                .Trim()
                .Trim('[', ']', ':')
                .Trim()
                .ToLowerInvariant();
        }

        private static bool IsUnavailableTimeLine(string line)
        {
            string normalized = (line ?? string.Empty).Trim().Trim('.', '-', ':').Trim().ToLowerInvariant();
            return normalized == "unknown"
                || normalized == "unavailable"
                || normalized == "not supplied"
                || normalized == "no time data"
                || normalized == "no time & place data";
        }

        private static bool IsAsteriskWrappedAction(string line)
        {
            string trimmed = (line ?? string.Empty).Trim();
            return trimmed.Length >= 2 && trimmed[0] == '*' && trimmed[trimmed.Length - 1] == '*';
        }

        private static string EnsureActionParagraphSpacing(List<string> lines)
        {
            List<string> spaced = new List<string>();
            foreach (string line in lines ?? new List<string>())
            {
                bool isAction = IsAsteriskWrappedAction(line);
                if (isAction && spaced.Count > 0 && !string.IsNullOrWhiteSpace(spaced[spaced.Count - 1]))
                {
                    spaced.Add(string.Empty);
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    if (spaced.Count > 0 && !string.IsNullOrWhiteSpace(spaced[spaced.Count - 1]))
                    {
                        spaced.Add(string.Empty);
                    }
                    continue;
                }

                spaced.Add(line.TrimEnd());
                if (isAction)
                {
                    spaced.Add(string.Empty);
                }
            }

            while (spaced.Count > 0 && string.IsNullOrWhiteSpace(spaced[spaced.Count - 1]))
            {
                spaced.RemoveAt(spaced.Count - 1);
            }

            return string.Join("\n", spaced);
        }

        private static bool ContainsPrivateReplyLeak(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string lowered = text.ToLowerInvariant();
            return lowered.Contains("internalthoughts")
                || lowered.Contains("memorywrites")
                || lowered.Contains("stateupdates")
                || lowered.Contains("suggestedactions")
                || lowered.Contains("relationshipsignal")
                || lowered.Contains("relationshipassessments")
                || lowered.Contains("fact check:");
        }
    }
}
