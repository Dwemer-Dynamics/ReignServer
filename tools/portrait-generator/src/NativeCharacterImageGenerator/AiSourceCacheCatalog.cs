using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Bannerlord.NativeCharacterImageGenerator;

internal static class AiSourceCacheContract
{
    public const int InputVersion = 1;
    public const int ManifestVersion = 2;
    public const string RenderContractVersion = "ai_source_wan_portrait_v5";
    public const string ResidentOutfitRenderContractVersion = "ai_source_resident_full_outfit_v1";
    public const string PreviousWanRenderContractVersion = "ai_source_wan_portrait_v4";
    public const string OlderWanRenderContractVersion = "ai_source_wan_portrait_v3";
    public const string OldestWanRenderContractVersion = "ai_source_wan_portrait_v2";
    public const string LegacyRenderContractVersion = "ai_source_close_v1";
    public const string CourtAppearanceVersion = "reign_court_shared_v1";
    public const int RenderWidth = 1536;
    public const int RenderHeight = 2048;
    public const int OutputWidth = 768;
    public const int OutputHeight = 1024;
    public const int MinimumAiPortraitAge = 18;
    public const double CropScale = 0.24d;
    public const double CropWidthRatio = 0.75d;
    public const double CropCenterYRatio = 0.324d;
    public const double CameraPitchDegrees = 4d;
    public const int MaximumSourceBytes = 10 * 1024 * 1024;

    public static readonly AiSourceCacheRenderProfile WanPortrait = new(
        RenderContractVersion,
        "WAN 768×1024 (recommended)",
        RenderWidth,
        RenderHeight,
        OutputWidth,
        OutputHeight,
        CropScale,
        CropWidthRatio,
        CropCenterYRatio,
        CameraPitchDegrees,
        CameraFramed: true);

    public static readonly AiSourceCacheRenderProfile LegacyQuality = new(
        LegacyRenderContractVersion,
        "Legacy 3072×3840 quality",
        3072,
        3840,
        1129,
        1075,
        0.28d,
        1.05d,
        0.35d,
        0d,
        CameraFramed: false);

    public static readonly AiSourceCacheRenderProfile ResidentFullOutfit = new(
        ResidentOutfitRenderContractVersion, "Resident complete native outfit",
        RenderWidth, RenderHeight, OutputWidth, OutputHeight,
        1d, CropWidthRatio, .5d, 0d, CameraFramed: true);

    public static AiSourceCacheRenderProfile DefaultProfile => WanPortrait;
    public static string RenderContractHash => WanPortrait.RenderContractHash;

    public static AiSourceCacheRenderProfile GetProfile(string? contractVersion)
    {
        if (contractVersion == ResidentOutfitRenderContractVersion) return ResidentFullOutfit;
        if (string.IsNullOrWhiteSpace(contractVersion)
            || contractVersion.Equals(RenderContractVersion, StringComparison.OrdinalIgnoreCase)
            || contractVersion.Equals(PreviousWanRenderContractVersion, StringComparison.OrdinalIgnoreCase)
            || contractVersion.Equals(OlderWanRenderContractVersion, StringComparison.OrdinalIgnoreCase)
            || contractVersion.Equals(OldestWanRenderContractVersion, StringComparison.OrdinalIgnoreCase))
        {
            return WanPortrait;
        }
        if (contractVersion.Equals(LegacyRenderContractVersion, StringComparison.OrdinalIgnoreCase))
        {
            return LegacyQuality;
        }
        throw new InvalidDataException("Unknown AI source render contract: " + contractVersion);
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

internal sealed record AiSourceCacheRenderProfile(
    string RenderContractVersion,
    string DisplayName,
    int RenderWidth,
    int RenderHeight,
    int OutputWidth,
    int OutputHeight,
    double CropScale,
    double CropWidthRatio,
    double CropCenterYRatio,
    double CameraPitchDegrees,
    bool CameraFramed)
{
    public string RenderContractHash { get; } = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("|",
            RenderContractVersion,
            RenderWidth,
            RenderHeight,
            OutputWidth,
            OutputHeight,
            CropScale.ToString("R", CultureInfo.InvariantCulture),
            CropWidthRatio.ToString("R", CultureInfo.InvariantCulture),
            CropCenterYRatio.ToString("R", CultureInfo.InvariantCulture),
            CameraPitchDegrees.ToString("R", CultureInfo.InvariantCulture),
            CameraFramed
                ? "tableau_camera_v5_pitch_down4_frozen_character_developer_idle_p25_face_morph_restart_pre_tick_local_camera_gaze_front_key0p55_fill0p20_rim0p08_reset_persistent_scene_lights_safe_scene_preload_tiled_material_rejection_double_clean_capture_bicubic_2x"
                : "publication_crop",
            "opaque_rgb_black")))).ToLowerInvariant();
}

