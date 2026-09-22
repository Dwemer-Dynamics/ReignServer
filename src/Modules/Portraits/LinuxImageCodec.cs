using System;
using System.IO;
using SkiaSharp;

namespace ReignBetaServer
{
    // Decode provider images on Linux without the Windows GDI+ dependency.
    internal static class LinuxImageCodec
    {
        internal static byte[] Normalize(byte[] bytes, bool requirePng = false)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Image exceeds the encoded size limit.");
            using (var data = SKData.CreateCopy(bytes))
            using (var codec = SKCodec.Create(data))
            {
                if (codec == null || codec.Info.Width < 1 || codec.Info.Height < 1
                    || codec.Info.Width > 8192 || codec.Info.Height > 8192
                    || (long)codec.Info.Width * codec.Info.Height > 33554432
                    || (requirePng && codec.EncodedFormat != SKEncodedImageFormat.Png))
                    throw new InvalidDataException("Invalid image format or dimensions.");
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using (var bitmap = new SKBitmap(info))
                {
                    if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
                        throw new InvalidDataException("Incomplete image data.");
                    using (var image = SKImage.FromBitmap(bitmap))
                    using (var png = image.Encode(SKEncodedImageFormat.Png, 100)) return png.ToArray();
                }
            }
        }

        // Existing provider contracts use deterministic offline image inputs on both platforms.
        internal static byte[] Fixture(int width, int height, bool jpeg = false)
        {
            using (var bitmap = new SKBitmap(width, height))
            {
                bitmap.Erase(new SKColor(30, 35, 40));
                using (var image = SKImage.FromBitmap(bitmap))
                using (var data = image.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 95))
                    return data.ToArray();
            }
        }
    }
}
