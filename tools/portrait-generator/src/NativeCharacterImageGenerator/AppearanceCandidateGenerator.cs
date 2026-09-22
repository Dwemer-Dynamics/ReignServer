using System.Globalization;
using System.Text.Json;

namespace Bannerlord.NativeCharacterImageGenerator;

internal static class AppearanceCandidateGenerator
{
    public const string Version = "appearance_lab_mapper_v2";
    private static readonly string[] CultureIds =
        ["aserai", "battania", "empire", "khuzait", "nord", "sturgia", "vlandia"];

    public static IReadOnlyList<AppearanceCandidate> Generate(
        AppearanceIntentV1 intent,
        IReadOnlyList<NativeCharacterDefinition> catalog,
        int? requestedSeed,
        int count = 4,
        int reroll = 0)
    {
        if (count is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(count));
        var valid = catalog.Where(character => IsValidBodyKey(character.BodyKey)).ToArray();
        if (valid.Length == 0) throw new InvalidOperationException("No native body-key templates are available.");

        var rootSeed = requestedSeed ?? StableSeed(JsonSerializer.Serialize(intent, AppearanceLabContract.JsonOptions));
        var isFemale = ResolveFemale(intent.Sex, rootSeed);
        var culture = NormalizeCulture(intent.Culture, valid, rootSeed);
        var compatible = valid.Where(character => character.IsFemale == isFemale
                && NormalizeCultureId(character.Culture) == culture)
            .OrderBy(character => character.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (compatible.Length == 0)
        {
            compatible = valid.Where(character => character.IsFemale == isFemale)
                .OrderBy(character => character.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        if (compatible.Length == 0) compatible = valid;

        var results = new List<AppearanceCandidate>(count);
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            var seed = unchecked(rootSeed + reroll * 104729 + index * 7919);
            var random = new Random(seed);
            var basis = compatible[Math.Abs(random.Next()) % compatible.Length];
            var key = ApplyIntent(basis.BodyKey, intent, compatible, random, index);
            for (var attempt = 0; !usedKeys.Add(key) && attempt < 16; attempt++)
            {
                key = SetMorph(key, (index * 7 + attempt * 11) % 57, (index + attempt + 3) % 16);
            }

            var age = Math.Clamp(intent.Age ?? basis.Age, 18f, 80f);
            var weight = Math.Clamp(intent.Weight ?? basis.Weight, 0f, 1f);
            var build = Math.Clamp(intent.Build ?? basis.Build, 0f, 1f);
            var character = basis with
            {
                Id = $"appearance_lab_{rootSeed:X8}_{reroll:D2}_{index:D2}",
                Name = $"Appearance Candidate {index + 1}",
                Culture = "Culture." + culture,
                IsFemale = isFemale,
                Age = age,
                Weight = weight,
                Build = build,
                BodyKey = key,
                NativePortraitPath = string.Empty,
                ReignCampaignId = string.Empty,
                CharacterObjectId = string.Empty,
                IsCampaignCharacter = false,
                CharacterCode = string.Empty,
                FaceTemplateId = string.Empty,
                SourceModule = "AppearanceLab",
                IsHero = false
            };
            results.Add(new AppearanceCandidate(index, seed, character, intent, basis.Id));
        }
        return results;
    }

    public static bool IsValidBodyKey(string value) => value.Length >= 128
        && value.Take(128).All(Uri.IsHexDigit);

    public static int StableSeed(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in value ?? string.Empty)
            {
                hash ^= character;
                hash *= 16777619;
            }
            return (int)(hash & 0x7FFFFFFF);
        }
    }

