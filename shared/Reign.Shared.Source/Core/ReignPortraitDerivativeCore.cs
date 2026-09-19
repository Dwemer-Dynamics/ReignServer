using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ReignPortraits
{
    public sealed class PortraitFaceFocus
    {
        public string Method;
        public string Model;
        public double Confidence;
        public int CandidateCount;
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    public sealed class PortraitDerivativeManifestV2
    {
        public int Version;
        public int CropAlgorithmVersion;
        public string SourceFileName;
        public string SourcePath;
        public long SourceLength;
        public long SourceLastWriteUtcTicks;
        public int SourceWidth;
        public int SourceHeight;
        public int CropX;
        public int CropY;
        public int CropWidth;
        public int CropHeight;
        public int WideCropX;
        public int WideCropY;
        public int WideCropWidth;
        public int WideCropHeight;
        public string FocusMethod;
        public string FocusModel;
        public double FocusConfidence;
        public int FaceCandidateCount;
        public double FaceX;
        public double FaceY;
        public double FaceWidth;
        public double FaceHeight;
        public bool NeedsReview;
        public string ThumbnailFileName;
        public int ThumbnailWidth;
        public int ThumbnailHeight;
        public string PartyThumbnailFileName;
        public int PartyThumbnailWidth;
        public int PartyThumbnailHeight;
        public string PortraitFileName;
        public int PortraitWidth;
        public int PortraitHeight;
        public string ZoomFileName;
        public int ZoomWidth;
        public int ZoomHeight;
    }

    public sealed class PortraitDerivativeBuild
    {
        public PortraitDerivativeManifestV2 Manifest;
        public byte[] ThumbnailPng;
        public byte[] PartyThumbnailPng;
        public byte[] PortraitPng;
        public byte[] ZoomPng;
    }

    public static class PortraitDerivativeCore
    {
        public const int CurrentVersion = 3;
        public const int CurrentCropAlgorithmVersion = 2;
        public const string ThumbnailFileName = "thumbnail.png";
        public const string PartyThumbnailFileName = "thumbnail_wide.png";
        public const string PortraitFileName = "portrait_chest.png";
        public const string ZoomFileName = "zoom.png";
        public const string ManifestFileName = ".portrait_derivatives.json";
        public const int ThumbnailMaxWidth = 256;
        public const int ThumbnailMaxHeight = 320;
        public const int PartyThumbnailMaxWidth = 448;
        public const int PartyThumbnailMaxHeight = 320;
        public const int PortraitMaxWidth = 768;
        public const int PortraitMaxHeight = 960;
        public const int ZoomMaxWidth = 1024;
        public const int ZoomMaxHeight = 1536;

        // Native game-menu rows can receive a portrait while their Reign overlay
        // is still hidden. Route that underlying widget identically to the overlay.
        public static bool UsesNativeWideThumbnail(string widgetId, string ownerType, bool partyWindow)
        {
            return partyWindow || (string.Equals(widgetId, "CharacterImage", StringComparison.Ordinal)
                && string.Equals(ownerType, "GameMenuPartyItemButtonWidget", StringComparison.Ordinal));
        }

        public static PortraitDerivativeBuild BuildV2(
            byte[] sourceRgba,
            int sourceWidth,
            int sourceHeight,
            FileInfo sourceInfo,
            Func<byte[], int, int, byte[]> encodeRgba,
            PortraitFaceFocus focus = null)
        {
            if (sourceRgba == null || sourceInfo == null || encodeRgba == null
                || sourceWidth <= 0 || sourceHeight <= 0
                || sourceRgba.Length < sourceWidth * sourceHeight * 4)
            {
                throw new InvalidDataException("The full-body portrait source could not be decoded.");
            }

            bool hasDetectedFocus = IsValidFocus(focus);
            int cropX;
            int cropY;
            int cropWidth;
            int cropHeight;
            if (hasDetectedFocus)
            {
                CalculateFocusedCrop(sourceWidth, sourceHeight, focus, 0.8d, out int focusedX, out int focusedY, out int focusedWidth, out int focusedHeight);
                cropX = focusedX;
                cropY = focusedY;
                cropWidth = focusedWidth;
                cropHeight = focusedHeight;
            }
            else
            {
                CalculateChestCrop(sourceWidth, sourceHeight, out cropX, out cropY, out cropWidth, out cropHeight);
            }
            CalculateAspectCrop(sourceWidth, sourceHeight, cropX + cropWidth * 0.5d, cropY, cropHeight, 1.4d,
                out int wideCropX, out int wideCropY, out int wideCropWidth, out int wideCropHeight);
            if (hasDetectedFocus)
            {
                CalculateFocusedCrop(sourceWidth, sourceHeight, focus, 1.4d, out wideCropX, out wideCropY, out wideCropWidth, out wideCropHeight);
            }
            byte[] cropRgba = CropRgba(sourceRgba, sourceWidth, sourceHeight, cropX, cropY, cropWidth, cropHeight);
            byte[] wideCropRgba = CropRgba(sourceRgba, sourceWidth, sourceHeight, wideCropX, wideCropY, wideCropWidth, wideCropHeight);

            CalculateContainedDimensions(cropWidth, cropHeight, ThumbnailMaxWidth, ThumbnailMaxHeight, out int thumbnailWidth, out int thumbnailHeight);
            CalculateContainedDimensions(wideCropWidth, wideCropHeight, PartyThumbnailMaxWidth, PartyThumbnailMaxHeight, out int partyThumbnailWidth, out int partyThumbnailHeight);
            CalculateContainedDimensions(cropWidth, cropHeight, PortraitMaxWidth, PortraitMaxHeight, out int portraitWidth, out int portraitHeight);
            CalculateContainedDimensions(sourceWidth, sourceHeight, ZoomMaxWidth, ZoomMaxHeight, out int zoomWidth, out int zoomHeight);

            byte[] thumbnailPng = encodeRgba(ResizeBilinear(cropRgba, cropWidth, cropHeight, thumbnailWidth, thumbnailHeight), thumbnailWidth, thumbnailHeight);
            byte[] partyThumbnailPng = encodeRgba(ResizeBilinear(wideCropRgba, wideCropWidth, wideCropHeight, partyThumbnailWidth, partyThumbnailHeight), partyThumbnailWidth, partyThumbnailHeight);
            byte[] portraitPng = encodeRgba(ResizeBilinear(cropRgba, cropWidth, cropHeight, portraitWidth, portraitHeight), portraitWidth, portraitHeight);
            byte[] zoomPng = encodeRgba(ResizeBilinear(sourceRgba, sourceWidth, sourceHeight, zoomWidth, zoomHeight), zoomWidth, zoomHeight);
            if (thumbnailPng == null || thumbnailPng.Length == 0
                || partyThumbnailPng == null || partyThumbnailPng.Length == 0
                || portraitPng == null || portraitPng.Length == 0
                || zoomPng == null || zoomPng.Length == 0)
            {
                throw new InvalidDataException("One or more portrait derivatives could not be encoded.");
            }

            return new PortraitDerivativeBuild
            {
                Manifest = new PortraitDerivativeManifestV2
                {
                    Version = CurrentVersion,
                    CropAlgorithmVersion = CurrentCropAlgorithmVersion,
                    SourceFileName = sourceInfo.Name,
                    SourcePath = sourceInfo.FullName,
                    SourceLength = sourceInfo.Length,
                    SourceLastWriteUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks,
                    SourceWidth = sourceWidth,
                    SourceHeight = sourceHeight,
                    CropX = cropX,
                    CropY = cropY,
                    CropWidth = cropWidth,
                    CropHeight = cropHeight,
                    WideCropX = wideCropX,
                    WideCropY = wideCropY,
                    WideCropWidth = wideCropWidth,
                    WideCropHeight = wideCropHeight,
                    FocusMethod = hasDetectedFocus ? (focus.Method ?? "face_detector") : "legacy_fallback",
                    FocusModel = hasDetectedFocus ? (focus.Model ?? "") : "",
                    FocusConfidence = hasDetectedFocus ? focus.Confidence : 0d,
                    FaceCandidateCount = hasDetectedFocus ? Math.Max(1, focus.CandidateCount) : 0,
                    FaceX = hasDetectedFocus ? focus.X : 0d,
                    FaceY = hasDetectedFocus ? focus.Y : 0d,
                    FaceWidth = hasDetectedFocus ? focus.Width : 0d,
                    FaceHeight = hasDetectedFocus ? focus.Height : 0d,
                    NeedsReview = !hasDetectedFocus || focus.CandidateCount != 1,
                    ThumbnailFileName = ThumbnailFileName,
                    ThumbnailWidth = thumbnailWidth,
                    ThumbnailHeight = thumbnailHeight,
                    PartyThumbnailFileName = PartyThumbnailFileName,
                    PartyThumbnailWidth = partyThumbnailWidth,
                    PartyThumbnailHeight = partyThumbnailHeight,
                    PortraitFileName = PortraitFileName,
                    PortraitWidth = portraitWidth,
                    PortraitHeight = portraitHeight,
                    ZoomFileName = ZoomFileName,
                    ZoomWidth = zoomWidth,
                    ZoomHeight = zoomHeight
                },
                ThumbnailPng = thumbnailPng,
                PartyThumbnailPng = partyThumbnailPng,
                PortraitPng = portraitPng,
                ZoomPng = zoomPng
            };
        }

        public static PortraitDerivativeBuild BuildRequiredFocusedV2(
            byte[] sourceRgba,
            int sourceWidth,
            int sourceHeight,
            FileInfo sourceInfo,
            Func<byte[], int, int, byte[]> encodeRgba,
            PortraitFaceFocus focus)
        {
            if (!IsValidFocus(focus) || focus.CandidateCount != 1)
            {
                throw new InvalidDataException(
                    "A generated AI portrait requires exactly one server-validated face focus; legacy fallback cropping is not allowed.");
            }

            return BuildV2(sourceRgba, sourceWidth, sourceHeight, sourceInfo, encodeRgba, focus);
        }

        public static void CalculateFocusedCrop(int sourceWidth, int sourceHeight, PortraitFaceFocus focus, double aspect,
            out int x, out int y, out int width, out int height)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0 || aspect <= 0d || !IsValidFocus(focus))
            {
                throw new ArgumentOutOfRangeException("A valid portrait size, aspect, and normalized face box are required.");
            }

            double faceHeight = focus.Height * sourceHeight;
            double faceTop = focus.Y * sourceHeight;
            double faceCenterX = (focus.X + focus.Width * 0.5d) * sourceWidth;
            int desiredHeight = Math.Max(1, (int)Math.Round(faceHeight * 2.15d, MidpointRounding.AwayFromZero));
            int widthLimitedHeight = Math.Max(1, (int)Math.Floor(sourceWidth / aspect));
            height = Math.Min(sourceHeight, Math.Min(desiredHeight, widthLimitedHeight));
            width = Math.Min(sourceWidth, Math.Max(1, (int)Math.Round(height * aspect, MidpointRounding.AwayFromZero)));
            x = Clamp((int)Math.Round(faceCenterX - width * 0.5d, MidpointRounding.AwayFromZero), 0, sourceWidth - width);
            y = Clamp((int)Math.Round(faceTop - faceHeight * 0.25d, MidpointRounding.AwayFromZero), 0, sourceHeight - height);
        }

        private static void CalculateAspectCrop(int sourceWidth, int sourceHeight, double centerX, int y, int desiredHeight, double aspect,
            out int cropX, out int cropY, out int cropWidth, out int cropHeight)
        {
            cropHeight = Math.Min(sourceHeight, Math.Min(Math.Max(1, desiredHeight), Math.Max(1, (int)Math.Floor(sourceWidth / aspect))));
            cropWidth = Math.Min(sourceWidth, Math.Max(1, (int)Math.Round(cropHeight * aspect, MidpointRounding.AwayFromZero)));
            cropX = Clamp((int)Math.Round(centerX - cropWidth * 0.5d, MidpointRounding.AwayFromZero), 0, sourceWidth - cropWidth);
            cropY = Clamp(y, 0, sourceHeight - cropHeight);
        }

        public static bool IsValidFocus(PortraitFaceFocus focus)
        {
            return focus != null
                && focus.Confidence > 0d
                && focus.X >= 0d && focus.Y >= 0d
                && focus.Width > 0d && focus.Height > 0d
                && focus.X + focus.Width <= 1.0001d
                && focus.Y + focus.Height <= 1.0001d;
        }

        // This source is compiled into both the native client and the server.
        // A server-accepted product must pass exactly the same composition gate on import.
        public static string PortraitCompositionError(PortraitFaceFocus focus)
        {
            if (focus == null) return "Portrait composition rejected: face metadata is missing.";
            var failures = new List<string>();
            if (!IsValidFocus(focus) || double.IsInfinity(focus.Confidence))
                failures.Add("face metadata has invalid coordinates or confidence");
            if (focus.CandidateCount != 1) failures.Add("detected " + focus.CandidateCount + " faces (required 1)");
            double centerX = focus.X + focus.Width * 0.5d;
            if (double.IsNaN(focus.Height) || focus.Height < 0.08d || focus.Height > 0.18d)
                failures.Add(string.Format(CultureInfo.InvariantCulture, "face height {0:F2}% (allowed 8-18%)", focus.Height * 100d));
            if (double.IsNaN(focus.Y) || focus.Y < 0.035d || focus.Y > 0.18d)
                failures.Add(string.Format(CultureInfo.InvariantCulture, "face top {0:F2}% (allowed 3.5-18%)", focus.Y * 100d));
            if (double.IsNaN(centerX) || centerX < 0.35d || centerX > 0.65d)
                failures.Add(string.Format(CultureInfo.InvariantCulture, "face center {0:F2}% (allowed 35-65%)", centerX * 100d));
            return failures.Count == 0 ? null : "Portrait composition rejected: " + string.Join("; ", failures) + ".";
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        public static void CalculateChestCrop(int sourceWidth, int sourceHeight, out int x, out int y, out int width, out int height)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                throw new ArgumentOutOfRangeException("Portrait dimensions must be positive.");
            }

            int fortyPercentHeight = Math.Max(1, (int)Math.Round(sourceHeight * 0.4d, MidpointRounding.AwayFromZero));
            int widthLimitedHeight = Math.Max(1, (int)Math.Floor(sourceWidth * 1.25d));
            height = Math.Min(sourceHeight, Math.Min(fortyPercentHeight, widthLimitedHeight));
            width = Math.Min(sourceWidth, Math.Max(1, (int)Math.Round(height * 0.8d, MidpointRounding.AwayFromZero)));
            x = Math.Max(0, (sourceWidth - width) / 2);
            y = 0;
        }

        public static void CalculateContainedDimensions(int width, int height, int maxWidth, int maxHeight, out int outputWidth, out int outputHeight)
        {
            if (width <= 0 || height <= 0 || maxWidth <= 0 || maxHeight <= 0)
            {
                throw new ArgumentOutOfRangeException("Portrait and output dimensions must be positive.");
            }

            double scale = Math.Min(1d, Math.Min((double)maxWidth / width, (double)maxHeight / height));
            outputWidth = Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero));
            outputHeight = Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero));
        }

        public static bool IsCurrentV2(PortraitDerivativeManifestV2 manifest, string sourcePath, string outputDirectory)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(outputDirectory))
            {
                return false;
            }

            try
            {
                FileInfo source = new FileInfo(sourcePath);
                return source.Exists
                    && manifest.Version == CurrentVersion
                    && manifest.CropAlgorithmVersion == CurrentCropAlgorithmVersion
                    && string.Equals(manifest.SourceFileName, source.Name, StringComparison.OrdinalIgnoreCase)
                    && manifest.SourceLength == source.Length
                    && manifest.SourceLastWriteUtcTicks == source.LastWriteTimeUtc.Ticks
                    && manifest.SourceWidth > 0
                    && manifest.SourceHeight > 0
                    && manifest.CropX >= 0
                    && manifest.CropY >= 0
                    && manifest.CropWidth > 0
                    && manifest.CropHeight > 0
                    && manifest.CropX + manifest.CropWidth <= manifest.SourceWidth
                    && manifest.CropY + manifest.CropHeight <= manifest.SourceHeight
                    && manifest.WideCropX >= 0
                    && manifest.WideCropY >= 0
                    && manifest.WideCropWidth > 0
                    && manifest.WideCropHeight > 0
                    && manifest.WideCropX + manifest.WideCropWidth <= manifest.SourceWidth
                    && manifest.WideCropY + manifest.WideCropHeight <= manifest.SourceHeight
                    && !string.IsNullOrWhiteSpace(manifest.FocusMethod)
                    && string.Equals(manifest.ThumbnailFileName, ThumbnailFileName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(manifest.PartyThumbnailFileName, PartyThumbnailFileName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(manifest.PortraitFileName, PortraitFileName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(manifest.ZoomFileName, ZoomFileName, StringComparison.OrdinalIgnoreCase)
                    && HasPngDimensions(Path.Combine(outputDirectory, ThumbnailFileName), manifest.ThumbnailWidth, manifest.ThumbnailHeight)
                    && HasPngDimensions(Path.Combine(outputDirectory, PartyThumbnailFileName), manifest.PartyThumbnailWidth, manifest.PartyThumbnailHeight)
                    && HasPngDimensions(Path.Combine(outputDirectory, PortraitFileName), manifest.PortraitWidth, manifest.PortraitHeight)
                    && HasPngDimensions(Path.Combine(outputDirectory, ZoomFileName), manifest.ZoomWidth, manifest.ZoomHeight);
            }
            catch
            {
                return false;
            }
        }

        public static bool CommitV2(string sourcePath, string outputDirectory, PortraitDerivativeBuild build, byte[] manifestBytes, out string error)
        {
            error = null;
            if (build == null || build.Manifest == null || manifestBytes == null || manifestBytes.Length == 0)
            {
                error = "The derivative build or manifest was empty.";
                return false;
            }

            Directory.CreateDirectory(outputDirectory);
            string token = Guid.NewGuid().ToString("N");
            var items = new List<CommitItem>
            {
                new CommitItem(Path.Combine(outputDirectory, ThumbnailFileName), build.ThumbnailPng, build.Manifest.ThumbnailWidth, build.Manifest.ThumbnailHeight, true, token),
                new CommitItem(Path.Combine(outputDirectory, PartyThumbnailFileName), build.PartyThumbnailPng, build.Manifest.PartyThumbnailWidth, build.Manifest.PartyThumbnailHeight, true, token),
                new CommitItem(Path.Combine(outputDirectory, PortraitFileName), build.PortraitPng, build.Manifest.PortraitWidth, build.Manifest.PortraitHeight, true, token),
                new CommitItem(Path.Combine(outputDirectory, ZoomFileName), build.ZoomPng, build.Manifest.ZoomWidth, build.Manifest.ZoomHeight, true, token),
                new CommitItem(Path.Combine(outputDirectory, ManifestFileName), manifestBytes, 0, 0, false, token)
            };

            try
            {
                foreach (CommitItem item in items)
                {
                    File.WriteAllBytes(item.TemporaryPath, item.Bytes);
                    if (item.IsPng && !HasPngDimensions(item.TemporaryPath, item.Width, item.Height))
                    {
                        throw new InvalidDataException("A temporary derivative failed PNG validation: " + Path.GetFileName(item.Path));
                    }
                }

                if (!SourceMatches(sourcePath, build.Manifest))
                {
                    throw new IOException("The full-body portrait changed while derivatives were being generated.");
                }

                foreach (CommitItem item in items)
                {
                    item.HadOriginal = File.Exists(item.Path);
                    if (item.HadOriginal)
                    {
                        File.Replace(item.TemporaryPath, item.Path, item.BackupPath);
                    }
                    else
                    {
                        File.Move(item.TemporaryPath, item.Path);
                    }
                    item.Committed = true;

                    if (item.IsPng && !HasPngDimensions(item.Path, item.Width, item.Height))
                    {
                        throw new InvalidDataException("A committed derivative failed PNG validation: " + Path.GetFileName(item.Path));
                    }

                    if (string.Equals(Path.GetFileName(item.Path), ZoomFileName, StringComparison.OrdinalIgnoreCase)
                        && !SourceMatches(sourcePath, build.Manifest))
                    {
                        throw new IOException("The full-body portrait changed before the derivative manifest could be committed.");
                    }
                }

                foreach (CommitItem item in items)
                {
                    DeleteIfExists(item.BackupPath);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    CommitItem item = items[i];
                    try
                    {
                        if (item.Committed)
                        {
                            DeleteIfExists(item.Path);
                            if (item.HadOriginal && File.Exists(item.BackupPath))
                            {
                                File.Move(item.BackupPath, item.Path);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                return false;
            }
            finally
            {
                foreach (CommitItem item in items)
                {
                    DeleteIfExists(item.TemporaryPath);
                    DeleteIfExists(item.BackupPath);
                }
            }
        }

        public static bool HasPngDimensions(string path, int expectedWidth, int expectedHeight)
        {
            if (expectedWidth <= 0 || expectedHeight <= 0)
            {
                return false;
            }

            return TryReadPngDimensions(path, out int width, out int height)
                && width == expectedWidth
                && height == expectedHeight;
        }

        public static bool TryReadPngDimensions(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                byte[] header = new byte[24];
                using (FileStream stream = File.OpenRead(path))
                {
                    if (stream.Read(header, 0, header.Length) != header.Length || !IsPng(header))
                    {
                        return false;
                    }
                }
                width = ReadBigEndianInt32(header, 16);
                height = ReadBigEndianInt32(header, 20);
                return width > 0 && height > 0;
            }
            catch
            {
                return false;
            }
        }

        public static byte[] ResizeBilinear(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        {
            if (sourceWidth == targetWidth && sourceHeight == targetHeight)
            {
                byte[] copy = new byte[source.Length];
                Buffer.BlockCopy(source, 0, copy, 0, source.Length);
                return copy;
            }

            byte[] target = new byte[targetWidth * targetHeight * 4];
            double xScale = (double)sourceWidth / targetWidth;
            double yScale = (double)sourceHeight / targetHeight;
            for (int y = 0; y < targetHeight; y++)
            {
                double sourceY = (y + 0.5d) * yScale - 0.5d;
                int y0 = Math.Max(0, Math.Min(sourceHeight - 1, (int)Math.Floor(sourceY)));
                int y1 = Math.Min(sourceHeight - 1, y0 + 1);
                double fy = Math.Max(0d, sourceY - Math.Floor(sourceY));
                for (int x = 0; x < targetWidth; x++)
                {
                    double sourceX = (x + 0.5d) * xScale - 0.5d;
                    int x0 = Math.Max(0, Math.Min(sourceWidth - 1, (int)Math.Floor(sourceX)));
                    int x1 = Math.Min(sourceWidth - 1, x0 + 1);
                    double fx = Math.Max(0d, sourceX - Math.Floor(sourceX));
                    int p00 = (y0 * sourceWidth + x0) * 4;
                    int p10 = (y0 * sourceWidth + x1) * 4;
                    int p01 = (y1 * sourceWidth + x0) * 4;
                    int p11 = (y1 * sourceWidth + x1) * 4;
                    int destination = (y * targetWidth + x) * 4;
                    for (int channel = 0; channel < 4; channel++)
                    {
                        double top = source[p00 + channel] + (source[p10 + channel] - source[p00 + channel]) * fx;
                        double bottom = source[p01 + channel] + (source[p11 + channel] - source[p01 + channel]) * fx;
                        target[destination + channel] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(top + (bottom - top) * fy, MidpointRounding.AwayFromZero)));
                    }
                }
            }
            return target;
        }

        private static byte[] CropRgba(byte[] source, int sourceWidth, int sourceHeight, int x, int y, int width, int height)
        {
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > sourceWidth || y + height > sourceHeight)
            {
                throw new ArgumentOutOfRangeException("The requested chest crop is outside the portrait source.");
            }

            byte[] crop = new byte[width * height * 4];
            for (int row = 0; row < height; row++)
            {
                Buffer.BlockCopy(source, ((y + row) * sourceWidth + x) * 4, crop, row * width * 4, width * 4);
            }
            return crop;
        }

        private static bool SourceMatches(string sourcePath, PortraitDerivativeManifestV2 manifest)
        {
            try
            {
                FileInfo source = new FileInfo(sourcePath);
                return source.Exists
                    && source.Length == manifest.SourceLength
                    && source.LastWriteTimeUtc.Ticks == manifest.SourceLastWriteUtcTicks;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPng(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 8
                && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71
                && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10;
        }

        private static int ReadBigEndianInt32(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private sealed class CommitItem
        {
            public readonly string Path;
            public readonly string TemporaryPath;
            public readonly string BackupPath;
            public readonly byte[] Bytes;
            public readonly int Width;
            public readonly int Height;
            public readonly bool IsPng;
            public bool HadOriginal;
            public bool Committed;

            public CommitItem(string path, byte[] bytes, int width, int height, bool isPng, string token)
            {
                Path = path;
                TemporaryPath = path + ".tmp." + token;
                BackupPath = path + ".bak." + token;
                Bytes = bytes;
                Width = width;
                Height = height;
                IsPng = isPng;
            }
        }
    }
}
