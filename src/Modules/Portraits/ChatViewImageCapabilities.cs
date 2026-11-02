using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AIPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Qualified transport controls, 2026-10-29. Atlas model markdown schemas and
        // OpenRouter /api/v1/images/models are the authority for reseller limits.
        private static Dictionary<string, object> ChatViewImageCapability(ImageGenerationProfile profile)
        {
            string model = ImageProfileModel(profile), size, resolution = "";
            bool runtime = IsCodexImageProvider(profile.Provider);
            if (runtime) size = "maximum-native-3:2";
            else if (profile.Provider == "OpenRouter")
            {
                if (model == "bytedance-seed/seedream-4.5") { size = "4992x3328"; resolution = "4K"; }
                else if (model == "google/gemini-3-pro-image-preview") { size = "5056x3392"; resolution = "4K"; }
                else if (model == "bytedance-seed/seedream-5-0-pro" || model == "x-ai/grok-imagine-image-quality") { size = "2496x1664"; resolution = "2K"; }
                else if (model == "openai/gpt-image-1") size = "1536x1024";
                else throw new InvalidOperationException("View has no qualified maximum-resolution reference route for this OpenRouter model.");
            }
            else if (IsAtlasProvider(profile.Provider))
            {
                if (UsesAtlasSeedreamEdit(model)) size = "4992x3328";
                else if (UsesAtlasSeedreamV5ProEdit(model)) size = "2496x1664";
                else if (UsesAtlasWan25ImageEdit(model)) size = "1560x1040";
                else if (UsesAtlasGrokImagineImageEdit(model)) { size = "2496x1664"; resolution = "2k"; }
                else throw new InvalidOperationException("This Atlas model has no qualified exact 3:2 View route. Choose a supported scenery image model.");
            }
            else if (model == "gpt-image-1.5" || model == "gpt-image-1") size = "1536x1024";
            else throw new InvalidOperationException("This NanoGPT model has no qualified maximum-resolution 3:2 View route. Choose a supported scenery image model.");
            return TestDict("provider", profile.Provider, "model", model, "size", size, "aspectRatio", "3:2", "resolution", resolution,
                "quality", "high", "maximumVerified", !runtime, "capabilityVersion", "2026-10-29",
                "maximumMode", runtime ? "runtime-managed; maximum native resolution requested, numerical maximum not exposed" : "qualified model/transport maximum");
        }

        private static void ApplyChatViewAtlasSize(Dictionary<string, object> body, ImageGenerationProfile profile)
        {
            var capability = ChatViewImageCapability(profile);
            string model = profile.AtlasModel, size = ReadString(capability, "size", "");
            if (UsesAtlasGrokImagineImageEdit(model))
            { body["aspect_ratio"] = "3:2"; body["resolution"] = ReadString(capability, "resolution", "2k"); }
            else body["size"] = size.Replace('x', '*');
            // Explicit photo inputs must not be expanded into invented narrative.
            if (body.ContainsKey("enable_prompt_expansion")) body["enable_prompt_expansion"] = false;
        }

        internal static byte[] NormalizeChatViewImage(byte[] png, Dictionary<string, object> capability, out int sourceWidth, out int sourceHeight)
        {
            // Resellers may return JPEG/WebP even for image APIs that normally return PNG.
            // The shared native codec checks encoded bytes and dimensions before decoding.
            if (png != null && png.Length >= 4 && !(png[0] == 137 && png[1] == 80 && png[2] == 78 && png[3] == 71))
                png = LinuxImageCodec.Normalize(png);
            ValidateChatViewPng(png);
            byte[] rgba = PngReencode.DecodeToRgba(png, out int width, out int height);
            sourceWidth = width; sourceHeight = height;
            if (rgba == null) throw new InvalidDataException("The View image could not be decoded.");
            if (TryParseSize(ReadString(capability, "size", ""), out int expectedWidth, out int expectedHeight)
                && (width < expectedWidth * .99 || height < expectedHeight * .99))
                throw new InvalidDataException("The provider returned less than its requested maximum native resolution. The original result is not being upscaled.");
            if (!ReadBool(capability, "maximumVerified", true) && (width < 1536 || height < 1024))
                throw new InvalidDataException("The runtime returned a low-resolution image despite the maximum-resolution request.");
            int unit = Math.Min(width / 3, height / 2), cropWidth = unit * 3, cropHeight = unit * 2;
            if (unit < 1 || 1d - (double)cropWidth * cropHeight / ((double)width * height) > .01)
                throw new InvalidDataException("The image is not 3:2. View refuses to stretch or substantially crop the scene.");
            if (cropWidth == width && cropHeight == height) return png;
            var crop = new byte[cropWidth * cropHeight * 4];
            int ox = (width - cropWidth) / 2, oy = (height - cropHeight) / 2;
            for (int y = 0; y < cropHeight; y++) Buffer.BlockCopy(rgba, ((oy + y) * width + ox) * 4, crop, y * cropWidth * 4, cropWidth * 4);
            return PngEncoder.EncodeRgba(crop, cropWidth, cropHeight);
        }

        private static void ValidateChatViewPng(byte[] png)
        {
            if (png == null || png.Length < 24 || png.Length > 64 * 1024 * 1024 || png[0] != 137 || png[1] != 80 || png[2] != 78 || png[3] != 71)
                throw new InvalidDataException("View requires a bounded valid PNG image.");
            int width = png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19];
            int height = png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23];
            if (width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > 33554432)
                throw new InvalidDataException("The View image exceeds the decoded pixel limit.");
        }
    }
}
