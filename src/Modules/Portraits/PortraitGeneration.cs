using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AIPortraits;

namespace ReignBetaServer
{
    internal static partial class PortraitsService
    {
        internal const string NanoGptImageGenerationsUrl = "https://nano-gpt.com/api/v1/images/generations";
        internal const string NanoGptChatCompletionsUrl = "https://nano-gpt.com/api/v1/chat/completions";
        private const string AtlasGenerateImageUrl = "https://api.atlascloud.ai/api/v1/model/generateImage";
        private const string AtlasUploadMediaUrl = "https://api.atlascloud.ai/api/v1/model/uploadMedia";
        private const string AtlasPredictionUrlPrefix = "https://api.atlascloud.ai/api/v1/model/prediction/";
        private const string AtlasModelFluxKontextDev = "black-forest-labs/flux-kontext-dev";
        private const string AtlasModelSeedreamEdit = "bytedance/seedream-v4.5/edit";
        internal const string AtlasModelSeedreamV5ProEdit = "bytedance/seedream-v5.0-pro/edit";
        private const string AtlasSeedreamV5ProLandscapeSize = "2720*1530";
        private const string AtlasSeedreamV5ProPortraitSize = "1530*2720";
        private const string AtlasModelWan25ImageEdit = "alibaba/wan-2.5/image-edit";
        private const string AtlasModelWan27ImageEdit = "alibaba/wan-2.7-pro/image-edit";
        private const string AtlasModelGrokImagineImageEdit = "xai/grok-imagine-image/edit";
        internal const string DefaultAtlasImageModel = AtlasModelGrokImagineImageEdit;
        internal const string DefaultAtlasWan25NegativePrompt = "illustration, painting, oil painting, watercolor, drawing, sketch, concept art, digital art, fantasy art, graphic novel, comic, cartoon, anime, cel shading, stylized image, 3D render, CGI, video game graphics, game screenshot, game model, synthetic person, doll, mannequin, wax figure, plastic skin, waxy skin, airbrushed skin, over-smoothed skin, artificial eyes, uncanny face, distorted anatomy, malformed hands, extra fingers, text, words, letters, numbers, captions, labels, names, ages, cultures, subtitles, signatures, logos, watermarks, interface elements, borders, banners, white strips, footer panels, metadata panels, armor, weapons, helmets, chainmail, modern clothing";
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

        internal sealed class ImageGenerationProfile
        {
            public bool ChatView;
            public string Name;
            public string PromptPurpose;
            public string Provider;
            public string NanoGptModel;
            public LocalModelImageOptions LocalModel;
            public string OpenRouterModel;
            public string AtlasModel;
            public string AtlasWanNegativePrompt;
            public double ImageStrength;
            public int InferenceSteps;
            public double GuidanceScale;
        }

        internal static string ImageGenerationProfileName(string promptPurpose)
        {
            if (string.Equals(promptPurpose, "adultPortrait", StringComparison.OrdinalIgnoreCase)) return "adultPortrait";
            if (string.Equals(promptPurpose, "adultScenery", StringComparison.OrdinalIgnoreCase)) return "adultScenery";
            return string.IsNullOrWhiteSpace(promptPurpose)
                || string.Equals(promptPurpose, "portrait", StringComparison.OrdinalIgnoreCase)
                ? "portrait"
                : "scenery";
        }

        internal static ImageGenerationProfile ResolveImageGenerationProfile(
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
                LocalModel = ReadLocalModelImageOptions(settings, prefix),
                AtlasModel = readString("AtlasImageModel", name.StartsWith("adult", StringComparison.Ordinal) ? AtlasModelSeedreamV5ProEdit : DefaultAtlasImageModel),
                AtlasWanNegativePrompt = readString("AtlasWanNegativePrompt", DefaultAtlasWan25NegativePrompt),
                ImageStrength = DialogueService.ClampDouble(readDouble("ImageStrength", 0.75d), 0.4d, 1d),
                InferenceSteps = Math.Max(10, Math.Min(50, readInt("InferenceSteps", 28))),
                GuidanceScale = DialogueService.ClampDouble(readDouble("GuidanceScale", 3.5d), 1d, 10d)
            };
        }

        private static readonly ConcurrentDictionary<string, Lazy<Dictionary<string, object>>> ActivePortraitRequests =
            new ConcurrentDictionary<string, Lazy<Dictionary<string, object>>>(StringComparer.Ordinal);

