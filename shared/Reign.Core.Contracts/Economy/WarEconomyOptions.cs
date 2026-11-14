using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace Reign.Core.Contracts.Economy
{
    // Shared, immutable and provider-free. No campaign state is changed by a preference.
    public sealed class WarEconomyOptions
    {
        public const int Version = 1;
        public const string Prefix = "economy";
        public const string NpcTradePrefix = "economyNpcTrade";
        public sealed class NumberOption
        {
            public string Label { get; }
            public double Default { get; }
            public double Minimum { get; }
            public double Maximum { get; }
            public double Step { get; }
            public NumberOption(string label, double value, double minimum, double maximum, double step)
            { Label = label; Default = value; Minimum = minimum; Maximum = maximum; Step = step; }
        }
        public static IReadOnlyDictionary<string, string> Switches { get; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string> {
            ["economyTroopWagesEnabled"] = "Custom troop wages",
            ["economyRecruitPricesEnabled"] = "Recruitment and upgrade premiums",
            ["economyVillageCurveEnabled"] = "Continuous village output",
            ["economyGarrisonWagesEnabled"] = "Garrison wage savings",
            ["economyGarrisonFoodEnabled"] = "Garrison food savings",
            ["economyReservesEnabled"] = "Automatic military spending reserves",
            ["economyRaidFoodEnabled"] = "Raid food disruption",
            ["economyNpcTradeCaravansEnabled"] = "Enable NPC clan caravans",
            ["economyNpcTradeConvoysEnabled"] = "Enable NPC clan convoys"
        });
        public static IReadOnlyDictionary<string, NumberOption> Numbers { get; } = CreateNumbers();
        private static IReadOnlyDictionary<string, NumberOption> CreateNumbers()
        {
            var result = new Dictionary<string, NumberOption>(StringComparer.Ordinal);
            var wages = new double[] { 3, 6, 12, 18, 24, 40, 68, 92 };
            var recruits = new double[] { 3, 3, 3.6, 4.5, 6, 8.25, 10.5, 10.5 };
            for (int tier = 0; tier < wages.Length; tier++)
            {
                string label = "Tier " + tier + (tier == 7 ? "+" : "");
                result["economyTroopWageTier" + tier] = new NumberOption(label + " wage", wages[tier], 0, 500, 1);
                result["economyRecruitTier" + tier] = new NumberOption(label + " recruitment multiplier", recruits[tier], 1, 20, .05);
            }
            result["economyVillageOutputScale"] = new NumberOption("Village output scale", 1, .1, 3, .05);
            result["economyVillageFood10"] = new NumberOption("Output at 10 hearths", 2, .1, 20, .1);
            result["economyVillageFood300"] = new NumberOption("Output at 300 hearths", 11, .1, 100, .1);
            result["economyVillageFood600"] = new NumberOption("Output at 600 hearths", 16.5, .1, 150, .1);
            result["economyVillageFood1200"] = new NumberOption("Output at 1,200 hearths", 22, .1, 200, .1);
            result["economyGarrisonWageFactor"] = new NumberOption("Garrison wage factor", .75, .1, 1, .05);
            result["economyGarrisonMenPerFood"] = new NumberOption("Garrison soldiers per food", 25, 20, 100, 1);
            result["economyRecruitReserveDays"] = new NumberOption("Recruitment wage reserve (days)", 7, 0, 30, 1);
            result["economyUpgradeReserveDays"] = new NumberOption("Upgrade wage reserve (days)", 14, 0, 60, 1);
            result["economyRaidShutdownPercent"] = new NumberOption("Applied raid damage for food shutdown (%)", 25, 5, 100, 1);
            result["economyRaidPartialDays"] = new NumberOption("Partial raid recovery (functioning days)", 40, 1, 365, 1);
            result["economyRaidFullDays"] = new NumberOption("Full looting recovery (functioning days)", 60, 1, 730, 1);
            result["economyRaidStressSpeed"] = new NumberOption("Stressed recovery speed (fraction)", .5, .05, 1, .05);
            result["economyNpcTradeCaravanPrice"] = new NumberOption("Caravan formation price (denars)", 15000, 1, 250000, 1);
            result["economyNpcTradeConvoyPrice"] = new NumberOption("Convoy formation price (denars)", 30000, 1, 250000, 1);
            result["economyNpcTradeCashPercent"] = new NumberOption("Maximum share of clan cash (%)", 50, 1, 100, 1);
            result["economyNpcTradeDailyChance"] = new NumberOption("Daily launch chance per eligible clan (%)", 4, 0, 100, .1);
            result["economyNpcTradeCaravanLimit"] = new NumberOption("Active caravans per clan", 1, 1, 8, 1);
            result["economyNpcTradeConvoyLimit"] = new NumberOption("Active convoys per clan", 1, 1, 8, 1);
            return new ReadOnlyDictionary<string, NumberOption>(result);
        }
        private readonly IReadOnlyDictionary<string, object> _values;
        private WarEconomyOptions(Dictionary<string, object> values) => _values = new ReadOnlyDictionary<string, object>(values);
        public static WarEconomyOptions Defaults { get; } = new WarEconomyOptions(DefaultValues());
        public static Dictionary<string, object> DefaultValues()
        {
            var values = Switches.Keys.ToDictionary(key => key, key => (object)!key.StartsWith(NpcTradePrefix, StringComparison.Ordinal), StringComparer.Ordinal);
            foreach (var pair in Numbers) values[pair.Key] = pair.Value.Default;
            return values;
        }
        public bool Enabled(string key) => (bool)_values[key];
        public double Number(string key) => (double)_values[key];
        public Dictionary<string, object> Values() => _values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        public static bool TryCreate(IDictionary<string, object> input, bool requireComplete, out WarEconomyOptions options, out string error)
        {
            options = Defaults;
            error = "";
            if (input == null) { error = "War Economy options are missing."; return false; }
            var values = DefaultValues();
            foreach (string key in input.Keys.Where(key => key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)))
                if (!values.ContainsKey(key)) { error = "Unknown War Economy option: " + key; return false; }
            foreach (string key in Switches.Keys)
            {
                if (!input.TryGetValue(key, out var value)) { if (requireComplete) { error = "Missing " + key; return false; } continue; }
                if (!(value is bool)) { error = Switches[key] + " must be on or off."; return false; }
                values[key] = value;
            }
            foreach (var pair in Numbers)
            {
                if (!input.TryGetValue(pair.Key, out var value)) { if (requireComplete) { error = "Missing " + pair.Key; return false; } continue; }
                if (!(value is byte || value is short || value is int || value is long || value is float || value is double || value is decimal))
                { error = pair.Value.Label + " must be a number."; return false; }
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                double steps = (number - pair.Value.Minimum) / pair.Value.Step;
                if (double.IsNaN(number) || double.IsInfinity(number) || number < pair.Value.Minimum || number > pair.Value.Maximum
                    || Math.Abs(steps - Math.Round(steps)) > .000001)
                { error = pair.Value.Label + " must be from " + pair.Value.Minimum.ToString(CultureInfo.InvariantCulture) + " to " + pair.Value.Maximum.ToString(CultureInfo.InvariantCulture) + " in steps of " + pair.Value.Step.ToString(CultureInfo.InvariantCulture) + "."; return false; }
                // Values within floating-point tolerance represent the same setting.
                // Canonicalize before integer wage casts and persistence.
                values[pair.Key] = Math.Round(pair.Value.Minimum + Math.Round(steps) * pair.Value.Step, 8);
            }
            double N(string key) => (double)values[key];
            if (N("economyRaidFullDays") < N("economyRaidPartialDays"))
            { error = "Full looting recovery must not be shorter than partial raid recovery."; return false; }
            for (int tier = 1; tier < 8; tier++)
                if (N("economyTroopWageTier" + tier) < N("economyTroopWageTier" + (tier - 1))
                    || N("economyRecruitTier" + tier) < N("economyRecruitTier" + (tier - 1)))
                { error = "Higher troop tiers cannot have a lower wage or recruitment multiplier."; return false; }
            double a = N("economyVillageFood10"), b = N("economyVillageFood300"), c = N("economyVillageFood600"), d = N("economyVillageFood1200");
            if (a >= b || b > c || c > d || (d - c) / 600 > (c - b) / 300 + .000001)
            { error = "Village output must rise from 10 to 300 hearths, never decrease, and grow no faster above 600 hearths."; return false; }
            options = new WarEconomyOptions(values);
            return true;
        }
        public int TroopWage(int tier, bool mercenary, int nativeWage)
            => !Enabled("economyTroopWagesEnabled") ? nativeWage : (int)(Number("economyTroopWageTier" + Math.Max(0, Math.Min(7, tier))) * (mercenary ? 1.5 : 1));
        public double RecruitMultiplier(int tier) => Enabled("economyRecruitPricesEnabled") ? Number("economyRecruitTier" + Math.Max(0, Math.Min(7, tier))) : 1;
        public bool NpcTradeEnabled(bool convoy) => Enabled(convoy ? "economyNpcTradeConvoysEnabled" : "economyNpcTradeCaravansEnabled");
        public int NpcTradePrice(bool convoy) => (int)Number(convoy ? "economyNpcTradeConvoyPrice" : "economyNpcTradeCaravanPrice");
        public int NpcTradeLimit(bool convoy) => (int)Number(convoy ? "economyNpcTradeConvoyLimit" : "economyNpcTradeCaravanLimit");
        public int NpcTradeCashPercent => (int)Number("economyNpcTradeCashPercent");
        public int NpcTradeDailyBasisPoints => (int)Math.Round(Number("economyNpcTradeDailyChance") * 100);
        public int ReserveDays(bool upgrade) => Enabled("economyReservesEnabled") ? (int)Number(upgrade ? "economyUpgradeReserveDays" : "economyRecruitReserveDays") : 0;
        public double GarrisonWageFactor => Enabled("economyGarrisonWagesEnabled") ? Number("economyGarrisonWageFactor") : 1;
        public int GarrisonMenPerFood => Enabled("economyGarrisonFoodEnabled") ? (int)Number("economyGarrisonMenPerFood") : 20;
        public double HealthyFood(double hearth, double nativeFood)
        {
            if (!Enabled("economyVillageCurveEnabled")) return nativeFood;
            hearth = Math.Max(0, hearth);
            double a = Number("economyVillageFood10"), b = Number("economyVillageFood300"), c = Number("economyVillageFood600"), d = Number("economyVillageFood1200");
            double output = hearth <= 10 ? a * Math.Pow(hearth / 10, Math.Log(6) / Math.Log(30))
                : hearth <= 300 ? a * Math.Pow(hearth / 10, Math.Log(b / a) / Math.Log(30))
                : hearth <= 600 ? b + (c - b) * (hearth - 300) / 300 : c + (d - c) * (hearth - 600) / 600;
            return output * Number("economyVillageOutputScale");
        }
    }
}
