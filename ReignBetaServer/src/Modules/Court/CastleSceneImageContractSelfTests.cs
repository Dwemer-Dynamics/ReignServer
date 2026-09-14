using System;
using System.Collections.Generic;
using AIPortraits;

namespace ReignBetaServer
{
    internal static class CastleSceneImageContractSelfTests
    {
        internal static void Append(List<Dictionary<string, object>> results)
        {
            Action<string, bool, string, object> add = (id, passed, summary, data) =>
                results.Add(new Dictionary<string, object>
                {
                    { "ok", true }, { "passed", passed }, { "suite", "court_system" },
                    { "caseId", id }, { "name", id }, { "summary", summary },
                    { "data", data ?? new Dictionary<string, object>() }, { "durationMs", 0 }
                });

            int[,] dimensions =
            {
                { 320, 180 },
                { 300, 200 },
                { 240, 180 },
                { 200, 200 },
                { 180, 320 },
                { 400, 120 }
            };
            bool normalized = true;
            var observations = new List<object>();
            for (int index = 0; index < dimensions.GetLength(0); index++)
            {
                int width = dimensions[index, 0];
                int height = dimensions[index, 1];
                byte[] source = BuildOpaqueTestPng(width, height);
                byte[] output = Program.NormalizeCastleScene16By9(source, out int sourceWidth, out int sourceHeight);
                byte[] rgba = PngReencode.DecodeToRgba(output, out int outputWidth, out int outputHeight);
                bool opaque = rgba != null;
                if (opaque)
                    for (int pixel = 3; pixel < rgba.Length; pixel += 4)
                        if (rgba[pixel] != 255) { opaque = false; break; }
                bool passed = sourceWidth == width && sourceHeight == height
                    && outputWidth == 1536 && outputHeight == 864 && opaque;
                normalized &= passed;
                observations.Add(new Dictionary<string, object>
                {
                    { "source", width + "x" + height },
                    { "output", outputWidth + "x" + outputHeight },
                    { "opaque", opaque },
                    { "passed", passed }
                });
            }
            add("castle_scene_cross_provider_16x9_contract", normalized,
                "Landscape, square, portrait, and ultrawide provider images normalize to one opaque 1536x864 fill-crop contract without letterboxing.",
                observations);
        }

        private static byte[] BuildOpaqueTestPng(int width, int height)
        {
            byte[] rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * width + x) * 4;
                    rgba[offset] = (byte)(20 + (x * 200 / Math.Max(1, width - 1)));
                    rgba[offset + 1] = (byte)(20 + (y * 180 / Math.Max(1, height - 1)));
                    rgba[offset + 2] = 80;
                    rgba[offset + 3] = 255;
                }
            return PngEncoder.EncodeRgba(rgba, width, height);
        }
    }
}
