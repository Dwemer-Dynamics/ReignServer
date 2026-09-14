using System.Numerics;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using TpacTool.Lib;

namespace Bannerlord.NativeCharacterImageGenerator;

internal sealed class TextureSampler
{
    private readonly byte[] _rgba;

    private TextureSampler(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        _rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }

    public static TextureSampler? TryCreate(Texture texture)
    {
        if (!texture.HasPixelData || texture.ResidentWidth <= 0 || texture.ResidentHeight <= 0)
        {
            return null;
        }

        var data = texture.TexturePixels?.Data.PrimaryRawImage;
        if (data is null || data.Length == 0)
        {
            return null;
        }

        try
        {
            if (texture.Format == TextureFormat.BC7)
            {
                var decoded = new BcDecoder().DecodeRaw(
                    data,
                    texture.ResidentWidth,
                    texture.ResidentHeight,
                    CompressionFormat.Bc7);
                var bc7Rgba = new byte[decoded.Length * 4];
                for (var index = 0; index < decoded.Length; index++)
                {
                    bc7Rgba[(index * 4) + 0] = decoded[index].r;
                    bc7Rgba[(index * 4) + 1] = decoded[index].g;
                    bc7Rgba[(index * 4) + 2] = decoded[index].b;
                    bc7Rgba[(index * 4) + 3] = decoded[index].a;
                }

                return new TextureSampler(texture.ResidentWidth, texture.ResidentHeight, bc7Rgba);
            }

#pragma warning disable CS0612
            var pixels = TextureUtil.DecodeTextureData(
                data,
                texture.ResidentWidth,
                texture.ResidentHeight,
                texture.Format);
#pragma warning restore CS0612
            var rgba = new byte[pixels.Length * 4];
            var byteScaled = texture.Format.IsBlockCompression();
            for (var index = 0; index < pixels.Length; index++)
            {
                var pixel = pixels[index];
                rgba[(index * 4) + 0] = ToByte(pixel.R, byteScaled);
                rgba[(index * 4) + 1] = ToByte(pixel.G, byteScaled);
                rgba[(index * 4) + 2] = ToByte(pixel.B, byteScaled);
                rgba[(index * 4) + 3] = ToByte(pixel.A, byteScaled);
            }

            return new TextureSampler(texture.ResidentWidth, texture.ResidentHeight, rgba);
        }
        catch (Exception exception) when (exception is FormatException or AggregateException or IndexOutOfRangeException)
        {
            // Unsupported compressed formats still render with a material fallback.
            return null;
        }
    }

    public Vector4 Sample(Vector2 uv)
    {
        var u = Wrap(uv.X);
        var v = Wrap(uv.Y);
        var x = u * (Width - 1);
        var y = (1f - v) * (Height - 1);
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var tx = x - x0;
        var ty = y - y0;

        var top = Vector4.Lerp(Read(x0, y0), Read(x1, y0), tx);
        var bottom = Vector4.Lerp(Read(x0, y1), Read(x1, y1), tx);
        return Vector4.Lerp(top, bottom, ty);
    }

    private Vector4 Read(int x, int y)
    {
        var index = ((y * Width) + x) * 4;
        return new Vector4(
            _rgba[index] / 255f,
            _rgba[index + 1] / 255f,
            _rgba[index + 2] / 255f,
            _rgba[index + 3] / 255f);
    }

    private static float Wrap(float value) => value - MathF.Floor(value);

    private static byte ToByte(float value, bool byteScaled) =>
        (byte)Math.Clamp((int)MathF.Round(byteScaled ? value : value * 255f), 0, 255);
}