internal sealed record CanonicalCharacterRecord
{
    public required NativeCharacterDefinition Character { get; init; }
    public required string CacheKey { get; init; }
    public required string HeroStringId { get; init; }
    public required string CharacterObjectId { get; init; }
    public required string CharacterName { get; init; }
    public required int AgeYears { get; init; }
    public required string Gender { get; init; }
    public required string CultureId { get; init; }
    public required string CultureName { get; init; }
    public required string ClanId { get; init; }
    public int? ClanTier { get; init; }
    public required string ClanProvenance { get; init; }
    public required string SocialStation { get; init; }
    public required string Occupation { get; init; }
    public required string Archetype { get; init; }
    public bool IsLord { get; init; }
    public bool IsNotable { get; init; }
    public bool IsWanderer { get; init; }
    public bool? IsAlive { get; init; }
    public required PortraitPhysicalConfidenceMetadata PhysicalConfidence { get; init; }
    public required string ProfileSource { get; init; }
    public required string ProfilePackVersion { get; init; }
    public required string CatalogInputHash { get; init; }
    public string CatalogHash { get; init; } = string.Empty;
}

internal sealed record PortraitPhysicalConfidenceMetadata
{
    public required string Definition { get; init; }
    public required int Score { get; init; }
    public required string Profile { get; init; }
    public required int FlirtatiousnessPercentage { get; init; }
    public required int ConfidencePercentage { get; init; }
    public required double FlirtatiousnessWeight { get; init; }
    public required double ConfidenceWeight { get; init; }
    public required string Source { get; init; }
}

internal sealed record PortraitEquipmentMetadata(string Slot, string ItemId);

internal sealed record PortraitGenerationInputV1
{
    public int Version { get; init; } = AiSourceCacheContract.InputVersion;
    public required string InputHash { get; init; }
    public required string CatalogHash { get; init; }
    public required string RenderContractHash { get; init; }
    public required string CacheKey { get; init; }
    public required string HeroStringId { get; init; }
    public required string CharacterObjectId { get; init; }
    public required string CharacterName { get; init; }
    public required int AgeYears { get; init; }
    public required string CultureId { get; init; }
    public required string CultureName { get; init; }
    public required string Gender { get; init; }
    public required string ClanId { get; init; }
    public int? ClanTier { get; init; }
    public required string ClanProvenance { get; init; }
    public required string SocialStation { get; init; }
    public required string Occupation { get; init; }
    public required string Archetype { get; init; }
    public bool IsLord { get; init; }
    public bool IsNotable { get; init; }
    public bool IsWanderer { get; init; }
    public bool? IsAlive { get; init; }
    public required string SourceModule { get; init; }
    public required string SourceFile { get; init; }
    public required string ProfileSource { get; init; }
    public required string ProfilePackVersion { get; init; }
    public required string BodyKey { get; init; }
    public required float BodyWeight { get; init; }
    public required float BodyBuild { get; init; }
    public required string FaceTemplateId { get; init; }
    public required string FaceGenerationVersion { get; init; }
    public required string CivilianTemplate { get; init; }
    public required IReadOnlyList<PortraitEquipmentMetadata> CivilianEquipment { get; init; }
    public bool HeadgearSuppressed { get; init; } = true;
    public required PortraitPhysicalConfidenceMetadata PhysicalConfidence { get; init; }
    public required string RenderPreset { get; init; }
    public required int RenderWidth { get; init; }
    public required int RenderHeight { get; init; }
    public required int SourceWidth { get; init; }
    public required int SourceHeight { get; init; }
    public required double CropScale { get; init; }
    public required double CropWidthRatio { get; init; }
    public required double CropCenterYRatio { get; init; }
    public required double CameraPitchDegrees { get; init; }
    public required string SourceSha256 { get; init; }
    public required string SourceFileName { get; init; }
    public required string GenerationEndpoint { get; init; }
    public required string CampaignIdSource { get; init; }
    public bool PromptDeferred { get; init; } = true;
    public required string SourceImageBase64From { get; init; }
    public required string OutputSizeOverride { get; init; }
    public required string PromptPurpose { get; init; }
}

