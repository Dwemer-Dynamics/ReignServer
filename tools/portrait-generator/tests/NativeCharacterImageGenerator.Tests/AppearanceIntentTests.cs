using Bannerlord.NativeCharacterImageGenerator;

namespace NativeCharacterImageGenerator.Tests;

public sealed class AppearanceIntentTests
{
    [Fact]
    public async Task BuiltInAnalyzerExtractsTextWithoutAProvider()
    {
        var analyzer = new RuleBasedAppearanceIntentAnalyzer();

        var result = await analyzer.AnalyzeAsync(
            new AppearanceAnalysisRequest(
                "A 38-year-old Vlandian woman with fair skin, long auburn hair, green eyes, high cheekbones, a narrow nose, and a strong jaw.",
                string.Empty),
            CancellationToken.None);

        Assert.Equal("female", result.Sex);
        Assert.Equal(38f, result.Age);
        Assert.Equal("vlandia", result.Culture);
        Assert.Equal("fair", result.Complexion);
        Assert.Equal("auburn", result.HairColor);
        Assert.Equal("long", result.HairLength);
        Assert.Equal("green", result.EyeColor);
        Assert.True(result.CheekboneProminence > 0.5f);
        Assert.Empty(result.Confidence);
        Assert.Null(result.OverallConfidence);
    }

    [Theory]
    [InlineData("A portrait with 35 mm framing, 12 freckles and green eyes.", null)]
    [InlineData("A person with age-related wrinkles in a 24 mm lens portrait.", null)]
    [InlineData("The subject is not a 45-year-old.", null)]
    [InlineData("The subject isn’t 45 years old.", null)]
    [InlineData("Not really a young adult.", null)]
    [InlineData("A woman of age 32, rather than a 46-year-old.", 32)]
    [InlineData("Not a 40-year-old. The subject is 34 years old.", 34)]
    [InlineData("Age: 36, with green eyes and 14 freckles.", 36)]
    [InlineData("Her age is 34, with green eyes.", 34)]
    [InlineData("Her age is not 34 years old.", null)]
    [InlineData("Her age is at least 34 years old.", null)]
    [InlineData("Her age is 34 or 36.", null)]
    [InlineData("Aged 36; described again as 36 years old.", 36)]
    [InlineData("Age 30–40, with green eyes.", null)]
    [InlineData("A person 30 to 40 years old.", null)]
    [InlineData("A person age 30 years old or 40.", null)]
    [InlineData("Not a 30-year-old or a 40-year-old.", null)]
    [InlineData("A person under 40 years old.", null)]
    [InlineData("A person no more than 40 years old.", null)]
    [InlineData("Age 30, alternatively 42 years old.", null)]
    [InlineData("A person aged 30.5 years old.", null)]
    [InlineData("A woman of age 30?", null)]
    [InlineData("A young adult with green eyes.", 22)]
    [InlineData("A young adult or middle-aged subject.", null)]
    [InlineData("A 32-year-old, or an elderly man.", null)]
    public async Task BuiltInAnalyzerSuggestsAgeOnlyFromEligibleUnambiguousWording(string description, int? expected)
    {
        var result = await new RuleBasedAppearanceIntentAnalyzer().AnalyzeAsync(
            new AppearanceAnalysisRequest(description, string.Empty), CancellationToken.None);

        Assert.Equal((float?)expected, result.Age);
        Assert.Equal(description, result.SourceSummary);
    }

    [Theory]
    [InlineData("A human from a subnordic suburb.", "unspecified", "unspecified")]
    [InlineData("A Vlandian woman or an Aserai man.", "unspecified", "unspecified")]
    [InlineData("A Vlandian woman with female facial features.", "female", "vlandia")]
    [InlineData("A Sturgian man with short hair.", "male", "sturgia")]
    [InlineData("Not a woman, and never an Aserai.", "unspecified", "unspecified")]
    [InlineData("Not a woman but a man. Sturgian rather than Vlandian.", "male", "sturgia")]
    public async Task BuiltInAnalyzerRecognizesOnlyUnambiguousIdentityWords(
        string description, string sex, string culture)
    {
        var result = await new RuleBasedAppearanceIntentAnalyzer().AnalyzeAsync(
            new AppearanceAnalysisRequest(description, string.Empty), CancellationToken.None);

        Assert.Equal(sex, result.Sex);
        Assert.Equal(culture, result.Culture);
    }

