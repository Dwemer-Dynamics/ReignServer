using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Government
{
    public sealed class GovernmentPoliticalValue
    {
        public ReignGovernmentPlank Value { get; set; }
        public int Stance { get; set; }
        public int Importance { get; set; }
    }

    public sealed class GovernmentPoliticalChange
    {
        public string ReceiptId { get; set; } = "";
        public int Interval { get; set; }
        public string Source { get; set; } = "";
        public ReignGovernmentPlank Value { get; set; }
        public int PreviousStance { get; set; }
        public int PreviousImportance { get; set; }
        public int Stance { get; set; }
        public int Importance { get; set; }
    }

    public sealed class GovernmentPoliticalProfile
    {
        public int ModelVersion { get; set; } = GovernmentPoliticalBeliefs.ModelVersion;
        public string CampaignId { get; set; } = "";
        public string HeroId { get; set; } = "";
        public string TimelineId { get; set; } = "";
        public long Revision { get; set; } = 1;
        public bool Formative { get; set; }
        public List<GovernmentPoliticalValue> Values { get; set; } = new List<GovernmentPoliticalValue>();
        public List<GovernmentPoliticalChange> Changes { get; set; } = new List<GovernmentPoliticalChange>();
        public List<string> SeenEvidence { get; set; } = new List<string>();
        public List<GovernmentPoliticalIntervalBudget> IntervalBudgets { get; set; } = new List<GovernmentPoliticalIntervalBudget>();
    }

    public sealed class GovernmentPoliticalIntervalBudget
    {
        public int Interval { get; set; }
        public int Discussions { get; set; }
        public Dictionary<ReignGovernmentPlank, int> Spent { get; set; } = new Dictionary<ReignGovernmentPlank, int>();
    }

    // Native facts are captured only after the campaign has verified an executed
    // effect. The server derives the small salience changes; models cannot supply them.
    public sealed class GovernmentPoliticalWorldEvidence
    {
        public string Id { get; set; } = "";
        public string CampaignId { get; set; } = "";
        public string TimelineId { get; set; } = "";
        public string KingdomId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string OptionId { get; set; } = "";
        public bool Applied { get; set; }
        public bool IsRepeal { get; set; }
        public int Interval { get; set; }
        public double WorldDay { get; set; }
        public List<string> AffectedHeroes { get; set; } = new List<string>();
        public double Authoritarian { get; set; }
        public double Oligarchic { get; set; }
        public double Egalitarian { get; set; }
    }

    // Shared by the server's canonical seed and the campaign's applied snapshot. No native,
    // provider, social identity, party affiliation or presentation text enters these formulas.
    public static class GovernmentPoliticalBeliefs
    {
        public const int ModelVersion = 1;
        private static readonly string[][] TraitTerms = {
            new[] { "authorityRespect", "traditionalism", "-assertiveness" },
            new[] { "assertiveness", "socialTrust", "-authorityRespect" },
            new[] { "compassion", "empathy", "generosity" },
            new[] { "wealthMotivation", "pragmatism", "curiosity" },
            new[] { "patience", "familyMotivation", "legacyMotivation" },
            new[] { "discipline", "pragmatism", "legacyMotivation" },
            new[] { "courage", "discipline", "dutyMotivation" },
            new[] { "ambition", "aggression", "riskTolerance" },
            new[] { "mercy", "patience", "-aggression" },
            new[] { "honesty", "dutyMotivation", "-vengefulness" },
            new[] { "survivalMotivation", "discipline", "authorityRespect" },
            new[] { "religionMotivation", "traditionalism" },
            new[] { "pride", "traditionalism", "-empathy" },
            new[] { "familyMotivation", "loyalty", "legacyMotivation" },
            new[] { "assertiveness", "familyMotivation", "-authorityRespect" },
            new[] { "curiosity", "pragmatism", "-honesty" }
        };

        public static GovernmentPoliticalProfile Seed(string campaignId, string heroId, string timelineId,
            IReadOnlyDictionary<string, int> traits, IReadOnlyDictionary<ReignGovernmentPlank, double> context, bool child)
        {
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(heroId) || traits == null)
                throw new ArgumentException("Political profiles require campaign, character and real trait evidence.");
            var profile = new GovernmentPoliticalProfile { CampaignId = campaignId, HeroId = heroId,
                TimelineId = timelineId ?? "", Formative = child };
            foreach (ReignGovernmentPlank value in Enum.GetValues(typeof(ReignGovernmentPlank)))
            {
                double trait = TraitTerms[(int)value].Average(term => {
                    bool inverse = term[0] == '-';
                    int percent;
                    if (!traits.TryGetValue(inverse ? term.Substring(1) : term, out percent))
                        throw new ArgumentException("Missing political seed trait: " + term);
                    return (inverse ? -1 : 1) * (Clamp(percent, 0, 100) - 50) / 50d;
                });
                double station = context != null && context.TryGetValue(value, out double raw) ? Math.Max(-1, Math.Min(1, raw)) : 0;
                string seed = campaignId + "|" + heroId + "|" + value + "|politics-v" + ModelVersion;
                int jitter = (int)(ReignGovernmentRules.StableHash(seed + "|stance") % 81) - 40;
                int priority = (int)(ReignGovernmentRules.StableHash(seed + "|priority") % 31);
                profile.Values.Add(new GovernmentPoliticalValue { Value = value,
                    Stance = Clamp((int)Math.Round(60 * trait + 20 * station + jitter), -100, 100),
                    Importance = Clamp((int)Math.Round(35 + 35 * Math.Abs(trait) + 20 * Math.Abs(station) + priority), 1, 100) });
            }
            return profile;
        }

        public static bool Valid(GovernmentPoliticalProfile profile)
        {
            if (profile == null || profile.ModelVersion != ModelVersion || string.IsNullOrWhiteSpace(profile.HeroId)
                || string.IsNullOrWhiteSpace(profile.CampaignId) || profile.Revision <= 0 || profile.Values == null
                || profile.Values.Count != 16 || profile.Changes == null || profile.SeenEvidence == null) return false;
            // Model1 requires each of the sixteen contiguous planks exactly once.
            // Avoid a Distinct set and sixteen boxed enum reflection calls per profile
            // in every native selection/heartbeat scan.
            uint seen = 0;
            foreach (var value in profile.Values)
            {
                if (value == null || (uint)value.Value >= 16 || value.Stance < -100 || value.Stance > 100
                    || value.Importance < 1 || value.Importance > 100) return false;
                uint bit = 1u << (int)value.Value;
                if ((seen & bit) != 0) return false;
                seen |= bit;
            }
            return seen == 0xffff;
        }

        public static IReadOnlyList<GovernmentPoliticalValue> WorldEventChanges(GovernmentPoliticalWorldEvidence evidence, string heroId)
        {
            if (evidence == null || !evidence.Applied || !evidence.Id.StartsWith("government:", StringComparison.Ordinal)
                || evidence.Interval < 0 || evidence.WorldDay < 0 || string.IsNullOrEmpty(evidence.CampaignId)
                || string.IsNullOrEmpty(evidence.KingdomId) || string.IsNullOrEmpty(evidence.TargetId)
                || evidence.AffectedHeroes == null || !evidence.AffectedHeroes.Contains(heroId))
                return Array.Empty<GovernmentPoliticalValue>();
            IReadOnlyDictionary<ReignGovernmentPlank, int> impacts;
            if (evidence.Kind == "edict")
            {
                if (!GovernmentEdictRules.All.Any(x => x.Id == evidence.TargetId)) return Array.Empty<GovernmentPoliticalValue>();
                impacts = EdictImpact(evidence.TargetId, evidence.IsRepeal);
            }
            else if (evidence.Kind == "policy")
            {
                if (new[] { evidence.Authoritarian, evidence.Oligarchic, evidence.Egalitarian }.Any(x => double.IsNaN(x) || double.IsInfinity(x)))
                    return Array.Empty<GovernmentPoliticalValue>();
                impacts = NativePolicyImpact(evidence.Authoritarian, evidence.Oligarchic, evidence.Egalitarian, evidence.IsRepeal);
            }
            else if (evidence.Kind == "war" || evidence.Kind == "peace")
                impacts = new Dictionary<ReignGovernmentPlank, int> { [ReignGovernmentPlank.Peace] = 100, [ReignGovernmentPlank.Security] = 80 };
            else return Array.Empty<GovernmentPoliticalValue>();
            // Living with a consequential law or war makes its concerns more salient.
            // It does not make opponents endorse it or reward a promised vote.
            return impacts.Where(x => x.Value != 0).OrderByDescending(x => Math.Abs(x.Value)).ThenBy(x => x.Key).Take(2)
                .Select(x => new GovernmentPoliticalValue { Value = x.Key, Importance = 1 }).ToList();
        }

        public static IReadOnlyList<GovernmentPoliticalValue> Priorities(GovernmentPoliticalProfile profile) =>
            profile.Values.OrderByDescending(x => x.Importance).ThenBy(x => ReignGovernmentRules.StableHash(profile.HeroId + "|" + x.Value)).ToList();

        public static double PartyCompatibility(GovernmentPoliticalProfile profile, IReadOnlyList<ReignGovernmentPlank> planks)
        {
            if (!Valid(profile)) throw new ArgumentException("A complete applied profile is required.");
            double dot = 0, personNorm = 0, partyNorm = 0;
            int[] weights = { 8, 4, 2, 1 };
            var unique = (planks ?? Array.Empty<ReignGovernmentPlank>()).Distinct().ToList();
            foreach (var row in profile.Values)
            {
                double personal = row.Stance / 100d * row.Importance / 100d;
                int index = unique.IndexOf(row.Value);
                double party = index < 0 ? 0 : weights[Math.Min(index, weights.Length - 1)];
                dot += personal * party; personNorm += personal * personal; partyNorm += party * party;
            }
            return personNorm == 0 || partyNorm == 0 ? 0 : Math.Max(-100, Math.Min(100, 100 * dot / Math.Sqrt(personNorm * partyNorm)));
        }

        public static int Alignment(GovernmentPoliticalProfile profile, IReadOnlyDictionary<ReignGovernmentPlank, int> impacts)
        {
            if (!Valid(profile)) throw new ArgumentException("A complete applied profile is required.");
            double sum = 0, importance = 0;
            foreach (var row in profile.Values)
                if (impacts.TryGetValue(row.Value, out int impact) && impact != 0)
                { sum += row.Importance * row.Stance / 100d * Clamp(impact, -100, 100) / 100d; importance += row.Importance; }
            return importance == 0 ? 0 : Clamp((int)Math.Round(40 * sum / importance), -40, 40);
        }

        public static IReadOnlyDictionary<ReignGovernmentPlank, int> NativePolicyImpact(double authoritarian, double oligarchic, double egalitarian, bool repeal)
        {
            double scale = (repeal ? -100d : 100d) / Math.Max(1, Math.Max(Math.Abs(authoritarian), Math.Max(Math.Abs(oligarchic), Math.Abs(egalitarian))));
            return new Dictionary<ReignGovernmentPlank, int> {
                [ReignGovernmentPlank.RoyalAuthority] = (int)Math.Round(authoritarian * scale),
                [ReignGovernmentPlank.NoblePrivilege] = (int)Math.Round(oligarchic * scale),
                [ReignGovernmentPlank.ClanPrivilege] = (int)Math.Round(oligarchic * scale),
                [ReignGovernmentPlank.RepresentativeAuthority] = (int)Math.Round(egalitarian * scale),
                [ReignGovernmentPlank.PopularWelfare] = (int)Math.Round(egalitarian * scale) };
        }

        public static IReadOnlyDictionary<ReignGovernmentPlank, int> MandateImpact(string kind, bool incumbentHasCivicMandate)
        {
            var result = new Dictionary<ReignGovernmentPlank, int>();
            if (kind == "admission") result[ReignGovernmentPlank.RepresentativeAuthority] = 25;
            else if (kind == "recall")
            {
                if (incumbentHasCivicMandate) result[ReignGovernmentPlank.RepresentativeAuthority] = -25;
            }
            else throw new ArgumentException("No verified mandate impact for: " + kind);
            return result;
        }

        public static IReadOnlyDictionary<ReignGovernmentPlank, int> EdictImpact(string id, bool repeal)
        {
            var values = new Dictionary<ReignGovernmentPlank, int>();
            switch (id)
            {
                case "castle_guard": Set(values, ReignGovernmentPlank.MilitaryStrength, 75, ReignGovernmentPlank.Security, 50,
                    ReignGovernmentPlank.PopularWelfare, -35, ReignGovernmentPlank.Agriculture, -30); break;
                case "extraordinary_subsidy": Set(values, ReignGovernmentPlank.NoblePrivilege, 30, ReignGovernmentPlank.ClanPrivilege, 30,
                    ReignGovernmentPlank.Trade, -30, ReignGovernmentPlank.PopularWelfare, -50); break;
                case "garrison_billeting": Set(values, ReignGovernmentPlank.MilitaryStrength, 50, ReignGovernmentPlank.NoblePrivilege, 30,
                    ReignGovernmentPlank.PopularWelfare, -50, ReignGovernmentPlank.Agriculture, -30); break;
                case "public_works": Set(values, ReignGovernmentPlank.Infrastructure, 80, ReignGovernmentPlank.PopularWelfare, -25,
                    ReignGovernmentPlank.Agriculture, -30); break;
                case "free_market": Set(values, ReignGovernmentPlank.Trade, 80, ReignGovernmentPlank.PopularWelfare, 20,
                    ReignGovernmentPlank.NoblePrivilege, -15); break;
                case "public_granary": Set(values, ReignGovernmentPlank.PopularWelfare, 90, ReignGovernmentPlank.Security, 20); break;
                case "harvest_exemption": Set(values, ReignGovernmentPlank.Agriculture, 80, ReignGovernmentPlank.PopularWelfare, 30,
                    ReignGovernmentPlank.MilitaryStrength, -60); break;
                case "local_customs": Set(values, ReignGovernmentPlank.LocalAutonomy, 80, ReignGovernmentPlank.RepresentativeAuthority, 20,
                    ReignGovernmentPlank.PopularWelfare, 50, ReignGovernmentPlank.MilitaryStrength, -20); break;
                default: throw new ArgumentException("No verified political impact for edict: " + id);
            }
            return values.ToDictionary(x => x.Key, x => repeal ? -x.Value : x.Value);
        }

        public static bool Evolve(GovernmentPoliticalProfile profile, string receiptId, string source, int interval,
            bool verified, bool discussion, IReadOnlyList<GovernmentPoliticalValue> deltas)
        {
            if (!verified || !Valid(profile) || string.IsNullOrWhiteSpace(receiptId) || string.IsNullOrWhiteSpace(source)
                || interval < 0 || deltas == null || deltas.Count == 0 || deltas.Count > 2 || deltas.Select(x => x.Value).Distinct().Count() != deltas.Count
                || deltas.Any(x => !Enum.IsDefined(typeof(ReignGovernmentPlank), x.Value))
                || profile.Changes.Any(x => x.ReceiptId == receiptId) || profile.SeenEvidence.Contains(receiptId)) return false;
            var period = profile.Changes.Where(x => x.Interval == interval).ToList();
            var ledger = profile.IntervalBudgets.FirstOrDefault(x => x.Interval == interval);
            if (ledger == null)
            {
                ledger = new GovernmentPoliticalIntervalBudget { Interval = interval,
                    Discussions = period.Where(x => x.Source.StartsWith("discussion:", StringComparison.Ordinal)).Select(x => x.ReceiptId).Distinct().Count(),
                    Spent = period.GroupBy(x => x.Value).ToDictionary(x => x.Key,
                        x => x.Sum(c => Math.Abs(c.Stance - c.PreviousStance) + Math.Abs(c.Importance - c.PreviousImportance))) };
                profile.IntervalBudgets.Add(ledger);
            }
            int remaining = discussion && ledger.Discussions >= 2 ? 0 : 10 - ledger.Spent.Values.Sum();
            if (discussion) ledger.Discussions = Math.Min(2, ledger.Discussions + 1);
            var changes = new List<GovernmentPoliticalChange>();
            foreach (var delta in deltas)
            {
                var row = profile.Values.Single(x => x.Value == delta.Value);
                int used = ledger.Spent.TryGetValue(row.Value, out int spent) ? spent : 0;
                int budget = Math.Max(0, Math.Min(discussion ? 1 : 3, Math.Min(5 - used, remaining)));
                int stance = Clamp(delta.Stance, -budget, budget); budget -= Math.Abs(stance);
                int importance = Clamp(delta.Importance, -budget, budget);
                int nextStance = Clamp(row.Stance + stance, -100, 100), nextImportance = Clamp(row.Importance + importance, 1, 100);
                int applied = Math.Abs(nextStance - row.Stance) + Math.Abs(nextImportance - row.Importance);
                if (applied == 0) continue;
                changes.Add(new GovernmentPoliticalChange { ReceiptId = receiptId, Interval = interval,
                    Source = (discussion ? "discussion:" : "event:") + source, Value = row.Value,
                    PreviousStance = row.Stance, PreviousImportance = row.Importance, Stance = nextStance, Importance = nextImportance });
                remaining -= applied;
                ledger.Spent[row.Value] = used + applied;
            }
            foreach (var change in changes) { var row = profile.Values.Single(x => x.Value == change.Value); row.Stance = change.Stance; row.Importance = change.Importance; }
            profile.Changes.AddRange(changes); profile.SeenEvidence.Add(receiptId);
            // A verified receipt is consumed even at a cap or natural value bound.
            // Keep interval spending independent of the bounded detail history so late
            // receipts cannot reopen an old budget or clog the native synchronization queue.
            while (profile.Changes.Count > 256)
            { int old = profile.Changes.FindIndex(x => x.Interval != interval); if (old < 0) break; profile.Changes.RemoveAt(old); }
            profile.Revision++; return true;
        }

        private static void Set(Dictionary<ReignGovernmentPlank, int> values, params object[] pairs)
        { for (int i = 0; i < pairs.Length; i += 2) values[(ReignGovernmentPlank)pairs[i]] = (int)pairs[i + 1]; }
        private static int Clamp(int n, int min, int max) => Math.Max(min, Math.Min(max, n));
    }
}
