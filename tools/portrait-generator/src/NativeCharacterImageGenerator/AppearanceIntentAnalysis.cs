using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bannerlord.NativeCharacterImageGenerator;

internal sealed partial class RuleBasedAppearanceIntentAnalyzer : IAppearanceIntentAnalyzer
{
    internal const string SuggestionNotice =
        "Written descriptions provide editable appearance suggestions without a confidence score.";

    public string Name => "Built-in text suggestions";
    public bool CanAnalyzeImages => false;

    public Task<AppearanceIntentV1> AnalyzeAsync(
        AppearanceAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Description))
        {
            throw new InvalidOperationException(
                "A written physical description is required because no vision analyzer is configured.");
        }

        var text = request.Description.Trim();
        var lower = text.ToLowerInvariant();
        string Pick(params string[] values) =>
            values.FirstOrDefault(value => lower.Contains(value, StringComparison.Ordinal))
            ?? "unspecified";
        string PickNear(string nounPattern, params string[] values) =>
            values.FirstOrDefault(value => IsNearFeature(lower, value, nounPattern))
            ?? "unspecified";

        var hairLength = PickNear("hair", "bald", "shaved", "cropped", "short", "shoulder-length", "long");
        if (hairLength == "unspecified" && (ContainsWord(lower, "bald") || ContainsWord(lower, "shaved")))
        {
            hairLength = ContainsWord(lower, "bald") ? "bald" : "shaved";
        }
        var sexHints = PositiveHints(SexHintRegex(), lower)
            .Select(match => match.Groups["female"].Success ? "female" : "male");
        var cultureHints = PositiveHints(CultureHintRegex(), lower)
            .Select(match => match.Groups["culture"].Value);
        var marks = new[] { "scar", "freckles", "mole", "birthmark", "weathered", "wrinkles" }
            .Where(value => lower.Contains(value, StringComparison.Ordinal))
            .ToArray();

        var intent = new AppearanceIntentV1
        {
            Sex = SingleHint(sexHints),
            Age = ReadAge(lower),
            Culture = SingleHint(cultureHints),
            Complexion = PickNear("(?:skin|complexion|skin tone)", "very fair", "light olive", "fair", "pale", "porcelain", "alabaster", "olive", "tanned", "tan", "medium", "deep", "dark", "brown"),
            HairColor = PickNear("hair", "black", "dark brown", "brown", "auburn", "red", "ginger", "strawberry blond", "strawberry blonde", "blond", "blonde", "platinum", "gray", "grey", "white"),
            HairLength = hairLength,
            HairTexture = PickNear("hair", "straight", "wavy", "curly", "coarse"),
            FacialHair = Pick("clean-shaven", "clean shaven", "stubble", "mustache", "moustache", "goatee", "full beard", "beard"),
            FaceShape = PickNear("face", "round", "oval", "square", "heart-shaped", "narrow", "long", "angular"),
            CheekboneProminence = Scale(lower, "high cheekbones", "prominent cheekbones", "soft cheekbones"),
            EyeColor = PickNear("eyes?", "blue", "green", "gray", "grey", "hazel", "brown", "dark"),
            EyeSize = Scale(lower, "large eyes", "big eyes", "small eyes"),
            EyeSpacing = Scale(lower, "wide-set eyes", "wide set eyes", "close-set eyes", "close set eyes"),
            EyeShape = PickNear("eyes?", "almond-shaped", "almond", "hooded", "deep-set", "deep set", "round", "upturned", "downturned"),
            BrowShape = PickNear("(?:brows?|eyebrows?)", "thick", "thin", "arched", "straight", "heavy"),
            NoseLength = Scale(lower, "long nose", "prominent nose", "short nose"),
            NoseWidth = Scale(lower, "broad nose", "wide nose", "narrow nose"),
            NoseShape = Pick("hooked nose", "aquiline nose", "straight nose", "button nose", "crooked nose", "upturned nose"),
            MouthWidth = Scale(lower, "wide mouth", "broad mouth", "small mouth", "narrow mouth"),
            LipFullness = Scale(lower, "full lips", "thick lips", "thin lips"),
            JawShape = Pick("square jaw", "strong jaw", "broad jaw", "narrow jaw", "soft jaw", "angular jaw"),
            ChinShape = Pick("strong chin", "prominent chin", "pointed chin", "round chin", "receding chin"),
            Weight = ScaleAny(lower, ["heavyset", "stocky", "plump"], ["thin", "slender", "lean"]),
            Build = ScaleAny(lower, ["muscular", "athletic", "broad-shouldered", "broad shouldered", "powerful build"], ["slight build", "slender", "frail"]),
            Height = Scale(lower, "very tall", "tall", "short", "small stature"),
            DistinguishingMarks = marks,
            SourceSummary = text
        };
        return Task.FromResult(intent);
    }

    private static float? ReadAge(string text)
    {
        var explicitAges = new HashSet<float>();
        var categoryAges = new HashSet<float>();
        foreach (Match match in AgeHintRegex().Matches(text))
        {
            var prefix = text.AsSpan(0, match.Index);
            if (AmbiguousAgePrefixRegex().IsMatch(prefix)
                || AmbiguousAgeSuffixRegex().IsMatch(text.AsSpan(match.Index + match.Length)))
            {
                return null;
            }
            if (NegatedHintPrefixRegex().IsMatch(prefix))
            {
                continue;
            }

            var ageGroup = match.Groups["age"];
            if (ageGroup.Success)
            {
                var age = int.Parse(ageGroup.Value, CultureInfo.InvariantCulture);
                if (age is < 1 or > 125) return null;
                explicitAges.Add(age);
            }
            else
            {
                categoryAges.Add(match.Groups["young"].Success ? 22f
                    : match.Groups["middle"].Success ? 45f : 68f);
            }
        }
        var ages = explicitAges.Count > 0 ? explicitAges : categoryAges;
        return ages.Count == 1 ? ages.Single() : null;
    }

    private static string SingleHint(IEnumerable<string> hints)
    {
        var choices = hints.Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return choices.Length == 1 ? choices[0] : "unspecified";
    }

    private static IEnumerable<Match> PositiveHints(Regex pattern, string text) =>
        pattern.Matches(text).Where(match =>
            !NegatedHintPrefixRegex().IsMatch(text.AsSpan(0, match.Index)));

    private static float? Scale(string text, string high, string alternateHigh, string low) =>
        text.Contains(high, StringComparison.Ordinal) || text.Contains(alternateHigh, StringComparison.Ordinal)
            ? 0.82f
            : text.Contains(low, StringComparison.Ordinal) ? 0.18f : null;

    private static float? Scale(string text, string high, string low) =>
        text.Contains(high, StringComparison.Ordinal) ? 0.82f : text.Contains(low, StringComparison.Ordinal) ? 0.18f : null;

    private static float? Scale(string text, string high, string alternateHigh, string low, string alternateLow) =>
        text.Contains(high, StringComparison.Ordinal) || text.Contains(alternateHigh, StringComparison.Ordinal)
            ? 0.82f
            : text.Contains(low, StringComparison.Ordinal) || text.Contains(alternateLow, StringComparison.Ordinal) ? 0.18f : null;

    private static float? ScaleAny(string text, string[] high, string[] low) =>
        high.Any(value => text.Contains(value, StringComparison.Ordinal))
            ? 0.82f
            : low.Any(value => text.Contains(value, StringComparison.Ordinal)) ? 0.18f : null;

    private static bool IsNearFeature(string text, string descriptor, string nounPattern)
    {
        var words = string.Join(@"[\s-]+", descriptor.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Regex.Escape));
        var between = @"(?:[\s-]+\w+){0,3}[\s-]+";
        return Regex.IsMatch(text, $@"\b{words}\b{between}(?:{nounPattern})\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(text, $@"\b(?:{nounPattern})\b{between}{words}\b", RegexOptions.IgnoreCase);
    }

    private static bool ContainsWord(string text, string value) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(value)}\b", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"\b(?:(?:age\s+is\s+|aged?\s*[:=]?\s*)(?<age>[0-9]{1,3})(?![.,][0-9])(?:[\s-]+years?[\s-]+old)?|(?<![0-9][.,])(?<age>[0-9]{1,3})[\s-]+years?[\s-]+old|(?<young>young[\s-]+adult)|(?<middle>middle[\s-]+aged)|elderly|old[\s-]+(?:woman|man))\b")]
    private static partial Regex AgeHintRegex();

    [GeneratedRegex(@"\b(?:not|never|no|isn['’]t|wasn['’]t|aren['’]t|rather than|instead of)(?:\s+(?:an?|the|actually|quite|really|yet|exactly)){0,3}\s+$", RegexOptions.RightToLeft)]
    private static partial Regex NegatedHintPrefixRegex();

    [GeneratedRegex(@"(?:\b(?:under|over|at least|at most|(?:younger|older|more|less) than)\s+|\b[0-9]+\s*(?:[-–—/]|\b(?:to|or|and)\b)\s*)$", RegexOptions.RightToLeft)]
    private static partial Regex AmbiguousAgePrefixRegex();

    [GeneratedRegex(@"^\s*(?:\?|,?\s*(?:[-–—/]|\b(?:to|or|and)\b)\s*(?:an?\s+)?(?:[0-9]|young[\s-]+adult|middle[\s-]+aged|elderly|old[\s-]+(?:woman|man)))")]
    private static partial Regex AmbiguousAgeSuffixRegex();

    [GeneratedRegex(@"\b(?:(?<female>wom[ae]n|female|lady)|m[ae]n|male|gentleman)\b")]
    private static partial Regex SexHintRegex();

    [GeneratedRegex(@"\b(?:(?<culture>aserai|empire|khuzait)|(?<culture>battania|sturgia|vlandia)n?|(?<culture>nord)(?:ic)?)\b")]
    private static partial Regex CultureHintRegex();
}

