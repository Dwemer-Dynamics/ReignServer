using System.Numerics;
using TpacTool.Lib;

namespace Bannerlord.NativeCharacterImageGenerator;

internal sealed class NativeAssetRepository
{
    private readonly Dictionary<string, (AssetPackage Package, Metamesh Asset)> _metameshes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, Material> _materials = [];
    private readonly Dictionary<Guid, Texture> _textures = [];

    private NativeAssetRepository()
    {
    }

    public int PackageCount { get; private set; }
    public int MetameshCount => _metameshes.Count;

    public static NativeAssetRepository Load(string gameRoot, IProgress<string>? progress = null)
    {
        var result = new NativeAssetRepository();
        var packageRoot = Path.Combine(gameRoot, "Modules", "Native", "AssetPackages");
        var preferred = new[]
        {
            "_shared.tpac",
            "core.tpac",
            "core_game.tpac",
            "materials.tpac",
            "bodies_shared.tpac",
            "shared_armor.tpac",
            "neutral_culture_armor.tpac",
            "looters_armor.tpac"
        };
        var paths = preferred
            .Select(name => Path.Combine(packageRoot, name))
            .Concat(Directory.Exists(packageRoot)
                ? Directory.EnumerateFiles(packageRoot, "*_armor.tpac", SearchOption.TopDirectoryOnly)
                : [])
            .Concat(Directory.Exists(packageRoot)
                ? Directory.EnumerateFiles(packageRoot, "pack_*.tpac", SearchOption.TopDirectoryOnly)
                : [])
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var index = 0; index < paths.Length; index++)
        {
            var path = paths[index];
            progress?.Report($"Indexing native assets {index + 1}/{paths.Length}: {Path.GetFileName(path)}");
            AssetPackage package;
            try
            {
                package = new AssetPackage(path);
            }
            catch
            {
                continue;
            }

            result.PackageCount++;
            foreach (var asset in package.Items.OfType<Metamesh>())
            {
                result._metameshes.TryAdd(asset.Name, (package, asset));
            }

            foreach (var material in package.Items.OfType<Material>())
            {
                result._materials.TryAdd(material.Guid, material);
            }

            foreach (var texture in package.Items.OfType<Texture>())
            {
                result._textures.TryAdd(texture.Guid, texture);
            }
        }

        return result;
    }

    public bool Contains(string assetName) => _metameshes.ContainsKey(assetName);

    public IReadOnlyList<RenderMesh> LoadAsset(
        string assetName,
        IReadOnlyDictionary<int, float> morphWeights,
        Vector4 skinColor)
    {
        if (!_metameshes.TryGetValue(assetName, out var entry))
        {
            return [];
        }

        return NativeAssetRenderer.LoadRenderMeshes(
            entry.Package,
            [assetName],
            morphWeights,
            skinColor,
            _materials,
            _textures);
    }
}
