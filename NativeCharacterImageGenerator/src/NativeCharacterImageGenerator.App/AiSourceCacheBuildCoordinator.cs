using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bannerlord.NativeCharacterImageGenerator.App;

internal sealed record AiSourceCacheProgress(
    int Rendered,
    int Validated,
    int Skipped,
    int Failed,
    int Total,
    string Current,
    string Message);

internal sealed record AiSourceCacheBuildResult(
    string PackageRoot,
    int Validated,
    int Skipped,
    int Failed,
    bool Canceled,
    bool Complete);

internal sealed class AiSourceCacheBuildCoordinator(
    NativeEngineRenderService renderer,
    CanonicalCharacterCatalogResult catalog,
    IReadOnlyList<NativeCharacterDefinition>? courtAppearanceCatalog = null,
    string? installedSharedRoot = null,
    AiSourceCacheRenderProfile? renderProfile = null)
{
    private const string ManifestFileName = "build_manifest.json";
    private const string ManifestTsvFileName = "build_manifest.tsv";
    private const string SharedFolderName = "_shared";
    private readonly AiSourceCacheRenderProfile _renderProfile =
        renderProfile ?? AiSourceCacheContract.DefaultProfile;

    public static string CreatePackageRoot(string parentDirectory) => Path.Combine(
        parentDirectory,
        $"Reign Shared Portrait Source Cache - {DateTime.Now:yyyyMMdd-HHmmss}");

    public async Task<AiSourceCacheBuildResult> RunAsync(
        string packageRoot,
        IProgress<AiSourceCacheProgress>? progress,
        CancellationToken cancellationToken)
    {
        packageRoot = Path.GetFullPath(packageRoot);
        if (!string.IsNullOrWhiteSpace(installedSharedRoot)
            && IsSameOrDescendant(packageRoot, Path.GetFullPath(installedSharedRoot)))
        {
            throw new InvalidOperationException(
                "The AI source-cache package cannot be created inside Reign's installed PortraitCache\\_shared folder.");
        }
        Directory.CreateDirectory(packageRoot);
        Directory.CreateDirectory(Path.Combine(packageRoot, SharedFolderName));
        var eligibleRecords = catalog.Characters
            .Where(IsAiPortraitEligible)
            .ToArray();
        var manifestPath = Path.Combine(packageRoot, ManifestFileName);
        var manifest = await LoadOrCreateManifest(
            manifestPath,
            eligibleRecords,
            cancellationToken).ConfigureAwait(false);
        var itemsByKey = manifest.Items.ToDictionary(item => item.CacheKey, StringComparer.OrdinalIgnoreCase);
        var recordsById = eligibleRecords.ToDictionary(
            record => record.Character.Id,
            StringComparer.OrdinalIgnoreCase);
        var pending = new List<CanonicalCharacterRecord>();
        var skipped = 0;

        foreach (var record in eligibleRecords)
        {
            if (!itemsByKey.TryGetValue(record.CacheKey, out var item))
            {
                item = CreateManifestItem(record);
                manifest.Items.Add(item);
                itemsByKey[record.CacheKey] = item;
            }
            RefreshManifestIdentity(item, record);
            await RefreshExistingMetadataCatalogAsync(packageRoot, record).ConfigureAwait(false);
            if (IsValidExistingEntry(packageRoot, record, out var sourceHash, out var metadataHash))
            {
                item.State = "validated";
                item.SourceSha256 = sourceHash;
                item.MetadataSha256 = metadataHash;
                item.MetadataStatus = "valid";
                item.Error = string.Empty;
                skipped++;
            }
            else
            {
                item.State = "pending";
                item.SourceSha256 = string.Empty;
                item.MetadataSha256 = string.Empty;
                item.MetadataStatus = "pending";
                item.Error = string.Empty;
                pending.Add(record);
            }
        }

        manifest.Skipped = skipped;
        manifest.Validated = skipped;
        manifest.Failed = 0;
        manifest.Rendered = 0;
        manifest.State = "in_progress";
        manifest.UpdatedUtc = DateTime.UtcNow;
        await WriteManifestFiles(packageRoot, manifest, CancellationToken.None).ConfigureAwait(false);
        progress?.Report(new AiSourceCacheProgress(
            0,
            skipped,
            skipped,
            0,
            eligibleRecords.Length,
            string.Empty,
            pending.Count == 0
                ? "All source-cache entries already validate."
                : $"Resuming with {pending.Count:N0} character sources remaining."));

        NativeEngineBatchResult? renderResult = null;
        if (pending.Count > 0)
        {
            var nativeProgress = new Progress<NativeEngineBatchProgress>(update =>
            {
                progress?.Report(new AiSourceCacheProgress(
                    update.Completed,
                    manifest.Validated,
                    skipped,
                    update.Failed,
                    eligibleRecords.Length,
                    update.CurrentCharacterId,
                    update.Message));
            });
            try
            {
                renderResult = await renderer.RenderBatchAsync(
                    pending.Select(record => record.Character).ToArray(),
                    _renderProfile.RenderWidth,
                    _renderProfile.RenderHeight,
                    nativeProgress,
                    cancellationToken,
                    courtAppearanceCatalog
                        ?? catalog.Characters.Select(record => record.Character).ToArray(),
                    new NativeEngineFrameContract(
                        _renderProfile.RenderContractVersion,
                        _renderProfile.CameraFramed
                            ? _renderProfile.OutputWidth
                            : _renderProfile.RenderWidth,
                        _renderProfile.CameraFramed
                            ? _renderProfile.OutputHeight
                            : _renderProfile.RenderHeight,
                        _renderProfile.CameraFramed ? _renderProfile.CropScale : 1d,
                        _renderProfile.CameraFramed ? _renderProfile.CropCenterYRatio : 0.5d,
                        _renderProfile.CameraFramed ? _renderProfile.CameraPitchDegrees : 0d))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                renderResult = new NativeEngineBatchResult(
                    string.Empty,
                    [],
                    0,
                    0,
                    Canceled: true);
            }
            catch (Exception exception)
            {
                foreach (var record in pending)
                {
                    var item = itemsByKey[record.CacheKey];
                    item.State = "failed";
                    item.MetadataStatus = "failed";
                    item.Error = "Native worker failure: " + exception.Message;
                }
                manifest.State = "incomplete";
                manifest.Failed = pending.Count;
                manifest.UpdatedUtc = DateTime.UtcNow;
                await WriteManifestFiles(packageRoot, manifest, CancellationToken.None).ConfigureAwait(false);
                return new AiSourceCacheBuildResult(
                    packageRoot,
                    manifest.Validated,
                    skipped,
                    manifest.Failed,
                    Canceled: false,
                    Complete: false);
            }
        }

        var rendered = 0;
        var failed = 0;
        if (renderResult is not null)
        {
            foreach (var renderItem in renderResult.Items)
            {
                if (!recordsById.TryGetValue(renderItem.Character.Id, out var record))
                {
                    continue;
                }
                var manifestItem = itemsByKey[record.CacheKey];
                if (!renderItem.Success)
                {
                    if (renderResult.Canceled
                        && !renderItem.State.Equals("failed", StringComparison.OrdinalIgnoreCase))
                    {
                        manifestItem.State = "pending";
                        manifestItem.MetadataStatus = "pending";
                        manifestItem.Error = string.Empty;
                        continue;
                    }
                    manifestItem.State = "failed";
                    manifestItem.MetadataStatus = "failed";
                    manifestItem.Error = renderItem.Error;
                    failed++;
                    continue;
                }

                try
                {
                    var paths = EntryPaths(packageRoot, record.CacheKey);
                    Directory.CreateDirectory(paths.Directory);
                    SaveSourceAtomic(renderItem.RawOutputPath, paths.Source);
                    var sourceHash = CanonicalCharacterCatalog.Sha256File(paths.Source);
                    var input = CanonicalCharacterCatalog.CreateInput(record, sourceHash, _renderProfile);
                    await WriteJsonAtomic(paths.Metadata, input, CancellationToken.None).ConfigureAwait(false);
                    await WriteTextAtomic(
                        paths.ExportInfo,
                        BuildExportInfo(record, paths.Source),
                        CancellationToken.None).ConfigureAwait(false);
                    if (!IsValidExistingEntry(packageRoot, record, out var validatedHash, out var metadataHash)
                        || !validatedHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The published source entry did not pass validation.");
                    }

                    manifestItem.State = "validated";
                    manifestItem.SourceSha256 = sourceHash;
                    manifestItem.MetadataSha256 = metadataHash;
                    manifestItem.MetadataStatus = "valid";
                    manifestItem.Error = string.Empty;
                    rendered++;
                    TryDelete(renderItem.RawOutputPath);
                }
                catch (Exception exception)
                {
                    manifestItem.State = "failed";
                    manifestItem.MetadataStatus = "failed";
                    manifestItem.Error = exception.Message;
                    failed++;
                }

                manifest.Rendered = rendered;
                manifest.Validated = skipped + rendered;
                manifest.Failed = failed;
                manifest.UpdatedUtc = DateTime.UtcNow;
                await WriteManifestFiles(packageRoot, manifest, CancellationToken.None).ConfigureAwait(false);
                progress?.Report(new AiSourceCacheProgress(
                    rendered,
                    manifest.Validated,
                    skipped,
                    failed,
                    eligibleRecords.Length,
                    record.CharacterName,
                    $"Published and validated {manifest.Validated:N0} of {eligibleRecords.Length:N0} adult characters."));
            }
        }

        var canceled = renderResult?.Canceled == true || cancellationToken.IsCancellationRequested;
        var complete = !canceled
            && failed == 0
            && manifest.Items.Count == eligibleRecords.Length
            && manifest.Items.All(item => item.State.Equals("validated", StringComparison.OrdinalIgnoreCase));
        manifest.State = complete ? "complete" : canceled ? "canceled" : "incomplete";
        manifest.Rendered = rendered;
        manifest.Validated = manifest.Items.Count(item =>
            item.State.Equals("validated", StringComparison.OrdinalIgnoreCase));
        manifest.Skipped = skipped;
        manifest.Failed = manifest.Items.Count(item =>
            item.State.Equals("failed", StringComparison.OrdinalIgnoreCase));
        manifest.UpdatedUtc = DateTime.UtcNow;
        await WriteManifestFiles(packageRoot, manifest, CancellationToken.None).ConfigureAwait(false);

        return new AiSourceCacheBuildResult(
            packageRoot,
            manifest.Validated,
            manifest.Skipped,
            manifest.Failed,
            canceled,
            complete);
    }

    private async Task<SourceCacheBuildManifestV2> LoadOrCreateManifest(
        string manifestPath,
        IReadOnlyList<CanonicalCharacterRecord> eligibleRecords,
        CancellationToken cancellationToken)
    {
        SourceCacheBuildManifestV2? manifest = null;
        if (File.Exists(manifestPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                manifest = JsonSerializer.Deserialize<SourceCacheBuildManifestV2>(
                    json,
                    AiSourceCacheContract.JsonOptions);
            }
            catch (JsonException)
            {
                manifest = null;
            }
        }

        manifest ??= new SourceCacheBuildManifestV2();
        var activeKeys = eligibleRecords
            .Select(record => record.CacheKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        manifest.Items = (manifest.Items ?? [])
            .Where(item => item is not null && activeKeys.Contains(item.CacheKey))
            .GroupBy(item => item.CacheKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        manifest.Version = AiSourceCacheContract.ManifestVersion;
        manifest.CatalogHash = catalog.CatalogHash;
        manifest.RenderContractVersion = _renderProfile.RenderContractVersion;
        manifest.RenderContractHash = _renderProfile.RenderContractHash;
        manifest.CourtAppearanceVersion = AiSourceCacheContract.CourtAppearanceVersion;
        manifest.ProfilePackVersion = catalog.ProfilePackVersion;
        manifest.Expected = eligibleRecords.Count;
        manifest.CatalogTotal = catalog.Characters.Count;
        manifest.ExcludedUnderage = catalog.Characters.Count - eligibleRecords.Count;
        manifest.ModuleCounts = eligibleRecords
            .GroupBy(record => record.Character.SourceModule, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        manifest.ModuleVersions = new Dictionary<string, string>(
            catalog.ModuleVersions,
            StringComparer.OrdinalIgnoreCase);
        manifest.SourceHashes = new Dictionary<string, string>(catalog.SourceHashes, StringComparer.OrdinalIgnoreCase);
        return manifest;
    }

    internal static bool IsAiPortraitEligible(CanonicalCharacterRecord record) =>
        record.AgeYears >= AiSourceCacheContract.MinimumAiPortraitAge;

    private async Task RefreshExistingMetadataCatalogAsync(
        string packageRoot,
        CanonicalCharacterRecord record)
    {
        var paths = EntryPaths(packageRoot, record.CacheKey);
        if (!File.Exists(paths.Source) || !File.Exists(paths.Metadata) || !File.Exists(paths.ExportInfo))
        {
            return;
        }

        try
        {
            var input = JsonSerializer.Deserialize<PortraitGenerationInputV1>(
                File.ReadAllText(paths.Metadata),
                AiSourceCacheContract.JsonOptions);
            if (input is null
                || !input.RenderContractHash.Equals(_renderProfile.RenderContractHash, StringComparison.OrdinalIgnoreCase)
                || !input.RenderPreset.Equals(_renderProfile.RenderContractVersion, StringComparison.Ordinal)
                || !input.HeadgearSuppressed)
            {
                return;
            }

            var sourceHash = CanonicalCharacterCatalog.Sha256File(paths.Source);
            if (!input.SourceSha256.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var expectedInputHash =
                CanonicalCharacterCatalog.PortraitInputHash(record, _renderProfile);
            bool exactInput = input.InputHash.Equals(
                expectedInputHash,
                StringComparison.OrdinalIgnoreCase);
            if (!exactInput && !AppearanceIdentityMatches(input, record, sourceHash))
            {
                return;
            }
            if (exactInput
                && input.CatalogHash.Equals(record.CatalogHash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var refreshed = CanonicalCharacterCatalog.CreateInput(record, sourceHash, _renderProfile);
            await WriteJsonAtomic(paths.Metadata, refreshed, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Normal validation will mark a malformed or stale entry pending.
        }
    }

    private bool AppearanceIdentityMatches(
        PortraitGenerationInputV1 existing,
        CanonicalCharacterRecord record,
        string sourceHash)
    {
        PortraitGenerationInputV1 expected =
            CanonicalCharacterCatalog.CreateInput(record, sourceHash, _renderProfile);
        JsonObject? existingNode = JsonSerializer.SerializeToNode(
            existing,
            AiSourceCacheContract.JsonOptions)?.AsObject();
        JsonObject? expectedNode = JsonSerializer.SerializeToNode(
            expected,
            AiSourceCacheContract.JsonOptions)?.AsObject();
        if (existingNode == null || expectedNode == null) return false;
        foreach (string politicalField in new[]
        {
            "inputHash",
            "catalogHash",
            "clanId",
            "clanTier",
            "clanProvenance",
            "socialStation"
        })
        {
            existingNode.Remove(politicalField);
            expectedNode.Remove(politicalField);
        }
        return JsonNode.DeepEquals(existingNode, expectedNode);
    }

    private SourceCacheManifestItem CreateManifestItem(CanonicalCharacterRecord record)
    {
        var input = CanonicalCharacterCatalog.CreateInput(record, renderProfile: _renderProfile);
        return new SourceCacheManifestItem
        {
            CacheKey = record.CacheKey,
            HeroStringId = record.HeroStringId,
            CharacterObjectId = record.CharacterObjectId,
            Name = record.CharacterName,
            SourceModule = record.Character.SourceModule,
            InputHash = input.InputHash,
            MetadataStatus = "pending",
            State = "pending"
        };
    }

    private void RefreshManifestIdentity(
        SourceCacheManifestItem item,
        CanonicalCharacterRecord record)
    {
        item.HeroStringId = record.HeroStringId;
        item.CharacterObjectId = record.CharacterObjectId;
        item.Name = record.CharacterName;
        item.SourceModule = record.Character.SourceModule;
        item.InputHash = CanonicalCharacterCatalog.PortraitInputHash(record, _renderProfile);
        item.MetadataStatus = item.State.Equals("validated", StringComparison.OrdinalIgnoreCase)
            ? "valid"
            : "pending";
        item.SourcePath = Path.Combine(SharedFolderName, record.CacheKey, "source.png");
        item.MetadataPath = Path.Combine(SharedFolderName, record.CacheKey, "portrait_input.json");
    }

    private bool IsValidExistingEntry(
        string packageRoot,
        CanonicalCharacterRecord record,
        out string sourceHash,
        out string metadataHash)
    {
        sourceHash = string.Empty;
        metadataHash = string.Empty;
        var paths = EntryPaths(packageRoot, record.CacheKey);
        if (!File.Exists(paths.Source)
            || !File.Exists(paths.Metadata)
            || !File.Exists(paths.ExportInfo)
            || new FileInfo(paths.Source).Length is <= 0 or > AiSourceCacheContract.MaximumSourceBytes
            || !TryReadPngDimensions(paths.Source, out var width, out var height)
            || width != _renderProfile.OutputWidth
            || height != _renderProfile.OutputHeight)
        {
            return false;
        }

        try
        {
            var input = JsonSerializer.Deserialize<PortraitGenerationInputV1>(
                File.ReadAllText(paths.Metadata),
                AiSourceCacheContract.JsonOptions);
            if (input is null
                || input.Version != AiSourceCacheContract.InputVersion
                || !input.InputHash.Equals(
                    CanonicalCharacterCatalog.PortraitInputHash(record, _renderProfile),
                    StringComparison.OrdinalIgnoreCase)
                || !input.CatalogHash.Equals(record.CatalogHash, StringComparison.OrdinalIgnoreCase)
                || !input.RenderContractHash.Equals(
                    _renderProfile.RenderContractHash,
                    StringComparison.OrdinalIgnoreCase)
                || !input.CacheKey.Equals(record.CacheKey, StringComparison.OrdinalIgnoreCase)
                || !input.RenderPreset.Equals(_renderProfile.RenderContractVersion, StringComparison.Ordinal)
                || !input.HeadgearSuppressed)
            {
                return false;
            }

            sourceHash = CanonicalCharacterCatalog.Sha256File(paths.Source);
            metadataHash = CanonicalCharacterCatalog.Sha256File(paths.Metadata);
            return !string.IsNullOrWhiteSpace(input.SourceSha256)
                && input.SourceSha256.Equals(sourceHash, StringComparison.OrdinalIgnoreCase)
                && new FileInfo(paths.ExportInfo).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void SaveSourceAtomic(string rawPath, string destinationPath)
    {
        using var input = File.OpenRead(rawPath);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = input;
        bitmap.EndInit();
        bitmap.Freeze();

        BitmapSource framed = bitmap;
        if (!_renderProfile.CameraFramed)
        {
            var cropHeight = Math.Clamp(
                (int)Math.Round(bitmap.PixelHeight * _renderProfile.CropScale),
                1,
                bitmap.PixelHeight);
            var cropWidth = Math.Clamp(
                (int)Math.Round(cropHeight * _renderProfile.CropWidthRatio),
                1,
                bitmap.PixelWidth);
            if (cropWidth != _renderProfile.OutputWidth || cropHeight != _renderProfile.OutputHeight)
            {
                throw new InvalidDataException(
                    $"Unexpected native crop size {cropWidth}x{cropHeight}; expected " +
                    $"{_renderProfile.OutputWidth}x{_renderProfile.OutputHeight}.");
            }
            var centerX = bitmap.PixelWidth / 2d;
            var centerY = bitmap.PixelHeight * _renderProfile.CropCenterYRatio;
            var x = Math.Clamp((int)Math.Round(centerX - cropWidth / 2d), 0, bitmap.PixelWidth - cropWidth);
            var y = Math.Clamp((int)Math.Round(centerY - cropHeight / 2d), 0, bitmap.PixelHeight - cropHeight);
            var cropped = new CroppedBitmap(bitmap, new Int32Rect(x, y, cropWidth, cropHeight));
            cropped.Freeze();
            framed = cropped;
        }
        if (framed.PixelWidth != _renderProfile.OutputWidth || framed.PixelHeight != _renderProfile.OutputHeight)
        {
            throw new InvalidDataException(
                $"Native worker returned {framed.PixelWidth}x{framed.PixelHeight}; expected " +
                $"{_renderProfile.OutputWidth}x{_renderProfile.OutputHeight}.");
        }
        var opaqueRgb = new FormatConvertedBitmap(framed, PixelFormats.Bgr24, null, 0);
        opaqueRgb.Freeze();

        var temporary = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = File.Create(temporary))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(opaqueRgb));
                encoder.Save(output);
            }
            File.Move(temporary, destinationPath, true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async Task WriteManifestFiles(
        string packageRoot,
        SourceCacheBuildManifestV2 manifest,
        CancellationToken cancellationToken)
    {
        await WriteJsonAtomic(
            Path.Combine(packageRoot, ManifestFileName),
            manifest,
            cancellationToken).ConfigureAwait(false);
        var tsv = new StringBuilder();
        tsv.AppendLine("State\tMetadataStatus\tName\tHeroStringId\tCharacterObjectId\tCacheKey\tSourceModule\tInputHash\tSourceSha256\tMetadataSha256\tError");
        foreach (var item in manifest.Items.OrderBy(item => item.CacheKey, StringComparer.OrdinalIgnoreCase))
        {
            tsv.Append(SafeTsv(item.State)).Append('\t')
                .Append(SafeTsv(item.MetadataStatus)).Append('\t')
                .Append(SafeTsv(item.Name)).Append('\t')
                .Append(SafeTsv(item.HeroStringId)).Append('\t')
                .Append(SafeTsv(item.CharacterObjectId)).Append('\t')
                .Append(SafeTsv(item.CacheKey)).Append('\t')
                .Append(SafeTsv(item.SourceModule)).Append('\t')
                .Append(SafeTsv(item.InputHash)).Append('\t')
                .Append(SafeTsv(item.SourceSha256)).Append('\t')
                .Append(SafeTsv(item.MetadataSha256)).Append('\t')
                .AppendLine(SafeTsv(item.Error));
        }
        await WriteTextAtomic(
            Path.Combine(packageRoot, ManifestTsvFileName),
            tsv.ToString(),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAtomic<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, AiSourceCacheContract.JsonOptions);
        await WriteTextAtomic(path, json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextAtomic(
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static string BuildExportInfo(CanonicalCharacterRecord record, string sourcePath)
    {
        return string.Join(Environment.NewLine,
            "Name: " + record.CharacterName,
            "HeroStringId: " + record.HeroStringId,
            "CharacterId: " + record.CharacterObjectId,
            "Cache folder: " + record.CacheKey,
            "Age: " + record.AgeYears.ToString(CultureInfo.InvariantCulture),
            "Gender: " + record.Gender,
            "Culture: " + record.CultureId,
            "Clan: " + record.ClanId,
            "Clan tier: " + (record.ClanTier?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
            "Clan provenance: " + record.ClanProvenance,
            "Social station: " + record.SocialStation,
            "Occupation: " + record.Occupation,
            "Physical confidence: " + record.PhysicalConfidence.Score.ToString(CultureInfo.InvariantCulture),
            "Profile source: " + record.ProfileSource,
            "Headgear suppressed: true",
            "Source: " + sourcePath,
            string.Empty);
    }

    private static (string Directory, string Source, string Metadata, string ExportInfo) EntryPaths(
        string packageRoot,
        string cacheKey)
    {
        var directory = Path.Combine(packageRoot, SharedFolderName, cacheKey);
        return (
            directory,
            Path.Combine(directory, "source.png"),
            Path.Combine(directory, "portrait_input.json"),
            Path.Combine(directory, "lord_export_info.txt"));
    }

    private static bool TryReadPngDimensions(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        Span<byte> header = stackalloc byte[26];
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Read(header) != header.Length
                || !header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                return false;
            }
            width = ReadBigEndian(header[16..20]);
            height = ReadBigEndian(header[20..24]);
            var bitDepth = header[24];
            var colorType = header[25];
            return width > 0 && height > 0 && bitDepth == 8 && colorType == 2;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadBigEndian(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    private static string SafeTsv(string value) =>
        (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(candidate);
        return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
