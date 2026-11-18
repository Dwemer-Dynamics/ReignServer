using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class CourtService
    {
        internal static bool CastleOpeningReplyImpliesPlayerSpeech(string reply)
        {
            string value = (reply ?? string.Empty).Trim();
            if (value.Length == 0) return false;

            string[] patterns =
            {
                @"\b(?:Your\s+Grace|Your\s+Majesty|Sire|My\s+Lord|My\s+Lady|Regent|King|Queen)\b[^.!?\r\n]{0,80}\bI\s+(?:can\s+)?hear\s+you\b",
                @"\b(?:Your\s+Grace|Your\s+Majesty|Sire|My\s+Lord|My\s+Lady|Regent|King|Queen)\b[^.!?\r\n]{0,80}\b(?:as\s+you|you)\s+(?:say|said|ask|asked|request|requested|order|ordered|command|commanded)\b",
                @"\b(?:Your\s+Grace|Your\s+Majesty|Sire|My\s+Lord|My\s+Lady|Regent|King|Queen)\b[^.!?\r\n]{0,80}\byour\s+(?:words?|question|request|concern|proposal|command|order|petition)\b",
                @"\bthe\s+(?:ruler|regent|king|queen)'s\s+(?:words?|question|request|concern|proposal|command|order|petition)\b"
            };
            foreach (string pattern in patterns)
            {
                if (Regex.IsMatch(value, pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return true;
            }

            return false;
        }
    }
}
