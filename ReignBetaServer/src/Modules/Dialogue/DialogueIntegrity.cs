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
            string speakerId, List<Dictionary<string, object>> priorLines)
        {
            List<Dictionary<string, object>> entries = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> sourceLines = priorLines ?? new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> line in sourceLines.Skip(Math.Max(0, sourceLines.Count - 18)))
            {
                string text = ReadFirstString(line, "text", "reply", "content");
                string lineSpeaker = ReadFirstString(line, "speakerId", "speakerHeroStringId", "heroStringId");
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!Regex.IsMatch(text,
                    @"\b(?:ask|asked|request|requested|invite|invited|offer|offered|want|wanted|wish|wished|would\s+like|take\s+me|bring\s+me|show\s+me|meet\s+me)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
                entries.Add(new Dictionary<string, object>
                {
                    ["speakerHeroStringId"] = lineSpeaker,
                    ["speakerRole"] = string.Equals(lineSpeaker, speakerId, StringComparison.OrdinalIgnoreCase) ? "current_npc" : "other",
                    ["exactLine"] = LimitText(text, 420)
                });
            }
            if (entries.Count == 0) return string.Empty;
            return "AUTHORITATIVE RECENT REQUEST AND OFFER LEDGER\n"
                + Json.Serialize(entries)
                + "\nPreserve who originated each request, invitation, offer, or plan. Acceptance by another person does not transfer authorship. Never say the player asked for an experience that the NPC requested, or the reverse.";
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
            string campaignId, string correlationId, string auditMode, string heroId, string eventId)
        {
            if (!ReadBool(llm, "ok", false)) return llm;
            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", ""));
            if (parsed == null) return llm;
            List<Dictionary<string, object>> violations = FindDialogueIntegrityViolations(parsed, payload, priorLines, heroId);
            if (violations.Count == 0) return llm;

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
                        ["content"] = "Return exactly one complete JSON object. Preserve the response's useful content, tone, personality, decisions, and scene progression. Correct only the supplied agency or cumulative-role errors. An accepted invitation remains authored by the person who proposed it. Two simultaneous roles with the same hero id refer to one person. A sovereign's spouse is their royal consort; queen or queen consort is a valid non-ruling style for a female consort, but consort status does not make her the reigning sovereign or grant independent authority. Apply the correction to every visible and private field. Do not invent new actions, relationships, or memories."
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = BuildRoleEquivalenceLedger(payload) + "\n\n"
                            + BuildDialogueAgencyLedger(heroId, priorLines) + "\n\nDETECTED ERRORS\n"
                            + Json.Serialize(violations) + "\n\nCHARACTER CONTEXT\n"
                            + Json.Serialize(DialogueValidationRepairCharacterContext(request))
                            + "\n\nORIGINAL JSON TO REPAIR\n" + originalJson
                    }
                }
            };
            string requestedModel = ReadString(request, "model", "");
            if (!string.IsNullOrWhiteSpace(requestedModel)) repairRequest["model"] = requestedModel;
            Dictionary<string, object> repaired = ChatWithLlm(repairRequest);
            Dictionary<string, object> repairedParsed = TryParseJsonObject(ReadString(repaired, "content", ""));
            List<Dictionary<string, object>> remaining = repairedParsed == null
                ? violations : FindDialogueIntegrityViolations(repairedParsed, payload, priorLines, heroId);
            bool usable = ReadBool(repaired, "ok", false) && repairedParsed != null
                && StructuredResponseIsComplete(ReadString(repaired, "content", ""), auditMode);
            bool cleared = usable && remaining.Count == 0;
            if (usable)
            {
                MarkRepairedVisibleResponse(repairedParsed, cleared);
                repaired["content"] = Json.Serialize(repairedParsed);
            }
            else
            {
                repaired["ok"] = false;
                repaired["errorCode"] = "dialogue_integrity_repair_unusable";
                repaired["error"] = "The integrity repair did not return usable structured dialogue; no canned fallback was substituted.";
            }
            WriteAudit(campaignId, correlationId, "server", auditMode, "llm.dialogue_integrity_repair",
                heroId, "", eventId, usable ? (cleared ? "completed" : "completed_with_revalidation_override") : "failed",
                ReadLong(repaired, "durationMs", 0), usable ? "Dialogue agency and role identity were repaired from the original response." : "Dialogue integrity repair failed without a fallback.",
                new Dictionary<string, object> { ["detected"] = violations, ["remaining"] = remaining, ["accepted"] = usable, ["revalidationCleared"] = cleared, ["deterministicFallback"] = false });
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

            List<Dictionary<string, object>> authored = (priorLines ?? new List<Dictionary<string, object>>())
                .Where(x => ReadFirstString(x, "speakerId", "speakerHeroStringId", "heroStringId")
                    .Equals(speakerId ?? "", StringComparison.OrdinalIgnoreCase))
                .Where(x => Regex.IsMatch(ReadFirstString(x, "text", "reply", "content"),
                    @"\b(?:ask|asked|request|requested|invite|invited|want|wanted|would\s+like|take\s+me|bring\s+me|show\s+me)\b", RegexOptions.IgnoreCase))
                .ToList();
            foreach (string text in texts.Where(x => Regex.IsMatch(x ?? "", @"\byou\s+(?:asked|requested|wanted|invited|wished)\b", RegexOptions.IgnoreCase)))
            {
                foreach (Dictionary<string, object> line in authored)
                {
                    string earlier = ReadFirstString(line, "text", "reply", "content");
                    List<string> shared = SignificantDialogueWords(text).Intersect(SignificantDialogueWords(earlier), StringComparer.OrdinalIgnoreCase).ToList();
                    if (shared.Count < 1) continue;
                    result.Add(new Dictionary<string, object> { ["type"] = "request_agency_inverted", ["currentText"] = LimitText(text, 360), ["originatingNpcLine"] = LimitText(earlier, 360), ["sharedTopicWords"] = shared });
                    break;
                }
            }
            return result;
        }

        private static List<string> SignificantDialogueWords(string text)
        {
            HashSet<string> stop = new HashSet<string>(new[] { "that", "this", "with", "from", "your", "have", "were", "what", "when", "then", "there", "would", "could", "should", "asked", "wanted", "requested", "invited" }, StringComparer.OrdinalIgnoreCase);
            return Regex.Matches(text ?? "", @"[\p{L}]{4,}").Cast<Match>().Select(x => x.Value.ToLowerInvariant())
                .Where(x => !stop.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
            return rows;
        }
    }
}