internal sealed class SourceCacheBuildManifestV2
{
    public int Version { get; set; } = AiSourceCacheContract.ManifestVersion;
    public string State { get; set; } = "in_progress";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string CatalogHash { get; set; } = string.Empty;
    public string RenderContractVersion { get; set; } = AiSourceCacheContract.RenderContractVersion;
    public string RenderContractHash { get; set; } = AiSourceCacheContract.RenderContractHash;
    public string CourtAppearanceVersion { get; set; } = AiSourceCacheContract.CourtAppearanceVersion;
    public string ProfilePackVersion { get; set; } = string.Empty;
    public int Expected { get; set; }
    public int CatalogTotal { get; set; }
    public int ExcludedUnderage { get; set; }
    public int Rendered { get; set; }
    public int Validated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public Dictionary<string, int> ModuleCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ModuleVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> SourceHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SourceCacheManifestItem> Items { get; set; } = [];
}

internal sealed class SourceCacheManifestItem
{
    public string CacheKey { get; set; } = string.Empty;
    public string HeroStringId { get; set; } = string.Empty;
    public string CharacterObjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SourceModule { get; set; } = string.Empty;
    public string InputHash { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string MetadataSha256 { get; set; } = string.Empty;
    public string MetadataStatus { get; set; } = "pending";
    public string State { get; set; } = "pending";
    public string Error { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string MetadataPath { get; set; } = string.Empty;
}

internal sealed record CanonicalCharacterCatalogResult(
    IReadOnlyList<CanonicalCharacterRecord> Characters,
    string CatalogHash,
    string ProfilePackVersion,
    IReadOnlyDictionary<string, string> SourceHashes,
    IReadOnlyDictionary<string, string> ModuleVersions);

internal static class CanonicalCharacterCatalog
{
    private static readonly string[] ProfileCatalogCandidates =
    [
        "Modules/ReignBeta/server/app/ProfileLibrary/profile_catalog.json",
        "Modules/ReignBeta/ProfileLibrary/profile_catalog.json"
    ];

    public static CanonicalCharacterCatalogResult Load(
        string gameRoot,
        IReadOnlyList<NativeCharacterDefinition> characters)
    {
        var profilePath = ProfileCatalogCandidates
            .Select(relative => Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "Reign's shipped character profile catalog is required for an AI-ready source cache.");

        using var profileDocument = JsonDocument.Parse(File.ReadAllText(profilePath));
        var profileRoot = profileDocument.RootElement;
        var packVersion = Text(profileRoot, "packVersion");
        var profiles = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (TryProperty(profileRoot, "profiles", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                var id = Text(row, "heroStringId");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    profiles[id] = row.Clone();
                }
            }
        }

        var heroFacts = HeroClanFacts.Load(gameRoot);
        var sourceHashes = BuildSourceHashes(gameRoot, profilePath);
        var records = new List<CanonicalCharacterRecord>(characters.Count);
        foreach (var character in characters)
        {
            profiles.TryGetValue(character.Id, out var profile);
            var sourceFacts = profile.ValueKind == JsonValueKind.Object
                && TryProperty(profile, "sourceFacts", out var facts)
                    ? facts
                    : default;
            var template = profile.ValueKind == JsonValueKind.Object
                && TryProperty(profile, "template", out var profileTemplate)
                    ? profileTemplate
                    : default;
            heroFacts.TryGetValue(character.Id, out var heroFact);

            var characterObjectId = FirstNonEmpty(
                Text(sourceFacts, "characterObjectId"),
                character.CharacterObjectId,
                character.Id);
            var occupation = FirstNonEmpty(Text(sourceFacts, "occupation"), character.Occupation, "NotAssigned");
            var profileClanId = Text(sourceFacts, "clanId");
            var courtCharacter = character.Id.StartsWith("reign_court_", StringComparison.OrdinalIgnoreCase);
            var clanId = courtCharacter
                ? FirstNonEmpty(heroFact?.ClanId, profileClanId, "none")
                : FirstNonEmpty(profileClanId, heroFact?.ClanId, "none");
            var clanTier = courtCharacter
                ? heroFact?.ClanTier ?? NullableInt(sourceFacts, "clanTier")
                : NullableInt(sourceFacts, "clanTier") ?? heroFact?.ClanTier;
            var clanProvenance = !string.IsNullOrWhiteSpace(profileClanId)
                ? "reign_profile_library"
                : heroFact is not null
                    ? "module_heroes_xml_and_clans_xml"
                    : "not_defined_in_module_heroes_xml";
            var profileSource = FirstNonEmpty(Text(profile, "source"), profiles.ContainsKey(character.Id)
                ? "shipped_profile_library"
                : "neutral_fallback");
            var archetype = FirstNonEmpty(Text(template, "archetype"), NormalizeArchetype(occupation));
            var flirtatiousness = TraitPercentage(profile, "flirtatiousness") ?? 50;
            var confidence = TraitPercentage(profile, "confidence") ?? 50;
            var score = Math.Clamp((int)Math.Round(
                flirtatiousness * 0.75d + confidence * 0.25d,
                MidpointRounding.AwayFromZero), 0, 100);
            var physicalConfidence = new PortraitPhysicalConfidenceMetadata
            {
                Definition = "physical_confidence",
                Score = score,
                Profile = ConfidenceProfile(score),
                FlirtatiousnessPercentage = flirtatiousness,
                ConfidencePercentage = confidence,
                FlirtatiousnessWeight = 0.75d,
                ConfidenceWeight = 0.25d,
                Source = profiles.ContainsKey(character.Id) ? "shipped_profile_library" : "neutral_fallback"
            };
            var cultureId = NormalizeCulture(character.Culture);
            var characterName = courtCharacter
                ? FirstNonEmpty(Text(sourceFacts, "name"), character.Name)
                : character.Name;
            var cacheKey = CharacterCacheKey.Build(characterName, characterObjectId);
            var inputHash = ComputeCatalogInputHash(
                character,
                cacheKey,
                characterObjectId,
                occupation,
                clanId,
                clanTier,
                clanProvenance,
                profileSource,
                packVersion,
                physicalConfidence);

            records.Add(new CanonicalCharacterRecord
            {
                Character = character,
                CacheKey = cacheKey,
                HeroStringId = character.Id,
                CharacterObjectId = characterObjectId,
                CharacterName = characterName,
                AgeYears = Math.Clamp((int)Math.Round(character.Age, MidpointRounding.AwayFromZero), 1, 130),
                Gender = character.IsFemale ? "woman" : "man",
                CultureId = cultureId,
                CultureName = CultureName(cultureId),
                ClanId = clanId,
                ClanTier = clanTier,
                ClanProvenance = clanProvenance,
                SocialStation = SocialStation(clanTier),
                Occupation = occupation,
                Archetype = archetype,
                IsLord = Bool(sourceFacts, "isLord") ?? occupation.Equals("Lord", StringComparison.OrdinalIgnoreCase),
                IsNotable = Bool(sourceFacts, "isNotable") ?? false,
                IsWanderer = Bool(sourceFacts, "isWanderer") ?? false,
                IsAlive = Bool(sourceFacts, "isAlive"),
                PhysicalConfidence = physicalConfidence,
                ProfileSource = profileSource,
                ProfilePackVersion = profiles.ContainsKey(character.Id) ? packVersion : string.Empty,
                CatalogInputHash = inputHash
            });
        }

        var duplicate = records.GroupBy(record => record.CacheKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate Reign portrait cache key: {duplicate.Key}");
        }
        if (records.Count != 1260)
        {
            throw new InvalidDataException($"AI source catalog expected 1,260 characters but found {records.Count:N0}.");
        }

        var matchedProfiles = records.Count(record => record.ProfileSource != "neutral_fallback");
        if (matchedProfiles != 1117)
        {
            throw new InvalidDataException(
                $"AI source catalog expected 1,117 Reign profile matches but found {matchedProfiles:N0}.");
        }

        var catalogHash = Sha256(string.Join("\n",
            sourceHashes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => "source|" + pair.Key + "|" + pair.Value)
                .Concat(records
                    .OrderBy(record => record.CacheKey, StringComparer.OrdinalIgnoreCase)
                    .Select(record => "character|" + record.CacheKey + "|" + record.CatalogInputHash))));
        for (var index = 0; index < records.Count; index++)
        {
            records[index] = records[index] with { CatalogHash = catalogHash };
        }
        var moduleVersions = BuildModuleVersions(gameRoot);
        return new CanonicalCharacterCatalogResult(
            records,
            catalogHash,
            packVersion,
            sourceHashes,
            moduleVersions);
    }

