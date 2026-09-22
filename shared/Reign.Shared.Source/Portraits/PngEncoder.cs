#nullable disable // Also compiled by nullable-enabled offline tests; this legacy codec returns null on invalid input.
using System;
using System.IO;
using System.IO.Compression;

namespace AIPortraits;

public static class PngEncoder
{
	private static uint[] _crcTable;

	public static byte[] EncodeRgba(byte[] rgba, int width, int height)
	{
		if (rgba == null || width <= 0 || height <= 0)
		{
			return null;
		}
		if (rgba.Length < width * height * 4)
		{
			return null;
		}
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.Write(new byte[8] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
		using (MemoryStream memoryStream2 = new MemoryStream())
		{
			WriteBE(memoryStream2, width);
			WriteBE(memoryStream2, height);
			memoryStream2.WriteByte(8);
			memoryStream2.WriteByte(6);
			memoryStream2.WriteByte(0);
			memoryStream2.WriteByte(0);
			memoryStream2.WriteByte(0);
			WriteChunk(memoryStream, "IHDR", memoryStream2.ToArray());
		}
		int num = width * 4;
		byte[] array = new byte[(num + 1) * height];
		for (int i = 0; i < height; i++)
		{
			array[i * (num + 1)] = 0;
			Buffer.BlockCopy(rgba, i * num, array, i * (num + 1) + 1, num);
		}
		byte[] data = ZlibCompress(array);
		WriteChunk(memoryStream, "IDAT", data);
		WriteChunk(memoryStream, "IEND", new byte[0]);
		return memoryStream.ToArray();
	}

	public static byte[] EncodeRgb(byte[] rgb, int width, int height)
	{
		if (rgb == null || width <= 0 || height <= 0)
		{
			return null;
		}
		if (rgb.Length < width * height * 3)
		{
			return null;
		}
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.Write(new byte[8] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
		using (MemoryStream memoryStream2 = new MemoryStream())
		{
			WriteBE(memoryStream2, width);
			WriteBE(memoryStream2, height);
			memoryStream2.WriteByte(8);
			memoryStream2.WriteByte(2);
			memoryStream2.WriteByte(0);
			memoryStream2.WriteByte(0);
			memoryStream2.WriteByte(0);
			WriteChunk(memoryStream, "IHDR", memoryStream2.ToArray());
		}
		int rowBytes = width * 3;
		byte[] scanlines = new byte[(rowBytes + 1) * height];
		for (int y = 0; y < height; y++)
		{
			scanlines[y * (rowBytes + 1)] = 0;
			Buffer.BlockCopy(rgb, y * rowBytes, scanlines, y * (rowBytes + 1) + 1, rowBytes);
		}
		WriteChunk(memoryStream, "IDAT", ZlibCompress(scanlines));
		WriteChunk(memoryStream, "IEND", new byte[0]);
		return memoryStream.ToArray();
	}

	private static byte[] ZlibCompress(byte[] data)
	{
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.WriteByte(120);
		memoryStream.WriteByte(1);
		using (DeflateStream deflateStream = new DeflateStream(memoryStream, CompressionLevel.Optimal, leaveOpen: true))
		{
			deflateStream.Write(data, 0, data.Length);
		}
		uint num = Adler32(data);
		memoryStream.WriteByte((byte)((num >> 24) & 0xFF));
		memoryStream.WriteByte((byte)((num >> 16) & 0xFF));
		memoryStream.WriteByte((byte)((num >> 8) & 0xFF));
		memoryStream.WriteByte((byte)(num & 0xFF));
		return memoryStream.ToArray();
	}

	private static uint Adler32(byte[] data)
	{
		uint num = 1u;
		uint num2 = 0u;
		foreach (byte b in data)
		{
			num = (num + b) % 65521;
			num2 = (num2 + num) % 65521;
		}
		return (num2 << 16) | num;
	}

	private static void WriteChunk(Stream s, string type, byte[] data)
	{
		WriteBE(s, data.Length);
		byte[] array = new byte[4];
		for (int i = 0; i < 4; i++)
		{
			array[i] = (byte)type[i];
		}
		s.Write(array, 0, 4);
		s.Write(data, 0, data.Length);
		byte[] array2 = new byte[4 + data.Length];
		Buffer.BlockCopy(array, 0, array2, 0, 4);
		Buffer.BlockCopy(data, 0, array2, 4, data.Length);
		WriteBE(s, (int)Crc32(array2));
	}

	private static void WriteBE(Stream s, int value)
	{
		s.WriteByte((byte)((value >> 24) & 0xFF));
		s.WriteByte((byte)((value >> 16) & 0xFF));
		s.WriteByte((byte)((value >> 8) & 0xFF));
		s.WriteByte((byte)(value & 0xFF));
	}

	private static uint Crc32(byte[] data)
	{
		if (_crcTable == null)
		{
			_crcTable = new uint[256];
			for (uint num = 0u; num < 256; num++)
			{
				uint num2 = num;
				for (int i = 0; i < 8; i++)
				{
					num2 = (((num2 & 1) != 0) ? (0xEDB88320u ^ (num2 >> 1)) : (num2 >> 1));
				}
				_crcTable[num] = num2;
			}
		}
		uint num3 = uint.MaxValue;
		foreach (byte b in data)
		{
			num3 = _crcTable[(num3 ^ b) & 0xFF] ^ (num3 >> 8);
		}
		return num3 ^ 0xFFFFFFFFu;
	}
}
