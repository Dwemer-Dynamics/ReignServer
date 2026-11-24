using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        private static Dictionary<string, object> MergeEditedNarrative(Dictionary<string, object> basis,
            Dictionary<string, object> effective, Dictionary<string, object> incoming, bool editItems = true)
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
            if (editItems) MergeEditorNarrativeItems(basis, effective, incoming, result);
            ValidateEditedNarrative(basis, result);
            return result;
        }

        private static void MergeEditorNarrativeItems(Dictionary<string, object> basis, Dictionary<string, object> effective,
            Dictionary<string, object> incoming, Dictionary<string, object> result)
        {
            var baseItems = ReadDictionaryList(basis, "items");
            var currentItems = ReadDictionaryList(effective, "items");
            var merged = new List<Dictionary<string, object>>();
            foreach (var item in ReadDictionaryList(incoming, "items"))
            {
                string id = ReadString(item, "id", "");
                var original = baseItems.FirstOrDefault(x => ReadString(x, "id", "") == id);
                var current = currentItems.FirstOrDefault(x => ReadString(x, "id", "") == id);
                if (original != null && current != null && SameEditableNarrativeItem(item, current))
                {
                    merged.Add(DeepCloneProfileDictionary(original));
                    continue;
                }
                var changed = original == null ? new Dictionary<string, object> { ["id"] = id, ["family"] = "personal" }
                    : DeepCloneProfileDictionary(original);
                foreach (string key in NarrativeItemEditFields)
                    if (item.ContainsKey(key)) changed[key] = item[key];
                string category = ReadString(changed, "category", "");
                if (new[] { "fear", "wound", "jealousy", "temptation", "secret" }.Contains(category)) changed["visibility"] = "private";
                else if (!changed.ContainsKey("visibility")) changed["visibility"] = "personal";
                if (original == null || ReadString(original, "title", "") != ReadString(changed, "title", ""))
                    changed["meaningKey"] = ActionPipelineService.NormalizeLookup(ReadString(changed, "title", ""));
                changed["source"] = "character_editor";
                changed.Remove("developmentId");
                merged.Add(changed);
            }
            result["items"] = merged;
            result["definingIds"] = SelectDefiningNarrativeIds(merged, ReadString(result, "seed", ""), ReadStringList(effective, "definingIds"));
        }

        private static readonly string[] NarrativeItemEditFields =
            { "category", "title", "description", "personalMeaning", "influence", "visibility" };

        private static bool SameEditableNarrativeItem(Dictionary<string, object> incoming, Dictionary<string, object> current)
        {
            return NarrativeItemEditFields.All(key => !incoming.TryGetValue(key, out object value)
                || (key == "influence"
                    ? ReadDouble(incoming, key, double.NaN) == ReadDouble(current, key, double.NaN)
                    : current.TryGetValue(key, out object original) && CanonicalJson(value) == CanonicalJson(original)));
        }

        private static void ValidateEditedNarrative(Dictionary<string, object> basis, Dictionary<string, object> result)
        {
            result["revision"] = ReadInt(basis, "revision", 1) + 1;
            var errors = ValidateCharacterNarrative(result, true);
            if (errors.Count > 0) throw new InvalidOperationException("Narrative changes were not saved: " + string.Join(" ", errors));
        }

        private const string EditorNarrativeBasisKind = "narrative-edit-v1";
        private const string EditorNarrativeDeltaKey = "editorNarrativeDelta";
        private const string NarrativeInterestsCell = "items";

        private static readonly string[] NarrativeStoryCells =
        {
            "life.publicSummary", "life.summary", "life.origin", "life.upbringing", "life.reputation", "life.privateBackstory",
            "voice.nativeVoice", "voice.speechStyle", "voice.socialMask", "voice.tells"
        };

        private static Dictionary<string, object> ReadEditorNarrative(string campaign, string hero) =>
            CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign, hero, "narrative.json"));

        private static Dictionary<string, object> ReadEffectiveEditorNarrative(ReignDbConnection connection, string hero,
            Dictionary<string, object> raw)
        {
            var rows = CharactersStore.SelectDynamicCharacteristics9(connection, TestDict("owner", hero));
            return ResolveEffectiveNarrative(TestDict("narrative", raw,
                "dynamicCharacteristics", TestDict("heroStringId", hero, "active", rows)));
        }

        private static Dictionary<string, object> NarrativeEditableValues(Dictionary<string, object> narrative)
        {
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            if (narrative == null) return fields;
            foreach (string cell in NarrativeStoryCells)
            {
                int separator = cell.IndexOf('.');
                var part = ReadDictionary(narrative, cell[..separator]);
                if (part != null && part.TryGetValue(cell[(separator + 1)..], out object value)) fields.Add(cell, value);
            }
            if (narrative.TryGetValue(NarrativeInterestsCell, out object items)) fields.Add(NarrativeInterestsCell, items);
            return fields;
        }

        private static Dictionary<string, object> NarrativeEditorBasis(Dictionary<string, object> raw, Dictionary<string, object> effective)
        {
            var basis = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var field in NarrativeEditableValues(raw)) basis.Add("raw:" + field.Key, field.Value);
            foreach (var field in NarrativeEditableValues(effective)) basis.Add("effective:" + field.Key, field.Value);
            return basis;
        }

        private static void SetNarrativeCell(Dictionary<string, object> narrative, string cell, object value)
        {
            if (cell == NarrativeInterestsCell)
            {
                narrative[NarrativeInterestsCell] = value;
                return;
            }
            int separator = cell.IndexOf('.');
            string name = cell[..separator];
            var part = ReadDictionary(narrative, name) ?? new Dictionary<string, object>(StringComparer.Ordinal);
            part[cell[(separator + 1)..]] = value;
            narrative[name] = part;
        }

        private static bool TryPrepareEditorNarrative(ReignDbConnection connection, string campaign, string hero,
            string revision, string generation, string token, Dictionary<string, object> documents, Dictionary<string, object> rawBefore)
        {
            var incoming = ReadDictionary(documents, "narrative");
            if (incoming == null) return false;
            if (incoming.Count == 0)
            {
                documents.Remove("narrative");
                return true;
            }
            var loaded = token.Length <= 4096
                ? ReadEditorProfileBasis(campaign, hero, revision, generation, token, EditorNarrativeBasisKind) : null;
            if (loaded == null) return false;

            var raw = ReadEditorNarrative(campaign, hero);
            if (!NarrativeDocumentReady(raw)) return false;
            var effective = ReadEffectiveEditorNarrative(connection, hero, raw);
            var current = NarrativeEditorBasis(raw, effective);
            var edits = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var submitted in NarrativeEditableValues(incoming))
            {
                string rawKey = "raw:" + submitted.Key;
                string effectiveKey = "effective:" + submitted.Key;
                if (!loaded.TryGetValue(effectiveKey, out object loadedEffective)) return false;
                if (Equals(loadedEffective, EditorProfileValueHash(submitted.Value))) continue;

                if (!loaded.TryGetValue(rawKey, out object loadedRaw)
                    || !current.TryGetValue(rawKey, out object currentRaw)
                    || !current.TryGetValue(effectiveKey, out object currentEffective)
                    || !Equals(loadedRaw, EditorProfileValueHash(currentRaw))
                    || !Equals(loadedEffective, EditorProfileValueHash(currentEffective))) return false;

                // Effective interests can contain an active development projection. Replacing the
                // list supersedes these overlays, so record when raw-only undo cannot restore it.
                rawBefore[submitted.Key] = TestDict("value", currentRaw,
                    "rawWasEffective", EditorProfileValueHash(currentRaw) == EditorProfileValueHash(currentEffective));
                SetNarrativeCell(edits, submitted.Key, submitted.Value);
            }
            if (rawBefore.Count == 0)
                documents.Remove("narrative");
            else
                documents["narrative"] = MergeEditedNarrative(raw, effective, edits, edits.ContainsKey(NarrativeInterestsCell));
            return true;
        }

        private static void RecordEditorNarrativeDelta(string campaign, string hero, Dictionary<string, object> before,
            Dictionary<string, object> after, Dictionary<string, object> rawBefore)
        {
            before[EditorNarrativeDeltaKey] = rawBefore;
            var postimages = new Dictionary<string, object>(StringComparer.Ordinal);
            after[EditorNarrativeDeltaKey] = postimages;
            if (rawBefore.Count == 0) return;

            var raw = ReadEditorNarrative(campaign, hero);
            var current = NarrativeEditorBasis(raw, ReadDictionary(ReadDictionary(after, "documents"), "narrative"));
            foreach (string cell in rawBefore.Keys)
                postimages[cell] = TestDict("raw", EditorProfileValueHash(current["raw:" + cell]),
                    "effective", EditorProfileValueHash(current["effective:" + cell]));
        }

        private static Dictionary<string, object> PrepareEditorNarrativeRollback(ReignDbConnection connection, string campaign,
            string hero, Dictionary<string, object> original, Dictionary<string, object> after, Dictionary<string, object> documents,
            List<Dictionary<string, object>> issues)
        {
            var prior = ReadDictionary(documents, "narrative");
            documents?.Remove("narrative");
            var rawBefore = ReadDictionary(original, EditorNarrativeDeltaKey);
            var undo = new Dictionary<string, object>(StringComparer.Ordinal);
            if (rawBefore == null)
            {
                if (NarrativeDocumentReady(prior) && CanonicalJson(prior) != CanonicalJson(ReadDictionary(ReadDictionary(after, "documents"), "narrative")))
                    issues.Add(TestDict("group", "narrative", "reason", "legacy_narrative_ownership_unverified"));
                return undo;
            }
            if (rawBefore.Count == 0 || documents == null) return undo;

            var raw = ReadEditorNarrative(campaign, hero);
            var current = NarrativeEditorBasis(raw, ReadEffectiveEditorNarrative(connection, hero, raw));
            var expected = ReadDictionary(after, EditorNarrativeDeltaKey);
            var result = DeepCloneProfileDictionary(raw);
            bool ready = NarrativeDocumentReady(raw);
            foreach (var entry in rawBefore)
            {
                string cell = entry.Key;
                var beforeCell = ReadDictionary(rawBefore, cell);
                var afterCell = ReadDictionary(expected, cell);
                if (!ready || !ReadBool(beforeCell, "rawWasEffective", false)
                    || !current.TryGetValue("raw:" + cell, out object rawValue)
                    || !current.TryGetValue("effective:" + cell, out object effectiveValue)
                    || !beforeCell.TryGetValue("value", out object priorValue)
                    || ReadString(afterCell, "raw", "") != EditorProfileValueHash(rawValue)
                    || ReadString(afterCell, "effective", "") != EditorProfileValueHash(effectiveValue))
                {
                    issues.Add(TestDict("group", "narrative", "column", cell, "reason", "narrative_cell_undo_unverified"));
                    continue;
                }

                undo[cell] = TestDict("value", rawValue,
                    "rawWasEffective", EditorProfileValueHash(rawValue) == EditorProfileValueHash(effectiveValue));
                SetNarrativeCell(result, cell, priorValue);
            }
            if (undo.Count > 0)
            {
                if (undo.ContainsKey(NarrativeInterestsCell))
                    result["definingIds"] = SelectDefiningNarrativeIds(ReadDictionaryList(result, "items"),
                        ReadString(result, "seed", ""), ReadStringList(raw, "definingIds"));
                ValidateEditedNarrative(raw, result);
                documents["narrative"] = result;
            }
            return undo;
        }

        private static HashSet<string> NarrativeChangedItemIds(Dictionary<string, object> previous, Dictionary<string, object> next)
        {
            var before = ReadDictionaryList(previous, "items").ToDictionary(item => ReadString(item, "id", ""), StringComparer.Ordinal);
            var after = ReadDictionaryList(next, "items").ToDictionary(item => ReadString(item, "id", ""), StringComparer.Ordinal);
            return before.Keys.Concat(after.Keys).Where(id => CanonicalJson(before.GetValueOrDefault(id))
                != CanonicalJson(after.GetValueOrDefault(id))).ToHashSet(StringComparer.Ordinal);
        }

        private static void ReconcileEditedNarrative(string campaign, string hero, HashSet<string> editedItems = null)
        {
            var basis = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign, hero, "narrative.json"));
            if (!NarrativeDocumentReady(basis)) return;
            var items = ReadDictionaryList(basis, "items");
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                if (editedItems == null || editedItems.Count > 0)
                {
                    foreach (var row in CharactersStore.SelectDynamicCharacteristics3(connection, TestDict("hero", hero)))
                    {
                        var change = ReadDictionary(TryParseJsonObject(ReadString(row, "payload_json", "{}")), "narrativeDevelopment");
                        string itemId = ReadString(change, "itemId", "");
                        if (editedItems != null && !editedItems.Contains(itemId)) continue;
                        var item = items.FirstOrDefault(x => ReadString(x, "id", "") == itemId);
                        if (item != null && ReadString(change, "baseItemFingerprint", "") == NarrativeItemFingerprint(item)) continue;
                        CharactersStore.UpdateDynamicCharacteristics4(connection, TestDict("id", ReadString(row, "characteristic_id", "")));
                    }
                }
                ProjectDynamicCharacteristicsFile(campaign, hero, connection);
            }
            var stack = new Dictionary<string, object> { ["narrative"] = basis,
                ["dynamicCharacteristics"] = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign, hero, "dynamic_characteristics.json")),
                ["background"] = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign, hero, "background.json")) };
            ApplyNarrativePromptProjection(stack);
            foreach (var part in new Dictionary<string, string> { ["background"] = "background", ["voice"] = "voice", ["motivations"] = "motivations",
                ["saveQuirks"] = "save_quirks", ["hiddenHistory"] = "hidden_history", ["secrets"] = "secrets" })
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, hero, part.Value + ".json"), stack[part.Key]);
        }
    }
}
