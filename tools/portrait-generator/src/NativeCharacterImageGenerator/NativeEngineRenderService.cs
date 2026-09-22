using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Bannerlord.NativeCharacterImageGenerator;

internal sealed record NativeEngineRenderResult(string OutputPath, byte[] PngBytes);

internal sealed record NativeEngineBatchProgress(
    int Completed,
    int Failed,
    int Total,
    string CurrentCharacterId,
    string Message);

internal sealed record NativeEngineBatchItemResult(
    NativeCharacterDefinition Character,
    string RawOutputPath,
    string StatusPath,
    bool Success,
    string State,
    string Error);

internal sealed record NativeEngineBatchResult(
    string BatchId,
    IReadOnlyList<NativeEngineBatchItemResult> Items,
    int Completed,
    int Failed,
    bool Canceled);

internal sealed record NativeEngineFrameContract(
    string RenderPreset,
    int OutputWidth,
    int OutputHeight,
    double CameraCropScale,
    double CameraCenterYRatio,
    double CameraPitchDegrees);

internal sealed class NativeEngineRenderService(string gameRoot)
{
    private const string JobEnvironmentVariable = "REIGN_NATIVE_PORTRAIT_JOB";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _gameRoot = Path.GetFullPath(gameRoot);

    public async Task<NativeEngineRenderResult> RenderAsync(
        NativeCharacterDefinition character,
        int width,
        int height,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var gameBin = Path.Combine(_gameRoot, "bin", "Win64_Shipping_Client");
        var installedHostPath = Path.Combine(gameBin, "Bannerlord.NativePortraitRenderHost.exe");
        var bundledHostPath = Path.Combine(AppContext.BaseDirectory, "Bannerlord.NativePortraitRenderHost.exe");
        var hostPath = File.Exists(installedHostPath) ? installedHostPath : bundledHostPath;
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException(
                "The native portrait render host is missing. Reinstall or republish the character studio.",
                hostPath);
        }

