#nullable disable // Also compiled by nullable-enabled offline tests; this legacy codec returns null on invalid input.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace AIPortraits;

public static class PngReencode
{
	public const int ImageEditMinimumDimension = 384;

	public const int ImageEditMaximumDimension = 5000;

	public const int ImageEditMaximumBytes = 10 * 1024 * 1024;

	public static byte[] ToPngEncoderFormat(byte[] png)
	{
		int width;
		int height;
		byte[] array = DecodeToRgba(png, out width, out height);
		if (array == null || width <= 0 || height <= 0)
		{
			return null;
		}
		return PngEncoder.EncodeRgba(array, width, height);
	}

	public static byte[] SwapRedBlueToPngEncoderFormat(byte[] png)
	{
		int width;
		int height;
		byte[] array = DecodeToRgba(png, out width, out height);
		if (array == null || width <= 0 || height <= 0)
		{
			return null;
		}
		for (int i = 0; i + 2 < array.Length; i += 4)
		{
			byte b = array[i];
			array[i] = array[i + 2];
			array[i + 2] = b;
		}
		return PngEncoder.EncodeRgba(array, width, height);
	}

	public static byte[] ToEngineTextureFormat(byte[] png, int maximumDimension)
	{
		int width;
		int height;
		byte[] rgba = DecodeToRgba(png, out width, out height);
		if (rgba == null || width <= 0 || height <= 0)
		{
			return null;
		}

		if (maximumDimension > 0 && Math.Max(width, height) > maximumDimension)
		{
			double scale = (double)maximumDimension / Math.Max(width, height);
			int targetWidth = Math.Max(1, (int)Math.Round(width * scale));
			int targetHeight = Math.Max(1, (int)Math.Round(height * scale));
			rgba = ResizeRgbaBilinear(rgba, width, height, targetWidth, targetHeight);
			width = targetWidth;
			height = targetHeight;
		}

		// Bannerlord's in-memory texture loader preserves channel order for RGBA PNGs.
		// Feeding it an opaque 24-bit RGB PNG makes the decoded surface arrive as BGR,
		// which swaps red and blue across event art and other dynamically loaded images.
		return PngEncoder.EncodeRgba(rgba, width, height);
	}

	public static byte[] NormalizeImageEditSource(byte[] png)
	{
		int width;
		int height;
		byte[] rgba = DecodeToRgba(png, out width, out height);
		if (rgba == null || width <= 0 || height <= 0)
		{
			return null;
		}

		double scale = 1.0;
		int shortest = Math.Min(width, height);
		int longest = Math.Max(width, height);
		if (shortest < ImageEditMinimumDimension)
		{
			scale = (double)ImageEditMinimumDimension / shortest;
		}
		if (longest * scale > ImageEditMaximumDimension)
		{
			scale = (double)ImageEditMaximumDimension / longest;
		}

		int targetWidth = Math.Max(1, (int)Math.Round(width * scale));
		int targetHeight = Math.Max(1, (int)Math.Round(height * scale));
		byte[] normalizedRgba = targetWidth == width && targetHeight == height
			? rgba
			: ResizeRgbaBilinear(rgba, width, height, targetWidth, targetHeight);
		if (normalizedRgba == null)
		{
			return null;
		}

		byte[] rgb = CompositeToOpaqueRgb(normalizedRgba, targetWidth, targetHeight);
		byte[] encoded = PngEncoder.EncodeRgb(rgb, targetWidth, targetHeight);
		if (encoded == null || encoded.Length > ImageEditMaximumBytes)
		{
			Debug.Print("[AIPortraits] Normalized image-edit source exceeds the 10 MB provider limit.");
			return null;
		}
		return encoded;
	}

	private static byte[] CompositeToOpaqueRgb(byte[] rgba, int width, int height)
	{
		byte[] rgb = new byte[width * height * 3];
		int source = 0;
		int destination = 0;
		while (source + 3 < rgba.Length && destination + 2 < rgb.Length)
		{
			int alpha = rgba[source + 3];
			rgb[destination] = (byte)((rgba[source] * alpha + 127) / 255);
			rgb[destination + 1] = (byte)((rgba[source + 1] * alpha + 127) / 255);
			rgb[destination + 2] = (byte)((rgba[source + 2] * alpha + 127) / 255);
			source += 4;
			destination += 3;
		}
		return rgb;
	}

