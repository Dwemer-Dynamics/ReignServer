namespace Bannerlord.NativeCharacterImageGenerator;

internal sealed record NativeEquipmentAssignment(string Slot, string ItemId, string ModifierId = "");

internal sealed record NativeCharacterDefinition(
    string Id,
    string Name,
    string Culture,
    bool IsFemale,
    float Age,
    float Weight,
    float Build,
    string BodyKey,
    string CivilianTemplate,
    IReadOnlyList<NativeEquipmentAssignment> CivilianEquipment,
    string SourceFile,
    string NativePortraitPath = "",
    string ReignCampaignId = "",
    string CharacterObjectId = "",
    bool IsCampaignCharacter = false,
    string CharacterCode = "",
    string FaceTemplateId = "",
    string SourceModule = "Native",
    string Occupation = "",
    bool IsHero = false)
{
    public string WeightSource { get; init; } = "resolved_character_record";
    public string BuildSource { get; init; } = "resolved_character_record";
    public bool PreserveEncounteredOutfit { get; init; }
    public uint? ClothingColor1 { get; init; }
    public uint? ClothingColor2 { get; init; }
    public string DisplayCulture => Culture.Replace("Culture.", string.Empty, StringComparison.OrdinalIgnoreCase);

    public bool HasNativePortrait => !string.IsNullOrWhiteSpace(NativePortraitPath)
        && File.Exists(NativePortraitPath);

    public string RosterSource => IsCampaignCharacter
        ? $"Reign campaign {ReignCampaignId}"
        : SourceModule.Equals("NavalDLC", StringComparison.OrdinalIgnoreCase)
            ? "War Sails"
            : SourceModule.Equals("ReignBeta", StringComparison.OrdinalIgnoreCase)
                ? "Reign"
                : "Native";

    public string EquipmentSummary => CivilianEquipment.Count == 0
        ? "Native civilian template"
        : string.Join(", ", CivilianEquipment.Select(item => item.Slot));
}

internal sealed record NativeItemDefinition(
    string Id,
    string Name,
    string MeshName,
    string Culture,
    string Type);
