using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        private static void RunNarrativePersistenceTests(Dictionary<string, object> fixture, Action<string, bool, string> check)
        {
            string campaign = "narrative_contract_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            const string hero = "narrative_contract_hero";
            string directory = Path.GetFullPath(CampaignFileService.CampaignDirectory(campaign));
            string allowedRoot = Path.GetFullPath(CampaignFileService.CampaignsRoot()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Narrative fixture escaped test campaign storage.");
            try
            {
                CampaignFileService.WriteCampaignMetadata(campaign, new Dictionary<string, object> { ["narrativeVersion"] = 5 });
                check("campaign_marker_for_unseen_character", ReadInt(WithNarrativeCampaignVersion(campaign, new Dictionary<string, object>()), "narrativeVersion", 0) == 5
                    && ReadInt(WithNarrativeCampaignVersion(campaign, new Dictionary<string, object> { ["narrativeVersion"] = 0 }), "narrativeVersion", -1) == 0,
                    "Unseen characters inherit the saved campaign generation version; an explicit older native save marker stays authoritative.");
                var basis = DeepCloneProfileDictionary(fixture); basis["heroStringId"] = hero;
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, hero, "narrative.json"), basis);
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, hero, "profile.json"), new Dictionary<string, object> { ["heroStringId"] = hero, ["narrativeVersion"] = 5 });
                CheckNarrativePublication(campaign, hero, basis, check);
                var target = ReadDictionaryList(basis, "items").First(x => ReadString(x, "category", "") == "fear");
                var proposal = new Dictionary<string, object> { ["itemId"] = ReadString(target, "id", ""), ["influence"] = 8,
                    ["description"] = "Approaches the familiar concern with deliberate calm and careful preparation.",
                    ["personalMeaning"] = "Learning to cope over many weeks has made this concern more manageable.",
                    ["supportingQuote"] = "Over these months I have learned to approach this concern with deliberate calm and careful preparation.",
                    ["quoteValidated"] = true, ["compatibilityValidated"] = true };
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    EnsureDynamicCharacteristicsSchema(connection); EnsureCharacterEditorSchema(connection);
                    CharactersSelfTestStore.InsertCharacterEditorHeads(connection, new Dictionary<string, object> { ["hero"] = hero });
                    StoreNarrativeDevelopment(connection, campaign, hero, proposal, "first", "exchange-1", "", 1, 100);
                    StoreNarrativeDevelopment(connection, campaign, hero, proposal, "second", "exchange-2", "", 15, 200);
                    var accepted = StoreNarrativeDevelopment(connection, campaign, hero, proposal, "third", "exchange-3", "", 31, 300);
                    var replay = StoreNarrativeDevelopment(connection, campaign, hero, proposal, "third", "exchange-3", "", 31, 300);
                    var rows = CharactersSelfTestStore.SelectDynamicCharacteristics3(connection, new Dictionary<string, object> { ["hero"] = hero });
                    check("development_database_and_replay", ReadString(accepted, "status", "") == "active" && rows.Count == 3
                        && ReadString(replay, "characteristic_id", "") == ReadString(accepted, "characteristic_id", ""),
                        "Three qualifying encounters persist one accepted change; replaying an exchange adds no duplicate evidence or override.");
                    var head = CharactersSelfTestStore.SelectCharacterEditorHeads(connection, new Dictionary<string, object> { ["hero"] = hero }).First();
                    check("development_editor_conflict", ReadString(head, "revision_token", "") != "before",
                        "Development invalidates an already-open editor revision so a stale draft cannot silently overwrite it.");
                    // Fill the ordinary projection beyond its display cap.
                    for (int i = 0; i < 260; i++) CharactersSelfTestStore.InsertDynamicCharacteristics(connection, new Dictionary<string, object> { ["id"] = "ordinary_" + i, ["hero"] = hero });
                    ProjectDynamicCharacteristicsFile(campaign, hero, connection);
                    var projection = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign, hero, "dynamic_characteristics.json"));
                    check("active_override_survives_projection_cap", ReadDictionaryList(projection, "active")
                        .Any(x => ReadString(x, "characteristic_id", "") == ReadString(accepted, "characteristic_id", "")),
                        "Accepted narrative development remains effective even with more than 250 higher-ranked ordinary characteristics.");
                    var edited = ResolveEffectiveNarrative(new Dictionary<string, object> { ["narrative"] = basis, ["dynamicCharacteristics"] = projection });
                    ReadDictionaryList(edited, "items").First(x => ReadString(x, "id", "") == ReadString(target, "id", ""))["influence"] = 2;
                    var effective = ResolveEffectiveNarrative(new Dictionary<string, object> { ["narrative"] = basis, ["dynamicCharacteristics"] = projection });
                    var merged = MergeEditedNarrative(basis, effective, edited);
                    CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, hero, "narrative.json"), merged);
                }
                ReconcileEditedNarrative(campaign, hero);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var rows = CharactersSelfTestStore.SelectDynamicCharacteristics4(connection, new Dictionary<string, object> { ["hero"] = hero });
                    check("editor_retains_superseded_history", rows.Count == 3 && rows.Count(x => ReadString(x, "status", "") == "superseded") == 1,
                        "Manual correction supersedes the accepted version without deleting its observations or history.");
                    var next = StoreNarrativeDevelopment(connection, campaign, hero, proposal, "fourth", "exchange-4", "", 90, 500);
                    check("editor_resets_old_evidence_basis", ReadString(next, "status", "") != "active",
                        "Observations based on the old manually corrected concern cannot immediately authorize another change.");
                }
            }
            catch (Exception ex) { check("persistence_fixture", false, ex.ToString()); }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void CheckNarrativePublication(string campaign, string heroId, Dictionary<string, object> narrative, Action<string, bool, string> check)
        {
            const string native = "Mira is a town notable.";
            const string approved = "Neighbors see Mira checking the market scales.";
            const string conflicting = "The old account calls her Sabra, a queen aged 72 and the daughter of Oren.";
            var profile = TestDict("heroStringId", heroId, "name", "Mira", "occupation", "Town notable", "isNotable", true,
                "isFemale", true, "age", 41, "fatherId", "native_father", "fatherName", "Beren", "nativeEncyclopediaText", native);
            var authored = DeepCloneProfileDictionary(narrative);
            var life = ReadDictionary(authored, "life");
            life["summary"] = conflicting;
            life["publicSummary"] = approved;
            string privateBackstory = ReadString(life, "privateBackstory", "");
            string narrativePath = CampaignFileService.CharacterFile(campaign, heroId, "narrative.json");
            string backgroundPath = CampaignFileService.CharacterFile(campaign, heroId, "background.json");
            var original = File.ReadAllBytes(narrativePath);
            var stack = TestDict("narrative", authored, "background", TestDict("nativeEncyclopediaText", native));
            ApplyNarrativePromptProjection(stack);
            CampaignFileService.WriteJsonObject(backgroundPath, ReadDictionary(stack, "background"));
            string publicText = BuildEncyclopediaBackstoryText(campaign, heroId);
            check("publication_encyclopedia_uses_public_projection", publicText == native + "\n\n" + approved,
                "The shipped encyclopedia response builder returns the native biography and the intentional public summary, excluding the conflicting life account and private backstory.");
            check("publication_conflicting_prose_does_not_fail_schema", NarrativeDocumentReady(authored),
                "Narrative readiness checks the document contract. Native factual authority is checked separately.");
            DialogueService.CheckNarrativePromptPublication(profile, stack, privateBackstory, conflicting, check);

            var legacy = TestDict("summary", privateBackstory);
            CampaignFileService.WriteJsonObject(backgroundPath, legacy);
            byte[] legacyBytes = File.ReadAllBytes(backgroundPath);
            check("publication_missing_public_field_keeps_private_legacy_summary", BuildEncyclopediaBackstoryText(campaign, heroId) == ""
                && File.ReadAllBytes(backgroundPath).SequenceEqual(legacyBytes)
                && File.ReadAllBytes(narrativePath).SequenceEqual(original),
                "Reading a legacy background without a public field excludes its private-capable summary and preserves both files byte for byte.");
            EnsureEncyclopediaText(legacy, TestDict());
            check("publication_construction_cannot_promote_summary", ReadString(legacy, "encyclopediaText", "") == ""
                && ReadString(legacy, "summary", "") == privateBackstory,
                "Legacy construction keeps general history for the owner and leaves the public field empty when no public material is available.");
            legacy["nativeEncyclopediaText"] = native;
            CampaignFileService.WriteJsonObject(backgroundPath, legacy);
            check("publication_blank_public_field_falls_back_to_native_text", BuildEncyclopediaBackstoryText(campaign, heroId) == native,
                "An explicit blank public field can fall back to the retained native encyclopedia text.");
            legacy["encyclopediaText"] = privateBackstory;
            legacy.Remove("nativeEncyclopediaText");
            CampaignFileService.WriteJsonObject(backgroundPath, legacy);
            byte[] promotedBytes = File.ReadAllBytes(backgroundPath);
            check("publication_older_duplicated_summary_stays_private", BuildEncyclopediaBackstoryText(campaign, heroId) == ""
                && File.ReadAllBytes(backgroundPath).SequenceEqual(promotedBytes),
                "The old construction path could copy private narrative into the public field. Reading it causes no rewrite or publication.");
            EnsureEncyclopediaText(legacy, TestDict("nativeEncyclopediaText", native));
            check("publication_native_refresh_excludes_older_duplicated_summary",
                ReadString(legacy, "encyclopediaText", "") == native && ReadString(legacy, "summary", "").Contains(privateBackstory),
                "Refreshing a native biography preserves the owner-only summary and publishes only the supplied native text.");

            var intentionallyPublic = DeepCloneProfileDictionary(authored);
            ReadDictionary(intentionallyPublic, "life")["summary"] = approved;
            var currentProjection = TestDict("narrative", intentionallyPublic, "background", TestDict());
            ApplyNarrativePromptProjection(currentProjection);
            CampaignFileService.WriteJsonObject(backgroundPath, ReadDictionary(currentProjection, "background"));
            check("publication_v5_explicit_public_text_can_equal_summary", BuildEncyclopediaBackstoryText(campaign, heroId) == approved,
                "Version-five publication deliberately supplies a public summary even when the general summary uses the same words.");
        }
    }
}
