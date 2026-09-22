#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Reign.Core.Contracts.Dialogue
{
    public enum ReignXpSkill { Charm, Steward, Trade, Leadership, Tactics }

    public sealed class ReignXpOptions
    {
        public bool Enabled { get; set; } = true;
        public double Multiplier { get; set; } = 1;
        public bool Confirmed { get; set; }
    }

    public sealed class ReignXpParticipant
    {
        public string HeroId { get; set; } = "";
        public int Steward { get; set; }
        public int Trade { get; set; }
        public int Leadership { get; set; }
        public int Tactics { get; set; }
    }

    // Serialized into the native campaign save alongside the player's actual XP.
    // Completed/disabled claims are retained: revisiting a receipt never pays it again.
    public sealed class ReignXpLedger
    {
        public HashSet<string> Consumed { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, ReignXpParticipant>> Phases { get; set; }
            = new Dictionary<string, Dictionary<string, ReignXpParticipant>>(StringComparer.Ordinal);

        public bool TryClaim(string key) => !string.IsNullOrWhiteSpace(key) && Consumed.Add(key);

        public bool RecordExchange(string phaseKey, string turnId, IEnumerable<ReignXpParticipant> people)
        {
            if (string.IsNullOrWhiteSpace(phaseKey) || string.IsNullOrWhiteSpace(turnId)
                || !TryClaim("social-turn:" + turnId)) return false;
            // A credited phase may be revisited after a native reload. New speech
            // still advances the event, but must not reopen its reward pool.
            if (Consumed.Contains("bonus:" + phaseKey)) return true;
            if (!Phases.TryGetValue(phaseKey, out var participants))
                Phases[phaseKey] = participants = new Dictionary<string, ReignXpParticipant>(StringComparer.Ordinal);
            foreach (var person in people ?? Enumerable.Empty<ReignXpParticipant>())
                if (person != null && !string.IsNullOrWhiteSpace(person.HeroId)
                    && !participants.ContainsKey(person.HeroId)) participants.Add(person.HeroId, person);
            return true;
        }
    }

    public static class ReignXpRules
    {
        public const double ConversationXp = 5;
        public const double PetitionXp = 10;
        public const double SocialBonusXp = 2;

        public static bool IsValidMultiplier(double value) => !double.IsNaN(value)
            && !double.IsInfinity(value) && value >= .25 && value <= 5
            && Math.Abs(value * 4 - Math.Round(value * 4)) < .000001;

        public static float Amount(double baseXp, ReignXpOptions options) => options != null
            && options.Confirmed && options.Enabled && IsValidMultiplier(options.Multiplier)
            && baseXp > 0 && baseXp <= PetitionXp ? (float)(baseXp * options.Multiplier) : 0;

        public static ReignXpSkill? SelectPhaseSkill(string phaseKey, IEnumerable<ReignXpParticipant> people)
        {
            var eligible = (people ?? Enumerable.Empty<ReignXpParticipant>())
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.HeroId)
                    && Math.Max(Math.Max(p.Steward, p.Trade), Math.Max(p.Leadership, p.Tactics)) > 0)
                .GroupBy(p => p.HeroId, StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(p => p.HeroId, StringComparer.Ordinal).ToList();
            if (eligible.Count == 0 || string.IsNullOrWhiteSpace(phaseKey)) return null;
            var person = eligible[Index(phaseKey + ":person", eligible.Count)];
            var skills = new[] { ReignXpSkill.Steward, ReignXpSkill.Trade, ReignXpSkill.Leadership, ReignXpSkill.Tactics };
            var values = new[] { person.Steward, person.Trade, person.Leadership, person.Tactics };
            int highest = values.Max();
            var tied = skills.Where((s, i) => values[i] == highest).ToArray();
            return tied[Index(phaseKey + ":skill:" + person.HeroId, tied.Length)];
        }

        private static int Index(string seed, int count)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
                uint value = ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | hash[3];
                return (int)(value % (uint)count);
            }
        }
    }
}
