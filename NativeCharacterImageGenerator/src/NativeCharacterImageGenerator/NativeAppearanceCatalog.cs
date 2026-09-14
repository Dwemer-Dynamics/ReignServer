using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace Bannerlord.NativeCharacterImageGenerator;

internal sealed record NativeAppearanceSelection(
    Vector4 SkinColor,
    Vector4 HairColor,
    string HairAsset,
    string BeardAsset,
    string EyebrowAsset,
    IReadOnlyDictionary<int, float> FaceMorphs);

internal sealed class NativeAppearanceCatalog
{
    private readonly NativeSkinAppearance _male;
    private readonly NativeSkinAppearance _female;

    private NativeAppearanceCatalog(NativeSkinAppearance male, NativeSkinAppearance female)
    {
        _male = male;
        _female = female;
    }

    public static NativeAppearanceCatalog Load(string gameRoot)
    {
        var path = Path.Combine(gameRoot, "Modules", "Native", "ModuleData", "skins.xml");
        if (!File.Exists(path))
        {
            return CreateFallback();
        }

        var document = XDocument.Load(path, LoadOptions.None);
        var skins = document.Descendants()
            .Where(element => element.Name.LocalName.Equals("skin", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new NativeAppearanceCatalog(
            ReadSkin(skins, "man", false),
            ReadSkin(skins, "woman", true));
    }

    public NativeAppearanceSelection Resolve(NativeCharacterDefinition character)
    {
        var skin = character.IsFemale ? _female : _male;
        var key = new string(character.BodyKey.Where(Uri.IsHexDigit).ToArray());
        var skinColor = ResolveGradient(skin.SkinColors, ReadByte(key, 2), new Vector3(1f, 0.86f, 0.78f));
        var hairColor = ResolveGradient(skin.HairColors, ReadByte(key, 7), new Vector3(0.10f, 0.07f, 0.05f));
        var hairIndex = ReadNibble(key, 15);
        var hairAsset = hairIndex >= 0 && hairIndex < skin.HairAssets.Count
            ? skin.HairAssets[hairIndex]
            : string.Empty;
        var beardIndex = ReadNibble(key, 14);
        var beardAsset = beardIndex >= 0 && beardIndex < skin.BeardAssets.Count
            ? skin.BeardAssets[beardIndex]
            : string.Empty;
        var eyebrowIndex = ReadNibble(key, 124);
        var eyebrowAsset = eyebrowIndex >= 0 && eyebrowIndex < skin.EyebrowAssets.Count
            ? skin.EyebrowAssets[eyebrowIndex]
            : string.Empty;
        return new NativeAppearanceSelection(
            skinColor,
            hairColor,
            hairAsset,
            beardAsset,
            eyebrowAsset,
            ResolveMorphs(skin.DeformKeys, key));
    }

    private static NativeSkinAppearance ReadSkin(XElement[] skins, string name, bool female)
    {
        var element = skins.FirstOrDefault(candidate =>
            Attribute(candidate, "name").Equals(name, StringComparison.OrdinalIgnoreCase));
        if (element is null)
        {
            return CreateFallbackSkin(female);
        }

        var hairs = element.Descendants()
            .Where(child => child.Name.LocalName.Equals("hair_mesh", StringComparison.OrdinalIgnoreCase))
            .Select(child => Attribute(child, "name"))
            .ToArray();
        var eyebrows = element.Descendants()
            .Where(child => child.Name.LocalName.Equals("eyebrow_mesh", StringComparison.OrdinalIgnoreCase))
            .Select(child => Attribute(child, "name"))
            .ToArray();
        var beards = element.Descendants()
            .Where(child => child.Name.LocalName.Equals("beard_mesh", StringComparison.OrdinalIgnoreCase))
            .Select(child => Attribute(child, "name"))
            .ToArray();
        var skinColors = ReadGradient(element, "skin_color_gradient_point");
        var hairColors = ReadGradient(element, "hair_color_gradient_point");
        var deformKeys = element.Elements()
            .FirstOrDefault(child => child.Name.LocalName.Equals("deform_keys", StringComparison.OrdinalIgnoreCase))
            ?.Elements()
            .Where(child => child.Name.LocalName.Equals("deform_key", StringComparison.OrdinalIgnoreCase))
            .Select(child => new NativeDeformKey(
                ParseInt(Attribute(child, "key_time_point"), -1),
                ParseFloat(Attribute(child, "key_min"), 0f),
                ParseFloat(Attribute(child, "key_max"), 0f)))
            .Where(key => key.TimePoint is > 0 and <= 59)
            .ToArray()
            ?? [];
        return new NativeSkinAppearance(hairs, beards, eyebrows, skinColors, hairColors, deformKeys);
    }

    private static Vector3[] ReadGradient(XElement skin, string pointName) =>
        skin.Descendants()
            .Where(element => element.Name.LocalName.Equals(pointName, StringComparison.OrdinalIgnoreCase))
            .Select(element => ParseVector(Attribute(element, "point")))
            .Where(color => color.HasValue)
            .Select(color => color!.Value)
            .ToArray();

    private static Vector4 ResolveGradient(
        IReadOnlyList<Vector3> gradient,
        int encoded,
        Vector3 fallback)
    {
        if (gradient.Count == 0 || encoded < 0)
        {
            return new Vector4(fallback, 1f);
        }

        var position = (encoded / 255f) * (gradient.Count - 1);
        var lower = Math.Clamp((int)MathF.Floor(position), 0, gradient.Count - 1);
        var upper = Math.Min(lower + 1, gradient.Count - 1);
        var color = Vector3.Lerp(gradient[lower], gradient[upper], position - lower);
        return new Vector4(Vector3.Clamp(color, Vector3.Zero, Vector3.One), 1f);
    }

    private static IReadOnlyDictionary<int, float> ResolveMorphs(
        IReadOnlyList<NativeDeformKey> deformKeys,
        string key)
    {
        var result = new Dictionary<int, float>();
        for (var index = 0; index < deformKeys.Count && index < 64; index++)
        {
            // KeyPart1 contains appearance choices. Face controls begin in
            // KeyPart2 and each 64-bit part is formatted most-significant
            // nibble first, while FaceGen packs controls least-significant first.
            var keyPartStart = ((index / 16) + 1) * 16;
            var characterIndex = keyPartStart + (15 - (index % 16));
            var encoded = ReadNibble(key, characterIndex);
            if (encoded < 0)
            {
                continue;
            }

            var deform = deformKeys[index];
            result[deform.TimePoint] = deform.Minimum
                + ((deform.Maximum - deform.Minimum) * (encoded / 15f));
        }

        return result;
    }

    private static int ReadByte(string key, int start)
    {
        if (start < 0 || start + 2 > key.Length)
        {
            return -1;
        }

        return int.TryParse(
            key.AsSpan(start, 2),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : -1;
    }

    private static int ReadNibble(string key, int index)
    {
        if (index < 0 || index >= key.Length)
        {
            return -1;
        }

        return int.TryParse(
            key.AsSpan(index, 1),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : -1;
    }

    private static Vector3? ParseVector(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return null;
        }

        return new Vector3(x, y, z);
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;

    private static float ParseFloat(string value, float fallback) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;

    private static string Attribute(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value
        ?? string.Empty;

    private static NativeAppearanceCatalog CreateFallback() =>
        new(CreateFallbackSkin(false), CreateFallbackSkin(true));

    private static NativeSkinAppearance CreateFallbackSkin(bool female)
    {
        var hair = female
            ? new[] { string.Empty, "female_hair_l", "female_hair_m", "female_hair_n", "female_hair_o" }
            : new[] { string.Empty, "hair_male_g_a", "hair_male_g_b" };
        return new NativeSkinAppearance(
            hair,
            female
                ? []
                : [string.Empty, "beard_a", "beard_b", "beard_c", "beard_d"],
            female
                ? [string.Empty, "female_eyebrow_1", "female_eyebrow_2", "female_eyebrow_3"]
                : [string.Empty, "male_eyebrow_1", "male_eyebrow_2", "male_eyebrow_3"],
            [new Vector3(1f, 0.86f, 0.78f), new Vector3(0.18f, 0.12f, 0.09f)],
            [new Vector3(0.8f, 0.65f, 0.4f), new Vector3(0.03f, 0.03f, 0.04f)],
            []);
    }

    private sealed record NativeSkinAppearance(
        IReadOnlyList<string> HairAssets,
        IReadOnlyList<string> BeardAssets,
        IReadOnlyList<string> EyebrowAssets,
        IReadOnlyList<Vector3> SkinColors,
        IReadOnlyList<Vector3> HairColors,
        IReadOnlyList<NativeDeformKey> DeformKeys);

    private sealed record NativeDeformKey(int TimePoint, float Minimum, float Maximum);
}
