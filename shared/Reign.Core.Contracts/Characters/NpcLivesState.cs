using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.Shared.Characters
{
    public sealed class NpcLifeFact
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string OutcomeObjectiveId { get; set; } = "";
        public string RequiredNarrativeOutcome { get; set; } = "";
        public bool Private { get; set; } = true;
        public bool Active { get; set; } = true;
    }
    public sealed class NpcLifeRecord
    {
        public string HeroId { get; set; } = "";
        public int LastRollDay { get; set; } = -1;
        public double NextInvitationDay { get; set; }
        public double NextMajorDay { get; set; }
        public string PendingStoryId { get; set; } = "";
        public string ActiveMajorId { get; set; } = "";
        public List<string> CompletedTemplates { get; set; } = new List<string>();
        public List<string> RecentFamilies { get; set; } = new List<string>();
        public List<NpcLifeFact> Facts { get; set; } = new List<NpcLifeFact>();
    }
    public sealed class NpcConversationEncounter
    {
        public string Id { get; set; } = "";
        public string HeroId { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string LogicalConversationId { get; set; } = "";
        public double? LastAcceptedDay { get; set; }
        public string OpeningOperationId { get; set; } = "";
        public string OpeningText { get; set; } = "";
        public string Activity { get; set; } = "";
        public string Receptiveness { get; set; } = "neutral";
        public long OpeningDeadlineUtcMs { get; set; }
        public bool OpeningPending { get; set; }
        public double OpeningDay { get; set; }
        public string OpeningGeneration { get; set; } = "";
        public string OpeningPayload { get; set; } = "{}";
        public List<string> AcceptedTurns { get; set; } = new List<string>();
        public bool AcceptTurn(string turnId, double day)
        {
            if (string.IsNullOrWhiteSpace(turnId) || !NpcLivesPolicy.Finite(day) || day < 0 || AcceptedTurns.Contains(turnId)
                || (LastAcceptedDay.HasValue && day < LastAcceptedDay.Value)) return false;
            AcceptedTurns.Add(turnId); LastAcceptedDay = day;
            return true;
        }
        public bool BeginOpening(string operationId, double day, double hours)
        {
            if (OpeningPending || string.IsNullOrWhiteSpace(operationId) || !NpcLivesPolicy.NewOpeningDue(LastAcceptedDay, day, hours)) return false;
            OpeningOperationId = operationId; OpeningPending = true; OpeningText = ""; return true;
        }
        public bool AcceptOpening(string operationId, string text, string activity, double day)
        {
            if (!OpeningPending || operationId != OpeningOperationId || string.IsNullOrWhiteSpace(text)
                || !AcceptTurn("opening:" + operationId, day)) return false;
            OpeningText = text; Activity = activity; OpeningPending = false; return true;
        }
    }
    public sealed class NpcStoryObjective
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "discussion";
        public string Instruction { get; set; } = "";
        public string RequiredOutcome { get; set; } = "";
        public int RequiredProgress { get; set; } = 2;
        public List<string> CompletionOutcomes { get; set; } = new List<string>();
        public bool Native => Kind == "visit" || Kind == "item" || Kind == "duel" || Kind == "hideout";
    }
    public sealed class NpcStoryPhase
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string OpeningText { get; set; } = "You join {npc} for a quiet personal moment.";
        public int MinimumAffinity { get; set; }
        public string LocationKind { get; set; } = "current";
        public bool RequiresCoast { get; set; }
        public string Skill { get; set; } = "Charm";
        public int Xp { get; set; } = 25;
        public int Affinity { get; set; } = 1;
        public List<NpcStoryObjective> Objectives { get; set; } = new List<NpcStoryObjective>();
        public List<NpcLifeFact> Facts { get; set; } = new List<NpcLifeFact>();
    }
    public sealed class NpcStoryDefinition
    {
        public string Id { get; set; } = "";
        public int Version { get; set; } = 1;
        public string Title { get; set; } = "";
        public string Category { get; set; } = "brief";
        public string Family { get; set; } = "";
        public string Interest { get; set; } = "";
        public string SupportingRole { get; set; } = "";
        public string RelatedMajorId { get; set; } = "";
        public int SupportingRevealPhase { get; set; } = 1;
        public bool Repeatable { get; set; }
        public List<NpcStoryPhase> Phases { get; set; } = new List<NpcStoryPhase>();
    }
    public sealed class NpcStoryEvidence
    {
        public string ObjectiveId { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string Kind { get; set; } = "conversation";
        public string Outcome { get; set; } = "";
        public string HeroId { get; set; } = "";
        public string LocationId { get; set; } = "";
        public string EncounterId { get; set; } = "";
        public string Quote { get; set; } = "";
        public string ResolutionQuote { get; set; } = "";
        public int Progress { get; set; } = 1;
    }
    public sealed class NpcStoryInstance
    {
        public string Id { get; set; } = "";
        public string HeroId { get; set; } = "";
        public NpcStoryDefinition Definition { get; set; } = new NpcStoryDefinition();
        public int PhaseIndex { get; set; }
        public int Revision { get; set; } = 1;
        public string Status { get; set; } = "invited";
        public string LocationId { get; set; } = "";
        public string LocationName { get; set; } = "";
        public string ProposalId { get; set; } = "";
        public string NativeEncounterId { get; set; } = "";
        public Dictionary<string, string> Bindings { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public string LinkedLocationId { get; set; } = "";
        public string LinkedLocationName { get; set; } = "";
        public List<string> EarlierExperiences { get; set; } = new List<string>();
        public double StartedDay { get; set; }
        public double? CompletedDay { get; set; }
        public List<NpcStoryEvidence> Evidence { get; set; } = new List<NpcStoryEvidence>();
        public List<string> CompletedPhaseIds { get; set; } = new List<string>();
        public bool AcceptEvidence(NpcStoryEvidence evidence, int revision, ISet<string> acceptedSources, bool nativeReceiptVerified, bool narrativeResolutionVerified = false)
        {
            if (Status != "active" || revision != Revision || PhaseIndex >= Definition.Phases.Count || evidence.HeroId != HeroId || string.IsNullOrWhiteSpace(evidence.SourceId)) return false;
            var objective = Definition.Phases[PhaseIndex].Objectives.FirstOrDefault(o => o.Id == evidence.ObjectiveId);
            if (objective == null || Evidence.Any(e => e.ObjectiveId == evidence.ObjectiveId && e.SourceId == evidence.SourceId)) return false;
            if (objective.Native)
            {
                if (!nativeReceiptVerified || evidence.Kind != "native" || (LocationId.Length > 0 && evidence.LocationId != LocationId)
                    || ((objective.Kind == "duel" || objective.Kind == "hideout") && (NativeEncounterId.Length == 0 || evidence.EncounterId != NativeEncounterId))) return false;
            }
            else if (evidence.Kind != "conversation" || string.IsNullOrWhiteSpace(evidence.Quote) || !acceptedSources.Contains(evidence.SourceId)
                || evidence.Progress != 1 || Evidence.Any(e => e.ObjectiveId == evidence.ObjectiveId && string.Equals(e.Quote.Trim(), evidence.Quote.Trim(), StringComparison.OrdinalIgnoreCase))) return false;
            if (!objective.Native && evidence.Outcome != "" && evidence.Outcome != "progress"
                && (!objective.CompletionOutcomes.Contains(evidence.Outcome) || !narrativeResolutionVerified || string.IsNullOrWhiteSpace(evidence.ResolutionQuote))) return false;
            if (objective.RequiredOutcome.Length > 0 && objective.RequiredOutcome != evidence.Outcome) return false;
            Evidence.Add(evidence); return true;
        }
        public bool PhaseComplete => Status == "active" && PhaseIndex < Definition.Phases.Count
            && Definition.Phases[PhaseIndex].Objectives.Count > 0
            && Definition.Phases[PhaseIndex].Objectives.All(o => o.Native ? Evidence.Any(e => e.ObjectiveId == o.Id)
                : Evidence.Where(e => e.ObjectiveId == o.Id).Select(e => e.SourceId).Distinct().Count() >= Math.Max(1, o.RequiredProgress)
                    && (o.CompletionOutcomes.Count == 0 || Evidence.Any(e => e.ObjectiveId == o.Id
                        && o.CompletionOutcomes.Contains(e.Outcome) && !string.IsNullOrWhiteSpace(e.ResolutionQuote))));
    }
    public sealed class NpcLifeEffect
    {
        public string Id { get; set; } = "";
        public string StoryId { get; set; } = "";
        public string HeroId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Skill { get; set; } = "";
        public int Amount { get; set; }
        public string Status { get; set; } = "pending";
        public string NativeReceipt { get; set; } = "";
    }
    public sealed class NpcPersonalLeave
    {
        public string Id { get; set; } = "";
        public string HeroId { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string DestinationId { get; set; } = "";
        public int Revision { get; set; } = 1;
        public string Status { get; set; } = "discussing";
        public string ProposalId { get; set; } = "";
        public double? DepartureDay { get; set; }
        public double? DepartedDay { get; set; }
        public double AbsenceDays { get; set; } = 5;
        public double? ReturnDay { get; set; }
        public double? ReconsiderDay { get; set; }
        public bool IndependentDecision { get; set; }
        public int Trust { get; set; }
        public double Honor { get; set; } = 50;
        public double Compassion { get; set; } = 50;
        public int Urgency { get; set; } = 1;
        public int DutyPressure { get; set; }
        public int RefusalMitigation { get; set; }
        public string LastArgumentResult { get; set; } = "";
        public List<string> AcceptedArgumentIds { get; set; } = new List<string>();
        public bool AgreeDeparture(string proposalId, int revision, double now, double waitDays, double awayDays, bool independent = false)
        {
            if (revision != Revision || proposalId != ProposalId || string.IsNullOrWhiteSpace(proposalId)
                || (Status != "discussing" && Status != "reconsider") || !NpcLivesPolicy.Finite(now) || now < 0
                || !NpcLivesPolicy.Finite(waitDays) || waitDays < 0 || !NpcLivesPolicy.Finite(awayDays) || awayDays <= 0) return false;
            DepartureDay = now + waitDays; AbsenceDays = awayDays; ReconsiderDay = null; IndependentDecision = independent; Status = "waiting"; return true;
        }
        public bool StayForNow(int revision, double now, double reaskDays)
        {
            if (revision != Revision || Status != "discussing" || !NpcLivesPolicy.Finite(now) || !NpcLivesPolicy.Finite(reaskDays) || reaskDays <= 0) return false;
            DepartureDay = null; ReconsiderDay = now + reaskDays; Status = "reconsider"; Revision++; return true;
        }
        public bool Depart(double day, bool safe)
        {
            if (!safe || Status != "waiting" || !DepartureDay.HasValue || !NpcLivesPolicy.Finite(day) || day < DepartureDay.Value) return false;
            DepartedDay = day; ReturnDay = day + AbsenceDays; Status = "away"; return true;
        }
        public bool Return(double day, bool available)
        {
            if (!available || Status != "away" || !ReturnDay.HasValue || !NpcLivesPolicy.Finite(day) || day < ReturnDay.Value) return false;
            Status = "returned"; return true;
        }
        public bool Reopen(double day)
        {
            if (Status != "reconsider" || !ReconsiderDay.HasValue || !NpcLivesPolicy.Finite(day) || day < ReconsiderDay.Value) return false;
            Status = "discussing"; ReconsiderDay = null; AcceptedArgumentIds.Clear(); Revision++; return true;
        }
    }
    public sealed class NpcPartyAnnouncement
    {
        public string Id { get; set; } = "";
        public string PlayerId { get; set; } = "";
        public string Text { get; set; } = "";
        public double WorldDay { get; set; }
        public string LocationId { get; set; } = "";
        public List<string> RecipientIds { get; set; } = new List<string>();
        public Dictionary<string, string> Reactions { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> ReviewChoices { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public bool CanRead(string heroId) => heroId == PlayerId || RecipientIds.Contains(heroId);
        public bool ReactionPending(string heroId) => RecipientIds.Contains(heroId) && !Reactions.ContainsKey(heroId);
        public bool MarkReacted(string heroId, string acceptedTurnId, bool visiblyAddressed)
        {
            return MarkReviewed(heroId, acceptedTurnId, "commented", visiblyAddressed);
        }
        public bool MarkReviewed(string heroId, string acceptedTurnId, string choice, bool responseAccepted)
        {
            if (!responseAccepted || (choice != "commented" && choice != "no_comment")
                || string.IsNullOrWhiteSpace(acceptedTurnId) || !ReactionPending(heroId)) return false;
            Reactions.Add(heroId, acceptedTurnId); ReviewChoices[heroId] = choice; return true;
        }
    }
    public sealed class NpcSummonedMeeting
    {
        // Missing in historical saves: keep their mutually accepted party-attendance terms.
        public bool SoloAttendance { get; set; }
        public string Id { get; set; } = "";
        public string IssuerId { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string DestinationId { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string ProposalId { get; set; } = "";
        public int Revision { get; set; } = 1;
        public string Status { get; set; } = "agreed";
        public double StayDays { get; set; } = 3;
        public double? ArrivalDelayDays { get; set; }
        public double? ScheduledArrivalDay { get; set; }
        public double? DeliveryDay { get; set; }
        public double? ArrivalDay { get; set; }
        public double? ReleaseDay { get; set; }
        public string Interruption { get; set; } = "";
        public NpcSummonedMeeting Revise(string proposalId, string destinationId, double stayDays,
            double day, double arrivalDelayDays, bool bothPresent)
        {
            if ((Status != "agreed" && Status != "dispatched" && Status != "travelling" && Status != "meeting")
                || string.IsNullOrWhiteSpace(proposalId) || string.IsNullOrWhiteSpace(destinationId)
                || !NpcLivesPolicy.Finite(day) || day < 0 || !NpcLivesPolicy.Finite(stayDays)
                || stayDays < .25 || stayDays > 31.5 || !NpcLivesPolicy.Finite(arrivalDelayDays)
                || arrivalDelayDays < 0 || arrivalDelayDays > 126 || Revision == int.MaxValue)
                throw new InvalidOperationException("The meeting revision needs valid active terms.");
            var revised = new NpcSummonedMeeting { Id = Id, IssuerId = IssuerId, TargetId = TargetId,
                DestinationId = destinationId, Purpose = Purpose, ProposalId = proposalId, Revision = Revision + 1,
                StayDays = stayDays, SoloAttendance = SoloAttendance };
            bool continuing = Status == "meeting" && bothPresent && destinationId == DestinationId && arrivalDelayDays == 0
                && ArrivalDay.HasValue && NpcLivesPolicy.Finite(ArrivalDay.Value) && ArrivalDay.Value <= day
                && ReleaseDay.HasValue && day < ReleaseDay.Value;
            if (continuing)
            {
                revised.Status = "meeting"; revised.ArrivalDelayDays = ArrivalDelayDays;
                revised.ScheduledArrivalDay = ScheduledArrivalDay; revised.DeliveryDay = DeliveryDay;
                revised.ArrivalDay = ArrivalDay; revised.ReleaseDay = ArrivalDay.GetValueOrDefault() + stayDays;
            }
            else if (!revised.ScheduleArrival(day, arrivalDelayDays))
                throw new InvalidOperationException("The revised meeting needs a valid fixed arrival wait.");
            return revised;
        }
        public bool ScheduleArrival(double day, double delay)
        {
            if (Status != "agreed" || ArrivalDay.HasValue || ScheduledArrivalDay.HasValue
                || !NpcLivesPolicy.Finite(day) || day < 0 || !NpcLivesPolicy.Finite(delay) || delay < 0 || delay > 126) return false;
            ArrivalDelayDays = delay; ScheduledArrivalDay = day + delay; return true;
        }
        public bool Arrive(string targetId, string destinationId, double day, bool available)
        {
            if (!available || targetId != TargetId || destinationId != DestinationId || ArrivalDay.HasValue
                || !NpcLivesPolicy.Finite(day) || day < 0 || !NpcLivesPolicy.Finite(StayDays) || StayDays <= 0
                || (ScheduledArrivalDay.HasValue && day + 1e-9 < ScheduledArrivalDay.Value)
                || (Status != "agreed" && Status != "dispatched" && Status != "travelling")) return false;
            ArrivalDay = day; ReleaseDay = day + StayDays; Status = "meeting"; return true;
        }
        public bool NeedsHold(string heroId, double day) => NpcLivesPolicy.Finite(day) && (heroId == IssuerId || heroId == TargetId)
            && ((heroId == IssuerId && (Status == "agreed" || Status == "dispatched" || Status == "travelling"))
                || (Status == "meeting" && ReleaseDay.HasValue && day < ReleaseDay.Value));
        public bool Complete(double day)
        {
            if (Status != "meeting" || !ReleaseDay.HasValue || !NpcLivesPolicy.Finite(day) || day < ReleaseDay.Value) return false;
            Status = "completed"; return true;
        }
    }
    public sealed class NpcLivesState
    {
        public int Version { get; set; } = 1;
        public string CampaignId { get; set; } = "";
        public string TimelineId { get; set; } = "";
        public long Revision { get; set; }
        public List<NpcLifeRecord> Characters { get; set; } = new List<NpcLifeRecord>();
        public List<NpcConversationEncounter> Encounters { get; set; } = new List<NpcConversationEncounter>();
        public List<NpcStoryInstance> Stories { get; set; } = new List<NpcStoryInstance>();
        public List<NpcLifeEffect> Effects { get; set; } = new List<NpcLifeEffect>();
        public List<NpcPersonalLeave> Leaves { get; set; } = new List<NpcPersonalLeave>();
        public List<NpcPartyAnnouncement> Announcements { get; set; } = new List<NpcPartyAnnouncement>();
        public List<NpcSummonedMeeting> Meetings { get; set; } = new List<NpcSummonedMeeting>();
        public List<string> RecentTemplates { get; set; } = new List<string>();
        public List<string> RecentBriefFamilies { get; set; } = new List<string>();
        public List<string> UsedMajorTemplates { get; set; } = new List<string>();
        public Dictionary<string, string> OperationReceipts { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public NpcLifeRecord Character(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A character identity is required.", nameof(id));
            var record = Characters.FirstOrDefault(c => c.HeroId == id);
            if (record == null) { record = new NpcLifeRecord { HeroId = id }; Characters.Add(record); }
            return record;
        }
        public bool Advance(NpcStoryInstance story, double day, NpcLivesOptions? options = null)
        {
            if (!Stories.Contains(story) || !story.PhaseComplete || !NpcLivesPolicy.Finite(day) || day < story.StartedDay) return false;
            var phase = story.Definition.Phases[story.PhaseIndex];
            if (story.CompletedPhaseIds.Contains(phase.Id)) return false;
            story.CompletedPhaseIds.Add(phase.Id);
            foreach (var award in new[] { new NpcLifeEffect { Kind = "skill_xp", Amount = phase.Xp, Skill = phase.Skill }, new NpcLifeEffect { Kind = "affinity", Amount = phase.Affinity } })
            {
                award.Id = story.Id + ":" + phase.Id + ":" + award.Kind; award.HeroId = story.HeroId; award.StoryId = story.Id;
                award.Amount = (int)Math.Round(award.Amount * (options?.Scale(award.Kind == "skill_xp" ? "XpReward" : "AffinityReward") ?? 1), MidpointRounding.AwayFromZero);
                if (award.Amount != 0 && !Effects.Any(e => e.Id == award.Id)) Effects.Add(award);
            }
            var character = Character(story.HeroId);
            string memoryId = story.Id + ":" + phase.Id + ":memory";
            if (!character.Facts.Any(f => f.Id == memoryId))
            {
                string memory = "You shared this completed personal experience with the player: " + phase.Title + ". Remember the accepted exchange; do not replay it as a new request.";
                if (phase.Objectives.Any(o => o.RequiredOutcome == "rescued") && story.Bindings.TryGetValue("heroName", out var friend))
                    memory = "The player helped you rescue " + friend + ", your " + story.Definition.SupportingRole + ". This is a completed native rescue, not an unresolved request.";
                else if (phase.Objectives.Any(o => o.RequiredOutcome == "recovered") && story.Bindings.TryGetValue("itemName", out var item))
                    memory = "The player helped recover your stolen heirloom, " + item + ". It was placed in the party inventory; later possession must follow current native inventory.";
                else if (phase.Objectives.Any(o => o.Kind == "duel"))
                    memory = "You and the player completed a practice duel for " + phase.Title + ". The verified player outcome was "
                        + (story.Evidence.LastOrDefault(e => e.Kind == "native" && e.Outcome.Length > 0)?.Outcome ?? "participation") + ".";
                character.Facts.Add(new NpcLifeFact { Id = memoryId, Kind = "experience", Text = memory, SourceId = story.Id });
            }
            if (story.Definition.RelatedMajorId.Length > 0 && !character.Facts.Any(f => f.Id == story.Id + ":shared_experience"))
                character.Facts.Add(new NpcLifeFact { Id = story.Id + ":shared_experience", SourceId = story.Id, Kind = "experience",
                    Text = "You and the player shared a personal experience: " + phase.Title + "."
                        + (story.LinkedLocationName.Length > 0 ? " The place connected with this interest is " + story.LinkedLocationName + "." : "")
                        + " Preserve what was established in the accepted conversation; do not invent later disclosures or completed native events." });
            foreach (var fact in phase.Facts)
                if ((fact.RequiredNarrativeOutcome.Length == 0 || story.Evidence.Any(e => e.ObjectiveId == fact.OutcomeObjectiveId
                        && e.Outcome == fact.RequiredNarrativeOutcome && !string.IsNullOrWhiteSpace(e.ResolutionQuote)))
                    && !character.Facts.Any(f => f.Id == story.Id + ":" + fact.Id))
                    character.Facts.Add(new NpcLifeFact { Id = story.Id + ":" + fact.Id, Kind = fact.Kind, Text = fact.Text, SourceId = story.Id, Private = fact.Private });
            story.PhaseIndex++; story.Revision++; story.NativeEncounterId = ""; story.ProposalId = "";
            if (story.PhaseIndex < story.Definition.Phases.Count) { story.Status = "between_phases"; return true; }
            story.Status = "completed"; story.CompletedDay = day; character.CompletedTemplates.Add(story.Definition.Id);
            character.PendingStoryId = ""; if (character.ActiveMajorId == story.Id) character.ActiveMajorId = "";
            character.RecentFamilies.Add(story.Definition.Family); while (character.RecentFamilies.Count > 3) character.RecentFamilies.RemoveAt(0);
            RecentTemplates.Add(story.Definition.Id); while (RecentTemplates.Count > 20) RecentTemplates.RemoveAt(0);
            return true;
        }
    }
}
