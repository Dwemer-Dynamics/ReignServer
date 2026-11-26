#if !REIGN_EXCLUDE_VERIFICATION
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using AIPortraits;

namespace ReignBetaServer
{
    internal static partial class PortraitsService
    {
        private static List<Dictionary<string, object>> RunLocalModelImageProviderContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string name, bool pass, string detail) =>
                AddSharedPortraitSelfTest(rows, "local_model_" + name, pass, detail);

            var settings = new Dictionary<string, object> { ["sceneryImageProfileInitialized"] = true,
                ["portraitNanoGptApiKey"] = "fixture-only-nanogpt-key",
                [LocalModelGenerationsKey] = DefaultLocalModelGenerationsUrl,
                [LocalModelEditsKey] = DefaultLocalModelEditsUrl };
            for (int slot = 0; slot < ImageProfilePrefixes.Length; slot++)
            {
                string prefix = ImageProfilePrefixes[slot];
                settings[prefix + "Provider"] = "Local Model";
                settings[prefix + "LocalModelSteps"] = 26 + slot;
                settings[prefix + "LocalModelCfg"] = 4.5d;
                settings[prefix + "LocalModelSampler"] = "heun";
                settings[prefix + "LocalModelScheduler"] = "karras";
                settings[prefix + "LocalModelNegativePrompt"] = "fixture negative";
                settings[prefix + "LocalModelReferenceResolution"] = 0;
                settings[prefix + "LocalModelFitReference"] = true;
                settings[prefix + "LocalModelSeed"] = "18446744073709551615";
            }
            SettingsService.NormalizePortraitImageSettings(settings);
            byte[] png = PngEncoder.EncodeRgba(new byte[256 * 256 * 4], 256, 256);
            for (int slot = 0; slot < ImageProfilePrefixes.Length; slot++)
            {
                string prefix = ImageProfilePrefixes[slot];
                var profile = ResolveImageGenerationProfile(settings, prefix);
                using HttpRequestMessage text = BuildLocalModelImageRequest(settings, profile, "one local scene", null, "", out _, out bool textFit);
                using HttpRequestMessage edit = BuildLocalModelImageRequest(settings, profile, "one local edit", png, "", out _, out bool editFit);
                string sentText = text.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                string sentEdit = edit.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                object textData = Json.DeserializeObject(sentText), editData = Json.DeserializeObject(sentEdit);
                Check(prefix + "_routes_and_options",
                    profile.Provider == LocalModelImageProviderId && ImageProfileModel(profile) == DefaultLocalModelImageModel
                    && text.RequestUri.AbsoluteUri == DefaultLocalModelGenerationsUrl && edit.RequestUri.AbsoluteUri == DefaultLocalModelEditsUrl
                    && !textFit && editFit && text.Headers.Authorization == null && edit.Headers.Authorization == null
                    && !sentText.Contains("fixture-only-nanogpt-key") && !sentEdit.Contains("fixture-only-nanogpt-key")
                    && ReadPathString(textData, "model") == DefaultLocalModelImageModel
                    && ReadPathString(textData, "prompt") == "one local scene" && ReadPathString(editData, "prompt") == "one local edit"
                    && ReadPathObject(textData, "imageDataUrl") == null
                    && ReadPathString(editData, "imageDataUrl") == "data:image/png;base64," + Convert.ToBase64String(png)
                    && ReadPathString(editData, "response_format") == "b64_json"
                    && ReadPathObject(editData, "fit_reference") is true && ReadPathString(editData, "reference_resolution") == "0"
                    && ReadPathString(editData, "steps") == (26 + slot).ToString()
                    && Convert.ToDouble(ReadPathObject(editData, "cfg")) == 4.5d
                    && ReadPathString(editData, "sampler") == "heun" && ReadPathString(editData, "scheduler") == "karras"
                    && ReadPathString(editData, "negative_prompt") == "fixture negative"
                    && ReadPathString(editData, "seed") == "18446744073709551615"
                    && ReadPathObject(editData, "strength") == null && ReadPathObject(editData, "denoise") == null,
                    "The exact profile chooses its own Qwen options and routes text and references over keyless Desk requests.");
            }
            Check("independent_normalization", !SettingsService.NormalizePortraitImageSettings(settings)
                && ReadString(settings, "portraitLocalModelImageSize", "") == "1024x1536"
                && ReadString(settings, "sceneryLocalModelImageSize", "") == "1536x1024",
                "Resolving all four profiles preserves the independently saved transport controls and orientation defaults.");

