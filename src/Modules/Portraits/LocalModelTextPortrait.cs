using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class PortraitsService
    {
        private const string LocalTextPortraitMethod = "local_text";
        private const string LocalTextPortraitAdapter = "local_model_text_portrait";
        private const string LocalTextPortraitVisionModel = "qwen3.5-9b-vision";
        private const string LocalTextVisionPlaceholderPrefix = "{{REIGN_VISION_";
        private const int LocalTextPortraitTimeoutSeconds = 1200;
        private static readonly Uri LocalTextPortraitEndpoint = new Uri("http://127.0.0.1:18500/v1/portraits/generations");

        private sealed record LocalTextPortraitResult(PortraitImageCallResult Normal,
            Dictionary<string, object> Vision = null, string EffectivePrompt = "", long ImageMs = 0);

        private static string LocalTextVisionPlaceholder(string field) => LocalTextVisionPlaceholderPrefix + field + "}}";

        private static string BuildLocalTextPortraitTemplate(Dictionary<string, object> payload, string clientPrompt)
        {
            var descriptions = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string field in PortraitVisionFields) descriptions.Add(field, LocalTextVisionPlaceholder(field));
            var templatePayload = new Dictionary<string, object>(payload);
            templatePayload["resolvedPortraitVision"] = TestDict("description", descriptions);
            string tavernClothing = IsTavernHousePortrait(templatePayload)
                ? ExpandPortraitIdentityTokens(templatePayload, PromptService.LoadPromptTemplate("tavern_house_portrait_clothing.txt").Trim())
                    .Replace("[TAVERN ROLE]", TavernHousePortraitRole(templatePayload))
                : null;
            string template = ApplyPortraitProductPromptContract(BuildPortraitPrompt(
                templatePayload, clientPrompt, residentClothingGuidance: tavernClothing));
            string remaining = template;
            foreach (string field in PortraitVisionFields)
            {
                if (field == "outfit" && !PreserveResidentClothing(payload)) continue;
                string placeholder = LocalTextVisionPlaceholder(field);
                int index = remaining.IndexOf(placeholder, StringComparison.Ordinal);
                if (index < 0) throw new InvalidDataException("The local portrait prompt omitted an expected vision field.");
                remaining = remaining.Remove(index, placeholder.Length);
            }
            if (remaining.Contains(LocalTextVisionPlaceholderPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("The local portrait prompt contains a duplicate or unsupported vision placeholder.");
            if (template.Length > LocalModelMaxPromptChars)
                throw new InvalidDataException("The local portrait prompt template exceeds 16,000 characters.");
            return template;
        }

        private static HttpRequestMessage BuildLocalTextPortraitRequest(ImageGenerationProfile normalProfile,
            byte[] source, string template)
        {
            if (source == null || source.Length > LocalModelMaxReferenceBytes || !HasLocalModelPngSignature(source))
                throw new InvalidDataException("Local Text Based portraits require a native PNG source no larger than 8 MiB.");
            ValidateCodexPng(source);
            var body = new Dictionary<string, object>
            {
                ["imageDataUrl"] = "data:image/png;base64," + Convert.ToBase64String(source),
                ["vision_prompt"] = PortraitVisionInstructions,
                ["prompt_template"] = template,
                ["image_settings"] = BuildLocalModelImageSettings(normalProfile)
            };
            return new HttpRequestMessage(HttpMethod.Post, LocalTextPortraitEndpoint)
            {
                Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json")
            };
        }

        private static LocalTextPortraitResult ReadLocalTextPortraitResponse(string json, string template, byte[] source,
            long duration, int requestChars)
        {
            var response = Json.Deserialize<Dictionary<string, object>>(json);
            if (ReadString(response, "model", "") != DefaultLocalModelImageModel
                || ReadString(response, "vision_model", "") != LocalTextPortraitVisionModel)
                throw new InvalidDataException("Local Model Desk returned unexpected portrait models.");
            var description = ParsePortraitVision(Json.Serialize(ReadDictionary(response, "description")));
            string expectedPrompt = template;
            foreach (string field in PortraitVisionFields)
            {
                string value = (string)description[field];
                if (value.Contains(LocalTextVisionPlaceholderPrefix, StringComparison.Ordinal))
                    throw new InvalidDataException("Local Model Desk returned a reserved placeholder in its vision description.");
                expectedPrompt = expectedPrompt.Replace(LocalTextVisionPlaceholder(field), value, StringComparison.Ordinal);
            }
            string effectivePrompt = ReadString(response, "prompt", "");
            if (effectivePrompt.Length == 0 || effectivePrompt.Length > LocalModelMaxPromptChars || effectivePrompt != expectedPrompt)
                throw new InvalidDataException("Local Model Desk returned a portrait prompt that did not match the source-grounded template.");

            var timing = ReadDictionary(response, "timing");
            long visionMs = ReadLocalTextPortraitStageTime(timing, "vision_ms");
            var vision = TestDict("schema", "reign-portrait-vision-v1", "provider", LocalModelImageProviderId,
                "model", LocalTextPortraitVisionModel, "description", description, "sourceSha256", Sha256Hex(source),
                "durationMs", visionMs);
            if (ReadPathObject(response, "data") is not object[] { Length: 1 } images)
                throw new InvalidDataException("Local Model Desk must return exactly one portrait.");
            var normal = PortraitImageCallResult.Success(LocalModelImageProviderId, DefaultLocalModelImageModel,
                LocalTextPortraitAdapter, DecodeLocalModelPng(ReadPathObject(images[0], "b64_json") as string, false),
                duration, requestChars, json.Length);
            return new LocalTextPortraitResult(normal, vision, effectivePrompt, ReadLocalTextPortraitStageTime(timing, "image_ms"));
        }

        private static long ReadLocalTextPortraitStageTime(Dictionary<string, object> timing, string key) =>
            Math.Clamp(ReadLong(timing, key, 0), 0, LocalTextPortraitTimeoutSeconds * 1000L);

        private static LocalTextPortraitResult GenerateLocalTextPortrait(ImageGenerationProfile normalProfile,
            byte[] source, string template)
        {
            var timer = Stopwatch.StartNew();
            int requestChars = 0, responseChars = 0;
            try
            {
                using var request = BuildLocalTextPortraitRequest(normalProfile, source, template);
                requestChars = (int)(request.Content.Headers.ContentLength ?? 0);
                Program.ProviderTestGuard.BeforeDispatch("local_model_image");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(LocalTextPortraitTimeoutSeconds));
                using var response = LocalModelImageHttpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(response.StatusCode == HttpStatusCode.ServiceUnavailable
                        ? "Local Model Desk could not admit this portrait. Check that Qwen Vision and Qwen Image are enabled and available."
                        : "Local Model Desk rejected the local portrait request (HTTP " + (int)response.StatusCode + ").");
                string json = ReadBoundedLocalModelResponse(response, timeout.Token, LocalModelMaxResponseBytes);
                responseChars = json.Length;
                return ReadLocalTextPortraitResponse(json, template, source, timer.ElapsedMilliseconds, requestChars);
            }
            catch (Exception ex)
            {
                string error = ex is OperationCanceledException ? "Local Model Desk did not finish within the 20-minute portrait request window."
                    : ex is HttpRequestException ? "Could not connect to Local Model Desk over the ReignServer loopback."
                    : ex is FormatException or System.Text.Json.JsonException ? "Local Model Desk returned malformed portrait data."
                    : ex.Message;
                var failure = PortraitImageCallResult.Fail(LocalModelImageProviderId, DefaultLocalModelImageModel, error,
                    timer.ElapsedMilliseconds, requestChars, responseChars);
                failure.Adapter = LocalTextPortraitAdapter;
                return new LocalTextPortraitResult(failure);
            }
        }
    }
}
