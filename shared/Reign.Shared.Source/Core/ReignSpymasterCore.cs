using System;

namespace ReignShared.Spymaster
{
    public sealed class ReignSpymasterCoreQuote
    {
        public int GoldCost;
        public double DurationDays;
        public double SuccessChance;
        public double DetectionChance;
        public bool Harmful;
        public bool Tiered;
        public string DifficultyLabel = string.Empty;
    }

    public sealed class ReignSpymasterEffectSpec
    {
        public string EffectType = string.Empty;
        public double Magnitude;
        public double DurationDays;
    }

    /// <summary>
    /// Bannerlord-independent, deterministic contract for the Spymaster feature.
    /// The client uses this class for production quotes and the server uses the
    /// same code for exhaustive boundary verification.
    /// </summary>
    public static class ReignSpymasterCore
    {
        public const int MissionCapacity = 3;
        public const double RumorRemovalChance = 82d;
        public const double ReputationRemovalChance = 15d;
        public const double SocialMitigationFactor = 0.35d;
        public const double ForeignAgentActivationRelation = -30d;

        public static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        public static double ClanMultiplier(int tier)
        {
            return 1d + Math.Max(0, Math.Min(6, tier)) * 0.35d;
        }

        public static double DetectionChance(double baseNoticePercent, int roguery)
        {
            double value = baseNoticePercent * (1.5d - Math.Max(0, Math.Min(300, roguery)) / 300d);
            return Clamp(value, 5d, 95d);
        }

        public static double RecruitmentChance(double loyalty)
        {
            return Clamp(90d - Clamp(loyalty, 0d, 40d) * 2d, 10d, 90d);
        }

        public static bool ShouldRecruit(double loyalty, double unitRoll)
        {
            return loyalty <= 40d && Clamp(unitRoll, 0d, 1d) < RecruitmentChance(loyalty) / 100d;
        }

        public static bool ForeignAgentCanDisrupt(double sponsorRulerOutlookTowardPlayer)
        {
            return sponsorRulerOutlookTowardPlayer <= ForeignAgentActivationRelation;
        }

        public static int RelationLoss(int clanTier, bool harmful, bool ruler)
        {
            return Math.Max(2, 3 + Math.Max(0, clanTier) * 2 + (harmful ? 4 : 0) + (ruler ? 4 : 0));
        }

        public static double BreakoutChance(int playerRoguery, double security, int targetClanTier)
        {
            return Clamp(25d + Math.Max(0, playerRoguery) * 0.18d - security * 0.20d - Math.Max(0, targetClanTier) * 4d, 5d, 85d);
        }

        public static string SocialMitigationOutcome(string missionType, double unitRoll)
        {
            unitRoll = Clamp(unitRoll, 0d, 1d);
            string type = (missionType ?? string.Empty).ToLowerInvariant();
            if (type.Contains("rumor")) return unitRoll < RumorRemovalChance / 100d ? "disproven" : "mitigated";
            if (type.Contains("reputation")) return unitRoll < ReputationRemovalChance / 100d ? "removed" : "greatly_mitigated";
            return string.Empty;
        }

        public static ReignSpymasterEffectSpec Effect(string missionType)
        {
            switch ((missionType ?? string.Empty).ToLowerInvariant())
            {
                case "disrupt_food": return NewEffect("food", -0.35d, 14d);
                case "disrupt_construction": return NewEffect("construction", -0.50d, 18d);
                case "disrupt_security": return NewEffect("security", -2d, 21d);
                case "disrupt_loyalty": return NewEffect("loyalty", -2d, 21d);
                default: return null;
            }
        }