            using var handler = CreateLocalModelImageHttpHandler();
            Check("isolated_transport", !handler.AllowAutoRedirect && !handler.UseProxy && !handler.UseCookies && handler.Credentials == null,
                "The local sender cannot reuse cookies, select a proxy, supply credentials or follow a redirect.");
            Check("loopback_endpoints",
                TryNormalizeLocalModelImageEndpoint("http://localhost:18500/v1/images/edits", true, out string canonical)
                && canonical == DefaultLocalModelEditsUrl
                && TryNormalizeLocalModelImageEndpoint("http://[::1]:18500/v1/images/generations", false, out _)
                && !TryNormalizeLocalModelImageEndpoint(DefaultLocalModelGenerationsUrl, true, out _)
                && !TryNormalizeLocalModelImageEndpoint("https://example.invalid/v1/images/edits", true, out _)
                && !TryNormalizeLocalModelImageEndpoint("http://127.0.0.1:18500/v1/images/edits?token=fixture", true, out _)
                && !TryNormalizeLocalModelImageEndpoint("http://fixture@127.0.0.1:18500/v1/images/edits", true, out _),
                "Local Model addresses are limited to the matching HTTP loopback path and cannot carry credentials or query parameters.");

            string fixtureJson = Json.Serialize(new Dictionary<string, object> { ["data"] = new[] {
                new Dictionary<string, object> { ["b64_json"] = Convert.ToBase64String(png) } } });
            bool refusedUrl = false, refusedMalformed = false;
            try { ReadLocalModelPngResponse("{\"data\":[{\"url\":\"https://example.invalid/image.png\"}]}", false); }
            catch (InvalidDataException) { refusedUrl = true; }
            try { ReadLocalModelPngResponse("{\"data\":[{\"b64_json\":\"BAUG\"}]}", false); }
            catch (InvalidDataException) { refusedMalformed = true; }
            Check("bounded_png_response", ReadLocalModelPngResponse(fixtureJson, false).SequenceEqual(png) && refusedUrl && refusedMalformed,
                "The response accepts only bounded Desk PNG bytes in data[0].b64_json.");

            string CanvasResponse(byte[] bytes) => Json.Serialize(new Dictionary<string, object> { ["data"] = new[] {
                new Dictionary<string, object> { ["b64_json"] = Convert.ToBase64String(bytes) } } });

            bool RejectsCanvas(string response, bool fit)
            {
                try { ReadLocalModelPngResponse(response, fit); return false; }
                catch (InvalidDataException) { return true; }
            }