internal sealed record AppearanceProviderSettings(string Endpoint, string Model, string ApiKey)
{
    public bool IsConfigured => Uri.TryCreate(Endpoint, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(Model)
        && !string.IsNullOrWhiteSpace(ApiKey);

    public static AppearanceProviderSettings FromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable("NATIVE_CHARACTER_AI_API_KEY")
            ?? Environment.GetEnvironmentVariable("NANO_GPT_API_KEY")
            ?? string.Empty;
        return new AppearanceProviderSettings(
            Environment.GetEnvironmentVariable("NATIVE_CHARACTER_AI_ENDPOINT")
                ?? "https://nano-gpt.com/api/v1/chat/completions",
            Environment.GetEnvironmentVariable("NATIVE_CHARACTER_AI_MODEL") ?? "gpt-4o-mini",
            key);
    }
}

internal sealed class OpenAiCompatibleAppearanceIntentAnalyzer(
    AppearanceProviderSettings settings,
    HttpClient? client = null) : IAppearanceIntentAnalyzer
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

    public string Name => $"OpenAI-compatible provider ({settings.Model})";
    public bool CanAnalyzeImages => settings.IsConfigured;

    public async Task<AppearanceIntentV1> AnalyzeAsync(
        AppearanceAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(
                "The optional appearance provider is not configured. Set NATIVE_CHARACTER_AI_API_KEY, " +
                "NATIVE_CHARACTER_AI_MODEL, and optionally NATIVE_CHARACTER_AI_ENDPOINT.");
        }

        var content = new List<object>();
        var prompt = "Extract visible or described physical traits into one JSON object. " +
            "Use schemaVersion bannerlord_appearance_intent_v1 and only these camelCase fields: " +
            "sex, age, culture, complexion, hairColor, hairLength, hairTexture, facialHair, faceShape, " +
            "cheekboneProminence, eyeColor, eyeSize, eyeSpacing, eyeShape, browShape, noseLength, noseWidth, " +
            "noseShape, mouthWidth, lipFullness, jawShape, chinShape, weight, build, height, " +
            "distinguishingMarks, confidence, overallConfidence. Numeric feature strengths are 0..1. " +
            "Use these calibrated labels when applicable: complexion = pale, very fair, fair, light olive, " +
            "olive, tanned, medium, brown, dark, or deep; hairColor = white, gray, platinum, blonde, " +
            "strawberry blonde, red, ginger, auburn, brown, dark brown, or black. " +
            "Keep weathered or wrinkles in distinguishingMarks instead of changing complexion. " +
            "Use unspecified or null when uncertain. Do not output raw game sliders. Description: " +
            (string.IsNullOrWhiteSpace(request.Description) ? "(none)" : request.Description.Trim());
        content.Add(new { type = "text", text = prompt });
        if (!string.IsNullOrWhiteSpace(request.ReferenceImagePath))
        {
            var bytes = await File.ReadAllBytesAsync(request.ReferenceImagePath, cancellationToken).ConfigureAwait(false);
            var mime = Path.GetExtension(request.ReferenceImagePath).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg";
            content.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" }
            });
        }

        var body = JsonSerializer.Serialize(new
        {
            model = settings.Model,
            temperature = 0.1,
            response_format = new { type = "json_object" },
            messages = new[] { new { role = "user", content = content.ToArray() } }
        });
        using var message = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Appearance provider returned HTTP {(int)response.StatusCode}.");
        }

        using var envelope = JsonDocument.Parse(responseText);
        var modelText = envelope.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString();
        return AppearanceIntentParser.Parse(modelText ?? string.Empty);
    }
}

