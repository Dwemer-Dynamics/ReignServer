using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Reign.Core.Contracts.Court
{
    public enum ReignPetitionKind
    {
        Food = 0,
        TownGold = 1,
        VillageGold = 2,
        Soldiers = 3
    }

    public enum ReignPetitionSeverity
    {
        None = 0,
        Minor = 1,
        Serious = 2,
        Severe = 3
    }

    public sealed class ReignPetitionCandidateScore
    {
        public string CandidateId { get; set; } = string.Empty;
        public string TownId { get; set; } = string.Empty;
        public ReignPetitionKind Kind { get; set; }
        public ReignPetitionSeverity Severity { get; set; }
        public double NormalizedNeed { get; set; }
    }

    public sealed class ReignPetitionTerms
    {
        public int Gold { get; set; }
        public int FoodStock { get; set; }
        public int Soldiers { get; set; }
        public int DurationDays { get; set; }
        public int RequesterRelation { get; set; }
        public int AssociatedRelation { get; set; }
        public double HearthDaily { get; set; }
        public double ProsperityDaily { get; set; }
        public double VillageOutputFactor { get; set; }
        public double SecurityDaily { get; set; }
        public int CasualtyPercentMaximum { get; set; }
    }

    public static class ReignRulerDocketRules
    {
        public const int NobleMatterPercent = 30;
        public const int FoodHealthyHearths = 400;
        public const int SoldierReserveFloor = 50;
        public const double SoldierReserveFraction = 0.25d;
        public const int FoodStockGrainUnits = 10;
        public const int GoldSubstituteMultiplier = 5;
        public const int DefaultActiveChancellorSalary = 500;
        public const int DeniedRepeatCooldownDays = 3;
        public const int AbsentLordWindowDays = 30;
        public const int AbsentLordThresholdDays = 15;

        public static ReignPetitionSeverity FoodSeverity(double hearths)
        {
            if (hearths < 100d) return ReignPetitionSeverity.Severe;
            if (hearths < 200d) return ReignPetitionSeverity.Serious;
            if (hearths < FoodHealthyHearths) return ReignPetitionSeverity.Minor;
            return ReignPetitionSeverity.None;
        }

        public static ReignPetitionSeverity TownGoldSeverity(double prosperity, double threeDayNetTrend)
        {
            if (prosperity < 1500d) return ReignPetitionSeverity.Severe;
            if (prosperity < 3500d && threeDayNetTrend <= 0d) return ReignPetitionSeverity.Serious;
            if (prosperity < 5000d && threeDayNetTrend < 0d) return ReignPetitionSeverity.Minor;
            return ReignPetitionSeverity.None;
        }

        public static ReignPetitionSeverity VillageGoldSeverity(double hearths, double actualOutput, double healthyExpectedOutput)
        {
            if (hearths < FoodHealthyHearths || healthyExpectedOutput <= 0d) return ReignPetitionSeverity.None;
            double ratio = Math.Max(0d, actualOutput) / healthyExpectedOutput;
            if (ratio < 0.40d) return ReignPetitionSeverity.Severe;
            if (ratio < 0.60d) return ReignPetitionSeverity.Serious;
            if (ratio < 0.80d) return ReignPetitionSeverity.Minor;
            return ReignPetitionSeverity.None;
        }

        public static ReignPetitionSeverity SoldierSeverity(double security, bool besieged)
        {
            if (besieged || security >= 80d) return ReignPetitionSeverity.None;
            if (security < 25d) return ReignPetitionSeverity.Severe;
            if (security < 50d) return ReignPetitionSeverity.Serious;
            return ReignPetitionSeverity.Minor;
        }

        public static ReignPetitionTerms Terms(ReignPetitionKind kind, ReignPetitionSeverity severity)
        {
            ReignPetitionTerms terms = new ReignPetitionTerms();
            switch (severity)
            {
                case ReignPetitionSeverity.Minor:
                    terms.Gold = 1000; terms.FoodStock = 20; terms.Soldiers = 10; terms.DurationDays = 3;
                    terms.RequesterRelation = 5; terms.AssociatedRelation = 1;
                    terms.HearthDaily = 1d; terms.ProsperityDaily = 0.2d; terms.VillageOutputFactor = 0.05d;
                    terms.SecurityDaily = 0.5d; terms.CasualtyPercentMaximum = 5;
                    break;
                case ReignPetitionSeverity.Serious:
                    terms.Gold = 7500; terms.FoodStock = 40; terms.Soldiers = 40; terms.DurationDays = 10;
                    terms.RequesterRelation = 10; terms.AssociatedRelation = 2;
                    terms.HearthDaily = 2d; terms.ProsperityDaily = 0.5d; terms.VillageOutputFactor = 0.10d;
                    terms.SecurityDaily = 1d; terms.CasualtyPercentMaximum = 20;
                    break;
                case ReignPetitionSeverity.Severe:
                    terms.Gold = 20000; terms.FoodStock = 60; terms.Soldiers = 100; terms.DurationDays = 15;
                    terms.RequesterRelation = 20; terms.AssociatedRelation = 4;
                    terms.HearthDaily = 4d; terms.ProsperityDaily = 1d; terms.VillageOutputFactor = 0.20d;
                    terms.SecurityDaily = 2d; terms.CasualtyPercentMaximum = 40;
                    break;
            }

            if (kind != ReignPetitionKind.Food) terms.FoodStock = 0;
            if (kind != ReignPetitionKind.Soldiers) terms.Soldiers = 0;
            if (kind != ReignPetitionKind.TownGold && kind != ReignPetitionKind.VillageGold) terms.Gold = 0;
            if (kind != ReignPetitionKind.Food) terms.HearthDaily = 0d;
            if (kind != ReignPetitionKind.TownGold) terms.ProsperityDaily = 0d;
            if (kind != ReignPetitionKind.VillageGold) terms.VillageOutputFactor = 0d;
            if (kind != ReignPetitionKind.Soldiers)
            {
                terms.SecurityDaily = 0d;
                terms.CasualtyPercentMaximum = 0;
            }
            return terms;
        }

        public static int DailyOpportunityCount(string campaignId, string timelineId, int day)
        {
            return 1 + UniformInclusive(campaignId + "|" + timelineId + "|docket|" + day, 4);
        }

        public static bool IsNobleMatterSlot(string campaignId, string timelineId, int day, int slot)
        {
            return UniformInclusive(campaignId + "|" + timelineId + "|docket-kind|" + day + "|" + slot, 99)
                < NobleMatterPercent;
        }

        public static ReignNobleMatterSeverity NobleMatterSeverity(string campaignId, string timelineId,
            int day, int slot)
        {
            int roll = UniformInclusive(campaignId + "|" + timelineId + "|noble-severity|" + day + "|" + slot, 99);
            if (roll < 50) return ReignNobleMatterSeverity.Petty;
            if (roll < 80) return ReignNobleMatterSeverity.Serious;
            if (roll < 95) return ReignNobleMatterSeverity.Grave;
            return ReignNobleMatterSeverity.Exceptional;
        }

        public static ReignNobleMatterTemplate? SelectNobleMatterTemplate(string campaignId, string timelineId,
            int day, int slot, ReignNobleMatterSeverity severity)
        {
            IReadOnlyList<ReignNobleMatterTemplate> pool = ReignNobleDocketCatalog.ForSeverity(severity);
            if (pool.Count == 0) return null;
            int index = UniformInclusive(campaignId + "|" + timelineId + "|noble-template|" + day + "|" + slot,
                pool.Count - 1);
            return pool[index];
        }

        public static int NobleWinnerRelation(ReignNobleMatterSeverity severity)
        {
            switch (severity)
            {
                case ReignNobleMatterSeverity.Petty: return 5;
                case ReignNobleMatterSeverity.Serious: return 9;
                case ReignNobleMatterSeverity.Grave: return 15;
                default: return 23;
            }
        }

        public static int NobleLoserRelation(ReignNobleMatterSeverity severity)
        {
            switch (severity)
            {
                case ReignNobleMatterSeverity.Petty: return -6;
                case ReignNobleMatterSeverity.Serious: return -15;
                case ReignNobleMatterSeverity.Grave: return -30;
                default: return -50;
            }
        }

        public static int AcceptedLossRelation(int fullLoss, int acceptanceTier)
        {
            if (fullLoss >= 0) return fullLoss;
            if (acceptanceTier >= 3) return 0;
            double multiplier = acceptanceTier == 2 ? 0.25d : acceptanceTier == 1 ? 0.60d : 1d;
            return (int)Math.Round(fullLoss * multiplier, MidpointRounding.AwayFromZero);
        }

        public static bool VisibleNobleAcceptanceMatchesTier(string quote, int acceptanceTier)
        {
            if (acceptanceTier <= 0 || acceptanceTier > 3 || string.IsNullOrWhiteSpace(quote))
                return acceptanceTier == 0;
            string value = quote.Trim().ToLowerInvariant()
                .Replace('–', '-').Replace('—', '-');
            if (ContainsForbiddenNobleDocketStatistics(value))
                return false;

            bool rejectsJudgment = ContainsAny(value,
                "will not accept", "won't accept", "cannot accept", "can't accept",
                "do not accept", "don't accept", "would not accept", "wouldn't accept",
                "refuse your judgment", "refuse the judgment",
                "did not accept", "have not accepted", "never accept", "never accepted",
                "refuse your ruling", "refuse the ruling", "reject your judgment",
                "reject the judgment", "reject your ruling", "reject the ruling",
                "defy your judgment", "defy the judgment", "defy your ruling",
                "defy the ruling", "appeal your judgment", "appeal the judgment",
                "appeal your ruling", "appeal the ruling", "refuse it", "reject it",
                "defy it", "will appeal", "shall appeal", "intend to appeal");
            if (rejectsJudgment) return false;

            bool acceptsJudgment = ContainsAny(value, "accept your judgment", "accept the judgment",
                "abide by your judgment", "abide by the judgment", "yield to your judgment",
                "abide by your ruling", "abide by the ruling", "abide by it",
                "yield to the judgment", "submit to your judgment", "submit to the judgment",
                "accept your ruling", "accept the ruling", "accept your decision",
                "accept the decision", "accept this judgment", "accept this ruling",
                "accept this decision", "accept it", "i accept it", "i will accept it", "i can accept it",
                "we accept it", "we will accept it", "we can accept it", "shall accept it",
                "accept it as your judgment", "accept it as the judgment",
                "honor your judgment", "honour your judgment",
                "honor the judgment", "honour the judgment", "honor your ruling",
                "honour your ruling", "respect your judgment", "respect the judgment",
                "respect your ruling", "respect the ruling", "obey your judgment",
                "obey the judgment", "obey your ruling", "obey the ruling",
                "bound by your judgment", "bound by the judgment", "bound by your ruling",
                "bound by the ruling", "bow to your judgment", "bow to the judgment",
                "bow to your ruling", "bow to the ruling", "your judgment is given",
                "the judgment is given", "your judgment is heard", "the judgment is heard",
                "your ruling is given", "the ruling is given", "your ruling is heard",
                "the ruling is heard");
            bool retainsGrievance = value.Contains("grievance") && ContainsAny(value,
                "remain", "keep", "not forget", "dead", "overruled", "still");
            if (acceptanceTier == 1)
                return acceptsJudgment && (retainsGrievance || ContainsAny(value,
                    "though", "although", "but", "reluctantly", "with reservations", "still object",
                    "with my whole heart? no", "with my whole heart, no",
                    "not with my whole heart", "wholeheartedly? no", "wholeheartedly, no",
                    "not wholeheartedly",
                    "cannot approve", "do not agree", "disagree"));
            if (acceptanceTier == 2)
            {
                bool disclaimsContinuationPromise = ContainsAny(value,
                        "cannot promise", "can't promise", "will not promise", "won't promise",
                        "do not promise", "don't promise", "cannot swear", "can't swear",
                        "will not swear", "won't swear")
                    && ContainsAny(value, "quarrel", "dispute", "grievance", "complaint", "claim");
                bool renouncesContinuation = !disclaimsContinuationPromise
                    && (ContainsNegatedNobleDisputeContinuation(value)
                        || ContainsAny(value, "keep this quarrel alive", "keep the quarrel alive",
                        "keep this dispute alive", "keep the dispute alive",
                        "renew this quarrel", "renew the quarrel", "continue this quarrel",
                        "continue the quarrel", "pursue this quarrel", "pursue the quarrel",
                        "keep it", "renew it", "continue it", "pursue it", "revive it"));
                return acceptsJudgment && (renouncesContinuation || ContainsAny(value, "set my grievance aside",
                    "set the grievance aside", "seek reconciliation", "no lasting quarrel",
                    "no lasting grudge", "make peace", "seek peace", "end this quarrel",
                    "let this quarrel end", "leave this grievance behind", "end the quarrel",
                    "end this dispute", "end the dispute", "put this matter to rest",
                    "put the matter to rest", "let the matter rest", "lay this quarrel aside",
                    "lay the quarrel aside", "pursue it no further", "press it no further",
                    "renew no quarrel", "no further quarrel", "close this dispute",
                    "the quarrel ends here", "this quarrel ends here", "quarrel ends here",
                    "the dispute ends here", "this dispute ends here"));
            }
            bool namesUnresentfulStance = ContainsAny(value,
                "no resentment", "without resentment", "no bitterness", "without bitterness",
                "no grudge", "without a grudge", "bear no grudge", "hold no grudge",
                "wholeheartedly", "with my whole heart", "feelings no court can command");
            bool disclaimsResentmentPromise = namesUnresentfulStance && ContainsAny(value,
                "cannot promise", "can't promise", "will not promise", "won't promise",
                "do not promise", "don't promise", "cannot swear", "can't swear",
                "will not swear", "won't swear", "cannot bind", "can't bind",
                "will not bind", "won't bind", "will not dress a lie", "won't dress a lie",
                "cannot honestly say", "can't honestly say");
            bool retainsResentment = disclaimsResentmentPromise || ContainsAny(value,
                "will bear resentment", "shall bear resentment",
                "carry resentment", "hold resentment", "retain resentment", "with resentment");
            return acceptsJudgment && !retainsResentment && ContainsAny(value,
                "no resentment", "without resentment", "carry no resentment", "bear no resentment", "without rancor",
                "without grievance", "without bitterness", "without ill will", "with no resentment",
                "accept wholeheartedly", "accept it wholeheartedly",
                "accept your judgment wholeheartedly", "accept the judgment wholeheartedly",
                "wholeheartedly accept", "fully accept", "accept it fully", "bear no grudge",
                "hold no grudge", "no quarrel with your judgment");
        }

        public static int VisibleNobleAcceptanceTier(string visibleText)
        {
            if (ContainsForbiddenNobleDocketStatistics(visibleText)) return 0;
            for (int tier = 3; tier >= 1; tier--)
                if (VisibleNobleAcceptanceMatchesTier(visibleText, tier)) return tier;
            return 0;
        }

        public static bool ContainsForbiddenNobleDocketStatistics(string visibleText)
        {
            string value = (visibleText ?? string.Empty).Trim().ToLowerInvariant();
            return value.Contains("%")
                || value.Contains("percent") || value.Contains("per cent")
                || value.Contains("acceptance tier") || value.Contains("relation penalty")
                || value.Contains("relationship penalty") || value.Contains("relationship loss")
                || value.Contains("relation loss") || value.Contains("relation score")
                || value.Contains("relationship score") || value.Contains("game mechanic")
                || value.Contains("mechanical penalty") || value.Contains("hidden truth")
                || value.Contains("canonical truth") || value.Contains("private field")
                || value.Contains("system instruction") || value.Contains("prompt instruction")
                || value.Contains("game terminology") || value.Contains("system terminology");
        }

        public static string SanitizeVisibleNobleReply(string visibleText, string courtLocationName)
        {
            string value = (visibleText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (value.Length == 0) return string.Empty;

            List<string> lines = value.Split('\n').ToList();
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
            string expectedHeading = NormalizeVisibleLocationHeading(courtLocationName);
            if (lines.Count > 1 && expectedHeading.Length > 0
                && string.Equals(NormalizeVisibleLocationHeading(lines[0]), expectedHeading,
                    StringComparison.OrdinalIgnoreCase)
                && lines.Skip(1).Any(line => !string.IsNullOrWhiteSpace(line)))
            {
                lines.RemoveAt(0);
                while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
            }

            return NormalizeAccidentalDoublePeriods(string.Join("\n", lines)).Trim();
        }

        private static string NormalizeVisibleLocationHeading(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            while (normalized.StartsWith("#", StringComparison.Ordinal))
                normalized = normalized.Substring(1).TrimStart();
            if (normalized.Length >= 4 && normalized.StartsWith("**", StringComparison.Ordinal)
                && normalized.EndsWith("**", StringComparison.Ordinal))
                normalized = normalized.Substring(2, normalized.Length - 4).Trim();
            return normalized.Trim().TrimEnd(':', '-', '–', '—').Trim();
        }

        private static string NormalizeAccidentalDoublePeriods(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf("..", StringComparison.Ordinal) < 0)
                return value ?? string.Empty;
            System.Text.StringBuilder builder = new System.Text.StringBuilder(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                bool exactDouble = value[index] == '.' && index + 1 < value.Length && value[index + 1] == '.'
                    && (index == 0 || value[index - 1] != '.')
                    && (index + 2 >= value.Length || value[index + 2] != '.');
                builder.Append(value[index]);
                if (exactDouble) index++;
            }
            return builder.ToString();
        }

        private static bool ContainsAny(string value, params string[] phrases)
        {
            return phrases.Any(phrase => value.IndexOf(phrase,
                StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool ContainsNegatedNobleDisputeContinuation(string value)
        {
            // Keep the promise, continuation verb, and disputed matter in one
            // bounded clause. Combining an unrelated promise such as "I will
            // not use violence" with a later "I will keep my grievance" would
            // invert the speaker's actual stance.
            return Regex.IsMatch(value ?? string.Empty,
                @"\b(?:will not|shall not|won't|do not intend to|have no intention of)\b[^.!?;\r\n]{0,32}\b(?:keep|renew|continue|pursue|revive|carry|press|reopen|raise)\b[^.!?;\r\n]{0,48}\b(?:(?:this|the)\s+)?(?:quarrel|dispute|grievance|complaint|claim|it)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public static int ClanSpilloverRelation(int principalDelta)
        {
            if (principalDelta == 0) return 0;
            return (int)Math.Round(principalDelta / 3d, MidpointRounding.AwayFromZero);
        }

        public static bool ShouldApplyOpposingClanSpillover(
            ReignNobleMatterCategory category, bool winnerIsClanLeader,
            bool loserIsClanLeader, string winnerClanId, string loserClanId)
        {
            if (string.IsNullOrWhiteSpace(winnerClanId)
                || string.IsNullOrWhiteSpace(loserClanId)
                || string.Equals(winnerClanId, loserClanId,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            return winnerIsClanLeader || loserIsClanLeader
                || category == ReignNobleMatterCategory.Family
                || category == ReignNobleMatterCategory.Property
                || category == ReignNobleMatterCategory.Dynastic;
        }

        public static double ChancellorInvestigationChance(int charm, int leadership, int steward)
        {
            double average = (Math.Max(0, charm) + Math.Max(0, leadership) + Math.Max(0, steward)) / 3d;
            return Math.Min(0.80d, average * 0.004d);
        }

        public static bool ChancellorInvestigationSucceeds(string caseId, int charm, int leadership, int steward)
        {
            int roll = UniformInclusive((caseId ?? string.Empty) + "|chancellor-investigation", 9999);
            return roll < (int)Math.Round(ChancellorInvestigationChance(charm, leadership, steward) * 10000d,
                MidpointRounding.AwayFromZero);
        }

        public static IReadOnlyList<ReignPetitionCandidateScore> SelectCandidates(
            IEnumerable<ReignPetitionCandidateScore> candidates,
            int opportunityCount,
            string stableSeed)
        {
            if (opportunityCount <= 0) return Array.Empty<ReignPetitionCandidateScore>();
            return (candidates ?? Enumerable.Empty<ReignPetitionCandidateScore>())
                .Where(x => x != null && x.Severity != ReignPetitionSeverity.None
                    && !string.IsNullOrWhiteSpace(x.CandidateId) && !string.IsNullOrWhiteSpace(x.TownId))
                .GroupBy(x => x.TownId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(x => (int)x.Severity)
                    .ThenByDescending(x => Clamp01(x.NormalizedNeed))
                    .ThenBy(x => StableHash(stableSeed + "|candidate|" + x.CandidateId))
                    .First())
                .OrderByDescending(x => (int)x.Severity)
                .ThenByDescending(x => Clamp01(x.NormalizedNeed))
                .ThenBy(x => StableHash(stableSeed + "|selected|" + x.CandidateId))
                .Take(Math.Min(5, opportunityCount))
                .ToList();
        }

        public static int FoodGoldSubstitute(int foodStock, int capitalGrainPrice)
        {
            return SaturatingMultiply(Math.Max(0, foodStock), Math.Max(0, capitalGrainPrice), FoodStockGrainUnits, GoldSubstituteMultiplier);
        }

        public static int SoldierGoldSubstitute(int nativeReplacementValue, int fullTermWages)
        {
            long baseValue = Math.Max(0L, nativeReplacementValue) + Math.Max(0L, fullTermWages);
            return baseValue > int.MaxValue / GoldSubstituteMultiplier
                ? int.MaxValue
                : (int)(baseValue * GoldSubstituteMultiplier);
        }

        public static int MinimumHealthyGarrisonAfterDispatch(int healthyGarrison)
        {
            return Math.Max(SoldierReserveFloor, (int)Math.Ceiling(Math.Max(0, healthyGarrison) * SoldierReserveFraction));
        }

        public static int CasualtyCount(string expeditionId, int manifestCount, int maximumPercent)
        {
            int maximum = (int)Math.Floor(Math.Max(0, manifestCount) * Math.Max(0, maximumPercent) / 100d);
            return UniformInclusive((expeditionId ?? string.Empty) + "|casualties", maximum);
        }

        private static int UniformInclusive(string seed, int maximumInclusive)
        {
            if (maximumInclusive <= 0) return 0;
            return (int)(StableHash(seed) % ((uint)maximumInclusive + 1u));
        }

        private static uint StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261u;
                foreach (char character in value ?? string.Empty)
                {
                    hash ^= character;
                    hash *= 16777619u;
                }
                return hash;
            }
        }

        private static double Clamp01(double value)
        {
            return Math.Max(0d, Math.Min(1d, value));
        }

        private static int SaturatingMultiply(params int[] values)
        {
            long result = 1L;
            foreach (int value in values)
            {
                result *= value;
                if (result >= int.MaxValue) return int.MaxValue;
            }
            return (int)result;
        }
    }
}
