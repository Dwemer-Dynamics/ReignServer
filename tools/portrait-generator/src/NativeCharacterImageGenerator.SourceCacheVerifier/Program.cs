using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bannerlord.NativeCharacterImageGenerator.App;

namespace Bannerlord.NativeCharacterImageGenerator.SourceCacheVerifier;

internal static class Program
{
    private static readonly string[] SmokeIds =
    [
        "lord_1_14",
        "lord_1_47_1",
        "lord_1_1",
        "lord_7_1",
        "reign_court_castle_EN1_mother"
    ];

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var packageRoot = ReadValue(args, "--package")
                ?? throw new ArgumentException("--package PATH is required.");
            var cancelSeconds = int.TryParse(ReadValue(args, "--cancel-after-seconds"), out var parsed)
                ? Math.Max(1, parsed)
                : 0;
            var renderProfile = ReadRenderProfile(ReadValue(args, "--preset"));
            var gameRoot = GameInstallation.ResolveRoot(ReadValue(args, "--game"));
            var native = CharacterCatalog.Load(gameRoot);
            var completeCatalog = CanonicalCharacterCatalog.Load(gameRoot, native.Characters);
            var byId = completeCatalog.Characters.ToDictionary(
                record => record.HeroStringId,
                StringComparer.OrdinalIgnoreCase);
            var fullBuild = args.Any(arg => arg.Equals("--full", StringComparison.OrdinalIgnoreCase));
            var requestedIds = (ReadValue(args, "--ids") ?? string.Join(',', SmokeIds))
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var smokeRecords = fullBuild
                ? completeCatalog.Characters.Where(AiSourceCacheBuildCoordinator.IsAiPortraitEligible).ToArray()
                : requestedIds.Select(id => byId.TryGetValue(id, out var record)
                        ? record
                        : throw new InvalidDataException("Smoke character was not found: " + id))
                    .Where(AiSourceCacheBuildCoordinator.IsAiPortraitEligible)
                    .ToArray();
            var smokeCatalog = new CanonicalCharacterCatalogResult(
                fullBuild ? completeCatalog.Characters : smokeRecords,
                completeCatalog.CatalogHash,
                completeCatalog.ProfilePackVersion,
                completeCatalog.SourceHashes,
                completeCatalog.ModuleVersions);
            var courtAppearances = completeCatalog.Characters
                .Where(record => record.Character.SourceModule.Equals(
                    "ReignBeta",
                    StringComparison.OrdinalIgnoreCase))
                .Select(record => record.Character)
                .ToArray();
            var progress = new Progress<AiSourceCacheProgress>(update => Console.WriteLine(
                $"{update.Validated}/{update.Total} validated; {update.Rendered} rendered; " +
                $"{update.Skipped} skipped; {update.Failed} failed; {update.Current}"));
            using var cancellation = new CancellationTokenSource();
            if (cancelSeconds > 0)
            {
                cancellation.CancelAfter(TimeSpan.FromSeconds(cancelSeconds));
            }
            var coordinator = new AiSourceCacheBuildCoordinator(
                new NativeEngineRenderService(gameRoot),
                smokeCatalog,
                courtAppearances,
                Path.Combine(gameRoot, "Modules", "ReignBeta", "PortraitCache", "_shared"),
                renderProfile);
            var result = await coordinator.RunAsync(
                Path.GetFullPath(packageRoot),
                progress,
                cancellation.Token);
            ValidatePublishedEntries(result.PackageRoot, smokeRecords, result.Complete, renderProfile);
            Console.WriteLine(JsonSerializer.Serialize(result, AiSourceCacheContract.JsonOptions));
            return result.Complete || result.Canceled ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void ValidatePublishedEntries(
        string packageRoot,
        IReadOnlyList<CanonicalCharacterRecord> records,
        bool requireAll,
        AiSourceCacheRenderProfile renderProfile)
    {
        var published = 0;
        foreach (var record in records)
        {
            var directory = Path.Combine(packageRoot, "_shared", record.CacheKey);
            var source = Path.Combine(directory, "source.png");
            var metadata = Path.Combine(directory, "portrait_input.json");
            if (!File.Exists(source) && !File.Exists(metadata))
            {
                continue;
            }
            if (!File.Exists(source) || !File.Exists(metadata))
            {
                throw new InvalidDataException("A smoke-cache entry was only partially published: " + record.CacheKey);
            }
            var input = JsonSerializer.Deserialize<PortraitGenerationInputV1>(
                File.ReadAllText(metadata),
                AiSourceCacheContract.JsonOptions)
                ?? throw new InvalidDataException("Smoke metadata could not be read: " + record.CacheKey);
            if (!input.HeadgearSuppressed
                || input.CivilianEquipment.Any(item => item.Slot == "Head")
                || input.RenderPreset != renderProfile.RenderContractVersion
                || input.RenderContractHash != renderProfile.RenderContractHash
                || input.RenderWidth != renderProfile.RenderWidth
                || input.RenderHeight != renderProfile.RenderHeight
                || input.SourceWidth != renderProfile.OutputWidth
                || input.SourceHeight != renderProfile.OutputHeight
                || Math.Abs(input.CameraPitchDegrees - renderProfile.CameraPitchDegrees) > 0.001d
                || new FileInfo(source).Length > AiSourceCacheContract.MaximumSourceBytes)
            {
                throw new InvalidDataException("Smoke entry violated the AI source contract: " + record.CacheKey);
            }
            ValidatePngContract(source, record.CacheKey, renderProfile);
            published++;
        }
        if (requireAll && published != records.Count)
        {
            throw new InvalidDataException($"Expected {records.Count} smoke entries; found {published}.");
        }
    }

