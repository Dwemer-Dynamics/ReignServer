using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> SocialEventApproachRollApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
            string eventId = ReadString(payload, "eventId", "");
            if (string.IsNullOrWhiteSpace(eventId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "A social event id is required." };
            }

            List<Dictionary<string, object>> candidates = ReadDictionaryList(payload, "approachCandidates")
                .Where(x => !string.IsNullOrWhiteSpace(ReadFirstString(x, "heroStringId", "heroId")))
                .GroupBy(x => ReadFirstString(x, "heroStringId", "heroId"), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();

            List<Dictionary<string, object>> passed = new List<Dictionary<string, object>>();
            for (int index = 0; index < candidates.Count; index++)
            {
                Dictionary<string, object> hero = candidates[index];
                string heroId = ReadFirstString(hero, "heroStringId", "heroId");
                Dictionary<string, object> traits = ReadJsonObject(CharacterFile(campaignId, heroId, "traits.json"));
                if (!TraitPercentageDocumentReady(traits))
                {
                    // Approaches must never trigger LLM construction. A deterministic
                    // native-fact profile supplies the same percentage model until a
                    // richer character profile is available.
                    traits = BuildTraitDocument(hero);
                }
                else
                {
                    if (EnsureTraitPercentageData(traits, heroId))
                        WriteJsonObject(CharacterFile(campaignId, heroId, "traits.json"), traits);
                }

                Dictionary<string, object> virtues = ReadDictionary(traits, "courtVirtues") ?? new Dictionary<string, object>();
                int boldness = Math.Max(0, Math.Min(100, ReadInt(virtues, "boldness", 50)));
                int playerRelation = Math.Max(-100, Math.Min(100, ReadInt(hero, "relationToPlayer", 0)));
                Dictionary<string, object> approachScore = SocialEventApproachScore(boldness, playerRelation);
                int adjustedBoldness = ReadInt(approachScore, "adjustedBoldness", boldness);
                int threshold = SocialEventApproachThreshold(payload, hero);
                Dictionary<string, object> roll = EvaluateSocialEventApproach(adjustedBoldness, null, threshold);
                if (ReadBool(roll, "passed", false))
                {
                    passed.Add(new Dictionary<string, object>
                    {
                        ["heroStringId"] = heroId,
                        ["candidateOrder"] = index,
                        ["approachScore"] = adjustedBoldness,
                        ["baseBoldness"] = boldness,
                        ["playerRelation"] = playerRelation,
                        ["relationshipModifier"] = ReadInt(approachScore, "relationshipModifier", 0),
                        ["threshold"] = threshold,
                        ["chance"] = ReadInt(roll, "chance", 0),
                        ["roll"] = ReadInt(roll, "roll", 0)
                    });
                }
            }

            int approachQuota = RollSocialEventD4(null);
            int remainingSlots = Math.Max(0, Math.Min(4, ReadInt(payload, "remainingActiveSlots", 4)));
            List<string> selected = remainingSlots == 0
                ? new List<string>()
                : SelectSocialEventApproachIds(passed, Math.Min(approachQuota, remainingSlots));
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["eventId"] = eventId,
                ["candidateCount"] = candidates.Count,
                ["passedCount"] = passed.Count,
                ["approachQuota"] = approachQuota,
                ["approachHeroStringIds"] = selected,
                ["passedCandidates"] = passed,
                ["relationshipRule"] = "adjustedBoldness = clamp(baseBoldness + round(relationToPlayer * 0.30), 0, 100)"
            };
            WriteAudit(campaignId, EnsureCorrelationId(payload), "server", "social_event", "social_event.approach_roll", "", "", eventId, "completed", 0, "Social-event approaches resolved from boldness plus the signed player-relationship modifier and a d4 approach quota.", result);
            return result;
        }

        private static Dictionary<string, object> SocialEventApproachScore(int boldnessPercentage, int relationToPlayer)
        {
            int boldness = Math.Max(0, Math.Min(100, boldnessPercentage));
            int relation = Math.Max(-100, Math.Min(100, relationToPlayer));
            int modifier = RoundAwayFromZero(relation * 0.30d);
            return new Dictionary<string, object>
            {
                ["baseBoldness"] = boldness,
                ["playerRelation"] = relation,
                ["relationshipModifier"] = modifier,
                ["adjustedBoldness"] = Math.Max(0, Math.Min(100, boldness + modifier))
            };
        }

        private static List<string> SelectSocialEventApproachIds(IEnumerable<Dictionary<string, object>> passedCandidates, int approachQuota)
        {
            int quota = Math.Max(1, Math.Min(4, approachQuota));
            return (passedCandidates ?? Enumerable.Empty<Dictionary<string, object>>())
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "heroStringId", "")))
                .OrderByDescending(x => ReadInt(x, "approachScore", 0))
                .ThenBy(x => ReadInt(x, "candidateOrder", int.MaxValue))
                .ThenBy(x => ReadString(x, "heroStringId", ""), StringComparer.OrdinalIgnoreCase)
                .Take(quota)
                .Select(x => ReadString(x, "heroStringId", ""))
                .ToList();
        }

        private static Dictionary<string, object> EvaluateSocialEventApproach(int boldnessPercentage, int? forcedRoll, int thresholdOffset = 60)
        {
            int boldness = Math.Max(0, Math.Min(100, boldnessPercentage));
            int threshold = Math.Max(0, Math.Min(100, thresholdOffset));
            int chance = boldness - threshold;
            if (chance <= 0)
            {
                return new Dictionary<string, object>
                {
                    ["boldness"] = boldness,
                    ["threshold"] = threshold,
                    ["chance"] = chance,
                    ["roll"] = 0,
                    ["passed"] = false
                };
            }

            int roll = Math.Max(1, Math.Min(100, forcedRoll ?? RollCourtD100()));
            return new Dictionary<string, object>
            {
                ["boldness"] = boldness,
                ["threshold"] = threshold,
                ["chance"] = chance,
                ["roll"] = roll,
                ["passed"] = roll < chance
            };
        }

        private static Dictionary<string, object> EvaluateSocialEventJoin(int boldnessPercentage, int thresholdOffset, int? forcedFirstRoll, int? forcedSecondRoll)
        {
            Dictionary<string, object> first = EvaluateSocialEventApproach(boldnessPercentage, forcedFirstRoll, thresholdOffset);
            Dictionary<string, object> second = ReadBool(first, "passed", false)
                ? EvaluateSocialEventApproach(boldnessPercentage, forcedSecondRoll, thresholdOffset)
                : new Dictionary<string, object> { ["passed"] = false, ["roll"] = 0, ["chance"] = ReadInt(first, "chance", 0), ["threshold"] = thresholdOffset, ["boldness"] = boldnessPercentage };
            return new Dictionary<string, object>
            {
                ["passed"] = ReadBool(first, "passed", false) && ReadBool(second, "passed", false),
                ["first"] = first,
                ["second"] = second,
                ["boldness"] = Math.Max(0, Math.Min(100, boldnessPercentage)),
                ["threshold"] = Math.Max(0, Math.Min(100, thresholdOffset))
            };
        }

        private static int RollSocialEventD4(int? forcedRoll)
        {
            if (forcedRoll.HasValue)
            {
                return Math.Max(1, Math.Min(4, forcedRoll.Value));
            }

            return 1 + ((RollCourtD100() - 1) % 4);
        }
    }
}