	private static byte[] ResizeRgbaBilinear(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
	{
		byte[] target = new byte[targetWidth * targetHeight * 4];
		double xScale = targetWidth > 1 ? (double)(sourceWidth - 1) / (targetWidth - 1) : 0.0;
		double yScale = targetHeight > 1 ? (double)(sourceHeight - 1) / (targetHeight - 1) : 0.0;
		for (int y = 0; y < targetHeight; y++)
		{
			double sourceY = y * yScale;
			int y0 = (int)sourceY;
			int y1 = Math.Min(y0 + 1, sourceHeight - 1);
			double fy = sourceY - y0;
			for (int x = 0; x < targetWidth; x++)
			{
				double sourceX = x * xScale;
				int x0 = (int)sourceX;
				int x1 = Math.Min(x0 + 1, sourceWidth - 1);
				double fx = sourceX - x0;
				int p00 = (y0 * sourceWidth + x0) * 4;
				int p10 = (y0 * sourceWidth + x1) * 4;
				int p01 = (y1 * sourceWidth + x0) * 4;
				int p11 = (y1 * sourceWidth + x1) * 4;
				int output = (y * targetWidth + x) * 4;
				for (int channel = 0; channel < 4; channel++)
				{
					double top = source[p00 + channel] + (source[p10 + channel] - source[p00 + channel]) * fx;
					double bottom = source[p01 + channel] + (source[p11 + channel] - source[p01 + channel]) * fx;
					target[output + channel] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(top + (bottom - top) * fy)));
				}
			}
		}
		return target;
	}

	public static byte[] DecodeToRgba(byte[] png, out int width, out int height)
	{
		width = 0;
		height = 0;
		try
		{
			if (png == null || png.Length < 8)
			{
				return null;
			}
			int num = 8;
			int num2 = 0;
			int num3 = 0;
			int num4 = 0;
			int num5 = 0;
			using MemoryStream memoryStream = new MemoryStream();
			while (num + 8 <= png.Length)
			{
				int num6 = ReadBE(png, num);
				num += 4;
				char c = (char)png[num];
				string text = c.ToString();
				c = (char)png[num + 1];
				string text2 = c.ToString();
				c = (char)png[num + 2];
				string text3 = c.ToString();
				c = (char)png[num + 3];
				string text4 = text + text2 + text3 + c;
				num += 4;
				if (text4 == "IHDR")
				{
					num2 = ReadBE(png, num);
					num3 = ReadBE(png, num + 4);
					num4 = png[num + 8];
					num5 = png[num + 9];
					if (png[num + 12] != 0)
					{
						return null;
					}
				}
				else if (text4 == "IDAT")
				{
					memoryStream.Write(png, num, num6);
				}
				num += num6 + 4;
				if (text4 == "IEND")
				{
					break;
				}
			}
			if (num4 != 8)
			{
				return null;
			}
			int num7;
			switch (num5)
			{
			case 6:
				num7 = 4;
				break;
			case 2:
				num7 = 3;
				break;
			default:
				return null;
			}
			byte[] array = Inflate(memoryStream.ToArray(), 2);
			if (array == null)
			{
				return null;
			}
			int num8 = num7;
			int num9 = num2 * num8;
			if (array.Length < (num9 + 1) * num3)
			{
				return null;
			}
			byte[] array2 = new byte[num9 * num3];
			for (int i = 0; i < num3; i++)
			{
				int num10 = array[i * (num9 + 1)];
				int num11 = i * (num9 + 1) + 1;
				int num12 = i * num9;
				int num13 = (i - 1) * num9;
				for (int j = 0; j < num9; j++)
				{
					int num14 = array[num11 + j];
					int num15 = ((j >= num8) ? array2[num12 + j - num8] : 0);
					int num16 = ((i > 0) ? array2[num13 + j] : 0);
					int c2 = ((i > 0 && j >= num8) ? array2[num13 + j - num8] : 0);
					int num17;
					switch (num10)
					{
					case 0:
						num17 = num14;
						break;
					case 1:
						num17 = num14 + num15;
						break;
					case 2:
						num17 = num14 + num16;
						break;
					case 3:
						num17 = num14 + (num15 + num16 >> 1);
						break;
					case 4:
						num17 = num14 + Paeth(num15, num16, c2);
						break;
					default:
						return null;
					}
					array2[num12 + j] = (byte)(num17 & 0xFF);
				}
			}
			byte[] array3 = new byte[num2 * num3 * 4];
			if (num7 == 4)
			{
				Buffer.BlockCopy(array2, 0, array3, 0, Math.Min(array2.Length, array3.Length));
			}
			else
			{
				int num18 = 0;
				int num19 = 0;
				while (num18 < array2.Length)
				{
					array3[num19] = array2[num18];
					array3[num19 + 1] = array2[num18 + 1];
					array3[num19 + 2] = array2[num18 + 2];
					array3[num19 + 3] = byte.MaxValue;
					num18 += 3;
					num19 += 4;
				}
			}
			width = num2;
			height = num3;
			return array3;
		}
		catch (Exception ex)
		{
			Debug.Print("[AIPortraits] PngReencode decode failed: " + ex.Message);
			return null;
		}
	}

	private static int Paeth(int a, int b, int c)
	{
		int num = a + b - c;
		int num2 = Math.Abs(num - a);
		int num3 = Math.Abs(num - b);
		int num4 = Math.Abs(num - c);
		if (num2 <= num3 && num2 <= num4)
		{
			return a;
		}
		if (num3 <= num4)
		{
			return b;
		}
		return c;
	}

	private static byte[] Inflate(byte[] data, int offset)
	{
		using MemoryStream stream = new MemoryStream(data, offset, data.Length - offset);
		using DeflateStream deflateStream = new DeflateStream(stream, CompressionMode.Decompress);
		using MemoryStream memoryStream = new MemoryStream();
		deflateStream.CopyTo(memoryStream);
		return memoryStream.ToArray();
	}

	private static int ReadBE(byte[] b, int i)
	{
		return (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
	}
}