    [Fact]
    public async Task BuiltInAnalyzerKeepsDescriptorsAttachedToTheirFeatures()
    {
        var analyzer = new RuleBasedAppearanceIntentAnalyzer();

        var result = await analyzer.AnalyzeAsync(
            new AppearanceAnalysisRequest(
                "A 34-year-old Vlandian woman with fair weathered skin, long wavy auburn hair, " +
                "green almond-shaped eyes, high cheekbones, dark arched eyebrows, a narrow aquiline nose, " +
                "full lips, a strong angular jaw, a pointed chin, a scar through her eyebrow, and an athletic build.",
                string.Empty),
            CancellationToken.None);

        Assert.Equal("fair", result.Complexion);
        Assert.Equal("auburn", result.HairColor);
        Assert.Equal("long", result.HairLength);
        Assert.Equal("wavy", result.HairTexture);
        Assert.Equal("green", result.EyeColor);
        Assert.Contains("almond", result.EyeShape, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("arched", result.BrowShape);
        Assert.Equal("unspecified", result.FaceShape);
        Assert.Equal("aquiline nose", result.NoseShape);
        Assert.Equal("angular jaw", result.JawShape);
        Assert.Equal(0.82f, result.Build);
        Assert.Contains("scar", result.DistinguishingMarks);
        Assert.Contains("weathered", result.DistinguishingMarks);
    }

    [Theory]
    [InlineData("A woman with dark eyebrows and fair hair.")]
    [InlineData("A man with brown eyes and a pale tunic.")]
    public async Task BuiltInAnalyzerDoesNotInferComplexionFromOtherFeatures(string description)
    {
        var analyzer = new RuleBasedAppearanceIntentAnalyzer();
        var result = await analyzer.AnalyzeAsync(
            new AppearanceAnalysisRequest(description, string.Empty),
            CancellationToken.None);

        Assert.Equal("unspecified", result.Complexion);
    }

    [Theory]
    [InlineData("porcelain skin", "porcelain")]
    [InlineData("very fair complexion", "very fair")]
    [InlineData("light olive skin tone", "light olive")]
    [InlineData("tanned skin", "tanned")]
    [InlineData("medium complexion", "medium")]
    [InlineData("deep brown complexion", "deep")]
    [InlineData("dark brown skin", "dark")]
    public async Task BuiltInAnalyzerRecognizesCalibratedComplexionVocabulary(
        string phrase,
        string expected)
    {
        var analyzer = new RuleBasedAppearanceIntentAnalyzer();
        var result = await analyzer.AnalyzeAsync(
            new AppearanceAnalysisRequest($"A 30-year-old man with {phrase}.", string.Empty),
            CancellationToken.None);

        Assert.Equal(expected, result.Complexion);
    }

    [Fact]
    public void ParserAcceptsPartialFencedOutputAndClampsStrengths()
    {
        var result = AppearanceIntentParser.Parse(
            "model notes\n```json\n{\"sex\":\"MALE\",\"age\":\"44\",\"eyeSize\":4.2," +
            "\"confidence\":{\"eyeSize\":-2},\"distinguishingMarks\":\"scar\"}\n```");

        Assert.Equal("male", result.Sex);
        Assert.Equal(44f, result.Age);
        Assert.Equal(1f, result.EyeSize);
        Assert.Equal(0f, result.Confidence["eyeSize"]);
        Assert.Null(result.OverallConfidence);
        Assert.Equal(["scar"], result.DistinguishingMarks);
        Assert.Equal("unspecified", result.Culture);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{broken}")]
    public void ParserRejectsMalformedModelOutput(string output)
    {
        Assert.ThrowsAny<Exception>(() => AppearanceIntentParser.Parse(output));
    }

    [Fact]
    public async Task ProviderFailureFallsBackOnlyWhenDescriptionExists()
    {
        var service = new AppearanceIntentExtractionService(
            new RuleBasedAppearanceIntentAnalyzer(),
            new ThrowingAnalyzer());

        var fallback = await service.ExtractAsync(
            new AppearanceAnalysisRequest("A tall middle-aged Aserai man with a beard.", "portrait.png"),
            CancellationToken.None);
        Assert.True(fallback.UsedDescription);
        Assert.False(fallback.UsedReferenceImage);
        Assert.Contains("description only", fallback.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("editable appearance suggestions", fallback.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fallback.Intent.OverallConfidence);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExtractAsync(
            new AppearanceAnalysisRequest(string.Empty, "portrait.png"),
            CancellationToken.None));
    }

    [Fact]
    public async Task UnconfiguredVisionKeepsEditableTextAndRejectsImageOnlyInput()
    {
        var service = new AppearanceIntentExtractionService(new RuleBasedAppearanceIntentAnalyzer(), null);
        var result = await service.ExtractAsync(
            new AppearanceAnalysisRequest("Age 30. Green eyes.", "fixture-not-read.png"),
            CancellationToken.None);

        Assert.True(result.UsedDescription);
        Assert.False(result.UsedReferenceImage);
        Assert.Contains("reference image was not analyzed", result.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("editable appearance suggestions", result.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(30f, result.Intent.Age);
        Assert.Equal("green", result.Intent.EyeColor);
        Assert.Empty(result.Intent.Confidence);
        Assert.Null(result.Intent.OverallConfidence);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Intent, AppearanceLabContract.JsonOptions);
        Assert.DoesNotContain("\"overallConfidence\"", json, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExtractAsync(
            new AppearanceAnalysisRequest(string.Empty, "fixture-not-read.png"), CancellationToken.None));
    }

    [Fact]
    public async Task StructuredIntentBypassesTheHelperLlm()
    {
        var service = new AppearanceIntentExtractionService(
            new RuleBasedAppearanceIntentAnalyzer(),
            new ThrowingAnalyzer());
        var json = "{\"schemaVersion\":\"bannerlord_appearance_intent_v1\",\"sex\":\"female\"," +
            "\"culture\":\"vlandia\",\"age\":33,\"faceShape\":\"angular\",\"overallConfidence\":0.95}";

        var result = await service.ExtractAsync(
            new AppearanceAnalysisRequest(json, string.Empty),
            CancellationToken.None);

        Assert.Equal("Structured appearance-intent contract", result.Analyzer);
        Assert.Equal("angular", result.Intent.FaceShape);
        Assert.Equal(0.95f, result.Intent.OverallConfidence);
    }

    private sealed class ThrowingAnalyzer : IAppearanceIntentAnalyzer
    {
        public string Name => "Unavailable vision";
        public bool CanAnalyzeImages => true;

        public Task<AppearanceIntentV1> AnalyzeAsync(
            AppearanceAnalysisRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Provider unavailable");
    }
}
