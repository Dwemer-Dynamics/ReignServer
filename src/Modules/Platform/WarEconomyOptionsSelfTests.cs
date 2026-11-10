using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EconomyOptions = Reign.Core.Contracts.Economy.WarEconomyOptions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunWarEconomyOptionsSelfTests()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)) throw new InvalidOperationException("Economy options checks require the isolated Verification Lab.");
            var rows = new List<Dictionary<string, object>>();
            void Check(string id, bool ok) => rows.Add(TestDict("caseId", "war_economy_options." + id, "suite", "interaction_architecture", "passed", ok));
            var settings = DefaultSettings(); settings["private_fixture"] = "preserve";
            Check("defaults_and_public_projection", ReadWarEconomyOptions(settings).TroopWage(5, false, 0) == 40
                && ReadDictionary(WarEconomyOptionsForClient(settings), "values").Count == EconomyOptions.DefaultValues().Count
                && !ReadDictionary(WarEconomyOptionsForClient(settings), "values").ContainsKey("private_fixture"));
            foreach (var pair in EconomyOptions.Switches)
            {
                Check(pair.Key + ".strict", ValidateWarEconomyPatch(settings, TestDict(pair.Key, false)) == ""
                    && ValidateWarEconomyPatch(settings, TestDict(pair.Key, "false")).Length > 0);
                MergeProviderSettings(settings, TestDict(pair.Key, false));
            }
            var restored = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(settings));
            Check("partial_json_roundtrip", EconomyOptions.Switches.Keys.All(k => !ReadWarEconomyOptions(restored).Enabled(k))
                && ReadString(restored, "private_fixture", "") == "preserve"
                && ReadWarEconomyOptions(restored).Number("economyTroopWageTier5") == 40);
            Check("merged_validation", ValidateWarEconomyPatch(restored, TestDict("economyTroopWageTier5", 100)).Length > 0
                && ValidateWarEconomyPatch(restored, TestDict("economyTroopWageTier5", 45)).Length == 0
                && ValidateWarEconomyPatch(restored, TestDict("economyVillageFood600", 5)).Length > 0
                && ValidateWarEconomyPatch(restored, TestDict("economyInvented", true)).Length > 0
                && ValidateWarEconomyPatch(restored, TestDict("EconomyTroopWagesEnabled", false)).Length > 0);
            foreach (object invalid in new object[] { double.NaN, double.PositiveInfinity, "40", true, 40.5, -1 })
                Check("invalid_" + rows.Count, ValidateWarEconomyPatch(restored, TestDict("economyTroopWageTier5", invalid)).Length > 0);
            Check("raid_days_order", ValidateWarEconomyPatch(restored, TestDict("economyRaidPartialDays", 70)) != ""
                && ValidateWarEconomyPatch(restored, TestDict("economyRaidPartialDays", 70,"economyRaidFullDays", 90)) == "");
            Check("stale_revision", ValidateWarEconomyPatch(restored, TestDict("expectedSettingsRevision", "stale", "economyRaidFoodEnabled", true)) != "");
            restored["settingsRevision"] = "current";
            Check("matched_revision", ValidateWarEconomyPatch(restored, TestDict("expectedSettingsRevision", "current", "economyRaidFoodEnabled", true)) == "");
            Check("old_client_cannot_overwrite_prices", ValidateWarEconomyPatch(restored, TestDict("economyRecruitTier7",11.5),requireRevision:true)!=""
                && ValidateWarEconomyPatch(restored, TestDict("economyRecruitTier7",ReadWarEconomyOptions(restored).Number("economyRecruitTier7")),requireRevision:true)=="");
            var trade = ReadWarEconomyOptions(restored);
            Check("npc_trade_defaults", !trade.NpcTradeEnabled(false) && !trade.NpcTradeEnabled(true)
                && trade.NpcTradePrice(false) == 15000 && trade.NpcTradePrice(true) == 30000
                && trade.NpcTradeCashPercent == 50 && trade.NpcTradeDailyBasisPoints == 400
                && trade.NpcTradeLimit(false) == 1 && trade.NpcTradeLimit(true) == 1);
            var tradePatch = TestDict("expectedSettingsRevision", "current", "economyNpcTradeConvoysEnabled", true,
                "economyNpcTradeConvoyPrice", 35000, "economyNpcTradeConvoyLimit", 2,
                "economyNpcTradeCashPercent", 45, "economyNpcTradeDailyChance", 6.5);
            string tradeError = ValidateWarEconomyPatch(restored, tradePatch, requireRevision: true);
            var tradeSettings = new Dictionary<string, object>(restored, StringComparer.Ordinal);
            MergeProviderSettings(tradeSettings, tradePatch);
            var appliedTrade = ReadWarEconomyOptions(Json.Deserialize<Dictionary<string, object>>(Json.Serialize(tradeSettings)));
            Check("npc_trade_revision_and_roundtrip", tradeError == "" && appliedTrade.NpcTradeEnabled(true)
                && !appliedTrade.NpcTradeEnabled(false) && appliedTrade.NpcTradePrice(true) == 35000
                && appliedTrade.NpcTradeLimit(true) == 2 && appliedTrade.NpcTradeCashPercent == 45
                && appliedTrade.NpcTradeDailyBasisPoints == 650);
            Check("npc_trade_rejects_invalid_values", ValidateWarEconomyPatch(restored, TestDict("economyNpcTradeConvoyLimit", 9)) != ""
                && ValidateWarEconomyPatch(restored, TestDict("economyNpcTradeCashPercent", 0)) != ""
                && ValidateWarEconomyPatch(restored, TestDict("economyNpcTradeDailyChance", 4.05)) != "");
            Check("production_registered", ReignXpOptionsHtml().Contains("id='warEconomyOptions'")
                && ReignXpOptionsScript().Contains("warEconomySettingsForSave")
                && ReignXpOptionsForClient(restored).ContainsKey("warEconomy"));
            WriteJsonObject(Path.Combine(VerificationDir, "war-economy-options-fixture.json"), TestDict(
                "schema", "reign-war-economy-options-browser-fixture-v1", "markup", WarEconomyOptionsHtml(), "script", WarEconomyOptionsScript(),
                "defaults", EconomyOptions.DefaultValues(), "switches", EconomyOptions.Switches.Keys.ToArray(),
                "numbers", EconomyOptions.Numbers.Keys.ToArray(), "gameplayOptions", WarEconomyOptionsForClient(DefaultSettings())));
            WriteJsonObject(Path.Combine(VerificationDir, "war-economy-full-options-fixture.json"), TestDict(
                "markup", ReignXpOptionsHtml(), "script", ReignXpOptionsScript(),
                "diagnosticsMarkup", NpcLivesDiagnosticsHtml(), "diagnosticsScript", NpcStoryEditorScript(),
                "definitions", ReignBeta.Shared.Characters.NpcStoryCatalog.All));
            return rows;
        }
    }
}
