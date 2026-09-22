using System.Collections.Concurrent;
using System.Numerics;

namespace Bannerlord.NativeCharacterImageGenerator;

internal sealed record CharacterRenderOutput(
    RenderResult Image,
    IReadOnlyList<string> ResolvedItems,
    IReadOnlyList<string> MissingAssets);

internal sealed class CharacterRenderService
{
    private static readonly HashSet<string> ClothingSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Head", "Body", "Cape", "Gloves", "Leg"
    };

    private readonly CharacterCatalog _catalog;
    private readonly NativeAssetRepository _assets;
    private readonly ConcurrentDictionary<string, Lazy<CharacterAssembly>> _assemblies =
        new(StringComparer.OrdinalIgnoreCase);

    public CharacterRenderService(CharacterCatalog catalog, NativeAssetRepository assets)
    {
        _catalog = catalog;
        _assets = assets;
    }

    public CharacterRenderOutput Render(
        NativeCharacterDefinition character,
        int width,
        int height,
        float yawDegrees = 0f,
        float zoom = 2.9f)
    {
        var assembly = _assemblies.GetOrAdd(
            character.Id,
            _ => new Lazy<CharacterAssembly>(
                () => BuildAssembly(character),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        var image = SoftwareRasterizer.Render(
            assembly.Meshes,
            width,
            height,
            yawDegrees,
            0f,
            false,
            zoom,
            0.805f);
        return new CharacterRenderOutput(image, assembly.ResolvedItems, assembly.MissingAssets);
    }

    private CharacterAssembly BuildAssembly(NativeCharacterDefinition character)
    {
        var appearance = _catalog.Appearance.Resolve(character);
        var skinColor = appearance.SkinColor;
        var bodyMorphs = new Dictionary<int, float>
        {
            [60] = Math.Clamp(character.Weight, 0f, 1f),
            [61] = Math.Clamp(character.Build, 0f, 1f)
        };
        var faceMorphs = appearance.FaceMorphs.ToDictionary(pair => pair.Key, pair => pair.Value);
        faceMorphs[60] = bodyMorphs[60];
        faceMorphs[61] = bodyMorphs[61];
        var equipmentBySlot = character.CivilianEquipment
            .Where(item => ClothingSlots.Contains(item.Slot))
            .GroupBy(item => item.Slot, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var requested = new List<(string Asset, bool IsFace, bool IsClothing, bool IsHair, string Label)>();
        var gender = character.IsFemale ? "female" : "male";
        requested.Add(($"head_{gender}_a", true, false, false, "native face"));
        if (!string.IsNullOrWhiteSpace(appearance.HairAsset))
        {
            requested.Add((appearance.HairAsset, true, false, true, "native hair"));
        }

        if (!string.IsNullOrWhiteSpace(appearance.BeardAsset))
        {
            requested.Add((appearance.BeardAsset, true, false, true, "native beard"));
        }

        if (!string.IsNullOrWhiteSpace(appearance.EyebrowAsset))
        {
            requested.Add((appearance.EyebrowAsset, true, false, true, "native eyebrows"));
        }

        if (!equipmentBySlot.ContainsKey("Body"))
        {
            requested.Add(($"body_{gender}_a", false, false, false, "native body"));
        }

        if (!equipmentBySlot.ContainsKey("Gloves"))
        {
            requested.Add(($"hands_{gender}_a", false, false, false, "native hands"));
        }

        if (!equipmentBySlot.ContainsKey("Leg"))
        {
            requested.Add(($"feet_{gender}_a", false, false, false, "native feet"));
        }

        var resolvedItems = new List<string>();
        var missing = new List<string>();
        foreach (var assignment in equipmentBySlot.Values)
        {
            if (!_catalog.Items.TryGetValue(assignment.ItemId, out var item))
            {
                missing.Add($"{assignment.Slot}: Item.{assignment.ItemId}");
                continue;
            }

            requested.Add((item.MeshName, false, true, false, $"{assignment.Slot}: {item.Name}"));
            resolvedItems.Add($"{assignment.Slot}: {item.Name}");
        }

        var meshes = new List<RenderMesh>();
        foreach (var request in requested)
        {
            var loaded = _assets.LoadAsset(
                request.Asset,
                request.IsFace ? faceMorphs : bodyMorphs,
                skinColor);
            if (loaded.Count == 0)
            {
                missing.Add($"{request.Label} ({request.Asset})");
                continue;
            }

            meshes.AddRange(request.IsHair
                ? loaded.Select(mesh => mesh with { FallbackColor = mesh.FallbackColor * appearance.HairColor })
                : loaded);
        }

        if (meshes.Count == 0)
        {
            throw new InvalidDataException($"No native geometry could be resolved for {character.Name}.");
        }

        var posed = RelaxedStandingPose.Apply(meshes);
        return new CharacterAssembly(posed, resolvedItems.ToArray(), missing.ToArray());
    }

    private sealed record CharacterAssembly(
        IReadOnlyList<RenderMesh> Meshes,
        IReadOnlyList<string> ResolvedItems,
        IReadOnlyList<string> MissingAssets);
}
