using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bannerlord.NativeCharacterImageGenerator;

internal sealed partial class RuleBasedAppearanceIntentAnalyzer : IAppearanceIntentAnalyzer
{
    private static readonly string[] Cultures =
        ["aserai", "battania", "empire", "khuzait", "nord", "sturgia", "vlandia"];

    public string Name => "Built-in description analyzer";
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
        var confidence = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        string Pick(string field, params string[] values)
        {
            var match = values.FirstOrDefault(value => lower.Contains(value, StringComparison.Ordinal));
            if (match is not null)
            {
                confidence[field] = 0.72f;
                return match;
            }
            return "unspecified";
        }

        string PickNear(string field, string nounPattern, params string[] values)
        {
            var match = values.FirstOrDefault(value => IsNearFeature(lower, value, nounPattern));
            if (match is not null)
            {
                confidence[field] = 0.82f;
                return match;
            }
            return "unspecified";
        }

        var age = ReadAge(lower);
        if (age.HasValue)
        {
            confidence["age"] = 0.9f;
        }

        var sex = lower.Contains("woman", StringComparison.Ordinal)
            || lower.Contains("female", StringComparison.Ordinal)
            || lower.Contains(" lady", StringComparison.Ordinal)
            ? "female"
            : lower.Contains("man", StringComparison.Ordinal)
                || lower.Contains("male", StringComparison.Ordinal)
                || lower.Contains(" gentleman", StringComparison.Ordinal)
                ? "male"
                : "unspecified";
        if (sex != "unspecified")
        {
            confidence["sex"] = 0.92f;
        }

        var culture = Cultures.FirstOrDefault(value => lower.Contains(value, StringComparison.Ordinal))
            ?? "unspecified";
        if (culture != "unspecified")
        {
            confidence["culture"] = 0.9f;
        }

        var hairLength = PickNear("hairLength", "hair", "bald", "shaved", "cropped", "short", "shoulder-length", "long");
        if (hairLength == "unspecified" && (ContainsWord(lower, "bald") || ContainsWord(lower, "shaved")))
        {
            hairLength = ContainsWord(lower, "bald") ? "bald" : "shaved";
            confidence["hairLength"] = 0.82f;
        }
        var facialHair = Pick("facialHair", "clean-shaven", "clean shaven", "stubble", "mustache", "moustache", "goatee", "full beard", "beard");
        var faceShape = PickNear("faceShape", "face", "round", "oval", "square", "heart-shaped", "narrow", "long", "angular");
        var eyeShape = PickNear("eyeShape", "eyes?", "almond-shaped", "almond", "hooded", "deep-set", "deep set", "round", "upturned", "downturned");
        var marks = new[] { "scar", "freckles", "mole", "birthmark", "weathered", "wrinkles" }
            .Where(value => lower.Contains(value, StringComparison.Ordinal))
            .ToArray();
        if (marks.Length > 0)
        {
            confidence["distinguishingMarks"] = 0.75f;
        }

        var intent = new AppearanceIntentV1
        {
            Sex = sex,
            Age = age,
            Culture = culture,
            Complexion = PickNear("complexion", "(?:skin|complexion|skin tone)", "very fair", "light olive", "fair", "pale", "porcelain", "alabaster", "olive", "tanned", "tan", "medium", "deep", "dark", "brown"),
            HairColor = PickNear("hairColor", "hair", "black", "dark brown", "brown", "auburn", "red", "ginger", "strawberry blond", "strawberry blonde", "blond", "blonde", "platinum", "gray", "grey", "white"),
            HairLength = hairLength,
            HairTexture = PickNear("hairTexture", "hair", "straight", "wavy", "curly", "coarse"),
            FacialHair = facialHair,
            FaceShape = faceShape,
            CheekboneProminence = Scale(lower, "high cheekbones", "prominent cheekbones", "soft cheekbones"),
            EyeColor = PickNear("eyeColor", "eyes?", "blue", "green", "gray", "grey", "hazel", "brown", "dark"),
            EyeSize = Scale(lower, "large eyes", "big eyes", "small eyes"),
            EyeSpacing = Scale(lower, "wide-set eyes", "wide set eyes", "close-set eyes", "close set eyes"),
            EyeShape = eyeShape,
            BrowShape = PickNear("browShape", "(?:brows?|eyebrows?)", "thick", "thin", "arched", "straight", "heavy"),
            NoseLength = Scale(lower, "long nose", "prominent nose", "short nose"),
            NoseWidth = Scale(lower, "broad nose", "wide nose", "narrow nose"),
            NoseShape = Pick("noseShape", "hooked nose", "aquiline nose", "straight nose", "button nose", "crooked nose", "upturned nose"),
            MouthWidth = Scale(lower, "wide mouth", "broad mouth", "small mouth", "narrow mouth"),
            LipFullness = Scale(lower, "full lips", "thick lips", "thin lips"),
            JawShape = Pick("jawShape", "square jaw", "strong jaw", "broad jaw", "narrow jaw", "soft jaw", "angular jaw"),
            ChinShape = Pick("chinShape", "strong chin", "prominent chin", "pointed chin", "round chin", "receding chin"),
            Weight = ScaleAny(lower, ["heavyset", "stocky", "plump"], ["thin", "slender", "lean"]),
            Build = ScaleAny(lower, ["muscular", "athletic", "broad-shouldered", "broad shouldered", "powerful build"], ["slight build", "slender", "frail"]),
            Height = Scale(lower, "very tall", "tall", "short", "small stature"),
            DistinguishingMarks = marks,
            Confidence = confidence,
            OverallConfidence = Math.Clamp(0.35f + confidence.Count * 0.035f, 0.35f, 0.82f),
            SourceSummary = text
        };
        return Task.FromResult(intent);
    }

    private static float? ReadAge(string text)
    {
        var match = AgeRegex().Match(text);
        if (match.Success
            && float.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var age))
        {
            return age;
        }
        if (text.Contains("young adult", StringComparison.Ordinal)) return 22f;
        if (text.Contains("middle-aged", StringComparison.Ordinal) || text.Contains("middle aged", StringComparison.Ordinal)) return 45f;
        if (text.Contains("elderly", StringComparison.Ordinal) || text.Contains("old woman", StringComparison.Ordinal) || text.Contains("old man", StringComparison.Ordinal)) return 68f;
        return null;
    }

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

    [GeneratedRegex(@"\b(?:age(?:d)?\s*)?(\d{1,2})(?:\s*years?\s*old)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex AgeRegex();
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
                    "Provider analysis was unavailable; candidates use the written description only.");
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
            hasImage ? "Vision is unavailable; the reference image was not analyzed." : string.Empty);
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
            OverallConfidence = Unit(root, "overallConfidence") ?? 0.5f,
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
