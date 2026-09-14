using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void RunNarrativePersistenceTests(Dictionary<string, object> fixture, Action<string, bool, string> check)
        {
            string campaign = "narrative_contract_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            const string hero = "narrative_contract_hero";
            string directory = Path.GetFullPath(CampaignDirectory(campaign));
            string allowedRoot = Path.GetFullPath(CampaignsRoot()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Narrative fixture escaped test campaign storage.");
            try
            {
                WriteCampaignMetadata(campaign, new Dictionary<string, object> { ["narrativeVersion"] = 5 });
                check("campaign_marker_for_unseen_character", ReadInt(WithNarrativeCampaignVersion(campaign, new Dictionary<string, object>()), "narrativeVersion", 0) == 5
                    && ReadInt(WithNarrativeCampaignVersion(campaign, new Dictionary<string, object> { ["narrativeVersion"] = 0 }), "narrativeVersion", -1) == 0,
                    "Unseen characters inherit the saved campaign generation version; an explicit older native save marker stays authoritative.");
                var basis = DeepCloneProfileDictionary(fixture); basis["heroStringId"] = hero;
                WriteJsonObject(CharacterFile(campaign, hero, "narrative.json"), basis);
                WriteJsonObject(CharacterFile(campaign, hero, "profile.json"), new Dictionary<string, object> { ["heroStringId"] = hero, ["narrativeVersion"] = 5 });
                var target = ReadDictionaryList(basis, "items").First(x => ReadString(x, "category", "") == "fear");
                var proposal = new Dictionary<string, object> { ["itemId"] = ReadString(target, "id", ""), ["influence"] = 8,
                    ["description"] = "Approaches the familiar concern with deliberate calm and careful preparation.",
                    ["personalMeaning"] = "Learning to cope over many weeks has made this concern more manageable.",
                    ["supportingQuote"] = "Over these months I have learned to approach this concern with deliberate calm and careful preparation.",
                    ["quoteValidated"] = true, ["compatibilityValidated"] = true };
                using (var connection = OpenCampaignConnection(campaign))
                {
                    EnsureDynamicCharacteristicsSchema(connection); EnsureCharacterEditorSchema(connection);
                    ExecuteSql(connection, "INSERT INTO character_editor_heads(hero_id,revision_token,updated_ts) VALUES($hero,'before',0);", new Dictionary<string, object> { ["hero"] = hero });
                    StoreNarrativeDevelopment(connection, campaign, hero, proposal, "first", "exchange-1", "", 1, 100);
                    StoreNarrativeDevelopment(connection, campaign, hero, proposal, "second", "exchange-2", "", 15, 200);
                    var accepted = StoreNarrativeDevelopment(connection, campaign, hero, proposal, "third", "exchange-3", "", 31, 300);
                    var replay = StoreNarrativeDevelopment(connection, campaign, hero, proposal, "third", "exchange-3", "", 31, 300);
                    var rows = QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE owner_id=$hero;", new Dictionary<string, object> { ["hero"] = hero });
                    check("development_database_and_replay", ReadString(accepted, "status", "") == "active" && rows.Count == 3
                        && ReadString(replay, "characteristic_id", "") == ReadString(accepted, "characteristic_id", ""),
                        "Three qualifying encounters persist one accepted change; replaying an exchange adds no duplicate evidence or override.");
                    var head = QuerySql(connection, "SELECT revision_token FROM character_editor_heads WHERE hero_id=$hero;", new Dictionary<string, object> { ["hero"] = hero }).First();
                    check("development_editor_conflict", ReadString(head, "revision_token", "") != "before",
                        "Development invalidates an already-open editor revision so a stale draft cannot silently overwrite it.");
                    // Fill the ordinary projection beyond its display cap.
                    for (int i = 0; i < 260; i++) ExecuteSql(connection,
                        "INSERT INTO dynamic_characteristics(characteristic_id,owner_id,category,text,normalized_key,status,importance,first_ts,last_ts) VALUES($id,$hero,'habit','An ordinary stored habit.',$id,'active',2,400,400);",
                        new Dictionary<string, object> { ["id"] = "ordinary_" + i, ["hero"] = hero });
                    ProjectDynamicCharacteristicsFile(campaign, hero, connection);
                    var projection = ReadJsonObject(CharacterFile(campaign, hero, "dynamic_characteristics.json"));
                    check("active_override_survives_projection_cap", ReadDictionaryList(projection, "active")
                        .Any(x => ReadString(x, "characteristic_id", "") == ReadString(accepted, "characteristic_id", "")),
                        "Accepted narrative development remains effective even with more than 250 higher-ranked ordinary characteristics.");
                    var edited = ResolveEffectiveNarrative(new Dictionary<string, object> { ["narrative"] = basis, ["dynamicCharacteristics"] = projection });
                    ReadDictionaryList(edited, "items").First(x => ReadString(x, "id", "") == ReadString(target, "id", ""))["influence"] = 2;
                    var effective = ResolveEffectiveNarrative(new Dictionary<string, object> { ["narrative"] = basis, ["dynamicCharacteristics"] = projection });
                    var merged = MergeEditedNarrative(basis, effective, edited);
                    WriteJsonObject(CharacterFile(campaign, hero, "narrative.json"), merged);
                }
                ReconcileEditedNarrative(campaign, hero);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var rows = QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE owner_id=$hero AND category='narrative_development';", new Dictionary<string, object> { ["hero"] = hero });
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
    }
}
