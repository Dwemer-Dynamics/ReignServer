using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AIPortraits;

namespace ReignBetaServer
{
    internal static partial class PortraitsService
    {
        private const string PortraitRefreshPublicationKey = "refreshPublication";

        private sealed record PortraitRefreshBaseline(string Directory, string Master, string Custom,
            string MasterMetadata, string Outfit, string OutfitConfirmation, string CustomReceipt);

        private static PortraitRefreshBaseline CapturePortraitRefreshBaseline(string directory)
        {
            lock (Program.FileLock)
            {
                return new PortraitRefreshBaseline(directory, PortraitRefreshFileDigest(directory, "portrait.png"),
                    PortraitRefreshFileDigest(directory, "custom.png"), PortraitRefreshFileDigest(directory, "portrait.json"),
                    PortraitRefreshFileDigest(directory, "outfit.json"), PortraitRefreshFileDigest(directory, OutfitConfirmationFileName),
                    PortraitRefreshFileDigest(directory, OutfitPortraitReceiptFileName));
            }
        }

        private static string PortraitRefreshFileDigest(string directory, string name)
        {
            try
            {
                using var file = File.OpenRead(Path.Combine(directory, name));
                return Convert.ToHexString(SHA256.HashData(file));
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static string PublishPortraitRefresh(PortraitRefreshBaseline baseline, byte[] image,
            Dictionary<string, object> receipt, Func<Dictionary<string, object>, string> storePortrait)
        {
            lock (Program.FileLock)
            {
                // Check the current campaign bytes before either the fallback master or the custom pair can change.
                // The v2 marker also distinguishes a newer outfit confirmation with identical image bytes.
                if (CapturePortraitRefreshBaseline(baseline.Directory) != baseline)
                    throw new InvalidOperationException("The portrait or outfit changed while the refresh was running. Start a new refresh.");

                // Record the completed bytes before writing. An interrupted custom replacement cannot pass recovery.
                string master = Convert.ToHexString(SHA256.HashData(image));
                var publication = PortraitRefreshPublication(master,
                    baseline.Custom == null ? null : master,
                    baseline.Custom == null ? baseline.CustomReceipt : Convert.ToHexString(SHA256.HashData(PortraitRefreshReceiptBytes(receipt))),
                    baseline.Outfit, baseline.OutfitConfirmation);
                string storedPath = storePortrait(publication);
                ReplaceRefreshedCharacterOverrideInDirectory(baseline.Directory, image, receipt, WriteSharedPortraitAtomic);
                return storedPath;
            }
        }

        private static Dictionary<string, object> PortraitRefreshPublication(string master, string custom,
            string customReceipt, string outfit, string marker)
            => new Dictionary<string, object>
            {
                ["schema"] = "reign-portrait-refresh-publication-v1",
                ["masterSha256"] = master,
                ["customSha256"] = custom ?? "",
                ["customReceiptSha256"] = customReceipt ?? "",
                ["outfitSha256"] = outfit ?? "",
                ["outfitConfirmationSha256"] = marker ?? ""
            };

        private static bool PortraitRefreshPublicationMatches(string directory, byte[] master, object witness)
        {
            if (witness is not Dictionary<string, object> expected) return false;
            var actual = PortraitRefreshPublication(Convert.ToHexString(SHA256.HashData(master)),
                PortraitRefreshFileDigest(directory, "custom.png"), PortraitRefreshFileDigest(directory, OutfitPortraitReceiptFileName),
                PortraitRefreshFileDigest(directory, "outfit.json"), PortraitRefreshFileDigest(directory, OutfitConfirmationFileName));
            return actual.Count == expected.Count && actual.All(pair =>
                expected.TryGetValue(pair.Key, out object value) && value is string text && text == (string)pair.Value);
        }

        private static byte[] PortraitRefreshReceiptBytes(Dictionary<string, object> receipt)
            => Encoding.UTF8.GetBytes(Json.Serialize(receipt));

        private static void ReplaceRefreshedCharacterOverrideInDirectory(string directory, byte[] image,
            Dictionary<string, object> receipt, Action<string, byte[]> write)
        {
            string custom = Path.Combine(directory, "custom.png");
            lock (Program.FileLock)
            {
                if (!File.Exists(custom)) return;
                CommitOutfitFiles(new Dictionary<string, byte[]> {
                    [custom] = image,
                    [Path.Combine(directory, OutfitPortraitReceiptFileName)] = PortraitRefreshReceiptBytes(receipt)
                }, write);
            }
        }

        private static string PortraitRefreshMode(Dictionary<string, object> payload)
        {
            string mode = ReadString(payload, "portraitRefreshMode", "");
            if (mode != "" && mode != "look_again" && mode != "reflect_years")
                throw new InvalidDataException("Unknown portrait refresh choice.");
            if (mode != "" && (ReadBool(payload, "sharedCacheOutput", false)
                || ReadString(payload, "promptPurpose", "portrait") != "portrait"))
                throw new InvalidDataException("Portrait refresh requires a campaign portrait.");
            return mode;
        }

        private static int PortraitReflectionAge(Dictionary<string, object> payload)
        {
            var snapshot = ReadDictionary(payload, "nativeCharacterSnapshot");
            if (snapshot == null || ReadString(snapshot, "schema", "") != "reign-native-portrait-snapshot-v1"
                || string.IsNullOrWhiteSpace(ReadString(payload, "campaignId", ""))
                || string.IsNullOrWhiteSpace(ReadString(payload, "heroStringId", ""))
                || ReadString(snapshot, "campaignId", "") != ReadString(payload, "campaignId", "")
                || ReadString(snapshot, "heroStringId", "") != ReadString(payload, "heroStringId", ""))
                throw new InvalidDataException("A current native character snapshot is required to reflect on the years.");
            if (!snapshot.TryGetValue("age", out object value) || value == null)
                throw new InvalidDataException("The character's current age is unavailable.");
            double age = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(age) || double.IsInfinity(age) || age < 18 || age > 150)
                throw new InvalidDataException("Reflect on the Years requires a valid current adult age.");
            return (int)Math.Floor(age);
        }

        private static string PortraitReflectionPrompt(int age)
        {
            if (age < 18 || age > 150) throw new InvalidDataException("A valid adult age is required.");
            return "Edit the supplied existing AI portrait. Change the character's visible appearance ONLY to match their CURRENT AGE of "
                + age.ToString(CultureInfo.InvariantCulture) + " years old. Change absolutely nothing else. "
                + "Keep the exact same person and recognizable facial identity. Make only natural age-related changes, such as skin texture, wrinkles and age-related greying. "
                + "Preserve facial proportions, eye shape and color, expression, hairstyle, hair length, facial-hair style, skin tone, scars, body shape, clothing, accessories, "
                + "pose, hands, camera angle, composition, framing, lighting, background and image style exactly. "
                + "Do not redesign clothes, remove clothing, add objects, beautify, restyle or change the scene. "
                + "Return only the edited portrait without text, labels, borders or watermarks. The age is private guidance and must never appear in the image.";
        }

        private static byte[] PortraitReflectionSource(Dictionary<string, object> payload, out Dictionary<string, object> provenance)
        {
            int age = PortraitReflectionAge(payload);
            string encoded = ReadString(payload, "sourceImageBase64", "");
            if (encoded.Length == 0 || encoded.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("An existing AI portrait is required to reflect on the years.");
            byte[] source = Convert.FromBase64String(encoded);
            ValidateOutfitImage(source);
            if (PngReencode.DecodeToRgba(source, out int width, out int height) == null)
                throw new InvalidDataException("The existing AI portrait could not be decoded.");
            provenance = new Dictionary<string, object> {
                ["kind"] = "portrait_age_edit", ["renderContract"] = "finished_portrait_age_edit_v1",
                ["width"] = width, ["height"] = height, ["currentAgeYears"] = age,
                ["referenceSha256"] = Sha256Hex(source)
            };
            return source;
        }

        private static PortraitProductValidation ValidatePortraitReflectionProduct(byte[] source, byte[] output, out byte[] normalized)
        {
            ValidateOutfitImage(output);
            byte[] pixels = PngReencode.DecodeToRgba(output, out int width, out int height);
            if (PngReencode.DecodeToRgba(source, out int sourceWidth, out int sourceHeight) == null
                || Math.Abs((double)width / height - (double)sourceWidth / sourceHeight) > .01d)
                throw new InvalidDataException("The age edit changed the portrait's framing. The existing portrait was kept.");
            var focus = DetectPortraitFaceFocus(pixels, width, height);
            string error = PortraitCompositionError(focus);
            if (error != null) throw new InvalidDataException(error);
            // Re-encode only. Never crop, pad, stretch or reframe an age-only edit.
            normalized = PngEncoder.EncodeRgba(pixels, width, height);
            return new PortraitProductValidation { Width = width, Height = height, Focus = focus, Normalized = false };
        }
    }
}
