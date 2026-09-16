using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string OpenRouterImagesUrl = "https://openrouter.ai/api/v1/images";
        private const string DefaultOpenRouterImageModel = "openai/gpt-image-1";
        // Exact overlapping models from GET /api/v1/images/models, verified 2026-09-09.
        private static readonly string[] OpenRouterImageModels = { DefaultOpenRouterImageModel,
            "google/gemini-3-pro-image-preview", "bytedance-seed/seedream-4.5",
            "bytedance-seed/seedream-5-0-pro", "x-ai/grok-imagine-image-quality" };

        private static string ImageProfileModel(ImageGenerationProfile profile)
        {
            return profile.Provider == "OpenRouter" ? profile.OpenRouterModel
                : IsCodexImageProvider(profile.Provider) ? CodexImageModel
                : IsAtlasProvider(profile.Provider) ? profile.AtlasModel : profile.NanoGptModel;
        }

        private static Dictionary<string, object> BuildOpenRouterImagePayload(string model, string prompt, byte[] source, string size)
        {
            if (!OpenRouterImageModels.Contains(model)) throw new ArgumentException("Select a supported OpenRouter image model.");
            if (source == null || source.Length == 0) throw new ArgumentException("OpenRouter image edits require the source image.");
            string[] dimensions = (size ?? "").Replace('*', 'x').Split('x');
            int width = 0, height = 0;
            if (dimensions.Length == 2) { int.TryParse(dimensions[0], out width); int.TryParse(dimensions[1], out height); }
            if (width <= 0 || height <= 0) { width = 768; height = 1024; }
            bool gpt = model == DefaultOpenRouterImageModel;
            // GPT Image 1 accepts 1:1 / 2:3 / 3:2; other retained models also support the scene/portrait ratios.
            string ratio = width == height ? "1:1" : width > height ? (gpt ? "3:2" : "16:9") : (gpt ? "2:3" : "3:4");
            var body = new Dictionary<string, object> {
                ["model"] = model, ["prompt"] = prompt, ["n"] = 1, ["stream"] = false,
                ["aspect_ratio"] = ratio,
                ["input_references"] = new ArrayList { new Dictionary<string, object> {
                    ["type"] = "image_url", ["image_url"] = new Dictionary<string, object> {
                        ["url"] = "data:image/png;base64," + Convert.ToBase64String(source) } } }
            };
            if (gpt) body["quality"] = "high";
            else body["resolution"] = "2K";
            return body;
        }

        private static PortraitImageCallResult GenerateOpenRouterImage(Dictionary<string, object> settings,
            ImageGenerationProfile profile, string prompt, byte[] source, string size,
            Func<string, string, string, Dictionary<string, object>, string> send = null)
        {
            string key = ReadString(settings, "openRouterApiKey", "");
            if (string.IsNullOrWhiteSpace(key)) return PortraitImageCallResult.Fail("OpenRouter", profile.OpenRouterModel,
                "No OpenRouter key is saved. Add it in API Settings; chat and images share that key.");
            var timer = Stopwatch.StartNew();
            int requestChars = 0;
            try
            {
                string json = Json.Serialize(BuildOpenRouterImagePayload(profile.OpenRouterModel, prompt, source, size));
                requestChars = json.Length;
                string response = (send ?? PostJsonToLlm)(OpenRouterImagesUrl, key, json, settings);
                var raw = Json.Deserialize<Dictionary<string, object>>(response);
                var data = ReadDictionaryList(raw, "data");
                byte[] bytes = data.Count == 0 ? null : DecodeImageBase64(ReadString(data[0], "b64_json", ""));
                if (bytes == null || bytes.Length == 0)
                    return PortraitImageCallResult.Fail("OpenRouter", profile.OpenRouterModel,
                        "OpenRouter returned no image data; the reference image will not be used as a generated result.", timer.ElapsedMilliseconds, requestChars, response.Length);
                return PortraitImageCallResult.Success("OpenRouter", profile.OpenRouterModel, "OpenRouter reference image",
                    bytes, timer.ElapsedMilliseconds, requestChars, response.Length);
            }
            catch (Exception ex) { return PortraitImageCallResult.Fail("OpenRouter", profile.OpenRouterModel,
                "OpenRouter image request failed: " + ex.Message, timer.ElapsedMilliseconds, requestChars, 0); }
        }
    }
}