    private static string ApplyIntent(
        string bodyKey,
        AppearanceIntentV1 intent,
        IReadOnlyList<NativeCharacterDefinition> compatible,
        Random random,
        int candidateIndex)
    {
        var key = new string(bodyKey.Take(128).ToArray()).ToUpperInvariant();
        key = ApplyFaceShape(key, intent.FaceShape);
        key = ApplyUnit(key, "CheekboneHeight", intent.CheekboneProminence);
        key = ApplyUnit(key, "CheekboneDepth", intent.CheekboneProminence);
        key = ApplyUnit(key, "EyeSize", intent.EyeSize);
        key = ApplyUnit(key, "EyeDistance", intent.EyeSpacing);
        key = ApplyUnit(key, "NoseLength", intent.NoseLength);
        key = ApplyUnit(key, "NoseWidth", intent.NoseWidth);
        key = ApplyUnit(key, "MouthWidth", intent.MouthWidth);
        key = ApplyUnit(key, "LipThickness", intent.LipFullness);
        key = ApplyNamedShapes(key, intent);

        if (intent.Age.HasValue)
        {
            key = ApplyUnit(key, "OldFace", Math.Clamp((intent.Age.Value - 35f) / 40f, 0f, 1f));
            key = ApplyUnit(key, "KidFace", Math.Clamp((24f - intent.Age.Value) / 10f, 0f, 1f));
        }
        key = ApplyHeightMultiplier(key, intent.Height);

        key = SetByte(key, 2, EncodeComplexion(intent.Complexion, ReadByte(key, 2)));
        key = SetByte(key, 4, EncodeEyeColor(intent.EyeColor, ReadByte(key, 4)));
        key = SetByte(key, 7, EncodeHairColor(intent.HairColor, ReadByte(key, 7)));
        var choiceSource = compatible[Math.Abs(random.Next()) % compatible.Count].BodyKey;
        if (IsBald(intent.HairLength))
        {
            key = SetNibble(key, 15, 0);
        }
        else if (!IsUnspecified(intent.HairLength) || candidateIndex > 0)
        {
            key = SetNibble(key, 15, ReadNibble(choiceSource, 15));
        }
        if (intent.Sex.Equals("female", StringComparison.OrdinalIgnoreCase))
        {
            key = SetNibble(key, 14, 0);
        }
        else if (!IsUnspecified(intent.FacialHair) || candidateIndex > 0)
        {
            key = SetNibble(key, 14, intent.FacialHair.Contains("clean", StringComparison.OrdinalIgnoreCase)
                ? 0
                : ReadNibble(choiceSource, 14));
        }
        key = SetNibble(key, 124, ReadNibble(choiceSource, 124));

        // Candidate-local variation is intentionally small and only affects controls
        // that were not confidently specified by the semantic intent.
        var variationMorphs = new List<string> { "EarShape" };
        if (IsUnspecified(intent.FaceShape)) variationMorphs.AddRange(["FaceDepth", "TempleWidth"]);
        if (IsUnspecified(intent.EyeShape)) variationMorphs.Add("EyeDepth");
        if (IsUnspecified(intent.NoseShape)) variationMorphs.Add("NoseTipHeight");
        if (IsUnspecified(intent.ChinShape)) variationMorphs.Add("ChinLength");
        foreach (var morph in variationMorphs)
        {
            if (FaceMorphCatalog.TryGetIndex(morph, out var index))
            {
                var current = ReadMorph(key, index);
                key = SetMorph(key, index, Math.Clamp(current + random.Next(-2, 3), 0, 15));
            }
        }
        return key;
    }

    private static string ApplyFaceShape(string key, string shape)
    {
        var value = shape.ToLowerInvariant();
        if (value.Contains("round"))
        {
            key = ApplyUnit(key, "FaceWidth", 0.78f);
            key = ApplyUnit(key, "FaceRatio", 0.32f);
            return ApplyUnit(key, "FaceSharpness", 0.22f);
        }
        if (value.Contains("square") || value.Contains("angular"))
        {
            key = ApplyUnit(key, "FaceWidth", 0.72f);
            key = ApplyUnit(key, "JawLine", 0.8f);
            return ApplyUnit(key, "JawShape", 0.76f);
        }
        if (value.Contains("narrow") || value.Contains("long"))
        {
            key = ApplyUnit(key, "FaceWidth", 0.22f);
            return ApplyUnit(key, "FaceRatio", 0.78f);
        }
        if (value.Contains("heart"))
        {
            key = ApplyUnit(key, "CheekboneWidth", 0.76f);
            return ApplyUnit(key, "JawShape", 0.28f);
        }
        if (value.Contains("oval"))
        {
            key = ApplyUnit(key, "FaceWidth", 0.48f);
            key = ApplyUnit(key, "FaceRatio", 0.62f);
            return ApplyUnit(key, "FaceSharpness", 0.46f);
        }
        return key;
    }

