using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bannerlord.NativeCharacterImageGenerator;

public static class AppearanceLabContract
{
    public const string IntentSchemaVersion = "bannerlord_appearance_intent_v1";
    public const string ExportSchemaVersion = "bannerlord_native_character_v1";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record AppearanceIntentV1
{
    public string SchemaVersion { get; init; } = AppearanceLabContract.IntentSchemaVersion;
    public string Sex { get; init; } = "unspecified";
    public float? Age { get; init; }
    public string Culture { get; init; } = "unspecified";
    public string Complexion { get; init; } = "unspecified";
    public string HairColor { get; init; } = "unspecified";
    public string HairLength { get; init; } = "unspecified";
    public string HairTexture { get; init; } = "unspecified";
    public string FacialHair { get; init; } = "unspecified";
    public string FaceShape { get; init; } = "unspecified";
    public float? CheekboneProminence { get; init; }
    public string EyeColor { get; init; } = "unspecified";
    public float? EyeSize { get; init; }
    public float? EyeSpacing { get; init; }
    public string EyeShape { get; init; } = "unspecified";
    public string BrowShape { get; init; } = "unspecified";
    public float? NoseLength { get; init; }
    public float? NoseWidth { get; init; }
    public string NoseShape { get; init; } = "unspecified";
    public float? MouthWidth { get; init; }
    public float? LipFullness { get; init; }
    public string JawShape { get; init; } = "unspecified";
    public string ChinShape { get; init; } = "unspecified";
    public float? Weight { get; init; }
    public float? Build { get; init; }
    public float? Height { get; init; }
    public IReadOnlyList<string> DistinguishingMarks { get; init; } = [];
    public IReadOnlyDictionary<string, float> Confidence { get; init; } =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    public float OverallConfidence { get; init; }
    public string SourceSummary { get; init; } = string.Empty;
}

internal sealed record AppearanceAnalysisRequest(
    string Description,
    string ReferenceImagePath);

internal sealed record AppearanceAnalysisResult(
    AppearanceIntentV1 Intent,
    string Analyzer,
    bool UsedDescription,
    bool UsedReferenceImage,
    string Notice);

internal interface IAppearanceIntentAnalyzer
{
    string Name { get; }
    bool CanAnalyzeImages { get; }

    Task<AppearanceIntentV1> AnalyzeAsync(
        AppearanceAnalysisRequest request,
        CancellationToken cancellationToken);
}

internal sealed record AppearanceCandidate(
    int Index,
    int Seed,
    NativeCharacterDefinition Character,
    AppearanceIntentV1 Intent,
    string BasisCharacterId);

public sealed record ExportedEquipmentV1(string Slot, string ItemId);

public sealed record ExportedNativeAppearanceV1(
    bool IsFemale,
    string Culture,
    float Age,
    float Weight,
    float Build,
    string BodyKey,
    IReadOnlyList<ExportedEquipmentV1> CivilianEquipment);

public sealed record AppearanceCharacterExportV1
{
    public string SchemaVersion { get; init; } = AppearanceLabContract.ExportSchemaVersion;
    public string GeneratorVersion { get; init; } = AppearanceCandidateGenerator.Version;
    public DateTimeOffset ExportedUtc { get; init; } = DateTimeOffset.UtcNow;
    public int Seed { get; init; }
    public int CandidateIndex { get; init; }
    public string BasisCharacterId { get; init; } = string.Empty;
    public AppearanceIntentV1 Intent { get; init; } = new();
    public ExportedNativeAppearanceV1 NativeAppearance { get; init; } =
        new(false, "Culture.empire", 30f, 0.5f, 0.5f, string.Empty, []);
}

public static class AppearanceCharacterExport
{
    internal static AppearanceCharacterExportV1 FromCandidate(AppearanceCandidate candidate) => new()
    {
        Seed = candidate.Seed,
        CandidateIndex = candidate.Index,
        BasisCharacterId = candidate.BasisCharacterId,
        Intent = candidate.Intent,
        NativeAppearance = new ExportedNativeAppearanceV1(
            candidate.Character.IsFemale,
            candidate.Character.Culture,
            candidate.Character.Age,
            candidate.Character.Weight,
            candidate.Character.Build,
            candidate.Character.BodyKey,
            candidate.Character.CivilianEquipment
                .Select(item => new ExportedEquipmentV1(item.Slot, item.ItemId))
                .ToArray())
    };

    public static string Serialize(AppearanceCharacterExportV1 value) =>
        JsonSerializer.Serialize(value, AppearanceLabContract.JsonOptions);

    public static AppearanceCharacterExportV1 Deserialize(string json)
    {
        var result = JsonSerializer.Deserialize<AppearanceCharacterExportV1>(
                json,
                AppearanceLabContract.JsonOptions)
            ?? throw new InvalidDataException("The appearance export is empty.");
        if (!result.SchemaVersion.Equals(AppearanceLabContract.ExportSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported appearance export schema '{result.SchemaVersion}'.");
        }

        if (!AppearanceCandidateGenerator.IsValidBodyKey(result.NativeAppearance.BodyKey))
        {
            throw new InvalidDataException("The appearance export contains an invalid native body key.");
        }

        return result;
    }
}