    public static PortraitGenerationInputV1 CreateInput(
        CanonicalCharacterRecord record,
        string sourceSha256 = "",
        AiSourceCacheRenderProfile? renderProfile = null)
    {
        renderProfile ??= AiSourceCacheContract.DefaultProfile;
        var character = record.Character;
        var visibleEquipment = character.CivilianEquipment
            .Where(item => item.Slot is "Body" or "Leg" or "Gloves" or "Cape")
            .OrderBy(item => item.Slot, StringComparer.OrdinalIgnoreCase)
            .Select(item => new PortraitEquipmentMetadata(item.Slot, item.ItemId))
            .ToArray();
        return new PortraitGenerationInputV1
        {
            InputHash = PortraitInputHash(record, renderProfile),
            CatalogHash = record.CatalogHash,
            RenderContractHash = renderProfile.RenderContractHash,
            CacheKey = record.CacheKey,
            HeroStringId = record.HeroStringId,
            CharacterObjectId = record.CharacterObjectId,
            CharacterName = record.CharacterName,
            AgeYears = record.AgeYears,
            CultureId = record.CultureId,
            CultureName = record.CultureName,
            Gender = record.Gender,
            ClanId = record.ClanId,
            ClanTier = record.ClanTier,
            ClanProvenance = record.ClanProvenance,
            SocialStation = record.SocialStation,
            Occupation = record.Occupation,
            Archetype = record.Archetype,
            IsLord = record.IsLord,
            IsNotable = record.IsNotable,
            IsWanderer = record.IsWanderer,
            IsAlive = record.IsAlive,
            SourceModule = character.SourceModule,
            SourceFile = character.SourceFile,
            ProfileSource = record.ProfileSource,
            ProfilePackVersion = record.ProfilePackVersion,
            BodyKey = character.BodyKey,
            BodyWeight = character.Weight,
            BodyBuild = character.Build,
            FaceTemplateId = character.FaceTemplateId,
            FaceGenerationVersion = character.Id.StartsWith("reign_court_", StringComparison.OrdinalIgnoreCase)
                ? AiSourceCacheContract.CourtAppearanceVersion
                : "native_authored",
            CivilianTemplate = character.CivilianTemplate,
            CivilianEquipment = visibleEquipment,
            PhysicalConfidence = record.PhysicalConfidence,
            RenderPreset = renderProfile.RenderContractVersion,
            RenderWidth = renderProfile.RenderWidth,
            RenderHeight = renderProfile.RenderHeight,
            SourceWidth = renderProfile.OutputWidth,
            SourceHeight = renderProfile.OutputHeight,
            CropScale = renderProfile.CropScale,
            CropWidthRatio = renderProfile.CropWidthRatio,
            CropCenterYRatio = renderProfile.CropCenterYRatio,
            CameraPitchDegrees = renderProfile.CameraPitchDegrees,
            SourceSha256 = sourceSha256,
            SourceFileName = "source.png",
            GenerationEndpoint = "/portraits/generate",
            CampaignIdSource = "active_campaign_at_generation_time",
            SourceImageBase64From = "source.png",
            OutputSizeOverride = string.Empty,
            PromptPurpose = "portrait"
        };
    }

