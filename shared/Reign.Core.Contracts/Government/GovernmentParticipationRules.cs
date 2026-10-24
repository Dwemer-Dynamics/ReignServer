using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Government
{
    public static class GovernmentParticipationBalance
    {
        public const int Version = 2;
        public const int OpenPetitionsPerAuthor = 3;
        public const int SponsorshipExpiryIntervals = 2;
        public const int ReintroductionIntervals = 2;
        public const int LeadershipChallengeIntervals = 2;
        public const double TerritorialStabilityDays = 1;
        public const int PartySwitchAdvantage = 15;
        public static int ContributionReference(int positiveDailyIncome, int unreservedGold, bool sponsorship) =>
            PoliticalServiceReference(positiveDailyIncome, unreservedGold, 0,
                sponsorship ? "government.sponsor_petition" : "government.endorse_petition");

        public static int PoliticalServiceReference(int positiveDailyIncome, int unreservedGold, int realmFiefs, string actionId)
        {
            int multiplier;
            switch (actionId)
            {
                case "government.endorse_petition": multiplier = 1; break;
                case "government.sponsor_petition": multiplier = 4; break;
                case "government.seek_petition_adoption": multiplier = 6; break;
                case "government.support_nomination": multiplier = 2; break;
                case "government.present_petition": multiplier = 2; break;
                case "government.transfer_mandate": multiplier = 12; break;
                default: throw new ArgumentException("This political service has no reference price.", nameof(actionId));
            }
            // A meaningful floor, a bounded week of income/5% of available wealth,
            // and the realm's territorial stake. Never an admission fee or consent.
            long means = Math.Max(Math.Max(0L, positiveDailyIncome) * 7, Math.Max(0L, unreservedGold) / 20);
            long stake = Math.Max(0L, realmFiefs) * 250;
            return (int)((2000 + Math.Min(8000, means) + Math.Min(5000, stake)) * multiplier);
        }
        public static int Willingness(int alignment, int conduct, int relation, int argument, int material, int politicalCost) =>
            Clamp(alignment, -40, 40) + Clamp(conduct, -15, 15) + Clamp(relation, -10, 10)
            + Clamp(argument, 0, 10) + Clamp(material, 0, 15) - Clamp(politicalCost, 0, 25);
        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    }

    public sealed class GovernmentConstitution
    {
        public string KingdomId { get; set; } = "";
        public string InstitutionId { get; set; } = "";
        public string Source { get; set; } = "migration";
        public bool Confirmed { get; set; } = true;
        public int DraftAuthority { get; set; } = 1;
        public long Revision { get; set; } = 1;
    }

    public sealed class GovernmentConstituency
    {
        public string Id { get; set; } = "";
        public string KingdomId { get; set; } = "";
        public string SettlementId { get; set; } = "";
        public string ClanId { get; set; } = "";
        public ReignGovernmentSeatSource Source { get; set; }
        public string IncumbentId { get; set; } = "";
        public string Mandate { get; set; } = "ordinary";
        public string Status { get; set; } = "pending";
        public double EligibleSinceDay { get; set; }
        public double AccreditedDay { get; set; } = -1;
        public int AccreditedInterval { get; set; } = -1;
        public string AdmissionPetitionId { get; set; } = "";
    }

    public sealed class GovernmentPetitionConsent
    {
        public string ActorId { get; set; } = "";
        public string Operation { get; set; } = "";
        public long Revision { get; set; }
        public string ReceiptId { get; set; } = "";
        public int ExpiresInterval { get; set; }
        public string PartyId { get; set; } = "";
        public int MaterialBenefit { get; set; }
    }

    public sealed class GovernmentPetition
    {
        public string Id { get; set; } = "petition_" + Guid.NewGuid().ToString("N");
        public string KingdomId { get; set; } = "";
        public string AuthorId { get; set; } = "";
        public string ProposalId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public string Terms { get; set; } = "";
        public string ExactPayloadJson { get; set; } = "";
        public string Argument { get; set; } = "";
        public string ConstituencyId { get; set; } = "";
        public string CandidateId { get; set; } = "";
        public string PreviousIncumbentId { get; set; } = "";
        public long Revision { get; set; } = 1;
        public string Status { get; set; } = "draft";
        public string BlockReason { get; set; } = "";
        public int CreatedInterval { get; set; }
        public int SubmittedInterval { get; set; } = -1;
        public int ResolvedInterval { get; set; } = -1;
        public string BusinessId { get; set; } = "";
        public string AdoptedPartyId { get; set; } = "";
        public List<string> DisclosedTo { get; set; } = new List<string>();
        public List<string> FrozenEndorsers { get; set; } = new List<string>();
        public List<GovernmentPetitionConsent> Consents { get; set; } = new List<GovernmentPetitionConsent>();
    }

    public sealed class GovernmentPartyCaucus
    {
        public string KingdomId { get; set; } = "";
        public string PartyId { get; set; } = "";
        public int TargetInterval { get; set; }
        public List<string> Roll { get; set; } = new List<string>();
        public List<string> Nominations { get; set; } = new List<string>();
        public Dictionary<string, string> Endorsements { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, long> EndorsementRevisions { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);
        public List<GovernmentCaucusBallot> SelectionBallots { get; set; } = new List<GovernmentCaucusBallot>();
        public bool Closed { get; set; }
        public string AdoptedPetitionId { get; set; } = "";
    }

    public sealed class GovernmentCaucusBallot
    {
        public string ActorId { get; set; } = "";
        public string PetitionId { get; set; } = "";
        public long PetitionRevision { get; set; }
        public bool Eligible { get; set; }
    }

    public sealed class GovernmentPartyElection
    {
        public string Id { get; set; } = "";
        public string KingdomId { get; set; } = "";
        public string PartyId { get; set; } = "";
        public string IncumbentId { get; set; } = "";
        public int OpenInterval { get; set; }
        public int CloseInterval { get; set; }
        public List<string> Roll { get; set; } = new List<string>();
        public Dictionary<string, List<string>> Nominations { get; set; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public Dictionary<string, string> Ballots { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> ActionReceipts { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public bool Closed { get; set; }
        public string WinnerId { get; set; } = "";
        public bool Opened { get; set; }
    }

    public sealed class GovernmentMemberBallot
    {
        public string BusinessId { get; set; } = "";
        public string HeroId { get; set; } = "";
        public string OptionId { get; set; } = "";
        public bool Abstain { get; set; }
        public string Source { get; set; } = "member";
        public double SubmittedDay { get; set; }
    }

    public sealed class GovernmentDocketReference
    {
        public string KingdomId { get; set; } = "";
        public string BusinessId { get; set; } = "";
        public string Alias { get; set; } = "";
        public double PresentationDay { get; set; }
        public int Index { get; set; }
    }

    public sealed class GovernmentParticipationState
    {
        public int Version { get; set; } = 1;
        public List<GovernmentConstitution> Constitutions { get; set; } = new List<GovernmentConstitution>();
        public List<GovernmentConstituency> Constituencies { get; set; } = new List<GovernmentConstituency>();
        public List<GovernmentPetition> Petitions { get; set; } = new List<GovernmentPetition>();
        public List<GovernmentPartyCaucus> Caucuses { get; set; } = new List<GovernmentPartyCaucus>();
        public List<GovernmentPartyElection> Elections { get; set; } = new List<GovernmentPartyElection>();
        public List<GovernmentMemberBallot> Ballots { get; set; } = new List<GovernmentMemberBallot>();
        public List<GovernmentDocketReference> Dockets { get; set; } = new List<GovernmentDocketReference>();
        public List<GovernmentPoliticalProfile> Profiles { get; set; } = new List<GovernmentPoliticalProfile>();
        public List<GovernmentPoliticalWorldEvidence> PoliticalEvents { get; set; } = new List<GovernmentPoliticalWorldEvidence>();
        public List<GovernmentPoliticalValue> PlayerDeclarations { get; set; } = new List<GovernmentPoliticalValue>();
        public Dictionary<string, string> SwitchCandidates { get; set; } = new Dictionary<string, string>();
    }

    public sealed class GovernmentAccess
    {
        public bool Inspect { get; set; }
        public bool Prepare { get; set; }
        public bool Member { get; set; }
        public bool Speaker { get; set; }
        public bool Crown { get; set; }
        public bool Ballot { get; set; }
        public bool CharterPending { get; set; }
        public string Reason { get; set; } = "";
    }

    public static class GovernmentParticipationRules
    {
        private static readonly string[] Cultures = { "empire", "vlandia", "sturgia", "battania", "aserai", "khuzait", "nord", "other" };
        public static IReadOnlyList<ReignGovernmentInstitutionProfile> Institutions { get; } = Cultures.Select(ReignGovernmentRules.InstitutionForCulture).ToArray();
        public static ReignGovernmentInstitutionProfile Institution(string id) => Institutions.First(x => x.Kind.ToString() == id);
        public static string Title(ReignGovernmentInstitutionKind kind) => kind == ReignGovernmentInstitutionKind.Senate ? "Senator"
            : kind == ReignGovernmentInstitutionKind.CouncilOfPeers ? "Peer" : kind == ReignGovernmentInstitutionKind.Thing ? "Assembly member" : "Councillor";
        public static string ConstituencyId(ReignGovernmentSeatSource source, string settlementId, string clanId) =>
            source == ReignGovernmentSeatSource.LandholdingClanLeader ? "clan:" + clanId : "settlement:" + settlementId + ":" + source;
        public static int Interval(double day) => (int)Math.Floor(day / GovernmentSessionRules.SeasonDays) * 2
            + (day % GovernmentSessionRules.SeasonDays >= 29 ? 2 : day % GovernmentSessionRules.SeasonDays >= 14 ? 1 : 0);
        public static string DocketAlias(int zeroBasedIndex)
        {
            if (zeroBasedIndex < 0) throw new ArgumentOutOfRangeException(nameof(zeroBasedIndex));
            string label = ""; long number = (long)zeroBasedIndex + 1;
            while (number > 0) { number--; label = (char)('A' + number % 26) + label; number /= 26; }
            return label;
        }
        public static bool CanConfirmCharter(int level, int eligibleMembers) => level >= 1 && level <= 5 && (level < 5 || eligibleMembers > 0);
        public static bool Closed(string status) => status == "rejected" || status == "withdrawn" || status == "expired"
            || status == "completed" || status == "active" || status == "failed";
        public static bool Public(GovernmentPetition petition) => petition.Status == "scheduled" || petition.Status == "presented"
            || petition.Status == "rejected" || petition.Status == "completed" || petition.Status == "active";
        public static bool CanRead(GovernmentPetition petition, string actorId) => Public(petition) || petition.AuthorId == actorId || petition.DisclosedTo.Contains(actorId);
        public static bool SemanticRevision(GovernmentPetition petition, string authorId, string proposalId, string title, string terms, string argument)
        {
            if (petition.AuthorId != authorId || Closed(petition.Status) || petition.Status == "presented" || petition.BusinessId.Length > 0) return false;
            if (petition.ProposalId != proposalId || petition.Terms != terms)
            { petition.Revision++; petition.Consents.Clear(); petition.FrozenEndorsers.Clear(); petition.AdoptedPartyId = ""; petition.Status = "draft"; }
            petition.ProposalId = proposalId; petition.Title = title; petition.Terms = terms; petition.Argument = argument; return true;
        }
        public static bool CanSubmit(GovernmentPetition petition, IReadOnlyList<GovernmentPetition> all, int interval, out string reason)
        {
            reason = "The petition is no longer a private draft.";
            if (petition.Status != "draft" && petition.Status != "blocked") return false;
            var others = all.Where(x => x.Id != petition.Id && x.KingdomId == petition.KingdomId).ToList();
            reason = "You already have three submitted, unresolved petitions in this kingdom.";
            if (others.Count(x => x.AuthorId == petition.AuthorId && x.Status != "draft" && !Closed(x.Status)) >= GovernmentParticipationBalance.OpenPetitionsPerAuthor) return false;
            reason = "An identical petition is already pending; support the existing petition.";
            if (others.Any(x => x.ProposalId == petition.ProposalId && !Closed(x.Status) && x.Status != "draft")) return false;
            reason = "A rejected measure must wait two regular sessions before reintroduction.";
            if (others.Any(x => x.ProposalId == petition.ProposalId && x.Status == "rejected" && interval < x.ResolvedInterval + 2)) return false;
            reason = ""; return true;
        }
        public static bool AddConsent(GovernmentPetition petition, string actorId, string operation, long revision, string receiptId,
            int interval, string partyId, out string reason)
        {
            reason = "The proposal or its revision is no longer available.";
            if (Closed(petition.Status) || petition.Status == "presented" || revision != petition.Revision || !CanRead(petition, actorId)) return false;
            if (string.IsNullOrWhiteSpace(receiptId) || string.IsNullOrWhiteSpace(actorId)) return false;
            if (petition.Consents.Any(x => x.ReceiptId == receiptId)) { reason = "The original consent is already recorded."; return true; }
            petition.Consents.RemoveAll(x => x.ActorId == actorId && x.Operation == operation);
            petition.Consents.Add(new GovernmentPetitionConsent { ActorId = actorId, Operation = operation, Revision = revision,
                ReceiptId = receiptId, PartyId = partyId, ExpiresInterval = interval + GovernmentParticipationBalance.SponsorshipExpiryIntervals });
            reason = "Consent recorded for this exact proposal revision."; return true;
        }
        public static bool HasConsent(GovernmentPetition petition, string actorId, string operation, int interval) =>
            petition.Consents.Any(x => x.ActorId == actorId && x.Operation == operation && x.Revision == petition.Revision && interval < x.ExpiresInterval);
        public static bool MandateSupported(GovernmentPetition petition, IReadOnlyCollection<string> eligible, int interval, bool recall)
        {
            if (eligible.Count == 0) return false;
            int count = eligible.Count(x => HasConsent(petition, x, "endorse", interval));
            return recall ? count * 3 >= eligible.Count * 2 : count * 2 > eligible.Count
                && (petition.PreviousIncumbentId.Length == 0 || HasConsent(petition, petition.PreviousIncumbentId, "transfer", interval));
        }
        public static string CaucusId(GovernmentPartyCaucus caucus) => caucus.KingdomId + "|" + caucus.PartyId + "|"
            + caucus.TargetInterval.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static bool EndorseNomination(GovernmentPartyCaucus caucus, string actor, string petitionId, IReadOnlyCollection<string> current, long revision = 0)
        {
            if (caucus.Closed || !caucus.Roll.Contains(actor) || !current.Contains(actor) || !caucus.Nominations.Contains(petitionId)) return false;
            caucus.Endorsements[actor] = petitionId; caucus.EndorsementRevisions[actor] = revision; return true;
        }
        public static string SelectNomination(GovernmentPartyCaucus caucus, IReadOnlyCollection<string> current, IReadOnlyCollection<string> validPetitions,
            IReadOnlyDictionary<string, long>? revisions = null)
        {
            if (caucus.Closed) return caucus.AdoptedPetitionId;
            // Only the actual selection creates final ballots. Legacy closed caucuses
            // keep their historical outcome without fabricated revision evidence.
            if (revisions != null)
                caucus.SelectionBallots = caucus.Roll.Distinct(StringComparer.Ordinal).Select(actor => {
                    bool eligible = current.Contains(actor);
                    bool valid = eligible && caucus.Endorsements.TryGetValue(actor, out string? id)
                        && validPetitions.Contains(id) && revisions.TryGetValue(id, out long revision)
                        && caucus.EndorsementRevisions.TryGetValue(actor, out long endorsed) && endorsed == revision;
                    string petition = valid ? caucus.Endorsements[actor] : "";
                    return new GovernmentCaucusBallot { ActorId = actor, Eligible = eligible, PetitionId = petition,
                        PetitionRevision = valid ? revisions[petition] : 0 };
                }).ToList();
            int rollCount = caucus.Roll.Distinct(StringComparer.Ordinal).Count();
            caucus.AdoptedPetitionId = caucus.Nominations.Where(validPetitions.Contains).FirstOrDefault(id =>
                (revisions == null
                    ? caucus.Roll.Distinct(StringComparer.Ordinal).Count(member => current.Contains(member) && caucus.Endorsements.TryGetValue(member, out string? vote) && vote == id)
                    : caucus.SelectionBallots.Count(v => v.Eligible && v.PetitionId == id)) * 2 > rollCount) ?? "";
            caucus.Closed = true; return caucus.AdoptedPetitionId;
        }

        public static bool ObserveNominationVote(GovernmentPartyCaucus? caucus, string caucusId, string actor,
            string petitionId, long revision, bool currentlyEligible)
        {
            if (caucus == null || string.IsNullOrWhiteSpace(caucusId) || CaucusId(caucus) != caucusId
                || string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(petitionId) || revision < 1)
                throw new InvalidOperationException("The exact promised party selection is unavailable.");
            if (caucus.Closed)
            {
                var ballots = caucus.SelectionBallots.Where(x => x.ActorId == actor).ToList();
                if (ballots.Count != 1 || !ballots[0].Eligible || ballots[0].PetitionId != petitionId || ballots[0].PetitionRevision != revision)
                    throw new InvalidOperationException("The party selection closed without the exact promised vote.");
                return true;
            }
            if (!currentlyEligible) throw new InvalidOperationException("The promised caucus member is no longer eligible for this nomination.");
            return false;
        }
        public static string CloseElection(GovernmentPartyElection election, IReadOnlyCollection<string> current)
        {
            if (election.Closed) return election.WinnerId;
            int required = election.Roll.Count == 1 ? 1 : 2;
            var ranked = election.Nominations.Where(x => current.Contains(x.Key) && x.Value.Distinct().Count(v => election.Roll.Contains(v) && current.Contains(v)) >= required)
                .Select(x => new { Id = x.Key, Votes = election.Roll.Count(v => current.Contains(v) && election.Ballots.TryGetValue(v, out string? id) && id == x.Key) })
                .OrderByDescending(x => x.Votes).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
            bool incumbent = current.Contains(election.IncumbentId);
            election.WinnerId = ranked.Count > 0 && (ranked[0].Votes * 2 > election.Roll.Count || !incumbent && ranked[0].Votes > 0)
                ? ranked[0].Id : incumbent ? election.IncumbentId : current.OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault() ?? "";
            election.Closed = true; return election.WinnerId;
        }
    }
}
