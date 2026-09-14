using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string PortraitBodyPromptFile = "portrait_body_appearance.txt";
        private static bool IsCharacterPortraitProfile(string name) => string.Equals(name, "portrait", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "adultPortrait", StringComparison.OrdinalIgnoreCase);
        private const string PortraitBodyPromptDefault = @"NATIVE BODY APPEARANCE — AUTHORITATIVE PHYSIQUE
Native weight: [NATIVE WEIGHT] on a 0–1 scale: [WEIGHT DESCRIPTION].
Native build: [NATIVE BUILD] on a 0–1 scale: [BUILD DESCRIPTION].
These are independent native appearance sliders, not kilograms, BMI, clinical classifications or body-fat percentages. Respect the exact position within each range, without abrupt changes at descriptive boundaries.
Weight controls body-fat fullness; build controls muscle mass. High weight does not imply muscle. High build does not imply extra fat or automatically visible abdominal definition; body fat and clothing can obscure muscle definition.
Match these native proportions even if another prompt or the supplied AI portrait conflicts. Preserve native age, sex, skeletal proportions, face and identity. Do not infer breast size, enlarge curves, beautify, slim or bulk up the person beyond these values.
Garments must fit this physique. Revealingness and physical confidence change clothing coverage only, never body size. Covered muscles need not be exposed. During a clothing edit, correct conflicting AI physique to these native values while preserving the person's face and identity. Never print these instructions or values in the image.";

        private static double NativePhysiqueUnit(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out object raw) || raw == null
                || !(raw is double || raw is float || raw is decimal || raw is int || raw is long))
                throw new InvalidDataException("Native physique " + key + " must be a numeric 0–1 value.");
            double value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1)
                throw new InvalidDataException("Native physique " + key + " must be between 0 and 1, not a percentage.");
            return value;
        }

        private static Dictionary<string, object> ValidateNativePhysique(Dictionary<string, object> data, byte[] source)
        {
            if (data == null || ReadString(data, "schema", "") != "reign-native-physique-v1"
                || string.IsNullOrWhiteSpace(ReadString(data, "weightSource", ""))
                || string.IsNullOrWhiteSpace(ReadString(data, "buildSource", ""))
                || !string.Equals(ReadString(data, "sourceSha256", ""), Sha256Hex(source), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native physique metadata is missing or does not match the rendered source.");
            NativePhysiqueUnit(data, "weight"); NativePhysiqueUnit(data, "build");
            return new Dictionary<string, object>(data);
        }

        private static string PhysiqueDescription(double value, bool muscle)
        {
            int band = value < .2 ? 0 : value < .4 ? 1 : value < .6 ? 2 : value < .8 ? 3 : 4;
            return (muscle ? new[] { "Low muscle mass", "Light musculature", "Moderate musculature", "Muscular", "Very muscular" }
                : new[] { "Very slim, minimal body fat", "Lean", "Medium body-fat level", "Heavyset, increased body fat", "Very heavyset, substantial body fat" })[band];
        }

        private static Dictionary<string, object> BuildPortraitPhysiqueEvidence(Dictionary<string, object> native, string template)
        {
            double weight = NativePhysiqueUnit(native, "weight"), build = NativePhysiqueUnit(native, "build");
            if (string.IsNullOrWhiteSpace(template)) throw new InvalidDataException("The body appearance prompt is empty.");
            string[] tokens = { "[NATIVE WEIGHT]", "[NATIVE BUILD]", "[WEIGHT DESCRIPTION]", "[BUILD DESCRIPTION]" };
            foreach (string token in tokens) if (!template.Contains(token)) throw new InvalidDataException("Body appearance prompt is missing " + token);
            string layer = template.Trim().Replace(tokens[0], weight.ToString("0.########", CultureInfo.InvariantCulture))
                .Replace(tokens[1], build.ToString("0.########", CultureInfo.InvariantCulture))
                .Replace(tokens[2], PhysiqueDescription(weight, false)).Replace(tokens[3], PhysiqueDescription(build, true));
            return new Dictionary<string, object> {
                ["schema"] = "reign-portrait-physique-v1", ["weight"] = weight, ["build"] = build,
                ["weightSource"] = ReadString(native, "weightSource", ""), ["buildSource"] = ReadString(native, "buildSource", ""),
                ["weightDescription"] = PhysiqueDescription(weight, false), ["buildDescription"] = PhysiqueDescription(build, true),
                ["sourceSha256"] = ReadString(native, "sourceSha256", ""), ["bodyLayer"] = layer,
                ["bodyLayerSha256"] = Sha256Hex(Encoding.UTF8.GetBytes(layer))
            };
        }

        private static string AppendPortraitPhysique(string prompt, Dictionary<string, object> payload)
        {
            var evidence = ReadDictionary(payload, "resolvedPortraitPhysique");
            if (evidence == null) return prompt; // Pure legacy prompt-preview callers have no resolved source.
            string layer = ReadString(evidence, "bodyLayer", "");
            if (string.IsNullOrWhiteSpace(layer)) throw new InvalidDataException("Resolved physique has no body layer.");
            return (prompt ?? "").Replace(layer, "").TrimEnd() + "\n\n" + layer;
        }
    }
}
