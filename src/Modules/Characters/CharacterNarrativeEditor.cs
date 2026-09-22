using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> MergeEditedNarrative(Dictionary<string, object> basis,
            Dictionary<string, object> effective, Dictionary<string, object> incoming)
        {
            if (!NarrativeDocumentReady(basis)) throw new InvalidOperationException("Expanded interests must be constructed before editing.");
            var result = DeepCloneProfileDictionary(basis);
            foreach (string part in new[] { "life", "voice" })
            {
                var edited = ReadDictionary(incoming, part);
                var original = ReadDictionary(result, part);
                foreach (string key in original.Keys.ToList())
                    if (edited != null && edited.ContainsKey(key)) original[key] = edited[key];
                result[part] = original;
            }
            var baseItems = ReadDictionaryList(basis, "items");
            var currentItems = ReadDictionaryList(effective, "items");
            var merged = new List<Dictionary<string, object>>();
            foreach (var item in ReadDictionaryList(incoming, "items"))
            {
                string id = ReadString(item, "id", "");
                var original = baseItems.FirstOrDefault(x => ReadString(x, "id", "") == id);
                var current = currentItems.FirstOrDefault(x => ReadString(x, "id", "") == id);
                if (original != null && current != null && NarrativeItemFingerprint(item) == NarrativeItemFingerprint(current))
                {
                    merged.Add(DeepCloneProfileDictionary(original));
                    continue;
                }
                var changed = original == null ? new Dictionary<string, object> { ["id"] = id, ["family"] = "personal" }
                    : DeepCloneProfileDictionary(original);
                foreach (string key in new[] { "category", "title", "description", "personalMeaning", "influence", "visibility" })
                    if (item.ContainsKey(key)) changed[key] = item[key];
                string category = ReadString(changed, "category", "");
                if (new[] { "fear", "wound", "jealousy", "temptation", "secret" }.Contains(category)) changed["visibility"] = "private";
                else if (!changed.ContainsKey("visibility")) changed["visibility"] = "personal";
                if (original == null || ReadString(original, "title", "") != ReadString(changed, "title", ""))
                    changed["meaningKey"] = NormalizeLookup(ReadString(changed, "title", ""));
                changed["source"] = "character_editor";
                changed.Remove("developmentId");
                merged.Add(changed);
            }
            result["items"] = merged;
            result["definingIds"] = SelectDefiningNarrativeIds(merged, ReadString(result, "seed", ""), ReadStringList(effective, "definingIds"));
            result["revision"] = ReadInt(basis, "revision", 1) + 1;
            var errors = ValidateCharacterNarrative(result, true);
            if (errors.Count > 0) throw new InvalidOperationException("Interests were not saved: " + string.Join(" ", errors));
            return result;
        }

        private static void PrepareEditorNarrative(string campaign, string hero, Dictionary<string, object> documents)
        {
            var incoming = ReadDictionary(documents, "narrative");
            if (incoming == null || incoming.Count == 0) return;
            var basis = ReadJsonObject(CharacterFile(campaign, hero, "narrative.json"));
            var effective = ResolveEffectiveNarrative(new Dictionary<string, object> { ["narrative"] = basis,
                ["dynamicCharacteristics"] = ReadJsonObject(CharacterFile(campaign, hero, "dynamic_characteristics.json")) });
            documents["narrative"] = MergeEditedNarrative(basis, effective, incoming);
        }

        private static void ReconcileEditedNarrative(string campaign, string hero)
        {
            var basis = ReadJsonObject(CharacterFile(campaign, hero, "narrative.json"));
            if (!NarrativeDocumentReady(basis)) return;
            var items = ReadDictionaryList(basis, "items");
            using (var connection = OpenCampaignConnection(campaign))
            {
                foreach (var row in QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE owner_id=$hero AND category='narrative_development' AND status='active';",
                    new Dictionary<string, object> { ["hero"] = hero }))
                {
                    var change = ReadDictionary(TryParseJsonObject(ReadString(row, "payload_json", "{}")), "narrativeDevelopment");
                    var item = items.FirstOrDefault(x => ReadString(x, "id", "") == ReadString(change, "itemId", ""));
                    if (item != null && ReadString(change, "baseItemFingerprint", "") == NarrativeItemFingerprint(item)) continue;
                    ExecuteSql(connection, "UPDATE dynamic_characteristics SET status='superseded',rejection_reason='superseded_by_character_editor' WHERE characteristic_id=$id;",
                        new Dictionary<string, object> { ["id"] = ReadString(row, "characteristic_id", "") });
                }
                ProjectDynamicCharacteristicsFile(campaign, hero, connection);
            }
            var stack = new Dictionary<string, object> { ["narrative"] = basis,
                ["dynamicCharacteristics"] = ReadJsonObject(CharacterFile(campaign, hero, "dynamic_characteristics.json")),
                ["background"] = ReadJsonObject(CharacterFile(campaign, hero, "background.json")) };
            ApplyNarrativePromptProjection(stack);
            foreach (var part in new Dictionary<string, string> { ["background"] = "background", ["voice"] = "voice", ["motivations"] = "motivations",
                ["saveQuirks"] = "save_quirks", ["hiddenHistory"] = "hidden_history", ["secrets"] = "secrets" })
                WriteJsonObject(CharacterFile(campaign, hero, part.Value + ".json"), stack[part.Key]);
        }
    }
}
