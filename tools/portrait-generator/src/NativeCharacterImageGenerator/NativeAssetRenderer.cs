using System.Numerics;
using TpacTool.Lib;

namespace Bannerlord.NativeCharacterImageGenerator;

internal static class NativeAssetRenderer
{
    public static RenderResult Render(string packagePath, CliOptions options)
    {
        var package = new AssetPackage(packagePath);
        var meshes = LoadRenderMeshes(package, options.AssetNames, options.MorphWeights, options.SkinColor);

        return SoftwareRasterizer.Render(
            meshes,
            options.Width,
            options.Height,
            options.YawDegrees,
            options.PitchDegrees,
            options.Transparent);
    }

    internal static RenderMesh[] LoadRenderMeshes(
        AssetPackage package,
        IReadOnlyCollection<string> assetNames,
        IReadOnlyDictionary<int, float> morphWeights,
        Vector4 skinColor,
        IReadOnlyDictionary<Guid, Material>? materialIndex = null,
        IReadOnlyDictionary<Guid, Texture>? textureIndex = null)
    {
        var allMetameshes = package.Items.OfType<Metamesh>().ToArray();
        var metameshes = assetNames
            .Select(name => allMetameshes.FirstOrDefault(
                item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(
                    $"Native metamesh '{name}' was not found in {package.File?.Name ?? "the selected package"}."))
            .ToArray();

        var meshes = metameshes
            .SelectMany(metamesh => metamesh.Meshes)
            .Where(mesh => mesh.Lod == 0 && mesh.EditData is not null)
            .Select(mesh => BuildRenderMesh(
                package,
                mesh,
                morphWeights,
                skinColor,
                materialIndex,
                textureIndex))
            .Where(mesh => mesh.Vertices.Length > 0 && mesh.Faces.Length > 0)
            .ToArray();

        if (meshes.Length == 0)
        {
            throw new InvalidDataException("The requested assets contain no readable detailed native geometry.");
        }

        return meshes;
    }

    private static RenderMesh BuildRenderMesh(
        AssetPackage package,
        Mesh mesh,
        IReadOnlyDictionary<int, float> morphWeights,
        Vector4 skinColor,
        IReadOnlyDictionary<Guid, Material>? materialIndex,
        IReadOnlyDictionary<Guid, Texture>? textureIndex)
    {
        var editData = mesh.EditData!.Data;
        var positions = editData.Positions
            .Select(position => new Vector3(position.X, position.Y, position.Z))
            .ToArray();
        var normals = editData.Vertices
            .Select(vertex => new Vector3(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z))
            .ToArray();

        ApplyMorphs(editData, morphWeights, positions, normals);

        var vertices = new RenderVertex[editData.Vertices.Length];
        for (var index = 0; index < vertices.Length; index++)
        {
            var source = editData.Vertices[index];
            if (source.PositionIndex >= positions.Length)
            {
                throw new InvalidDataException($"Mesh '{mesh.Name}' contains an invalid native position index.");
            }

            var normal = normals[index];
            var tangent = new Vector3(source.Tangent.X, source.Tangent.Y, source.Tangent.Z);
            var binormal = new Vector3(source.Binormal.X, source.Binormal.Y, source.Binormal.Z);
            vertices[index] = new RenderVertex(
                positions[source.PositionIndex],
                normal.LengthSquared() > 0.000001f ? Vector3.Normalize(normal) : Vector3.UnitY,
                tangent.LengthSquared() > 0.000001f ? Vector3.Normalize(tangent) : Vector3.UnitX,
                binormal.LengthSquared() > 0.000001f ? Vector3.Normalize(binormal) : Vector3.UnitZ,
                source.Uv);
        }

        var faces = editData.Faces
            .Where(face => face.V0 >= 0 && face.V0 < vertices.Length
                && face.V1 >= 0 && face.V1 < vertices.Length
                && face.V2 >= 0 && face.V2 < vertices.Length)
            .Select(face => new RenderFace(face.V0, face.V1, face.V2))
            .ToArray();

        var material = ResolveMaterial(package, materialIndex, mesh.Material.Guid);
        var diffuse = ResolveTexture(package, textureIndex, material, 0);
        var colorMask = ResolveTexture(package, textureIndex, material, 1);
        var normalMap = mesh.Name.StartsWith("head_", StringComparison.OrdinalIgnoreCase)
            ? null
            : ResolveTexture(package, textureIndex, material, 2);
        var specular = ResolveTexture(package, textureIndex, material, 4);
        var useDoubleColorMap = material?.ShaderMaterialFlags.Any(flag =>
            flag.Equals("use_double_colormap_with_mask_texture", StringComparison.OrdinalIgnoreCase)) == true;
        var useAlpha = material is not null
            && (material.ShaderMaterialFlags.Any(flag =>
                    flag.Equals("alpha_test", StringComparison.OrdinalIgnoreCase))
                || !material.BlendMode.Equals("no_alpha_blend", StringComparison.OrdinalIgnoreCase));
        var settings = material?.ExtraMaterialSettings;
        return new RenderMesh(
            mesh.Name,
            vertices,
            faces,
            diffuse,
            useDoubleColorMap ? colorMask : null,
            normalMap,
            specular,
            GetBaseColor(mesh.Name, skinColor),
            Vector4.One,
            Vector4.One,
            useDoubleColorMap,
            useAlpha,
            settings?.NormalmapPower ?? 1f,
            settings?.SpecularCoef ?? 0.2f,
            settings?.GlossCoef ?? 0.25f);
    }

    private static void ApplyMorphs(
        MeshEditData editData,
        IReadOnlyDictionary<int, float> morphWeights,
        Vector3[] positions,
        Vector3[] normals)
    {
        if (morphWeights.Count == 0 || editData.MorphFrames.Count == 0)
        {
            return;
        }

        var basis = editData.MorphFrames.FirstOrDefault(frame => frame.Time == 0)
            ?? editData.MorphFrames[0];
        foreach (var (requestedIndex, weight) in morphWeights)
        {
            var target = editData.MorphFrames.FirstOrDefault(frame => frame.Time == requestedIndex);
            if (target is null)
            {
                // Hair, eyebrows, and some facial submeshes only carry the
                // subset of native deform frames that affect their geometry.
                continue;
            }

            if (target.Positions.Length == positions.Length && basis.Positions.Length == positions.Length)
            {
                for (var index = 0; index < positions.Length; index++)
                {
                    positions[index] += weight * new Vector3(
                        target.Positions[index].X - basis.Positions[index].X,
                        target.Positions[index].Y - basis.Positions[index].Y,
                        target.Positions[index].Z - basis.Positions[index].Z);
                }
            }

            if (target.Normals.Length == normals.Length && basis.Normals.Length == normals.Length)
            {
                for (var index = 0; index < normals.Length; index++)
                {
                    normals[index] += weight * new Vector3(
                        target.Normals[index].X - basis.Normals[index].X,
                        target.Normals[index].Y - basis.Normals[index].Y,
                        target.Normals[index].Z - basis.Normals[index].Z);
                }
            }
        }
    }

    private static Material? ResolveMaterial(
        AssetPackage package,
        IReadOnlyDictionary<Guid, Material>? materialIndex,
        Guid materialGuid)
    {
        if (materialIndex?.TryGetValue(materialGuid, out var indexed) == true)
        {
            return indexed;
        }

        return package.Items.OfType<Material>().FirstOrDefault(item => item.Guid == materialGuid);
    }

    private static TextureSampler? ResolveTexture(
        AssetPackage package,
        IReadOnlyDictionary<Guid, Texture>? textureIndex,
        Material? material,
        int slot)
    {
        if (material is null || !material.Textures.TryGetValue(slot, out var dependence))
        {
            return null;
        }

        Texture? texture = null;
        if (textureIndex?.TryGetValue(dependence.Guid, out var indexed) == true)
        {
            texture = indexed;
        }

        texture ??= package.Items.OfType<Texture>().FirstOrDefault(item => item.Guid == dependence.Guid);
        return texture is null ? null : TextureSampler.TryCreate(texture);
    }

    private static Vector4 GetBaseColor(string meshName, Vector4 skinColor)
    {
        if (meshName.Contains("eye", StringComparison.OrdinalIgnoreCase))
        {
            return Vector4.One;
        }

        if (meshName.Contains("mouth", StringComparison.OrdinalIgnoreCase)
            || meshName.Contains("teeth", StringComparison.OrdinalIgnoreCase))
        {
            return new Vector4(0.45f, 0.18f, 0.15f, 1f);
        }

        if (meshName.StartsWith("head_male", StringComparison.OrdinalIgnoreCase)
            || meshName.StartsWith("head_female", StringComparison.OrdinalIgnoreCase)
            || meshName.StartsWith("body_male", StringComparison.OrdinalIgnoreCase)
            || meshName.StartsWith("body_female", StringComparison.OrdinalIgnoreCase)
            || meshName.StartsWith("hands_male", StringComparison.OrdinalIgnoreCase)
            || meshName.StartsWith("hands_female", StringComparison.OrdinalIgnoreCase)
            || meshName.StartsWith("feet_male", StringComparison.OrdinalIgnoreCase)
            || meshName.StartsWith("feet_female", StringComparison.OrdinalIgnoreCase))
        {
            return skinColor;
        }

        return Vector4.One;
    }
}
