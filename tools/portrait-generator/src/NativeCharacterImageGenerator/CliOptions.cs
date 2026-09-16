using System.Globalization;
using System.Numerics;

namespace Bannerlord.NativeCharacterImageGenerator;

internal sealed class CliOptions
{
    public string? GamePath { get; private set; }
    public string? PackagePath { get; private set; }
    public string? CharacterId { get; private set; }
    public List<string> AssetNames { get; } = [];
    public string OutputPath { get; private set; } = Path.Combine("artifacts", "head_male_a.png");
    public int Width { get; private set; } = 768;
    public int Height { get; private set; } = 1024;
    public float YawDegrees { get; private set; }
    public float PitchDegrees { get; private set; }
    public float Zoom { get; private set; } = 2.9f;
    public bool Transparent { get; private set; }
    public bool ListMorphs { get; private set; }
    public bool VerifyAiCacheCatalog { get; private set; }
    public bool ShowHelp { get; private set; }
    public Vector4 SkinColor { get; private set; } = new(0.72f, 0.47f, 0.34f, 1f);
    public Dictionary<int, float> MorphWeights { get; } = new();

    public static CliOptions Parse(string[] args)
    {
        var result = new CliOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "render":
                    break;
                case "--game":
                    result.GamePath = ReadValue(args, ref index, argument);
                    break;
                case "--package":
                    result.PackagePath = ReadValue(args, ref index, argument);
                    break;
                case "--character":
                    result.CharacterId = ReadValue(args, ref index, argument);
                    break;
                case "--asset":
                    result.AssetNames.Add(ReadValue(args, ref index, argument));
                    break;
                case "--output":
                    result.OutputPath = ReadValue(args, ref index, argument);
                    break;
                case "--width":
                    result.Width = ParseInt(ReadValue(args, ref index, argument), argument, 64, 4096);
                    break;
                case "--height":
                    result.Height = ParseInt(ReadValue(args, ref index, argument), argument, 64, 4096);
                    break;
                case "--yaw":
                    result.YawDegrees = ParseFloat(ReadValue(args, ref index, argument), argument, -180, 180);
                    break;
                case "--pitch":
                    result.PitchDegrees = ParseFloat(ReadValue(args, ref index, argument), argument, -89, 89);
                    break;
                case "--zoom":
                    result.Zoom = ParseFloat(ReadValue(args, ref index, argument), argument, 0.65f, 4f);
                    break;
                case "--morph":
                    result.AddMorph(ReadValue(args, ref index, argument));
                    break;
                case "--skin-color":
                    result.SkinColor = ParseColor(ReadValue(args, ref index, argument));
                    break;
                case "--transparent":
                    result.Transparent = true;
                    break;
                case "--list-morphs":
                    result.ListMorphs = true;
                    break;
                case "--verify-ai-cache-catalog":
                    result.VerifyAiCacheCatalog = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    result.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {argument}");
            }
        }

        if (result.AssetNames.Count == 0)
        {
            result.AssetNames.Add("head_male_a");
        }

        return result;
    }

    private void AddMorph(string value)
    {
        var separator = value.IndexOf('=');
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new ArgumentException("Morphs must use NAME=WEIGHT or INDEX=WEIGHT syntax.");
        }

        var key = value[..separator];
        var weight = ParseFloat(value[(separator + 1)..], "--morph", -3, 3);
        int frameIndex;
        if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out frameIndex)
            && !FaceMorphCatalog.TryGetIndex(key, out frameIndex))
        {
            throw new ArgumentException($"Unknown facial morph '{key}'. Use --list-morphs to see supported names.");
        }

        MorphWeights[frameIndex] = weight;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        return args[index];
    }

    private static int ParseInt(string value, string option, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < min || parsed > max)
        {
            throw new ArgumentException($"{option} must be an integer from {min} to {max}.");
        }

        return parsed;
    }

    private static float ParseFloat(string value, string option, float min, float max)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || parsed < min || parsed > max)
        {
            throw new ArgumentException($"{option} must be a number from {min} to {max}.");
        }

        return parsed;
    }

    private static Vector4 ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            throw new ArgumentException("--skin-color must be a six-digit RGB hex value, such as C47F5B.");
        }

        return new Vector4(
            ((rgb >> 16) & 0xff) / 255f,
            ((rgb >> 8) & 0xff) / 255f,
            (rgb & 0xff) / 255f,
            1f);
    }
}
