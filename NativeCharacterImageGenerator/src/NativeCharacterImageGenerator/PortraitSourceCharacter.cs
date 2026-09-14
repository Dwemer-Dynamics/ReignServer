using System.Text.Json;

namespace Bannerlord.NativeCharacterImageGenerator;

internal static class PortraitSourceCharacter
{
    internal static AiSourceCacheRenderProfile RenderProfile(NativeCharacterDefinition character)
        => character.PreserveEncounteredOutfit ? AiSourceCacheContract.ResidentFullOutfit : AiSourceCacheContract.DefaultProfile;
    internal static float Unit(JsonElement row, string key, float fallback = .5f)
    {
        if (!row.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var number)
            || !float.IsFinite(number) || number < 0 || number > 1)
            throw new InvalidDataException(key + " must be a finite native value from 0 to 1, not a percentage.");
        return number;
    }
    public static NativeCharacterDefinition FromSnapshot(JsonElement row, string campaignId, string heroId)
    {
        string Text(string key) => row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        float Number(string key, float fallback) => row.TryGetProperty(key, out var v) && v.TryGetSingle(out var n) && float.IsFinite(n) ? n : fallback;
        if (Text("schema") != "reign-native-portrait-snapshot-v1" || Text("campaignId") != campaignId
            || Text("heroStringId") != heroId || string.IsNullOrWhiteSpace(heroId) || string.IsNullOrWhiteSpace(Text("bodyKey")))
            throw new InvalidDataException("The requested character snapshot has missing facial data or mismatched identity.");
        var equipment = new List<NativeEquipmentAssignment>();
        if (row.TryGetProperty("civilianEquipment", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("slot", out var slot) && item.TryGetProperty("itemId", out var id)
                    && slot.ValueKind == JsonValueKind.String && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString()))
                    equipment.Add(new NativeEquipmentAssignment(slot.GetString()!, id.GetString()!,
                        item.TryGetProperty("modifierId", out var modifier) && modifier.ValueKind == JsonValueKind.String ? modifier.GetString() ?? "" : ""));
        string requestedProfile = Text("portraitSourceProfile");
        if (row.TryGetProperty("portraitSourceProfile", out var profileValue)
            && profileValue.ValueKind != JsonValueKind.String && profileValue.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Portrait source profile must be a supported profile name.");
        if (!string.IsNullOrEmpty(requestedProfile) && requestedProfile != AiSourceCacheContract.RenderContractVersion
            && requestedProfile != AiSourceCacheContract.ResidentOutfitRenderContractVersion)
            throw new InvalidDataException("Unsupported portrait source profile: " + requestedProfile);
        bool preserveOutfit = requestedProfile == AiSourceCacheContract.ResidentOutfitRenderContractVersion
            || row.TryGetProperty("encounteredResident", out var resident)
            && resident.ValueKind == JsonValueKind.Object
            && resident.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.String
            && schema.GetString() == "reign-encountered-resident-v1";
        var character = new NativeCharacterDefinition(heroId, Text("name"), Text("cultureId"),
            row.TryGetProperty("isFemale", out var female) && female.ValueKind == JsonValueKind.True,
            Number("age", 30), Unit(row, "bodyWeight"), Unit(row, "bodyBuild"),
            Text("bodyKey"), "", equipment, "Reign live portrait snapshot", "", campaignId, Text("characterObjectId"), true)
        {
            WeightSource = row.TryGetProperty("bodyWeight", out _) ? "request_snapshot" : "generator_default_0.5",
            BuildSource = row.TryGetProperty("bodyBuild", out _) ? "request_snapshot" : "generator_default_0.5",
            PreserveEncounteredOutfit = preserveOutfit,
            ClothingColor1 = row.TryGetProperty("clothingColor1", out var color1) && color1.TryGetUInt32(out var c1) ? c1 : null,
            ClothingColor2 = row.TryGetProperty("clothingColor2", out var color2) && color2.TryGetUInt32(out var c2) ? c2 : null
        };
        return preserveOutfit ? character : WithRenderableClothing(character);
    }

    public static NativeCharacterDefinition WithRenderableClothing(NativeCharacterDefinition character)
    {
        if (string.IsNullOrWhiteSpace(character.BodyKey) && string.IsNullOrWhiteSpace(character.CharacterCode)
            && string.IsNullOrWhiteSpace(character.FaceTemplateId))
            throw new InvalidDataException("Native facial appearance is missing. Synchronize this character from the game before generating a portrait.");
        if (character.PreserveEncounteredOutfit) return character;
        if (character.CivilianEquipment.Any(item => item.Slot.Equals("Body", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.ItemId))) return character;
        // These stock SandBoxCore garments are only renderer defaults. AI clothing is prompt controlled.
        return character with { CivilianEquipment = character.CivilianEquipment
            .Where(item => !item.Slot.Equals("Body", StringComparison.OrdinalIgnoreCase))
            .Append(new NativeEquipmentAssignment("Body", character.IsFemale ? "dress_with_overall" : "tunic_with_rolled_cloth")).ToArray() };
    }
}
