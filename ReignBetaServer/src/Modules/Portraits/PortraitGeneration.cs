using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
#if !REIGN_LINUX
using System.Drawing;
using System.Drawing.Imaging;
#endif
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AIPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string NanoGptImageGenerationsUrl = "https://nano-gpt.com/api/v1/images/generations";
        private const string NanoGptChatCompletionsUrl = "https://nano-gpt.com/api/v1/chat/completions";
        private const string AtlasGenerateImageUrl = "https://api.atlascloud.ai/api/v1/model/generateImage";
        private const string AtlasUploadMediaUrl = "https://api.atlascloud.ai/api/v1/model/uploadMedia";
        private const string AtlasPredictionUrlPrefix = "https://api.atlascloud.ai/api/v1/model/prediction/";
        private const string AtlasModelFluxKontextDev = "black-forest-labs/flux-kontext-dev";
        private const string AtlasModelSeedreamEdit = "bytedance/seedream-v4.5/edit";
        private const string AtlasModelSeedreamV5ProEdit = "bytedance/seedream-v5.0-pro/edit";
        private const string AtlasSeedreamV5ProLandscapeSize = "2720*1530";
        private const string AtlasSeedreamV5ProPortraitSize = "1530*2720";
        private const string AtlasModelWan25ImageEdit = "alibaba/wan-2.5/image-edit";
        private const string AtlasModelWan27ImageEdit = "alibaba/wan-2.7-pro/image-edit";
        private const string AtlasModelGrokImagineImageEdit = "xai/grok-imagine-image/edit";
        private const string DefaultAtlasImageModel = AtlasModelGrokImagineImageEdit;
        private const string DefaultAtlasWan25NegativePrompt = "illustration, painting, oil painting, watercolor, drawing, sketch, concept art, digital art, fantasy art, graphic novel, comic, cartoon, anime, cel shading, stylized image, 3D render, CGI, video game graphics, game screenshot, game model, synthetic person, doll, mannequin, wax figure, plastic skin, waxy skin, airbrushed skin, over-smoothed skin, artificial eyes, uncanny face, distorted anatomy, malformed hands, extra fingers, text, words, letters, numbers, captions, labels, names, ages, cultures, subtitles, signatures, logos, watermarks, interface elements, borders, banners, white strips, footer panels, metadata panels, armor, weapons, helmets, chainmail, modern clothing";
        private const int AtlasPollIntervalMs = 2500;
        private const int AtlasPollTimeoutMs = 300000;
        private static readonly ConcurrentDictionary<string, PortraitGenerationOperationState> PortraitGenerationOperations =
            new ConcurrentDictionary<string, PortraitGenerationOperationState>(StringComparer.OrdinalIgnoreCase);

        private sealed class PortraitGenerationOperationState
        {
            public string CampaignId = "";
            public string HeroStringId = "";
            public string CacheKey = "";
            public string OperationId = "";
            public string Status = "pending";
            public string Error = "";
            public string StartedUtc = "";
            public string CompletedUtc = "";
            public Dictionary<string, object> Failure;
        }

        private sealed class ImageGenerationProfile
        {
            public string Name;
            public string PromptPurpose;
            public string Provider;
            public string NanoGptModel;
            public string OpenRouterModel;
            public string AtlasModel;
            public string AtlasWanNegativePrompt;
            public double ImageStrength;
            public int InferenceSteps;
            public double GuidanceScale;
        }

        private static string ImageGenerationProfileName(string promptPurpose)
        {
            if (string.Equals(promptPurpose, "adultPortrait", StringComparison.OrdinalIgnoreCase)) return "adultPortrait";
            if (string.Equals(promptPurpose, "adultScenery", StringComparison.OrdinalIgnoreCase)) return "adultScenery";
            return string.IsNullOrWhiteSpace(promptPurpose)
                || string.Equals(promptPurpose, "portrait", StringComparison.OrdinalIgnoreCase)
                ? "portrait"
                : "scenery";
        }

        private static ImageGenerationProfile ResolveImageGenerationProfile(
            Dictionary<string, object> settings,
            string promptPurpose,
            string providerOverride = null)
        {
            settings = settings ?? new Dictionary<string, object>();
            string name = ImageGenerationProfileName(promptPurpose);
            string prefix = name;
            string fallbackPrefix = name.StartsWith("adult", StringComparison.Ordinal) ? name : "portrait";
            Func<string, string, string> readString = (suffix, fallback) =>
                ReadString(settings, prefix + suffix, ReadString(settings, fallbackPrefix + suffix, fallback));
            Func<string, int, int> readInt = (suffix, fallback) =>
                ReadInt(settings, prefix + suffix, ReadInt(settings, fallbackPrefix + suffix, fallback));
            Func<string, double, double> readDouble = (suffix, fallback) =>
                ReadDouble(settings, prefix + suffix, ReadDouble(settings, fallbackPrefix + suffix, fallback));

            string configuredProvider = readString("Provider", name.StartsWith("adult", StringComparison.Ordinal) ? "AtlasCloud" : "NanoGPT");
            return new ImageGenerationProfile
            {
                Name = name,
                PromptPurpose = string.IsNullOrWhiteSpace(promptPurpose) ? "portrait" : promptPurpose.Trim(),
                Provider = NormalizeImageProvider(string.IsNullOrWhiteSpace(providerOverride) ? configuredProvider : providerOverride),
                OpenRouterModel = readString("OpenRouterImageModel", DefaultOpenRouterImageModel),
                NanoGptModel = readString("NanoGptImageModel", "gpt-image-1.5"),
                AtlasModel = readString("AtlasImageModel", name.StartsWith("adult", StringComparison.Ordinal) ? AtlasModelSeedreamV5ProEdit : DefaultAtlasImageModel),
                AtlasWanNegativePrompt = readString("AtlasWanNegativePrompt", DefaultAtlasWan25NegativePrompt),
                ImageStrength = ClampDouble(readDouble("ImageStrength", 0.75d), 0.4d, 1d),
                InferenceSteps = Math.Max(10, Math.Min(50, readInt("InferenceSteps", 28))),
                GuidanceScale = ClampDouble(readDouble("GuidanceScale", 3.5d), 1d, 10d)
            };
        }

        private static readonly ConcurrentDictionary<string, Lazy<Dictionary<string, object>>> ActivePortraitRequests =
            new ConcurrentDictionary<string, Lazy<Dictionary<string, object>>>(StringComparer.Ordinal);

        private static Dictionary<string, object> PortraitGenerate(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (ImageGenerationProfileName(ReadString(payload, "promptPurpose", "portrait")) != "portrait")
                return PortraitGenerateCore(payload);
            string campaign = ReadString(payload, "campaignId", "default");
            string hero = ReadFirstString(payload, "heroStringId", "heroId", "characterId");
            string key = campaign + "|" + hero;
            var request = new Lazy<Dictionary<string, object>>(() => PortraitGenerateCore(payload), true);
            var active = ActivePortraitRequests.GetOrAdd(key, request);
            try { return active.Value; }
            finally
            {
                // Only the owner removes the shared in-flight request.
                if (ReferenceEquals(active, request)) ActivePortraitRequests.TryRemove(key, out var _);
            }
        }

        internal static byte[] ResolveImageRequestSource(string profile, byte[] supplied, Func<byte[]> nativeSource)
        {
            byte[] resolved = IsCharacterPortraitProfile(profile) ? nativeSource() : supplied;
            if (resolved == null || resolved.Length == 0) throw new InvalidDataException("No valid image source is available.");
            return resolved;
        }

        private static Dictionary<string, object> PortraitGenerateCore(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            payload.Remove("resolvedPortraitPhysique");
            Dictionary<string, object> settings = LoadSettings();
            Stopwatch total = Stopwatch.StartNew();

            string campaignId = ReadString(payload, "campaignId", "default");
            string heroId = ReadFirstString(payload, "heroStringId", "heroId", "characterId");
            string cacheKey = ReadString(payload, "cacheKey", "");
            string generationOperationId = NormalizePortraitGenerationOperationId(
                ReadString(payload, "generationOperationId", ""));
            string generationStartedUtc = ReadString(payload, "generationStartedUtc", DateTime.UtcNow.ToString("o"));
            string characterObjectId = ReadString(payload, "characterObjectId", "");
            string promptPurpose = ReadString(payload, "promptPurpose", "portrait");
            if (string.IsNullOrWhiteSpace(promptPurpose)) promptPurpose = "portrait";
            bool adultClothingEdit = UsesAdultPortraitClothingEdit(payload);
            string clientPrompt = ReadString(payload, "prompt", "");
            string prompt = ExpandPortraitIdentityTokens(payload, clientPrompt);
            Dictionary<string, object> physicalConfidence = ReadDictionary(payload, "physicalConfidence") ?? new Dictionary<string, object>();
            string sourceBase64 = ReadFirstString(payload, "sourceImageBase64", "imageBase64", "sourceImageDataUrl", "imageDataUrl");
            string outputSizeOverride = ReadString(payload, "outputSize", "");
            ImageGenerationProfile imageProfile = ResolveImageGenerationProfile(
                settings,
                promptPurpose,
                ReadString(payload, "provider", ""));
            Dictionary<string, object> timing = new Dictionary<string, object>();

            if (IsCharacterPortraitProfile(imageProfile.Name))
            {
                PortraitGenerationOperations[generationOperationId] = new PortraitGenerationOperationState
                {
                    CampaignId = campaignId,
                    HeroStringId = heroId ?? "",
                    CacheKey = cacheKey ?? "",
                    OperationId = generationOperationId,
                    Status = "pending",
                    StartedUtc = generationStartedUtc
                };
            }

            try
            {
                Stopwatch phase = Stopwatch.StartNew();
                byte[] sourceImage = IsCharacterPortraitProfile(imageProfile.Name) && !ReadBool(payload, "sharedCacheOutput", false)
                    ? null : DecodeImageBase64(sourceBase64);
                phase.Stop();
                timing["decodeMs"] = phase.ElapsedMilliseconds;

                Dictionary<string, object> sourceProvenance = new Dictionary<string, object>();
                phase.Restart();
                sourceImage = ResolveImageRequestSource(imageProfile.Name, sourceImage,
                    () => ResolvePortraitGenerationSource(settings, payload, sourceImage, out sourceProvenance));
                if (IsCharacterPortraitProfile(imageProfile.Name))
                {
                    phase.Stop();
                    timing["nativeSourceMs"] = phase.ElapsedMilliseconds;
                    var nativePhysique = ValidateNativePhysique(ReadDictionary(sourceProvenance, "physique"), sourceImage);
                    payload["resolvedPortraitPhysique"] = BuildPortraitPhysiqueEvidence(nativePhysique, LoadPromptTemplate(PortraitBodyPromptFile));
                    bool sharedRebuild = ReadString(sourceProvenance, "kind", "") == "shared_ai_portrait_reference";
                    if (PreserveResidentClothing(payload))
                    {
                        adultClothingEdit = false;
                        // Even custom negative prompts must not silently strip the encountered outfit.
                        imageProfile.AtlasWanNegativePrompt = "text, watermark, captions, border, cartoon, plastic skin, distorted anatomy";
                        sourceProvenance["clothingPolicy"] = ReignBeta.Shared.Characters.EncounteredResidentRules.EquippedOutfitPolicy;
                    }
                    if (sharedRebuild) {
                        adultClothingEdit = false; // Preserve the existing outfit; no second clothing redesign.
                        if (imageProfile.AtlasWanNegativePrompt == DefaultAtlasWan25NegativePrompt)
                            imageProfile.AtlasWanNegativePrompt = DefaultAtlasWan25NegativePrompt.Replace(", armor, weapons, helmets, chainmail, modern clothing", "");
                    }
                    prompt = sharedRebuild
                        ? ApplyPortraitProductPromptContract(BuildSharedPortraitRebuildPrompt(payload))
                        : ApplyPortraitProductPromptContract(BuildPortraitPrompt(payload, clientPrompt, !adultClothingEdit));
                    outputSizeOverride = "768x1024";
                }

                if (string.IsNullOrWhiteSpace(prompt))
                {
                    return FailPortraitGenerationOperation(generationOperationId,
                        PortraitGenerateError(campaignId, imageProfile, "", "Prompt is empty.", timing, total));
                }

                phase.Restart();
                PortraitImageCallResult call = CallImageProvider(settings, imageProfile, prompt, sourceImage, outputSizeOverride);
                phase.Stop();
                timing["providerCallMs"] = phase.ElapsedMilliseconds;

                timing["totalMs"] = total.ElapsedMilliseconds;
                if (!call.Ok || call.ImageBytes == null || call.ImageBytes.Length == 0)
                {
                    LogPortraitImageCall(campaignId, imageProfile, call, prompt.Length, sourceImage.Length, false, timing);
                    return FailPortraitGenerationOperation(generationOperationId,
                        PortraitGenerateError(campaignId, imageProfile, call.Model, call.Error, timing, total, call));
                }

                PortraitProductValidation productValidation = null;
                if (IsCharacterPortraitProfile(imageProfile.Name))
                {
                    phase.Restart();
                    try
                    {
                        productValidation = NormalizeAndValidateGeneratedPortraitProduct(
                            call.ImageBytes,
                            out byte[] normalizedPortrait);
                        call.ImageBytes = normalizedPortrait;
                    }
                    catch (Exception validationError)
                    {
                        phase.Stop();
                        timing["productValidationMs"] = phase.ElapsedMilliseconds;
                        LogPortraitImageCall(campaignId, imageProfile, call, prompt.Length, sourceImage.Length, false, timing);
                        return FailPortraitGenerationOperation(generationOperationId, PortraitGenerateError(
                            campaignId,
                            imageProfile,
                            call.Model,
                            "The provider returned an unusable portrait and Reign kept the existing portrait. " + validationError.Message,
                            timing,
                            total,
                            call));
                    }
                    phase.Stop();
                    timing["productValidationMs"] = phase.ElapsedMilliseconds;
                }

                var generationStages = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["profile"] = imageProfile.Name, ["provider"] = call.Provider,
                        ["model"] = call.Model, ["outcome"] = "completed", ["durationMs"] = timing["providerCallMs"],
                        ["sourceSha256"] = Sha256Hex(sourceImage), ["outputSha256"] = Sha256Hex(call.ImageBytes) }
                };
                string generationWarning = "";
                if (ReadDictionary(payload, "resolvedPortraitPhysique") != null)
                    generationStages[0]["physique"] = ReadDictionary(payload, "resolvedPortraitPhysique");
                if (adultClothingEdit && productValidation != null)
                {
                    LogPortraitImageCall(campaignId, imageProfile, call, prompt.Length, sourceImage.Length, true, timing);
                    ImageGenerationProfile adultProfile = ResolveImageGenerationProfile(settings, "adultPortrait");
                    PortraitProductValidation editedValidation = null;
                    PortraitClothingEditResult edit = RunPortraitClothingEdit(call, prompt, sourceImage, adultProfile,
                        () => BuildAdultPortraitClothingPrompt(payload),
                        (editPrompt, reference) => CallImageProvider(settings, adultProfile, editPrompt, reference, "768x1024"),
                        bytes => { editedValidation = NormalizeAndValidateGeneratedPortraitProduct(bytes, out byte[] normalized); return normalized; });
                    generationStages.Add(edit.Evidence);
                    edit.Evidence["physique"] = ReadDictionary(payload, "resolvedPortraitPhysique");
                    generationWarning = ReadString(edit.Evidence, "warning", "");
                    timing["clothingEditMs"] = edit.Evidence["durationMs"];
                    if (edit.AttemptedCall != null)
                        LogPortraitImageCall(campaignId, adultProfile, edit.AttemptedCall,
                            ReadInt(edit.Evidence, "promptChars", 0), call.ImageBytes.Length,
                            ReadString(edit.Evidence, "outcome", "") == "completed",
                            new Dictionary<string, object> { ["providerCallMs"] = edit.Evidence["durationMs"] });
                    LogOperational("portrait.clothing_edit", new Dictionary<string, object> {
                        ["campaignId"] = campaignId, ["heroStringId"] = heroId,
                        ["generationOperationId"] = generationOperationId, ["stage"] = edit.Evidence });
                    if (ReadString(edit.Evidence, "outcome", "") == "completed")
                    {
                        call = edit.Call;
                        productValidation = editedValidation;
                    }
                }
                total.Stop();
                timing["totalMs"] = total.ElapsedMilliseconds;

                Dictionary<string, object> productReceipt = productValidation == null
                    ? null
                    : BuildPortraitProductReceipt(productValidation, sourceImage, prompt, call, sourceProvenance);
                Dictionary<string, object> portraitInput = productValidation == null
                    ? null
                    : BuildPortraitInputReceipt(sourceImage, sourceProvenance);

                if (productReceipt != null)
                {
                    productReceipt["generationStages"] = generationStages;
                    productReceipt["warning"] = generationWarning;
                    productReceipt["physique"] = ReadDictionary(payload, "resolvedPortraitPhysique");
                }
                payload["generationOperationId"] = generationOperationId;
                payload["generationStartedUtc"] = generationStartedUtc;
                string storedPath = ReadBool(payload, "sharedCacheOutput", false)
                    ? StoreGeneratedSharedPortrait(
                        payload,
                        cacheKey,
                        prompt,
                        sourceImage,
                        call.ImageBytes,
                        call,
                        physicalConfidence,
                        productReceipt)
                    : StoreGeneratedPortrait(
                        campaignId,
                        heroId,
                        characterObjectId,
                        cacheKey,
                        prompt,
                        sourceImage,
                        call.ImageBytes,
                        call,
                        physicalConfidence,
                        sourceProvenance,
                        productReceipt,
                        portraitInput,
                        generationOperationId,
                        generationStartedUtc,
                        timing,
                        total.ElapsedMilliseconds);
                Dictionary<string, object> response = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["heroStringId"] = heroId ?? "",
                    ["cacheKey"] = cacheKey ?? "",
                    ["generationOperationId"] = generationOperationId,
                    ["generationStatus"] = "completed",
                    ["promptPurpose"] = imageProfile.PromptPurpose,
                    ["imageProfile"] = imageProfile.Name,
                    ["provider"] = call.Provider,
                    ["model"] = call.Model,
                    ["adapter"] = call.Adapter,
                    ["durationMs"] = total.ElapsedMilliseconds,
                    ["requestChars"] = call.RequestChars,
                    ["responseChars"] = call.ResponseChars,
                    ["imageBytes"] = call.ImageBytes.Length,
                    ["imageBase64"] = Convert.ToBase64String(call.ImageBytes),
                    ["storedPath"] = storedPath ?? "",
                    ["sourceProvenance"] = sourceProvenance,
                    ["timing"] = timing,
                    ["generationStages"] = generationStages,
                    ["warning"] = generationWarning
                };
                if (productValidation != null)
                {
                    response["effectivePrompt"] = prompt;
                    response["sourceImageBase64"] = Convert.ToBase64String(sourceImage);
                    response["portraitProductReceipt"] = productReceipt;
                    response["portraitInput"] = portraitInput;
                    response["portraitProductContract"] = new Dictionary<string, object>
                    {
                        ["version"] = PortraitProductReceiptVersion,
                        ["accepted"] = true,
                        ["width"] = productValidation.Width,
                        ["height"] = productValidation.Height,
                        ["faceMethod"] = productValidation.Focus.Method ?? "",
                        ["faceModel"] = productValidation.Focus.Model ?? "",
                        ["faceConfidence"] = productValidation.Focus.Confidence,
                        ["faceCandidateCount"] = productValidation.Focus.CandidateCount,
                        ["faceXRatio"] = productValidation.Focus.X,
                        ["faceYRatio"] = productValidation.Focus.Y,
                        ["faceWidthRatio"] = productValidation.Focus.Width,
                        ["faceHeightRatio"] = productValidation.Focus.Height,
                        ["faceTopRatio"] = productValidation.Focus.Y,
                        ["normalizedProviderCanvas"] = productValidation.Normalized
                    };
                }

                response["characterName"] = ReadString(payload, "characterName", "");
                response["ageYears"] = ReadInt(payload, "ageYears", 0);
				response["clanTier"] = ReadInt(payload, "clanTier", 0);
				response["socialStation"] = ReadString(payload, "socialStation", "");
				response["occupation"] = ReadString(payload, "occupation", "");
				response["isNotable"] = ReadBool(payload, "isNotable", false);
				response["physicalConfidence"] = physicalConfidence;

                if (!adultClothingEdit)
                    LogPortraitImageCall(campaignId, imageProfile, call, prompt.Length, sourceImage.Length, true, timing);
                CompletePortraitGenerationOperation(generationOperationId);
                return response;
            }
            catch (Exception ex)
            {
                total.Stop();
                timing["totalMs"] = total.ElapsedMilliseconds;
                return FailPortraitGenerationOperation(generationOperationId,
                    PortraitGenerateError(campaignId, imageProfile, "", ex.Message, timing, total));
            }
        }

        private static string NormalizePortraitGenerationOperationId(string value)
        {
            value = (value ?? "").Trim();
            if (Regex.IsMatch(value, "^[0-9a-fA-F]{32}$"))
            {
                return value.ToLowerInvariant();
            }
            return Guid.NewGuid().ToString("N");
        }

        private static void CompletePortraitGenerationOperation(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId)) return;
            if (PortraitGenerationOperations.TryGetValue(operationId, out PortraitGenerationOperationState state))
            {
                state.Status = "completed";
                state.CompletedUtc = DateTime.UtcNow.ToString("o");
            }
        }

        private static Dictionary<string, object> FailPortraitGenerationOperation(
            string operationId,
            Dictionary<string, object> response)
        {
            if (!string.IsNullOrWhiteSpace(operationId)
                && PortraitGenerationOperations.TryGetValue(operationId, out PortraitGenerationOperationState state))
            {
                state.Status = "failed";
                state.Error = ReadString(response, "error", "Portrait generation failed.");
                state.Failure = response;
                state.CompletedUtc = DateTime.UtcNow.ToString("o");
            }
            response["generationOperationId"] = operationId ?? "";
            response["generationStatus"] = "failed";
            return response;
        }

        private static Dictionary<string, object> PortraitGenerationStatus(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string heroId = ReadFirstString(payload, "heroStringId", "heroId", "characterId");
            string operationId = ReadString(payload, "generationOperationId", "").Trim().ToLowerInvariant();
            if (!Regex.IsMatch(operationId, "^[0-9a-f]{32}$"))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["generationStatus"] = "invalid",
                    ["error"] = "A valid portrait generation operation id is required."
                };
            }

            if (PortraitGenerationOperations.TryGetValue(operationId, out PortraitGenerationOperationState state))
            {
                if (!string.Equals(state.CampaignId, campaignId, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(heroId)
                        && !string.Equals(state.HeroStringId, heroId, StringComparison.OrdinalIgnoreCase)))
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["generationStatus"] = "not_found",
                        ["error"] = "That portrait operation does not belong to this campaign character."
                    };
                }
                if (string.Equals(state.Status, "pending", StringComparison.OrdinalIgnoreCase))
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["generationOperationId"] = operationId,
                        ["generationStatus"] = "pending",
                        ["campaignId"] = campaignId,
                        ["heroStringId"] = state.HeroStringId,
                        ["cacheKey"] = state.CacheKey,
                        ["startedUtc"] = state.StartedUtc
                    };
                }
                if (string.Equals(state.Status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    if (state.Failure != null) return new Dictionary<string, object>(state.Failure);
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["generationOperationId"] = operationId,
                        ["generationStatus"] = "failed",
                        ["error"] = state.Error,
                        ["completedUtc"] = state.CompletedUtc
                    };
                }
            }

            if (string.Equals(campaignId, "_shared", StringComparison.OrdinalIgnoreCase))
            {
                string sharedKey = ReadString(payload, "cacheKey", state == null ? "" : state.CacheKey);
                if (TryLoadSharedPortraitEntry(sharedKey, out SharedPortraitEntry entry, out string error)
                    && (string.IsNullOrWhiteSpace(heroId) || string.Equals(heroId, entry.HeroStringId, StringComparison.OrdinalIgnoreCase)))
                    return BuildStoredPortraitGenerationResponseFromPaths(campaignId, entry.HeroStringId, operationId,
                        entry.PortraitPath, entry.SourcePath, Path.Combine(entry.Folder, "prompt.txt"),
                        Path.Combine(entry.Folder, SharedPortraitGenerationMetadataFileName));
                return new Dictionary<string, object> { ["ok"] = false, ["generationStatus"] = "not_found", ["error"] = "Shared portrait not found." };
            }
            return BuildStoredPortraitGenerationResponse(campaignId, heroId, operationId);
        }

        private static Dictionary<string, object> BuildStoredPortraitGenerationResponse(
            string campaignId,
            string heroId,
            string operationId)
        {
            if (string.IsNullOrWhiteSpace(heroId))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["generationStatus"] = "not_found",
                    ["error"] = "heroStringId is required to recover a portrait product."
                };
            }

            string portraitPath = CharacterFile(campaignId, heroId, "portraits", "portrait.png");
            string sourcePath = CharacterFile(campaignId, heroId, "portraits", "source.png");
            string promptPath = CharacterFile(campaignId, heroId, "portraits", "prompt.txt");
            string metadataPath = CharacterFile(campaignId, heroId, "portraits", "portrait.json");
            return BuildStoredPortraitGenerationResponseFromPaths(
                campaignId, heroId, operationId, portraitPath, sourcePath, promptPath, metadataPath);
        }

        private static Dictionary<string, object> BuildStoredPortraitGenerationResponseFromPaths(
            string campaignId,
            string heroId,
            string operationId,
            string portraitPath,
            string sourcePath,
            string promptPath,
            string metadataPath)
        {
            Dictionary<string, object> metadata;
            byte[] portraitBytes;
            byte[] sourceBytes;
            string prompt;
            lock (FileLock)
            {
                metadata = ReadJsonObject(metadataPath);
                if (!string.Equals(ReadString(metadata, "generationOperationId", ""), operationId, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(portraitPath)
                    || !File.Exists(sourcePath)
                    || !File.Exists(promptPath))
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["generationOperationId"] = operationId,
                        ["generationStatus"] = "not_found",
                        ["campaignId"] = campaignId,
                        ["heroStringId"] = heroId
                    };
                }
                portraitBytes = File.ReadAllBytes(portraitPath);
                sourceBytes = File.ReadAllBytes(sourcePath);
                prompt = File.ReadAllText(promptPath, Encoding.UTF8);
            }

            Dictionary<string, object> receipt = ReadDictionary(metadata, "productReceipt")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> portraitInput = ReadDictionary(metadata, "portraitInput")
                ?? new Dictionary<string, object>();
            if (!ReadBool(receipt, "accepted", false)
                || !string.Equals(ReadString(receipt, "schema", ""), "reign-portrait-product-v1", StringComparison.Ordinal))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["generationOperationId"] = operationId,
                    ["generationStatus"] = "failed",
                    ["error"] = "The stored portrait does not contain an accepted product receipt."
                };
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["recovered"] = true,
                ["generationOperationId"] = operationId,
                ["generationStatus"] = "completed",
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["cacheKey"] = ReadString(metadata, "cacheKey", ""),
                ["promptPurpose"] = "portrait",
                ["imageProfile"] = "portrait",
                ["provider"] = ReadString(metadata, "provider", ""),
                ["model"] = ReadString(metadata, "model", ""),
                ["adapter"] = ReadString(metadata, "adapter", ""),
                ["durationMs"] = ReadLong(metadata, "durationMs", 0),
                ["imageBytes"] = portraitBytes.Length,
                ["imageBase64"] = Convert.ToBase64String(portraitBytes),
                ["sourceImageBase64"] = Convert.ToBase64String(sourceBytes),
                ["effectivePrompt"] = prompt,
                ["portraitProductReceipt"] = receipt,
                ["generationStages"] = receipt.TryGetValue("generationStages", out object stages) ? stages : new object[0],
                ["warning"] = ReadString(receipt, "warning", ""),
                ["portraitInput"] = portraitInput,
                ["sourceProvenance"] = ReadDictionary(metadata, "sourceProvenance") ?? new Dictionary<string, object>(),
                ["timing"] = ReadDictionary(metadata, "timing") ?? new Dictionary<string, object>(),
                ["storedPath"] = portraitPath
            };
        }

        private static string ExpandPortraitIdentityTokens(Dictionary<string, object> payload, string prompt)
        {
            string characterName = ReadString(payload, "characterName", "this character");
            int ageYears = ReadInt(payload, "ageYears", 0);
            string culture = ReadFirstString(payload, "cultureName", "cultureId");
            string gender = ReadString(payload, "gender", "person");
			int clanTier = ReadInt(payload, "clanTier", 0);
			string socialStation = ReadString(payload, "socialStation", SocialStationForClanTier(clanTier));
			Dictionary<string, object> physicalConfidence = EnsurePortraitPhysicalConfidence(payload);
			int physicalConfidenceScore = ReadInt(physicalConfidence, "score", 50);
            if (string.IsNullOrWhiteSpace(culture))
            {
                culture = "unknown";
            }
            if (string.IsNullOrWhiteSpace(gender))
            {
                gender = "person";
            }
			if (string.IsNullOrWhiteSpace(socialStation))
			{
				socialStation = SocialStationForClanTier(clanTier);
			}

            return (prompt ?? "")
                .Replace("[CHARACTER NAME]", characterName)
                .Replace("[AGE]", ageYears > 0 ? ageYears.ToString() : "unknown")
                .Replace("[CULTURE]", culture)
				.Replace("[GENDER]", gender)
				.Replace("[CLAN TIER]", clanTier > 0 ? clanTier.ToString() : "unknown")
				.Replace("[SOCIAL STATION]", socialStation)
				.Replace("[PHYSICAL CONFIDENCE]", physicalConfidenceScore.ToString());
        }

		private static string SocialStationForClanTier(int clanTier)
		{
			if (clanTier <= 0) return "unranked";
			if (clanTier <= 2) return "landowner";
			if (clanTier <= 4) return "lesser lord";
			return "high noble";
		}

        private static string BuildPortraitPrompt(Dictionary<string, object> payload, string clientPrompt, bool includePhysicalConfidence = true)
        {
            if (PreserveResidentClothing(payload)) return BuildResidentPortraitPrompt(payload);
			List<string> layers = new List<string>
            {
                "portrait_identity.txt",
                "portrait_composition.txt"
            };
			string cultureClothingPrompt = SelectPortraitClothingPromptFile(payload);
			if (!string.IsNullOrWhiteSpace(cultureClothingPrompt))
			{
				layers.Add(cultureClothingPrompt);
			}
			string physicalConfidencePrompt = includePhysicalConfidence ? SelectPortraitPhysicalConfidencePromptFile(payload) : "";
			if (!string.IsNullOrWhiteSpace(physicalConfidencePrompt))
			{
				layers.Add(physicalConfidencePrompt);
			}
			layers.Add("portrait_prompt_style.txt");
			layers.Add("portrait_output_rules.txt");
            List<string> parts = new List<string>();
            foreach (string layer in layers)
            {
                string text = LoadPromptTemplate(layer).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(text);
                }
            }

            string assembled = string.Join(Environment.NewLine + Environment.NewLine, parts);
            if (string.IsNullOrWhiteSpace(assembled))
            {
                assembled = clientPrompt ?? "";
            }

            return AppendPortraitPhysique(ExpandPortraitIdentityTokens(payload, assembled), payload);
        }

        private static Dictionary<string, object> PortraitGenerateError(
            string campaignId,
            ImageGenerationProfile imageProfile,
            string model,
            string error,
            Dictionary<string, object> timing,
            Stopwatch total,
            PortraitImageCallResult call = null)
        {
            if (total != null && total.IsRunning)
            {
                total.Stop();
            }

            Dictionary<string, object> row = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["campaignId"] = campaignId,
                ["promptPurpose"] = imageProfile == null ? "portrait" : imageProfile.PromptPurpose,
                ["imageProfile"] = imageProfile == null ? "portrait" : imageProfile.Name,
                ["provider"] = imageProfile == null ? "" : imageProfile.Provider,
                ["model"] = model ?? "",
                ["adapter"] = call == null ? "" : call.Adapter,
                ["durationMs"] = total == null ? 0 : total.ElapsedMilliseconds,
                ["requestChars"] = call == null ? 0 : call.RequestChars,
                ["responseChars"] = call == null ? 0 : call.ResponseChars,
                ["error"] = string.IsNullOrWhiteSpace(error) ? "Image generation failed." : error,
                ["timing"] = timing ?? new Dictionary<string, object>()
            };
            LogOperational("portrait.generate_failed", row);
            row["generationStages"] = new[] { new Dictionary<string, object> {
                ["profile"] = imageProfile == null ? "portrait" : imageProfile.Name,
                ["provider"] = imageProfile == null ? "" : imageProfile.Provider, ["model"] = model ?? "",
                ["outcome"] = "failed", ["durationMs"] = total == null ? 0 : total.ElapsedMilliseconds,
                ["error"] = row["error"] } };
            return row;
        }

        private static PortraitImageCallResult GenerateNanoGptImage(
            Dictionary<string, object> settings,
            ImageGenerationProfile imageProfile,
            string prompt,
            byte[] sourceImagePng,
            string outputSizeOverride)
        {
            string apiKey = ReadString(settings, "portraitNanoGptApiKey", "");
            string model = imageProfile.NanoGptModel;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return PortraitImageCallResult.Fail("NanoGPT", model, "No NanoGPT image API key is set in the server AI Image Generation tab.");
            }

            if (UsesGeminiChatImageAdapter(model))
            {
                return GenerateNanoGptGeminiChatImage(settings, apiKey, model, prompt, sourceImagePng);
            }

            if (UsesImageReferenceAdapter(model))
            {
                return GenerateNanoGptImageReference(settings, apiKey, model, prompt, sourceImagePng, outputSizeOverride);
            }

            return GenerateNanoGptFluxKontext(settings, imageProfile, apiKey, model, prompt, sourceImagePng, outputSizeOverride);
        }

        private static PortraitImageCallResult GenerateNanoGptFluxKontext(
            Dictionary<string, object> settings,
            ImageGenerationProfile imageProfile,
            string apiKey,
            string model,
            string prompt,
            byte[] sourceImagePng,
            string outputSizeOverride)
        {
            string outputSize = NanoFluxSize(outputSizeOverride);
            string url = ReadString(settings, "portraitNanoGptImageGenerationsUrl", NanoGptImageGenerationsUrl);
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["prompt"] = prompt ?? "",
                ["n"] = 1,
                ["size"] = outputSize,
                ["imageDataUrl"] = "data:image/png;base64," + Convert.ToBase64String(sourceImagePng),
                ["strength"] = imageProfile.ImageStrength,
                ["guidance_scale"] = imageProfile.GuidanceScale,
                ["num_inference_steps"] = imageProfile.InferenceSteps,
                ["response_format"] = "b64_json"
            };
            return PostImageJson(url, apiKey, body, settings, "NanoGPT", model, "flux-kontext image generation");
        }

        private static PortraitImageCallResult GenerateNanoGptImageReference(
            Dictionary<string, object> settings,
            string apiKey,
            string model,
            string prompt,
            byte[] sourceImagePng,
            string outputSizeOverride)
        {
            string outputSize = NanoGptReferenceSize(outputSizeOverride);
            string url = ReadString(settings, "portraitNanoGptImageGenerationsUrl", NanoGptImageGenerationsUrl);
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["prompt"] = prompt ?? "",
                ["n"] = 1,
                ["size"] = outputSize,
                ["imageDataUrl"] = "data:image/png;base64," + Convert.ToBase64String(sourceImagePng),
                ["response_format"] = "b64_json"
            };
            return PostImageJson(url, apiKey, body, settings, "NanoGPT", model, "image reference generation");
        }

        private static PortraitImageCallResult GenerateNanoGptGeminiChatImage(
            Dictionary<string, object> settings,
            string apiKey,
            string model,
            string prompt,
            byte[] sourceImagePng)
        {
            string url = ReadString(settings, "portraitNanoGptChatCompletionsUrl", NanoGptChatCompletionsUrl);
            ArrayList content = new ArrayList
            {
                new Dictionary<string, object> { ["type"] = "text", ["text"] = prompt ?? "" },
                new Dictionary<string, object>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object> { ["url"] = "data:image/png;base64," + Convert.ToBase64String(sourceImagePng) }
                }
            };
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new ArrayList
                {
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = content }
                },
                ["stream"] = false
            };
            return PostImageJson(url, apiKey, body, settings, "NanoGPT", model, "gemini chat image generation");
        }

        private static PortraitImageCallResult GenerateAtlasImage(
            Dictionary<string, object> settings,
            ImageGenerationProfile imageProfile,
            string prompt,
            byte[] sourceImagePng,
            string outputSizeOverride)
        {
            string apiKey = ReadString(settings, "portraitAtlasApiKey", "");
            string model = imageProfile.AtlasModel;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return PortraitImageCallResult.Fail("AtlasCloud", model, "No AtlasCloud API key is set in the server AI Image Generation tab.");
            }

            Stopwatch total = Stopwatch.StartNew();
            int requestChars = 0;
            int responseChars = 0;
            try
            {
                string uploadUrl = ReadString(settings, "portraitAtlasUploadMediaUrl", AtlasUploadMediaUrl);
                string sourceUrl = UploadAtlasMedia(uploadUrl, apiKey, sourceImagePng, out int uploadResponseChars);
                responseChars += uploadResponseChars;
                if (string.IsNullOrWhiteSpace(sourceUrl))
                {
                    return PortraitImageCallResult.Fail("AtlasCloud", model, "AtlasCloud upload returned no source URL.");
                }

                string outputSize = AtlasPresetSourceSize(outputSizeOverride);
                Dictionary<string, object> body = BuildAtlasImagePayload(
                    model,
                    prompt,
                    sourceUrl,
                    outputSize,
                    imageProfile.AtlasWanNegativePrompt);
                string json = Json.Serialize(body);
                requestChars += json.Length;
                string generateUrl = ReadString(settings, "portraitAtlasGenerateImageUrl", AtlasGenerateImageUrl);
                string response = PostJsonToLlm(generateUrl, apiKey, json, settings);
                responseChars += response.Length;
                string predictionId = ExtractAtlasPredictionId(response);
                if (string.IsNullOrWhiteSpace(predictionId))
                {
                    return PortraitImageCallResult.Fail("AtlasCloud", model, "AtlasCloud image submit returned no prediction id. " + Shorten(response, 260));
                }

                string predictionPrefix = ReadString(settings, "portraitAtlasPredictionUrlPrefix", AtlasPredictionUrlPrefix);
                byte[] imageBytes = PollAtlasPrediction(predictionPrefix, apiKey, predictionId, out int pollResponseChars);
                responseChars += pollResponseChars;
                total.Stop();
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    return PortraitImageCallResult.Fail("AtlasCloud", model, "AtlasCloud prediction completed without image bytes.");
                }

                imageBytes = NormalizeAtlasOutputToPng(imageBytes);
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    return PortraitImageCallResult.Fail("AtlasCloud", model, "AtlasCloud prediction returned an image format that could not be converted to PNG.");
                }

                return PortraitImageCallResult.Success("AtlasCloud", model, "atlas upload/generate/poll", imageBytes, total.ElapsedMilliseconds, requestChars, responseChars);
            }
            catch (Exception ex)
            {
                total.Stop();
                return PortraitImageCallResult.Fail("AtlasCloud", model, "AtlasCloud image generation failed: " + ex.Message, total.ElapsedMilliseconds, requestChars, responseChars);
            }
        }

        private static byte[] NormalizeAtlasOutputToPng(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length == 0)
            {
                return null;
            }

            byte[] png = PngReencode.ToPngEncoderFormat(imageBytes);
            if (png != null && png.Length > 0)
            {
                return png;
            }

            try
            {
#if REIGN_LINUX
                return LinuxImageCodec.Normalize(imageBytes);
#else
                using (MemoryStream input = new MemoryStream(imageBytes, false))
                using (Image source = Image.FromStream(input, true, true))
                using (Bitmap bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                using (MemoryStream output = new MemoryStream())
                {
                    graphics.DrawImage(source, 0, 0, source.Width, source.Height);
                    bitmap.Save(output, ImageFormat.Png);
                    return output.ToArray();
                }
#endif
            }
            catch
            {
                return null;
            }
        }

        private static PortraitImageCallResult PostImageJson(
            string url,
            string apiKey,
            Dictionary<string, object> body,
            Dictionary<string, object> settings,
            string provider,
            string model,
            string adapter)
        {
            Stopwatch timer = Stopwatch.StartNew();
            string json = Json.Serialize(body);
            try
            {
                string response = PostJsonToLlm(url, apiKey, json, settings);
                timer.Stop();
                byte[] imageBytes = ExtractImageBytesFromJson(response);
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    return PortraitImageCallResult.Fail(provider, model, adapter + " returned no image data. " + Shorten(response, 260), timer.ElapsedMilliseconds, json.Length, response.Length);
                }

                return PortraitImageCallResult.Success(provider, model, adapter, imageBytes, timer.ElapsedMilliseconds, json.Length, response.Length);
            }
            catch (Exception ex)
            {
                timer.Stop();
                PortraitImageCallResult failed = PortraitImageCallResult.Fail(
                    provider,
                    model,
                    adapter + " API error: " + ex.Message,
                    timer.ElapsedMilliseconds,
                    json.Length,
                    0);
                failed.Adapter = adapter ?? "";
                return failed;
            }
        }

        private static Dictionary<string, object> BuildAtlasImagePayload(
            string model,
            string prompt,
            string sourceUrl,
            string outputSize,
            string wanNegativePrompt)
        {
            if (UsesAtlasFluxKontextDev(model))
            {
                return new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["seed"] = -1,
                    ["width"] = GetAtlasFluxKontextWidth(outputSize),
                    ["height"] = GetAtlasFluxKontextHeight(outputSize),
                    ["image"] = sourceUrl,
                    ["prompt"] = prompt ?? "",
                    ["num_images"] = 1,
                    ["guidance_scale"] = 5,
                    ["num_inference_steps"] = 30,
                    ["enable_base64_output"] = false,
                    ["enable_safety_checker"] = true
                };
            }

            if (UsesAtlasSeedreamEdit(model))
            {
                return new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["prompt"] = prompt ?? "",
                    ["images"] = new ArrayList { sourceUrl },
                    ["size"] = MapToAtlasSeedreamSize(outputSize),
                    ["enable_base64_output"] = false
                };
            }

            if (UsesAtlasSeedreamV5ProEdit(model))
            {
                return new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["prompt"] = prompt ?? "",
                    ["images"] = new ArrayList { sourceUrl },
                    ["size"] = MapToAtlasSeedreamV5Size(outputSize),
                    ["output_format"] = "png",
                    ["thinking"] = "enabled",
                    ["enable_base64_output"] = false
                };
            }

            if (UsesAtlasWan25ImageEdit(model))
            {
                return new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["images"] = new ArrayList { sourceUrl },
                    ["prompt"] = prompt ?? "",
                    ["negative_prompt"] = wanNegativePrompt ?? "",
                    ["seed"] = -1,
                    ["size"] = MapToAtlasWan25Size(outputSize),
                    ["enable_prompt_expansion"] = true
                };
            }

            if (UsesAtlasWan27ImageEdit(model))
            {
                return new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["prompt"] = prompt ?? "",
                    ["images"] = new ArrayList { sourceUrl },
                    ["size"] = MapToAtlasWanSize(outputSize),
                    ["n"] = 1,
                    ["thinking_mode"] = true,
                    ["seed"] = -1,
                    ["enable_sync_mode"] = false,
                    ["enable_base64_output"] = false
                };
            }

            if (UsesAtlasGrokImagineImageEdit(model))
            {
                return new Dictionary<string, object>
                {
                    ["model"] = model,
                    ["prompt"] = prompt ?? "",
                    ["image_urls"] = new ArrayList { sourceUrl },
                    ["num_images"] = 1,
                    ["aspect_ratio"] = "auto",
                    ["resolution"] = "1k",
                    ["enable_base64_output"] = false
                };
            }

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["model"] = model,
                ["prompt"] = prompt ?? "",
                ["num_images"] = 1,
                ["aspect_ratio"] = MapToAtlasAspectRatio(outputSize),
                ["resolution"] = "1k"
            };

            if (AtlasModelUsesImageUrlsArray(model))
            {
                payload["image_urls"] = new ArrayList { sourceUrl };
            }
            else
            {
                payload["image_url"] = sourceUrl;
            }

            return payload;
        }

        private static string UploadAtlasMedia(string uploadUrl, string apiKey, byte[] sourceImagePng, out int responseChars)
        {
            responseChars = 0;
            string boundary = "----BannerlordReign" + Guid.NewGuid().ToString("N");
            byte[] prefix = Encoding.UTF8.GetBytes("--" + boundary + "\r\n"
                + "Content-Disposition: form-data; name=\"file\"; filename=\"aiportraits_reference.png\"\r\n"
                + "Content-Type: image/png\r\n\r\n");
            byte[] suffix = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uploadUrl);
            request.Method = "POST";
            request.ContentType = "multipart/form-data; boundary=" + boundary;
            request.Accept = "application/json";
            request.Timeout = 360000;
            request.Headers["Authorization"] = "Bearer " + apiKey;
            request.ContentLength = prefix.Length + sourceImagePng.Length + suffix.Length;
            using (Stream stream = request.GetRequestStream())
            {
                stream.Write(prefix, 0, prefix.Length);
                stream.Write(sourceImagePng, 0, sourceImagePng.Length);
                stream.Write(suffix, 0, suffix.Length);
            }

            string response = ReadWebResponseText(request);
            responseChars = response.Length;
            return ExtractAtlasUploadUrl(response);
        }

        private static byte[] PollAtlasPrediction(string predictionPrefix, string apiKey, string predictionId, out int responseChars)
        {
            responseChars = 0;
            Stopwatch timer = Stopwatch.StartNew();
            string url = predictionPrefix.TrimEnd('/') + "/" + Uri.EscapeDataString(predictionId);
            while (timer.ElapsedMilliseconds < AtlasPollTimeoutMs)
            {
                string response = GetTextWithBearer(url, apiKey);
                responseChars += response.Length;
                object root = Json.DeserializeObject(response);
                string status = ReadPathString(root, "data", "status");
                if (string.IsNullOrWhiteSpace(status))
                {
                    status = ReadPathString(root, "status");
                }

                if (status.Equals("completed", StringComparison.OrdinalIgnoreCase) || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] embedded = ExtractImageBytesFromObject(root);
                    if (embedded != null && embedded.Length > 0)
                    {
                        return embedded;
                    }

                    string outputUrl = ExtractAtlasOutputUrl(root);
                    if (string.IsNullOrWhiteSpace(outputUrl))
                    {
                        throw new InvalidOperationException("AtlasCloud prediction completed but returned no output URL. " + Shorten(response, 260));
                    }

                    return DownloadBytes(outputUrl);
                }

                if (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("AtlasCloud prediction failed: " + ExtractAtlasError(root));
                }

                Thread.Sleep(AtlasPollIntervalMs);
            }

            throw new TimeoutException("AtlasCloud prediction timed out after " + (AtlasPollTimeoutMs / 1000) + " seconds.");
        }

        private static string StoreGeneratedPortrait(
            string campaignId,
            string heroId,
            string characterObjectId,
            string cacheKey,
            string prompt,
            byte[] sourceImage,
            byte[] imageBytes,
            PortraitImageCallResult call,
            Dictionary<string, object> physicalConfidence,
            Dictionary<string, object> sourceProvenance,
            Dictionary<string, object> productReceipt,
            Dictionary<string, object> portraitInput,
            string generationOperationId,
            string generationStartedUtc,
            Dictionary<string, object> timing,
            long durationMs)
        {
            if (string.IsNullOrWhiteSpace(heroId))
            {
                heroId = ResolveHeroIdByCharacterObjectId(campaignId, characterObjectId);
            }

            if (string.IsNullOrWhiteSpace(heroId))
            {
                return "";
            }

            string portraitPath = CharacterFile(campaignId, heroId, "portraits", "portrait.png");
            string sourcePath = CharacterFile(campaignId, heroId, "portraits", "source.png");
            string promptPath = CharacterFile(campaignId, heroId, "portraits", "prompt.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(portraitPath));
            lock (FileLock)
            {
                File.WriteAllBytes(portraitPath, imageBytes);
                File.WriteAllBytes(sourcePath, sourceImage);
                File.WriteAllText(promptPath, prompt ?? "", Encoding.UTF8);
                WriteJsonObject(CharacterFile(campaignId, heroId, "portraits", "portrait.json"), new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["heroStringId"] = heroId,
                    ["characterObjectId"] = characterObjectId ?? "",
                    ["cacheKey"] = cacheKey ?? "",
                    ["generationOperationId"] = generationOperationId ?? "",
                    ["generationStartedUtc"] = generationStartedUtc ?? "",
                    ["generationStatus"] = "completed",
                    ["durationMs"] = durationMs,
                    ["timing"] = timing ?? new Dictionary<string, object>(),
                    ["provider"] = call.Provider,
                    ["model"] = call.Model,
                    ["adapter"] = call.Adapter,
                    ["sourceProvenance"] = sourceProvenance ?? new Dictionary<string, object>(),
                    ["portraitInput"] = portraitInput ?? new Dictionary<string, object>(),
                    ["productReceipt"] = productReceipt ?? new Dictionary<string, object>(),
                    ["generationStages"] = productReceipt != null && productReceipt.TryGetValue("generationStages", out object stages) ? stages : new object[0],
                    ["warning"] = ReadString(productReceipt, "warning", ""),
                    ["physicalConfidence"] = physicalConfidence ?? new Dictionary<string, object>(),
                    ["storedPath"] = portraitPath,
                    ["updatedUtc"] = DateTime.UtcNow.ToString("o")
                });
            }

            return portraitPath;
        }

        private static void LogPortraitImageCall(string campaignId, ImageGenerationProfile imageProfile, PortraitImageCallResult call, int promptChars, int sourceBytes, bool ok, Dictionary<string, object> timing)
        {
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["ok"] = ok,
                ["promptPurpose"] = imageProfile.PromptPurpose,
                ["imageProfile"] = imageProfile.Name,
                ["provider"] = imageProfile.Provider,
                ["model"] = call.Model ?? "",
                ["adapter"] = call.Adapter ?? "",
                ["durationMs"] = call.DurationMs,
                ["requestChars"] = call.RequestChars,
                ["responseChars"] = call.ResponseChars,
                ["promptChars"] = promptChars,
                ["sourceBytes"] = sourceBytes,
                ["imageBytes"] = call.ImageBytes == null ? 0 : call.ImageBytes.Length,
                ["timing"] = timing ?? new Dictionary<string, object>()
            };
            if (!string.IsNullOrWhiteSpace(call.Error))
            {
                row["error"] = call.Error;
            }

            LogOperational(ok ? "portrait.generate" : "portrait.generate_failed", row);
            Dictionary<string, object> settings = LoadSettings();
            if (!ReadBool(settings, "enableLlmLogging", true))
            {
                return;
            }

            lock (FileLock)
            {
                AppendBoundedJsonLineToPathLocked(Path.Combine(LogsDir, "llm-log.jsonl"), new Dictionary<string, object>
                {
                    ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["ok"] = ok,
                    ["requestType"] = imageProfile.Name + "_image",
                    ["promptPurpose"] = imageProfile.PromptPurpose,
                    ["imageProfile"] = imageProfile.Name,
                    ["provider"] = imageProfile.Provider,
                    ["model"] = call.Model ?? "",
                    ["adapter"] = call.Adapter ?? "",
                    ["durationMs"] = call.DurationMs,
                    ["requestChars"] = call.RequestChars,
                    ["responseChars"] = call.ResponseChars,
                    ["error"] = call.Error ?? "",
                    ["request"] = new Dictionary<string, object>
                    {
                        ["promptChars"] = promptChars,
                        ["sourceBytes"] = sourceBytes,
                        ["imageData"] = "[omitted]"
                    },
                    ["response"] = new Dictionary<string, object>
                    {
                        ["imageBytes"] = call.ImageBytes == null ? 0 : call.ImageBytes.Length
                    }
                }, 16L * 1024L * 1024L, 8L * 1024L * 1024L);
            }
        }

        private static byte[] ExtractImageBytesFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            object root = Json.DeserializeObject(json);
            return ExtractImageBytesFromObject(root);
        }

        private static byte[] ExtractImageBytesFromObject(object root)
        {
            string b64 = FindFirstPropertyString(root, "b64_json");
            if (!string.IsNullOrWhiteSpace(b64) && TryDecodeBase64OrDataUrl(b64, out byte[] b64Bytes))
            {
                return b64Bytes;
            }

            string dataUrl = FindDataImageUrl(root);
            if (!string.IsNullOrWhiteSpace(dataUrl) && TryDecodeDataUrl(dataUrl, out byte[] dataBytes))
            {
                return dataBytes;
            }

            string url = FindFirstImageUrl(root);
            return string.IsNullOrWhiteSpace(url) ? null : DownloadBytes(url);
        }

        private static byte[] DecodeImageBase64(string value)
        {
            if (TryDecodeBase64OrDataUrl(value, out byte[] bytes))
            {
                return bytes;
            }

            return null;
        }

        private static bool TryDecodeBase64OrDataUrl(string text, out byte[] bytes)
        {
            if (TryDecodeDataUrl(text, out bytes))
            {
                return true;
            }

            bytes = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                bytes = Convert.FromBase64String(text.Trim());
                return true;
            }
            catch
            {
                bytes = null;
                return false;
            }
        }

        private static bool TryDecodeDataUrl(string dataUrl, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrWhiteSpace(dataUrl))
            {
                return false;
            }

            int comma = dataUrl.IndexOf(',');
            if (comma < 0 || dataUrl.IndexOf("base64", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            try
            {
                bytes = Convert.FromBase64String(dataUrl.Substring(comma + 1));
                return true;
            }
            catch
            {
                bytes = null;
                return false;
            }
        }

        private static string FindFirstPropertyString(object token, string propertyName)
        {
            if (token == null)
            {
                return null;
            }

            Dictionary<string, object> dict = token as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (KeyValuePair<string, object> pair in dict)
                {
                    if (pair.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        string direct = pair.Value as string;
                        if (!string.IsNullOrWhiteSpace(direct))
                        {
                            return direct;
                        }

                        string nestedUrl = FindFirstPropertyString(pair.Value, "url");
                        if (!string.IsNullOrWhiteSpace(nestedUrl))
                        {
                            return nestedUrl;
                        }
                    }
                }

                foreach (KeyValuePair<string, object> pair in dict)
                {
                    string found = FindFirstPropertyString(pair.Value, propertyName);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }
            }

            IList list = token as IList;
            if (list != null)
            {
                foreach (object item in list)
                {
                    string found = FindFirstPropertyString(item, propertyName);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }
            }

            return null;
        }

        private static string FindDataImageUrl(object token)
        {
            foreach (string text in EnumerateStringValues(token))
            {
                Match match = Regex.Match(text, @"data:image/(?:png|jpeg|jpg|webp);base64,[A-Za-z0-9+/=]+", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Value;
                }
            }

            return null;
        }

        private static string FindFirstImageUrl(object token)
        {
            string explicitUrl = FindFirstPropertyString(token, "url");
            if (IsHttpUrl(explicitUrl))
            {
                return explicitUrl;
            }

            string imageUrl = FindFirstPropertyString(token, "image_url");
            if (IsHttpUrl(imageUrl))
            {
                return imageUrl;
            }

            foreach (string text in EnumerateStringValues(token))
            {
                Match match = Regex.Match(text, @"https?://[^\s""')>]+", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Value;
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateStringValues(object token)
        {
            if (token == null)
            {
                yield break;
            }

            string text = token as string;
            if (text != null)
            {
                yield return text;
                yield break;
            }

            Dictionary<string, object> dict = token as Dictionary<string, object>;
            if (dict != null)
            {
                foreach (KeyValuePair<string, object> pair in dict)
                {
                    foreach (string value in EnumerateStringValues(pair.Value))
                    {
                        yield return value;
                    }
                }
            }

            IList list = token as IList;
            if (list != null)
            {
                foreach (object item in list)
                {
                    foreach (string value in EnumerateStringValues(item))
                    {
                        yield return value;
                    }
                }
            }
        }

        private static string ReadWebResponseText(HttpWebRequest request)
        {
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream responseStream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(responseStream ?? Stream.Null, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                string body = "";
                if (ex.Response != null)
                {
                    using (Stream responseStream = ex.Response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(responseStream ?? Stream.Null, Encoding.UTF8))
                    {
                        body = reader.ReadToEnd();
                    }
                }

                throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? ex.Message : body, ex);
            }
        }

        private static string GetTextWithBearer(string url, string apiKey)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = "application/json";
            request.Timeout = 360000;
            request.Headers["Authorization"] = "Bearer " + apiKey;
            return ReadWebResponseText(request);
        }

        private static byte[] DownloadBytes(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 360000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream responseStream = response.GetResponseStream())
            using (MemoryStream memory = new MemoryStream())
            {
                (responseStream ?? Stream.Null).CopyTo(memory);
                return memory.ToArray();
            }
        }

        private static string ExtractAtlasPredictionId(string json)
        {
            object root = Json.DeserializeObject(json);
            return FirstNonEmpty(
                ReadPathString(root, "data", "id"),
                ReadPathString(root, "id"),
                FindFirstPropertyString(root, "predictionId"),
                FindFirstPropertyString(root, "prediction_id"));
        }

        private static string ExtractAtlasUploadUrl(string json)
        {
            object root = Json.DeserializeObject(json);
            string url = FirstNonEmpty(
                ReadPathString(root, "data", "download_url"),
                ReadPathString(root, "data", "url"),
                ReadPathString(root, "download_url"),
                ReadPathString(root, "url"));
            return IsHttpUrl(url) ? url : FindFirstImageUrl(root);
        }

        private static string ExtractAtlasOutputUrl(object root)
        {
            object token = ReadPathObject(root, "data", "outputs", "0")
                ?? ReadPathObject(root, "outputs", "0")
                ?? ReadPathObject(root, "data", "output")
                ?? ReadPathObject(root, "output");
            string text = token as string;
            if (IsHttpUrl(text))
            {
                return text;
            }

            return FirstNonEmpty(FindFirstImageUrl(token), FindFirstImageUrl(root));
        }

        private static string ExtractAtlasError(object root)
        {
            return FirstNonEmpty(
                ReadPathString(root, "data", "error"),
                ReadPathString(root, "error"),
                ReadPathString(root, "data", "message"),
                ReadPathString(root, "message"),
                "unknown error");
        }

        private static string ReadPathString(object root, params string[] path)
        {
            object value = ReadPathObject(root, path);
            return value == null ? "" : Convert.ToString(value);
        }

        private static object ReadPathObject(object root, params string[] path)
        {
            object current = root;
            foreach (string segment in path)
            {
                if (current == null)
                {
                    return null;
                }

                Dictionary<string, object> dict = current as Dictionary<string, object>;
                if (dict != null)
                {
                    if (!dict.TryGetValue(segment, out current))
                    {
                        return null;
                    }
                    continue;
                }

                IList list = current as IList;
                if (list != null && int.TryParse(segment, out int index) && index >= 0 && index < list.Count)
                {
                    current = list[index];
                    continue;
                }

                return null;
            }

            return current;
        }

        private static string NormalizeImageProvider(string provider)
        {
            if (string.Equals(provider, "OpenRouter", StringComparison.OrdinalIgnoreCase)) return "OpenRouter";
            if (IsCodexImageProvider(provider)) return CodexImageProvider;
            string value = (provider ?? "").Replace(" ", "").Replace("-", "").Trim();
            if (value.Equals("AtlasCloud", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Atlas", StringComparison.OrdinalIgnoreCase))
            {
                return "AtlasCloud";
            }
            if (IsRetiredImageProvider(provider)) return "AtlasCloud";
            return "NanoGPT";
        }

        private static bool IsAtlasProvider(string provider)
        {
            return NormalizeImageProvider(provider).Equals("AtlasCloud", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHttpUrl(string url)
        {
            return !string.IsNullOrWhiteSpace(url)
                && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return "";
        }

        private static bool UsesImageReferenceAdapter(string model)
        {
            return !string.IsNullOrWhiteSpace(model) && model.Trim().StartsWith("gpt-image-", StringComparison.OrdinalIgnoreCase);
        }

        private static bool UsesGeminiChatImageAdapter(string model)
        {
            return !string.IsNullOrWhiteSpace(model) && model.Trim().Equals("gemini-3-pro-image-preview", StringComparison.OrdinalIgnoreCase);
        }

        private static bool AtlasModelUsesImageUrlsArray(string model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return true;
            }

            string trimmed = model.Trim();
            return trimmed.IndexOf("grok-imagine", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("reference", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.EndsWith("/edit", StringComparison.OrdinalIgnoreCase);
        }

        private static bool UsesAtlasFluxKontextDev(string model)
        {
            return !string.IsNullOrWhiteSpace(model) && model.Trim().Equals(AtlasModelFluxKontextDev, StringComparison.OrdinalIgnoreCase);
        }

        private static bool UsesAtlasSeedreamEdit(string model)
        {
            return !string.IsNullOrWhiteSpace(model) && model.Trim().Equals(AtlasModelSeedreamEdit, StringComparison.OrdinalIgnoreCase);
        }

        private static bool UsesAtlasSeedreamV5ProEdit(string model)
        {
            return !string.IsNullOrWhiteSpace(model) && model.Trim().Equals(AtlasModelSeedreamV5ProEdit, StringComparison.OrdinalIgnoreCase);
        }

        private static bool UsesAtlasWan25ImageEdit(string model)
        {
            return !string.IsNullOrWhiteSpace(model) && model.Trim().Equals(AtlasModelWan25ImageEdit, StringComparison.OrdinalIgnoreCase);
        }

        private static bool UsesAtlasWan27ImageEdit(string model)
        {
            return !string.IsNullOrWhiteSpace(model) && model.Trim().Equals(AtlasModelWan27ImageEdit, StringComparison.OrdinalIgnoreCase);
        }

        private static bool UsesAtlasGrokImagineImageEdit(string model)
        {
            return !string.IsNullOrWhiteSpace(model) && model.Trim().Equals(AtlasModelGrokImagineImageEdit, StringComparison.OrdinalIgnoreCase);
        }

        private static string NanoFluxSize(string outputSizeOverride)
        {
            return string.IsNullOrWhiteSpace(outputSizeOverride) ? "768x1024" : outputSizeOverride.Trim();
        }

        private static string NanoGptReferenceSize(string outputSizeOverride)
        {
            return string.IsNullOrWhiteSpace(outputSizeOverride)
                ? "1024x1536"
                : MapToGptImageSize(outputSizeOverride);
        }

        private static string AtlasPresetSourceSize(string outputSizeOverride)
        {
            return string.IsNullOrWhiteSpace(outputSizeOverride) ? "768x1024" : outputSizeOverride.Trim();
        }

        private static string MapToGptImageSize(string outputSize)
        {
            if (!TryParseSize(outputSize, out int width, out int height))
            {
                return "1024x1536";
            }

            if (width == height)
            {
                return "1024x1024";
            }

            return width > height ? "1536x1024" : "1024x1536";
        }

        private static string MapToAtlasAspectRatio(string outputSize)
        {
            if (!TryParseSize(outputSize, out int width, out int height))
            {
                return "3:4";
            }

            float target = (float)width / height;
            string[] names = { "2:1", "16:9", "3:2", "4:3", "1:1", "3:4", "2:3", "9:16", "1:2" };
            float[] ratios = { 2f, 16f / 9f, 1.5f, 4f / 3f, 1f, 0.75f, 2f / 3f, 9f / 16f, 0.5f };
            return ClosestRatioName(target, names, ratios);
        }

        private static string MapToAtlasWanSize(string outputSize)
        {
            return TryParseSize(outputSize, out int width, out int height) && Math.Max(width, height) <= 1024 ? "1K" : "2K";
        }

        private static string MapToAtlasWan25Size(string outputSize)
        {
            if (!TryParseSize(outputSize, out int width, out int height))
            {
                return "768*1024";
            }

            string[] names =
            {
                "576*1344", "720*1280", "720*1680", "768*1024", "800*1200", "816*1904",
                "936*1664", "960*1280", "960*1440", "1024*768", "1024*1024", "1040*1560",
                "1104*1472", "1200*800", "1280*720", "1280*960", "1280*1280", "1344*576",
                "1440*960", "1472*1104", "1560*1040", "1664*936", "1680*720", "1904*816"
            };
            float[] ratios =
            {
                576f / 1344f, 720f / 1280f, 720f / 1680f, 768f / 1024f, 800f / 1200f, 816f / 1904f,
                936f / 1664f, 960f / 1280f, 960f / 1440f, 1024f / 768f, 1f, 1040f / 1560f,
                1104f / 1472f, 1200f / 800f, 1280f / 720f, 1280f / 960f, 1f, 1344f / 576f,
                1440f / 960f, 1472f / 1104f, 1560f / 1040f, 1664f / 936f, 1680f / 720f, 1904f / 816f
            };
            return ClosestRatioName((float)width / height, names, ratios);
        }

        private static string MapToAtlasSeedreamSize(string outputSize)
        {
            if (!TryParseSize(outputSize, out int width, out int height))
            {
                return "2048*2048";
            }

            if (width == height)
            {
                return "2048*2048";
            }

            float target = (float)width / height;
            string[] names = { "3136*1344", "2848*1600", "2496*1664", "2304*1728", "1728*2304", "1664*2496", "1600*2848" };
            float[] ratios = { 3136f / 1344f, 2848f / 1600f, 2496f / 1664f, 2304f / 1728f, 1728f / 2304f, 1664f / 2496f, 1600f / 2848f };
            return ClosestRatioName(target, names, ratios);
        }

        private static string MapToAtlasSeedreamV5Size(string outputSize)
        {
            if (TryParseSize(outputSize, out int width, out int height) && height > width)
            {
                return AtlasSeedreamV5ProPortraitSize;
            }

            return AtlasSeedreamV5ProLandscapeSize;
        }

        private static string ClosestRatioName(float target, string[] names, float[] ratios)
        {
            int bestIndex = 0;
            float bestDistance = Math.Abs(target - ratios[0]);
            for (int i = 1; i < ratios.Length; i++)
            {
                float distance = Math.Abs(target - ratios[i]);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            return names[bestIndex];
        }

        private static int GetAtlasFluxKontextWidth(string outputSize)
        {
            return TryParseSize(outputSize, out int width, out int height) ? width : 1024;
        }

        private static int GetAtlasFluxKontextHeight(string outputSize)
        {
            return TryParseSize(outputSize, out int width, out int height) ? height : 1024;
        }

        private static bool TryParseSize(string size, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrWhiteSpace(size))
            {
                return false;
            }

            string[] parts = size.ToLowerInvariant().Split('x');
            return parts.Length == 2
                && int.TryParse(parts[0], out width)
                && int.TryParse(parts[1], out height)
                && width > 0
                && height > 0;
        }

        private static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "";
            }

            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= maxLength ? text : text.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }

        private sealed class PortraitImageCallResult
        {
            public bool Ok;
            public string Provider = "";
            public string Model = "";
            public string Adapter = "";
            public byte[] ImageBytes;
            public string Error = "";
            public long DurationMs;
            public int RequestChars;
            public int ResponseChars;

            public static PortraitImageCallResult Success(string provider, string model, string adapter, byte[] imageBytes, long durationMs, int requestChars, int responseChars)
            {
                return new PortraitImageCallResult
                {
                    Ok = true,
                    Provider = provider ?? "",
                    Model = model ?? "",
                    Adapter = adapter ?? "",
                    ImageBytes = imageBytes,
                    DurationMs = durationMs,
                    RequestChars = requestChars,
                    ResponseChars = responseChars
                };
            }

            public static PortraitImageCallResult Fail(string provider, string model, string error, long durationMs = 0, int requestChars = 0, int responseChars = 0)
            {
                return new PortraitImageCallResult
                {
                    Ok = false,
                    Provider = provider ?? "",
                    Model = model ?? "",
                    Error = error ?? "Portrait image generation failed.",
                    DurationMs = durationMs,
                    RequestChars = requestChars,
                    ResponseChars = responseChars
                };
            }
        }
    }
}
