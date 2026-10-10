using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string BuildDialogueAgencyLedger(
            string speakerId, List<Dictionary<string, object>> priorLines,
            List<Dictionary<string, object>> violations)
        {
            var entries = (violations ?? new List<Dictionary<string, object>>())
                .Where(v => new[] { "request_agency_inverted", "recalled_speaker_inverted", "source_proven_meeting_order_reversed", "source_proven_native_spouse_conflict" }.Contains(ReadString(v, "type", "")))
                .Select(v => new Dictionary<string, object> {
                    ["speakerHeroStringId"] = ReadString(v, "sourceSpeakerId", ReadString(v, "originalRequestOwner", speakerId)),
                    ["speakerRole"] = ReadString(v, "sourceSpeakerId", ReadString(v, "originalRequestOwner", speakerId)) == speakerId ? "current_npc" : "original_source",
                    ["evidenceKind"] = ReadString(v, "evidenceKind", "original_spoken_statement"),
                    ["exactLine"] = ReadString(v, "originatingNpcLine", ""),
                    ["sourceId"] = ReadString(v, "sourceId", ""), ["sourceHash"] = ReadString(v, "sourceHash", ""),
                    ["start"] = ReadInt(v, "sourceStart", 0), ["length"] = ReadInt(v, "sourceLength", 0),
                    ["requestOwner"] = ReadString(v, "originalRequestOwner", ""),
                    ["performer"] = ReadString(v, "performer", ""), ["observer"] = ReadString(v, "observer", "") }).ToList();
            if (entries.Count == 0) return string.Empty;
            return "AUTHORITATIVE SOURCE AND REQUEST LEDGER\n"
                + Json.Serialize(entries)
                + "\nPreserve who originated each statement, request, invitation, offer, or plan and the supplied evidence kind. Native state is not spoken dialogue. Acceptance by another person does not transfer authorship. Never say the player asked for an experience that the NPC requested, or the reverse.";
        }

        private static string BuildRoleEquivalenceLedger(Dictionary<string, object> payload)
        {
            List<string> facts = new List<string>();
            foreach (Dictionary<string, object> profile in MergedInteractionParticipantProfiles(payload))
            {
                string id = CharacterIdFrom(profile);
                string spouseId = ReadString(profile, "spouseId", "");
                string sovereignId = ReadString(profile, "sovereignHeroStringId", "");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(spouseId)
                    || !spouseId.Equals(sovereignId, StringComparison.OrdinalIgnoreCase)) continue;
                string subjectName = FirstNonEmpty(ReadString(profile, "name", ""), id);
                string sovereignName = FirstNonEmpty(ReadString(profile, "sovereignName", ""), sovereignId);
                facts.Add(sovereignName
                    + " [" + sovereignId + "] is simultaneously the spouse and sovereign of "
                    + subjectName + " [" + id + "]. These titles identify one person, not two people.");
                if (ReadBool(profile, "isFemale", false))
                {
                    facts.Add(subjectName + " [" + id + "] is the female royal consort of " + sovereignName
                        + ". Queen or queen consort is a valid non-ruling style for her unless authoritative cultural evidence supplies an equivalent consort style. She is not the reigning sovereign unless isRuler is true, and marriage alone grants no independent ruler or diplomatic authority.");
                }
                else
                {
                    facts.Add(subjectName + " [" + id + "] is the royal consort of " + sovereignName
                        + ", not the reigning sovereign unless isRuler is true. Do not invent an independent crown or authority from marriage alone.");
                }
            }
            return facts.Count == 0 ? string.Empty
                : "AUTHORITATIVE CUMULATIVE ROLE EQUIVALENCE\n- " + string.Join("\n- ", facts);
        }

        private static Dictionary<string, object> RetryDialogueIntegrityViolation(
            Dictionary<string, object> llm, Dictionary<string, object> request,
            Dictionary<string, object> payload, List<Dictionary<string, object>> priorLines,
            string campaignId, string correlationId, string auditMode, string heroId, string eventId,
            Func<Dictionary<string, object>, Dictionary<string, object>> responder = null, bool persist = true)
        {
            if (!ReadBool(llm, "ok", false)) return llm;
            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", ""));
            if (parsed == null) return llm;
            List<Dictionary<string, object>> violations = FindDialogueIntegrityViolations(parsed, payload, priorLines, heroId);
            var claimReceipt = CompareDialogueRecallClaims(parsed, payload, priorLines, heroId);
            claimReceipt["withheldEffects"] = QuarantineDialogueClaimEffects(parsed, payload, priorLines, heroId);
            claimReceipt["withheldIncompleteFacts"] = QuarantineUncoveredDialogueFacts(parsed, payload, priorLines, heroId);
            claimReceipt["exactSourceViolations"] = violations.Where(v => new[] { "recalled_speaker_inverted", "source_proven_meeting_order_reversed", "source_proven_native_spouse_conflict" }.Contains(ReadString(v, "type", ""))).ToList();
            parsed["dialogueClaimValidation"] = claimReceipt;
            llm["dialogueClaimValidation"] = claimReceipt;
            llm["content"] = Json.Serialize(parsed);
            if (violations.Count == 0) return llm;
            if (TryCorrectExactDialogueOwnership(parsed, payload, priorLines, heroId, violations, out var localCorrection))
            {
                claimReceipt["correction"] = "exact_source_speaker_only";
                claimReceipt["changedFields"] = new[] { new[] { "reply", "body", "response", "text" }.FirstOrDefault(parsed.ContainsKey) ?? "reply" };
                claimReceipt["remainingExactSourceViolations"] = 0;
                claimReceipt["acceptedSemanticCoverage"] = "not_rechecked_after_prose_correction";
                MarkRepairedVisibleResponse(localCorrection, true, "visible_prose_correction");
                claimReceipt["acceptedReplyHash"] = MemorySelectionTextHash(ReadFirstString(localCorrection, "reply", "body", "response", "text")).ToLowerInvariant();
                localCorrection["dialogueClaimValidation"] = claimReceipt;
                llm["content"] = Json.Serialize(localCorrection);
                if (persist) WriteAudit(campaignId, correlationId, "server", auditMode, "llm.dialogue_claim_validation",
                    heroId, "", eventId, "completed", 0, "Recalled speaker corrected from the exclusive exact original clause.", claimReceipt);
                return llm;
            }

            string originalJson = Json.Serialize(parsed);
            Dictionary<string, object> repairRequest = new Dictionary<string, object>
            {
                ["requestType"] = ReadString(request, "requestType", "dialogue") + "_integrity_repair",
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId + "-integrity-repair",
                ["heroStringId"] = heroId,
                ["eventId"] = eventId,
                ["promptCacheEligible"] = false,
                ["reasoningDisabled"] = true,
                ["temperature"] = 0d,
                ["maxTokens"] = Math.Max(6000, ReadInt(request, "maxTokens", 6000)),
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" },
                ["messages"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] = "Return one JSON object containing only the corrected reply string. Preserve useful content, tone, personality, decisions, and scene progression. Correct only the supplied source-proven attribution, chronology, current native family-state or cumulative-role errors using the pinned original clauses and actors. An accepted invitation remains authored by the person who proposed it. Two simultaneous roles with the same hero id refer to one person. A sovereign's spouse is their royal consort; queen or queen consort is a valid non-ruling style for a female consort, but consort status does not make her the reigning sovereign or grant independent authority. Do not return or change relationship numbers, consent, actions or memory metadata; the server retains independent valid fields and invalidates changed source citations."
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = BuildRoleEquivalenceLedger(payload) + "\n\n"
                            + BuildDialogueAgencyLedger(heroId, priorLines, violations) + "\n\nDETECTED ERRORS\n"
                            + Json.Serialize(violations) + "\n\nCHARACTER CONTEXT\n"
                            + Json.Serialize(DialogueValidationRepairCharacterContext(request))
                            + BuildDialogueClaimRepairGuidance(claimReceipt, violations)
                            + "\n\nORIGINAL JSON TO REPAIR\n" + originalJson
                    }
                }
            };
            string requestedModel = ReadString(request, "model", "");
            if (!string.IsNullOrWhiteSpace(requestedModel)) repairRequest["model"] = requestedModel;
            Dictionary<string, object> repaired = (responder ?? ChatWithLlm)(repairRequest);
            Dictionary<string, object> repairedParsed = MergeDialogueIntegrityProse(parsed,
                TryParseJsonObject(ReadString(repaired, "content", "")), violations);
            List<Dictionary<string, object>> remaining = repairedParsed == null
                ? violations : FindDialogueIntegrityViolations(repairedParsed, payload, priorLines, heroId);
            bool usable = ReadBool(repaired, "ok", false) && repairedParsed != null
                && StructuredResponseIsComplete(Json.Serialize(repairedParsed), auditMode);
            bool cleared = usable && remaining.Count == 0;
            bool markedFailure = false;
            if (cleared)
            {
                claimReceipt["remainingExactSourceViolations"] = 0;
                claimReceipt["acceptedSemanticCoverage"] = "not_rechecked_after_prose_correction";
                claimReceipt["withheldRevisedEffects"] = QuarantineDialogueClaimEffects(repairedParsed, payload, priorLines, heroId);
                claimReceipt["withheldRevisedIncompleteFacts"] = QuarantineUncoveredDialogueFacts(repairedParsed, payload, priorLines, heroId);
                CarryConversationRepairState(parsed, repairedParsed);
                MarkRepairedVisibleResponse(repairedParsed, cleared);
                claimReceipt["acceptedReplyHash"] = MemorySelectionTextHash(ReadFirstString(repairedParsed, "reply", "body", "response", "text")).ToLowerInvariant();
                repairedParsed["dialogueClaimValidation"] = claimReceipt;
                repaired["dialogueClaimValidation"] = claimReceipt;
                repaired["content"] = Json.Serialize(repairedParsed);
            }
            else
            {
                if (violations.All(v => new[] { "recalled_speaker_inverted", "source_proven_meeting_order_reversed", "source_proven_native_spouse_conflict" }.Contains(ReadString(v, "type", ""))))
                {
                    // An unusable semantic repair must not replace useful speech
                    // with a negotiation failure or strand the conversation. Remove
                    // only the source-proven false attribution and its dependents.
                    string visible = ReadFirstString(parsed, "reply", "body", "response", "text");
                    foreach (var v in violations.OrderByDescending(v => ReadInt(v, "currentStart", -1)))
                    {
                        int start = ReadInt(v, "currentStart", -1), length = ReadInt(v, "currentLength", 0);
                        if (start >= 0 && start + length <= visible.Length) visible = visible.Remove(start, length);
                    }
                    if (string.IsNullOrWhiteSpace(visible.Trim(' ', '\r', '\n', '.', ',', '!', '?')))
                        visible = "I need a moment to put that properly.";
                    var recovered = MergeDialogueIntegrityProse(parsed, TestDict("reply", visible), violations);
                    if (recovered != null && FindDialogueIntegrityViolations(recovered, payload, priorLines, heroId).Count == 0)
                    {
                        claimReceipt["correction"] = "unsupported_attribution_withheld_after_rewrite_failure";
                        claimReceipt["semanticRewriteFailed"] = true;
                        claimReceipt["remainingExactSourceViolations"] = 0;
                        claimReceipt["acceptedSemanticCoverage"] = "not_rechecked_after_false_clause_withholding";
                        MarkRepairedVisibleResponse(recovered, true, "visible_prose_correction");
                        claimReceipt["acceptedReplyHash"] = MemorySelectionTextHash(ReadFirstString(recovered, "reply", "body", "response", "text")).ToLowerInvariant();
                        recovered["dialogueClaimValidation"] = claimReceipt;
                        llm["content"] = Json.Serialize(recovered);
                        llm["dialogueClaimValidation"] = claimReceipt;
                        if (persist) WriteAudit(campaignId, correlationId, "server", auditMode, "llm.dialogue_claim_validation",
                            heroId, "", eventId, "partial", 0, "Useful dialogue retained; source-proven false attribution withheld after writer repair failure.", claimReceipt);
                        return llm;
                    }
                }
                markedFailure = TryReturnMarkedVisibleFailure(repaired, repairedParsed, parsed, request, auditMode,
                    remaining.Where(v => ReadString(v, "field", "") == "reply")
                        .Select(v => ReadString(v, "currentText", ""))
                        .Where(t => t.Length > 0).ToList());
                if (!markedFailure)
                {
                    repaired["ok"] = false;
                    repaired["errorCode"] = "dialogue_integrity_repair_unusable";
                    repaired["error"] = "The integrity repair returned no usable visible reply.";
                }
            }
            if (persist) WriteAudit(campaignId, correlationId, "server", auditMode, "llm.dialogue_integrity_repair",
                heroId, "", eventId, cleared ? "completed" : "failed",
                ReadLong(repaired, "durationMs", 0), cleared ? "Dialogue agency and role identity were repaired from pinned source clauses."
                    : markedFailure ? "The integrity repair remained rejected; marked dialogue was returned without structured effects."
                    : "Dialogue integrity repair returned no usable visible reply.",
                new Dictionary<string, object> { ["detected"] = violations, ["remaining"] = remaining, ["accepted"] = cleared,
                    ["revalidationCleared"] = cleared, ["markedVisibleFailure"] = markedFailure, ["deterministicFallback"] = markedFailure });
            return repaired;
        }

        private static List<Dictionary<string, object>> FindDialogueIntegrityViolations(
            Dictionary<string, object> parsed, Dictionary<string, object> payload,
            List<Dictionary<string, object>> priorLines, string speakerId)
        {
            List<string> texts = new List<string>();
            CollectConversationStringValues(parsed, texts);
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            Dictionary<string, object> speaker = MergedInteractionParticipantProfiles(payload)
                .FirstOrDefault(x => CharacterIdFrom(x).Equals(speakerId ?? "", StringComparison.OrdinalIgnoreCase));
            if (speaker != null && !string.IsNullOrWhiteSpace(ReadString(speaker, "spouseId", ""))
                && ReadString(speaker, "spouseId", "").Equals(ReadString(speaker, "sovereignHeroStringId", ""), StringComparison.OrdinalIgnoreCase))
            {
                foreach (string text in texts.Where(x => Regex.IsMatch(x ?? "", @"\bmy\s+(?:husband|wife|spouse)\b", RegexOptions.IgnoreCase)
                    && Regex.IsMatch(x ?? "", @"\bmy\s+(?:king|queen|sovereign|ruler)\b", RegexOptions.IgnoreCase)))
                {
                    bool explicitlySeparated = Regex.IsMatch(text,
                        @"\b(?:not\s+the\s+same|different\s+(?:man|woman|person)|my\s+(?:husband|wife|spouse)\s+is\s+not\s+(?:my\s+|the\s+)?(?:king|queen|sovereign|ruler)|my\s+(?:king|queen|sovereign|ruler)\s+is\s+not\s+my\s+(?:husband|wife|spouse)|my\s+(?:king|queen)\s+is\s+my\s+(?:king|queen).{0,100}my\s+(?:husband|wife)\s+is\s+my\s+(?:husband|wife))\b",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    bool explicitlyUnified = Regex.IsMatch(text,
                        @"\b(?:the\s+same\s+(?:man|woman|person)|one\s+and\s+the\s+same|my\s+(?:husband|wife|spouse)\s+is\s+(?:also\s+)?(?:my\s+|the\s+)?(?:king|queen|sovereign|ruler)|my\s+(?:king|queen|sovereign|ruler)\s+is\s+(?:also\s+)?my\s+(?:husband|wife|spouse)|both\s+my\s+(?:husband|wife|spouse)\s+and\s+my\s+(?:king|queen|sovereign|ruler)|who\s+is\s+also)\b",
                        RegexOptions.IgnoreCase);
                    if (explicitlySeparated || !explicitlyUnified)
                        result.Add(new Dictionary<string, object> { ["type"] = "same_person_roles_separated", ["text"] = LimitText(text, 360) });
                }
                if (ReadBool(speaker, "isFemale", false))
                {
                    foreach (string text in texts.Where(x => Regex.IsMatch(x ?? "",
                        @"\b(?:I\s+am|I'm|she\s+is)\s+not\s+(?:a\s+|the\s+)?queen\b|\bwill\s+not\s+claim\s+(?:a\s+)?crown\s+by\s+marriage\b|\bno\s+supplied\s+(?:fact|evidence).{0,100}\bqueen\b",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline)))
                    {
                        result.Add(new Dictionary<string, object> { ["type"] = "sovereign_spouse_consort_status_denied", ["text"] = LimitText(text, 360) });
                    }
                }
            }

            result.AddRange(FindAttributedRequestInversions(ReadFirstString(parsed, "reply", "body", "response", "text"), payload, priorLines, speakerId));
            result.AddRange(FindRecalledSpeakerInversions(parsed, payload, priorLines, speakerId));
            result.AddRange(FindDialogueFactualContradictions(parsed, payload, priorLines, speakerId));
            return result;
        }

        private static List<Dictionary<string, object>> RunDialogueIntegrityAssertions()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (name, pass, detail) => rows.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = pass, ["detail"] = detail });
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["participantProfiles"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["heroStringId"] = "asta", ["name"] = "Asta", ["spouseId"] = "king", ["sovereignHeroStringId"] = "king", ["sovereignName"] = "Raganvad", ["isFemale"] = true, ["isRuler"] = false }
                }
            };
            Dictionary<string, object> parsed = new Dictionary<string, object> { ["reply"] = "My husband agreed, but my king did not." };
            add("same_person_roles_are_cumulative", FindDialogueIntegrityViolations(parsed, payload, new List<Dictionary<string, object>>(), "asta").Any(x => ReadString(x, "type", "") == "same_person_roles_separated"), "Spouse and sovereign with one hero id cannot be narrated as two people.");
            parsed["reply"] = "My husband is not the king. My king is my king; my husband is my husband. Those are not the same man.";
            add("negated_same_person_wording_is_not_a_false_pass", FindDialogueIntegrityViolations(parsed, payload, new List<Dictionary<string, object>>(), "asta").Any(x => ReadString(x, "type", "") == "same_person_roles_separated"), "The words 'same man' do not satisfy role equivalence when they are explicitly negated.");
            parsed["reply"] = "I am not a queen, and I will not claim a crown by marriage.";
            add("female_sovereign_spouse_keeps_consort_status", FindDialogueIntegrityViolations(parsed, payload, new List<Dictionary<string, object>>(), "asta").Any(x => ReadString(x, "type", "") == "sovereign_spouse_consort_status_denied"), "A female spouse of the current sovereign may distinguish consort from ruler but cannot deny the supplied consort status.");
            Dictionary<string, object> rootHeroPayload = new Dictionary<string, object>
            {
                ["hero"] = new Dictionary<string, object>
                {
                    ["heroStringId"] = "asta", ["name"] = "Asta", ["spouseId"] = "king",
                    ["sovereignHeroStringId"] = "king", ["sovereignName"] = "Raganvad",
                    ["fatherId"] = "father", ["fatherName"] = "Olek",
                    ["childrenIds"] = new List<string> { "child" }, ["childrenNames"] = new List<string> { "Miroslava" },
                    ["isFemale"] = true, ["isRuler"] = false
                }
            };
            add("root_hero_role_evidence_is_merged", BuildRoleEquivalenceLedger(rootHeroPayload).Contains("queen consort"), "The complete live hero record must outrank the reduced scene participant so spouse/sovereign equivalence reaches the model and validator.");
            string familyMap = CompactNativeFamilyMap(rootHeroPayload);
            add("absent_native_family_names_are_available", familyMap.Contains("Raganvad [king]") && familyMap.Contains("Olek [father]") && familyMap.Contains("Miroslava [child]"), "Known spouse, parent, and child names remain available even when those relatives are not present in the scene.");
            List<Dictionary<string, object>> history = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["speakerId"] = "asta", ["text"] = "I would like you to take me to the waterfall." } };
            parsed["reply"] = "You asked for the waterfall.";
            add("request_agency_is_preserved", FindDialogueIntegrityViolations(parsed, payload, history, "asta").Any(x => ReadString(x, "type", "") == "request_agency_inverted"), "The NPC-originated waterfall request must not be attributed to the player.");
            rows.AddRange(RunDialogueAgencyEvidenceAssertions());
            rows.AddRange(RunDialogueClaimValidationAssertions());
            return rows;
        }
    }
}