    public static string PortraitInputHash(
        CanonicalCharacterRecord record,
        AiSourceCacheRenderProfile renderProfile) =>
        Sha256(record.CatalogInputHash + "|" + renderProfile.RenderContractHash);

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ComputeCatalogInputHash(
        NativeCharacterDefinition character,
        string cacheKey,
        string characterObjectId,
        string occupation,
        string clanId,
        int? clanTier,
        string clanProvenance,
        string profileSource,
        string packVersion,
        PortraitPhysicalConfidenceMetadata confidence)
    {
        var equipment = string.Join(",", character.CivilianEquipment
            .Where(item => item.Slot is "Body" or "Leg" or "Gloves" or "Cape")
            .OrderBy(item => item.Slot, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Slot + "=" + item.ItemId));
        return Sha256(string.Join("|",
            AiSourceCacheContract.RenderContractVersion,
            AiSourceCacheContract.CourtAppearanceVersion,
            cacheKey,
            character.Id,
            characterObjectId,
            character.Name,
            character.Age.ToString("R", CultureInfo.InvariantCulture),
            character.Culture,
            character.IsFemale,
            occupation,
            clanId,
            clanTier?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            clanProvenance,
            profileSource,
            packVersion,
            confidence.Score,
            character.BodyKey,
            character.FaceTemplateId,
            character.Weight.ToString("R", CultureInfo.InvariantCulture),
            character.Build.ToString("R", CultureInfo.InvariantCulture),
            character.CivilianTemplate,
            equipment));
    }

