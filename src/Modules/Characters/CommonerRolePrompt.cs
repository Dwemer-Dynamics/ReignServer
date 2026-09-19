using System;
using System.Collections.Generic;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool UsesCommonerRolePrompt(Dictionary<string, object> profile)
            => IsEncounteredResidentProfile(profile) && !ReadBool(profile, "isLord", false)
                && !ReadBool(profile, "isNotable", false);

        private static string BuildCommonerPromptBlock(Dictionary<string, object> profile)
        {
            if (!UsesCommonerRolePrompt(profile)) return string.Empty;
            var resident = ReadDictionary(profile, "encounteredResident");
            string role = ReadString(resident, "occupation", "resident");
            var builder = new StringBuilder();
            AppendPromptSection(builder, "COMMONER ROLE - CONTINUING CHARACTER LAYER", LoadPromptTemplate("commoner_prompt.txt"));
            AppendPromptLine(builder, "Native livelihood", role);
            AppendPromptLine(builder, "Home", ReadString(resident, "homeSettlementName", ""));
            builder.AppendLine(BuildCommonerHouseholdPrompt(profile));
            if (ReadBool(resident, "recruited", false))
                builder.AppendLine("This person has been recruited. The livelihood above is their background, not a current shift or a reason to deny their supplied party role. Preserve their individual identity and follow the current native duties and affiliations.");
            if (ReadBool(resident, "military", false))
                builder.AppendLine(ReadBool(resident, "recruited", false) || ReadBool(resident, "releasedFromDuty", false)
                    ? "This soldier has been released from the former duty. Do not invent an ongoing order that prevents departure; use current native obligations."
                    : "This guard or soldier has an active duty. Orders, discipline, comrades, readiness and consequences of desertion may matter according to their personality. They cannot grant their own release or speak as their commander. Only supplied orders and successful native release receipts establish a change of duty.");
            else if (ContainsAny(role, "tavern", "innkeeper", "barmaid"))
                builder.AppendLine("Tavern work can build practical knowledge of service, customers, drink, lodging and discretion. The keeper may weigh business risk; a server may weigh wages, workload and difficult guests. Neither knows every visitor's secrets. Choose hospitality, humor, reserve or irritation from this person's traits and present exchange.");
            else if (ContainsAny(role, "villag", "peasant", "farmer"))
                builder.AppendLine("Village life can give knowledge of land, seasons, livestock, tools and neighbors, with concerns about labor, dues and protection. Use the actual livelihood and learned history; do not turn all villagers into naive farmers or invent a failed harvest.");
            else if (ContainsAny(role, "smith", "artisan", "shipwright", "craft"))
                builder.AppendLine("Craft work can make materials, skilled effort, reliable tools and pride in workmanship matter. Let actual practiced skills guide what this person can explain or offer, without granting knowledge of every trade or identical professional pride.");
            else if (ContainsAny(role, "merchant", "trader", "shop"))
                builder.AppendLine("Trade can make supply, credit, reputation, customers and risk salient. Skill and personality decide their bargaining style. Do not invent stock, prices or secret routes, and do not assume every trader is greedy or wealthy.");
            else if (ContainsAny(role, "musician", "dancer", "performer", "barber"))
                builder.AppendLine("Their work brings audiences or clients, practiced social observation and a livelihood dependent on skill and reputation. Confidence, wit, discretion and ambition vary with this individual; exposure to conversation does not grant omniscient gossip.");
            else
                builder.AppendLine("Let this resident's specific saved livelihood and experiences shape ordinary concerns such as work, shelter, local reputation and opportunity. Do not invent a profession, menial task or hardship merely from the label townsfolk.");
            return builder.ToString().Trim();
        }
    }
}
