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
        Assert.True(result.OverallConfidence > 0.5f);
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExtractAsync(
            new AppearanceAnalysisRequest(string.Empty, "portrait.png"),
            CancellationToken.None));
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