        var jobId = Guid.NewGuid().ToString("N");
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bannerlord Native Character Studio");
        var jobDirectory = Path.Combine(appData, "RenderJobs", jobId);
        var outputDirectory = Path.Combine(appData, "GeneratedSources");
        Directory.CreateDirectory(jobDirectory);
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(
            outputDirectory,
            $"{SanitizeFileName(character.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}-{jobId[..8]}.png");
        var statusPath = Path.Combine(jobDirectory, "status.json");
        var jobPath = Path.Combine(jobDirectory, "job.json");
        var request = new NativePortraitRenderRequest(
            4,
            jobId,
            character.Id,
            string.IsNullOrWhiteSpace(character.CharacterObjectId) ? character.Id : character.CharacterObjectId,
            character.CharacterCode,
            character.FaceTemplateId,
            AppearanceMode(character),
            NormalizeCulture(character.Culture),
            CharacterCacheKey.Build(
                character.Name,
                string.IsNullOrWhiteSpace(character.CharacterObjectId) ? character.Id : character.CharacterObjectId),
            RenderPreset(width, height),
            character.SourceModule,
            character.SourceFile,
            character.CivilianTemplate,
            character.SourceFile + "#" + character.CivilianTemplate,
            character.Age,
            character.Weight,
            character.Build,
            BuildBodyProperties(character),
            BuildEquipmentCode(character.CivilianEquipment, character.PreserveEncounteredOutfit),
            character.IsFemale,
            0,
            Math.Clamp(width, 384, 4096),
            Math.Clamp(height, 384, 4096),
            Math.Clamp(width, 384, 4096),
            Math.Clamp(height, 384, 4096),
            1d,
            0.5d,
            0d,
            outputPath,
            statusPath) { ClothingColor1 = character.ClothingColor1, ClothingColor2 = character.ClothingColor2,
                PreserveEncounteredOutfit = character.PreserveEncounteredOutfit };
        await File.WriteAllTextAsync(
            jobPath,
            JsonSerializer.Serialize(request, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath,
            WorkingDirectory = gameBin,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.Environment["BANNERLORD_GAME_PATH"] = _gameRoot;
        startInfo.Environment[JobEnvironmentVariable] = jobPath;
        startInfo.ArgumentList.Add("/singleplayer");
        startInfo.ArgumentList.Add(BuildModuleArgument());
        startInfo.ArgumentList.Add("no_watchdog");
        startInfo.ArgumentList.Add("/suppress_message_boxes");
        startInfo.ArgumentList.Add("/DisableErrorReporting");

        progress?.Report("Starting the render-only TaleWorlds engine worker…");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The native portrait render worker could not be started.");
        var lastState = string.Empty;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(150))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await TryReadStatus(statusPath, cancellationToken).ConfigureAwait(false);
                if (status is not null && !string.Equals(status.State, lastState, StringComparison.OrdinalIgnoreCase))
                {
                    lastState = status.State;
                    if (!string.IsNullOrWhiteSpace(status.Message))
                    {
                        progress?.Report(status.Message);
                    }
                }

                if (string.Equals(status?.State, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(outputPath))
                    {
                        throw new InvalidDataException("The native worker completed without writing its new source PNG.");
                    }
                    var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
                    if (bytes.Length == 0)
                    {
                        throw new InvalidDataException("The new native source PNG is empty.");
                    }
                    return new NativeEngineRenderResult(outputPath, bytes);
                }

                if (string.Equals(status?.State, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(status?.Message ?? "The native renderer reported a failure.");
                }

                if (process.HasExited)
                {
                    status = await TryReadStatus(statusPath, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(status?.State, "complete", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(outputPath))
                    {
                        return new NativeEngineRenderResult(
                            outputPath,
                            await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false));
                    }
                    throw new InvalidOperationException(
                        $"The native render worker exited early (code {process.ExitCode}). " +
                        (status?.Message ?? "No render status was written."));
                }

                await Task.Delay(125, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("The native render worker did not finish within 150 seconds.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<NativeEngineBatchResult> RenderBatchAsync(
        IReadOnlyList<NativeCharacterDefinition> characters,
        int width,
        int height,
        IProgress<NativeEngineBatchProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<NativeCharacterDefinition>? courtAppearanceCatalog = null,
        NativeEngineFrameContract? frameContract = null)
    {
        if (characters.Count == 0)
        {
            throw new ArgumentException("At least one character is required for a native source batch.", nameof(characters));
        }

        var gameBin = Path.Combine(_gameRoot, "bin", "Win64_Shipping_Client");
        var installedHostPath = Path.Combine(gameBin, "Bannerlord.NativePortraitRenderHost.exe");
        var bundledHostPath = Path.Combine(AppContext.BaseDirectory, "Bannerlord.NativePortraitRenderHost.exe");
        var hostPath = File.Exists(installedHostPath) ? installedHostPath : bundledHostPath;
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException(
                "The native portrait render host is missing. Reinstall or republish the character studio.",
                hostPath);
        }

        var batchId = Guid.NewGuid().ToString("N");
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bannerlord Native Character Studio");
        var batchDirectory = Path.Combine(appData, "BatchJobs", batchId);
        var rawDirectory = Path.Combine(appData, "BatchRaw", batchId);
        Directory.CreateDirectory(batchDirectory);
        Directory.CreateDirectory(rawDirectory);

        var requests = characters.Select((character, index) =>
        {
            var jobId = $"{batchId}-{index:D5}";
            var outputWidth = frameContract?.OutputWidth ?? width;
            var outputHeight = frameContract?.OutputHeight ?? height;
            return new NativePortraitRenderRequest(
                4,
                jobId,
                character.Id,
                string.IsNullOrWhiteSpace(character.CharacterObjectId) ? character.Id : character.CharacterObjectId,
                character.CharacterCode,
                character.FaceTemplateId,
                AppearanceMode(character),
                NormalizeCulture(character.Culture),
                CharacterCacheKey.Build(
                    character.Name,
                    string.IsNullOrWhiteSpace(character.CharacterObjectId) ? character.Id : character.CharacterObjectId),
                frameContract?.RenderPreset ?? RenderPreset(width, height),
                character.SourceModule,
                character.SourceFile,
                character.CivilianTemplate,
                character.SourceFile + "#" + character.CivilianTemplate,
                character.Age,
                character.Weight,
                character.Build,
                BuildBodyProperties(character),
                BuildEquipmentCode(character.CivilianEquipment, character.PreserveEncounteredOutfit),
                character.IsFemale,
                0,
                Math.Clamp(width, 384, 4096),
                Math.Clamp(height, 384, 4096),
                Math.Clamp(outputWidth, 384, 4096),
                Math.Clamp(outputHeight, 384, 4096),
                frameContract?.CameraCropScale ?? 1d,
                frameContract?.CameraCenterYRatio ?? 0.5d,
                frameContract?.CameraPitchDegrees ?? 0d,
                Path.Combine(
                    rawDirectory,
                    $"{index:D5}-{SanitizeFileName(character.Name)}-{SanitizeFileName(character.Id)}.png"),
                Path.Combine(batchDirectory, $"{index:D5}.status.json")) {
                    ClothingColor1 = character.ClothingColor1, ClothingColor2 = character.ClothingColor2,
                    PreserveEncounteredOutfit = character.PreserveEncounteredOutfit };
        }).ToArray();
        var batchStatusPath = Path.Combine(batchDirectory, "status.json");
        var manifestPath = Path.Combine(batchDirectory, "batch.json");
        var batchRequest = new NativePortraitBatchRequest(
            3,
            batchId,
            batchStatusPath,
            true,
            (courtAppearanceCatalog ?? characters)
                .Where(character => AppearanceMode(character) == AiSourceCacheContract.CourtAppearanceVersion)
                .Select(BuildAppearanceRequest)
                .OrderBy(character => character.CharacterId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            requests);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(batchRequest, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = hostPath,
            WorkingDirectory = gameBin,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.Environment["BANNERLORD_GAME_PATH"] = _gameRoot;
        startInfo.Environment[JobEnvironmentVariable] = manifestPath;
        startInfo.ArgumentList.Add("/singleplayer");
        startInfo.ArgumentList.Add(BuildModuleArgument());
        startInfo.ArgumentList.Add("no_watchdog");
        startInfo.ArgumentList.Add("/suppress_message_boxes");
        startInfo.ArgumentList.Add("/DisableErrorReporting");

        progress?.Report(new NativeEngineBatchProgress(
            0,
            0,
            characters.Count,
            string.Empty,
            "Starting one persistent TaleWorlds engine session for the full source build…"));
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The native portrait batch worker could not be started.");
        var lastProgressKey = string.Empty;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (stopwatch.Elapsed < TimeSpan.FromHours(12))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await TryReadBatchStatus(batchStatusPath, cancellationToken).ConfigureAwait(false);
                if (status is not null)
                {
                    var key = $"{status.State}|{status.Completed}|{status.Failed}|{status.CurrentCharacterId}|{status.Message}";
                    if (!string.Equals(key, lastProgressKey, StringComparison.Ordinal))
                    {
                        lastProgressKey = key;
                        progress?.Report(new NativeEngineBatchProgress(
                            status.Completed,
                            status.Failed,
                            status.Total > 0 ? status.Total : characters.Count,
                            status.CurrentCharacterId ?? string.Empty,
                            status.Message ?? string.Empty));
                    }

                    if (string.Equals(status.State, "complete", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status.State, "complete_with_errors", StringComparison.OrdinalIgnoreCase))
                    {
                        return await BuildBatchResult(
                            batchId,
                            characters,
                            requests,
                            status.Completed,
                            status.Failed,
                            canceled: false,
                            cancellationToken).ConfigureAwait(false);
                    }

                    if (string.Equals(status.State, "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        return await BuildBatchResult(
                            batchId,
                            characters,
                            requests,
                            status.Completed,
                            Math.Max(status.Failed, characters.Count - status.Completed),
                            canceled: false,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                if (process.HasExited)
                {
                    status = await TryReadBatchStatus(batchStatusPath, cancellationToken).ConfigureAwait(false);
                    if (status is not null
                        && (string.Equals(status.State, "complete", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(status.State, "complete_with_errors", StringComparison.OrdinalIgnoreCase)))
                    {
                        return await BuildBatchResult(
                            batchId,
                            characters,
                            requests,
                            status.Completed,
                            status.Failed,
                            canceled: false,
                            cancellationToken).ConfigureAwait(false);
                    }
                    return await BuildBatchResult(
                        batchId,
                        characters,
                        requests,
                        status?.Completed ?? 0,
                        Math.Max(status?.Failed ?? 0, characters.Count - (status?.Completed ?? 0)),
                        canceled: false,
                        cancellationToken,
                        process.ExitCode).ConfigureAwait(false);
                }

                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("The native source batch did not finish within twelve hours.");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            var status = await TryReadBatchStatus(batchStatusPath, CancellationToken.None).ConfigureAwait(false);
            return await BuildBatchResult(
                batchId,
                characters,
                requests,
                status?.Completed ?? 0,
                status?.Failed ?? 0,
                canceled: true,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<NativeEngineBatchResult> BuildBatchResult(
        string batchId,
        IReadOnlyList<NativeCharacterDefinition> characters,
        IReadOnlyList<NativePortraitRenderRequest> requests,
        int completed,
        int failed,
        bool canceled,
        CancellationToken cancellationToken,
        int? workerExitCode = null)
    {
        var items = new List<NativeEngineBatchItemResult>(requests.Count);
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var status = await TryReadStatus(request.StatusPath, cancellationToken).ConfigureAwait(false);
            var success = string.Equals(status?.State, "complete", StringComparison.OrdinalIgnoreCase)
                && File.Exists(request.OutputPath);
            items.Add(new NativeEngineBatchItemResult(
                characters[index],
                request.OutputPath,
                request.StatusPath,
                success,
                status?.State ?? "pending",
                success ? string.Empty : DescribeRenderFailure(status?.State, status?.Message, workerExitCode, request.StatusPath)));
        }
        return new NativeEngineBatchResult(batchId, items, completed, failed, canceled);
    }

    internal static string DescribeRenderFailure(string? state, string? message, int? exitCode, string statusPath)
    {
        if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(message))
            return message;
        if (exitCode.HasValue)
            return $"Native portrait worker exited before producing a completed source (exit 0x{unchecked((uint)exitCode.Value):X8}). "
                + $"Last state: {state ?? "unknown"}. Last progress: {message ?? "none"}. Job status: {statusPath}";
        return "No completed native source was written. Last state: " + (state ?? "unknown")
            + ". Last progress: " + (message ?? "none") + ". Job status: " + statusPath;
    }

    private string BuildModuleArgument()
    {
        string[] preferredModules =
        [
            "Native",
            "SandBoxCore",
            "Sandbox",
            "StoryMode",
            "CustomBattle",
            "NavalDLC",
            "BannerlordNativePortraitRenderer"
        ];
        var installed = preferredModules
            .Where(module => Directory.Exists(Path.Combine(_gameRoot, "Modules", module)))
            .ToArray();
        foreach (var required in new[] { "Native", "SandBoxCore", "Sandbox", "CustomBattle", "BannerlordNativePortraitRenderer" })
        {
            if (!installed.Contains(required, StringComparer.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException($"Required Bannerlord module '{required}' is not installed.");
            }
        }
        return "_MODULES_*" + string.Join("*", installed) + "*_MODULES_";
    }

    private static string BuildBodyProperties(NativeCharacterDefinition character)
    {
        if (string.IsNullOrWhiteSpace(character.BodyKey))
        {
            return string.Empty;
        }
        return string.Create(
            CultureInfo.InvariantCulture,
            $"<BodyProperties version=\"4\" age=\"{character.Age:0.####}\" weight=\"{character.Weight:0.####}\" build=\"{character.Build:0.####}\" key=\"{character.BodyKey}\" />");
    }

    private static NativePortraitAppearanceRequest BuildAppearanceRequest(
        NativeCharacterDefinition character)
    {
        var id = character.Id;
        var familyPrefix = id;
        foreach (var suffix in new[] { "_father", "_mother", "_child_1", "_child_2", "_child_3", "_child_4" })
        {
            if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                familyPrefix = id[..^suffix.Length];
                break;
            }
        }
        var isChild = id.Contains("_child_", StringComparison.OrdinalIgnoreCase);
        return new NativePortraitAppearanceRequest(
            character.Id,
            string.IsNullOrWhiteSpace(character.CharacterObjectId) ? character.Id : character.CharacterObjectId,
            character.FaceTemplateId,
            NormalizeCulture(character.Culture),
            character.IsFemale,
            0,
            character.Age,
            character.Weight,
            character.Build,
            isChild ? familyPrefix + "_mother" : string.Empty,
            isChild ? familyPrefix + "_father" : string.Empty);
    }

    private static string AppearanceMode(NativeCharacterDefinition character) =>
        string.IsNullOrWhiteSpace(character.BodyKey)
            && character.Id.StartsWith("reign_court_", StringComparison.OrdinalIgnoreCase)
            ? AiSourceCacheContract.CourtAppearanceVersion
            : "native_authored";

    private static string RenderPreset(int width, int height) =>
        width == AiSourceCacheContract.RenderWidth && height == AiSourceCacheContract.RenderHeight
            ? AiSourceCacheContract.RenderContractVersion
            : "native_upper_body_v1";

    private static string NormalizeCulture(string value) => value
        .Replace("Culture.", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Trim()
        .ToLowerInvariant();

    internal static string BuildEquipmentCode(IReadOnlyList<NativeEquipmentAssignment> assignments, bool preserveEncounteredOutfit = false)
    {
        var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["WeaponItemBeginSlot"] = 0,
            ["Weapon0"] = 0,
            ["Weapon1"] = 1,
            ["Weapon2"] = 2,
            ["Weapon3"] = 3,
            ["ExtraWeaponSlot"] = 4,
            ["NumAllWeaponSlots"] = 5,
            ["ArmorItemBeginSlot"] = 5,
            ["Head"] = 5,
            ["Body"] = 6,
            ["Leg"] = 7,
            ["Gloves"] = 8,
            ["Cape"] = 9,
            ["Horse"] = 10,
            ["HorseHarness"] = 11
        };
        return string.Concat(assignments
            .Where(item => slots.ContainsKey(item.Slot) && !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(item => slots[item.Slot])
            // Portrait sources must always expose the native hair and face. Deliberately
            // omit the Head slot while preserving civilian body, legs, gloves, and cape.
            .Where(group => group.Key >= (preserveEncounteredOutfit ? 0 : 6) && group.Key <= 9)
            .OrderBy(group => group.Key)
            .Select(group => $"+{group.Key}-{group.First().ItemId}-"
                + (string.IsNullOrWhiteSpace(group.First().ModifierId) ? "@null" : group.First().ModifierId)));
    }

    private static async Task<NativePortraitRenderStatus?> TryReadStatus(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var json = await ReadSharedTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<NativePortraitRenderStatus>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<NativePortraitBatchStatus?> TryReadBatchStatus(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var json = await ReadSharedTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<NativePortraitBatchStatus>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<string> ReadSharedTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(result) ? "character" : result;
    }

    private sealed record NativePortraitRenderRequest(
        int Version,
        string JobId,
        string CharacterId,
        string CharacterObjectId,
        string CharacterCode,
        string FaceTemplateId,
        string AppearanceMode,
        string CultureId,
        string CacheKey,
        string RenderPreset,
        string SourceModule,
        string SourceFile,
        string CivilianTemplate,
        string CivilianEquipmentProvenance,
        float Age,
        float Weight,
        float Build,
        string BodyProperties,
        string EquipmentCode,
        bool IsFemale,
        int Race,
        int Width,
        int Height,
        int OutputWidth,
        int OutputHeight,
        double CameraCropScale,
        double CameraCenterYRatio,
        double CameraPitchDegrees,
        string OutputPath,
        string StatusPath)
    {
        public uint? ClothingColor1 { get; init; }
        public uint? ClothingColor2 { get; init; }
        public bool PreserveEncounteredOutfit { get; init; }
    }

    private sealed record NativePortraitBatchRequest(
        int Version,
        string BatchId,
        string BatchStatusPath,
        bool ContinueOnError,
        IReadOnlyList<NativePortraitAppearanceRequest> CourtAppearances,
        IReadOnlyList<NativePortraitRenderRequest> Requests);

    private sealed record NativePortraitAppearanceRequest(
        string CharacterId,
        string CharacterObjectId,
        string FaceTemplateId,
        string CultureId,
        bool IsFemale,
        int Race,
        float Age,
        float Weight,
        float Build,
        string MotherId,
        string FatherId);

    private sealed record NativePortraitRenderStatus(
        int Version,
        string JobId,
        string State,
        string? Message,
        string? OutputPath,
        string? Utc);

    private sealed record NativePortraitBatchStatus(
        int Version,
        string BatchId,
        string State,
        string? Message,
        int Completed,
        int Failed,
        int Total,
        string? CurrentCharacterId,
        string? Utc);
}
