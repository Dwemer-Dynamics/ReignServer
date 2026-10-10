#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBeta.Shared.Dialogue
{
    public sealed class PlayerTransferIntent
    {
        public string Quote { get; set; } = "";
        public int Offset { get; set; }
        public string Command { get; set; } = "";
        public int Amount { get; set; }
        public string Asset { get; set; } = "";
        public string Purpose { get; set; } = "gift";
        public string Error { get; set; } = "";
    }

    /// <summary>Conservative current-player action extraction. Never consumes NPC prose or history.</summary>
    public static class PlayerActionRules
    {
        public const string Schema = "reign-player-action-v1";
        public const string Policy = "player_owned_transfer_v1";
        public static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
        }
        public static string ActionId(string campaign, string timeline, string turn, int slot)
            => "pa_" + Hash(campaign + "|" + timeline + "|" + turn + "|" + slot).Substring(0, 48);

        public static bool IsNonActionContext(string input) => Regex.IsMatch(input ?? "",
            @"^\s*(?:```|~~~|>|['‘]|if\b|unless\b|provided\b|imagine\b|pretend\b|suppose\b|hypothetically\b|for example\b|what if\b|(?:do not|don't)\s+(?:do|execute|perform|transfer)\b)", RegexOptions.IgnoreCase);

        public static List<PlayerTransferIntent> Extract(string input, string recipientName, bool singleRecipient = true)
        {
            var result = new List<PlayerTransferIntent>();
            if (string.IsNullOrWhiteSpace(input) || input.Length > 16000 || IsNonActionContext(input)) return result;
            // Mask quoted statements, preserving offsets. Apostrophes in contractions are not quotes.
            string text = Regex.Replace(input, @"(?s:```.*?(?:```|\z)|~~~.*?(?:~~~|\z))|`[^`\r\n]*`|(?m:^[ \t]*>[^\r\n]*)", m => new string(' ', m.Length));
            text = Regex.Replace(text, "[\"“][^\"”]*[\"”]", m => new string(' ', m.Length));
            foreach (Match part in Regex.Matches(text, @"[^*.!?;\r\n]+"))
            {
                if (part.Index + part.Length < text.Length && text[part.Index + part.Length] == '?') continue;
                foreach (Match clause in Regex.Matches(part.Value, @"(?:^|\band\s+(?=I\s))(?:(?!\band\s+I\s).)+", RegexOptions.IgnoreCase))
                {
                    string raw = clause.Value.Trim();
                    string value = Regex.Replace(raw.Replace('’', '\''), @"^and\s+", "", RegexOptions.IgnoreCase);
                    // A current first-person handover must begin the clause. Past/future/quoted/negated
                    // claims and statements about another actor cannot authorize a transfer.
                    Match verb = Regex.Match(value, @"^(?:I\s+(?:give|hand(?:\s+over)?|pass|pay|send|transfer|gift|donate)|here\s*[, :]?\s*(?:take|accept))\s+(.+)$", RegexOptions.IgnoreCase);
                    if (!verb.Success) continue;
                    if (Regex.IsMatch(value, @"\b(?:if|unless|provided|assuming|would|could|might|pretend|imagine|tomorrow|later|yesterday|already|not|never|nothing|instead|in exchange|once you|when you|after you)\b|\bfor\s+(?:your|the)\s+(?:sword|horse|armor|armour|workshop|town|goods|items?)\b", RegexOptions.IgnoreCase)) continue;
                    string body = verb.Groups[1].Value.Trim();
                    // A social gesture is prose, even when it uses the verb "give" and names a person.
                    // Match the whole object so a tangible gift followed by a smile still reaches inventory.
                    if (Regex.IsMatch(body, @"^(?:(?i:you|her|him|them)\s+|\p{Lu}[\p{L}'-]*(?:\s+\p{Lu}[\p{L}'-]*)?\s+)?(?i:a|an|the|my)\s+(?:(?i:[\p{L}'-]+)\s+){0,3}(?i:smile|grin|nod|look|glance|sigh|laugh|chuckle|shrug|wink|hug|embrace|handshake|kiss|word)$")) continue;
                    string recipient = @"(?:" + (singleRecipient ? @"you|her|him|" : "")
                        + (string.IsNullOrWhiteSpace(recipientName) ? "(?!)" : Regex.Escape(recipientName)) + @")";
                    Match direct = Regex.Match(body, "^" + recipient + @"\s+(.+)$", RegexOptions.IgnoreCase);
                    Match indirect = Regex.Match(body, @"^(.+?)\s+to\s+" + recipient + @"(?:\s+(.+))?$", RegexOptions.IgnoreCase);
                    string asset = direct.Success ? direct.Groups[1].Value : indirect.Success
                        ? indirect.Groups[1].Value + " " + indirect.Groups[2].Value
                        : value.StartsWith("here", StringComparison.OrdinalIgnoreCase) && singleRecipient ? body : "";
                    var intent = new PlayerTransferIntent { Quote = input.Substring(part.Index + clause.Index, clause.Length).Trim(),
                        Offset = part.Index + clause.Index, Asset = asset.Trim() };
                    if (asset.Length == 0)
                    { intent.Error = "Specify the recipient of this handover."; result.Add(intent); continue; }
                    if (Regex.IsMatch(asset, @"\b(?:denars?|gold|coins?)\b", RegexOptions.IgnoreCase))
                    {
                        intent.Command = "transfer_gold";
                        Match money = Regex.Match(asset, @"^(?:the\s+)?(.+?)\s+(?:denars?|gold|coins?)\b", RegexOptions.IgnoreCase);
                        if (!money.Success || !TryAmount(money.Groups[1].Value, out int amount))
                            intent.Error = "Specify one exact positive whole number of denars.";
                        else intent.Amount = amount;
                        if (Regex.Matches(asset, @"\b(?:denars?|gold|coins?)\b", RegexOptions.IgnoreCase).Count != 1)
                            intent.Error = "Separate the different payments and their exact amounts.";
                        if (Regex.IsMatch(asset, @"\b(?:to join|recruitment|companion|retainer|signing (?:fee|advance))\b", RegexOptions.IgnoreCase))
                            intent.Purpose = "recruitment_advance";
                    }
                    else
                    {
                        intent.Command = "transfer_item";
                        // Native inventory resolution owns item identity, modifier and quantity.
                        if (Regex.IsMatch(asset, @"\b(?:money|some|anything|everything)\b", RegexOptions.IgnoreCase))
                            intent.Error = "Specify the exact item and quantity to give.";
                    }
                    result.Add(intent);
                }
            }
            if (result.Count > 4)
                foreach (var row in result) row.Error = "Give at most four distinct items or payments in one turn.";
            return result;
        }

        public static bool TryAmount(string text, out int amount)
        {
            amount = 0;
            string value = (text ?? "").Trim().ToLowerInvariant();
            if (Regex.IsMatch(value, @"^(?:[1-9]\d*|[1-9]\d{0,2}(?:,\d{3})+)$"))
                return int.TryParse(value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out amount) && amount > 0;
            string[] ones = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
            string[] tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };
            long sum = 0, group = 0, lastScale = long.MaxValue; int last = 0; bool seen = false, hundred = false;
            foreach (string word in value.Replace('-', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int unit = Array.IndexOf(ones, word), ten = Array.IndexOf(tens, word);
                if (unit > 0 && (last == 0 || last == 3 || (last == 2 && unit < 10))) { group += unit; seen = true; last = 1; }
                else if (ten >= 2 && (last == 0 || last == 3)) { group += ten * 10; seen = true; last = 2; }
                else if (word == "hundred" && group > 0 && group < 10 && !hundred) { group *= 100; last = 3; hundred = true; }
                else if ((word == "thousand" || word == "million") && group > 0)
                { long scale = word == "thousand" ? 1000 : 1000000; if (scale >= lastScale) return false;
                    sum += group * scale; group = 0; lastScale = scale; last = 0; hundred = false; }
                else if (word == "and" && seen && (last == 0 || last == 3) && !value.EndsWith("and", StringComparison.Ordinal)) { }
                else return false;
                if (sum + group > int.MaxValue) return false;
            }
            if (!seen || sum + group <= 0) return false;
            amount = (int)(sum + group); return true;
        }

        public static bool IsAcceptedCommitment(string value)
        {
            string key = (value ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            return new[] { "accepted", "accept", "commanded", "final", "sealed", "agreed", "commit", "committed", "immediate", "ordered" }.Contains(key);
        }
    }
}
