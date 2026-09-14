using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool ResidentHasUnconditionalConsent(string reply)
        {
            string text = (reply ?? "").ToLowerInvariant().Replace('’', '\'');
            if (text.Length == 0 || text.Length > 6000) return false;
            // Stage directions and unrelated self-description are not a decision about joining.
            text = Regex.Replace(text, @"\*[^*]*\*", " ");
            if (DialogueActionBlockingPhrases().Where(p => p != "i will not" && p != "i won't" && p != "prove")
                    .Any(p => Regex.IsMatch(text, @"\b" + Regex.Escape(p) + @"\b"))
                || Regex.IsMatch(text, @"\b(if|unless|perhaps|maybe|might|would|could|hypothetically|provided|assuming)\b")
                || Regex.IsMatch(text, @"\b(refuse|decline|not ready|changed my mind|let me think)\b")
                || Regex.IsMatch(text, @"\bi (?:will not|shall not|do not|don't|can't|cannot|won't)\s+(?:(?:ever|now|yet|freely)\s+)*(?:join|travel|ride|serve|come|accompany|leave|accept|agree|consent|become|commit)\b")
                || Regex.IsMatch(text, @"\bi (?:will not|won't|can't|cannot)\s*(?:[.!?;]|$)")) return false;
            return ContainsAnyPhrase(text, DialogueActionAcceptancePhrases())
                || Regex.IsMatch(text, @"\bi(?: (?:will|shall|agree to)|'ll)? (?:join|travel|ride|serve|come|accompany|become)\b")
                || text.Contains("i'm joining you") || text.Contains("i am joining you");
        }

        private static Dictionary<string, object> BindResidentActionProfile(Dictionary<string, object> payload,
            Dictionary<string, object> profile)
        {
            string speaker = ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId");
            if (!IsEncounteredResidentProfile(profile) || string.IsNullOrWhiteSpace(speaker)
                || speaker != ReadString(profile, "heroStringId", "")) return payload;
            // Event/Homes requests carry a speaker rather than the individual-chat hero object.
            // Carry only the already-resolved speaker profile into the shared action validator.
            var bound = new Dictionary<string, object>(payload, StringComparer.OrdinalIgnoreCase);
            bound["hero"] = profile;
            return bound;
        }

        private static string LatestResidentReply(string resolution)
        {
            // Earlier accepted offers in dialogue history cannot authorize the current refusal.
            string value = resolution ?? "";
            var matches = Regex.Matches(value, @"(?im)^npc:\s*");
            return matches.Count == 0 ? "" : value.Substring(matches[matches.Count - 1].Index + matches[matches.Count - 1].Length).Trim();
        }

        private static void BindAndValidateResidentAgreement(Dictionary<string, object> raw,
            Dictionary<string, object> terms, Dictionary<string, object> payload, string resolution,
            string command, List<string> errors)
        {
            var hero = ReadDictionary(payload, "hero");
            bool resident = IsEncounteredResidentProfile(hero);
            bool recruit = command == "recruit_encountered_resident";
            bool release = command == "release_resident_from_duty";
            bool guest = resident && (command == "accept_temporary_party_guest" || command == "renew_temporary_party_guest");
            if (!recruit && !release && !guest) return;
            string speaker = FirstNonEmpty(ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"), ReadString(hero, "heroStringId", ""));
            if (string.IsNullOrWhiteSpace(speaker)) errors.Add("Resident agreements require the current conversation participant.");
            raw["actorHeroStringId"] = speaker; raw["actorHeroId"] = speaker; raw["FromHero"] = speaker;
            string reply = LatestResidentReply(resolution);
            if (release)
            {
                if (resident) { raw["targetHeroStringId"] = speaker; raw["TargetHero"] = speaker; }
                string player = LatestPlayerDirectiveOrText(resolution.Substring(0, Math.Max(0, resolution.LastIndexOf("npc:", StringComparison.OrdinalIgnoreCase))));
                string grant = resident ? player : reply;
                if (!Regex.IsMatch(grant ?? "", @"(?i)\b(release|discharge|relieve)\b.*\b(duty|service|duties)\b")
                    || Regex.IsMatch(grant ?? "", @"(?i)\b(if|unless|might|would|could|not|won't|cannot)\b"))
                    errors.Add("Duty release requires an explicit present release by the authorized commander.");
                terms["releaseConfirmed"] = errors.Count == 0;
                terms["verifiedConsentQuote"] = LimitText(grant, 1200);
                return;
            }
            if (recruit && !resident) errors.Add("Permanent resident recruitment requires encountered-resident metadata.");
            if (!ResidentHasUnconditionalConsent(reply)) errors.Add("The current visible reply does not unconditionally agree to join.");
            terms["consentConfirmed"] = errors.Count == 0;
            terms["verifiedConsentQuote"] = LimitText(reply, 1200);
            if (recruit && ReadString(terms, "agreementKind", "") != "permanent")
                errors.Add("Permanent companion recruitment must explicitly identify agreementKind=permanent.");
            if (recruit || terms.ContainsKey("agreedGold"))
            {
                string amount = ReadString(terms, "agreedGold", "");
                if (!int.TryParse(amount, out int gold) || gold < 0) errors.Add("The exact nonnegative agreedGold amount is required, including zero.");
                else
                {
                    terms["agreedGold"] = gold;
                    // Price must be present in the accepted exchange, never invented by the planner.
                    string accepted = (LatestPlayerDirectiveOrText(resolution.Substring(0,
                        Math.Max(0, resolution.LastIndexOf("npc:", StringComparison.OrdinalIgnoreCase)))) + "\n" + reply).Replace(",", "");
                    var prices = Regex.Matches(accepted, @"(?i)\b(\d+)\s*(?:denars?|gold|coins?)\b")
                        .Cast<Match>().Select(m => int.TryParse(m.Groups[1].Value, out int n) ? n : -1).ToList();
                    if ((gold > 0 && !prices.Contains(gold)) || (gold == 0 && prices.Any(n => n > 0)))
                        errors.Add("The recruitment payment does not match the visible accepted price; confirm the exact price first.");
                }
            }
        }

        private static string ResidentRecruitmentCandidate(Dictionary<string, object> hero, string text)
        {
            if (!IsEncounteredResidentProfile(hero)) return "";
            if (Regex.IsMatch(text ?? "", @"(?i)\b(companion|recruit|permanent|retainer)\b")) return "recruit_encountered_resident";
            return "";
        }
    }
}