internal sealed class AppearanceIntentExtractionService(
    IAppearanceIntentAnalyzer fallback,
    IAppearanceIntentAnalyzer? provider)
{
    public async Task<AppearanceAnalysisResult> ExtractAsync(
        AppearanceAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        var hasDescription = !string.IsNullOrWhiteSpace(request.Description);
        var hasImage = !string.IsNullOrWhiteSpace(request.ReferenceImagePath);
        if (!hasDescription && !hasImage)
        {
            throw new InvalidOperationException("Enter a physical description or choose a reference image.");
        }

        if (hasDescription && AppearanceIntentParser.TryParse(request.Description, out var structuredIntent))
        {
            return new AppearanceAnalysisResult(
                structuredIntent,
                "Structured appearance-intent contract",
                true,
                false,
                hasImage
                    ? "The supplied intent was authoritative; the optional reference image was not reinterpreted."
                    : string.Empty);
        }

        if (provider is not null && (!hasImage || provider.CanAnalyzeImages))
        {
            try
            {
                var intent = await provider.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
                return new AppearanceAnalysisResult(intent, provider.Name, hasDescription, hasImage, string.Empty);
            }
            catch when (hasDescription)
            {
                var intent = await fallback.AnalyzeAsync(
                    new AppearanceAnalysisRequest(request.Description, string.Empty), cancellationToken).ConfigureAwait(false);
                return new AppearanceAnalysisResult(
                    intent,
                    fallback.Name,
                    true,
                    false,
                    "Provider analysis was unavailable; candidates use the written description only. " +
                    RuleBasedAppearanceIntentAnalyzer.SuggestionNotice);
            }
        }

        if (hasImage && !hasDescription)
        {
            throw new InvalidOperationException(
                "Image analysis is unavailable. Configure a vision-capable provider or add a written description.");
        }

        var fallbackIntent = await fallback.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
        return new AppearanceAnalysisResult(
            fallbackIntent,
            fallback.Name,
            true,
            false,
            hasImage ? "Vision is unavailable; the reference image was not analyzed. " +
                RuleBasedAppearanceIntentAnalyzer.SuggestionNotice
                : RuleBasedAppearanceIntentAnalyzer.SuggestionNotice);
    }
}

