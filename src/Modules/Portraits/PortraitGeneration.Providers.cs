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
        private static PortraitImageCallResult GenerateNanoGptImage(
            Dictionary<string, object> settings,
            ImageGenerationProfile imageProfile,
            string prompt,
            byte[] sourceImagePng,
            string outputSizeOverride)
        {
            string apiKey = SettingsService.ProviderSecret(settings, "portraitNanoGptApiKey", ReadString(settings, "portraitNanoGptImageGenerationsUrl", NanoGptImageGenerationsUrl),
                ReadString(settings, "portraitNanoGptChatCompletionsUrl", NanoGptChatCompletionsUrl));
            string model = imageProfile.NanoGptModel;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return PortraitImageCallResult.Fail("NanoGPT", model, SettingsService.ProviderKeyUnavailable(settings, "portraitNanoGptApiKey", apiKey) ? SettingsService.GlobalKeyUnavailableError : "No NanoGPT image API key is set in the server AI Image Generation tab.");
            }

            if (UsesGeminiChatImageAdapter(model))
            {
                return GenerateNanoGptGeminiChatImage(settings, apiKey, model, prompt, sourceImagePng);
            }

            if (sourceImagePng == null) return PostImageJson(
                ReadString(settings, "portraitNanoGptImageGenerationsUrl", NanoGptImageGenerationsUrl), apiKey,
                BuildNanoPortraitTextPayload(model, prompt, outputSizeOverride), settings, "NanoGPT", model, "text image generation");

            if (UsesImageReferenceAdapter(model))
            {
                return GenerateNanoGptImageReference(settings, apiKey, model, prompt, sourceImagePng, outputSizeOverride, imageProfile.ChatView);
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
            string outputSizeOverride, bool chatView = false)
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
            if (chatView) { body["quality"] = "high"; body["input_fidelity"] = "high"; }
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
                    ["image_url"] = new Dictionary<string, object> { ["url"] = sourceImagePng == null ? "" : "data:image/png;base64," + Convert.ToBase64String(sourceImagePng) }
                }
            };
            if (sourceImagePng == null) content.RemoveAt(1);
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
            string apiKey = SettingsService.ProviderSecret(settings, "portraitAtlasApiKey", ReadString(settings, "portraitAtlasUploadMediaUrl", AtlasUploadMediaUrl),
                ReadString(settings, "portraitAtlasGenerateImageUrl", AtlasGenerateImageUrl), ReadString(settings, "portraitAtlasPredictionUrlPrefix", AtlasPredictionUrlPrefix));
            string model = imageProfile.AtlasModel;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return PortraitImageCallResult.Fail("AtlasCloud", model, SettingsService.ProviderKeyUnavailable(settings, "portraitAtlasApiKey", apiKey) ? SettingsService.GlobalKeyUnavailableError : "No AtlasCloud API key is set in the server AI Image Generation tab.");
            }

            Stopwatch total = Stopwatch.StartNew();
            int requestChars = 0;
            int responseChars = 0;
            try
            {
                string uploadUrl = ReadString(settings, "portraitAtlasUploadMediaUrl", AtlasUploadMediaUrl);
                int uploadResponseChars = 0;
                string sourceUrl = sourceImagePng == null ? "" : UploadAtlasMedia(uploadUrl, apiKey, sourceImagePng, out uploadResponseChars);
                responseChars += uploadResponseChars;
                if (sourceImagePng != null && string.IsNullOrWhiteSpace(sourceUrl))
                {
                    return PortraitImageCallResult.Fail("AtlasCloud", model, "AtlasCloud upload returned no source URL.");
                }

                string outputSize = AtlasPresetSourceSize(outputSizeOverride);
                Dictionary<string, object> body = sourceImagePng == null ? BuildAtlasPortraitTextPayload(model, prompt, outputSize, imageProfile.AtlasWanNegativePrompt) : BuildAtlasImagePayload(
                    model,
                    prompt,
                    sourceUrl,
                    outputSize,
                    imageProfile.AtlasWanNegativePrompt);
                if (imageProfile.ChatView) ApplyChatViewAtlasSize(body, imageProfile);
                model = ReadString(body, "model", model);
                string json = Json.Serialize(body);
                requestChars += json.Length;
                string generateUrl = ReadString(settings, "portraitAtlasGenerateImageUrl", AtlasGenerateImageUrl);
                string response = LlmProviderService.PostJsonToLlm(generateUrl, apiKey, json, settings);
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
                return LinuxImageCodec.Normalize(imageBytes);
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
                string response = LlmProviderService.PostJsonToLlm(url, apiKey, json, settings);
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
            Program.ProviderTestGuard.BeforeDispatch("atlas_reference_upload");
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
                heroId = DialogueService.ResolveHeroIdByCharacterObjectId(campaignId, characterObjectId);
            }

            if (string.IsNullOrWhiteSpace(heroId))
            {
                return "";
            }

            string portraitPath = CampaignFileService.CharacterFile(campaignId, heroId, "portraits", "portrait.png");
            string sourcePath = CampaignFileService.CharacterFile(campaignId, heroId, "portraits", "source.png");
            string promptPath = CampaignFileService.CharacterFile(campaignId, heroId, "portraits", "prompt.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(portraitPath));
            lock (Program.FileLock)
            {
                File.WriteAllBytes(portraitPath, imageBytes);
                File.WriteAllBytes(sourcePath, sourceImage);
                File.WriteAllText(promptPath, prompt ?? "", Encoding.UTF8);
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "portraits", "portrait.json"), new Dictionary<string, object>
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

            OperationsService.LogOperational(ok ? "portrait.generate" : "portrait.generate_failed", row);
            Dictionary<string, object> settings = SettingsService.LoadSettings();
            if (!ReadBool(settings, "enableLlmLogging", true))
            {
                return;
            }

            lock (Program.FileLock)
            {
                CampaignFileService.AppendBoundedJsonLineToPathLocked(Path.Combine(Program.LogsDir, "llm-log.jsonl"), new Dictionary<string, object>
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
            Program.ProviderTestGuard.BeforeDispatch("image_prediction_poll");
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = "application/json";
            request.Timeout = 360000;
            request.Headers["Authorization"] = "Bearer " + apiKey;
            return ReadWebResponseText(request);
        }

        private static byte[] DownloadBytes(string url)
        {
            Program.ProviderTestGuard.BeforeDispatch("image_download");
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

        internal static string NormalizeImageProvider(string provider)
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
