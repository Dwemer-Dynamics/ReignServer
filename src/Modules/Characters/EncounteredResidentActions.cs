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
            if (Regex.IsMatch(text, @"\b(refuse|decline|not ready|changed my mind|let me think)\b")
                || Regex.IsMatch(text, @"\bi (?:will not|shall not|do not|don't|can't|cannot|won't)\s+(?:(?:ever|now|yet|freely)\s+)*(?:join|travel|ride|serve|come|accompany|leave|accept|agree|consent|become|commit)\b")
                || Regex.IsMatch(text, @"\bi (?:will not|won't|can't|cannot)\s*(?:[.!?;]|$)")) return false;
            // Conditions attach to the decision clause. Optional arrangements for someone else do not veto consent.
            var clauses = Regex.Split(text, @"[.!?;\r\n]+");
            bool accepted = false;
            foreach (string clause in clauses)
            {
                bool decision = ContainsAnyPhrase(clause, DialogueActionAcceptancePhrases())
                    || Regex.IsMatch(clause, @"\bi(?: (?:will|shall|agree to)|'ll)? (?:join|travel|ride|serve|come|accompany|become)\b")
                    || clause.Contains("i'm joining you") || clause.Contains("i am joining you");
                bool blocked = Regex.IsMatch(clause, @"\b(if|unless|perhaps|maybe|might|would|could|hypothetically|provided|assuming|tomorrow|later|after|once|first)\b");
                if ((decision || Regex.IsMatch(clause, @"\bi (?:could|might|would) (?:join|travel|serve|come)\b")) && blocked) return false;
                if (Regex.IsMatch(clause, @"\b(?:but only|on condition|provided that|before i (?:join|leave)|not until)\b")) return false;
                if (Regex.IsMatch(clause, @"\b(?:if|unless|once|after|before)\s+(?:you (?!wish\b|want\b|like\b)|i (?:am|have|get)|(?:my|the) (?:payment|release|permission))")) return false;
                accepted |= decision;
            }
            return accepted;
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
            if (resident && command == "join_clan")
            { errors.Add("A resident must use recruit_encountered_resident to become a native companion and party member."); return; }
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
            if (recruit)
            {
                string turn = FirstNonEmpty(ReadFirstString(payload, "sceneTurnId", "turnId", "requestId"), EnsureCorrelationId(payload));
                terms["paymentDependencies"] = CurrentPlayerTransfers(payload).Select((i, slot) => new { i, slot })
                    .Where(x => x.i.Error.Length == 0 && x.i.Command == "transfer_gold" && x.i.Purpose == "recruitment_advance")
                    .Select(x => ReignBeta.Shared.Dialogue.PlayerActionRules.ActionId(ReadString(payload, "campaignId", "default"), ContinuityTimeline(payload), turn, x.slot)).ToList();
            }
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
                    var agency = ReadDictionary(payload, "conversationAgencyReceipt");
                    bool exactAgreement = ReadBool(agency, "ok", false) && ReadDictionaryList(agency, "updates")
                        .Any(r => ReadString(r, "topic", "") == "allegiance" && AgencyAccepted(ReadString(r, "disposition", ""))
                            && ReadDouble(ReadDictionary(r, "terms"), "gold", -1) == gold);
                    if ((gold > 0 && !prices.Contains(gold) && !(prices.Count == 0 && exactAgreement)) || (gold == 0 && prices.Any(n => n > 0)))
                        errors.Add("The recruitment payment does not match the visible accepted price; confirm the exact price first.");
                }
            }
        }

        private static Dictionary<string, object> QueueExplicitResidentCompanion(Dictionary<string, object> payload, Dictionary<string, object> response)
        {
            var hero = ReadDictionary(payload, "hero") ?? ReadDictionary(payload, "speaker");
            var receipt = ReadDictionary(payload, "conversationAgencyReceipt");
            string reply = ReadString(response, "reply", "");
            if (!ReadBool(response, "ok", false) || !IsEncounteredResidentProfile(hero) || !ReadBool(receipt, "ok", false) || ReadBool(receipt, "replayed", false)
                || !ResidentHasUnconditionalConsent(reply) || !Regex.IsMatch(reply, @"\b(?:companion|retainer|join your clan)\b", RegexOptions.IgnoreCase)) return null;
            if ((_stagedTurnActions ?? new List<Dictionary<string, object>>()).Any(r => ReadString(r, "command", "") == "recruit_encountered_resident")) return null;
            var decisions = ReadDictionaryList(receipt, "updates").Where(r => ReadString(r, "topic", "") == "allegiance"
                && AgencyAccepted(ReadString(r, "disposition", ""))).ToList();
            if (decisions.Count != 1) return null;
            double total = ReadDouble(ReadDictionary(decisions[0], "terms"), "gold", -1);
            if (double.IsNaN(total) || double.IsInfinity(total) || total < 0 || total > int.MaxValue || total != Math.Truncate(total)) return null;
            string campaign = ReadString(payload, "campaignId", "default");
            var candidate = TestDict("command", "recruit_encountered_resident", "source", "dialogue_resident_companion",
                "FromHero", FirstNonEmpty(ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"), CharacterIdFrom(hero)),
                "requiresAcceptance", false, "reason", "The resident explicitly accepted permanent companionship under the verified agreement.",
                "terms", TestDict("agreementKind", "permanent", "agreedGold", (int)total));
            var bound = BindResidentActionProfile(payload, hero);
            var normalized = NormalizeActionCommand(candidate, campaign, out var errors, bound,
                BuildRouterResolutionText(ReadFirstString(payload, "playerText", "text", "message"), reply));
            if (normalized == null || errors.Count > 0)
            {
                response["reply"] = reply + "\n\n[Recruitment not queued: " + string.Join(" ", errors) + "]";
                return null;
            }
            return QueueAction(campaign, normalized);
        }

        private static string ResidentRecruitmentCandidate(Dictionary<string, object> hero, string text)
        {
            if (!IsEncounteredResidentProfile(hero)) return "";
            if (Regex.IsMatch(text ?? "", @"(?i)\b(companion|recruit|permanent|retainer)\b")) return "recruit_encountered_resident";
            return "";
        }
    }
}
