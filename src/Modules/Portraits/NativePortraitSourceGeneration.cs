using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using AIPortraits;
using ReignPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string NativePortraitGeneratorExecutable = "Bannerlord.NativeCharacterImageGenerator.App.exe";
        private const string NativePortraitRenderContract = "ai_source_wan_portrait_v5";
        private const string ResidentOutfitRenderContract = "ai_source_resident_full_outfit_v1";
        private const int NativePortraitSourceWidth = 768;
        private const int NativePortraitSourceHeight = 1024;
        private const int NativePortraitGeneratorTimeoutMs = 180000;
        private const int PortraitProductReceiptVersion = 1;
        private const string PortraitProductReceiptSchema = "reign-portrait-product-v1";
        private const string PortraitInputReceiptSchema = "reign-portrait-input-v1";

        private sealed class PortraitProductValidation
        {
            public int Width;
            public int Height;
            public PortraitFaceFocus Focus;
            public bool Normalized;
        }

        private static byte[] ResolvePortraitGenerationSource(
            Dictionary<string, object> settings,
            Dictionary<string, object> payload,
            byte[] suppliedSource,
            out Dictionary<string, object> provenance)
        {
            provenance = new Dictionary<string, object>
            {
                ["kind"] = "supplied",
                ["width"] = 0,
                ["height"] = 0
            };
            if (payload.ContainsKey("preparedNativeSourceId"))
                return ResolvePreparedTavernSource(payload, PreparedTavernStore, out provenance);
            // Shared rebuilds resolve their reference on the server, never from a caller's image
            // or legacy native-source hash. First-time generation still uses the renderer below.
            if (ReadBool(payload, "sharedCacheOutput", false) && !PreserveResidentClothing(payload))
            {
                if (!TryLoadSharedPortraitEntry(ReadString(payload, "cacheKey", ""), out SharedPortraitEntry shared, out string sharedError))
                    throw new InvalidDataException(sharedError);
                if (File.Exists(shared.PortraitPath) && !shared.HasPortrait)
                    throw new InvalidDataException("The existing shared AI portrait is corrupt; it will not be replaced using a different source.");
                if (shared.HasPortrait)
                {
                    byte[] master = File.ReadAllBytes(shared.PortraitPath);
                    if (PngReencode.DecodeToRgba(master, out int masterWidth, out int masterHeight) == null)
                        throw new InvalidDataException("Shared AI master could not be decoded.");
                    var physique = BindSharedPortraitPhysique(shared.Metadata, master);
                    provenance = new Dictionary<string, object> {
                        ["kind"] = "shared_ai_portrait_reference", ["width"] = masterWidth, ["height"] = masterHeight,
                        ["referenceFile"] = "portrait.png", ["referenceSha256"] = Sha256Hex(master), ["physique"] = physique
                    };
                    return master;
                }
            }
            var snapshotPhysique = ReadDictionary(payload, "nativeCharacterSnapshot");
            if (snapshotPhysique != null)
                foreach (string field in new[] { "bodyWeight", "bodyBuild" })
                    if (snapshotPhysique.ContainsKey(field)) NativePhysiqueUnit(snapshotPhysique, field);
            if (snapshotPhysique == null && ReadDictionary(payload, "nativePhysique") != null && ReadBool(payload, "sharedCacheOutput", false)
                && suppliedSource != null && suppliedSource.Length > 0
                && ReadString(payload, "renderPreset", "") == NativePortraitRenderContract
                && string.Equals(ReadString(payload, "sourceSha256", ""), Sha256Hex(suppliedSource), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(ReadString(payload, "renderContractHash", "")))
            {
                RequirePortraitSourceContract(suppliedSource, "Shared portrait source");
                string renderPreset = ReadString(payload, "renderPreset", "");
                if (!string.Equals(renderPreset, NativePortraitRenderContract, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Shared portrait source metadata must use the current " + NativePortraitRenderContract + " render contract.");
                }
                provenance["kind"] = "shared_native_source_cache";
                provenance["renderContract"] = NativePortraitRenderContract;
                provenance["width"] = NativePortraitSourceWidth;
                provenance["height"] = NativePortraitSourceHeight;
                provenance["physique"] = ValidateNativePhysique(ReadDictionary(payload, "nativePhysique"), suppliedSource);
                return suppliedSource;
            }

            if (TryGenerateNativePortraitSource(settings, payload, out byte[] generated, out provenance, out string error))
            {
                return generated;
            }
            throw new InvalidOperationException(
                "The required native portrait source could not be generated. " + error);
        }

        private static bool TryGenerateNativePortraitSource(
            Dictionary<string, object> settings,
            Dictionary<string, object> payload,
            out byte[] source,
            out Dictionary<string, object> provenance,
            out string error)
        {
            source = null;
            provenance = new Dictionary<string, object>();
            error = "";
            string executable = ResolveNativePortraitGeneratorPath(settings);
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                error = "The bundled native portrait generator is missing.";
                return false;
            }

            string jobRoot = Path.Combine(Path.GetTempPath(), "ReignPortraitSource", Guid.NewGuid().ToString("N"));
            string outputPath = Path.Combine(jobRoot, "source.png");
            string resultPath = Path.Combine(jobRoot, "result.json");
            Directory.CreateDirectory(jobRoot);
            try
            {
                var arguments = new StringBuilder();
                var snapshot = ReadDictionary(payload, "nativeCharacterSnapshot");
                if (snapshot != null)
                {
                    if (!string.Equals(ReadString(snapshot, "schema", ""), "reign-native-portrait-snapshot-v1", StringComparison.Ordinal)
                        || !string.Equals(ReadString(snapshot, "campaignId", ""), ReadString(payload, "campaignId", ""), StringComparison.Ordinal)
                        || !string.Equals(ReadString(snapshot, "heroStringId", ""), ReadFirstString(payload, "heroStringId", "heroId", "characterId"), StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(ReadString(snapshot, "bodyKey", "")))
                        throw new InvalidDataException("Native portrait snapshot identity or facial appearance is invalid.");
                    string snapshotPath = Path.Combine(jobRoot, "character.json");
                    WriteJsonObject(snapshotPath, snapshot);
                    AppendProcessArgument(arguments, "--character-snapshot", NativeGeneratorPath(snapshotPath));
                }
                AppendProcessArgument(arguments, "--reign-render-portrait-source");
                AppendProcessArgument(arguments, "--campaign-id", ReadString(payload, "campaignId", ""));
                AppendProcessArgument(arguments, "--reign-data-root", NativeGeneratorPath(DataDir));
                AppendProcessArgument(arguments, "--hero-id", ReadFirstString(payload, "heroStringId", "heroId", "characterId"));
                AppendProcessArgument(arguments, "--character-object-id", ReadString(payload, "characterObjectId", ""));
                AppendProcessArgument(arguments, "--cache-key", ReadString(payload, "cacheKey", ""));
                AppendProcessArgument(arguments, "--output", NativeGeneratorPath(outputPath));
                AppendProcessArgument(arguments, "--result", NativeGeneratorPath(resultPath));
                var start = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments.ToString(),
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process process = Process.Start(start))
                {
                    if (process == null)
                    {
                        error = "The native portrait generator process could not be started.";
                        return false;
                    }
                    if (!process.WaitForExit(NativePortraitGeneratorTimeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        error = "The native portrait generator did not finish within three minutes.";
                        return false;
                    }
                    if (process.ExitCode != 0 || !File.Exists(outputPath))
                    {
                        error = ReadNativePortraitGeneratorError(resultPath, process.ExitCode);
                        return false;
                    }
                }

                source = File.ReadAllBytes(outputPath);
                RequirePortraitSourceContract(source, "Native portrait source");
                var generatorResult = ReadJsonObject(resultPath);
                string expectedRenderContract = PreserveResidentClothing(payload) ? ResidentOutfitRenderContract : NativePortraitRenderContract;
                if (ReadString(generatorResult, "renderContract", "") != expectedRenderContract)
                    throw new InvalidDataException("The native source did not use the required framing contract: " + expectedRenderContract);
                var physique = ValidateNativePhysique(ReadDictionary(generatorResult, "physique"), source);
                provenance = new Dictionary<string, object>
                {
                    ["kind"] = "native_character_image_generator",
                    ["renderContract"] = expectedRenderContract,
                    ["generatorPath"] = executable,
                    ["appearanceSource"] = snapshot == null ? "saved_roster" : "request_snapshot",
                    ["appearanceSha256"] = snapshot == null ? "" : Sha256Hex(Encoding.UTF8.GetBytes(Json.Serialize(snapshot))),
                    ["width"] = NativePortraitSourceWidth,
                    ["height"] = NativePortraitSourceHeight
                };
                provenance["physique"] = physique;
                provenance["renderContractHash"] = ReadString(generatorResult, "renderContractHash", "");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (Directory.Exists(jobRoot)) Directory.Delete(jobRoot, true); } catch { }
            }
        }

        private static string ResolveNativePortraitGeneratorPath(Dictionary<string, object> settings)
        {
            // The game renderer remains Windows-native; only a locally installed helper is callable.
            string bridge = Environment.GetEnvironmentVariable("REIGN_NATIVE_GENERATOR")
                ?? ReadString(settings, "portraitNativeSourceGeneratorPath", "");
            string bridgeRecord = Path.Combine(DataDir, "windows-bridge.json");
            if (string.IsNullOrWhiteSpace(bridge) && File.Exists(bridgeRecord))
                bridge = ReadString(ReadJsonObject(bridgeRecord), "nativeGenerator", "");
            if (!bridge.StartsWith("/mnt/", StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(bridge), NativePortraitGeneratorExecutable, StringComparison.Ordinal)
                || !File.Exists(bridge)) return "";
            return Path.GetFullPath(bridge);
        }

        // WSL exposes the same job files to the bounded Windows renderer through its local UNC path.
        private static string NativeGeneratorPath(string path)
        {
            var start = new ProcessStartInfo("/usr/bin/wslpath")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-w");
            start.ArgumentList.Add(Path.GetFullPath(path));
            using (Process process = Process.Start(start))
            {
                if (process == null) throw new IOException("Could not resolve the Windows portrait bridge path.");
                if (!process.WaitForExit(5000))
                {
                    process.Kill();
                    throw new IOException("Windows portrait bridge path lookup timed out.");
                }
                string mapped = process.StandardOutput.ReadToEnd().Trim();
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(mapped))
                    throw new IOException("Windows portrait bridge path lookup failed.");
                return mapped;
            }
        }

        private static string ReadNativePortraitGeneratorError(string resultPath, int exitCode)
        {
            try
            {
                Dictionary<string, object> result = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(resultPath));
                string message = ReadString(result, "error", "");
                if (!string.IsNullOrWhiteSpace(message)) return message;
            }
            catch { }
            return "The native portrait generator exited with code " + exitCode + ".";
        }

        private static void AppendProcessArgument(StringBuilder builder, string name, string value = null)
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(QuoteProcessArgument(name));
            if (value != null) builder.Append(' ').Append(QuoteProcessArgument(value));
        }

        private static string QuoteProcessArgument(string value) =>
            "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";

        private static void RequirePortraitSourceContract(byte[] bytes, string label)
        {
            byte[] rgba = PngReencode.DecodeToRgba(bytes, out int width, out int height);
            if (rgba == null || width != NativePortraitSourceWidth || height != NativePortraitSourceHeight)
            {
                throw new InvalidDataException(label + " must be an opaque 768x1024 PNG produced by the Reign native source contract.");
            }
            for (int alpha = 3; alpha < rgba.Length; alpha += 4)
            {
                if (rgba[alpha] != 255)
                {
                    throw new InvalidDataException(label + " must be fully opaque.");
                }
            }
        }

        private static string PortraitCompositionError(PortraitFaceFocus focus)
        {
            return PortraitDerivativeCore.PortraitCompositionError(focus);
        }

        private static PortraitProductValidation NormalizeAndValidateGeneratedPortraitProduct(
            byte[] imageBytes,
            out byte[] normalizedImage)
        {
            byte[] canonical = PngReencode.ToPngEncoderFormat(imageBytes);
            byte[] rgba = PngReencode.DecodeToRgba(canonical, out int width, out int height);
            if (rgba == null || width < 768 || height < 1024 || width >= height)
            {
                throw new InvalidDataException("Generated portrait master must be a decodable portrait-oriented PNG of at least 768x1024.");
            }
            double aspect = (double)width / height;
            bool normalized = false;
            if (aspect >= 0.52d && aspect < 0.64d)
            {
                PortraitFaceFocus originalFocus = DetectPortraitFaceFocus(rgba, width, height);
                if (originalFocus.CandidateCount != 1)
                {
                    throw new InvalidDataException("Generated portrait must contain exactly one detectable face before its provider-specific canvas can be normalized.");
                }
                int targetHeight = Math.Min(height, (int)Math.Round(width / (2d / 3d)));
                int excess = height - targetHeight;
                int desiredFaceTop = (int)Math.Round(targetHeight * 0.08d);
                int cropY = Math.Max(0, Math.Min(excess,
                    (int)Math.Round(originalFocus.Y * height) - desiredFaceTop));
                rgba = CropPortraitRgba(rgba, width, height, 0, cropY, width, targetHeight);
                height = targetHeight;
                canonical = PngEncoder.EncodeRgba(rgba, width, height);
                aspect = (double)width / height;
                normalized = true;
            }
            if (aspect < 0.64d || aspect > 0.77d)
            {
                throw new InvalidDataException("Generated portrait master must use, or be safely normalizable to, the approved 2:3 through 3:4 portrait aspect range.");
            }

            PortraitFaceFocus focus = DetectPortraitFaceFocus(rgba, width, height);
            string compositionError = PortraitCompositionError(focus);
            if (compositionError != null) throw new InvalidDataException(compositionError);
            normalizedImage = canonical;
            return new PortraitProductValidation
            {
                Width = width,
                Height = height,
                Focus = focus,
                Normalized = normalized
            };
        }

        private static byte[] CropPortraitRgba(
            byte[] source,
            int sourceWidth,
            int sourceHeight,
            int cropX,
            int cropY,
            int cropWidth,
            int cropHeight)
        {
            if (source == null || source.Length != sourceWidth * sourceHeight * 4
                || cropX < 0 || cropY < 0 || cropWidth <= 0 || cropHeight <= 0
                || cropX + cropWidth > sourceWidth || cropY + cropHeight > sourceHeight)
            {
                throw new InvalidDataException("Generated portrait could not be cropped to the canonical product canvas.");
            }
            byte[] cropped = new byte[cropWidth * cropHeight * 4];
            int rowBytes = cropWidth * 4;
            for (int row = 0; row < cropHeight; row++)
            {
                Buffer.BlockCopy(
                    source,
                    ((cropY + row) * sourceWidth + cropX) * 4,
                    cropped,
                    row * rowBytes,
                    rowBytes);
            }
            return cropped;
        }

        private static Dictionary<string, object> BuildPortraitProductReceipt(
            PortraitProductValidation validation,
            byte[] sourceImage,
            string effectivePrompt,
            PortraitImageCallResult call,
            Dictionary<string, object> sourceProvenance)
        {
            if (validation == null || !PortraitDerivativeCore.IsValidFocus(validation.Focus)
                || validation.Focus.CandidateCount != 1)
            {
                throw new InvalidDataException(
                    "A portrait product receipt requires exactly one valid server-detected face.");
            }

            PortraitFaceFocus focus = validation.Focus;
            return new Dictionary<string, object>
            {
                ["schema"] = PortraitProductReceiptSchema,
                ["version"] = PortraitProductReceiptVersion,
                ["accepted"] = true,
                ["provider"] = call == null ? "" : call.Provider,
                ["model"] = call == null ? "" : call.Model,
                ["adapter"] = call == null ? "" : call.Adapter,
                ["master"] = new Dictionary<string, object>
                {
                    ["width"] = validation.Width,
                    ["height"] = validation.Height,
                    ["sha256"] = Sha256Hex(call == null ? null : call.ImageBytes),
                    ["normalizedProviderCanvas"] = validation.Normalized
                },
                ["source"] = new Dictionary<string, object>
                {
                    ["width"] = NativePortraitSourceWidth,
                    ["height"] = NativePortraitSourceHeight,
                    ["sha256"] = Sha256Hex(sourceImage),
                    ["renderContract"] = ReadString(sourceProvenance, "renderContract", NativePortraitRenderContract),
                    ["provenance"] = sourceProvenance ?? new Dictionary<string, object>()
                },
                ["prompt"] = new Dictionary<string, object>
                {
                    ["sha256"] = Sha256Hex(Encoding.UTF8.GetBytes(effectivePrompt ?? "")),
                    ["characters"] = (effectivePrompt ?? "").Length
                },
                ["focus"] = new Dictionary<string, object>
                {
                    ["method"] = focus.Method ?? "",
                    ["model"] = focus.Model ?? "",
                    ["confidence"] = focus.Confidence,
                    ["candidateCount"] = focus.CandidateCount,
                    ["x"] = focus.X,
                    ["y"] = focus.Y,
                    ["width"] = focus.Width,
                    ["height"] = focus.Height
                },
                ["derivativeContract"] = new Dictionary<string, object>
                {
                    ["version"] = PortraitDerivativeCore.CurrentVersion,
                    ["cropAlgorithmVersion"] = PortraitDerivativeCore.CurrentCropAlgorithmVersion,
                    ["faceCropHeightMultiplier"] = 2.15d,
                    ["legacyFallbackAllowed"] = false
                },
                ["generatedUtc"] = DateTime.UtcNow.ToString("o")
            };
        }

        private static Dictionary<string, object> BuildPortraitInputReceipt(
            byte[] sourceImage,
            Dictionary<string, object> sourceProvenance)
        {
            return new Dictionary<string, object>
            {
                ["schema"] = PortraitInputReceiptSchema,
                ["version"] = PortraitProductReceiptVersion,
                ["renderContract"] = ReadString(sourceProvenance, "renderContract", NativePortraitRenderContract),
                ["width"] = NativePortraitSourceWidth,
                ["height"] = NativePortraitSourceHeight,
                ["sha256"] = Sha256Hex(sourceImage),
                ["provenance"] = sourceProvenance ?? new Dictionary<string, object>()
            };
        }

        private static string ApplyPortraitProductPromptContract(string prompt)
        {
            return (prompt ?? "").Trim() + @"

REIGN PORTRAIT PRODUCT CONTRACT - PROVIDER INDEPENDENT
The supplied image is a standardized 768 by 1024 native identity source. Preserve that identity; do not copy its black background or game-render style.
Return one portrait-oriented 2:3 or 3:4 image containing exactly one upright person, centered and facing the camera.
Show the complete head-to-toe figure, including both feet, while keeping the person large enough for all Reign portrait uses.
The face must occupy 9 to 18 percent of the full image height, with the top of the face 3.5 to 18 percent below the image top and its center on the middle third.
Keep only a small margin above the hair and below the feet. Do not zoom out to emphasize architecture, floor, sky, doorway, or scenery.
This composition is mandatory even if the model normally chooses a wider environmental portrait.";
        }
    }
}
