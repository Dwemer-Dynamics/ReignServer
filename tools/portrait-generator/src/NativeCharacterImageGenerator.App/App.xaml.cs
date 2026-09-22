using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Bannerlord.NativeCharacterImageGenerator;

namespace Bannerlord.NativeCharacterImageGenerator.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (BackgroundPortraitSourceCommand.IsRequested(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int exitCode = await BackgroundPortraitSourceCommand.RunAsync(e.Args).ConfigureAwait(true);
            Shutdown(exitCode);
            return;
        }

        new MainWindow().Show();
    }
}

internal static class BackgroundPortraitSourceCommand
{
    private const string Command = "--reign-render-portrait-source";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(value => value.Equals(Command, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        string resultPath = Value(args, "--result");
        try
        {
            string outputPath = Required(args, "--output");
            string heroId = Value(args, "--hero-id");
            string characterObjectId = Value(args, "--character-object-id");
            string cacheKey = Value(args, "--cache-key");
            string campaignId = Value(args, "--campaign-id");
            string reignDataRoot = Value(args, "--reign-data-root");
            string gameRoot = GameInstallation.ResolveRoot(Value(args, "--game"));
            var nativeCatalog = CharacterCatalog.Load(gameRoot);
            var reignCatalog = ReignCharacterCatalog.Merge(
                gameRoot,
                nativeCatalog.Characters,
                campaignId,
                reignDataRoot);
            string snapshotPath = Value(args, "--character-snapshot");
            using var snapshot = string.IsNullOrWhiteSpace(snapshotPath) ? null : JsonDocument.Parse(File.ReadAllText(snapshotPath));
            var character = snapshot != null ? PortraitSourceCharacter.FromSnapshot(snapshot.RootElement, campaignId, heroId)
                : reignCatalog.Characters.FirstOrDefault(candidate =>
                (string.IsNullOrWhiteSpace(campaignId)
                    || (campaignId == "_shared" && !candidate.IsCampaignCharacter)
                    || candidate.ReignCampaignId.Equals(campaignId, StringComparison.OrdinalIgnoreCase))
                && ((!string.IsNullOrWhiteSpace(heroId)
                        && candidate.Id.Equals(heroId, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(characterObjectId)
                        && candidate.CharacterObjectId.Equals(characterObjectId, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(cacheKey)
                        && CharacterCacheKey.Build(candidate.Name,
                        string.IsNullOrWhiteSpace(candidate.CharacterObjectId)
                            ? candidate.Id
                            : candidate.CharacterObjectId)
                        .Equals(cacheKey, StringComparison.OrdinalIgnoreCase))))
                ?? throw new InvalidDataException(
                    "The requested character was not found in Reign's saved portrait roster.");

            character = PortraitSourceCharacter.WithRenderableClothing(character);

            var profile = PortraitSourceCharacter.RenderProfile(character);
            var frameContract = new NativeEngineFrameContract(
                profile.RenderContractVersion,
                profile.OutputWidth,
                profile.OutputHeight,
                profile.CropScale,
                profile.CropCenterYRatio,
                profile.CameraPitchDegrees);
            var render = await new NativeEngineRenderService(gameRoot).RenderBatchAsync(
                [character],
                profile.RenderWidth,
                profile.RenderHeight,
                progress: null,
                CancellationToken.None,
                reignCatalog.Characters,
                frameContract).ConfigureAwait(true);
            var item = render.Items.Single();
            if (!item.Success || !File.Exists(item.RawOutputPath))
            {
                throw new InvalidDataException(string.IsNullOrWhiteSpace(item.Error)
                    ? "The native portrait worker did not produce a source image."
                    : item.Error);
            }

            ValidateSource(item.RawOutputPath, profile.OutputWidth, profile.OutputHeight);
            using var renderStatus = JsonDocument.Parse(File.ReadAllText(item.StatusPath));
            var effectiveBody = System.Xml.Linq.XElement.Parse(renderStatus.RootElement.GetProperty("effectiveBodyProperties").GetString()!);
            float effectiveWeight = float.Parse(effectiveBody.Attribute("weight")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            float effectiveBuild = float.Parse(effectiveBody.Attribute("build")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (!float.IsFinite(effectiveWeight) || effectiveWeight < 0 || effectiveWeight > 1
                || !float.IsFinite(effectiveBuild) || effectiveBuild < 0 || effectiveBuild > 1)
                throw new InvalidDataException("Rendered physique is outside the native 0-1 range.");
            WriteFileAtomic(outputPath, await File.ReadAllBytesAsync(item.RawOutputPath).ConfigureAwait(true));
            WriteResult(resultPath, new
            {
                ok = true,
                campaignId,
                heroStringId = character.Id,
                characterObjectId = character.CharacterObjectId,
                cacheKey,
                outputPath = Path.GetFullPath(outputPath),
                width = profile.OutputWidth,
                height = profile.OutputHeight,
                renderContract = profile.RenderContractVersion,
                renderContractHash = profile.RenderContractHash,
                headgearSuppressed = !character.PreserveEncounteredOutfit,
                background = "black",
                physique = new {
                    schema = "reign-native-physique-v1", weight = effectiveWeight, build = effectiveBuild,
                    weightSource = string.IsNullOrWhiteSpace(character.BodyKey) ? "native_engine_resolved" : character.WeightSource,
                    buildSource = string.IsNullOrWhiteSpace(character.BodyKey) ? "native_engine_resolved" : character.BuildSource,
                    sourceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(item.RawOutputPath))).ToLowerInvariant()
                },
                generatedUtc = DateTime.UtcNow
            });
            return 0;
        }
        catch (Exception exception)
        {
            WriteResult(resultPath, new { ok = false, error = exception.Message, generatedUtc = DateTime.UtcNow });
            return 1;
        }
    }

    private static void ValidateSource(string path, int expectedWidth, int expectedHeight)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.Single();
        if (frame.PixelWidth != expectedWidth || frame.PixelHeight != expectedHeight)
        {
            throw new InvalidDataException(
                $"Native source must be {expectedWidth}x{expectedHeight}; received {frame.PixelWidth}x{frame.PixelHeight}.");
        }
    }

    private static void WriteFileAtomic(string path, byte[] bytes)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string incoming = path + ".incoming-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(incoming, bytes);
        File.Move(incoming, path, true);
    }

    private static void WriteResult(string path, object value)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        WriteFileAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }));
    }

    private static string Required(IReadOnlyList<string> args, string name)
    {
        string value = Value(args, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Missing required argument " + name + ".")
            : value;
    }

    private static string Value(IReadOnlyList<string> args, string name)
    {
        for (int index = 0; index + 1 < args.Count; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        }
        return string.Empty;
    }
}
