using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string StoreGeneratedSharedPortrait(Dictionary<string, object> payload, string cacheKey, string prompt,
            byte[] sourceImage, byte[] imageBytes, PortraitImageCallResult call,
            Dictionary<string, object> physicalConfidence, Dictionary<string, object> productReceipt)
        {
            if (!TryLoadSharedPortraitEntry(cacheKey, out SharedPortraitEntry entry, out string error)) throw new InvalidDataException(error);
            var provenance = ReadDictionary(ReadDictionary(productReceipt, "source"), "provenance");
            if (ReadString(provenance, "kind", "") == "shared_ai_portrait_reference"
                && (!File.Exists(entry.PortraitPath) || Sha256Hex(File.ReadAllBytes(entry.PortraitPath)) != Sha256Hex(sourceImage)))
                throw new InvalidDataException("Shared master changed during generation; refusing to overwrite the newer portrait.");
            string result = null;
            WithSharedProductRollback(entry.Folder, () => result = StoreGeneratedSharedPortraitCore(payload, cacheKey,
                prompt, sourceImage, imageBytes, call, physicalConfidence, productReceipt));
            return result;
        }

        private static void WithSharedProductRollback(string folder, Action write)
        {
            var previous = SharedPortraitGeneratedArtifactFileNames.Concat(new[] { "portrait_input.json" })
                .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(name => Path.Combine(folder, name),
                    name => File.Exists(Path.Combine(folder, name)) ? File.ReadAllBytes(Path.Combine(folder, name)) : null);
            var attributes = previous.Where(x => x.Value != null).ToDictionary(x => x.Key, x => File.GetAttributes(x.Key));
            var timestamps = previous.Where(x => x.Value != null).ToDictionary(x => x.Key, x => File.GetLastWriteTimeUtc(x.Key));
            try {
                foreach (var item in attributes) File.SetAttributes(item.Key, item.Value & ~FileAttributes.ReadOnly);
                write();
            }
            catch (Exception failure)
            {
                var errors = new System.Collections.Generic.List<Exception> { failure };
                foreach (var item in previous)
                    try {
                        if (item.Value != null) {
                            WriteSharedPortraitAtomic(item.Key, item.Value);
                            File.SetLastWriteTimeUtc(item.Key, timestamps[item.Key]);
                        }
                        else if (File.Exists(item.Key)) { File.SetAttributes(item.Key, FileAttributes.Normal); File.Delete(item.Key); }
                    } catch (Exception restore) { errors.Add(restore); }
                if (errors.Count > 1) throw new AggregateException("Shared portrait rollback requires attention.", errors);
                throw;
            }
            finally {
                foreach (var item in attributes) if (File.Exists(item.Key)) File.SetAttributes(item.Key, item.Value);
            }
        }

        private static Dictionary<string, object> BindSharedPortraitPhysique(Dictionary<string, object> metadata, byte[] master)
        {
            var saved = ReadDictionary(metadata, "nativePhysique");
            if (saved == null || ReadString(saved, "schema", "") != "reign-native-physique-v1"
                || string.IsNullOrWhiteSpace(ReadString(saved, "weightSource", ""))
                || string.IsNullOrWhiteSpace(ReadString(saved, "buildSource", "")))
                throw new InvalidDataException("Shared portrait rebuild requires saved native physique metadata.");
            double weight = NativePhysiqueUnit(saved, "weight"), build = NativePhysiqueUnit(saved, "build");
            if (metadata.ContainsKey("bodyWeight") && NativePhysiqueUnit(metadata, "bodyWeight") != weight
                || metadata.ContainsKey("bodyBuild") && NativePhysiqueUnit(metadata, "bodyBuild") != build)
                throw new InvalidDataException("Shared portrait native physique conflicts with its saved character data.");
            var bound = new Dictionary<string, object>(saved) {
                ["sourceSha256"] = Sha256Hex(master),
                ["referenceKind"] = "existing_ai_portrait",
                ["savedSourceSha256"] = ReadString(saved, "sourceSha256", ""),
                ["rendererReexecuted"] = false
            };
            return ValidateNativePhysique(bound, master);
        }

        private static string BuildSharedPortraitRebuildPrompt(Dictionary<string, object> payload)
        {
            return AppendPortraitPhysique("EDIT THE SUPPLIED EXISTING FULL-BODY AI PORTRAIT. "
                + "Preserve the exact person's face, identity, age, sex, hairstyle, expression, skeletal proportions, pose, "
                + "background, lighting and photographic style. Preserve the existing clothing design, colors, materials, "
                + "ornaments and coverage. Adjust only physique to the authoritative native weight and build below, "
                + "and adjust the fit of those same clothes accordingly. Do not replace the outfit, change revealingness, "
                + "beautify the face or reinterpret the character. Keep the full head and both feet in frame. No text or overlays.", payload);
        }
    }
}