        internal static Dictionary<string, object> PortraitGenerate(Dictionary<string, object> payload)
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
            payload.Remove("resolvedPortraitVision");
            Dictionary<string, object> settings = SettingsService.LoadSettings();
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
            string refreshMode = "";
            bool reflectYears = false;
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
            Dictionary<string, object> visionEvidence = null;
            bool useLocalText = false;

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
                refreshMode = PortraitRefreshMode(payload);
                PortraitRefreshBaseline refreshBaseline = null;
                if (refreshMode.Length != 0)
                {
                    if (string.IsNullOrWhiteSpace(heroId)) throw new InvalidDataException("A character identity is required.");
                    refreshBaseline = CapturePortraitRefreshBaseline(CampaignFileService.CharacterFile(campaignId, heroId, "portraits"));
                }
                reflectYears = refreshMode == "reflect_years";
                if (reflectYears)
                {
                    imageProfile = ResolveImageGenerationProfile(settings, "adultPortrait");
                    imageProfile.AtlasWanNegativePrompt = "text, watermark, captions, border, distorted anatomy";
                    adultClothingEdit = false;
                }
                Stopwatch phase = Stopwatch.StartNew();
                byte[] sourceImage = IsCharacterPortraitProfile(imageProfile.Name) && !ReadBool(payload, "sharedCacheOutput", false)
                    ? null : DecodeImageBase64(sourceBase64);
                phase.Stop();
                timing["decodeMs"] = phase.ElapsedMilliseconds;

                Dictionary<string, object> sourceProvenance = new Dictionary<string, object>();
                phase.Restart();
                sourceImage = reflectYears
                    ? PortraitReflectionSource(payload, out sourceProvenance)
                    : ResolveImageRequestSource(imageProfile.Name, sourceImage,
                        () => ResolvePortraitGenerationSource(settings, payload, sourceImage, out sourceProvenance));
                if (reflectYears)
                {
                    prompt = PortraitReflectionPrompt(ReadInt(sourceProvenance, "currentAgeYears", 0));
                    outputSizeOverride = ReadInt(sourceProvenance, "width", 0) + "x" + ReadInt(sourceProvenance, "height", 0);
                }
                else if (IsCharacterPortraitProfile(imageProfile.Name))
                {
                    phase.Stop();
                    timing["nativeSourceMs"] = phase.ElapsedMilliseconds;
                    var nativePhysique = ValidateNativePhysique(ReadDictionary(sourceProvenance, "physique"), sourceImage);
                    payload["resolvedPortraitPhysique"] = BuildPortraitPhysiqueEvidence(
                        nativePhysique,
                        PromptService.LoadPromptTemplate(PortraitBodyPromptFile),
                        IsFemalePortraitSubject(payload));
                    bool sharedRebuild = ReadString(sourceProvenance, "kind", "") == "shared_ai_portrait_reference";
                    if (PreserveResidentClothing(payload))
                    {
                        // Tavern staff retain their native outfit for the identity pass, then receive
                        // the editable Tavern clothing edit through the configured adult portrait profile.
                        if (!IsTavernHousePortrait(payload)) adultClothingEdit = false;
                        // Even custom negative prompts must not silently strip the encountered outfit.
                        imageProfile.AtlasWanNegativePrompt = "text, watermark, captions, border, cartoon, plastic skin, distorted anatomy";
                        sourceProvenance["clothingPolicy"] = ReignBeta.Shared.Characters.EncounteredResidentRules.EquippedOutfitPolicy;
                    }
                    if (sharedRebuild) {
                        adultClothingEdit = false; // Preserve the existing outfit; no second clothing redesign.
                        if (imageProfile.AtlasWanNegativePrompt == DefaultAtlasWan25NegativePrompt)
                            imageProfile.AtlasWanNegativePrompt = DefaultAtlasWan25NegativePrompt.Replace(", armor, weapons, helmets, chainmail, modern clothing", "");
                    }
                    useLocalText = !sharedRebuild && imageProfile.Name == "portrait"
                        && PortraitGenerationMethod(settings) == LocalTextPortraitMethod;
                    if (useLocalText)
                    {
                        imageProfile = ResolveImageGenerationProfile(settings, promptPurpose, LocalModelImageProviderId);
                        adultClothingEdit = false;
                    }
                    else if (!sharedRebuild && imageProfile.Name == "portrait"
                        && imageProfile.Provider != LocalModelImageProviderId && PortraitGenerationMethod(settings) == "text")
                    {
                        phase.Restart();
                        visionEvidence = DescribePortraitSource(settings, sourceImage);
                        phase.Stop();
                        timing["visionMs"] = phase.ElapsedMilliseconds;
                        payload["resolvedPortraitVision"] = visionEvidence;
                        sourceProvenance["vision"] = visionEvidence;
                    }
                    prompt = useLocalText
                        ? BuildLocalTextPortraitTemplate(payload, clientPrompt)
                        : sharedRebuild
                            ? ApplyPortraitProductPromptContract(BuildSharedPortraitRebuildPrompt(payload))
                            : ApplyPortraitProductPromptContract(BuildPortraitPrompt(payload, clientPrompt, !adultClothingEdit));
                    if (imageProfile.Provider != LocalModelImageProviderId) outputSizeOverride = "768x1024";
                }

