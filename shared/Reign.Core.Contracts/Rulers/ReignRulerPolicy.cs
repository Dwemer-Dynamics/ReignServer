using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.Shared.Rulers
{
    public enum RulerVerdict { TortureExecution, CleanExecution, Exile, Submission, Independence, RebelExecution, Reunification, Freedom, Imprisonment }
    public enum RulerJudgmentStage { AwaitingAuthority, AwaitingDecision, Sentenced, Applying, Completed, Cancelled }

    public sealed class RulerOptions
    {
        public const string Master = "rulerSystemEnabled";
        public const string Capitals = "rulerCapitalResidenceEnabled";
        public const string Capture = "rulerCaptureProtectionEnabled";
        public const string Judgments = "rulerJudgmentsEnabled";
        public const string Effects = "rulerSettlementEffectsEnabled";
        public const string CivilWars = "rulerCivilWarJudgmentsEnabled";
        public const string Reactions = "rulerUnauthorizedExecutionReactionsEnabled";
        public const string GovernorCulture = "rulerRemoveForeignGovernorPenalty";
        public const string Mortality = "rulerRequireLifeDeathEnabled";
        public static readonly IReadOnlyDictionary<string, string> Switches = new Dictionary<string, string>(StringComparer.Ordinal) {
            [Master] = "Ruler system", [Capitals] = "Capital residence and army departure rules",
            [Capture] = "Ruler capture protection", [Judgments] = "Captured ruler judgments",
            [Effects] = "Judgment settlement bonuses and penalties", [CivilWars] = "Civil-war ruler judgments",
            [Reactions] = "Unauthorized ruler execution reactions", [GovernorCulture] = "Remove foreign-governor loyalty penalty",
            [Mortality] = "Require life and death"
        };
        private readonly Func<string, bool> _read;
        public RulerOptions(Func<string, bool>? read = null) { _read = read ?? (_ => true); }
        public bool Preference(string key) => Switches.ContainsKey(key) && _read(key);
        public bool Enabled(string key) => Preference(Master) && Preference(key);
    }

    public sealed class RulerCapital
    {
        public string KingdomId { get; set; } = "";
        public string SettlementId { get; set; } = "";
        public string RulingClanId { get; set; } = "";
        public bool Manual { get; set; }
        public double SelectedDay { get; set; }
    }
    public sealed class RulerCapitalCandidate
    {
        public string Id { get; set; } = "";
        public bool RulingClan { get; set; }
        public bool Town { get; set; }
        public double GarrisonStrength { get; set; }
        public int ClanTier { get; set; }
    }
    public sealed class RulerWar
    {
        public string Id { get; set; } = "";
        public string KingdomA { get; set; } = "";
        public string KingdomB { get; set; } = "";
        public double StartDay { get; set; }
        public bool Active { get; set; } = true;
        public bool MigrationBaseline { get; set; }
        public List<string> HomelandA { get; set; } = new List<string>();
        public List<string> HomelandB { get; set; } = new List<string>();
        public List<string> CapturesA { get; set; } = new List<string>();
        public List<string> CapturesB { get; set; } = new List<string>();
    }
    public sealed class RulerClanSnapshot
    {
        public string ClanId { get; set; } = "";
        public string LeaderId { get; set; } = "";
        public int Affinity { get; set; }
        public int AffinityToVictor { get; set; }
        public List<string> Fiefs { get; set; } = new List<string>();
    }
    public sealed class RulerJudgment
    {
        public string Id { get; set; } = "";
        public string PrisonerId { get; set; } = "";
        public string VictorId { get; set; } = "";
        public string DefeatedKingdomId { get; set; } = "";
        public string VictorKingdomId { get; set; } = "";
        public string WarId { get; set; } = "";
        public string MovementId { get; set; } = "";
        public bool RebelVictory { get; set; }
        public bool CivilWar { get; set; }
        public bool DefeatedLord { get; set; }
        public RulerJudgmentStage Stage { get; set; }
        public RulerVerdict Verdict { get; set; }
        public double CapturedDay { get; set; }
        public double SentencedDay { get; set; }
        public double Deadline { get; set; }
        public double Draw { get; set; } = -1;
        public int? VictorAffinity { get; set; }
        public string TraitSource { get; set; } = "";
        public string CededSettlementId { get; set; } = "";
        public string SuccessorId { get; set; } = "";
        public string ExileClanId { get; set; } = "";
        public Dictionary<string, int> Payments { get; set; } = new Dictionary<string, int>();
        public string CancellationReason { get; set; } = "";
        public string Failure { get; set; } = "";
        public bool NativeDeathObserved { get; set; }
        public bool NativeExecutionRequested { get; set; }
        public double NativeDeathDay { get; set; } = -1;
        public List<RulerClanSnapshot> Clans { get; set; } = new List<RulerClanSnapshot>();
        public Dictionary<string, double> Traits { get; set; } = new Dictionary<string, double>();
        public HashSet<string> Receipts { get; set; } = new HashSet<string>(StringComparer.Ordinal);
    }
    public sealed class RulerEffect
    {
        public string JudgmentId { get; set; } = "";
        public string VictorKingdomId { get; set; } = "";
        public string DefeatedKingdomId { get; set; } = "";
        public string WarId { get; set; } = "";
        public RulerVerdict Verdict { get; set; }
        public double StartDay { get; set; }
        public List<string> Homeland { get; set; } = new List<string>();
        public List<string> Conquered { get; set; } = new List<string>();
    }
    public sealed class RulerExile
    {
        public string Id { get; set; } = "";
        public string HeroId { get; set; } = "";
        public string ClanId { get; set; } = "";
        public double UntilDay { get; set; }
        public bool Fugitive { get; set; }
    }
    public sealed class RulerState
    {
        public int Version { get; set; } = 1;
        public List<RulerCapital> Capitals { get; set; } = new List<RulerCapital>();
        public List<RulerWar> Wars { get; set; } = new List<RulerWar>();
        public List<RulerJudgment> Judgments { get; set; } = new List<RulerJudgment>();
        public List<RulerEffect> Effects { get; set; } = new List<RulerEffect>();
        public List<RulerExile> Exiles { get; set; } = new List<RulerExile>();
        public Dictionary<string, string> Returns { get; set; } = new Dictionary<string, string>();
        public HashSet<string> Reactions { get; set; } = new HashSet<string>(StringComparer.Ordinal);
    }
    public sealed class RulerModifiers
    {
        public double Loyalty { get; set; }
        public double Prosperity { get; set; }
        public double Security { get; set; }
    }
    public static class ReignRulerPolicy
    {
        public const int DepartureTroops = 1000, ReturnTroops = 750;
        public const double ExecutionDays = 15, ExileDays = 126, FugitiveDays = 31.5;
        public static bool Execution(RulerVerdict verdict) => verdict == RulerVerdict.TortureExecution || verdict == RulerVerdict.CleanExecution || verdict == RulerVerdict.RebelExecution;
        public static bool CanDepart(bool commander, int healthyPresent, bool relocationOrReturn = false) => relocationOrReturn || (commander && healthyPresent >= DepartureTroops);
        public static bool CompleteReturnIfArrived(RulerState state, string rulerId, string? currentSettlementId)
        {
            if (string.IsNullOrEmpty(currentSettlementId) || !state.Returns.TryGetValue(rulerId, out string destination)
                || destination != currentSettlementId) return false;
            return state.Returns.Remove(rulerId);
        }
        public static bool ShouldReturn(bool armyActive, int healthyPresent, bool objectiveComplete) => !armyActive || objectiveComplete || healthyPresent < ReturnTroops;
        public static bool Capture(bool wounded, double roll) => Finite(roll) && roll >= 0 && roll < (wounded ? .5 : .25);
        public static bool Defects(RulerVerdict verdict, int affinity) => (verdict == RulerVerdict.TortureExecution && affinity <= -20) || (verdict == RulerVerdict.CleanExecution && affinity <= -10);
        public static bool DefectsFromRealm(RulerVerdict verdict, int affinity, string? currentKingdom, string defeatedKingdom)
            => !string.IsNullOrEmpty(currentKingdom) && currentKingdom == defeatedKingdom && Defects(verdict, affinity);
        public static RulerCapitalCandidate? SelectCapital(IEnumerable<RulerCapitalCandidate> candidates) => candidates
            .OrderBy(c => c.Town ? (c.RulingClan ? 0 : 1) : 2).ThenByDescending(c => c.GarrisonStrength)
            .ThenByDescending(c => c.ClanTier).ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
        public static double Duration(RulerVerdict verdict) => verdict == RulerVerdict.TortureExecution ? 60 : verdict == RulerVerdict.CleanExecution ? 30 : verdict == RulerVerdict.Exile || verdict == RulerVerdict.Submission ? 45 : 0;
        public static double Fade(RulerVerdict verdict, double start, double day)
        {
            double duration = Duration(verdict), elapsed = day - start;
            if (!Finite(elapsed) || duration <= 0 || elapsed < 0 || elapsed >= duration) return 0;
            return elapsed <= duration * 2 / 3 ? 1 : (duration - elapsed) / (duration / 3);
        }
        public static RulerModifiers Modifiers(IEnumerable<RulerEffect> effects, string settlement, string owner, double day)
        {
            var result = new RulerModifiers(); double securityBonus = 0, securityPenalty = 0;
            foreach (var e in effects)
            {
                double f = Fade(e.Verdict, e.StartDay, day); if (f <= 0) continue;
                bool victor = owner == e.VictorKingdomId, defeated = owner == e.DefeatedKingdomId;
                bool homeland = victor && e.Homeland.Contains(settlement);
                bool conquered = victor && !homeland && e.Conquered.Contains(settlement);
                if (e.Verdict == RulerVerdict.TortureExecution || e.Verdict == RulerVerdict.CleanExecution)
                {
                    bool torture = e.Verdict == RulerVerdict.TortureExecution;
                    if (conquered) result.Loyalty -= (torture ? 3.5 : 2) * f;
                    if (homeland) { result.Prosperity = Math.Max(result.Prosperity, (torture ? 2 : 1) * f); securityBonus = Math.Max(securityBonus, (torture ? 1 : .5) * f); }
                }
                else if (e.Verdict == RulerVerdict.Exile && (victor || defeated)) result.Loyalty -= (defeated ? 2.5 : .75) * f;
                else if (e.Verdict == RulerVerdict.Submission && victor)
                { result.Loyalty -= (conquered ? 2 : .75) * f; securityPenalty = Math.Min(securityPenalty, -.25 * f); }
            }
            result.Loyalty = Math.Max(-3.5, result.Loyalty); result.Security = securityBonus + securityPenalty; return result;
        }
        public static Dictionary<RulerVerdict, double> Scores(IDictionary<string, double> traits, int affinity, bool rebel)
        {
            double T(string key) => traits.TryGetValue(key, out double v) && Finite(v) ? Math.Max(0, Math.Min(100, v)) : 50;
            double a = (Math.Max(-100, Math.Min(100, affinity)) + 100d) / 2, h = 100 - a, c = 100 - T("mercy");
            double torture = .45*T("vengefulness")+.35*c+.10*T("aggression")+.10*h;
            if (rebel) return new Dictionary<RulerVerdict, double> {
                [RulerVerdict.Independence] = .40*T("mercy")+.35*T("legacyMotivation")+.25*(100-T("powerMotivation")),
                [RulerVerdict.RebelExecution] = torture,
                [RulerVerdict.Reunification] = .50*T("ambition")+.35*T("powerMotivation")+.15*T("pragmatism") };
            return new Dictionary<RulerVerdict, double> {
                [RulerVerdict.TortureExecution] = torture,
                [RulerVerdict.CleanExecution] = .45*c+.25*T("dutyMotivation")+.20*T("traditionalism")+.10*h,
                [RulerVerdict.Exile] = .65*T("mercy")+.20*T("compassion")+.15*a,
                [RulerVerdict.Submission] = .40*T("ambition")+.35*T("pragmatism")+.15*T("powerMotivation")+.10*T("mercy") };
        }
        public static RulerVerdict Choose(IDictionary<RulerVerdict, double> scores, double draw)
        {
            if (scores.Count == 0 || !Finite(draw) || draw < 0 || draw >= 1) throw new ArgumentOutOfRangeException(nameof(draw));
            double maximum = scores.Values.Max(), total = scores.Values.Sum(v => Math.Exp((v-maximum)/12));
            double target = draw * total;
            foreach (var entry in scores.OrderBy(p => p.Key)) { target -= Math.Exp((entry.Value-maximum)/12); if (target < 0) return entry.Key; }
            return scores.Keys.OrderBy(k => k).Last();
        }
        public static bool Cancel(RulerJudgment judgment, string reason)
        {
            if (judgment.Stage == RulerJudgmentStage.Completed || judgment.Stage == RulerJudgmentStage.Cancelled || judgment.NativeDeathObserved || judgment.NativeExecutionRequested
                || (judgment.Stage == RulerJudgmentStage.Applying && !Execution(judgment.Verdict))) return false;
            judgment.Stage = RulerJudgmentStage.Cancelled; judgment.CancellationReason = reason; judgment.Failure = ""; return true;
        }
        public static bool SentenceDue(RulerJudgment j, double day) => j.Stage == RulerJudgmentStage.Sentenced && Execution(j.Verdict) && day >= j.Deadline;
        public static bool ObserveNativeExecution(RulerJudgment j, bool dead, bool nativeExecutionMarked)
        {
            if (j.Stage != RulerJudgmentStage.Applying || !Execution(j.Verdict)) throw new InvalidOperationException("No execution is being applied.");
            if (dead) { j.NativeDeathObserved = true; j.Receipts.Add("execution"); return true; }
            if (!nativeExecutionMarked) throw new InvalidOperationException("Native execution neither completed nor accepted a death request.");
            j.NativeExecutionRequested = true; return false;
        }
        public static bool BlocksRelease(RulerJudgment j) => !j.NativeDeathObserved && Execution(j.Verdict)
            && (j.Stage == RulerJudgmentStage.Sentenced || j.Stage == RulerJudgmentStage.Applying);
        public static long Reparations(long availableWealth) => Math.Max(0, availableWealth) / 4;
        public static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
    }
}