            string EnvelopeProbe(int width, int height)
            {
                // Only the IHDR dimensions are relevant to these rejection cases.
                byte[] header = (byte[])png.Clone();
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16, 4), width);
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20, 4), height);
                return CanvasResponse(header);
            }

            // At reference resolutions 2048 and 256, Desk rounds a 2:3 reference to these dimensions.
            byte[] largeFit = PngEncoder.EncodeRgba(new byte[1664 * 2496 * 4], 1664, 2496);
            byte[] smallFit = PngEncoder.EncodeRgba(new byte[224 * 320 * 4], 224, 320);
            string largeFitResponse = CanvasResponse(largeFit);
            string smallFitResponse = CanvasResponse(smallFit);
            Check("fitted_response_dimensions",
                ReadLocalModelPngResponse(largeFitResponse, true).SequenceEqual(largeFit)
                && ReadLocalModelPngResponse(smallFitResponse, true).SequenceEqual(smallFit)
                && RejectsCanvas(largeFitResponse, false) && RejectsCanvas(smallFitResponse, false)
                && RejectsCanvas(EnvelopeProbe(2176, 2176), true)
                && RejectsCanvas(EnvelopeProbe(4128, 32), true)
                && RejectsCanvas(EnvelopeProbe(1665, 2496), true),
                "Only fitted edits accept Desk's rounded reference dimensions and they remain bounded by axes and total pixels.");

            var view = ResolveImageGenerationProfile(settings, "scenery", LocalModelImageProviderId);
            view.ChatView = true;
            Dictionary<string, object> capability = ChatViewImageCapability(view);
            using HttpRequestMessage fixedView = BuildLocalModelImageRequest(settings, view, "fixed view", png,
                ReadString(capability, "size", ""), out _, out bool viewFit);
            object fixedData = Json.DeserializeObject(fixedView.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            Check("fixed_view_canvas", ReadString(capability, "size", "") == "1536x1024"
                && ReadPathString(fixedData, "width") == "1536" && ReadPathString(fixedData, "height") == "1024"
                && !viewFit && ReadPathObject(fixedData, "fit_reference") is false,
                "A fixed 3:2 View request preserves its required canvas even when the profile prefers fitting its reference.");

            var legacy = new Dictionary<string, object> { ["sceneryImageProfileInitialized"] = true,
                ["portraitProvider"] = "NanoGPT", ["portraitNanoGptImageModel"] = "gpt-image-1.5",
                ["sceneryProvider"] = "OpenRouter", ["adultPortraitProvider"] = "AtlasCloud",
                ["adultSceneryProvider"] = "NanoGPT", ["adultSceneryNanoGptImageModel"] = "gemini-3-pro-image-preview",
                ["portraitNanoGptImageGenerationsUrl"] = DefaultLocalModelGenerationsUrl,
                ["portraitNanoGptChatCompletionsUrl"] = NanoGptChatCompletionsUrl,
                ["portraitNanoGptApiKey"] = "fixture-only-existing-image-key", ["nanoGptApiKey"] = "fixture-only-text-key" };
            bool migrated = SettingsService.NormalizePortraitImageSettings(legacy);
            Check("exact_legacy_generation_migration",
                migrated && !SettingsService.NormalizePortraitImageSettings(legacy)
                && ReadString(legacy, "portraitProvider", "") == LocalModelImageProviderId
                && ReadString(legacy, "sceneryProvider", "") == "OpenRouter"
                && ReadString(legacy, "adultPortraitProvider", "") == "AtlasCloud"
                && ReadString(legacy, "adultSceneryProvider", "") == "NanoGPT"
                && ReadString(legacy, "portraitNanoGptImageGenerationsUrl", "") == NanoGptImageGenerationsUrl
                && ReadString(legacy, "portraitNanoGptChatCompletionsUrl", "") == NanoGptChatCompletionsUrl
                && ReadString(legacy, "portraitNanoGptApiKey", "") == "fixture-only-existing-image-key"
                && ReadString(legacy, "nanoGptApiKey", "") == "fixture-only-text-key",
                "Only NanoGPT profiles actually routed to the Desk generation endpoint migrate. Other providers, routes and keys are preserved.");
            legacy["portraitNanoGptChatCompletionsUrl"] = "http://127.0.0.1:18500/v1/chat/completions";
            SettingsService.NormalizePortraitImageSettings(legacy);
            Check("exact_legacy_chat_migration",
                ReadString(legacy, "adultSceneryProvider", "") == LocalModelImageProviderId
                && ReadString(legacy, "portraitNanoGptChatCompletionsUrl", "") == NanoGptChatCompletionsUrl,
                "The old Gemini image adapter migrates only when its own chat route was set to the Desk.");

            const string deskChat = "http://127.0.0.1:18500/v1/chat/completions";
            var legacyAliases = new Dictionary<string, object> { ["sceneryImageProfileInitialized"] = true,
                ["portraitProvider"] = "Nano-GPT", ["portraitNanoGptImageModel"] = "gpt-image-1.5",
                ["sceneryProvider"] = "OpenRouter",
                ["portraitNanoGptImageGenerationsUrl"] = DefaultLocalModelGenerationsUrl,
                ["portraitNanoGptChatCompletionsUrl"] = deskChat };
            bool aliasMigrated = SettingsService.NormalizePortraitImageSettings(legacyAliases);
            Check("normalized_legacy_route_migration",
                aliasMigrated && !SettingsService.NormalizePortraitImageSettings(legacyAliases)
                && ReadString(legacyAliases, "portraitProvider", "") == LocalModelImageProviderId
                && ReadString(legacyAliases, "sceneryProvider", "") == "OpenRouter"
                && ReadString(legacyAliases, "portraitNanoGptImageGenerationsUrl", "") == NanoGptImageGenerationsUrl
                && ReadString(legacyAliases, "portraitNanoGptChatCompletionsUrl", "") == deskChat,
                "Normalize the effective legacy provider and restore only the old route used by a migrated profile.");

            var foreign = new Dictionary<string, object> { ["sceneryImageProfileInitialized"] = true,
                ["portraitProvider"] = "NanoGPT", ["portraitNanoGptImageModel"] = "gpt-image-1.5",
                ["portraitNanoGptImageGenerationsUrl"] = "http://127.0.0.1:18501/v1/images/generations" };
            SettingsService.NormalizePortraitImageSettings(foreign);
            Check("credential_destination_wall",
                ReadString(foreign, "portraitProvider", "") == "NanoGPT"
                && !SettingsService.LocalKeyDestinationAllowed(foreign, "portraitNanoGptApiKey",
                    new[] { NanoGptImageRequestUrl(foreign, "gpt-image-1.5") })
                && !SettingsService.LocalKeyDestinationAllowed(foreign, "portraitNanoGptApiKey",
                    new[] { "https://example.invalid/v1/images/generations" })
                && !IsNanoGptImageCredentialDestination("http://nano-gpt.com/api/v1/images/generations")
                && !IsNanoGptImageCredentialDestination("https://nano-gpt.com.attacker.invalid/v1/images/generations")
                && !IsNanoGptImageCredentialDestination("https://nano-gpt.com@example.invalid/v1/images/generations")
                && SettingsService.LocalKeyDestinationAllowed(foreign, "portraitNanoGptApiKey",
                    new[] { NanoGptImageGenerationsUrl, NanoGptChatCompletionsUrl }),
                "A custom or local destination cannot resolve or receive the NanoGPT image credential.");
            return rows;
        }
    }
}
#endif