    private static string ApplyNamedShapes(string key, AppearanceIntentV1 intent)
    {
        var eye = intent.EyeShape;
        if (eye.Contains("round", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "EyeShape", 0.25f);
        if (eye.Contains("almond", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "EyeShape", 0.7f);
        if (eye.Contains("deep", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "EyeDepth", 0.82f);
        if (eye.Contains("hooded", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "EyelidHeight", 0.22f);
        if (eye.Contains("upturned", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "EyeOuterHeight", 0.82f);
        if (eye.Contains("downturned", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "EyeOuterHeight", 0.18f);

        var brow = intent.BrowShape;
        if (brow.Contains("arched", StringComparison.OrdinalIgnoreCase))
        {
            key = ApplyUnit(key, "BrowOuterHeight", 0.72f);
            key = ApplyUnit(key, "BrowMiddleHeight", 0.84f);
            key = ApplyUnit(key, "BrowInnerHeight", 0.45f);
        }
        if (brow.Contains("straight", StringComparison.OrdinalIgnoreCase))
        {
            key = ApplyUnit(key, "BrowOuterHeight", 0.5f);
            key = ApplyUnit(key, "BrowMiddleHeight", 0.5f);
            key = ApplyUnit(key, "BrowInnerHeight", 0.5f);
        }
        if (brow.Contains("heavy", StringComparison.OrdinalIgnoreCase) || brow.Contains("thick", StringComparison.OrdinalIgnoreCase))
        {
            key = ApplyUnit(key, "EyebrowDepth", 0.78f);
        }
        if (brow.Contains("thin", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "EyebrowDepth", 0.22f);

        var nose = intent.NoseShape;
        if (nose.Contains("hook", StringComparison.OrdinalIgnoreCase) || nose.Contains("aquiline", StringComparison.OrdinalIgnoreCase))
        {
            key = ApplyUnit(key, "NoseBump", 0.85f);
            key = ApplyUnit(key, "NoseShape", 0.8f);
        }
        if (nose.Contains("upturned", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "NoseTipHeight", 0.82f);
        if (nose.Contains("button", StringComparison.OrdinalIgnoreCase))
        {
            key = ApplyUnit(key, "NoseLength", 0.25f);
            key = ApplyUnit(key, "NoseSize", 0.28f);
            key = ApplyUnit(key, "NoseTipHeight", 0.72f);
        }
        if (nose.Contains("straight", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "NoseBump", 0.5f);
        if (nose.Contains("crooked", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "NoseAsymmetry", 0.82f);

        var jaw = intent.JawShape;
        if (jaw.Contains("strong", StringComparison.OrdinalIgnoreCase) || jaw.Contains("square", StringComparison.OrdinalIgnoreCase) || jaw.Contains("broad", StringComparison.OrdinalIgnoreCase) || jaw.Contains("angular", StringComparison.OrdinalIgnoreCase))
        {
            key = ApplyUnit(key, "JawLine", 0.82f);
            key = ApplyUnit(key, "JawShape", 0.78f);
        }
        if (jaw.Contains("soft", StringComparison.OrdinalIgnoreCase) || jaw.Contains("narrow", StringComparison.OrdinalIgnoreCase))
        {
            key = ApplyUnit(key, "JawLine", 0.25f);
            key = ApplyUnit(key, "JawShape", 0.3f);
        }

        var chin = intent.ChinShape;
        if (chin.Contains("prominent", StringComparison.OrdinalIgnoreCase) || chin.Contains("strong", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "ChinForward", 0.84f);
        if (chin.Contains("receding", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "ChinForward", 0.15f);
        if (chin.Contains("pointed", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "ChinShape", 0.82f);
        if (chin.Contains("round", StringComparison.OrdinalIgnoreCase)) key = ApplyUnit(key, "ChinShape", 0.28f);

        if (intent.DistinguishingMarks.Any(mark =>
                mark.Contains("weathered", StringComparison.OrdinalIgnoreCase)
                || mark.Contains("wrinkle", StringComparison.OrdinalIgnoreCase)))
        {
            var ageWeathering = intent.Age.HasValue
                ? Math.Clamp((intent.Age.Value - 28f) / 42f, 0.2f, 0.78f)
                : 0.42f;
            if (FaceMorphCatalog.TryGetIndex("OldFace", out var oldFaceIndex))
            {
                ageWeathering = Math.Max(ageWeathering, ReadMorph(key, oldFaceIndex) / 15f);
            }
            key = ApplyUnit(key, "OldFace", ageWeathering);
        }
        return key;
    }

    private static string ApplyUnit(string key, string name, float? value)
    {
        if (!value.HasValue || !FaceMorphCatalog.TryGetIndex(name, out var index)) return key;
        return SetMorph(key, index, (int)MathF.Round(Math.Clamp(value.Value, 0f, 1f) * 15f));
    }

    private static string SetMorph(string key, int morphIndex, int value)
    {
        var position = ((morphIndex / 16) + 1) * 16 + (15 - morphIndex % 16);
        return SetNibble(key, position, value);
    }

    private static int ReadMorph(string key, int morphIndex)
    {
        var position = ((morphIndex / 16) + 1) * 16 + (15 - morphIndex % 16);
        return ReadNibble(key, position);
    }

    private static string SetByte(string key, int position, int value)
    {
        var hex = Math.Clamp(value, 0, 255).ToString("X2", CultureInfo.InvariantCulture);
        var chars = key.ToCharArray();
        chars[position] = hex[0];
        chars[position + 1] = hex[1];
        return new string(chars);
    }

    private static string SetNibble(string key, int position, int value)
    {
        var chars = key.ToCharArray();
        chars[position] = Math.Clamp(value, 0, 15).ToString("X1", CultureInfo.InvariantCulture)[0];
        return new string(chars);
    }

    private static string ApplyHeightMultiplier(string key, float? value)
    {
        if (!value.HasValue) return key;

        // MBBodyProperties stores HeightMultiplier as a six-bit value beginning
        // at bit 19 of KeyPart7. Preserve every other native field in that part.
        const int partStart = 96;
        const int startBit = 19;
        const int bitCount = 6;
        var part = ulong.Parse(key.AsSpan(partStart, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var mask = ((1UL << bitCount) - 1UL) << startBit;
        var encoded = (ulong)(int)MathF.Round(Math.Clamp(value.Value, 0f, 1f) * 63f);
        part = (part & ~mask) | (encoded << startBit);
        var hex = part.ToString("X16", CultureInfo.InvariantCulture);
        return key[..partStart] + hex + key[(partStart + 16)..];
    }

    private static int ReadByte(string key, int position) =>
        int.Parse(key.AsSpan(position, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static int ReadNibble(string key, int position) =>
        int.Parse(key.AsSpan(position, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    internal static int EncodeComplexion(string value, int fallback)
    {
        var lower = value.ToLowerInvariant();
        // Native's 24-point gradient is ordered from light to dark. These offsets
        // target representative stops rather than dividing 0..255 into equal labels.
        if (lower.Contains("porcelain") || lower.Contains("alabaster") || lower.Contains("pale")) return 10;
        if (lower.Contains("very fair")) return 16;
        if (lower.Contains("fair") || lower.Contains("light skin")) return 24;
        if (lower.Contains("light olive")) return 76;
        if (lower.Contains("olive") || lower.Contains("golden")) return 98;
        if (lower.Contains("tan")) return 122;
        if (lower.Contains("medium")) return 148;
        if (lower.Contains("deep")) return 232;
        if (lower.Contains("dark")) return 214;
        if (lower.Contains("brown")) return 178;
        return fallback;
    }

    internal static int EncodeHairColor(string value, int fallback)
    {
        var lower = value.ToLowerInvariant();
        if (lower.Contains("white") || lower.Contains("gray") || lower.Contains("grey") || lower.Contains("platinum")) return 0;
        if (lower.Contains("blond")) return 18;
        if (lower.Contains("strawberry") || lower.Contains("ginger") || lower.Contains("red")) return 78;
        if (lower.Contains("auburn")) return 102;
        if (lower.Contains("dark brown")) return 166;
        if (lower.Contains("brown")) return 140;
        if (lower.Contains("black")) return 226;
        return fallback;
    }

    internal static int EncodeEyeColor(string value, int fallback)
    {
        var lower = value.ToLowerInvariant();
        // Native's eye gradient runs through blue, gray, green, hazel, and brown
        // families in that order. Target the center of each family.
        if (lower.Contains("blue")) return 24;
        if (lower.Contains("gray") || lower.Contains("grey")) return 76;
        if (lower.Contains("green")) return 122;
        if (lower.Contains("hazel")) return 174;
        if (lower.Contains("dark")) return 244;
        if (lower.Contains("brown")) return 220;
        return fallback;
    }

    private static bool IsBald(string value) => value.Contains("bald", StringComparison.OrdinalIgnoreCase)
        || value.Contains("shaved", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnspecified(string value) => string.IsNullOrWhiteSpace(value)
        || value.Equals("unspecified", StringComparison.OrdinalIgnoreCase);

    private static bool ResolveFemale(string sex, int seed) => sex.Equals("female", StringComparison.OrdinalIgnoreCase)
        || (!sex.Equals("male", StringComparison.OrdinalIgnoreCase) && seed % 2 == 0);

    private static string NormalizeCulture(
        string culture,
        IReadOnlyList<NativeCharacterDefinition> catalog,
        int seed)
    {
        var normalized = NormalizeCultureId(culture);
        if (CultureIds.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return normalized;
        var available = catalog.Select(character => NormalizeCultureId(character.Culture))
            .Where(value => CultureIds.Contains(value, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        return available.Length == 0 ? "empire" : available[Math.Abs(seed) % available.Length];
    }

    private static string NormalizeCultureId(string value)
    {
        var normalized = value.Replace("Culture.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim().ToLowerInvariant();
        return normalized switch
        {
            "imperial" => "empire",
            "battanian" => "battania",
            "vlandian" => "vlandia",
            "sturgian" => "sturgia",
            "khuzait" => "khuzait",
            "aserai" => "aserai",
            "northern" => "nord",
            _ => normalized
        };
    }
}
