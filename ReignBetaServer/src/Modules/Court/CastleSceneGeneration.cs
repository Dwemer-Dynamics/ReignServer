using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AIPortraits;
using ReignPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string CastleSceneRenderContract = "castle_scene_16x9_room_attire_v3";
        private const int CastleSceneOutputWidth = 1536;
        private const int CastleSceneOutputHeight = 864;

        private static string CastleScenePromptPurpose(Dictionary<string, object> payload)
        {
            string room = ReadString(payload, "room", "").Replace("_", "").Replace(" ", "").ToLowerInvariant();
            return room == "baths" || room == "guestbedrooms" || room == "royalbedroom"
                ? "adultScenery" : "castle_scene";
        }

        private static Dictionary<string, object> CastleSceneGenerate(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string cacheKey = ReadString(payload, "cacheKey", "");
            string prompt = ReadString(payload, "prompt", "");
            string contactSheet = ReadFirstString(payload, "contactSheetBase64", "sourceImageBase64");
            bool courtAudience = ReadString(payload, "room", "") == "throne_petition";
            string renderContract = ReadString(payload, "renderContract", CastleSceneRenderContract);
            if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(contactSheet))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "cacheKey, prompt, and contactSheetBase64 are required." };

            string folder = Path.Combine(CampaignDirectory(campaignId), "castle_chat", SafePathSegment(timelineId, "main"));
            Directory.CreateDirectory(folder);
            string safeKey = SafePathSegment(cacheKey, "scene");
            string imagePath = Path.Combine(folder, safeKey + ".png");
            string manifestPath = Path.Combine(folder, safeKey + ".json");
            if (File.Exists(imagePath) && File.Exists(manifestPath))
            {
                byte[] cachedBytes = File.ReadAllBytes(imagePath);
                byte[] normalizedCached = NormalizeCastleScene16By9(cachedBytes, out int cachedSourceWidth, out int cachedSourceHeight);
                if (normalizedCached == null || normalizedCached.Length == 0)
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The cached castle scene could not be normalized to 16:9." };
                bool cacheRequiredNormalization = cachedSourceWidth != CastleSceneOutputWidth || cachedSourceHeight != CastleSceneOutputHeight;
                if (cacheRequiredNormalization)
                    WriteSharedPortraitAtomic(imagePath, normalizedCached);
                Dictionary<string, object> cachedManifest = ReadJsonObject(manifestPath) ?? new Dictionary<string, object>();
                cachedManifest["renderContract"] = renderContract;
                cachedManifest["sourceWidth"] = cachedSourceWidth;
                cachedManifest["sourceHeight"] = cachedSourceHeight;
                cachedManifest["outputWidth"] = CastleSceneOutputWidth;
                cachedManifest["outputHeight"] = CastleSceneOutputHeight;
                cachedManifest["normalization"] = "fill_center_crop";
                if (cacheRequiredNormalization) cachedManifest["normalizedUtc"] = DateTime.UtcNow.ToString("O");
                WriteSharedPortraitAtomic(manifestPath, Encoding.UTF8.GetBytes(Json.Serialize(cachedManifest)));
                return new Dictionary<string, object> { ["ok"] = true, ["cached"] = true, ["cacheKey"] = cacheKey,
                    ["renderContract"] = renderContract,
                    ["imageBase64"] = Convert.ToBase64String(normalizedCached), ["manifestPath"] = manifestPath };
            }

            Dictionary<string, object> imagePayload = new Dictionary<string, object>(payload, StringComparer.OrdinalIgnoreCase)
            {
                ["promptPurpose"] = CastleScenePromptPurpose(payload),
                ["sceneInterface"] = "castle_keep_location",
                ["sourceImageBase64"] = contactSheet,
                ["prompt"] = prompt + "\n\nCompose a true 16:9 landscape, first-person view "
                    + (courtAudience ? "from the ruler seated above the audience on the dais. " : "of what the ruler sees upon entering. ")
                    + "Include every referenced NPC, recognizable and in the supplied stable order. Keep every participant, face, and important gesture inside the central 80% safe area so provider-specific cropping cannot cut them off. Do not print names, captions, labels, borders, logos, or UI elements.",
                ["outputSize"] = "1536x864",
                ["sharedCacheOutput"] = false,
                ["cacheKey"] = "castle_scene_" + safeKey,
                ["heroStringId"] = "castle_scene"
            };
            // The provider receives the one composite under its canonical image field only.
            imagePayload.Remove("contactSheetBase64");
            // These rooms must use the configured Adult Scenery & Events
            // provider/model, never a caller override or the normal scene profile.
            if (CastleScenePromptPurpose(payload) == "adultScenery") imagePayload.Remove("provider");
            Dictionary<string, object> generated = PortraitGenerate(imagePayload);
            if (!ReadBool(generated, "ok", false))
            {
                LogOperational("castle_scene.generate_failed", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["timelineId"] = timelineId,
                    ["cacheKey"] = cacheKey,
                    ["room"] = ReadString(payload, "room", ""),
                    ["error"] = ReadString(generated, "error", "Image generation failed.")
                });
                return generated;
            }
            byte[] bytes = Convert.FromBase64String(ReadString(generated, "imageBase64", ""));
            bytes = NormalizeCastleScene16By9(bytes, out int sourceWidth, out int sourceHeight);
            if (bytes == null || bytes.Length == 0)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The generated castle scene could not be normalized to 16:9." };
            WriteSharedPortraitAtomic(imagePath, bytes);
            Dictionary<string, object> manifest = new Dictionary<string, object>
            {
                ["cacheKey"] = cacheKey, ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["settlementId"] = ReadString(payload, "settlementId", ""), ["room"] = ReadString(payload, "room", ""),
                ["timeBlock"] = ReadString(payload, "timeBlock", ""), ["promptRevision"] = ReadString(payload, "promptRevision", ""),
                ["orderedParticipants"] = ReadStringList(payload, "orderedParticipants"), ["createdUtc"] = DateTime.UtcNow.ToString("O"),
                ["renderContract"] = renderContract, ["sourceWidth"] = sourceWidth, ["sourceHeight"] = sourceHeight,
                ["inputImageCount"] = 1,
                ["outputWidth"] = CastleSceneOutputWidth, ["outputHeight"] = CastleSceneOutputHeight,
                ["normalization"] = "fill_center_crop"
            };
            WriteSharedPortraitAtomic(manifestPath, Encoding.UTF8.GetBytes(Json.Serialize(manifest)));
            generated["cacheKey"] = cacheKey; generated["manifestPath"] = manifestPath; generated["cached"] = false;
            generated["imageBase64"] = Convert.ToBase64String(bytes);
            LogOperational("castle_scene.generated", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["timelineId"] = timelineId,
                ["cacheKey"] = cacheKey,
                ["room"] = ReadString(payload, "room", ""),
                ["imageBytes"] = bytes.Length,
                ["manifestPath"] = manifestPath
            });
            return generated;
        }

        internal static byte[] NormalizeCastleScene16By9(byte[] png, out int sourceWidth, out int sourceHeight)
        {
            byte[] rgba = PngReencode.DecodeToRgba(png, out int width, out int height);
            sourceWidth = width;
            sourceHeight = height;
            if (rgba == null || width <= 0 || height <= 0) return null;
            int cropWidth = width;
            int cropHeight = (int)Math.Round(width * 9d / 16d);
            if (cropHeight > height)
            {
                cropHeight = height;
                cropWidth = (int)Math.Round(height * 16d / 9d);
            }
            cropWidth = Math.Max(1, Math.Min(width, cropWidth));
            cropHeight = Math.Max(1, Math.Min(height, cropHeight));
            int startX = (width - cropWidth) / 2;
            int startY = (height - cropHeight) / 2;
            byte[] crop = new byte[cropWidth * cropHeight * 4];
            for (int y = 0; y < cropHeight; y++)
                Buffer.BlockCopy(rgba, ((startY + y) * width + startX) * 4,
                    crop, y * cropWidth * 4, cropWidth * 4);
            byte[] resized = PortraitDerivativeCore.ResizeBilinear(crop, cropWidth, cropHeight, CastleSceneOutputWidth, CastleSceneOutputHeight);
            return PngEncoder.EncodeRgba(resized, CastleSceneOutputWidth, CastleSceneOutputHeight);
        }
    }
}
