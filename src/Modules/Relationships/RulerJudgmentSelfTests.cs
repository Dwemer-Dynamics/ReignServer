using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReignBeta.Shared.Rulers;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunRulerJudgmentSelfTests()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)) throw new InvalidOperationException("Ruler checks require isolated Verification Lab storage.");
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass) => results.Add(TestDict("caseId", "rulers_" + id, "passed", pass, "summary", id));
            foreach (var key in RulerOptions.Switches.Keys)
            {
                Check(key + "_boolean_validation", ValidateRulerOptions(TestDict(key, false)).Length == 0 && ValidateRulerOptions(TestDict(key, "false")).Length > 0
                    && ValidateRulerOptions(TestDict(key, 0)).Length > 0 && RulerOptionsHtml().Contains("id='" + key + "'"));
            }
            Check("unknown_switch_rejected", ValidateRulerOptions(TestDict("rulerInventedOption", true)).Length > 0);
            var settings = DefaultSettings(); settings["private_fixture"] = "preserve";
            Check("settings_api_defaults_are_explicit_booleans", RulerOptions.Switches.Keys.All(k => settings.TryGetValue(k, out var value) && value is bool on && on)
                && RulerOptions.Switches.Keys.All(k => ReadBool(SettingsForUi(settings), k, false)));
            MergeProviderSettings(settings, TestDict(RulerOptions.Master, false)); MergeProviderSettings(settings, TestDict(RulerOptions.Capture, false));
            var restored = TryParseJsonObject(Json.Serialize(settings));
            Check("partial_merge_roundtrip", !ReadBool(restored, RulerOptions.Master, true) && !ReadBool(restored, RulerOptions.Capture, true)
                && ReadBool(RulerOptionsForClient(restored), RulerOptions.Judgments, false) && ReadString(restored, "private_fixture", "") == "preserve");
            Check("production_web_options_registered", ReignXpOptionsHtml().Contains("id='rulerOptions'") && ReignXpOptionsScript().Contains("saveRulerOptions")
                && ReadDictionary(ReignXpOptionsForClient(restored), "rulers")?.Count == RulerOptions.Switches.Count);
            string fixture = Path.Combine(VerificationDir, "ruler-options-fixture.json");
            WriteJsonObject(fixture, TestDict("markup", RulerOptionsHtml(), "script", RulerOptionsScript()));
            Check("production_browser_fixture", File.Exists(fixture));
            foreach (string key in Reign.Core.Contracts.World.ReignNewsCategories.Switches.Keys)
            {
                Check("news_" + key, ValidateAnnouncementOptions(TestDict(key, false)).Length == 0
                    && ValidateAnnouncementOptions(TestDict(key, "false")).Length > 0
                    && ValidateAnnouncementOptions(TestDict(key, 0)).Length > 0
                    && AnnouncementOptionsHtml().Contains("id='" + key + "'"));
            }
            Check("news_unknown_rejected", ValidateAnnouncementOptions(TestDict("announcementInvented", true)).Length > 0);
            var newsSettings = DefaultSettings(); newsSettings["unrelatedNewsFixture"] = "preserve";
            MergeProviderSettings(newsSettings, TestDict("announcementPopupsEnabled", false));
            MergeProviderSettings(newsSettings, TestDict("announcementWorldEventsEnabled", false));
            MergeProviderSettings(newsSettings, TestDict(Reign.Core.Contracts.World.ReignNewsCategories.Government, false));
            var newsRestored = TryParseJsonObject(Json.Serialize(newsSettings));
            Check("news_partial_merge_roundtrip", !ReadBool(newsRestored, "announcementPopupsEnabled", true)
                && !ReadBool(newsRestored, "announcementWorldEventsEnabled", true)
                && !ReadBool(newsRestored, Reign.Core.Contracts.World.ReignNewsCategories.Government, true)
                && ReadBool(AnnouncementOptionsForClient(newsRestored), "announcementExecutionsEnabled", false)
                && ReadString(newsRestored, "unrelatedNewsFixture", "") == "preserve"
                && ReadDictionary(ReignXpOptionsForClient(newsRestored), "announcements")?.Count == Reign.Core.Contracts.World.ReignNewsCategories.Switches.Count);
            Check("news_production_registration", ReignXpOptionsHtml().Contains("id='announcementOptions'")
                && ReignXpOptionsScript().Contains("loadAnnouncementOptions(settings)")
                && Reign.Core.Contracts.World.ReignNewsCategories.Switches.Keys.All(k => ReadBool(SettingsForUi(DefaultSettings()), k, false)));
            WriteJsonObject(Path.Combine(VerificationDir, "announcement-options-fixture.json"),
                TestDict("markup", AnnouncementOptionsHtml(), "script", AnnouncementOptionsScript()));
            string campaign = "rulers_test_" + Guid.NewGuid().ToString("N"), timeline = "test";
            try
            {
                var p = TestDict("campaignId", campaign, "timelineId", timeline, "worldDay", 100d, "victorId", "victor", "prisonerId", "prisoner",
                    "clans", new[] { TestDict("clanId", "clan", "leaderId", "prisoner") });
                var context = RulerRelationsApi("context", p);
                Check("authoritative_context", ReadBool(context, "ok", false) && ReadDictionary(context, "traits")?.Count == 10 && ReadDictionaryList(context, "clans").Count == 1);
                Check("unknown_personality_is_explicitly_neutral", ReadString(context, "traitSource", "") == "neutral_missing_trait_evidence"
                    && ReadDictionary(context, "traits").Values.All(v => Convert.ToDouble(v) == 50d)
                    && !File.Exists(CharacterFile(campaign, "victor", "traits.json")));
                foreach (int version in new[] { 4, 5 })
                {
                    WriteJsonObject(CampaignFile(campaign, "campaign.json"), TestDict("narrativeVersion", version));
                    var library = CharacterProfileLibraryFor(TestDict("narrativeVersion", version));
                    foreach (string ruler in new[] { "lord_1_14", "lord_4_1", "lord_5_1" })
                    {
                        p["victorId"] = ruler;
                        var canonicalContext = RulerRelationsApi("context", p);
                        var expected = ReadDictionary(ReadDictionary(library[ruler], "traits"), "foundationTraits");
                        var actual = ReadDictionary(canonicalContext, "traits");
                        Check("unconstructed_shipped_ruler_v" + version + "_" + ruler,
                            ReadString(canonicalContext, "traitSource", "") == "shipped_foundation_traits"
                            && actual.All(t => Convert.ToDouble(t.Value) == Trait100(expected, t.Key))
                            && actual.Values.Any(v => Convert.ToDouble(v) != 50d)
                            && !File.Exists(CharacterFile(campaign, ruler, "traits.json")));
                    }
                }
                p["victorId"] = "lord_1_14";
                WriteJsonObject(CharacterFile(campaign, "lord_1_14", "traits.json"), TestDict("foundationTraits", TestDict("mercy", -2d)));
                var corrected = RulerRelationsApi("context", p);
                Check("campaign_override_precedes_shipped_and_missing_keys_stay_neutral", ReadString(corrected, "traitSource", "") == "campaign_foundation_traits"
                    && ReadDouble(ReadDictionary(corrected, "traits"), "mercy", -1) == 0d
                    && ReadDouble(ReadDictionary(corrected, "traits"), "ambition", -1) == 50d);
                p["victorId"] = "victor";
                p["victorProfile"] = TestDict("heroStringId", "someone_else", "traits", TestDict("mercy", 1));
                Check("mismatched_native_personality_rejected", !ReadBool(RulerRelationsApi("context", p), "ok", true));
                p["victorProfile"] = TestDict("heroStringId", "victor", "age", 45d, "isLord", true,
                    "traits", TestDict("mercy", 1), "skills", TestDict("leadership", 200));
                var dynamicContext = RulerRelationsApi("context", p);
                Check("dynamic_ruler_uses_native_personality_without_construction", ReadString(dynamicContext, "traitSource", "") == "transient_deterministic_native_traits_and_skills"
                    && ReadDouble(ReadDictionary(dynamicContext, "traits"), "mercy", -1) == 100d
                    && !File.Exists(CharacterFile(campaign, "victor", "traits.json")));
                p.Remove("victorProfile");
                string generation = ReadString(context, "generation", "");
                var delta = TestDict("subjectId", "prisoner", "targetId", "victor", "delta", 75, "reason", "submission");
                var payment = TestDict("campaignId", campaign, "timelineId", timeline, "worldDay", 100d, "generation", generation,
                    "operationId", "submission_once", "deltas", new[] { delta });
                var first = RulerRelationsApi("relations", payment); var replay = RulerRelationsApi("relations", payment);
                Check("consequence_receipt_and_replay", ReadBool(first, "ok", false) && ReadBool(replay, "replay", false));
                using var c = OpenCampaignConnection(campaign);
                var pair = QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;", TestDict("pair", AmbientPairKey("prisoner", "victor"))).Single();
                Check("submission_is_directional_exact_and_not_repeated", ReadInt(pair, "affinity_a_to_b", -1) == 75 && ReadInt(pair, "affinity_b_to_a", -1) == 0);
                delta["delta"] = -30; delta["reason"] = "unauthorized_execution";
                Check("changed_receipt_terms_rejected", !ReadBool(RulerRelationsApi("relations", payment), "ok", true));
                payment["operationId"] = "wrong_generation"; payment["generation"] = "not_current";
                Check("restore_generation_guard", !ReadBool(RulerRelationsApi("relations", payment), "ok", true));
                payment["generation"] = generation; delta["delta"] = -60;
                Check("sovereign_reaction_supported", ReadBool(RulerRelationsApi("relations", payment), "ok", false));
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