                if (string.IsNullOrWhiteSpace(prompt))
                {
                    return FailPortraitGenerationOperation(generationOperationId,
                        PortraitGenerateError(campaignId, imageProfile, "", "Prompt is empty.", timing, total));
                }

                phase.Restart();
                LocalTextPortraitResult localText = useLocalText ? GenerateLocalTextPortrait(imageProfile, sourceImage, prompt) : null;
                PortraitImageCallResult call = localText?.Normal ?? CallImageProvider(settings, imageProfile, prompt,
                    visionEvidence == null ? sourceImage : null, outputSizeOverride);
                phase.Stop();
                timing["providerCallMs"] = phase.ElapsedMilliseconds;
                if (localText?.Vision != null)
                {
                    prompt = localText.EffectivePrompt;
                    visionEvidence = localText.Vision;
                    sourceProvenance["vision"] = visionEvidence;
                    timing["visionMs"] = visionEvidence["durationMs"];
                    timing["imageMs"] = localText.ImageMs;
                }

                timing["totalMs"] = total.ElapsedMilliseconds;
                if (!call.Ok || call.ImageBytes == null || call.ImageBytes.Length == 0)
                {
                    LogPortraitImageCall(campaignId, imageProfile, call, prompt.Length, sourceImage?.Length ?? 0, false, timing);
                    return FailPortraitGenerationOperation(generationOperationId,
                        PortraitGenerateError(campaignId, imageProfile, call.Model, call.Error, timing, total, call));
                }

