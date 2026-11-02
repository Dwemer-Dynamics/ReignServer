using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.Shared.Characters
{
    public sealed class NpcLivesSetting
    {
        public string Key { get; }
        public string Label { get; }
        public double Default { get; }
        public double Minimum { get; }
        public double Maximum { get; }
        public double Step { get; }
        public string Unit { get; }
        public NpcLivesSetting(string key, string label, double value, double minimum, double maximum, double step, string unit)
        { Key = key; Label = label; Default = value; Minimum = minimum; Maximum = maximum; Step = step; Unit = unit; }
        public bool Accepts(double value) => NpcLivesPolicy.Finite(value) && value >= Minimum && value <= Maximum
            && Math.Abs((value - Minimum) / Step - Math.Round((value - Minimum) / Step)) < 0.000001;
    }

    /// <summary>One immutable settings snapshot is used for each scheduling decision.</summary>
    public sealed class NpcLivesOptions
    {
        public const string MasterKey = "npcLivesEnabled";
        public const string InitialApproachKey = "npcLivesInitialApproachEnabled";
        public static readonly IReadOnlyDictionary<string, string> Switches = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MasterKey] = "Enable NPC Lives and Storylines",
            [InitialApproachKey] = "NPC speaks first when approached",
            ["npcLivesApproachEnabled"] = "Approach activity LLM calls",
            ["npcLivesStoriesEnabled"] = "Personal stories",
            ["npcLivesBriefEnabled"] = "Brief personal experiences",
            ["npcLivesOutingsEnabled"] = "Outings and accompanied visits",
            ["npcLivesMajorEnabled"] = "Major storylines",
            ["npcLivesLeaveEnabled"] = "Personal leave",
            ["npcLivesIndependentEnabled"] = "Independent personal activities",
            ["npcLivesAnnouncementsEnabled"] = "Party announcements",
            ["npcLivesReactionsEnabled"] = "Announcement reactions",
            ["npcLivesMeetingsEnabled"] = "Summoned meetings"
        };
        public static readonly IReadOnlyList<NpcLivesSetting> Numbers = BuildNumbers();
        private readonly Dictionary<string, bool> _switches;
        private readonly Dictionary<string, double> _numbers;
        public NpcLivesOptions() : this(_ => true, _ => null) { }
        public NpcLivesOptions(Func<string, bool> readSwitch, Func<string, double?> readNumber)
        {
            _switches = Switches.Keys.ToDictionary(k => k, k => readSwitch(k), StringComparer.Ordinal);
            _numbers = Numbers.ToDictionary(s => s.Key, s => { double? v = readNumber(s.Key); return v.HasValue && s.Accepts(v.Value) ? v.Value : s.Default; }, StringComparer.Ordinal);
        }
        public bool Preference(string key) => _switches.TryGetValue(key, out bool value) && value;
        public double Number(string key) => _numbers.TryGetValue(key, out double value) ? value : throw new ArgumentException("Unknown NPC Lives setting.", nameof(key));
        public double Scale(string suffix) => Number("npcLives" + suffix + "Percent") / 100d;
        public bool Allows(string feature)
        {
            if (!Preference(MasterKey)) return false;
            string key;
            switch (feature)
            {
                case "brief": case "outing": case "major":
                    if (!Preference("npcLivesStoriesEnabled")) return false;
                    key = feature == "brief" ? "Brief" : feature == "outing" ? "Outings" : "Major"; break;
                case "leave": key = "Leave"; break;
                case "independent": key = "Independent"; break;
                case "approach":
                    if (!Preference(InitialApproachKey)) return false;
                    key = "Approach"; break;
                case "announcement": key = "Announcements"; break;
                case "reaction": key = "Reactions"; break;
                case "meeting": key = "Meetings"; break;
                default: return false;
            }
            return Preference("npcLives" + key + "Enabled");
        }
        public double Frequency(string category)
        {
            string suffix = category == "brief" ? "BriefFrequency" : category == "outing" ? "OutingFrequency"
                : category == "major" ? "MajorFrequency" : category == "leave" ? "LeaveFrequency" : category == "independent" ? "IndependentFrequency" : "";
            return suffix.Length == 0 || !Allows(category) ? 0d : Scale("Frequency") * Scale(suffix);
        }
        private static IReadOnlyList<NpcLivesSetting> BuildNumbers()
        {
            var rows = new List<NpcLivesSetting>();
            string[] frequencies = { "Frequency|Overall spontaneous-event frequency", "BriefFrequency|Brief personal experiences", "OutingFrequency|Outings and accompanied visits", "MajorFrequency|New major storyline starts", "LeaveFrequency|Personal-leave requests", "IndependentFrequency|Independent personal activities" };
            foreach (string row in frequencies) { string[] p = row.Split('|'); rows.Add(new NpcLivesSetting("npcLives" + p[0] + "Percent", p[1], 100, 0, 500, 5, "%")); }
            foreach (string row in new[] { "XpReward|NPC skill XP rewards", "AffinityReward|Personal-story affinity rewards", "RefusalLoss|Relation loss for declining" })
            { string[] p = row.Split('|'); rows.Add(new NpcLivesSetting("npcLives" + p[0] + "Percent", p[1], 100, 0, 500, 5, "%")); }
            foreach (string row in new[] { "InvitationCooldown|Minimum time between new invitations", "MajorCooldown|Minimum time between new major stories", "ReaskDelay|Default delay before asking again" })
            { string[] p = row.Split('|'); rows.Add(new NpcLivesSetting("npcLives" + p[0] + "Percent", p[1], 100, 25, 400, 5, "%")); }
            rows.Add(new NpcLivesSetting("npcLivesSummonsTravelTimePercent", "Summons wait compared with normal travel", 80, 10, 100, 5, "%"));
            rows.Add(new NpcLivesSetting("npcLivesInvitationDays", "Base invitation cooldown", 7, 1, 126, 0.25, "days"));
            rows.Add(new NpcLivesSetting("npcLivesMajorMinDays", "Base minimum major-story cooldown", 15, 1, 126, 0.25, "days"));
            rows.Add(new NpcLivesSetting("npcLivesMajorMaxDays", "Base maximum major-story cooldown", 31.5, 1, 126, 0.25, "days"));
            rows.Add(new NpcLivesSetting("npcLivesResumeHours", "Fresh Approach interval", 1, 0.25, 24, 0.25, "hours"));
            rows.Add(new NpcLivesSetting("npcLivesReaskDays", "Base reconsideration delay", 7, 1, 126, 0.25, "days"));
            for (int i = 0; i < NpcLivesPolicy.AffinityAnchors.Count; i++) rows.Add(new NpcLivesSetting("npcLivesChance" + (NpcLivesPolicy.AffinityAnchors[i] < 0 ? "Minus" + -NpcLivesPolicy.AffinityAnchors[i] : NpcLivesPolicy.AffinityAnchors[i].ToString(System.Globalization.CultureInfo.InvariantCulture)), "Daily chance at " + NpcLivesPolicy.AffinityAnchors[i] + " affinity", NpcLivesPolicy.DefaultChances[i], 0, 100, 0.05, "% chance"));
            return rows.AsReadOnly();
        }
    }

    public static class NpcLivesPolicy
    {
        public const string Version = "reign-npc-lives-v1";
        public static string StoryActionTitle(NpcStoryInstance story, string missingStoryId)
        {
            // A completed story deliberately advances beyond its final phase.
            // Old proposals can still be rendered; wording must not index that cursor.
            if (story == null || story.Definition == null) return missingStoryId ?? "";
            var definition = story.Definition;
            var phases = definition.Phases;
            if (phases != null && story.PhaseIndex >= 0 && story.PhaseIndex < phases.Count)
            {
                var phase = phases[story.PhaseIndex];
                if (phase != null && !string.IsNullOrWhiteSpace(phase.Title)) return phase.Title;
            }
            return !string.IsNullOrWhiteSpace(definition.Title) ? definition.Title : missingStoryId ?? "";
        }
        public static readonly IReadOnlyDictionary<string, double> CategoryWeights = new Dictionary<string, double>(StringComparer.Ordinal)
            { ["brief"] = 80, ["outing"] = 10, ["leave"] = 4, ["independent"] = 1, ["major"] = 5 };
        public static readonly IReadOnlyList<int> AffinityAnchors = Array.AsReadOnly(new[] { -100, -80, -60, -40, -20, 0, 20, 40, 60, 80, 100 });
        public static readonly IReadOnlyList<double> DefaultChances = Array.AsReadOnly(new[] { 0d, .05, .10, .20, .30, .50, 1, 5, 12, 25, 40 });
        public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static double SummonsArrivalDelay(double normalTravelDays, NpcLivesOptions options)
        {
            if (!Finite(normalTravelDays) || normalTravelDays < 0) throw new ArgumentOutOfRangeException(nameof(normalTravelDays));
            return Math.Min(126, Math.Ceiling(normalTravelDays * options.Number("npcLivesSummonsTravelTimePercent")) / 100d);
        }
        // Native Traveling is valid for a partyless attendee already en route.
        // Disabled, fugitive and released heroes must never be made Active merely
        // to satisfy a summons. Battles are transient and handled by the caller.
        public static string MeetingParticipantBlockReason(bool exists, bool alive, bool adult,
            bool prisoner, bool activeOrTravelling, bool validParty)
        {
            if (!exists) return "is no longer available";
            if (!alive) return "has died";
            if (!adult) return "cannot attend as an adult participant";
            if (prisoner) return "is a prisoner";
            if (!activeOrTravelling) return "is currently unavailable for travel";
            if (!validParty) return "belongs to an inactive or disbanding party";
            return "";
        }
        public static bool NativeEncounterSettled(bool missionActive, bool battleActive, string encounterStatus)
            => !missionActive && !battleActive && encounterStatus == "resolved";
        public static bool CanReleaseBoundRescueCaptive(bool currentScope, bool nativeVictory,
            bool activeRescue, bool participantsWitnessed, bool captureWitnessed, bool sameBoundHero,
            bool alivePrisoner, bool ownedCustody, bool alreadyAttempted)
            => currentScope && nativeVictory && activeRescue && participantsWitnessed && captureWitnessed
                && sameBoundHero && alivePrisoner && ownedCustody && !alreadyAttempted;
        public static bool ControlledRescueCustodyMatches(bool captureWitnessed, bool sameCapturedHero,
            bool alivePrisoner, bool holderMatches, bool holderMissing, bool sameActiveBattle)
            => captureWitnessed && sameCapturedHero && alivePrisoner
                && (holderMatches || holderMissing && sameActiveBattle);
        public static bool CanAcknowledgeControlledRescueConversation(bool ownedScope, bool missionActive,
            bool conversationActive, bool participantsWitnessed, bool captiveOwned, string requiredOutcome,
            string controlledOutcome, string boundHeroId, string speakingHeroId)
            => ownedScope && !missionActive && conversationActive && participantsWitnessed && captiveOwned
                && requiredOutcome == "rescued" && controlledOutcome == "won"
                && !string.IsNullOrWhiteSpace(boundHeroId)
                && string.Equals(boundHeroId, speakingHeroId, StringComparison.Ordinal);
        public static bool CanResumeGuestService(string capturedAgreement, string currentAgreement, string currentPhase, bool permanentlyJoined)
            => string.IsNullOrEmpty(capturedAgreement) || permanentlyJoined || capturedAgreement == currentAgreement
                && (currentPhase == "Active" || currentPhase == "ReviewDue");
        public static double BaseChance(int affinity, NpcLivesOptions options)
        {
            affinity = Math.Max(-100, Math.Min(100, affinity));
            double Chance(int a) => options.Number("npcLivesChance" + (a < 0 ? "Minus" + -a : a.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (affinity == 100) return Chance(100);
            int low = (int)Math.Floor((affinity + 100d) / 20d) * 20 - 100;
            return Chance(low) + (Chance(low + 20) - Chance(low)) * (affinity - low) / 20d;
        }
        public static bool AffinityAllows(string category, int affinity) => category == "major" ? affinity >= 80 : category == "outing" ? affinity >= 60 : category == "brief" || category == "leave" || category == "independent";

        /// <summary>Scale allocated probabilities, not normalized selection weights. Turning a category down must not increase another category.</summary>
        public static Dictionary<string, double> DailyProbabilities(int affinity, NpcLivesOptions options, IReadOnlyDictionary<string, double> eligibleWeights)
        {
            var weights = eligibleWeights.Where(p => options.Allows(p.Key) && AffinityAllows(p.Key, affinity) && Finite(p.Value) && p.Value > 0).ToArray();
            double totalWeight = weights.Sum(p => p.Value);
            var result = weights.ToDictionary(p => p.Key, p => totalWeight == 0 ? 0 : BaseChance(affinity, options) / 100d * p.Value / totalWeight * options.Frequency(p.Key), StringComparer.Ordinal);
            double total = result.Values.Sum();
            if (total > 1d) foreach (string key in result.Keys.ToArray()) result[key] /= total;
            return result;
        }
        public static string SelectCategory(IReadOnlyDictionary<string, double> probabilities, double roll)
        {
            if (!Finite(roll) || roll < 0 || roll >= 1) throw new ArgumentOutOfRangeException(nameof(roll));
            double cumulative = 0;
            foreach (var pair in probabilities.OrderBy(p => p.Key, StringComparer.Ordinal))
            { cumulative += pair.Value; if (roll < cumulative) return pair.Key; }
            return "";
        }
        public static bool NewOpeningDue(double? lastAcceptedDay, double worldDay, double intervalHours)
            => Finite(worldDay) && Finite(intervalHours) && intervalHours > 0 && (!lastAcceptedDay.HasValue
                || (worldDay >= lastAcceptedDay.Value && (worldDay - lastAcceptedDay.Value) * 24d + 1e-9 >= intervalHours));
        public static double InvitationDelay(NpcLivesOptions o) => o.Number("npcLivesInvitationDays") * o.Scale("InvitationCooldown");
        public static double MajorDelay(NpcLivesOptions o, double roll)
        {
            if (!Finite(roll) || roll < 0 || roll >= 1) throw new ArgumentOutOfRangeException(nameof(roll));
            return (o.Number("npcLivesMajorMinDays") + (o.Number("npcLivesMajorMaxDays") - o.Number("npcLivesMajorMinDays")) * roll) * o.Scale("MajorCooldown");
        }
        public static double ReaskDelay(NpcLivesOptions o) => o.Number("npcLivesReaskDays") * o.Scale("ReaskDelay");
        public static int RefusalLoss(string kind) => kind == "brief" ? -2 : kind == "outing" ? -5 : kind == "broken_promise" ? -12 : -8;
    }
}
