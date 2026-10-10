using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.Shared
{
    /// <summary>Saved preferences remain independent of the master switch.</summary>
    public sealed class PlayerApproachOptions
    {
        public const string MasterKey = "playerApproachesEnabled";
        public const string AttentionKey = "playerAttentionTrackingEnabled";
        public const string JealousyKey = "playerJealousyApproachesEnabled";
        public const string KeepKey = "playerKeepInvitationsEnabled";
        public const string PatrolKey = "playerPatrolInterceptionsEnabled";
        public const string BattleKey = "playerBattleRescueDialogueEnabled";
        public const string PrisonKey = "playerPrisonRescueDialogueEnabled";
        public static IEnumerable<string> SettingKeys => new[] { MasterKey, AttentionKey, JealousyKey, KeepKey, PatrolKey, BattleKey, PrisonKey };

        private readonly Dictionary<string, bool> _preferences;
        public PlayerApproachOptions() : this(_ => true) { }
        public PlayerApproachOptions(Func<string, bool> read)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            _preferences = SettingKeys.ToDictionary(key => key, key => read(key), StringComparer.Ordinal);
        }
        public bool Preference(string key) => _preferences.TryGetValue(key, out bool value) && value;
        public bool TrackAttention => Preference(MasterKey) && Preference(AttentionKey);
        public bool Allows(string kind)
        {
            string key = kind == "shared" ? JealousyKey : kind == "keep" ? KeepKey : kind == "patrol" ? PatrolKey
                : kind == "battle_rescue" ? BattleKey : kind == "prison_rescue" ? PrisonKey : "";
            return Preference(MasterKey) && Preference(key);
        }
    }

    /// <summary>Provider-free, shared numerical rules for attention and social approaches.</summary>
    public static class PlayerApproachPolicy
    {
        public const string Version = "reign-player-approach-policy-v1";
        public const float PatrolAreaRadius = 15f;
        public const float PatrolContactRadius = 1.5f;
        public const double PatrolPursuitDays = 0.25d;
        public const int PatrolCooldownDays = 3;
        public const int MaximumKeepSpeakers = 4;
        public static bool EligibleKeepNoble(bool isLord, bool atCurrentSettlement, bool belongsToPlayerParty, bool inPlayerRoster) =>
            isLord && atCurrentSettlement && !belongsToPlayerParty && !inPlayerRoster;
        public static double FightingUnitPower(int healthyMembers, int tier) => Math.Max(0, healthyMembers) * Math.Pow(1d + Math.Max(0, tier), 2d);
        public static bool OutmatchedBeforeJoining(double friendlyPower, double enemyPower) =>
            !double.IsNaN(friendlyPower) && !double.IsNaN(enemyPower) && !double.IsInfinity(friendlyPower)
            && !double.IsInfinity(enemyPower) && friendlyPower > 0d && enemyPower > friendlyPower;

        private static double Percent(double value) => Math.Max(0d, Math.Min(100d, double.IsNaN(value) ? 50d : value));
        public static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
        public static int Clamp(int value, int low, int high) => Math.Max(low, Math.Min(high, value));
        public static double Sensitivity(double jealousy, double patience) =>
            (0.5d + Percent(jealousy) / 100d) * (1.5d - Percent(patience) / 100d);
        public static int SharedDayInterval(double jealousy, double patience) =>
            Clamp(Round(6d + 0.04d * Percent(patience) - 0.04d * Percent(jealousy)), 2, 10);
        public static double NeglectScore(IEnumerable<int> rivalDays, double jealousy, double patience)
        {
            var scores = (rivalDays ?? Enumerable.Empty<int>()).Select(n => Math.Max(0, n) * Sensitivity(jealousy, patience)).ToArray();
            if (scores.Length == 0) return 0d;
            double strongest = scores.Max();
            return strongest + 0.25d * (scores.Sum() - strongest);
        }
        public static int NeglectLevel(double score) => score <= 0d ? 0 : score < 3d ? 1 : score < 6d ? 2 : score < 10d ? 3 : 4;
        public static string NeglectLabel(int level) => level <= 0 ? "none" : level == 1 ? "slightly neglected"
            : level == 2 ? "moderately neglected" : level == 3 ? "strongly neglected" : "hugely neglected";
        public static int NeglectApproachChance(double jealousy, double patience, double boldness, int level, int affinity)
        {
            if (level <= 0) return 0;
            return Clamp(Round(20d + 0.25d * Percent(jealousy) + 0.15d * Percent(boldness)
                - 0.20d * Percent(patience) + 5d * Clamp(level, 0, 4) + 0.10d * Math.Max(0, Clamp(affinity, -100, 100))), 5, 80);
        }
        public static int NeglectRefusalPenalty(int level, double jealousy, double patience)
        {
            if (level <= 0) return 0;
            int basis = level == 1 ? 1 : level == 2 ? 2 : level == 3 ? 4 : 6;
            return Clamp(Round(basis * Sensitivity(jealousy, patience)), 1, 8);
        }
        public static int RenownBonus(double clanRenown) => double.IsNaN(clanRenown) ? 0
            : (int)Math.Min(10d, 2d * Math.Floor(Math.Max(0d, clanRenown) / 100d));
        public static int KeepApproachChance(int affinity, double clanRenown) =>
            Clamp((affinity >= 20 ? Clamp(affinity, -100, 100) : 0) + RenownBonus(clanRenown), 0, 100);
        public static int PatrolApproachChance(int healthyMembers) => healthyMembers < 20 ? 5
            : healthyMembers < 50 ? 10 : healthyMembers < 100 ? 15 : healthyMembers < 200 ? 20 : 25;
        public static int PassingPenalty(double pride, double irritability, double patience) =>
            Clamp(Round(1d + 0.03d * Percent(pride) + 0.02d * Percent(irritability) - 0.02d * Percent(patience)), 1, 5);
        public static double AnyApproachProbability(IEnumerable<int> chances)
        {
            double none = 1d;
            foreach (int chance in chances ?? Enumerable.Empty<int>()) none *= 1d - Clamp(chance, 0, 100) / 100d;
            return 1d - none;
        }
    }
}
