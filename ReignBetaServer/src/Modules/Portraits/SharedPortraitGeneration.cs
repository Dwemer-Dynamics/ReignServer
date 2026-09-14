using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIPortraits;
using ReignPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string SharedPortraitGenerationMetadataFileName = ".ai_generation.json";
        private const int SharedPortraitConcurrentBatchSize = 5;
        private static readonly string[] SharedPortraitGeneratedArtifactFileNames =
        {
            "portrait.png",
            "custom.png",
            PortraitDerivativeCore.ThumbnailFileName,
            PortraitDerivativeCore.PartyThumbnailFileName,
            PortraitDerivativeCore.PortraitFileName,
            PortraitDerivativeCore.ZoomFileName,
            PortraitDerivativeCore.ManifestFileName,
            "prompt.txt",
            SharedPortraitGenerationMetadataFileName
        };

        private sealed class SharedPortraitEntry
        {
            public string Folder = "";
            public string CacheKey = "";
            public string HeroStringId = "";
            public string CharacterObjectId = "";
            public string CharacterName = "";
            public string CultureName = "";
            public string SourceModule = "";
            public string SourcePath = "";
            public string MetadataPath = "";
            public string PortraitPath = "";
            public Dictionary<string, object> Metadata = new Dictionary<string, object>();
            public bool HasSource;
            public bool HasPortrait;
            public bool HasDerivatives;
            public string Error = "";
        }

        private sealed class SharedPortraitGenerationJobState
        {
            public string JobId = "";
            public string State = "idle";
            public string Root = "";
            public string Provider = "";
            public string Model = "";
            public int Scanned;
            public int Eligible;
            public int AlreadyGenerated;
            public int Queued;
            public int Attempted;
            public int Generated;
            public readonly List<Dictionary<string, object>> Warnings = new List<Dictionary<string, object>>();
            public int Skipped;
            public int Failed;
            public int Remaining;
            public string CurrentCacheKey = "";
            public string CurrentCharacterName = "";
            public string StartedUtc = "";
            public string CompletedUtc = "";
            public string Mode = "full_missing";
            public int DelayMs;
            public bool CancelRequested;
            public readonly List<Dictionary<string, object>> Errors = new List<Dictionary<string, object>>();
        }

        private static readonly object SharedPortraitGenerationJobLock = new object();
        private static SharedPortraitGenerationJobState ActiveSharedPortraitGenerationJob =
            new SharedPortraitGenerationJobState();

        private static string SharedPortraitCacheRootForServer()
        {
            var installation = Reign.Core.Contracts.Platform.ReignInstallation.TryLoadCurrent();
            if (installation != null) return installation.SharedPortraitRoot;
            string root = PortraitCacheRootForServer();
            return string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "_shared");
        }

        private static Dictionary<string, object> SharedPortraitCatalogApi()
        {
            List<SharedPortraitEntry> entries = ReadSharedPortraitEntries();
            ReadSharedPortraitProvider(out string provider, out string model, out string providerError);
            int sourceReady = entries.Count(x => x.HasSource);
            int generated = entries.Count(x => x.HasPortrait);
            int ready = entries.Count(x => x.HasPortrait && x.HasDerivatives);
            return new Dictionary<string, object>
            {
                ["ok"] = Directory.Exists(SharedPortraitCacheRootForServer()),
                ["root"] = SharedPortraitCacheRootForServer(),
                ["detected"] = entries.Count,
                ["sourceReady"] = sourceReady,
                ["generated"] = generated,
                ["missing"] = Math.Max(0, sourceReady - generated),
                ["gameReady"] = ready,
                ["provider"] = provider,
                ["model"] = model,
                ["providerError"] = providerError,
                ["characters"] = entries.Select(SharedPortraitEntrySnapshot).ToList()
            };
        }

        private static Dictionary<string, object> OpenSharedPortraitCacheFolderApi()
        {
            string root = SharedPortraitCacheRootForServer();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "The shared portrait cache folder was not found."
                };
            }

            try
            {
                Process.Start(BuildSharedPortraitCacheOpenStartInfo(root));
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["root"] = root
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["root"] = root,
                    ["error"] = "The shared portrait cache folder could not be opened: " + ex.Message
                };
            }
        }

        private static ProcessStartInfo BuildSharedPortraitCacheOpenStartInfo(string root)
        {
            return new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + Path.GetFullPath(root) + "\"",
                UseShellExecute = true
            };
        }

        private static Dictionary<string, object> GenerateOneSharedPortraitApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string cacheKey = ReadString(payload, "cacheKey", "");
            bool force = ReadBool(payload, "force", true);
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Select a shared-cache character first." };
            }

            lock (SharedPortraitGenerationJobLock)
            {
                if (string.Equals(ActiveSharedPortraitGenerationJob.State, "running", StringComparison.OrdinalIgnoreCase))
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["error"] = "The missing-portrait batch is already running. Stop it or let it finish before generating one character."
                    };
                }
            }

            if (!TryLoadSharedPortraitEntry(cacheKey, out SharedPortraitEntry entry, out string error))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = error };
            }
            if (!entry.HasSource && string.IsNullOrWhiteSpace(entry.HeroStringId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = entry.Error };
            }
            if (entry.HasPortrait && !force)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["alreadyGenerated"] = true,
                    ["character"] = SharedPortraitEntrySnapshot(entry)
                };
            }
            if (!ReadSharedPortraitProvider(out string provider, out string model, out string providerError))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["provider"] = provider,
                    ["model"] = model,
                    ["error"] = providerError
                };
            }

            Dictionary<string, object> result = GenerateSharedPortrait(entry, provider);
            result.Remove("imageBase64");
            if (TryLoadSharedPortraitEntry(cacheKey, out SharedPortraitEntry refreshed, out string _))
            {
                result["character"] = SharedPortraitEntrySnapshot(refreshed);
            }
            return result;
        }

        private static Dictionary<string, object> DeleteOneSharedPortraitApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string cacheKey = ReadString(payload, "cacheKey", "");
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Select a shared-cache character first."
                };
            }

            lock (SharedPortraitGenerationJobLock)
            {
                if (string.Equals(ActiveSharedPortraitGenerationJob.State, "running", StringComparison.OrdinalIgnoreCase))
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["error"] = "Stop the missing-portrait batch before deleting a character portrait."
                    };
                }
            }

            if (!TryLoadSharedPortraitEntry(cacheKey, out SharedPortraitEntry entry, out string error))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = error };
            }

            try
            {
                List<string> deleted = DeleteSharedPortraitGeneratedArtifacts(entry.Folder);
                if (!TryLoadSharedPortraitEntry(cacheKey, out SharedPortraitEntry refreshed, out string refreshError))
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["error"] = refreshError,
                        ["deleted"] = deleted
                    };
                }
                LogOperational("portrait.shared_generation.deleted", new Dictionary<string, object>
                {
                    ["cacheKey"] = entry.CacheKey,
                    ["characterName"] = entry.CharacterName,
                    ["deleted"] = deleted
                });
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["deleted"] = deleted,
                    ["deletedCount"] = deleted.Count,
                    ["character"] = SharedPortraitEntrySnapshot(refreshed)
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "The generated portrait files could not be fully deleted: " + ex.Message
                };
            }
        }

        private static List<string> DeleteSharedPortraitGeneratedArtifacts(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                throw new DirectoryNotFoundException("The selected shared-cache character folder was not found.");
            }

            var deleted = new List<string>();
            foreach (string fileName in SharedPortraitGeneratedArtifactFileNames
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string path = Path.Combine(folder, fileName);
                if (!File.Exists(path))
                {
                    continue;
                }
                File.Delete(path);
                deleted.Add(fileName);
            }
            return deleted;
        }

        private static Dictionary<string, object> StartMissingSharedPortraitBuildApi()
        {
            return StartSharedPortraitBuildApi();
        }

        private static Dictionary<string, object> StartSharedPortraitBuildApi()
        {
            lock (SharedPortraitGenerationJobLock)
            {
                if (string.Equals(ActiveSharedPortraitGenerationJob.State, "running", StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, object> active = SharedPortraitGenerationJobSnapshot(ActiveSharedPortraitGenerationJob);
                    active["attached"] = true;
                    return active;
                }

                List<SharedPortraitEntry> entries = ReadSharedPortraitEntries();
                if (!ReadSharedPortraitProvider(out string provider, out string model, out string providerError))
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["state"] = "failed",
                        ["provider"] = provider,
                        ["model"] = model,
                        ["error"] = providerError
                    };
                }

                List<SharedPortraitEntry> eligible = entries.Where(x => x.HasSource || !string.IsNullOrWhiteSpace(x.HeroStringId)).ToList();
                List<SharedPortraitEntry> missing = eligible.Where(x => !x.HasPortrait).ToList();
                SharedPortraitGenerationJobState job = new SharedPortraitGenerationJobState
                {
                    JobId = Guid.NewGuid().ToString("N"),
                    State = missing.Count == 0 ? "completed" : "running",
                    Root = SharedPortraitCacheRootForServer(),
                    Provider = provider,
                    Model = model,
                    Scanned = entries.Count,
                    Eligible = eligible.Count,
                    AlreadyGenerated = eligible.Count - missing.Count,
                    Queued = missing.Count,
                    Remaining = missing.Count,
                    Mode = "batched_concurrent",
                    DelayMs = 0,
                    StartedUtc = DateTime.UtcNow.ToString("o"),
                    CompletedUtc = missing.Count == 0 ? DateTime.UtcNow.ToString("o") : ""
                };
                ActiveSharedPortraitGenerationJob = job;
                if (missing.Count > 0)
                {
                    Task.Run(() => RunBatchedSharedPortraitBuild(job, missing));
                }
                return SharedPortraitGenerationJobSnapshot(job);
            }
        }

        private static Dictionary<string, object> SharedPortraitGenerationStatusApi()
        {
            lock (SharedPortraitGenerationJobLock)
            {
                return SharedPortraitGenerationJobSnapshot(ActiveSharedPortraitGenerationJob);
            }
        }

        private static Dictionary<string, object> CancelSharedPortraitGenerationApi()
        {
            lock (SharedPortraitGenerationJobLock)
            {
                bool running = string.Equals(
                    ActiveSharedPortraitGenerationJob.State,
                    "running",
                    StringComparison.OrdinalIgnoreCase);
                ActiveSharedPortraitGenerationJob.CancelRequested = running;
                Dictionary<string, object> result = SharedPortraitGenerationJobSnapshot(ActiveSharedPortraitGenerationJob);
                result["cancelAccepted"] = running;
                return result;
            }
        }

        private static void RunBatchedSharedPortraitBuild(
            SharedPortraitGenerationJobState job,
            List<SharedPortraitEntry> missing)
        {
            try
            {
                for (int offset = 0; offset < missing.Count; offset += SharedPortraitConcurrentBatchSize)
                {
                    lock (SharedPortraitGenerationJobLock)
                    {
                        if (job.CancelRequested)
                        {
                            job.State = "canceled";
                            job.CurrentCacheKey = "";
                            job.CurrentCharacterName = "";
                            job.CompletedUtc = DateTime.UtcNow.ToString("o");
                            return;
                        }
                    }
                    List<SharedPortraitEntry> batch = missing
                        .Skip(offset)
                        .Take(SharedPortraitConcurrentBatchSize)
                        .ToList();
                    List<Task> tasks = batch
                        .Select(entry => Task.Run(() => RunOneSharedPortraitEntry(job, entry)))
                        .ToList();
                    Task.WaitAll(tasks.ToArray());
                }
                lock (SharedPortraitGenerationJobLock)
                {
                    job.State = job.CancelRequested ? "canceled" : job.Failed == 0 ? "completed" : "completed_with_errors";
                    job.CurrentCacheKey = "";
                    job.CurrentCharacterName = "";
                    job.Remaining = 0;
                    job.CompletedUtc = DateTime.UtcNow.ToString("o");
                }
                LogOperational("portrait.shared_generation.completed", SharedPortraitGenerationJobSnapshot(job));
            }
            catch (Exception ex)
            {
                lock (SharedPortraitGenerationJobLock)
                {
                    job.State = "failed";
                    job.CurrentCacheKey = "";
                    job.CurrentCharacterName = "";
                    job.CompletedUtc = DateTime.UtcNow.ToString("o");
                    AddSharedPortraitGenerationError(job, job.CurrentCacheKey, job.CurrentCharacterName, ex.Message);
                }
                LogOperational("portrait.shared_generation.failed", SharedPortraitGenerationJobSnapshot(job));
            }
        }

        private static void RunOneSharedPortraitEntry(
            SharedPortraitGenerationJobState job,
            SharedPortraitEntry queued)
        {
            lock (SharedPortraitGenerationJobLock)
            {
                if (job.CancelRequested)
                {
                    job.Skipped++;
                    job.Remaining--;
                    return;
                }
                job.CurrentCacheKey = queued.CacheKey;
                job.CurrentCharacterName = queued.CharacterName;
            }

            if (!TryLoadSharedPortraitEntry(queued.CacheKey, out SharedPortraitEntry entry, out string loadError))
            {
                RecordSharedPortraitFailure(job, queued.CacheKey, queued.CharacterName, loadError);
                return;
            }
            if (entry.HasPortrait)
            {
                lock (SharedPortraitGenerationJobLock)
                {
                    job.Skipped++;
                    job.Remaining--;
                }
                return;
            }

            Dictionary<string, object> result = GenerateSharedPortrait(entry, job.Provider);
            bool ok = ReadBool(result, "ok", false)
                && IsValidSharedPortraitPng(entry.PortraitPath);
            lock (SharedPortraitGenerationJobLock)
            {
                job.Attempted++;
            }
            if (ok)
            {
                lock (SharedPortraitGenerationJobLock)
                {
                    job.Generated++;
                    string warning = ReadString(result, "warning", "");
                    if (!string.IsNullOrWhiteSpace(warning)) job.Warnings.Add(new Dictionary<string, object> {
                        ["cacheKey"] = entry.CacheKey, ["characterName"] = entry.CharacterName, ["warning"] = warning });
                    job.Remaining--;
                }
            }
            else
            {
                RecordSharedPortraitFailure(
                    job,
                    queued.CacheKey,
                    queued.CharacterName,
                    ReadString(result, "error", "Image generation did not return a valid portrait PNG."));
            }
        }

        private static Dictionary<string, object> GenerateSharedPortrait(
            SharedPortraitEntry entry,
            string provider)
        {
            return PortraitGenerate(BuildSharedPortraitGenerationPayload(entry, provider));
        }

        private static Dictionary<string, object> BuildSharedPortraitGenerationPayload(
            SharedPortraitEntry entry,
            string provider)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>(
                entry.Metadata ?? new Dictionary<string, object>(),
                StringComparer.OrdinalIgnoreCase);
            payload["campaignId"] = "_shared";
            payload["cacheKey"] = entry.CacheKey;
            payload["heroStringId"] = entry.HeroStringId;
            payload["characterObjectId"] = entry.CharacterObjectId;
            payload["characterName"] = entry.CharacterName;
            payload["promptPurpose"] = "portrait";
            payload["sharedCacheOutput"] = true;
            payload["provider"] = NormalizeImageProvider(provider);
            payload["sourceImageBase64"] = ""; // Reference is resolved from the authoritative shared master on the server.
            return payload;
        }

        private static string StoreGeneratedSharedPortraitCore(
            Dictionary<string, object> payload,
            string cacheKey,
            string prompt,
            byte[] sourceImage,
            byte[] imageBytes,
            PortraitImageCallResult call,
            Dictionary<string, object> physicalConfidence,
            Dictionary<string, object> productReceipt)
        {
            if (!TryLoadSharedPortraitEntry(cacheKey, out SharedPortraitEntry entry, out string error))
            {
                throw new InvalidDataException(error);
            }

            byte[] canonicalPng = PngReencode.ToPngEncoderFormat(imageBytes);
            if (canonicalPng == null || canonicalPng.Length == 0)
            {
                throw new InvalidDataException("The image provider did not return a supported PNG portrait.");
            }
            byte[] decoded = PngReencode.DecodeToRgba(canonicalPng, out int width, out int height);
            if (decoded == null
                || width < PngReencode.ImageEditMinimumDimension
                || height < PngReencode.ImageEditMinimumDimension
                || width > PngReencode.ImageEditMaximumDimension
                || height > PngReencode.ImageEditMaximumDimension)
            {
                throw new InvalidDataException(
                    "The generated portrait dimensions must be between 384 and 5000 pixels.");
            }

            WriteSharedPortraitAtomic(entry.PortraitPath, canonicalPng);
            var resolvedSource = ReadDictionary(ReadDictionary(productReceipt, "source"), "provenance");
            var nativePhysique = ReadDictionary(resolvedSource, "physique");
            if (nativePhysique != null)
            {
                entry.Metadata["nativePhysique"] = nativePhysique;
                string renderHash = ReadString(resolvedSource, "renderContractHash", "");
                if (!string.IsNullOrWhiteSpace(renderHash)) entry.Metadata["renderContractHash"] = renderHash;
            }
            // Shared products retain the AI master and physique, not a redundant native source PNG.
            entry.Metadata["referenceImageFile"] = "portrait.png";
            entry.Metadata["referenceImageSha256"] = Sha256Hex(canonicalPng);
            if (nativePhysique != null) WriteSharedPortraitAtomic(entry.MetadataPath, Encoding.UTF8.GetBytes(Json.Serialize(entry.Metadata)));
            WriteSharedPortraitAtomic(
                Path.Combine(entry.Folder, "prompt.txt"),
                Encoding.UTF8.GetBytes(prompt ?? ""));

            bool derivativesReady = false;
            string derivativeError = "";
            try
            {
                BuildPortraitDerivativesFromMaster(entry.PortraitPath, entry.Folder);
                derivativesReady = true;
            }
            catch (Exception ex) { throw new InvalidDataException("Shared portrait derivatives failed; previous product will be restored.", ex); }

            Dictionary<string, object> generation = new Dictionary<string, object>
            {
                ["version"] = 1,
                ["cacheKey"] = entry.CacheKey,
                ["heroStringId"] = entry.HeroStringId,
                ["characterObjectId"] = entry.CharacterObjectId,
                ["characterName"] = entry.CharacterName,
                ["provider"] = call.Provider,
                ["model"] = call.Model,
                ["adapter"] = call.Adapter,
                ["sourceSha256"] = Sha256Hex(sourceImage),
                ["portraitSha256"] = Sha256Hex(canonicalPng),
                ["portraitWidth"] = width,
                ["portraitHeight"] = height,
                ["productReceipt"] = productReceipt ?? new Dictionary<string, object>(),
                ["generationOperationId"] = ReadString(payload, "generationOperationId", ""),
                ["generationStages"] = productReceipt != null && productReceipt.TryGetValue("generationStages", out object stages) ? stages : new object[0],
                ["warning"] = ReadString(productReceipt, "warning", ""),
                ["physicalConfidence"] = physicalConfidence ?? new Dictionary<string, object>(),
                ["derivativesReady"] = derivativesReady,
                ["derivativeError"] = derivativeError,
                ["generatedUtc"] = DateTime.UtcNow.ToString("o")
            };
            WriteSharedPortraitAtomic(
                Path.Combine(entry.Folder, SharedPortraitGenerationMetadataFileName),
                Encoding.UTF8.GetBytes(Json.Serialize(generation)));
            return entry.PortraitPath;
        }

        private static void WriteSharedPortraitAtomic(string path, byte[] bytes)
        {
            if (string.IsNullOrWhiteSpace(path) || bytes == null)
            {
                throw new InvalidDataException("A shared portrait output path and file content are required.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string incoming = path + ".incoming-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(incoming, bytes);
            try
            {
                if (File.Exists(path))
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    try {
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                        File.Replace(incoming, path, null, true);
                    } finally { if (File.Exists(path)) File.SetAttributes(path, attributes); }
                }
                else
                {
                    File.Move(incoming, path);
                }
            }
            finally
            {
                try { if (File.Exists(incoming)) File.Delete(incoming); } catch { }
            }
        }

        private static List<SharedPortraitEntry> ReadSharedPortraitEntries()
        {
            string root = SharedPortraitCacheRootForServer();
            var entries = new List<SharedPortraitEntry>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return entries;
            }

            foreach (string folder in Directory.GetDirectories(root))
            {
                try
                {
                    DirectoryInfo info = new DirectoryInfo(folder);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0
                        || (info.Attributes & FileAttributes.Hidden) != 0)
                    {
                        continue;
                    }
                    entries.Add(LoadSharedPortraitEntry(folder));
                }
                catch (Exception ex)
                {
                    entries.Add(new SharedPortraitEntry
                    {
                        Folder = folder,
                        CacheKey = Path.GetFileName(folder) ?? "",
                        CharacterName = Path.GetFileName(folder) ?? "",
                        Error = ex.Message
                    });
                }
            }

            return entries
                .OrderBy(x => x.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.CacheKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryLoadSharedPortraitEntry(
            string cacheKey,
            out SharedPortraitEntry entry,
            out string error)
        {
            entry = null;
            error = "";
            try
            {
                string root = SharedPortraitCacheRootForServer();
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    error = "The installed Reign shared portrait cache was not found.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(cacheKey)
                    || cacheKey.IndexOf(Path.DirectorySeparatorChar) >= 0
                    || cacheKey.IndexOf(Path.AltDirectorySeparatorChar) >= 0
                    || cacheKey == "."
                    || cacheKey == "..")
                {
                    error = "The shared portrait cache key is invalid.";
                    return false;
                }

                string rootFull = Path.GetFullPath(root).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string folder = Path.GetFullPath(Path.Combine(root, cacheKey));
                if (!folder.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                    || !Directory.Exists(folder))
                {
                    error = "The selected shared-cache character folder was not found.";
                    return false;
                }
                entry = LoadSharedPortraitEntry(folder);
                if (!string.Equals(entry.CacheKey, cacheKey, StringComparison.OrdinalIgnoreCase))
                {
                    error = "The folder name does not match portrait_input.json.";
                    entry = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                entry = null;
                return false;
            }
        }

        private static SharedPortraitEntry LoadSharedPortraitEntry(string folder)
        {
            string sourcePath = Path.Combine(folder, "source.png");
            string metadataPath = Path.Combine(folder, "portrait_input.json");
            string portraitPath = Path.Combine(folder, "portrait.png");
            var entry = new SharedPortraitEntry
            {
                Folder = folder,
                CacheKey = Path.GetFileName(folder) ?? "",
                CharacterName = Path.GetFileName(folder) ?? "",
                SourcePath = sourcePath,
                MetadataPath = metadataPath,
                PortraitPath = portraitPath
            };

            if (!File.Exists(metadataPath))
            {
                entry.Error = "source.png or portrait_input.json is missing.";
                return entry;
            }

            Dictionary<string, object> metadata =
                Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(metadataPath, Encoding.UTF8))
                ?? new Dictionary<string, object>();
            entry.Metadata = metadata;
            entry.CacheKey = ReadString(metadata, "cacheKey", entry.CacheKey);
            entry.HeroStringId = ReadString(metadata, "heroStringId", "");
            entry.CharacterObjectId = ReadString(metadata, "characterObjectId", entry.HeroStringId);
            entry.CharacterName = ReadString(metadata, "characterName", entry.CacheKey);
            entry.CultureName = ReadFirstString(metadata, "cultureName", "cultureId");
            entry.SourceModule = ReadString(metadata, "sourceModule", "");
            entry.HasSource = IsValidSharedPortraitPng(sourcePath);
            entry.HasPortrait = IsValidSharedPortraitPng(portraitPath);
            entry.HasDerivatives = entry.HasPortrait
                && TryReadCurrentPortraitDerivativeManifest(portraitPath, folder, out var _);
            if (!entry.HasPortrait && string.IsNullOrWhiteSpace(entry.HeroStringId))
            {
                entry.Error = "No AI portrait or character identity is available for generation.";
            }
            return entry;
        }

        private static bool IsValidSharedPortraitPng(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length < 64)
                {
                    return false;
                }
                byte[] header = new byte[24];
                using (FileStream stream = File.OpenRead(path))
                {
                    if (stream.Read(header, 0, header.Length) != header.Length)
                    {
                        return false;
                    }
                }
                if (header[0] != 0x89
                    || header[1] != 0x50
                    || header[2] != 0x4E
                    || header[3] != 0x47
                    || header[4] != 0x0D
                    || header[5] != 0x0A
                    || header[6] != 0x1A
                    || header[7] != 0x0A
                    || header[12] != (byte)'I'
                    || header[13] != (byte)'H'
                    || header[14] != (byte)'D'
                    || header[15] != (byte)'R')
                {
                    return false;
                }
                int width = ReadBigEndianInt32(header, 16);
                int height = ReadBigEndianInt32(header, 20);
                return width >= PngReencode.ImageEditMinimumDimension
                    && height >= PngReencode.ImageEditMinimumDimension
                    && width <= PngReencode.ImageEditMaximumDimension
                    && height <= PngReencode.ImageEditMaximumDimension;
            }
            catch
            {
                return false;
            }
        }

        private static int ReadBigEndianInt32(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24)
                | (bytes[offset + 1] << 16)
                | (bytes[offset + 2] << 8)
                | bytes[offset + 3];
        }

        private static bool ReadSharedPortraitProvider(
            out string provider,
            out string model,
            out string error)
        {
            Dictionary<string, object> settings = LoadSettings();
            provider = NormalizeImageProvider(ReadString(settings, "portraitProvider", "NanoGPT"));
            if (IsCodexImageProvider(provider))
            {
                model = CodexImageModel;
                error = string.IsNullOrWhiteSpace(ReadString(settings, "codexExecutable", "codex"))
                    ? "Configure the Codex executable and sign in to ChatGPT in API Settings." : "";
                return string.IsNullOrWhiteSpace(error);
            }
            if (provider == "OpenRouter")
            {
                model = ReadString(settings, "portraitOpenRouterImageModel", DefaultOpenRouterImageModel);
                error = string.IsNullOrWhiteSpace(ReadString(settings, "openRouterApiKey", ""))
                    ? "Save your OpenRouter key in API Settings." : "";
                return string.IsNullOrWhiteSpace(error);
            }
            bool atlas = IsAtlasProvider(provider);
            model = atlas
                ? ReadString(settings, "portraitAtlasImageModel", DefaultAtlasImageModel)
                : ReadString(settings, "portraitNanoGptImageModel", "gpt-image-1.5");
            string key = atlas
                ? ReadString(settings, "portraitAtlasApiKey", "")
                : ReadString(settings, "portraitNanoGptApiKey", "");
            error = string.IsNullOrWhiteSpace(key)
                ? "No " + provider + " image API key is configured in AI Image Generation."
                : "";
            return string.IsNullOrWhiteSpace(error);
        }

        private static Dictionary<string, object> SharedPortraitEntrySnapshot(SharedPortraitEntry entry)
        {
            entry = entry ?? new SharedPortraitEntry();
            return new Dictionary<string, object>
            {
                ["cacheKey"] = entry.CacheKey,
                ["heroStringId"] = entry.HeroStringId,
                ["characterObjectId"] = entry.CharacterObjectId,
                ["characterName"] = entry.CharacterName,
                ["cultureName"] = entry.CultureName,
                ["sourceModule"] = entry.SourceModule,
                ["hasSource"] = entry.HasSource,
                ["hasPortrait"] = entry.HasPortrait,
                ["hasDerivatives"] = entry.HasDerivatives,
                ["canGenerate"] = entry.HasPortrait || !string.IsNullOrWhiteSpace(entry.HeroStringId),
                ["state"] = entry.HasPortrait
                        ? entry.HasDerivatives ? "ready" : "portrait_only"
                        : "missing",
                ["error"] = entry.Error
            };
        }

        private static Dictionary<string, object> SharedPortraitGenerationJobSnapshot(
            SharedPortraitGenerationJobState job)
        {
            job = job ?? new SharedPortraitGenerationJobState();
            return new Dictionary<string, object>
            {
                ["ok"] = !string.Equals(job.State, "failed", StringComparison.OrdinalIgnoreCase),
                ["jobId"] = job.JobId,
                ["state"] = job.State,
                ["root"] = job.Root,
                ["provider"] = job.Provider,
                ["model"] = job.Model,
                ["scanned"] = job.Scanned,
                ["eligible"] = job.Eligible,
                ["alreadyGenerated"] = job.AlreadyGenerated,
                ["queued"] = job.Queued,
                ["attempted"] = job.Attempted,
                ["generated"] = job.Generated,
                ["warnings"] = job.Warnings.Select(x => new Dictionary<string, object>(x)).ToList(),
                ["skipped"] = job.Skipped,
                ["failed"] = job.Failed,
                ["remaining"] = job.Remaining,
                ["currentCacheKey"] = job.CurrentCacheKey,
                ["currentCharacterName"] = job.CurrentCharacterName,
                ["startedUtc"] = job.StartedUtc,
                ["completedUtc"] = job.CompletedUtc,
                ["mode"] = job.Mode,
                ["delayMs"] = job.DelayMs,
                ["cancelRequested"] = job.CancelRequested,
                ["errors"] = job.Errors.Select(x => new Dictionary<string, object>(x)).ToList()
            };
        }

        private static void RecordSharedPortraitFailure(
            SharedPortraitGenerationJobState job,
            string cacheKey,
            string characterName,
            string error)
        {
            lock (SharedPortraitGenerationJobLock)
            {
                job.Failed++;
                job.Remaining--;
                AddSharedPortraitGenerationError(job, cacheKey, characterName, error);
            }
        }

        private static void AddSharedPortraitGenerationError(
            SharedPortraitGenerationJobState job,
            string cacheKey,
            string characterName,
            string error)
        {
            if (job.Errors.Count >= 100)
            {
                return;
            }
            job.Errors.Add(new Dictionary<string, object>
            {
                ["cacheKey"] = cacheKey ?? "",
                ["characterName"] = characterName ?? "",
                ["error"] = error ?? "Unknown image-generation error."
            });
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes ?? new byte[0]))
                    .Replace("-", "")
                    .ToLowerInvariant();
            }
        }

        private static List<Dictionary<string, object>> RunSharedPortraitGenerationSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            List<SharedPortraitEntry> entries = ReadSharedPortraitEntries();
            AddSharedPortraitSelfTest(
                rows,
                "shared_cache_catalog_detected",
                entries.Count > 0 && entries.All(x => !string.IsNullOrWhiteSpace(x.CacheKey)),
                "The installed _shared cache can be scanned without launching Bannerlord.");
            AddSharedPortraitSelfTest(
                rows,
                "cache_keys_unique",
                entries.Select(x => x.CacheKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == entries.Count,
                "Every character has one unambiguous Reign-compatible cache key.");
            AddSharedPortraitSelfTest(
                rows,
                "source_contract_complete",
                entries.Count > 0 && entries.All(x => x.HasPortrait || !string.IsNullOrWhiteSpace(x.HeroStringId)),
                "Every catalog entry has an AI master or identity for first-time native rendering.");
            AddSharedPortraitSelfTest(
                rows,
                "path_traversal_rejected",
                !TryLoadSharedPortraitEntry("..\\outside", out SharedPortraitEntry _, out string _),
                "Character selection cannot escape PortraitCache\\_shared.");
            string deleteTestRoot = Path.Combine(
                Path.GetTempPath(),
                "reign_shared_portrait_delete_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(deleteTestRoot);
                string[] preserved =
                {
                    "source.png",
                    "portrait_input.json",
                    "lord_export_info.txt"
                };
                foreach (string fileName in preserved)
                {
                    File.WriteAllText(Path.Combine(deleteTestRoot, fileName), "preserve", Encoding.UTF8);
                }
                foreach (string fileName in SharedPortraitGeneratedArtifactFileNames)
                {
                    File.WriteAllText(Path.Combine(deleteTestRoot, fileName), "delete", Encoding.UTF8);
                }
                List<string> deleted = DeleteSharedPortraitGeneratedArtifacts(deleteTestRoot);
                bool sourcesPreserved = preserved.All(
                    fileName => File.Exists(Path.Combine(deleteTestRoot, fileName)));
                bool generatedRemoved = SharedPortraitGeneratedArtifactFileNames.All(
                    fileName => !File.Exists(Path.Combine(deleteTestRoot, fileName)));
                AddSharedPortraitSelfTest(
                    rows,
                    "delete_generated_preserves_sources",
                    sourcesPreserved
                        && generatedRemoved
                        && deleted.Count == SharedPortraitGeneratedArtifactFileNames
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                    "Deleting one AI portrait removes its generated master, derivatives, prompt, and generation metadata while preserving the native source contract.");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(deleteTestRoot))
                    {
                        Directory.Delete(deleteTestRoot, true);
                    }
                }
                catch
                {
                }
            }
            if (entries.Count > 0)
            {
                Dictionary<string, object> payload =
                    BuildSharedPortraitGenerationPayload(entries[0], "NanoGPT");
                AddSharedPortraitSelfTest(
                    rows,
                    "generation_payload_contract",
                    ReadBool(payload, "sharedCacheOutput", false)
                        && !string.IsNullOrWhiteSpace(ReadString(payload, "cacheKey", ""))
                        && string.IsNullOrWhiteSpace(ReadString(payload, "sourceImageBase64", "")),
                    "Offline cache metadata supplies the identity fields and native source required by /portraits/generate.");
                AddSharedPortraitSelfTest(
                    rows,
                    "generation_provider_locked",
                    string.Equals(
                        ReadString(payload, "provider", ""),
                        "NanoGPT",
                        StringComparison.OrdinalIgnoreCase),
                    "Each individual or batch request is locked to the server-confirmed provider selected when that operation starts.");
                Dictionary<string, object> wanPayload = BuildAtlasImagePayload(
                    AtlasModelWan25ImageEdit,
                    "positive prompt",
                    "https://example.invalid/source.png",
                    "768*1024",
                    "custom negative prompt");
                AddSharedPortraitSelfTest(
                    rows,
                    "wan_negative_prompt_payload",
                    string.Equals(
                        ReadString(wanPayload, "negative_prompt", ""),
                        "custom negative prompt",
                        StringComparison.Ordinal),
                    "The editable WAN negative prompt is passed through the documented negative_prompt request field.");
                Dictionary<string, object> seedreamV5Payload = BuildAtlasImagePayload(
                    AtlasModelSeedreamV5ProEdit,
                    "positive prompt",
                    "https://example.invalid/source.png",
                    "768x1024",
                    "");
                ArrayList seedreamV5Images = seedreamV5Payload.TryGetValue("images", out object seedreamV5ImagesValue)
                    ? seedreamV5ImagesValue as ArrayList
                    : null;
                Dictionary<string, object> seedreamV5LandscapePayload = BuildAtlasImagePayload(
                    AtlasModelSeedreamV5ProEdit,
                    "positive prompt",
                    "https://example.invalid/source.png",
                    "1024x768",
                    "");
                AddSharedPortraitSelfTest(
                    rows,
                    "seedream_v5_pro_edit_payload",
                    string.Equals(
                        ReadString(seedreamV5Payload, "model", ""),
                        AtlasModelSeedreamV5ProEdit,
                        StringComparison.Ordinal)
                        && string.Equals(ReadString(seedreamV5Payload, "size", ""), "1530*2720", StringComparison.Ordinal)
                        && string.Equals(ReadString(seedreamV5LandscapePayload, "size", ""), "2720*1530", StringComparison.Ordinal)
                        && string.Equals(ReadString(seedreamV5Payload, "output_format", ""), "png", StringComparison.Ordinal)
                        && string.Equals(ReadString(seedreamV5Payload, "thinking", ""), "enabled", StringComparison.Ordinal)
                        && !ReadBool(seedreamV5Payload, "enable_base64_output", true)
                        && seedreamV5Images != null
                        && seedreamV5Images.Count == 1
                        && string.Equals(
                            Convert.ToString(seedreamV5Images[0]),
                            "https://example.invalid/source.png",
                            StringComparison.Ordinal),
                    "Seedream 5 Pro Edit receives its documented images[], orientation-aware 3K portrait/landscape size, PNG output, thinking, and URL-output fields.");
                string sharedRoot = SharedPortraitCacheRootForServer();
                ProcessStartInfo openFolderStartInfo = BuildSharedPortraitCacheOpenStartInfo(sharedRoot);
                AddSharedPortraitSelfTest(
                    rows,
                    "open_shared_cache_folder_contract",
                    Directory.Exists(sharedRoot)
                        && string.Equals(openFolderStartInfo.FileName, "explorer.exe", StringComparison.OrdinalIgnoreCase)
                        && openFolderStartInfo.UseShellExecute
                        && openFolderStartInfo.Arguments.IndexOf(
                            Path.GetFullPath(sharedRoot),
                        StringComparison.OrdinalIgnoreCase) >= 0,
                    "The Control Center opens the detected PortraitCache\\_shared folder in Windows Explorer without changing cache contents.");
                Dictionary<string, object> grokEditPayload = BuildAtlasImagePayload(
                    AtlasModelGrokImagineImageEdit,
                    "editing instruction",
                    "https://example.invalid/source.png",
                    "768x1024",
                    "");
                ArrayList grokEditImages = grokEditPayload.TryGetValue("image_urls", out object grokEditImagesValue)
                    ? grokEditImagesValue as ArrayList
                    : null;
                AddSharedPortraitSelfTest(
                    rows,
                    "grok_imagine_image_edit_payload",
                    string.Equals(
                        ReadString(grokEditPayload, "model", ""),
                        AtlasModelGrokImagineImageEdit,
                        StringComparison.Ordinal)
                        && string.Equals(ReadString(grokEditPayload, "aspect_ratio", ""), "auto", StringComparison.Ordinal)
                        && string.Equals(ReadString(grokEditPayload, "resolution", ""), "1k", StringComparison.Ordinal)
                        && ReadInt(grokEditPayload, "num_images", 0) == 1
                        && !ReadBool(grokEditPayload, "enable_base64_output", true)
                        && grokEditImages != null
                        && grokEditImages.Count == 1
                        && string.Equals(
                        Convert.ToString(grokEditImages[0]),
                        "https://example.invalid/source.png",
                        StringComparison.Ordinal),
                    "Grok Imagine Image Edit receives its documented image_urls[], single-output, automatic-aspect, 1K, and URL-output fields.");
                AddSharedPortraitSelfTest(
                    rows,
                    "atlas_non_png_output_normalization",
                    AtlasOutputNormalizationSelfTest(),
                    "AtlasCloud JPEG/BMP/GIF outputs are normalized to PNG before shared-cache validation.");
            }
            return rows;
        }

        private static bool AtlasOutputNormalizationSelfTest()
        {
            try
            {
                using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(2, 2))
                using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
                using (System.IO.MemoryStream jpeg = new System.IO.MemoryStream())
                {
                    graphics.Clear(System.Drawing.Color.Black);
                    bitmap.Save(jpeg, System.Drawing.Imaging.ImageFormat.Jpeg);
                    byte[] normalized = NormalizeAtlasOutputToPng(jpeg.ToArray());
                    return normalized != null
                        && normalized.Length >= 8
                        && normalized[0] == 137
                        && normalized[1] == 80
                        && normalized[2] == 78
                        && normalized[3] == 71;
                }
            }
            catch
            {
                return false;
            }
        }

        private static List<Dictionary<string, object>> RunPortraitProviderAdapterContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            Dictionary<string, object> seedreamV5Payload = BuildAtlasImagePayload(
                AtlasModelSeedreamV5ProEdit,
                "positive prompt",
                "https://example.invalid/source.png",
                "768x1024",
                "");
            ArrayList seedreamV5Images = seedreamV5Payload.TryGetValue("images", out object seedreamV5ImagesValue)
                ? seedreamV5ImagesValue as ArrayList
                : null;
            Dictionary<string, object> seedreamV5LandscapePayload = BuildAtlasImagePayload(
                AtlasModelSeedreamV5ProEdit,
                "positive prompt",
                "https://example.invalid/source.png",
                "1024x768",
                "");
            AddSharedPortraitSelfTest(
                rows,
                "seedream_v5_pro_edit_payload",
                string.Equals(
                    ReadString(seedreamV5Payload, "model", ""),
                    AtlasModelSeedreamV5ProEdit,
                    StringComparison.Ordinal)
                    && string.Equals(ReadString(seedreamV5Payload, "size", ""), "1530*2720", StringComparison.Ordinal)
                    && string.Equals(ReadString(seedreamV5LandscapePayload, "size", ""), "2720*1530", StringComparison.Ordinal)
                    && string.Equals(ReadString(seedreamV5Payload, "output_format", ""), "png", StringComparison.Ordinal)
                    && string.Equals(ReadString(seedreamV5Payload, "thinking", ""), "enabled", StringComparison.Ordinal)
                    && !ReadBool(seedreamV5Payload, "enable_base64_output", true)
                    && seedreamV5Images != null
                    && seedreamV5Images.Count == 1
                    && string.Equals(
                        Convert.ToString(seedreamV5Images[0]),
                        "https://example.invalid/source.png",
                        StringComparison.Ordinal),
                "Seedream 5 Pro Edit receives its documented images[], orientation-aware 3K portrait/landscape size, PNG output, thinking, and URL-output fields.");

            Dictionary<string, object> legacySettings = new Dictionary<string, object>
            {
                ["portraitProvider"] = "AtlasCloud",
                ["portraitNanoGptImageModel"] = "gpt-image-1",
                ["portraitAtlasImageModel"] = AtlasModelSeedreamV5ProEdit,
                ["portraitAtlasWanNegativePrompt"] = "portrait atlas negative",
                ["portraitImageStrength"] = 0.61d,
                ["portraitInferenceSteps"] = 31,
                ["portraitGuidanceScale"] = 4.2d
            };
            bool migrated = NormalizePortraitImageSettings(legacySettings);
            AddSharedPortraitSelfTest(
                rows,
                "image_profile_migration_clones_portrait_once",
                migrated
                    && ReadBool(legacySettings, "sceneryImageProfileInitialized", false)
                    && ReadString(legacySettings, "sceneryProvider", "") == "AtlasCloud"
                    && ReadString(legacySettings, "sceneryNanoGptImageModel", "") == "gpt-image-1"
                    && ReadString(legacySettings, "sceneryAtlasImageModel", "") == AtlasModelSeedreamV5ProEdit
                    && ReadString(legacySettings, "sceneryAtlasWanNegativePrompt", "") == "portrait atlas negative"
                    && Math.Abs(ReadDouble(legacySettings, "sceneryImageStrength", 0d) - 0.61d) < 0.001d
                    && ReadInt(legacySettings, "sceneryInferenceSteps", 0) == 31
                    && Math.Abs(ReadDouble(legacySettings, "sceneryGuidanceScale", 0d) - 4.2d) < 0.001d,
                "A legacy installation copies its complete portrait profile into the new scenery profile before the profiles diverge.");

            legacySettings["portraitProvider"] = "NanoGPT";
            legacySettings["portraitAtlasImageModel"] = AtlasModelWan25ImageEdit;
            bool normalizedAgain = NormalizePortraitImageSettings(legacySettings);
            AddSharedPortraitSelfTest(
                rows,
                "image_profile_migration_does_not_overwrite_scenery",
                !normalizedAgain
                    && ReadString(legacySettings, "sceneryProvider", "") == "AtlasCloud"
                    && ReadString(legacySettings, "sceneryAtlasImageModel", "") == AtlasModelSeedreamV5ProEdit,
                "Once initialized, later portrait changes do not overwrite the independently persisted scenery profile.");

            Dictionary<string, object> splitSettings = new Dictionary<string, object>(legacySettings);
            splitSettings["portraitProvider"] = "NanoGPT";
            splitSettings["portraitNanoGptImageModel"] = "gpt-image-1.5";
            splitSettings["sceneryProvider"] = "AtlasCloud";
            splitSettings["sceneryAtlasImageModel"] = AtlasModelSeedreamV5ProEdit;
            splitSettings["sceneryAtlasWanNegativePrompt"] = "scenery-only-negative";
            splitSettings["sceneryImageStrength"] = 0.88d;
            splitSettings["sceneryInferenceSteps"] = 44;
            splitSettings["sceneryGuidanceScale"] = 6.5d;
            ImageGenerationProfile portraitProfile = ResolveImageGenerationProfile(splitSettings, "portrait");
            ImageGenerationProfile legacyProfile = ResolveImageGenerationProfile(splitSettings, "");
            ImageGenerationProfile memoryProfile = ResolveImageGenerationProfile(splitSettings, "memory");
            ImageGenerationProfile futureProfile = ResolveImageGenerationProfile(splitSettings, "future_event_art");
            ImageGenerationProfile overrideProfile = ResolveImageGenerationProfile(splitSettings, "castle_scene", "NanoGPT");
            AddSharedPortraitSelfTest(
                rows,
                "image_profile_purpose_routing",
                portraitProfile.Name == "portrait"
                    && portraitProfile.Provider == "NanoGPT"
                    && portraitProfile.NanoGptModel == "gpt-image-1.5"
                    && legacyProfile.Name == "portrait"
                    && memoryProfile.Name == "scenery"
                    && memoryProfile.Provider == "AtlasCloud"
                    && memoryProfile.AtlasModel == AtlasModelSeedreamV5ProEdit
                    && memoryProfile.AtlasWanNegativePrompt == "scenery-only-negative"
                    && Math.Abs(memoryProfile.ImageStrength - 0.88d) < 0.001d
                    && memoryProfile.InferenceSteps == 44
                    && Math.Abs(memoryProfile.GuidanceScale - 6.5d) < 0.001d
                    && futureProfile.Name == "scenery"
                    && overrideProfile.Name == "scenery"
                    && overrideProfile.Provider == "NanoGPT",
                "Missing and portrait purposes use Portrait Images; all explicit non-portrait purposes use Scenery & Event Images while explicit provider overrides retain purpose-specific models and tuning.");

            string contractedPrompt = ApplyPortraitProductPromptContract("identity prompt");
            AddSharedPortraitSelfTest(
                rows,
                "provider_independent_portrait_product_prompt",
                contractedPrompt.Contains("REIGN PORTRAIT PRODUCT CONTRACT - PROVIDER INDEPENDENT")
                    && contractedPrompt.Contains("face must occupy 9 to 18 percent")
                    && contractedPrompt.Contains("complete head-to-toe figure")
                    && contractedPrompt.Contains("Do not zoom out"),
                "Every portrait provider receives the same measurable full-body, face-scale, centering, and scenery-exclusion contract.");

            Dictionary<string, object> nativeSettings = new Dictionary<string, object>();
            bool nativeSettingsInitialized = NormalizePortraitImageSettings(nativeSettings);
            AddSharedPortraitSelfTest(
                rows,
                "native_portrait_source_required_by_default",
                nativeSettingsInitialized
                    && ReadString(nativeSettings, "portraitNativeSourceMode", "") == "required"
                    && nativeSettings.ContainsKey("portraitNativeSourceGeneratorPath"),
                "Portrait generation defaults to the packaged native 768x1024 source generator and fails closed when that source cannot be produced.");

            string controlCenterHtml = ControlCenterHtml();
            AddSharedPortraitSelfTest(
                rows,
                "split_image_profiles_control_center_contract",
                controlCenterHtml.Contains("id='portraitProvider'")
                    && controlCenterHtml.Contains("id='sceneryProvider'")
                    && controlCenterHtml.Contains("id='portraitNanoGptImageModel'")
                    && controlCenterHtml.Contains("id='sceneryNanoGptImageModel'")
                    && controlCenterHtml.Contains("id='portraitAtlasImageModel'")
                    && controlCenterHtml.Contains("id='portraitNativeSourceMode'")
                    && controlCenterHtml.Contains("id='portraitNativeSourceGeneratorPath'")
                    && controlCenterHtml.Contains("id='sceneryAtlasImageModel'")
                    && controlCenterHtml.Contains("Shared Provider Connections")
                    && !controlCenterHtml.Contains("id='sceneryNanoGptApiKey'")
                    && !controlCenterHtml.Contains("id='sceneryAtlasApiKey'")
                    && controlCenterHtml.Contains("updateImageProfileUi()")
                    && controlCenterHtml.Contains("'gemini-3-pro-image-preview'")
                    && controlCenterHtml.Contains("'xai/grok-imagine-image-quality/edit'"),
                "The Control Center exposes four complete independent profile selectors with the full provider/model catalog and one shared connection area.");
            rows.AddRange(RunAdultPortraitContractTests());
            return rows;
        }

        private static void AddSharedPortraitSelfTest(
            List<Dictionary<string, object>> rows,
            string name,
            bool passed,
            string detail)
        {
            rows.Add(new Dictionary<string, object>
            {
                ["name"] = name,
                ["passed"] = passed,
                ["detail"] = detail
            });
        }
    }
}
