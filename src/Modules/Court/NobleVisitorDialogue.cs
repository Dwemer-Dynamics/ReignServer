using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Shared by the docket hearing and subsequent ordinary social conversations.
        private static string BuildNobleVisitorPrompt(string campaignId, string heroId, Dictionary<string, object> context)
        {
            if (context == null || !ReadBool(context, "enabled", false)) return string.Empty;
            List<Dictionary<string, object>> members = ReadDictionaryList(context, "members");
            Dictionary<string, object> member = members.FirstOrDefault(x => ReadString(x, "heroId", "") == heroId);
            if (member == null || ReadDouble(member, "age", 0) < 18) return string.Empty;
            string role = ReadString(member, "role", "courtesy_visitor");
            bool married = ReadBool(context, "rulerMarried", false);
            bool matchmaking = ReadBool(context, "isMatchmaking", false);
            bool romantic = ReadBool(context, "romanceEligibleNow", false)
                && (matchmaking || ReadBool(context, "isSoloRomanticApproach", false));
            List<Dictionary<string, object>> initiators = matchmaking ? members.Where(x => ReadString(x, "role", "") == "parent").ToList()
                : members.Where(x => ReadString(x, "heroId", "") == ReadString(context, "initiatorHeroId", "")).ToList();
            var permittedInitiators = new List<string>();
            foreach (Dictionary<string, object> initiator in initiators)
            {
                string id = ReadString(initiator, "heroId", "");
                Dictionary<string, object> document = ReadJsonObject(CharacterFile(campaignId, id, "traits.json"));
                Dictionary<string, object> virtues = ReadDictionary(document, "courtVirtues");
                Dictionary<string, object> percentages = ReadDictionary(document, "traitPercentages");
                if (virtues == null && percentages != null) virtues = CalculateCourtVirtues(percentages);
                int boldness = Math.Max(0, Math.Min(100, ReadInt(virtues, "boldness", 50)));
                int honor = Math.Max(0, Math.Min(100, ReadInt(virtues, "honor", 50)));
                if (ReignNobleVisitorRules.CanInitiatePrivateRomance(married, boldness, honor)) permittedInitiators.Add(id);
            }
            bool privateInitiative = romantic && permittedInitiators.Count > 0;
            var prompt = new StringBuilder();
            prompt.AppendLine("CURRENT NOBLE VISIT: You are a real visiting noble, with your ordinary personality, knowledge, relationships, and agency intact.");
            prompt.AppendLine("Visit purpose: " + LimitText(ReadString(context, "purpose", ""), 600));
            prompt.AppendLine("During introductions, naturally state the planned stay of " + ReadInt(context, "stayDays", 3) + " days. The stay is temporary; later conversation remains possible while you are resident.");
            prompt.AppendLine("Your household role: " + role + ". Present household: " + Json.Serialize(members));
            if (romantic && matchmaking)
            {
                prompt.AppendLine("The household shares the hope that the adult candidate " + ReadString(context, "candidateHeroId", "")
                    + " may catch the ruler's interest. Parents may make introductions, mention true accomplishments and interests, and allow the candidate space to answer. Siblings may support the introduction naturally. "
                    + "Each adult still has a separate personality and can hesitate, disagree, or refuse; do not make everyone repeat the same pitch.");
            }
            else if (romantic) prompt.AppendLine("You may have a personal romantic interest in this ruler. Express it only as your actual personality and the ruler's response warrant; interest is not consent.");
            else prompt.AppendLine("This is a courtesy or personal-business visit. Do not invent a romantic motive, a political grievance, an oath of vassalage, or a demand for the ruler's intervention.");
            if (!privateInitiative)
                prompt.AppendLine("Do not initiate or engineer a romantic private meeting. A respectful introduction and ordinary conversation are appropriate. If the ruler independently makes an invitation, respond according to your own boundaries rather than assuming acceptance.");
            else
                prompt.AppendLine("A discreet romantic private invitation is personality-eligible for these initiating adults: " + string.Join(", ", permittedInitiators)
                    + ". This permits an attempt, never guarantees one. Other household members may make appropriate conversational space; the candidate decides for themselves. A married ruler's spouse and witnessed attention can matter through known relationships and rumors.");
            prompt.AppendLine("Accepted invitation memory: " + Json.Serialize(ReadDictionaryList(context, "agreedInvitations")));
            prompt.AppendLine("An invitation refers to a later conversation only. Do not transition the scene, narrate a private meeting as completed, decide for the player, assume romantic consent, or fabricate an affair. State mechanical thresholds and private motives nowhere in visible speech.");
            return prompt.ToString();
        }
    }
}