    private static void ValidatePngContract(
        string sourcePath,
        string cacheKey,
        AiSourceCacheRenderProfile renderProfile)
    {
        var header = File.ReadAllBytes(sourcePath);
        if (header.Length < 26
            || header[0] != 0x89
            || header[1] != 0x50
            || header[2] != 0x4E
            || header[3] != 0x47
            || header[24] != 8
            || header[25] != 2)
        {
            throw new InvalidDataException(
                "Source must be an opaque, 8-bit RGB PNG: " + cacheKey);
        }

        using var stream = File.OpenRead(sourcePath);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth != renderProfile.OutputWidth
            || frame.PixelHeight != renderProfile.OutputHeight)
        {
            throw new InvalidDataException("PNG dimensions do not match the render contract: " + cacheKey);
        }

        var pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0d);
        var stride = pixels.PixelWidth * 3;
        var buffer = new byte[stride * pixels.PixelHeight];
        pixels.CopyPixels(buffer, stride, 0);
        var top = pixels.PixelHeight;
        var bottom = -1;
        for (var y = 0; y < pixels.PixelHeight; y++)
        {
            var row = y * stride;
            for (var x = 0; x < pixels.PixelWidth; x++)
            {
                var offset = row + x * 3;
                if (buffer[offset] <= 12
                    && buffer[offset + 1] <= 12
                    && buffer[offset + 2] <= 12)
                {
                    continue;
                }
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        if (bottom < 0)
        {
            throw new InvalidDataException("Source image contains no visible character pixels: " + cacheKey);
        }

        Console.WriteLine(
            $"  {cacheKey}: RGB PNG {pixels.PixelWidth}x{pixels.PixelHeight}, " +
            $"foreground y={top}..{bottom}");
        if (renderProfile.CameraFramed && top > pixels.PixelHeight * 0.55d)
        {
            throw new InvalidDataException(
                $"WAN portrait subject begins too low in the frame ({top}px): {cacheKey}");
        }
    }

    private static AiSourceCacheRenderProfile ReadRenderProfile(string? value) =>
        value?.Equals("legacy", StringComparison.OrdinalIgnoreCase) == true
            ? AiSourceCacheContract.LegacyQuality
            : value is null || value.Equals("wan", StringComparison.OrdinalIgnoreCase)
                ? AiSourceCacheContract.WanPortrait
                : throw new ArgumentException("--preset must be 'wan' or 'legacy'.");

    private static string? ReadValue(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }
}
