using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunOpenRouterImageProviderContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            Action<string, bool> check = (id, passed) => AddSharedPortraitSelfTest(rows, "openrouter_image_" + id, passed, id);
            var settings = new Dictionary<string, object> { ["openRouterApiKey"] = "fixture-router", ["apiKey"] = "fixture-other" };
            NormalizePortraitImageSettings(settings);
            byte[] source = { 1, 2, 3 }, generated = { 4, 5, 6 };
            foreach (string prefix in ImageProfilePrefixes)
            {
                settings[prefix + "Provider"] = "OpenRouter";
                foreach (string model in OpenRouterImageModels)
                {
                    settings[prefix + "OpenRouterImageModel"] = model;
                    var restored = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(settings));
                    NormalizePortraitImageSettings(restored);
                    var profile = ResolveImageGenerationProfile(restored, prefix);
                    bool wire = false;
                    var result = GenerateOpenRouterImage(restored, profile, "fixture edit", source, prefix.Contains("cenery") ? "2720x1530" : "768x1024",
                        (url, key, json, _) => {
                            var body = Json.Deserialize<Dictionary<string, object>>(json);
                            var reference = ReadDictionary(ReadDictionaryList(body, "input_references")[0], "image_url");
                            wire = url == OpenRouterImagesUrl && key == "fixture-router" && ReadString(body, "model", "") == model
                                && ReadString(body, "prompt", "") == "fixture edit" && ReadInt(body, "n", 0) == 1
                                && DecodeImageBase64(ReadString(reference, "url", "")).SequenceEqual(source)
                                && !body.ContainsKey("strength") && !body.ContainsKey("steps") && !body.ContainsKey("negative_prompt");
                            return "{\"data\":[{\"b64_json\":\"BAUG\",\"media_type\":\"image/png\"}]}";
                        });
                    check(prefix + "_" + model, wire && profile.Provider == "OpenRouter" && ImageProfileModel(profile) == model
                        && result.Ok && result.ImageBytes.SequenceEqual(generated));
                }
            }
            var p = ResolveImageGenerationProfile(settings, "portrait");
            foreach (string response in new[] { "{}", "{\"data\":[]}", "{\"data\":[{\"b64_json\":\"invalid\"}]}",
                "{\"error\":{\"message\":\"refused\"},\"input_references\":[{\"image_url\":{\"url\":\"data:image/png;base64,AQID\"}}]}" })
                check("reject_empty_or_echo_" + rows.Count, !GenerateOpenRouterImage(settings, p, "test", source, "768x1024", (a,b,c,d) => response).Ok);
            check("timeout", !GenerateOpenRouterImage(settings, p, "test", source, "768x1024", (a,b,c,d) => { throw new TimeoutException("fixture"); }).Ok);
            settings["openRouterApiKey"] = "";
            int calls = 0;
            check("missing_key_never_falls_back", !GenerateOpenRouterImage(settings, p, "test", source, "768x1024", (a,b,c,d) => { calls++; return "{}"; }).Ok && calls == 0);
            bool rejected = false;
            try { BuildOpenRouterImagePayload("gpt-image-1.5", "test", source, "768x1024"); } catch (ArgumentException) { rejected = true; }
            check("unknown_model_rejected", rejected);
            check("gpt_supported_ratio", ReadString(BuildOpenRouterImagePayload(DefaultOpenRouterImageModel, "test", source, "2720x1530"), "aspect_ratio", "") == "3:2");
            return rows;
        }
    }
}
