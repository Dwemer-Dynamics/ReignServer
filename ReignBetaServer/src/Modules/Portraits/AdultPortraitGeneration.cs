using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] ImageProfilePrefixes = { "portrait", "scenery", "adultPortrait", "adultScenery" };
        private static readonly string[] NanoImageModels = { "gpt-image-1.5", "gpt-image-1", "flux-kontext", "gemini-3-pro-image-preview" };
        private static readonly string[] AtlasImageModels = { AtlasModelWan25ImageEdit, AtlasModelSeedreamEdit, AtlasModelSeedreamV5ProEdit,
            AtlasModelFluxKontextDev, AtlasModelWan27ImageEdit, AtlasModelGrokImagineImageEdit, "xai/grok-imagine-image-quality/edit" };

        private static bool UsesAdultPortraitClothingEdit(Dictionary<string, object> payload)
        {
            return !PreserveResidentClothing(payload)
                && ImageGenerationProfileName(ReadString(payload, "promptPurpose", "portrait")) == "portrait"
                && IsFemalePortraitSubject(payload) && ReadInt(payload, "ageYears", 0) >= 18
                && ReadInt(EnsurePortraitPhysicalConfidence(payload), "score", 50) >= 61;
        }

        private static string BuildAdultPortraitClothingPrompt(Dictionary<string, object> payload)
        {
            string layer = SelectPortraitPhysicalConfidencePromptFile(payload);
            if (!UsesAdultPortraitClothingEdit(payload) || string.IsNullOrWhiteSpace(layer))
                throw new InvalidOperationException("This portrait is not eligible for an adult clothing edit.");
            return AppendPortraitPhysique("CLOTHING EDIT OF THE SUPPLIED FINISHED ADULT PORTRAIT. "
                + "Change only the clothing according to the clothing guidance below, except correct physique to the authoritative native body layer if the supplied AI portrait conflicts. "
                + "Preserve the exact face, facial features, expression, identity, age, hair, skin, skeletal proportions, pose, hands, "
                + "lighting, background, camera position, full-body framing and image dimensions. "
                + "Do not redraw, beautify, replace or reinterpret the person. Do not add text, borders or other objects. "
                + "Clothing guidance below applies only to garments; body size is governed exclusively by the native body layer, never by revealingness.\n\n"
                + ExpandPortraitIdentityTokens(payload, LoadPromptTemplate(layer).Trim()), payload);
        }

        private static PortraitImageCallResult CallImageProvider(Dictionary<string, object> settings,
            ImageGenerationProfile profile, string prompt, byte[] source, string size)
        {
            if (profile.Provider == "OpenRouter") return GenerateOpenRouterImage(settings, profile, prompt, source, size);
            if (IsCodexImageProvider(profile.Provider))
                return GenerateCodexImage(settings, profile, prompt, source, size);
            return IsAtlasProvider(profile.Provider)
                ? GenerateAtlasImage(settings, profile, prompt, source, size)
                : GenerateNanoGptImage(settings, profile, prompt, source, size);
        }

        private sealed class PortraitClothingEditResult
        {
            public PortraitImageCallResult Call;
            public PortraitImageCallResult AttemptedCall;
            public string Prompt;
            public byte[] Source;
            public Dictionary<string, object> Evidence;
        }

        // Per-call delegates provide isolated, provider-free failure and handoff coverage.
        private static PortraitClothingEditResult RunPortraitClothingEdit(
            PortraitImageCallResult normal, string normalPrompt, byte[] nativeSource,
            ImageGenerationProfile adultProfile, Func<string> buildPrompt,
            Func<string, byte[], PortraitImageCallResult> generate, Func<byte[], byte[]> validate)
        {
            if (normal == null || !normal.Ok || normal.ImageBytes == null || normal.ImageBytes.Length == 0)
                throw new InvalidDataException("A successful normal portrait is required before the clothing edit.");
            var result = new PortraitClothingEditResult { Call = normal, Prompt = normalPrompt, Source = nativeSource };
            var evidence = new Dictionary<string, object>
            {
                ["schema"] = "reign-portrait-clothing-edit-v1", ["profile"] = adultProfile.Name,
                ["provider"] = adultProfile.Provider,
                ["model"] = ImageProfileModel(adultProfile),
                ["sourceSha256"] = Sha256Hex(normal.ImageBytes), ["outcome"] = "fallback", ["warning"] = ""
            };
            result.Evidence = evidence;
            var timer = Stopwatch.StartNew();
            try
            {
                string prompt = buildPrompt();
                evidence["promptSha256"] = Sha256Hex(System.Text.Encoding.UTF8.GetBytes(prompt));
                evidence["promptChars"] = prompt.Length;
                PortraitImageCallResult edited = generate(prompt, normal.ImageBytes);
                result.AttemptedCall = edited;
                if (edited == null || !edited.Ok || edited.ImageBytes == null || edited.ImageBytes.Length == 0)
                    throw new InvalidDataException(edited == null ? "No clothing-edit result was returned." : edited.Error);
                byte[] normalized = validate(edited.ImageBytes);
                if (normalized == null || normalized.Length == 0) throw new InvalidDataException("Empty clothing-edit image.");
                edited.ImageBytes = normalized;
                result.Call = edited;
                result.Prompt = prompt;
                result.Source = normal.ImageBytes;
                evidence["outcome"] = "completed";
                evidence["model"] = edited.Model;
                evidence["outputSha256"] = Sha256Hex(normalized);
            }
            catch (Exception ex)
            {
                evidence["warning"] = "Clothing edit failed; the normal portrait was used. " + ex.Message;
            }
            timer.Stop();
            evidence["durationMs"] = timer.ElapsedMilliseconds;
            return result;
        }

        private static bool IsRetiredImageProvider(string provider)
        {
            string value = (provider ?? "").Replace(" ", "").Replace("-", "").Trim();
            return new[] { "ReignImageGenerator", "ReignGenerator", "LocalReign" }.Contains(value, StringComparer.OrdinalIgnoreCase);
        }
    }
}
