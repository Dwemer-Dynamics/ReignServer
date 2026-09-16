using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] PromptWealthKeys = {
            "gold", "goldSemantics", "personalWealthTier", "clanGold", "clanWealthTier",
            "clanTier", "clanRenown", "clanFiefCount", "clanSocialCredit", "canLeanOnClanReputation",
            "partyInventoryValue", "partyInventoryState", "wealthEvidenceBasis", "interpretation"
        };

        private static Dictionary<string, object> PromptWealthFacts(Dictionary<string, object> wealth)
        {
            var facts = new Dictionary<string, object>(wealth ?? new Dictionary<string, object>());
            // v1/v2 visibleWealthTier was derived from cargo, with absent cargo
            // coerced to zero. It is not evidence of appearance in any saved version.
            facts.Remove("visibleWealthTier");
            if (ReadInt(facts, "version", 0) < 3)
            {
                facts.Remove("personalWealthTier");
                if (facts.TryGetValue("gold", out object gold) && gold != null && ReadBool(facts, "economicCapacityKnown", true)
                    && ReadString(facts, "dataState", "") != "unknown")
                    facts["personalWealthTier"] = WealthTier(ReadInt(facts, "gold", 0));
                facts["partyInventoryState"] = "unknown_legacy_observation";
                // Historical nonzero cargo remains a historical fact. An old zero
                // cannot distinguish no party, observation failure or an empty roster.
                if (ReadInt(facts, "partyInventoryValue", 0) == 0) facts.Remove("partyInventoryValue");
            }
            if (!ReadBool(facts, "economicCapacityKnown", true))
            {
                facts.Remove("gold");
                facts.Remove("personalWealthTier");
                facts.Remove("clanGold");
                facts.Remove("clanWealthTier");
            }
            facts["interpretation"] = "Personal funds, clan backing, party cargo and visible dress are separate. Use supplied appearance for dress; rich clothing does not prove available cash, and low cash or absent cargo does not prove poor dress.";
            return facts;
        }
    }
}
