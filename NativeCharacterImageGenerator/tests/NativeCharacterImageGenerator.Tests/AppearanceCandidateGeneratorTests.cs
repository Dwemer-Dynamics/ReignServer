using Bannerlord.NativeCharacterImageGenerator;

namespace NativeCharacterImageGenerator.Tests;

public sealed class AppearanceCandidateGeneratorTests
{
    [Fact]
    public void MappingIsStableAndCandidatesAreDistinct()
    {
        var intent = Intent();
        var catalog = Catalog();

        var first = AppearanceCandidateGenerator.Generate(intent, catalog, 12345, 4);
        var second = AppearanceCandidateGenerator.Generate(intent, catalog, 12345, 4);

        Assert.Equal(first.Select(item => item.Character.BodyKey), second.Select(item => item.Character.BodyKey));
        Assert.Equal(4, first.Select(item => item.Character.BodyKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(first, item => Assert.True(AppearanceCandidateGenerator.IsValidBodyKey(item.Character.BodyKey)));
    }

    [Fact]
    public void MappingEnforcesCultureSexAgeAndBodyConstraints()
    {
        var intent = Intent() with { Age = 250f, Weight = -3f, Build = 8f };

        var candidate = AppearanceCandidateGenerator.Generate(intent, Catalog(), 77, 1).Single();

        Assert.True(candidate.Character.IsFemale);
        Assert.Equal("Culture.vlandia", candidate.Character.Culture);
        Assert.Equal(80f, candidate.Character.Age);
        Assert.Equal(0f, candidate.Character.Weight);
        Assert.Equal(1f, candidate.Character.Build);
        Assert.Equal(128, candidate.Character.BodyKey.Length);
        Assert.All(candidate.Character.BodyKey, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public void RerollChangesCandidatesButKeepsIntentAndStableRootIdentity()
    {
        var intent = Intent();
        var initial = AppearanceCandidateGenerator.Generate(intent, Catalog(), 99, 4, reroll: 0);
        var rerolled = AppearanceCandidateGenerator.Generate(intent, Catalog(), 99, 4, reroll: 1);

        Assert.NotEqual(initial[0].Character.BodyKey, rerolled[0].Character.BodyKey);
        Assert.Equal(initial[0].Intent, rerolled[0].Intent);
        Assert.StartsWith("appearance_lab_00000063", initial[0].Character.Id, StringComparison.Ordinal);
        Assert.StartsWith("appearance_lab_00000063", rerolled[0].Character.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportRoundTripsAndPreservesNativeMaterializationFields()
    {
        var candidate = AppearanceCandidateGenerator.Generate(Intent(), Catalog(), 42, 1).Single();

        var json = AppearanceCharacterExport.Serialize(AppearanceCharacterExport.FromCandidate(candidate));
        var restored = AppearanceCharacterExport.Deserialize(json);

        Assert.Equal(AppearanceLabContract.ExportSchemaVersion, restored.SchemaVersion);
        Assert.Equal(candidate.Seed, restored.Seed);
        Assert.Equal(candidate.Character.BodyKey, restored.NativeAppearance.BodyKey);
        Assert.Equal(candidate.Character.Culture, restored.NativeAppearance.Culture);
        Assert.Equal(candidate.Character.CivilianEquipment.Count, restored.NativeAppearance.CivilianEquipment.Count);
        Assert.Equal(candidate.Intent.Sex, restored.Intent.Sex);
        Assert.Equal(candidate.Intent.Culture, restored.Intent.Culture);
        Assert.Equal(candidate.Intent.FaceShape, restored.Intent.FaceShape);
        Assert.Equal(candidate.Intent.OverallConfidence, restored.Intent.OverallConfidence);
    }

    [Fact]
    public void GenerationDoesNotMutateRosterSourceAndPreservesCivilianEquipment()
    {
        var catalog = Catalog();
        var original = catalog[0];
        var originalKey = original.BodyKey;
        var candidate = AppearanceCandidateGenerator.Generate(Intent(), catalog, 5, 1).Single();

        Assert.Equal(originalKey, original.BodyKey);
        Assert.Equal("Native", original.SourceModule);
        Assert.Equal("AppearanceLab", candidate.Character.SourceModule);
        Assert.Equal(original.CivilianEquipment, candidate.Character.CivilianEquipment);
        Assert.False(candidate.Character.IsCampaignCharacter);
        Assert.False(candidate.Character.IsHero);
    }

    [Theory]
    [InlineData("pale", 10)]
    [InlineData("very fair", 16)]
    [InlineData("fair", 24)]
    [InlineData("olive", 98)]
    [InlineData("tanned", 122)]
    [InlineData("brown", 178)]
    [InlineData("dark", 214)]
    [InlineData("deep", 232)]
    public void ComplexionLabelsMapAcrossTheNativeLightToDarkGradient(string label, int expected)
    {
        Assert.Equal(expected, AppearanceCandidateGenerator.EncodeComplexion(label, 127));
    }

    [Fact]
    public void ExplicitPigmentationAndFemaleHairRulesOverrideEveryBasisCandidate()
    {
        var intent = Intent() with
        {
            Complexion = "fair",
            EyeColor = "green",
            HairColor = "auburn",
            FacialHair = "full beard"
        };

        var candidates = AppearanceCandidateGenerator.Generate(intent, Catalog(), 12345, 4);

        Assert.All(candidates, candidate =>
        {
            Assert.Equal("18", candidate.Character.BodyKey.Substring(2, 2));
            Assert.Equal("7A", candidate.Character.BodyKey.Substring(4, 2));
            Assert.Equal("66", candidate.Character.BodyKey.Substring(7, 2));
            Assert.Equal('0', candidate.Character.BodyKey[14]);
        });
    }

    [Fact]
    public void ExplicitBaldIntentOverridesBasisHair()
    {
        var candidate = AppearanceCandidateGenerator.Generate(
            Intent() with { HairLength = "bald" }, Catalog(), 22, 1).Single();

        Assert.Equal('0', candidate.Character.BodyKey[15]);
    }

    [Theory]
    [InlineData("white", 0)]
    [InlineData("blonde", 18)]
    [InlineData("red", 78)]
    [InlineData("auburn", 102)]
    [InlineData("brown", 140)]
    [InlineData("dark brown", 166)]
    [InlineData("black", 226)]
    public void HairColorLabelsMapAcrossTheNativeLightToDarkGradient(string label, int expected)
    {
        Assert.Equal(expected, AppearanceCandidateGenerator.EncodeHairColor(label, 127));
    }

    [Theory]
    [InlineData("blue", 24)]
    [InlineData("gray", 76)]
    [InlineData("green", 122)]
    [InlineData("hazel", 174)]
    [InlineData("brown", 220)]
    [InlineData("dark brown", 244)]
    public void EyeColorLabelsTargetBannerlordsNativeColorFamilies(string label, int expected)
    {
        Assert.Equal(expected, AppearanceCandidateGenerator.EncodeEyeColor(label, 127));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 32)]
    [InlineData(1f, 63)]
    public void ExplicitHeightUsesBannerlordsNativeSixBitHeightField(float height, int expected)
    {
        var candidate = AppearanceCandidateGenerator.Generate(
            Intent() with { Height = height }, Catalog(), 31, 1).Single();
        var part7 = Convert.ToUInt64(candidate.Character.BodyKey.Substring(96, 16), 16);

        Assert.Equal(expected, (int)((part7 >> 19) & 0x3F));
    }

    private static AppearanceIntentV1 Intent() => new()
    {
        Sex = "female",
        Culture = "vlandian",
        Age = 31f,
        Complexion = "fair",
        HairColor = "auburn",
        HairLength = "long",
        FaceShape = "angular",
        CheekboneProminence = 0.8f,
        EyeSize = 0.65f,
        EyeSpacing = 0.4f,
        NoseLength = 0.7f,
        NoseWidth = 0.3f,
        MouthWidth = 0.55f,
        LipFullness = 0.6f,
        JawShape = "strong jaw",
        ChinShape = "pointed chin",
        OverallConfidence = 0.8f
    };

    private static IReadOnlyList<NativeCharacterDefinition> Catalog() =>
    [
        Character("vlandia_f_1", true, "Culture.vlandia", '1'),
        Character("vlandia_f_2", true, "Culture.vlandia", '8'),
        Character("vlandia_m_1", false, "Culture.vlandia", '4'),
        Character("aserai_f_1", true, "Culture.aserai", 'B')
    ];

    private static NativeCharacterDefinition Character(
        string id,
        bool female,
        string culture,
        char keyDigit) => new(
            id,
            id,
            culture,
            female,
            30f,
            0.5f,
            0.5f,
            new string(keyDigit, 128),
            "civilian_template",
            [new NativeEquipmentAssignment("Body", "native_tunic")],
            "Modules/SandBox/ModuleData/lords.xml");
}
