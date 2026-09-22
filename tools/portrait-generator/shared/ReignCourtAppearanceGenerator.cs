using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Bannerlord.ReignCourtAppearance
{
    internal sealed class ReignCourtAppearanceInput
    {
        public string CharacterId;
        public bool IsFemale;
        public int Race;
        public float Age;
        public float Weight;
        public float Build;
        public MBBodyProperty Range;
        public int[] CultureHairIndices;
        public string MotherId;
        public string FatherId;
    }

    internal sealed class ReignCourtAppearanceResult
    {
        public string CharacterId;
        public BodyProperties BodyProperties;
        public bool Unique;
        public int Attempt;
    }

    internal static class ReignCourtAppearanceGenerator
    {
        public const string Version = "reign_court_shared_v1";
        private const int MinimumFaceSliderValue = -25;
        private const int MaximumFaceSliderValue = 24;
        private const float MinimumFaceWeight = 0.375f;
        private const float MaximumFaceWeight = 0.62f;

        public static Dictionary<string, ReignCourtAppearanceResult> Generate(
            IEnumerable<ReignCourtAppearanceInput> source)
        {
            var inputs = (source ?? Enumerable.Empty<ReignCourtAppearanceInput>())
                .Where(input => input != null && !string.IsNullOrWhiteSpace(input.CharacterId) && input.Range != null)
                .ToDictionary(input => input.CharacterId, StringComparer.OrdinalIgnoreCase);
            var ordered = inputs.Values
                .OrderBy(input => IsChild(input) ? 1 : 0)
                .ThenBy(input => input.CharacterId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var results = new Dictionary<string, ReignCourtAppearanceResult>(StringComparer.OrdinalIgnoreCase);
            var usedFaces = new HashSet<string>(StringComparer.Ordinal);

            foreach (var input in ordered)
            {
                ReignCourtAppearanceResult mother = null;
                ReignCourtAppearanceResult father = null;
                var inherited = IsChild(input)
                    && results.TryGetValue(input.MotherId ?? string.Empty, out mother)
                    && results.TryGetValue(input.FatherId ?? string.Empty, out father);
                BodyProperties fallback = default(BodyProperties);
                var hasFallback = false;
                for (var attempt = 0; attempt < 64; attempt++)
                {
                    var seed = unchecked(StableSeed(input.CharacterId) + attempt * 7919);
                    BodyProperties generated;
                    if (inherited)
                    {
                        generated = BodyProperties.GetRandomBodyProperties(
                            input.Race,
                            input.IsFemale,
                            mother.BodyProperties,
                            father.BodyProperties,
                            1,
                            seed,
                            input.Range.HairTags,
                            input.Range.BeardTags,
                            input.Range.TattooTags,
                            0.15f);
                    }
                    else
                    {
                        generated = BodyProperties.GetRandomBodyProperties(
                            input.Race,
                            input.IsFemale,
                            input.Range.BodyPropertyMin,
                            input.Range.BodyPropertyMax,
                            0,
                            seed,
                            input.Range.HairTags,
                            input.Range.BeardTags,
                            input.Range.TattooTags,
                            0f);
                    }

                    generated = ConstrainGeneratedAppearance(input, generated, seed, inherited);
                    generated = WithDynamicProperties(input, generated.StaticProperties);
                    fallback = generated;
                    hasFallback = true;
                    if (usedFaces.Add(StaticFaceKey(generated.StaticProperties)))
                    {
                        results[input.CharacterId] = new ReignCourtAppearanceResult
                        {
                            CharacterId = input.CharacterId,
                            BodyProperties = generated,
                            Unique = true,
                            Attempt = attempt
                        };
                        break;
                    }
                }

                if (!results.ContainsKey(input.CharacterId) && hasFallback)
                {
                    results[input.CharacterId] = new ReignCourtAppearanceResult
                    {
                        CharacterId = input.CharacterId,
                        BodyProperties = fallback,
                        Unique = false,
                        Attempt = 63
                    };
                }
            }

            return results;
        }

        public static int StableSeed(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var character in value ?? string.Empty)
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return (int)(hash & 0x7FFFFFFF);
            }
        }

        private static BodyProperties ConstrainGeneratedAppearance(
            ReignCourtAppearanceInput input,
            BodyProperties generated,
            int seed,
            bool inheritedFromParents)
        {
            var parameters = FaceGenerationParams.Create();
            MBBodyProperties.GetParamsFromKey(
                ref parameters,
                generated,
                earsAreHidden: false,
                mouthHidden: false);
            var deformKeyCount = MBBodyProperties.GetFaceGenInstancesLength(
                input.Race,
                input.IsFemale ? 1 : 0,
                (int)input.Age);
            var random = new Random(seed);
            for (var keyIndex = 0;
                keyIndex < deformKeyCount && keyIndex < parameters.KeyWeights.Length;
                keyIndex++)
            {
                var key = MBBodyProperties.GetDeformKeyData(
                    keyIndex,
                    input.Race,
                    input.IsFemale ? 1 : 0,
                    (int)input.Age);
                if (IsNonFaceKey(key.Id))
                {
                    continue;
                }
                parameters.KeyWeights[keyIndex] = inheritedFromParents
                    ? Clamp(parameters.KeyWeights[keyIndex] + random.Next(-3, 4) / 200f,
                        MinimumFaceWeight,
                        MaximumFaceWeight)
                    : SliderValueToWeight(random.Next(MinimumFaceSliderValue, MaximumFaceSliderValue + 1));
            }

            parameters.HeightMultiplier = inheritedFromParents
                ? Clamp(parameters.HeightMultiplier, MinimumFaceWeight, MaximumFaceWeight)
                : SliderValueToWeight(random.Next(MinimumFaceSliderValue, MaximumFaceSliderValue + 1));
            MBBodyProperties.EnforceConstraints(ref parameters);
            for (var keyIndex = 0;
                keyIndex < deformKeyCount && keyIndex < parameters.KeyWeights.Length;
                keyIndex++)
            {
                var key = MBBodyProperties.GetDeformKeyData(
                    keyIndex,
                    input.Race,
                    input.IsFemale ? 1 : 0,
                    (int)input.Age);
                if (!IsNonFaceKey(key.Id))
                {
                    parameters.KeyWeights[keyIndex] = Clamp(
                        parameters.KeyWeights[keyIndex],
                        MinimumFaceWeight,
                        MaximumFaceWeight);
                }
            }
            parameters.HeightMultiplier = Clamp(
                parameters.HeightMultiplier,
                MinimumFaceWeight,
                MaximumFaceWeight);

            var constrained = generated;
            MBBodyProperties.ProduceNumericKeyWithParams(
                parameters,
                earsAreHidden: false,
                mouthIsHidden: false,
                ref constrained);
            TaleWorlds.Core.FaceGen.SetHair(
                ref constrained,
                SelectNonBaldHair(input, seed),
                input.IsFemale ? 0 : -1,
                -1);
            return constrained;
        }

        private static int SelectNonBaldHair(ReignCourtAppearanceInput input, int seed)
        {
            var choices = new HashSet<int>();
            AddNonBaldHair(choices, input.CultureHairIndices);
            if (!string.IsNullOrWhiteSpace(input.Range.HairTags))
            {
                AddNonBaldHair(choices, TaleWorlds.Core.FaceGen.GetHairIndicesByTag(
                    input.Race,
                    input.IsFemale ? 1 : 0,
                    input.Age,
                    input.Range.HairTags));
            }
            if (choices.Count == 0)
            {
                return 1;
            }
            var ordered = choices.OrderBy(value => value).ToArray();
            return ordered[new Random(unchecked(seed ^ 0x5F3759DF)).Next(ordered.Length)];
        }

        private static void AddNonBaldHair(HashSet<int> choices, IEnumerable<int> indices)
        {
            if (indices == null)
            {
                return;
            }
            foreach (var index in indices)
            {
                if (index > 0)
                {
                    choices.Add(index);
                }
            }
        }

        private static bool IsChild(ReignCourtAppearanceInput input) =>
            !string.IsNullOrWhiteSpace(input.MotherId) && !string.IsNullOrWhiteSpace(input.FatherId);

        private static bool IsNonFaceKey(string keyId) =>
            string.Equals(keyId, "weight", StringComparison.OrdinalIgnoreCase)
            || string.Equals(keyId, "build", StringComparison.OrdinalIgnoreCase)
            || string.Equals(keyId, "height", StringComparison.OrdinalIgnoreCase)
            || string.Equals(keyId, "age", StringComparison.OrdinalIgnoreCase);

        private static float SliderValueToWeight(int sliderValue) => (sliderValue + 100) / 200f;

        private static float Clamp(float value, float minimum, float maximum) =>
            Math.Max(minimum, Math.Min(maximum, value));

        private static BodyProperties WithDynamicProperties(
            ReignCourtAppearanceInput input,
            StaticBodyProperties staticProperties) =>
            new BodyProperties(
                new DynamicBodyProperties(input.Age, Clamp01(input.Weight), Clamp01(input.Build)),
                staticProperties);

        private static float Clamp01(float value) => Math.Max(0f, Math.Min(1f, value));

        private static string StaticFaceKey(StaticBodyProperties properties) =>
            properties.KeyPart1.ToString("X16") + properties.KeyPart2.ToString("X16")
            + properties.KeyPart3.ToString("X16") + properties.KeyPart4.ToString("X16")
            + properties.KeyPart5.ToString("X16") + properties.KeyPart6.ToString("X16")
            + properties.KeyPart7.ToString("X16") + properties.KeyPart8.ToString("X16");
    }
}
