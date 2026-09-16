using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bannerlord.NativeCharacterImageGenerator;

internal sealed record ReignCatalogResult(
    IReadOnlyList<NativeCharacterDefinition> Characters,
    int NativePortraitCount,
    int CampaignCharacterCount,
    string CampaignId,
    string ReignModuleRoot);

internal static partial class ReignCharacterCatalog
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static ReignCatalogResult Merge(
        string gameRoot,
        IReadOnlyList<NativeCharacterDefinition> baseCharacters,
        string requestedCampaignId = "",
        string reignDataRoot = "")
    {
        var moduleRoot = Path.Combine(gameRoot, "Modules", "ReignBeta");
        if (!Directory.Exists(moduleRoot))
        {
            return new ReignCatalogResult(baseCharacters, 0, 0, string.Empty, string.Empty);
        }

        var merged = baseCharacters.ToDictionary(character => character.Id, StringComparer.OrdinalIgnoreCase);
        var sharedPortraits = ReadPortraitFolders(Path.Combine(moduleRoot, "PortraitCache", "_shared"));
        foreach (var (id, portrait) in sharedPortraits)
        {
            if (merged.TryGetValue(id, out var character))
            {
                merged[id] = character with { NativePortraitPath = portrait.SourcePath };
            }
        }

        var campaign = FindCampaign(moduleRoot, requestedCampaignId, reignDataRoot);
        var campaignCharacters = 0;
        if (campaign is not null)
        {
            var campaignPortraits = ReadPortraitFolders(
                Path.Combine(moduleRoot, "PortraitCache", campaign.Id));
            var rosterRows = ReadPortraitRoster(campaign.Path);
            bool legacyPercentUnits = rosterRows.Count == 0;
            if (legacyPercentUnits)
            {
                rosterRows = ReadLegacyProfiles(campaign.Path);
            }

            foreach (var row in rosterRows)
            {
                var character = MergeCampaignCharacter(
                    row,
                    campaign.Id,
                    merged,
                    campaignPortraits,
                    sharedPortraits,
                    legacyPercentUnits);
                if (character is null)
                {
                    continue;
                }

                merged[character.Id] = character;
                campaignCharacters++;
            }
        }

        var ordered = merged.Values
            .OrderByDescending(character => character.IsCampaignCharacter)
            .ThenBy(character => character.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(character => character.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ReignCatalogResult(
            ordered,
            ordered.Count(character => character.HasNativePortrait),
            campaignCharacters,
            campaign?.Id ?? string.Empty,
            moduleRoot);
    }

    private static NativeCharacterDefinition? MergeCampaignCharacter(
        JsonElement row,
        string campaignId,
        IReadOnlyDictionary<string, NativeCharacterDefinition> known,
        IReadOnlyDictionary<string, PortraitFolder> campaignPortraits,
        IReadOnlyDictionary<string, PortraitFolder> sharedPortraits,
        bool legacyPercentUnits = false)
    {
        var heroId = Text(row, "heroStringId", "heroId", "id");
        if (string.IsNullOrWhiteSpace(heroId))
        {
            return null;
        }

        var characterObjectId = Text(row, "characterObjectId");
        NativeCharacterDefinition? native = null;
        if (!string.IsNullOrWhiteSpace(characterObjectId))
        {
            known.TryGetValue(characterObjectId, out native);
        }
        if (native is null)
        {
            known.TryGetValue(heroId, out native);
        }

        var name = Text(row, "name");
        var culture = Text(row, "cultureId", "culture");
        var portraitCacheKey = Text(row, "portraitCacheKey", "cacheKey");
        var portrait = FindPortrait(
            heroId,
            characterObjectId,
            portraitCacheKey,
            campaignPortraits,
            sharedPortraits,
            native?.NativePortraitPath ?? string.Empty);
        var equipment = ReadCivilianEquipment(row);
        var bodyKey = Text(row, "bodyKey");

        return new NativeCharacterDefinition(
            heroId,
            string.IsNullOrWhiteSpace(name) ? native?.Name ?? heroId : name,
            string.IsNullOrWhiteSpace(culture) ? native?.Culture ?? string.Empty : culture,
            Bool(row, "isFemale", native?.IsFemale ?? false),
            Number(row, "age", native?.Age ?? 30f),
            RosterUnit(row, "bodyWeight", "weight", native?.Weight ?? .5f, legacyPercentUnits),
            RosterUnit(row, "bodyBuild", "build", native?.Build ?? .5f, legacyPercentUnits),
            string.IsNullOrWhiteSpace(bodyKey) ? native?.BodyKey ?? string.Empty : bodyKey,
            native?.CivilianTemplate ?? string.Empty,
            equipment.Count == 0 ? native?.CivilianEquipment ?? [] : equipment,
            "Reign portrait roster",
            portrait,
            campaignId,
            string.IsNullOrWhiteSpace(characterObjectId) ? native?.Id ?? heroId : characterObjectId,
            true,
            Text(row, "characterCode"),
            native?.FaceTemplateId ?? string.Empty,
            native?.SourceModule ?? "ReignBeta")
        {
            WeightSource = TryProperty(row, "bodyWeight", out _) || TryProperty(row, "weight", out _) ? (legacyPercentUnits ? "legacy_profile_percent_converted" : "saved_roster") : native?.WeightSource ?? "generator_default_0.5",
            BuildSource = TryProperty(row, "bodyBuild", out _) || TryProperty(row, "build", out _) ? (legacyPercentUnits ? "legacy_profile_percent_converted" : "saved_roster") : native?.BuildSource ?? "generator_default_0.5"
        };
    }

    private static string FindPortrait(
        string heroId,
        string characterObjectId,
        string portraitCacheKey,
        IReadOnlyDictionary<string, PortraitFolder> campaignPortraits,
        IReadOnlyDictionary<string, PortraitFolder> sharedPortraits,
        string fallback)
    {
        foreach (var key in new[] { portraitCacheKey, heroId, characterObjectId }
            .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (campaignPortraits.TryGetValue(key, out var campaignPortrait))
            {
                return campaignPortrait.SourcePath;
            }
            if (sharedPortraits.TryGetValue(key, out var sharedPortrait))
            {
                return sharedPortrait.SourcePath;
            }
        }

        return fallback;
    }

    private static List<NativeEquipmentAssignment> ReadCivilianEquipment(JsonElement row)
    {
        JsonElement items;
        if (!TryProperty(row, "civilianEquipment", out items)
            && TryProperty(row, "appearance", out var appearance))
        {
            TryProperty(appearance, "civilianEquipment", out items);
        }

        if (items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return items.EnumerateArray()
            .Select(item => new NativeEquipmentAssignment(
                NormalizeSlot(Text(item, "slot")),
                Text(item, "itemId", "id").Replace("Item.", string.Empty, StringComparison.OrdinalIgnoreCase)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Slot)
                && !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(item => item.Slot, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static string NormalizeSlot(string value) => value switch
    {
        "ArmorItemBeginSlot" => "Head",
        "ArmorItemEndSlot" => "Cape",
        _ => value
    };

    private static List<JsonElement> ReadPortraitRoster(string campaignPath)
    {
        var path = Path.Combine(campaignPath, "portrait_roster.json");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
            if (!TryProperty(document.RootElement, "characters", out var rows)
                || rows.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return rows.EnumerateArray().Select(item => item.Clone()).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<JsonElement> ReadLegacyProfiles(string campaignPath)
    {
        var charactersPath = Path.Combine(campaignPath, "characters");
        if (!Directory.Exists(charactersPath))
        {
            return [];
        }

        var result = new List<JsonElement>();
        foreach (var path in Directory.EnumerateFiles(charactersPath, "profile.json", SearchOption.AllDirectories))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
                result.Add(document.RootElement.Clone());
            }
            catch
            {
                // A partially written profile should not prevent the studio from opening.
            }
        }

        return result;
    }

    private static CampaignFolder? FindCampaign(
        string moduleRoot,
        string requestedCampaignId,
        string reignDataRoot)
    {
        var root = string.IsNullOrWhiteSpace(reignDataRoot)
            ? Path.Combine(moduleRoot, "server", "app", "data", "campaigns")
            : Path.Combine(Path.GetFullPath(reignDataRoot), "campaigns");
        if (!Directory.Exists(root))
        {
            return null;
        }

        var campaigns = Directory.EnumerateDirectories(root)
            .Select(path => new CampaignFolder(Path.GetFileName(path), path, Directory.GetLastWriteTimeUtc(path)))
            .Where(folder => File.Exists(Path.Combine(folder.Path, "campaign.json")))
            .Where(folder => !folder.Id.StartsWith("__", StringComparison.Ordinal)
                && !folder.Id.Contains("test", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(requestedCampaignId))
        {
            return campaigns.FirstOrDefault(folder =>
                folder.Id.Equals(requestedCampaignId, StringComparison.OrdinalIgnoreCase));
        }
        return campaigns.OrderByDescending(folder => folder.UpdatedUtc).FirstOrDefault();
    }

    private static Dictionary<string, PortraitFolder> ReadPortraitFolders(string root)
    {
        var result = new Dictionary<string, PortraitFolder>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var sourcePath = Path.Combine(directory, "source.png");
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var name = Path.GetFileName(directory);
            var match = CacheFolderIdRegex().Match(name);
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups["id"].Value.Trim();
            var folder = new PortraitFolder(id, name, sourcePath);
            result[id] = folder;
            result[name] = folder;
        }

        return result;
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

    private static string Text(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryProperty(element, name, out var value))
            {
                return value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : value.ToString();
            }
        }

        return string.Empty;
    }

    private static bool Bool(JsonElement element, string name, bool fallback)
    {
        if (!TryProperty(element, name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static float Number(JsonElement element, string name, float fallback)
    {
        if (!TryProperty(element, name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number))
        {
            return number;
        }

        return float.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : fallback;
    }

    internal static float RosterUnit(JsonElement row, string primary, string secondary, float fallback, bool legacyPercentUnits = false)
    {
        if (!TryProperty(row, primary, out var value) && !TryProperty(row, secondary, out value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float number)
            || !float.IsFinite(number) || number < 0 || number > (legacyPercentUnits ? 100 : 1))
            throw new InvalidDataException(primary + " must use native 0–1 units.");
        return legacyPercentUnits ? number / 100f : number;
    }

    [GeneratedRegex(@"\((?<id>[^()]*)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex CacheFolderIdRegex();

    private sealed record PortraitFolder(string Id, string CacheKey, string SourcePath);
    private sealed record CampaignFolder(string Id, string Path, DateTime UpdatedUtc);
}
