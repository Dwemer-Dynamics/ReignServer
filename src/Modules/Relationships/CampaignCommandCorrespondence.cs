using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> QueueAcceptedCampaignOrderLetterReply(
            string campaignId,
            Dictionary<string, object> storedPayload,
            Dictionary<string, object> receivedLetter,
            Dictionary<string, object> npcReply,
            string playerId,
            string commanderId)
        {
            Dictionary<string, object> order = ReadDictionary(npcReply, "campaignOrder");
            if (order == null || !string.Equals(ReadString(order, "decision", "none"), "accept",
                    StringComparison.OrdinalIgnoreCase))
                return new List<Dictionary<string, object>>();

            string request = ReadString(receivedLetter, "body", "");
            List<Dictionary<string, object>> steps = ReadDictionaryList(order, "steps");
            List<string> errors = new List<string>();
            if (steps.Count == 0)
                errors.Add("The letter did not produce an ordered campaign plan.");
            if (steps.Count > 20)
                errors.Add("A campaign plan may contain at most 20 steps.");
            bool armyExplicit = ContainsAnyNormalized(request, "army", "call the lords",
                "gather parties", "multiple commanders");
            bool vagueForces = ContainsAnyNormalized(request, "raise forces", "gather forces", "recruit forces")
                && !armyExplicit && !ContainsAnyNormalized(request, "party", "warband", "company", "men", "troops");
            if (vagueForces)
            {
                // Written orders cannot sustain a synchronous clarification dialogue.
                // The bounded correspondence default is the recipient's personal party.
                order["forceKindDefault"] = "party";
            }

            Dictionary<string, object> index = ReadDictionary(storedPayload, "actionResolutionIndex")
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> normalizedSteps = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> rawStep in steps.Take(20))
            {
                Dictionary<string, object> step = CampaignCommandCloneDictionary(rawStep);
                string objective = CanonicalCampaignCommandObjective(ReadString(step, "objective", ""));
                step["objective"] = objective;
                if (string.IsNullOrWhiteSpace(objective))
                {
                    errors.Add("Every written plan step requires an objective.");
                    continue;
                }
                if (objective == "form_army" && !armyExplicit)
                {
                    errors.Add("An army cannot be inferred from vague written references to forces.");
                    continue;
                }
                ResolveCampaignCommandLetterStep(index, step, errors);
                if (objective == "recruit_resupply")
                {
                    int total = ReadInt(step, "minimumTroops", 0);
                    int roles = ReadInt(step, "minimumInfantry", 0) + ReadInt(step, "minimumArchers", 0)
                        + ReadInt(step, "minimumCavalry", 0);
                    if (total <= 0 && roles <= 0)
                    {
                        step["useNativePartyCapacityDefault"] = true;
                        step["minimumFoodDays"] = Math.Max(3d, ReadDouble(step, "minimumFoodDays", 3d));
                    }
                }
                if (CampaignCommandObjectiveNeedsSettlement(objective)
                    && string.IsNullOrWhiteSpace(ReadString(step, "targetSettlementStringId", ""))
                    && string.IsNullOrWhiteSpace(ReadString(step, "region", "")))
                    errors.Add("The written " + objective.Replace('_', ' ') + " step lacks a resolvable settlement or region.");
                if (CampaignCommandObjectiveNeedsParty(objective)
                    && string.IsNullOrWhiteSpace(ReadFirstString(step, "targetPartyId", "targetHeroStringId")))
                    errors.Add("The written " + objective.Replace('_', ' ') + " step lacks a resolvable party or commander.");
                if ((objective == "timed_hold" || objective == "scout_report")
                    && ReadDouble(step, "durationHours", 0d) <= 0d)
                    errors.Add("The written " + objective.Replace('_', ' ') + " step lacks a duration.");
                normalizedSteps.Add(step);
            }

            errors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (errors.Count > 0)
            {
                npcReply["body"] = "I cannot safely execute that written order yet. Please send a new letter clarifying: "
                    + string.Join("; ", errors) + ". No campaign action has begun.";
                npcReply["campaignOrderQueued"] = false;
                npcReply["campaignOrderErrors"] = errors;
                return new List<Dictionary<string, object>>();
            }

            Dictionary<string, object> terms = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["steps"] = normalizedSteps,
                ["issuerHeroStringId"] = playerId,
                ["correspondenceDefaulted"] = true,
                ["sourceLetterId"] = ReadString(receivedLetter, "letter_id", "")
            };
            string planHash = CampaignCommandCorrespondencePlanHash(terms);
            terms["planHash"] = planHash;
            Dictionary<string, object> candidate = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["command"] = "issue_campaign_order",
                ["source"] = "correspondence_campaign_command_acceptance",
                ["requiresAcceptance"] = false,
                ["accepted"] = true,
                ["actorHeroId"] = commanderId,
                ["TargetHero"] = commanderId,
                ["reason"] = "The commander accepted a sovereign's written campaign order; bounded correspondence defaults were applied without inventing targets.",
                ["terms"] = terms
            };
            List<Dictionary<string, object>> queued = EvaluateAndQueueActionCandidates(campaignId,
                storedPayload, request + "\n" + ReadString(npcReply, "body", ""),
                new List<Dictionary<string, object>> { candidate },
                out List<string> queueErrors, out List<Dictionary<string, object>> rejected);
            if (queued.Count == 0)
            {
                npcReply["body"] = "I understood the written sequence, but it failed deterministic validation: "
                    + string.Join("; ", queueErrors.DefaultIfEmpty("the order could not be resolved"))
                    + ". No campaign action has begun.";
                npcReply["campaignOrderQueued"] = false;
                npcReply["campaignOrderErrors"] = queueErrors;
                return queued;
            }

            npcReply["body"] = BuildCampaignCommandCorrespondenceAcknowledgement(
                ReadString(npcReply, "body", ""), normalizedSteps, planHash);
            npcReply["campaignOrderQueued"] = true;
            npcReply["campaignOrderPlanHash"] = planHash;
            return queued;
        }

        private static void ResolveCampaignCommandLetterStep(Dictionary<string, object> index,
            Dictionary<string, object> step, List<string> errors)
        {
            if (index == null || index.Count == 0) return;
            List<Dictionary<string, object>> trace = new List<Dictionary<string, object>>();
            string settlementQuery = ReadFirstString(step, "targetSettlementStringId", "targetSettlementId",
                "TargetSettlement", "targetSettlement", "settlementName");
            if (!string.IsNullOrWhiteSpace(settlementQuery))
            {
                string settlement = ResolveIndexedEntity(index, "settlements", "settlement", settlementQuery,
                    "settlementId", new[] { "name" }, "campaign plan settlement", errors, true, trace);
                if (!string.IsNullOrWhiteSpace(settlement)) step["targetSettlementStringId"] = settlement;
            }
            string partyQuery = ReadFirstString(step, "targetPartyId", "TargetParty", "targetParty");
            if (!string.IsNullOrWhiteSpace(partyQuery))
            {
                string party = ResolveIndexedEntity(index, "parties", "party", partyQuery, "partyId",
                    new[] { "name", "leaderName" }, "campaign plan party", errors, true, trace);
                if (!string.IsNullOrWhiteSpace(party)) step["targetPartyId"] = party;
            }
            string heroQuery = ReadFirstString(step, "targetHeroStringId", "targetHeroId", "TargetHero");
            if (!string.IsNullOrWhiteSpace(heroQuery))
            {
                string hero = ResolveIndexedEntity(index, "heroes", "hero", heroQuery, "heroStringId",
                    new[] { "name" }, "campaign plan commander", errors, true, trace);
                if (!string.IsNullOrWhiteSpace(hero)) step["targetHeroStringId"] = hero;
            }
        }

        private static string BuildCampaignCommandCorrespondenceAcknowledgement(string prose,
            List<Dictionary<string, object>> steps, string planHash)
        {
            List<string> readback = new List<string>();
            for (int i = 0; i < steps.Count; i++)
            {
                Dictionary<string, object> step = steps[i];
                string objective = ReadString(step, "objective", "").Replace('_', ' ');
                string target = FirstNonEmpty(ReadFirstString(step, "targetSettlementStringId", "region"),
                    ReadFirstString(step, "targetPartyId", "targetHeroStringId"));
                readback.Add((i + 1) + ". " + objective
                    + (string.IsNullOrWhiteSpace(target) ? "" : " — " + target));
            }
            return (prose ?? string.Empty).Trim() + "\n\nI acknowledge and begin this ordered plan:\n"
                + string.Join("\n", readback) + "\nPlan reference: " + planHash.Substring(0, 12) + ".";
        }

        private static string CampaignCommandCorrespondencePlanHash(Dictionary<string, object> terms)
        {
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Serialize(terms)))
                    .Select(x => x.ToString("x2")));
        }
    }
}