    private static Dictionary<string, string> BuildSourceHashes(string gameRoot, string profilePath)
    {
        string[] relativePaths =
        [
            "Modules/SandBox/ModuleData/lords.xml",
            "Modules/StoryMode/ModuleData/story_mode_characters.xml",
            "Modules/SandBox/ModuleData/spspecialcharacters.xml",
            "Modules/SandBoxCore/ModuleData/spnpccharacters.xml",
            "Modules/NavalDLC/ModuleData/naval_lords.xml",
            "Modules/NavalDLC/ModuleData/naval_characters.xml",
            "Modules/NavalDLC/ModuleData/heroes.xml",
            "Modules/NavalDLC/ModuleData/clans.xml",
            "Modules/ReignBeta/ModuleData/reign_court_lords.xml",
            "Modules/ReignBeta/ModuleData/reign_court_heroes.xml"
        ];
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in relativePaths)
        {
            var path = Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                result[relative] = Sha256File(path);
            }
        }
        result["ReignProfileLibrary"] = Sha256File(profilePath);
        return result;
    }

    private static IReadOnlyDictionary<string, string> BuildModuleVersions(string gameRoot)
    {
        string[] moduleIds = ["Native", "SandBoxCore", "SandBox", "StoryMode", "NavalDLC", "ReignBeta"];
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var moduleId in moduleIds)
        {
            var subModulePath = Path.Combine(gameRoot, "Modules", moduleId, "SubModule.xml");
            if (!File.Exists(subModulePath))
            {
                continue;
            }
            try
            {
                var document = XDocument.Load(subModulePath, LoadOptions.None);
                var version = document.Descendants()
                    .FirstOrDefault(element => element.Name.LocalName.Equals(
                        "Version",
                        StringComparison.OrdinalIgnoreCase));
                result[moduleId] = version is null
                    ? "unknown"
                    : FirstNonEmpty(XmlAttribute(version, "value"), version.Value.Trim(), "unknown");
            }
            catch
            {
                result[moduleId] = "unreadable";
            }
        }
        return result;
    }

    private static string XmlAttribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(
            name,
            StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

    private static int? TraitPercentage(JsonElement profile, string key)
    {
        if (profile.ValueKind != JsonValueKind.Object
            || !TryProperty(profile, "traits", out var traits)
            || !TryProperty(traits, "traitPercentages", out var percentages)
            || !TryProperty(percentages, key, out var value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? Math.Clamp(number, 0, 100)
            : null;
    }

    private static string ConfidenceProfile(int score) => score switch
    {
        <= 20 => "00-20",
        <= 40 => "21-40",
        <= 60 => "41-60",
        <= 80 => "61-80",
        _ => "81-100"
    };

    private static string NormalizeCulture(string value) => value
        .Replace("Culture.", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Trim()
        .ToLowerInvariant();

    private static string CultureName(string cultureId) => cultureId switch
    {
        "vlandia" => "Vlandian",
        "battania" => "Battanian",
        "sturgia" => "Sturgian",
        "empire" => "Imperial",
        "aserai" => "Aserai",
        "khuzait" => "Khuzait",
        "nord" => "Nord",
        _ => string.IsNullOrWhiteSpace(cultureId)
            ? "Unknown"
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cultureId.Replace('_', ' '))
    };

    private static string SocialStation(int? clanTier) => clanTier switch
    {
        >= 1 and <= 2 => "landowner",
        >= 3 and <= 4 => "lesser lord",
        >= 5 => "high noble",
        _ => "unranked"
    };

    private static string NormalizeArchetype(string occupation) =>
        string.IsNullOrWhiteSpace(occupation)
            ? "unassigned"
            : occupation.Trim().Replace(' ', '_').ToLowerInvariant();

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !TryProperty(element, name, out var value))
        {
            return string.Empty;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static bool? Bool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !TryProperty(element, name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int? NullableInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !TryProperty(element, name, out var value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private sealed record HeroClanFact(string ClanId, int? ClanTier);

    private static class HeroClanFacts
    {
        public static IReadOnlyDictionary<string, HeroClanFact> Load(string gameRoot)
        {
            var moduleRoots = new[] { "SandBox", "StoryMode", "NavalDLC", "ReignBeta" }
                .Select(module => Path.Combine(gameRoot, "Modules", module, "ModuleData"))
                .Where(Directory.Exists)
                .ToArray();
            var clanTiers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in moduleRoots)
            {
                foreach (var path in Directory.EnumerateFiles(root, "*clan*.xml", SearchOption.TopDirectoryOnly))
                {
                    TryReadXml(path, document =>
                    {
                        foreach (var faction in document.Descendants()
                            .Where(element => element.Name.LocalName.Equals("Faction", StringComparison.OrdinalIgnoreCase)))
                        {
                            var id = Attribute(faction, "id");
                            if (!string.IsNullOrWhiteSpace(id)
                                && int.TryParse(Attribute(faction, "tier"), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out var tier))
                            {
                                clanTiers[id] = tier;
                            }
                        }
                    });
                }
            }

            var result = new Dictionary<string, HeroClanFact>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in moduleRoots)
            {
                foreach (var path in Directory.EnumerateFiles(root, "*heroes*.xml", SearchOption.TopDirectoryOnly))
                {
                    TryReadXml(path, document =>
                    {
                        foreach (var hero in document.Descendants()
                            .Where(element => element.Name.LocalName.Equals("Hero", StringComparison.OrdinalIgnoreCase)))
                        {
                            var id = Attribute(hero, "id");
                            var clanId = Attribute(hero, "faction")
                                .Replace("Faction.", string.Empty, StringComparison.OrdinalIgnoreCase);
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                result[id] = new HeroClanFact(
                                    clanId,
                                    clanTiers.TryGetValue(clanId, out var tier) ? tier : null);
                            }
                        }
                    });
                }
            }
            return result;
        }

        private static void TryReadXml(string path, Action<XDocument> read)
        {
            try
            {
                read(XDocument.Load(path, LoadOptions.None));
            }
            catch
            {
                // One optional module metadata file must not hide otherwise valid canonical facts.
            }
        }

        private static string Attribute(XElement element, string name) =>
            element.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
    }
}

internal static class CharacterCacheKey
{
    public static string Build(string name, string characterObjectId)
    {
        if (string.IsNullOrWhiteSpace(characterObjectId))
        {
            throw new ArgumentException("A character-object ID is required for a Reign cache key.");
        }
        return Sanitize(name ?? "Unknown") + " (" + Sanitize(characterObjectId) + ")";
    }

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "Unknown";
        }
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid.ToString(), string.Empty, StringComparison.Ordinal);
        }
        return value.Replace(' ', '_');
    }
}
