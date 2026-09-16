using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunAdultPortraitContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            rows.AddRange(RunPortraitPhysiqueContractTests());
            rows.AddRange(RunCodexImageProviderContractTests());
            rows.AddRange(RunOpenRouterImageProviderContractTests());
            Action<string, bool, string> check = (name, passed, detail) => AddSharedPortraitSelfTest(rows, name, passed, detail);
            Func<int, int, string, Dictionary<string, object>> subject = (score, age, gender) => new Dictionary<string, object>
            {
                ["ageYears"] = age, ["gender"] = gender, ["promptPurpose"] = "portrait",
                ["physicalConfidence"] = new Dictionary<string, object> { ["score"] = score }
            };
            check("adult_portrait_confidence_boundaries", new[] { 60, 61, 80, 81, 100 }.All(score =>
                UsesAdultPortraitClothingEdit(subject(score, 25, "female")) == (score >= 61)), "The second pass begins at 61 inclusive.");
            check("adult_portrait_age_and_gender", !UsesAdultPortraitClothingEdit(subject(100, 17, "female"))
                && !UsesAdultPortraitClothingEdit(subject(100, 0, "female"))
                && !UsesAdultPortraitClothingEdit(subject(100, 30, "male"))
                && UsesAdultPortraitClothingEdit(subject(61, 18, "woman")), "Known adulthood and female gender are both required.");
            var eventSubject = subject(100, 25, "female"); eventSubject["promptPurpose"] = "castle_scene";
            check("adult_portrait_does_not_route_events", !UsesAdultPortraitClothingEdit(eventSubject)
                && ImageGenerationProfileName("castle_scene") == "scenery", "Event routing remains normal scenery.");
            foreach (int score in new[] { 61, 80, 81, 100 })
            {
                var payload = subject(score, 25, "female");
                string selected = SelectPortraitPhysicalConfidencePromptFile(payload);
                string confidence = ExpandPortraitIdentityTokens(payload, LoadPromptTemplate(selected).Trim());
                string normal = BuildPortraitPrompt(payload, "", false);
                string edit = BuildAdultPortraitClothingPrompt(payload);
                check("adult_portrait_prompt_split_" + score, !string.IsNullOrWhiteSpace(confidence)
                    && !normal.Contains(confidence) && edit.Contains(confidence)
                    && edit.Contains("Preserve the exact face") && edit.Contains("Change only the clothing")
                    && selected.Contains(score <= 80 ? "61_80" : "81_100"), "Normal prompt omits the selected confidence layer; edit uses only the correct clothing band and preservation instructions.");
            }
            var settings = new Dictionary<string, object> { ["portraitProvider"] = "NanoGPT", ["portraitAtlasImageModel"] = AtlasModelWan25ImageEdit };
            NormalizePortraitImageSettings(settings);
            check("adult_image_profile_defaults", new[] { "adultPortrait", "adultScenery" }.All(prefix =>
                ReadString(settings, prefix + "Provider", "") == "AtlasCloud"
                && ReadString(settings, prefix + "AtlasImageModel", "") == AtlasModelSeedreamV5ProEdit)
                && ReadString(settings, "portraitProvider", "") == "NanoGPT", "New adult profiles initialize to AtlasCloud Seedream v5 without replacing normal selections.");
            settings["adultPortraitProvider"] = "NanoGPT";
            settings["adultPortraitNanoGptImageModel"] = "flux-kontext";
            settings["adultPortraitImageStrength"] = 0.91d;
            settings["adultSceneryAtlasWanNegativePrompt"] = "independent scenery negative";
            var restored = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(settings));
            NormalizePortraitImageSettings(restored);
            var profile = ResolveImageGenerationProfile(restored, "adultPortrait");
            check("adult_image_profile_roundtrip", profile.Name == "adultPortrait" && profile.Provider == "NanoGPT"
                && profile.NanoGptModel == "flux-kontext" && Math.Abs(profile.ImageStrength - 0.91d) < 0.001
                && ResolveImageGenerationProfile(restored, "adultScenery").AtlasWanNegativePrompt == "independent scenery negative"
                && ResolveImageGenerationProfile(restored, "portrait").NanoGptModel == "gpt-image-1.5", "Adult selections and parameters survive serialization independently of normal settings.");
            foreach (string prefix in ImageProfilePrefixes)
            {
                restored[prefix + "Provider"] = "Reign Image Generator";
                restored[prefix + "ReignGeneratorSteps"] = 9;
            }
            NormalizePortraitImageSettings(restored);
            check("retired_image_provider_migration", ImageProfilePrefixes.All(prefix =>
                ReadString(restored, prefix + "Provider", "") == "AtlasCloud"
                && ReadString(restored, prefix + "AtlasImageModel", "") == AtlasModelSeedreamV5ProEdit
                && !restored.ContainsKey(prefix + "ReignGeneratorSteps")) && !NormalizePortraitImageSettings(restored),
                "Retired selections migrate once to AtlasCloud Seedream v5 and obsolete tuning is removed.");
            string html = ControlCenterHtml();
            check("four_image_profile_catalog_parity", ImageProfilePrefixes.All(prefix =>
                new[] { "Provider", "NanoGptImageModel", "AtlasImageModel", "OpenRouterImageModel" }.All(suffix =>
                {
                    Match select = Regex.Match(html, "<select id='" + prefix + suffix + "'[^>]*>(.*?)</select>", RegexOptions.Singleline);
                    string[] values = suffix == "Provider" ? (prefix.StartsWith("adult") ? new[] { "NanoGPT", "AtlasCloud", "OpenRouter" } : new[] { "NanoGPT", "AtlasCloud", "OpenRouter", "Codex" }) : suffix == "NanoGptImageModel" ? NanoImageModels : suffix == "OpenRouterImageModel" ? OpenRouterImageModels : AtlasImageModels;
                    return select.Success && values.All(value => select.Groups[1].Value.Contains("value='" + value + "'"))
                        && Regex.Matches(select.Groups[1].Value, "<option ").Count == values.Length;
                })) && !html.Contains("reign-generator/") && !html.Contains("Reign Image Generator")
                && !html.Contains("reignGeneratorStatus"), "All four profiles retain current catalogs; Codex appears only in normal profiles and retired controls remain absent.");

            byte[] native = { 1, 2 }, normalBytes = { 3, 4 }, editedBytes = { 5, 6 }, normalizedBytes = { 7, 8 };
            var normalCall = PortraitImageCallResult.Success("NanoGPT", "gpt-image-1.5", "test", normalBytes, 1, 0, 0);
            var adult = ResolveImageGenerationProfile(restored, "adultPortrait");
            int calls = 0; bool receivedNormal = false; bool validated = false;
            var success = RunPortraitClothingEdit(normalCall, "normal", native, adult, () => "clothes",
                (prompt, source) => { calls++; receivedNormal = source.SequenceEqual(normalBytes) && prompt == "clothes";
                    return PortraitImageCallResult.Success("AtlasCloud", AtlasModelSeedreamV5ProEdit, "test", editedBytes, 1, 0, 0); },
                bytes => { validated = bytes.SequenceEqual(editedBytes); return normalizedBytes; });
            check("adult_clothing_edit_handoff", calls == 1 && receivedNormal && validated
                && success.Call.ImageBytes.SequenceEqual(normalizedBytes) && success.Source.SequenceEqual(normalBytes)
                && ReadString(success.Evidence, "outcome", "") == "completed", "The second provider receives the validated normal image and publishes only the validated edited result.");
            foreach (string failure in new[] { "provider", "timeout", "invalid", "empty", "prompt" })
            {
                var result = RunPortraitClothingEdit(normalCall, "normal", native, adult,
                    () => { if (failure == "prompt") throw new IOException("fixture prompt failure"); return "clothes"; },
                    (prompt, source) => {
                        if (failure == "timeout") throw new TimeoutException("fixture timeout");
                        if (failure == "provider") return PortraitImageCallResult.Fail("AtlasCloud", "test", "fixture refusal");
                        return PortraitImageCallResult.Success("AtlasCloud", "test", "test", editedBytes, 1, 0, 0);
                    },
                    bytes => { if (failure == "invalid") throw new InvalidDataException("fixture invalid image"); return null; });
                check("adult_clothing_edit_fallback_" + failure, ReferenceEquals(result.Call, normalCall)
                    && result.Call.ImageBytes.SequenceEqual(normalBytes) && result.Prompt == "normal"
                    && result.Source.SequenceEqual(native) && ReadString(result.Evidence, "outcome", "") == "fallback"
                    && !string.IsNullOrEmpty(ReadString(result.Evidence, "warning", "")), "Second-stage failure retains the validated normal image and records the warning.");
            }
            bool rejectedNormal = false;
            try
            {
                RunPortraitClothingEdit(PortraitImageCallResult.Fail("NanoGPT", "test", "base failed"), "normal", native, adult,
                    () => "clothes", (prompt, source) => { calls++; return normalCall; }, bytes => bytes);
            }
            catch (InvalidDataException) { rejectedNormal = true; }
            check("adult_clothing_edit_rejects_failed_base", rejectedNormal && calls == 1, "First-stage failure cannot start an adult provider call.");

            string fixture = Path.Combine(Path.GetTempPath(), "reign-portrait-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            try
            {
                string portraitPath = Path.Combine(fixture, "portrait.png"), sourcePath = Path.Combine(fixture, "source.png"),
                    promptPath = Path.Combine(fixture, "prompt.txt"), metadataPath = Path.Combine(fixture, "portrait.json");
                File.WriteAllBytes(portraitPath, normalBytes); File.WriteAllBytes(sourcePath, native); File.WriteAllText(promptPath, "normal");
                const string operation = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                var receipt = new Dictionary<string, object> { ["schema"] = "reign-portrait-product-v1", ["accepted"] = true,
                    ["generationStages"] = new[] { success.Evidence }, ["warning"] = "fixture clothing edit fallback" };
                File.WriteAllText(metadataPath, Json.Serialize(new Dictionary<string, object> {
                    ["generationOperationId"] = operation, ["productReceipt"] = receipt }));
                var recovered = BuildStoredPortraitGenerationResponseFromPaths("fixture", "hero", operation,
                    portraitPath, sourcePath, promptPath, metadataPath);
                check("adult_clothing_edit_recovery_evidence", ReadBool(recovered, "ok", false)
                    && ReadString(recovered, "generationStatus", "") == "completed"
                    && ReadString(recovered, "warning", "") == "fixture clothing edit fallback"
                    && Json.Serialize(recovered["generationStages"]).Contains("adultPortrait")
                    && ReadString(recovered, "imageBase64", "") == Convert.ToBase64String(normalBytes),
                    "The common stored-product recovery reader retains stage evidence, fallback warning and selected master bytes.");
            }
            finally { Directory.Delete(fixture, true); }
            string previewDirectory = Path.Combine(VerificationDir, "portrait-profiles");
            Directory.CreateDirectory(previewDirectory);
            string previewPath = Path.Combine(previewDirectory, "control-center.html");
            File.WriteAllText(previewPath, html);
            check("portrait_profile_render_fixture", true, "Provider-free rendered Control Center HTML: " + previewPath);
            return rows;
        }
    }
}
