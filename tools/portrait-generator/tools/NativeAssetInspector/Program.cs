using TpacTool.Lib;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: NativeAssetInspector PACKAGE_OR_DIRECTORY ASSET");
    return 1;
}

var source = Path.GetFullPath(args[0]);
var paths = Directory.Exists(source)
    ? new[] { "_shared.tpac", "core.tpac", "core_game.tpac", "materials.tpac", "bodies_shared.tpac", "shared_armor.tpac" }
        .Select(name => Path.Combine(source, name))
        .Concat(Directory.EnumerateFiles(source, "*_armor.tpac"))
        .Concat(Directory.EnumerateFiles(source, "pack_*.tpac"))
        .Where(File.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray()
    : [source];
var packages = paths.Select(path => new AssetPackage(path)).ToArray();
var materials = packages.SelectMany(package => package.Items.OfType<Material>())
    .GroupBy(item => item.Guid).ToDictionary(group => group.Key, group => group.First());
var textures = packages.SelectMany(package => package.Items.OfType<Texture>())
    .GroupBy(item => item.Guid).ToDictionary(group => group.Key, group => group.First());
var metamesh = packages.SelectMany(package => package.Items.OfType<Metamesh>()
        .Select(item => (Package: package, Asset: item)))
    .FirstOrDefault(entry => entry.Asset.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase));
if (metamesh.Asset is null)
{
    Console.Error.WriteLine($"asset not found: {args[1]}");
    return 2;
}

Console.WriteLine($"packages={packages.Length} materialIndex={materials.Count} textureIndex={textures.Count}");
Console.WriteLine($"package={metamesh.Package.File?.Name} asset={metamesh.Asset.Name} meshes={metamesh.Asset.Meshes.Count}");
foreach (var mesh in metamesh.Asset.Meshes.Where(item => item.Lod == 0))
{
    Console.WriteLine($"mesh={mesh.Name} material={mesh.Material.Guid} factor={mesh.FactorColor} factor2={mesh.Factor2Color} flags=[{string.Join(',', mesh.MaterialFlags)}]");
    if (!materials.TryGetValue(mesh.Material.Guid, out var material))
    {
        Console.WriteLine("  MATERIAL NOT FOUND");
        continue;
    }

    Console.WriteLine($"  name={material.Name} shader={material.Shader.Guid} blend={material.BlendMode} flags=[{string.Join(',', material.ShaderMaterialFlags)}]");
    Console.WriteLine($"  meshFactor={material.ExtraMaterialSettings.MeshFactorColorMultiplier} meshFactor2={material.ExtraMaterialSettings.MeshFactor2ColorMultiplier} spec={material.ExtraMaterialSettings.SpecularCoef} gloss={material.ExtraMaterialSettings.GlossCoef} ao={material.ExtraMaterialSettings.AmbientOcclusionCoef}");
    foreach (var (slot, dependency) in material.Textures)
    {
        Console.WriteLine(!textures.TryGetValue(dependency.Guid, out var texture)
            ? $"  texture[{slot}]={dependency.Guid} NOT FOUND"
            : $"  texture[{slot}]={texture.Name} guid={texture.Guid} format={texture.Format} size={texture.Width}x{texture.Height} resident={texture.ResidentWidth}x{texture.ResidentHeight} flags=[{string.Join(',', texture.Flags)}]");
        if (slot == 0
            && texture is not null
            && texture.HasPixelData
            && texture.Format.IsSupported()
            && texture.TexturePixels?.Data.PrimaryRawImage is { Length: > 0 } raw)
        {
#pragma warning disable CS0612
            var decoded = TextureUtil.DecodeTextureData(raw, texture.ResidentWidth, texture.ResidentHeight, texture.Format);
#pragma warning restore CS0612
            Console.WriteLine($"    decoded rgb avg={decoded.Average(pixel => pixel.R):0.000},{decoded.Average(pixel => pixel.G):0.000},{decoded.Average(pixel => pixel.B):0.000} range={decoded.Min(pixel => Math.Min(pixel.R, Math.Min(pixel.G, pixel.B))):0.000}-{decoded.Max(pixel => Math.Max(pixel.R, Math.Max(pixel.G, pixel.B))):0.000}");
        }
    }
}

return 0;