                PortraitProductValidation productValidation = null;
                if (IsCharacterPortraitProfile(imageProfile.Name))
                {
                    phase.Restart();
                    try
                    {
                        byte[] normalizedPortrait;
                        productValidation = reflectYears
                            ? ValidatePortraitReflectionProduct(sourceImage, call.ImageBytes, out normalizedPortrait)
                            : NormalizeAndValidateGeneratedPortraitProduct(call.ImageBytes, out normalizedPortrait);
                        call.ImageBytes = normalizedPortrait;
                    }
                    catch (Exception validationError)
                    {
                        phase.Stop();
                        timing["productValidationMs"] = phase.ElapsedMilliseconds;
                        LogPortraitImageCall(campaignId, imageProfile, call, prompt.Length, sourceImage?.Length ?? 0, false, timing);
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
                generationStages[0]["generationMethod"] = useLocalText ? LocalTextPortraitMethod
                    : visionEvidence == null && sourceImage != null ? "image" : "text";
                if (visionEvidence != null) generationStages[0]["vision"] = visionEvidence;
                string generationWarning = "";
                if (ReadDictionary(payload, "resolvedPortraitPhysique") != null)
                    generationStages[0]["physique"] = ReadDictionary(payload, "resolvedPortraitPhysique");
                if (adultClothingEdit && productValidation != null)
                {
                    LogPortraitImageCall(campaignId, imageProfile, call, prompt.Length, sourceImage?.Length ?? 0, true, timing);
                    ImageGenerationProfile adultProfile = ResolveImageGenerationProfile(settings, "adultPortrait");
                    PortraitProductValidation editedValidation = null;
                    PortraitClothingEditResult edit = RunPortraitClothingEdit(call, prompt, sourceImage, adultProfile,
                        () => BuildAdultPortraitClothingPrompt(payload),
                        (editPrompt, reference) => CallImageProvider(settings, adultProfile, editPrompt, reference,
                            adultProfile.Provider == LocalModelImageProviderId ? "" : "768x1024"),
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
                    OperationsService.LogOperational("portrait.clothing_edit", new Dictionary<string, object> {
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
                    productReceipt["portraitRefreshMode"] = refreshMode;
                    productReceipt["generationStages"] = generationStages;
                    productReceipt["warning"] = generationWarning;
                    productReceipt["physique"] = ReadDictionary(payload, "resolvedPortraitPhysique");
                }
                payload["generationOperationId"] = generationOperationId;
                payload["generationStartedUtc"] = generationStartedUtc;
                string StoreCharacterPortrait(Dictionary<string, object> refreshPublication)
                    => StoreGeneratedPortrait(campaignId, heroId, characterObjectId, cacheKey, prompt, sourceImage,
                        call.ImageBytes, call, physicalConfidence, sourceProvenance, productReceipt, portraitInput,
                        generationOperationId, generationStartedUtc, timing, total.ElapsedMilliseconds, refreshPublication);
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
                    : refreshBaseline == null
                        ? StoreCharacterPortrait(null)
                        : PublishPortraitRefresh(refreshBaseline, call.ImageBytes, productReceipt, StoreCharacterPortrait);
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

                response["portraitRefreshMode"] = refreshMode;
                response["characterName"] = ReadString(payload, "characterName", "");
                response["ageYears"] = ReadInt(payload, "ageYears", 0);
				response["clanTier"] = ReadInt(payload, "clanTier", 0);
				response["socialStation"] = ReadString(payload, "socialStation", "");
				response["occupation"] = ReadString(payload, "occupation", "");
				response["isNotable"] = ReadBool(payload, "isNotable", false);
				response["physicalConfidence"] = physicalConfidence;

                if (!adultClothingEdit)
                    LogPortraitImageCall(campaignId, imageProfile, call, prompt.Length, sourceImage?.Length ?? 0, true, timing);
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

        internal static Dictionary<string, object> PortraitGenerationStatus(Dictionary<string, object> payload)
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

            string portraitPath = CampaignFileService.CharacterFile(campaignId, heroId, "portraits", "portrait.png");
            string sourcePath = CampaignFileService.CharacterFile(campaignId, heroId, "portraits", "source.png");
            string promptPath = CampaignFileService.CharacterFile(campaignId, heroId, "portraits", "prompt.txt");
            string metadataPath = CampaignFileService.CharacterFile(campaignId, heroId, "portraits", "portrait.json");
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
            lock (Program.FileLock)
            {
                metadata = CampaignFileService.ReadJsonObject(metadataPath);
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
                if (metadata.TryGetValue(PortraitRefreshPublicationKey, out object publication)
                    && !PortraitRefreshPublicationMatches(Path.GetDirectoryName(portraitPath), portraitBytes, publication))
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["generationOperationId"] = operationId,
                        ["generationStatus"] = "failed",
                        ["error"] = "The stored portrait refresh is incomplete or has been superseded. Start a new refresh."
                    };
                }
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

        internal static string BuildPortraitPrompt(Dictionary<string, object> payload, string clientPrompt,
            bool includePhysicalConfidence = true, string residentClothingGuidance = null)
        {
            if (PreserveResidentClothing(payload)) return ReadDictionary(payload, "resolvedPortraitVision") == null
                ? BuildResidentPortraitPrompt(payload, residentClothingGuidance)
                : PortraitVisionIdentityPrompt(payload, residentClothingGuidance == null) + "\n"
                    + BuildResidentPortraitPrompt(payload, residentClothingGuidance)
                    .Replace("supplied native source image", "written identity and outfit description")
                    .Replace("in the source", "in the outfit description");
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
                string text = layer == "portrait_identity.txt" && ReadDictionary(payload, "resolvedPortraitVision") != null
                    ? PortraitVisionIdentityPrompt(payload) : PromptService.LoadPromptTemplate(layer).Trim();
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
            OperationsService.LogOperational("portrait.generate_failed", row);
            row["generationStages"] = new[] { new Dictionary<string, object> {
                ["profile"] = imageProfile == null ? "portrait" : imageProfile.Name,
                ["provider"] = imageProfile == null ? "" : imageProfile.Provider, ["model"] = model ?? "",
                ["outcome"] = "failed", ["durationMs"] = total == null ? 0 : total.ElapsedMilliseconds,
                ["error"] = row["error"] } };
            return row;
        }
    }
}
