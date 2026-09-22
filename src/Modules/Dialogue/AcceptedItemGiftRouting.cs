using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool ShouldPreserveAcceptedItemGift(Dictionary<string, object> gate, string playerText, string existingCommand)
        {
            if (!ReadBool(gate, "needed", false)
                || !string.Equals(ReadString(gate, "commitment", ""), "accepted", StringComparison.OrdinalIgnoreCase)) return false;
            // An item within a priced/compound exchange must stay inside its atomic package.
            if (ContainsAny(existingCommand ?? "", "trade_package", "ransom_package", "diplomatic_package")) return false;
            string intent = ReadString(gate, "intent", "");
            if (LooksLikeGiveGoldDirective(playerText + " " + intent)
                && !LooksLikeGiveItemDirective(playerText + " " + intent)) return false;
            bool acceptedObject = Regex.IsMatch(intent, @"\b(?:accepts?|receives?|takes?|keeps?)\b[^.!?\r\n]{0,220}\b(?:gift|token|present|prize|item|weapon|equipment|armor|armour|jewelry|jewel|ring|necklace|brooch)\b", RegexOptions.IgnoreCase);
            bool physicalOffer = Regex.IsMatch(playerText ?? "", @"\b(?:here (?:is|are)|would you like|(?:give|giving|offer|offering|hand|handing|take|keep) (?:you|her|him|them|this|that|it|the|my))\b", RegexOptions.IgnoreCase);
            return acceptedObject && physicalOffer;
        }
    }
}
