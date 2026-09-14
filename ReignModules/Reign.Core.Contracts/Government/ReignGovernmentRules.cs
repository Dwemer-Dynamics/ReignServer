using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Government
{
    public enum ReignGovernmentInstitutionKind
    {
        Senate,
        CouncilOfPeers,
        Veche,
        Oenach,
        Majlis,
        Kurultai,
        Thing,
        CouncilOfEstates
    }

    public enum ReignGovernmentSeatSource
    {
        TownNotable,
        VillageNotable,
        TownMerchantOrArtisan,
        VillageHeadmanOrLandowner,
        TownMerchant,
        LandholdingClanLeader
    }

    public enum ReignGovernmentPlank
    {
        RoyalAuthority,
        RepresentativeAuthority,
        PopularWelfare,
        Trade,
        Agriculture,
        Infrastructure,
        MilitaryStrength,
        Expansion,
        Peace,
        Justice,
        Security,
        Faith,
        NoblePrivilege,
        ClanPrivilege,
        LocalAutonomy,
        Espionage
    }

    public enum ReignGovernmentActionKind
    {
        Advice,
        War,
        Peace,
        Treaty,
        Policy,
        Taxation,
        MajorSpending,
        FiefTransfer,
        MajorJustice,
        Espionage,
        SettlementRelief
    }

    public enum ReignGovernmentActionDisposition
    {
        Advisory,
        FormalPressure,
        ReconsiderationDelay,
        ApprovalOrOverride,
        BlockedPendingApprovalOrLevelReduction
    }

    public enum ReignGovernmentResolutionScale
    {
        Minor = 1,
        Standard = 2,
        Major = 3
    }

    public sealed class ReignGovernmentInstitutionProfile
    {
        public ReignGovernmentInstitutionProfile(
            string cultureId,
            string name,
            ReignGovernmentInstitutionKind kind,
            params ReignGovernmentSeatSource[] seatSources)
        {
            CultureId = cultureId ?? string.Empty;
            Name = name ?? string.Empty;
            Kind = kind;
            SeatSources = seatSources ?? Array.Empty<ReignGovernmentSeatSource>();
        }

        public string CultureId { get; }
        public string Name { get; }
        public ReignGovernmentInstitutionKind Kind { get; }
        public IReadOnlyList<ReignGovernmentSeatSource> SeatSources { get; }
        public bool IsPrimarilyNoble => SeatSources.Contains(ReignGovernmentSeatSource.LandholdingClanLeader);
    }

    public sealed class ReignGovernmentPartyBlueprint
    {
        public ReignGovernmentPartyBlueprint(string id, string name, params ReignGovernmentPlank[] planks)
        {
            Id = id ?? string.Empty;
            Name = name ?? string.Empty;
            Planks = planks ?? Array.Empty<ReignGovernmentPlank>();
        }

        public string Id { get; }
        public string Name { get; }
        public IReadOnlyList<ReignGovernmentPlank> Planks { get; }
    }

    public sealed class ReignGovernmentPersonality
    {
        public int Valor { get; set; }
        public int Mercy { get; set; }
        public int Generosity { get; set; }
        public int Honor { get; set; }
        public int Calculating { get; set; }
        public int Charm { get; set; }
        public int Leadership { get; set; }
        public bool IsRulerClanMember { get; set; }
        public bool IsLandholdingLord { get; set; }
        public bool IsMerchant { get; set; }
        public bool IsRuralNotable { get; set; }
    }

    public sealed class ReignGovernmentVoteInput
    {
        public ReignGovernmentPlank PrimaryPlank { get; set; }
        public IReadOnlyList<ReignGovernmentPlank> SecondaryPlanks { get; set; } = Array.Empty<ReignGovernmentPlank>();
        public int RulerRelation { get; set; }
        public int CurrentLevel { get; set; }
        public double PartySeatShare { get; set; }
        public int PersonalityModifier { get; set; }
        public int PartyLoyalty { get; set; }
        public bool SpeakerSupportsReduction { get; set; }
        public int GrievancePenalty { get; set; }
        public int StableVariance { get; set; }
    }

    public readonly struct ReignGovernmentVoteResult
    {
        public ReignGovernmentVoteResult(int score, bool supports)
        {
            Score = score;
            Supports = supports;
        }

        public int Score { get; }
        public bool Supports { get; }
    }

    public sealed class ReignGovernmentResolutionVoteInput
    {
        public bool IsProposingPartyMember { get; set; }
        public int SharedPlankCount { get; set; }
        public int GovernmentLoyalty { get; set; }
        public int PartyLoyalty { get; set; }
        public bool OwnSpeakerSupports { get; set; }
        public int ProposingSpeakerCharm { get; set; }
        public int OwnSpeakerCharm { get; set; }
        public int ProposingSpeakerRelation { get; set; }
        public int LobbyingShift { get; set; }
        public int StableVariance { get; set; }
    }

    public readonly struct ReignGovernmentDailyEffects
    {
        public ReignGovernmentDailyEffects(double loyalty, double prosperity, double hearth)
        {
            Loyalty = loyalty;
            Prosperity = prosperity;
            Hearth = hearth;
        }

        public double Loyalty { get; }
        public double Prosperity { get; }
        public double Hearth { get; }
    }

    public readonly struct ReignGovernmentAuthorityTransition
    {
        public ReignGovernmentAuthorityTransition(
            int loyalty,
            int prosperity,
            int hearth,
            double temporaryLoyaltyPerDay,
            double temporaryProsperityPerDay,
            double temporaryHearthPerDay,
            int durationDays,
            int trust,
            int supportiveRelation,
            int royalistRelation)
        {
            Loyalty = loyalty;
            Prosperity = prosperity;
            Hearth = hearth;
            TemporaryLoyaltyPerDay = temporaryLoyaltyPerDay;
            TemporaryProsperityPerDay = temporaryProsperityPerDay;
            TemporaryHearthPerDay = temporaryHearthPerDay;
            DurationDays = durationDays;
            Trust = 0; // Legacy compatibility field; Government Trust is retired.
            SupportiveRelation = supportiveRelation;
            RoyalistRelation = royalistRelation;
        }

        public int Loyalty { get; }
        public int Prosperity { get; }
        public int Hearth { get; }
        public double TemporaryLoyaltyPerDay { get; }
        public double TemporaryProsperityPerDay { get; }
        public double TemporaryHearthPerDay { get; }
        public int DurationDays { get; }
        public int Trust { get; }
        public int SupportiveRelation { get; }
        public int RoyalistRelation { get; }
    }

    public readonly struct ReignGovernmentReductionPenalty
    {
        public ReignGovernmentReductionPenalty(
            int settlementLoyalty,
            int landholdingClanLeaderRelation,
            int nonLandholdingDissenterRelation,
            int trust)
        {
            SettlementLoyalty = settlementLoyalty;
            LandholdingClanLeaderRelation = landholdingClanLeaderRelation;
            NonLandholdingDissenterRelation = nonLandholdingDissenterRelation;
            Trust = 0; // Legacy compatibility field; Government Trust is retired.
        }

        public int SettlementLoyalty { get; }
        public int LandholdingClanLeaderRelation { get; }
        public int NonLandholdingDissenterRelation { get; }
        public int Trust { get; }
    }

    public static class ReignGovernmentRules
    {
        public const int MinimumLevel = 1;
        public const int MaximumLevel = 5;
        public const int ReductionApprovalNumerator = 2;
        public const int ReductionApprovalDenominator = 3;
        public const int SeasonalMeetingDays = 21;
        public const int MaximumSpeakerCallsPerMeeting = 1;

        private static readonly ReignGovernmentPartyBlueprint[] PartyBlueprints =
        {
            new ReignGovernmentPartyBlueprint("crown_order", "Crown and Order", ReignGovernmentPlank.RoyalAuthority, ReignGovernmentPlank.Security),
            new ReignGovernmentPartyBlueprint("common_weal", "Common Weal", ReignGovernmentPlank.RepresentativeAuthority, ReignGovernmentPlank.PopularWelfare, ReignGovernmentPlank.Agriculture),
            new ReignGovernmentPartyBlueprint("open_markets", "Open Markets", ReignGovernmentPlank.Trade, ReignGovernmentPlank.Infrastructure, ReignGovernmentPlank.PopularWelfare, ReignGovernmentPlank.Peace),
            new ReignGovernmentPartyBlueprint("land_and_hearth", "Land and Hearth", ReignGovernmentPlank.Agriculture, ReignGovernmentPlank.LocalAutonomy, ReignGovernmentPlank.PopularWelfare),
            new ReignGovernmentPartyBlueprint("iron_standard", "Iron Standard", ReignGovernmentPlank.MilitaryStrength, ReignGovernmentPlank.Expansion),
            new ReignGovernmentPartyBlueprint("peace_and_plenty", "Peace and Plenty", ReignGovernmentPlank.Peace, ReignGovernmentPlank.Trade, ReignGovernmentPlank.PopularWelfare),
            new ReignGovernmentPartyBlueprint("lawful_estates", "Lawful Estates", ReignGovernmentPlank.Justice, ReignGovernmentPlank.NoblePrivilege, ReignGovernmentPlank.RepresentativeAuthority, ReignGovernmentPlank.Peace),
            new ReignGovernmentPartyBlueprint("free_clans", "Free Clans", ReignGovernmentPlank.ClanPrivilege, ReignGovernmentPlank.LocalAutonomy, ReignGovernmentPlank.RepresentativeAuthority),
            new ReignGovernmentPartyBlueprint("temple_compact", "Temple Compact", ReignGovernmentPlank.Faith, ReignGovernmentPlank.PopularWelfare),
            new ReignGovernmentPartyBlueprint("watchful_realm", "Watchful Realm", ReignGovernmentPlank.Security, ReignGovernmentPlank.Espionage, ReignGovernmentPlank.RoyalAuthority),
            new ReignGovernmentPartyBlueprint("civic_builders", "Civic Builders", ReignGovernmentPlank.Infrastructure, ReignGovernmentPlank.Trade, ReignGovernmentPlank.RepresentativeAuthority),
            new ReignGovernmentPartyBlueprint("old_privileges", "Old Privileges", ReignGovernmentPlank.NoblePrivilege, ReignGovernmentPlank.RoyalAuthority, ReignGovernmentPlank.LocalAutonomy),
            new ReignGovernmentPartyBlueprint("border_voice", "Border Voice", ReignGovernmentPlank.Security, ReignGovernmentPlank.LocalAutonomy, ReignGovernmentPlank.Agriculture),
            new ReignGovernmentPartyBlueprint("just_peace", "Just Peace", ReignGovernmentPlank.Peace, ReignGovernmentPlank.Justice, ReignGovernmentPlank.RepresentativeAuthority),
            new ReignGovernmentPartyBlueprint("merchant_defence", "Merchant Defence", ReignGovernmentPlank.Trade, ReignGovernmentPlank.Security, ReignGovernmentPlank.Infrastructure),
            new ReignGovernmentPartyBlueprint("war_clans", "War Clans", ReignGovernmentPlank.MilitaryStrength, ReignGovernmentPlank.ClanPrivilege, ReignGovernmentPlank.Expansion)
        };

        public static int SelectStartingLevel(string kingdomId)
        {
            int roll = StablePercent("level|" + (kingdomId ?? string.Empty));
            if (roll < 10) return 1;
            if (roll < 35) return 2;
            if (roll < 65) return 3;
            if (roll < 90) return 4;
            return 5;
        }

        public static int SelectPartyCount(string kingdomId, int occupiedSeats)
        {
            int roll = StablePercent("parties|" + (kingdomId ?? string.Empty));
            if (roll < 45) return 2;
            if (roll < 90) return 3;
            return occupiedSeats >= 12 ? 4 : 3;
        }

        public static IReadOnlyList<ReignGovernmentPartyBlueprint> SelectParties(
            string kingdomId,
            int occupiedSeats)
        {
            int count = SelectPartyCount(kingdomId, occupiedSeats);
            return PartyBlueprints
                .OrderBy(x => StableHash("party|" + (kingdomId ?? string.Empty) + "|" + x.Id))
                .Take(count)
                .ToArray();
        }

        public static ReignGovernmentInstitutionProfile InstitutionForCulture(string cultureId)
        {
            string id = (cultureId ?? string.Empty).Trim().ToLowerInvariant();
            switch (id)
            {
                case "empire":
                case "empire_w":
                case "empire_s":
                case "empire_n":
                    return new ReignGovernmentInstitutionProfile(id, "Imperial Senate", ReignGovernmentInstitutionKind.Senate,
                        ReignGovernmentSeatSource.TownNotable, ReignGovernmentSeatSource.VillageNotable);
                case "vlandia":
                    return new ReignGovernmentInstitutionProfile(id, "Great Council of Peers", ReignGovernmentInstitutionKind.CouncilOfPeers,
                        ReignGovernmentSeatSource.LandholdingClanLeader);
                case "sturgia":
                    return new ReignGovernmentInstitutionProfile(id, "Grand Veche", ReignGovernmentInstitutionKind.Veche,
                        ReignGovernmentSeatSource.LandholdingClanLeader, ReignGovernmentSeatSource.TownMerchantOrArtisan);
                case "battania":
                    return new ReignGovernmentInstitutionProfile(id, "Oenach", ReignGovernmentInstitutionKind.Oenach,
                        ReignGovernmentSeatSource.LandholdingClanLeader, ReignGovernmentSeatSource.VillageHeadmanOrLandowner);
                case "aserai":
                    return new ReignGovernmentInstitutionProfile(id, "Majlis al-Shura", ReignGovernmentInstitutionKind.Majlis,
                        ReignGovernmentSeatSource.LandholdingClanLeader, ReignGovernmentSeatSource.TownMerchant);
                case "khuzait":
                    return new ReignGovernmentInstitutionProfile(id, "Great Kurultai", ReignGovernmentInstitutionKind.Kurultai,
                        ReignGovernmentSeatSource.LandholdingClanLeader);
                case "nord":
                case "nords":
                    return new ReignGovernmentInstitutionProfile(id, "Great Thing", ReignGovernmentInstitutionKind.Thing,
                        ReignGovernmentSeatSource.LandholdingClanLeader, ReignGovernmentSeatSource.TownNotable,
                        ReignGovernmentSeatSource.VillageNotable);
                default:
                    return new ReignGovernmentInstitutionProfile(id, "Council of Estates", ReignGovernmentInstitutionKind.CouncilOfEstates,
                        ReignGovernmentSeatSource.LandholdingClanLeader, ReignGovernmentSeatSource.TownNotable);
            }
        }

        public static ReignGovernmentPartyBlueprint? SelectPartyForMember(
            string memberId,
            ReignGovernmentPersonality personality,
            IReadOnlyList<ReignGovernmentPartyBlueprint> parties)
        {
            if (parties == null || parties.Count == 0) return null;
            personality = personality ?? new ReignGovernmentPersonality();
            return parties
                .OrderByDescending(x => PartyCompatibility(personality, x))
                .ThenBy(x => StableHash("member-party|" + (memberId ?? string.Empty) + "|" + x.Id))
                .First();
        }

        public static int PartyCompatibility(ReignGovernmentPersonality personality, ReignGovernmentPartyBlueprint party)
        {
            if (personality == null || party == null) return 0;
            int score = 0;
            foreach (ReignGovernmentPlank plank in party.Planks)
            {
                switch (plank)
                {
                    case ReignGovernmentPlank.RoyalAuthority: score += personality.Honor * 5 + personality.Calculating * 3 + (personality.IsRulerClanMember ? 18 : 0); break;
                    case ReignGovernmentPlank.RepresentativeAuthority: score += personality.Honor * 4 + personality.Generosity * 4 - (personality.IsRulerClanMember ? 12 : 0); break;
                    case ReignGovernmentPlank.PopularWelfare: score += personality.Mercy * 7 + personality.Generosity * 6; break;
                    case ReignGovernmentPlank.Trade: score += personality.Calculating * 6 + (personality.IsMerchant ? 20 : 0); break;
                    case ReignGovernmentPlank.Agriculture: score += personality.Generosity * 3 + (personality.IsRuralNotable ? 20 : 0); break;
                    case ReignGovernmentPlank.Infrastructure: score += personality.Calculating * 5 + personality.Generosity * 3; break;
                    case ReignGovernmentPlank.MilitaryStrength: score += personality.Valor * 7 + personality.Leadership / 25; break;
                    case ReignGovernmentPlank.Expansion: score += personality.Valor * 7 - personality.Mercy * 3; break;
                    case ReignGovernmentPlank.Peace: score += personality.Mercy * 7 - personality.Valor * 2; break;
                    case ReignGovernmentPlank.Justice: score += personality.Honor * 7 + personality.Mercy * 2; break;
                    case ReignGovernmentPlank.Security: score += personality.Calculating * 4 + personality.Valor * 4; break;
                    case ReignGovernmentPlank.Faith: score += personality.Honor * 4 + personality.Generosity * 2; break;
                    case ReignGovernmentPlank.NoblePrivilege: score += personality.IsLandholdingLord ? 18 : -6; break;
                    case ReignGovernmentPlank.ClanPrivilege: score += personality.IsLandholdingLord ? 14 : 0; break;
                    case ReignGovernmentPlank.LocalAutonomy: score += personality.IsRuralNotable ? 14 : 2; break;
                    case ReignGovernmentPlank.Espionage: score += personality.Calculating * 8 - personality.Honor * 2; break;
                }
            }
            return score;
        }

        public static int SpeakerScore(ReignGovernmentPersonality personality, int governmentLoyalty, int rulerRelation)
        {
            if (personality == null) personality = new ReignGovernmentPersonality();
            return Math.Max(0, personality.Charm) * 2
                + Math.Max(0, personality.Leadership)
                + Clamp(governmentLoyalty, 0, 100)
                + Clamp(rulerRelation, -100, 100) / 2;
        }

        public static ReignGovernmentVoteResult EvaluateLevelReductionVote(ReignGovernmentVoteInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            int ideology = input.PrimaryPlank == ReignGovernmentPlank.RoyalAuthority ? 40
                : input.PrimaryPlank == ReignGovernmentPlank.RepresentativeAuthority ? -40 : 0;
            if (input.SecondaryPlanks != null)
            {
                if (input.SecondaryPlanks.Contains(ReignGovernmentPlank.RoyalAuthority)) ideology += 20;
                if (input.SecondaryPlanks.Contains(ReignGovernmentPlank.RepresentativeAuthority)) ideology -= 20;
            }
            int relation = Clamp(input.RulerRelation, -100, 100) / 2;
            int institutionalCost = 10 + 5 * Clamp(input.CurrentLevel, MinimumLevel, MaximumLevel)
                + (int)Math.Round(15d * Clamp(input.PartySeatShare, 0d, 1d), MidpointRounding.AwayFromZero);
            int disciplineMagnitude = (int)Math.Round(15d * Clamp(input.PartyLoyalty, 0, 100) / 100d, MidpointRounding.AwayFromZero);
            int discipline = input.SpeakerSupportsReduction ? disciplineMagnitude : -disciplineMagnitude;
            int score = ideology + relation - institutionalCost
                + Clamp(input.PersonalityModifier, -15, 15)
                + discipline
                - Clamp(input.GrievancePenalty, 0, 30)
                + Clamp(input.StableVariance, -10, 10);
            return new ReignGovernmentVoteResult(score, score >= 0);
        }

        public static bool ReductionRatified(int votesFor, int occupiedSeats)
        {
            if (occupiedSeats <= 0 || votesFor < 0) return false;
            return (long)votesFor * ReductionApprovalDenominator >= (long)occupiedSeats * ReductionApprovalNumerator;
        }

        public static ReignGovernmentVoteResult EvaluateResolutionVote(ReignGovernmentResolutionVoteInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            int disciplineMagnitude = (int)Math.Round(15d * Clamp(input.PartyLoyalty, 0, 100) / 100d,
                MidpointRounding.AwayFromZero);
            int discipline = input.OwnSpeakerSupports ? disciplineMagnitude : -disciplineMagnitude;
            int speakerContest = Clamp((input.ProposingSpeakerCharm - input.OwnSpeakerCharm) / 20, -15, 15);
            int score = (input.IsProposingPartyMember ? 35 : -10)
                + Clamp(input.SharedPlankCount, 0, 4) * 12
                + (Clamp(input.GovernmentLoyalty, 0, 100) - 50) / 4
                + discipline
                + speakerContest
                + Clamp(input.ProposingSpeakerRelation, -100, 100) / 5
                + Clamp(input.LobbyingShift, -30, 30)
                + Clamp(input.StableVariance, -10, 10);
            return new ReignGovernmentVoteResult(score, score >= 0);
        }

        public static bool ResolutionAdopted(int votesFor, int occupiedSeats)
        {
            return occupiedSeats > 0 && votesFor * 2 > occupiedSeats;
        }

        public static ReignGovernmentDailyEffects DailyEffects(int level)
        {
            switch (Clamp(level, MinimumLevel, MaximumLevel))
            {
                case 2: return new ReignGovernmentDailyEffects(0.10d, 0.25d, 0.05d);
                case 3: return new ReignGovernmentDailyEffects(0.20d, 0.50d, 0.10d);
                case 4: return new ReignGovernmentDailyEffects(0.35d, 0.75d, 0.15d);
                case 5: return new ReignGovernmentDailyEffects(0.50d, 1.00d, 0.25d);
                default: return new ReignGovernmentDailyEffects(0d, 0d, 0d);
            }
        }

        public static ReignGovernmentAuthorityTransition IncreaseTransition(int fromLevel)
        {
            switch (Clamp(fromLevel, MinimumLevel, MaximumLevel))
            {
                case 1: return new ReignGovernmentAuthorityTransition(8, 200, 10, 0.25d, 0.50d, 0.10d, 30, 15, 6, -3);
                case 2: return new ReignGovernmentAuthorityTransition(12, 350, 20, 0.50d, 1.00d, 0.25d, 30, 25, 10, -5);
                case 3: return new ReignGovernmentAuthorityTransition(16, 550, 35, 0.75d, 1.50d, 0.50d, 30, 35, 15, -8);
                case 4: return new ReignGovernmentAuthorityTransition(22, 900, 60, 1.00d, 2.50d, 0.75d, 30, 50, 25, -12);
                default: return new ReignGovernmentAuthorityTransition(0, 0, 0, 0d, 0d, 0d, 0, 0, 0, 0);
            }
        }

        public static ReignGovernmentReductionPenalty ApprovedReductionPenalty(int fromLevel)
        {
            switch (Clamp(fromLevel, MinimumLevel, MaximumLevel))
            {
                case 2: return new ReignGovernmentReductionPenalty(-2, 0, -2, -3);
                case 3: return new ReignGovernmentReductionPenalty(-3, 0, -3, -5);
                case 4: return new ReignGovernmentReductionPenalty(-4, 0, -4, -7);
                case 5: return new ReignGovernmentReductionPenalty(-5, 0, -5, -10);
                default: return new ReignGovernmentReductionPenalty(0, 0, 0, 0);
            }
        }

        public static ReignGovernmentReductionPenalty ForcedReductionPenalty(int fromLevel)
        {
            switch (Clamp(fromLevel, MinimumLevel, MaximumLevel))
            {
                case 2: return new ReignGovernmentReductionPenalty(-20, -20, -10, -40);
                case 3: return new ReignGovernmentReductionPenalty(-35, -35, -20, -60);
                case 4: return new ReignGovernmentReductionPenalty(-55, -50, -30, -80);
                case 5: return new ReignGovernmentReductionPenalty(-70, -70, -40, 0);
                default: return new ReignGovernmentReductionPenalty(0, 0, 0, 0);
            }
        }

        public static ReignGovernmentActionDisposition ActionDisposition(int level, ReignGovernmentActionKind action)
        {
            int bounded = Clamp(level, MinimumLevel, MaximumLevel);
            if (bounded == 1 || action == ReignGovernmentActionKind.Advice) return ReignGovernmentActionDisposition.Advisory;
            if (bounded == 2) return ReignGovernmentActionDisposition.FormalPressure;
            if (bounded == 3) return ReignGovernmentActionDisposition.ReconsiderationDelay;
            if (bounded == 4) return IsMajorAction(action)
                ? ReignGovernmentActionDisposition.ApprovalOrOverride
                : ReignGovernmentActionDisposition.FormalPressure;
            return IsMajorAction(action)
                ? ReignGovernmentActionDisposition.BlockedPendingApprovalOrLevelReduction
                : ReignGovernmentActionDisposition.ApprovalOrOverride;
        }

        public static int ReconsiderationDelayDays(int level)
        {
            return Clamp(level, MinimumLevel, MaximumLevel) == 3 ? 7 : 0;
        }

        public static double ResolutionFailureMultiplier(int level)
        {
            switch (Clamp(level, MinimumLevel, MaximumLevel))
            {
                case 1: return 0.50d;
                case 2: return 0.75d;
                case 3: return 1.00d;
                case 4: return 1.50d;
                default: return 2.00d;
            }
        }

        public static int StableVariance(string memberId, string voteId)
        {
            return (int)(StableHash("vote|" + (memberId ?? string.Empty) + "|" + (voteId ?? string.Empty)) % 21u) - 10;
        }

        public static uint StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261u;
                string text = value ?? string.Empty;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 16777619u;
                }
                return hash;
            }
        }

        private static bool IsMajorAction(ReignGovernmentActionKind action)
        {
            return action == ReignGovernmentActionKind.War
                || action == ReignGovernmentActionKind.Peace
                || action == ReignGovernmentActionKind.Treaty
                || action == ReignGovernmentActionKind.Policy
                || action == ReignGovernmentActionKind.Taxation
                || action == ReignGovernmentActionKind.MajorSpending
                || action == ReignGovernmentActionKind.FiefTransfer
                || action == ReignGovernmentActionKind.MajorJustice;
        }

        private static int StablePercent(string value) => (int)(StableHash(value) % 100u);
        private static int Clamp(int value, int minimum, int maximum) => Math.Max(minimum, Math.Min(maximum, value));
        private static double Clamp(double value, double minimum, double maximum)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return minimum;
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
