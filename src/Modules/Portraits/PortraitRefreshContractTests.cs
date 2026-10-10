using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunPortraitRefreshContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string name, Func<bool> run)
            {
                try { AddSharedPortraitSelfTest(rows, "portrait_refresh_" + name, run(), "Provider-free portrait refresh contract."); }
                catch (Exception ex) { AddSharedPortraitSelfTest(rows, "portrait_refresh_" + name, false, ex.Message); }
            }
            bool Reject(Action run) { try { run(); return false; } catch { return true; } }
            Dictionary<string, object> Request(double age) => new Dictionary<string, object> {
                ["campaignId"] = "portrait_refresh_fixture", ["heroStringId"] = "hero",
                ["portraitRefreshMode"] = "reflect_years",
                ["nativeCharacterSnapshot"] = new Dictionary<string, object> {
                    ["schema"] = "reign-native-portrait-snapshot-v1", ["campaignId"] = "portrait_refresh_fixture",
                    ["heroStringId"] = "hero", ["age"] = age
                }
            };
            Check("current_native_age_overrides_stale_metadata", () => {
                var p = Request(67.9); p["ageYears"] = 21;
                return PortraitReflectionAge(p) == 67;
            });
            foreach (double age in new[] { 17.99, -1, 151, double.NaN, double.PositiveInfinity })
                Check("reject_age_" + age, () => Reject(() => PortraitReflectionAge(Request(age))));
            Check("missing_snapshot_rejected", () => Reject(() => PortraitReflectionAge(new Dictionary<string, object>())));
            Check("foreign_snapshot_rejected", () => {
                var p = Request(50); ReadDictionary(p, "nativeCharacterSnapshot")["heroStringId"] = "other";
                return Reject(() => PortraitReflectionAge(p));
            });
            Check("shared_library_refresh_rejected", () => {
                var p = Request(50); p["sharedCacheOutput"] = true; return Reject(() => PortraitRefreshMode(p));
            });
            Check("unknown_choice_rejected", () => Reject(() => PortraitRefreshMode(new Dictionary<string, object> { ["portraitRefreshMode"] = "redesign" })));
            Check("look_again_remains_native_generation", () => {
                var p = Request(50); p["portraitRefreshMode"] = "look_again";
                byte[] native = { 1, 2 }, supplied = { 9 };
                return PortraitRefreshMode(p) == "look_again" && ReferenceEquals(native, ResolveImageRequestSource("portrait", supplied, () => native));
            });
            Check("age_only_prompt_preserves_scene_and_clothes", () => {
                string prompt = PortraitReflectionPrompt(67);
                return prompt.Contains("CURRENT AGE of 67") && prompt.Contains("Change absolutely nothing else")
                    && new[] { "clothing", "pose", "framing", "lighting", "background", "facial identity", "private guidance" }.All(prompt.Contains);
            });
            Check("adult_provider_uses_configured_profile", () => {
                var settings = new Dictionary<string, object> { ["portraitProvider"] = "NanoGPT", ["adultPortraitProvider"] = "AtlasCloud" };
                var profile = ResolveImageGenerationProfile(settings, "adultPortrait");
                return profile.Name == "adultPortrait" && profile.Provider == "AtlasCloud";
            });
            Check("exact_ai_reference_and_age_provenance", () => {
                var p = Request(45); byte[] pixels = new byte[864 * 1152 * 4];
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                byte[] image = PngEncoder.EncodeRgba(pixels, 864, 1152);
                p["sourceImageBase64"] = Convert.ToBase64String(image);
                byte[] resolved = PortraitReflectionSource(p, out var evidence);
                return resolved.SequenceEqual(image) && ReadInt(evidence, "width", 0) == 864 && ReadInt(evidence, "height", 0) == 1152
                    && ReadString(evidence, "kind", "") == "portrait_age_edit"
                    && ReadInt(evidence, "currentAgeYears", 0) == 45 && ReadString(evidence, "referenceSha256", "") == Sha256Hex(image);
            });
            Check("invalid_reference_rejected_before_provider", () => {
                var p = Request(50); p["sourceImageBase64"] = Convert.ToBase64String(new byte[] { 1, 2, 3 });
                return Reject(() => PortraitReflectionSource(p, out var _));
            });
            Check("custom_override_replaced_and_other_directories_preserved", () => {
                string root = Path.Combine(Path.GetTempPath(), "reign-portrait-refresh-" + Guid.NewGuid().ToString("N"));
                string target = Path.Combine(root, "current"), other = Path.Combine(root, "other"), shared = Path.Combine(root, "_shared");
                try {
                    foreach (string dir in new[] { target, other, shared }) { Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, "custom.png"), new byte[] { 1 }); }
                    ReplaceRefreshedCharacterOverrideInDirectory(target, new byte[] { 2 }, new Dictionary<string, object>(), WriteSharedPortraitAtomic);
                    return File.ReadAllBytes(Path.Combine(target, "custom.png"))[0] == 2
                        && File.ReadAllBytes(Path.Combine(other, "custom.png"))[0] == 1 && File.ReadAllBytes(Path.Combine(shared, "custom.png"))[0] == 1;
                } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            });
            Check("custom_override_rolls_back_failed_receipt", () => {
                string root = Path.Combine(Path.GetTempPath(), "reign-portrait-refresh-" + Guid.NewGuid().ToString("N"));
                try {
                    Directory.CreateDirectory(root); string custom = Path.Combine(root, "custom.png"); File.WriteAllBytes(custom, new byte[] { 1 });
                    int writes = 0;
                    bool rejected = Reject(() => ReplaceRefreshedCharacterOverrideInDirectory(root, new byte[] { 2 }, new Dictionary<string, object>(),
                        (path, bytes) => { if (++writes == 2) throw new IOException("Injected receipt failure"); WriteSharedPortraitAtomic(path, bytes); }));
                    return rejected && File.ReadAllBytes(custom)[0] == 1 && !File.Exists(Path.Combine(root, OutfitPortraitReceiptFileName));
                } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            });
            return rows;
        }
    }
}