internal static class AppearanceIntentParser
{
    public static bool TryParse(string output, out AppearanceIntentV1 intent)
    {
        try
        {
            if (!output.Contains('{', StringComparison.Ordinal)
                || !output.Contains(AppearanceLabContract.IntentSchemaVersion, StringComparison.OrdinalIgnoreCase))
            {
                intent = new AppearanceIntentV1();
                return false;
            }
            intent = Parse(output);
            return true;
        }
        catch (JsonException)
        {
            intent = new AppearanceIntentV1();
            return false;
        }
        catch (InvalidDataException)
        {
            intent = new AppearanceIntentV1();
            return false;
        }
    }

    public static AppearanceIntentV1 Parse(string output)
    {
        var json = ExtractObject(output);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Appearance analysis did not return a JSON object.");
        }

        var confidence = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (TryProperty(root, "confidence", out var confidenceElement)
            && confidenceElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in confidenceElement.EnumerateObject())
            {
                if (TryFloat(property.Value, out var value)) confidence[property.Name] = Clamp01(value);
            }
        }

        return new AppearanceIntentV1
        {
            Sex = Text(root, "sex"),
            Age = Number(root, "age"),
            Culture = Text(root, "culture"),
            Complexion = Text(root, "complexion"),
            HairColor = Text(root, "hairColor"),
            HairLength = Text(root, "hairLength"),
            HairTexture = Text(root, "hairTexture"),
            FacialHair = Text(root, "facialHair"),
            FaceShape = Text(root, "faceShape"),
            CheekboneProminence = Unit(root, "cheekboneProminence"),
            EyeColor = Text(root, "eyeColor"),
            EyeSize = Unit(root, "eyeSize"),
            EyeSpacing = Unit(root, "eyeSpacing"),
            EyeShape = Text(root, "eyeShape"),
            BrowShape = Text(root, "browShape"),
            NoseLength = Unit(root, "noseLength"),
            NoseWidth = Unit(root, "noseWidth"),
            NoseShape = Text(root, "noseShape"),
            MouthWidth = Unit(root, "mouthWidth"),
            LipFullness = Unit(root, "lipFullness"),
            JawShape = Text(root, "jawShape"),
            ChinShape = Text(root, "chinShape"),
            Weight = Unit(root, "weight"),
            Build = Unit(root, "build"),
            Height = Unit(root, "height"),
            DistinguishingMarks = Strings(root, "distinguishingMarks"),
            Confidence = confidence,
            OverallConfidence = Unit(root, "overallConfidence"),
            SourceSummary = "Provider-extracted appearance intent"
        };
    }

    private static string ExtractObject(string value)
    {
        var start = value.IndexOf('{');
        var end = value.LastIndexOf('}');
        if (start < 0 || end < start) throw new InvalidDataException("Appearance analysis returned no JSON object.");
        return value[start..(end + 1)];
    }

    private static string Text(JsonElement root, string name)
    {
        if (!TryProperty(root, name, out var value)) return "unspecified";
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim().ToLowerInvariant()
            : "unspecified";
    }

    private static float? Number(JsonElement root, string name) =>
        TryProperty(root, name, out var value) && TryFloat(value, out var result) ? result : null;

    private static float? Unit(JsonElement root, string name) =>
        Number(root, name) is { } value ? Clamp01(value) : null;

    private static IReadOnlyList<string> Strings(JsonElement root, string name)
    {
        if (!TryProperty(root, name, out var value)) return [];
        if (value.ValueKind == JsonValueKind.String) return [value.GetString() ?? string.Empty];
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToArray()
            : [];
    }

    private static bool TryFloat(JsonElement value, out float result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out result)) return true;
        result = default;
        return value.ValueKind == JsonValueKind.String
            && float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryProperty(JsonElement root, string name, out JsonElement result)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                result = property.Value;
                return true;
            }
        }
        result = default;
        return false;
    }

    private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
}