        public static ReignSpymasterCoreQuote Quote(string missionType, int roguery, int clanTier, bool ruler)
        {
            string type = (missionType ?? string.Empty).ToLowerInvariant();
            int boundedTier = Math.Max(0, Math.Min(6, clanTier));
            double tier = ClanMultiplier(boundedTier) * (ruler ? 1.25d : 1d);
            int baseCost;
            double duration;
            double difficulty;
            double baseNotice;
            bool harmful;
            switch (type)
            {
                case "land_intelligence": baseCost = 1500; duration = 4d; difficulty = 10d; baseNotice = 20d; harmful = false; break;
                case "person_skills": baseCost = 1500; duration = 4d; difficulty = 14d; baseNotice = 24d; harmful = false; break;
                case "person_relationships": baseCost = 2250; duration = 5d; difficulty = 22d; baseNotice = 32d; harmful = false; break;
                case "person_rumors": baseCost = 3000; duration = 6d; difficulty = 30d; baseNotice = 40d; harmful = false; break;
                case "counterintelligence": baseCost = 7500; duration = 8d; difficulty = 25d; baseNotice = 12d; harmful = false; break;
                case "disrupt_food": baseCost = 7500; duration = 7d; difficulty = 30d; baseNotice = 38d; harmful = true; break;
                case "disrupt_construction": baseCost = 15000; duration = 9d; difficulty = 40d; baseNotice = 48d; harmful = true; break;
                case "disrupt_security": baseCost = 22500; duration = 10d; difficulty = 50d; baseNotice = 58d; harmful = true; break;
                case "disrupt_loyalty": baseCost = 30000; duration = 12d; difficulty = 60d; baseNotice = 68d; harmful = true; break;
                case "assassinate_governor": baseCost = 37500; duration = 14d; difficulty = 78d; baseNotice = 100d; harmful = true; break;
                case "assassinate_person": baseCost = 37500; duration = 14d; difficulty = 82d; baseNotice = 100d; harmful = true; break;
                case "mitigate_own_rumor": baseCost = 4500; duration = 5d; difficulty = 20d; baseNotice = 10d; harmful = false; break;
                case "mitigate_own_reputation": baseCost = 11250; duration = 7d; difficulty = 35d; baseNotice = 10d; harmful = false; break;
                case "mitigate_target_rumor": baseCost = 4500; duration = 6d; difficulty = 30d; baseNotice = 26d; harmful = false; break;
                case "mitigate_target_reputation": baseCost = 11250; duration = 8d; difficulty = 45d; baseNotice = 34d; harmful = false; break;
                case "fabricate_positive": baseCost = 4500; duration = 6d; difficulty = 28d; baseNotice = 24d; harmful = false; break;
                case "fabricate_moderate": baseCost = 11250; duration = 8d; difficulty = 48d; baseNotice = 48d; harmful = true; break;
                case "fabricate_severe": baseCost = 30000; duration = 12d; difficulty = 70d; baseNotice = 72d; harmful = true; break;
                default: baseCost = 1500; duration = 5d; difficulty = 25d; baseNotice = 30d; harmful = false; break;
            }

            bool tiered = type != "land_intelligence" && type != "counterintelligence"
                && type != "disrupt_food" && type != "disrupt_construction"
                && type != "disrupt_security" && type != "disrupt_loyalty"
                && type != "mitigate_own_rumor" && type != "mitigate_own_reputation";
            double targetDifficulty = difficulty + (tiered ? boundedTier * 5d + (ruler ? 8d : 0d) : 0d);
            double success = Clamp(52d + Math.Max(0, roguery) * 0.18d - targetDifficulty, 5d, 95d);
            int cost = type.StartsWith("assassinate", StringComparison.OrdinalIgnoreCase)
                ? baseCost * (boundedTier + (ruler ? 1 : 0) + 1)
                : (int)(Math.Ceiling(baseCost * (tiered ? tier : 1d) / 50d) * 50d);
            return new ReignSpymasterCoreQuote
            {
                GoldCost = cost,
                DurationDays = duration + (tiered ? boundedTier * 0.5d : 0d),
                SuccessChance = success,
                DetectionChance = DetectionChance(baseNotice, roguery),
                Harmful = harmful,
                Tiered = tiered,
                DifficultyLabel = success >= 75d ? "Routine" : success >= 55d ? "Challenging" : success >= 35d ? "Difficult" : "Extreme"
            };
        }

        private static ReignSpymasterEffectSpec NewEffect(string type, double magnitude, double duration)
        {
            return new ReignSpymasterEffectSpec { EffectType = type, Magnitude = magnitude, DurationDays = duration };
        }
    }
}
