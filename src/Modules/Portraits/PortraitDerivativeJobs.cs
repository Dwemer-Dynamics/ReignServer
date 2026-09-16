using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AIPortraits;
using ReignPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private sealed class PortraitDerivativeJobState
        {
            public string JobId = "";
            public string State = "idle";
            public string Mode = "missing";
            public string Root = "";
            public int Scanned;
            public int Eligible;
            public int AlreadyReady;
            public int Generated;
            public int FaceFocused;
            public int NeedsReview;
            public int Failed;
            public int Remaining;
            public string CurrentFolder = "";
            public string StartedUtc = "";
            public string CompletedUtc = "";
            public readonly List<Dictionary<string, object>> Errors = new List<Dictionary<string, object>>();
            public readonly List<string> ReviewFolders = new List<string>();
        }

        private static readonly object PortraitDerivativeJobLock = new object();
        private static PortraitDerivativeJobState ActivePortraitDerivativeJob = new PortraitDerivativeJobState();

        private static Dictionary<string, object> StartPortraitDerivativeBuildApi()
        {
            return StartPortraitDerivativeBuildApi("missing");
        }

        private static Dictionary<string, object> StartPortraitDerivativeFallbackRepairApi()
        {
            return StartPortraitDerivativeBuildApi("legacy_fallback");
        }

        private static Dictionary<string, object> StartPortraitDerivativeBuildApi(string mode)
        {
            lock (PortraitDerivativeJobLock)
            {
                if (string.Equals(ActivePortraitDerivativeJob.State, "running", StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, object> active = PortraitDerivativeJobSnapshot(ActivePortraitDerivativeJob);
                    active["attached"] = true;
                    return active;
                }

                string root = PortraitCacheRootForServer();
                PortraitDerivativeJobState job = new PortraitDerivativeJobState
                {
                    JobId = Guid.NewGuid().ToString("N"),
                    State = "running",
                    Mode = mode ?? "missing",
                    Root = root,
                    StartedUtc = DateTime.UtcNow.ToString("o")
                };
                ActivePortraitDerivativeJob = job;
                Task.Run(() => RunPortraitDerivativeBuild(job));
                return PortraitDerivativeJobSnapshot(job);
            }
        }

        private static Dictionary<string, object> PortraitDerivativeBuildStatusApi()
        {
            lock (PortraitDerivativeJobLock)
            {
                return PortraitDerivativeJobSnapshot(ActivePortraitDerivativeJob);
            }
        }

        private static void RunPortraitDerivativeBuild(PortraitDerivativeJobState job)
        {
            try
            {
                var installation = Reign.Core.Contracts.Platform.ReignInstallation.TryLoadCurrent();
                string sharedRoot = installation == null ? "" : Path.Combine(installation.ContentRoot, "PortraitCache");
                if ((string.IsNullOrWhiteSpace(job.Root) || !Directory.Exists(job.Root)) && !Directory.Exists(sharedRoot))
                {
                    FinishPortraitDerivativeJob(job, "failed", "The installed Reign PortraitCache folder was not found.", job.Root);
                    return;
                }

                List<string> discovered = DiscoverPortraitDerivativeFolders(job.Root, out int scanned);
                if (!string.IsNullOrEmpty(sharedRoot) && !sharedRoot.Equals(job.Root, StringComparison.OrdinalIgnoreCase))
                {
                    discovered.AddRange(DiscoverPortraitDerivativeFolders(sharedRoot, out int sharedScanned));
                    scanned += sharedScanned;
                    discovered = discovered.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
                List<string> folders = string.Equals(job.Mode, "legacy_fallback", StringComparison.OrdinalIgnoreCase)
                    ? discovered.Where(folder => IsCampaignLegacyFallbackPortrait(folder)).ToList()
                    : discovered;
                lock (PortraitDerivativeJobLock)
                {
                    job.Scanned = scanned;
                    job.Eligible = folders.Count;
                    job.Remaining = folders.Count;
                }

                foreach (string folder in folders)
                {
                    string sourcePath = Path.Combine(folder, "portrait.png");
                    string relativeFolder = MakeRelativePath(job.Root, folder);
                    lock (PortraitDerivativeJobLock)
                    {
                        job.CurrentFolder = relativeFolder;
                    }

                    try
                    {
                        if (!string.Equals(job.Mode, "legacy_fallback", StringComparison.OrdinalIgnoreCase)
                            && TryReadCurrentPortraitDerivativeManifest(sourcePath, folder, out var _))
                        {
                            lock (PortraitDerivativeJobLock)
                            {
                                job.AlreadyReady++;
                                job.Remaining--;
                            }
                            continue;
                        }

                        PortraitDerivativeBuild completedBuild = BuildPortraitDerivativesFromMaster(sourcePath, folder);
                        lock (PortraitDerivativeJobLock)
                        {
                            job.Generated++;
                            job.FaceFocused++;
                            if (completedBuild.Manifest.NeedsReview)
                            {
                                job.NeedsReview++;
                                if (job.ReviewFolders.Count < 100) job.ReviewFolders.Add(relativeFolder);
                            }
                            job.Remaining--;
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (PortraitDerivativeJobLock)
                        {
                            job.Failed++;
                            job.Remaining--;
                            AddPortraitDerivativeError(job, relativeFolder, ex.Message);
                        }
                    }
                }

                lock (PortraitDerivativeJobLock)
                {
                    job.State = job.Failed == 0 ? "completed" : "completed_with_errors";
                    job.CurrentFolder = "";
                    job.Remaining = 0;
                    job.CompletedUtc = DateTime.UtcNow.ToString("o");
                }
                LogOperational("portrait.derivatives.completed", PortraitDerivativeJobSnapshot(job));
            }
            catch (Exception ex)
            {
                FinishPortraitDerivativeJob(job, "failed", ex.Message, job.CurrentFolder);
            }
        }

        private static PortraitDerivativeBuild BuildPortraitDerivativesFromMaster(string sourcePath, string outputDirectory)
        {
            FileInfo sourceInfo = new FileInfo(sourcePath);
            if (!sourceInfo.Exists)
            {
                throw new FileNotFoundException("The full-body portrait master was not found.", sourcePath);
            }

            byte[] sourceBytes = File.ReadAllBytes(sourcePath);
            byte[] rgba = PngReencode.DecodeToRgba(sourceBytes, out int sourceWidth, out int sourceHeight);
            if (rgba == null || sourceWidth <= 0 || sourceHeight <= 0)
            {
                throw new InvalidDataException("portrait.png is not a decodable PNG image.");
            }
            PortraitFaceFocus focus = DetectPortraitFaceFocus(rgba, sourceWidth, sourceHeight);

            PortraitDerivativeBuild build = PortraitDerivativeCore.BuildV2(
                rgba,
                sourceWidth,
                sourceHeight,
                sourceInfo,
                PngEncoder.EncodeRgba,
                focus);
            byte[] manifestBytes = Encoding.UTF8.GetBytes(Json.Serialize(build.Manifest));
            if (!PortraitDerivativeCore.CommitV2(sourcePath, outputDirectory, build, manifestBytes, out string error))
            {
                throw new IOException(error ?? "The current portrait derivatives could not be committed.");
            }
            return build;
        }

        private static bool TryReadCurrentPortraitDerivativeManifest(string sourcePath, string outputDirectory, out PortraitDerivativeManifestV2 manifest)
        {
            manifest = null;
            try
            {
                string path = Path.Combine(outputDirectory, PortraitDerivativeCore.ManifestFileName);
                if (!File.Exists(path))
                {
                    return false;
                }
                manifest = Json.Deserialize<PortraitDerivativeManifestV2>(File.ReadAllText(path, Encoding.UTF8));
                return PortraitDerivativeCore.IsCurrentV2(manifest, sourcePath, outputDirectory);
            }
            catch
            {
                manifest = null;
                return false;
            }
        }

        private static bool IsCampaignLegacyFallbackPortrait(string folder)
        {
            try
            {
                DirectoryInfo info = new DirectoryInfo(folder);
                if (info.Parent == null || info.Parent.Name.Equals("_shared", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                string sourcePath = Path.Combine(folder, "portrait.png");
                return TryReadCurrentPortraitDerivativeManifest(sourcePath, folder, out PortraitDerivativeManifestV2 manifest)
                    && string.Equals(manifest.FocusMethod, "legacy_fallback", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static List<string> DiscoverPortraitDerivativeFolders(string root, out int scanned)
        {
            scanned = 0;
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return result;
            }

            foreach (string cacheRoot in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (ExcludePortraitCacheDirectory(new DirectoryInfo(cacheRoot), topLevel: true))
                {
                    continue;
                }

                foreach (string folder in Directory.GetDirectories(cacheRoot).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    DirectoryInfo info = new DirectoryInfo(folder);
                    if (ExcludePortraitCacheDirectory(info, topLevel: false))
                    {
                        continue;
                    }

                    scanned++;
                    if (File.Exists(Path.Combine(folder, "portrait.png")))
                    {
                        result.Add(folder);
                    }
                }
            }
            return result;
        }

        private static bool ExcludePortraitCacheDirectory(DirectoryInfo directory, bool topLevel)
        {
            if (directory == null)
            {
                return true;
            }

            string name = directory.Name ?? "";
            FileAttributes attributes;
            try { attributes = directory.Attributes; }
            catch { return true; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Hidden) != 0)
            {
                return true;
            }

            if (name.StartsWith(".", StringComparison.Ordinal)
                || name.StartsWith("__", StringComparison.Ordinal)
                || name.IndexOf(".incoming", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("backup", StringComparison.OrdinalIgnoreCase) >= 0
                || name.Equals("unresolved", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return topLevel && name.StartsWith("_", StringComparison.Ordinal) && !name.Equals("_shared", StringComparison.OrdinalIgnoreCase);
        }

        private static string PortraitCacheRootForServer()
        {
            var installation = Reign.Core.Contracts.Platform.ReignInstallation.TryLoadCurrent();
            if (installation != null) return installation.PortraitCacheRoot;
            DirectoryInfo app = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            string module = app.Parent?.Parent?.FullName ?? "";
            return string.IsNullOrWhiteSpace(module) ? "" : Path.Combine(module, "PortraitCache");
        }

        private static void FinishPortraitDerivativeJob(PortraitDerivativeJobState job, string state, string error, string folder)
        {
            lock (PortraitDerivativeJobLock)
            {
                job.State = state;
                job.CurrentFolder = "";
                job.CompletedUtc = DateTime.UtcNow.ToString("o");
                AddPortraitDerivativeError(job, folder, error);
            }
            LogOperational("portrait.derivatives.failed", PortraitDerivativeJobSnapshot(job));
        }

        private static void AddPortraitDerivativeError(PortraitDerivativeJobState job, string folder, string error)
        {
            if (job.Errors.Count >= 20)
            {
                return;
            }
            job.Errors.Add(new Dictionary<string, object>
            {
                ["folder"] = folder ?? "",
                ["error"] = error ?? "Unknown portrait derivative error."
            });
        }

        private static Dictionary<string, object> PortraitDerivativeJobSnapshot(PortraitDerivativeJobState job)
        {
            job = job ?? new PortraitDerivativeJobState();
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["jobId"] = job.JobId,
                ["state"] = job.State,
                ["mode"] = job.Mode,
                ["root"] = job.Root,
                ["scanned"] = job.Scanned,
                ["eligible"] = job.Eligible,
                ["alreadyReady"] = job.AlreadyReady,
                ["generated"] = job.Generated,
                ["faceFocused"] = job.FaceFocused,
                ["needsReview"] = job.NeedsReview,
                ["failed"] = job.Failed,
                ["remaining"] = job.Remaining,
                ["currentFolder"] = job.CurrentFolder,
                ["startedUtc"] = job.StartedUtc,
                ["completedUtc"] = job.CompletedUtc,
                ["errors"] = job.Errors.Select(x => new Dictionary<string, object>(x)).ToList(),
                ["reviewFolders"] = job.ReviewFolders.ToList()
            };
        }

        private static string MakeRelativePath(string root, string path)
        {
            try
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string fullPath = Path.GetFullPath(path);
                return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                    ? fullPath.Substring(fullRoot.Length)
                    : fullPath;
            }
            catch
            {
                return path ?? "";
            }
        }

        private static List<Dictionary<string, object>> RunPortraitDerivativeSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            string tempRoot = Path.Combine(Path.GetTempPath(), "reign_portrait_derivatives_" + Guid.NewGuid().ToString("N"));
            try
            {
                AddPortraitDerivativeTest(rows, "native_keep_wide_route", 
                    PortraitDerivativeCore.UsesNativeWideThumbnail("CharacterImage", "GameMenuPartyItemButtonWidget", false)
                    && PortraitDerivativeCore.UsesNativeWideThumbnail("CharacterImage", "GameMenuPartyItemButtonWidget", true)
                    && PortraitDerivativeCore.UsesNativeWideThumbnail("OtherImage", null, true)
                    && !PortraitDerivativeCore.UsesNativeWideThumbnail("OtherImage", "GameMenuPartyItemButtonWidget", false)
                    && !PortraitDerivativeCore.UsesNativeWideThumbnail("CharacterImage", "UnrelatedWidget", false)
                    && !PortraitDerivativeCore.UsesNativeWideThumbnail(null, null, false),
                    "Keep and settlement rows use the wide derivative before layout, on hot refresh and reopening; unrelated and zoom widgets are not reclassified.");
                var abeilFocus = new PortraitFaceFocus { Method = "ultraface", Model = "version-RFB-320",
                    Confidence = 0.999909520149231d, CandidateCount = 1,
                    X = 0.46196165680885315d, Y = 0.087346002459526062d,
                    Width = 0.09491083025932312d, Height = 0.10555821657180786d };
                PortraitDerivativeCore.CalculateFocusedCrop(864, 1152, abeilFocus, 1.4d,
                    out int ax, out int ay, out int aw, out int ah);
                AddPortraitDerivativeTest(rows, "native_keep_abeil_composition", aw == 365 && ah == 261
                    && ay == 70 && Math.Abs((117d / 85d) / ((double)aw / ah) - 1d) < 0.02d,
                    "Abeil's wide derivative fits the native 117x85 slot within 2%, retaining head and upper chest instead of the vertical-thumbnail 1.7x recrop.");
                PortraitDerivativeCore.CalculateChestCrop(1024, 1536, out int x, out int y, out int width, out int height);
                AddPortraitDerivativeTest(rows, "crop_1024x1536", x == 266 && y == 0 && width == 491 && height == 614,
                    "Expected x=266,y=0,w=491,h=614; received x=" + x + ",y=" + y + ",w=" + width + ",h=" + height + ".");

                bool geometryValid = true;
                foreach (int[] size in new[] { new[] { 1000, 1000 }, new[] { 1600, 900 }, new[] { 200, 1000 }, new[] { 1, 1 } })
                {
                    PortraitDerivativeCore.CalculateChestCrop(size[0], size[1], out int gx, out int gy, out int gw, out int gh);
                    geometryValid &= gx >= 0 && gy == 0 && gw > 0 && gh > 0
                        && gx + gw <= size[0] && gy + gh <= size[1]
                        && gh <= Math.Max(1, (int)Math.Round(size[1] * 0.4d, MidpointRounding.AwayFromZero));
                }
                AddPortraitDerivativeTest(rows, "crop_geometry_bounds", geometryValid, "Square, landscape, narrow, and one-pixel sources remain inside the source and end by 40% height.");

                var testFocus = new PortraitFaceFocus
                {
                    Method = "test",
                    Model = "synthetic",
                    Confidence = 0.99d,
                    CandidateCount = 1,
                    X = 0.4d,
                    Y = 0.05d,
                    Width = 0.2d,
                    Height = 0.1d
                };
                PortraitDerivativeCore.CalculateFocusedCrop(100, 150, testFocus, 0.8d,
                    out int focusX, out int focusY, out int focusWidth, out int focusHeight);
                AddPortraitDerivativeTest(rows, "face_focused_neck_crop", focusX == 37 && focusY == 4 && focusWidth == 26 && focusHeight == 32,
                    "Expected x=37,y=4,w=26,h=32; received x=" + focusX + ",y=" + focusY + ",w=" + focusWidth + ",h=" + focusHeight + ".");

                string output = Path.Combine(tempRoot, "cache", "campaign", "hero");
                Directory.CreateDirectory(output);
                int sourceWidth = 100;
                int sourceHeight = 150;
                byte[] rgba = new byte[sourceWidth * sourceHeight * 4];
                for (int py = 0; py < sourceHeight; py++)
                {
                    for (int px = 0; px < sourceWidth; px++)
                    {
                        int index = (py * sourceWidth + px) * 4;
                        rgba[index] = py < 15 ? (byte)255 : (byte)0;
                        rgba[index + 1] = py >= 15 && py < 60 ? (byte)255 : (byte)0;
                        rgba[index + 2] = py >= 60 ? (byte)255 : (byte)0;
                        rgba[index + 3] = 255;
                    }
                }

                string sourcePath = Path.Combine(output, "portrait.png");
                byte[] sourcePng = PngEncoder.EncodeRgba(rgba, sourceWidth, sourceHeight);
                File.WriteAllBytes(sourcePath, sourcePng);
                byte[] masterBefore = File.ReadAllBytes(sourcePath);
                FileInfo sourceInfo = new FileInfo(sourcePath);
                PortraitDerivativeBuild build = PortraitDerivativeCore.BuildV2(rgba, sourceWidth, sourceHeight, sourceInfo, PngEncoder.EncodeRgba, testFocus);
                byte[] manifestBytes = Encoding.UTF8.GetBytes(Json.Serialize(build.Manifest));
                bool committed = PortraitDerivativeCore.CommitV2(sourcePath, output, build, manifestBytes, out string commitError);
                AddPortraitDerivativeTest(rows, "atomic_v2_commit", committed, commitError ?? "The complete v2 set committed successfully.");

                bool missingFocusRejected = false;
                try
                {
                    PortraitDerivativeCore.BuildRequiredFocusedV2(
                        rgba,
                        sourceWidth,
                        sourceHeight,
                        sourceInfo,
                        PngEncoder.EncodeRgba,
                        null);
                }
                catch (InvalidDataException)
                {
                    missingFocusRejected = true;
                }
                AddPortraitDerivativeTest(rows, "generated_product_rejects_legacy_fallback", missingFocusRejected,
                    "A newly generated AI portrait cannot build display derivatives without exactly one authoritative face focus.");

                byte[] chest = PngReencode.DecodeToRgba(build.ThumbnailPng, out int chestWidth, out int chestHeight);
                byte[] party = PngReencode.DecodeToRgba(build.PartyThumbnailPng, out int partyWidth, out int partyHeight);
                byte[] zoom = PngReencode.DecodeToRgba(build.ZoomPng, out int zoomWidth, out int zoomHeight);
                bool chestHasBlue = ContainsStrongBlue(chest);
                bool zoomHasBlue = ContainsStrongBlue(zoom);
                bool markersValid = chest != null && party != null && zoom != null && !chestHasBlue && !ContainsStrongBlue(party) && zoomHasBlue
                    && chestWidth == 26 && chestHeight == 32 && partyWidth == 45 && partyHeight == 32
                    && zoomWidth == sourceWidth && zoomHeight == sourceHeight;
                AddPortraitDerivativeTest(rows, "chest_excludes_legs_zoom_keeps_full_body", markersValid,
                    "Chest=" + chestWidth + "x" + chestHeight + " blue=" + chestHasBlue + "; party=" + partyWidth + "x" + partyHeight
                    + "; zoom=" + zoomWidth + "x" + zoomHeight + " blue=" + zoomHasBlue + ".");

                bool masterUnchanged = masterBefore.SequenceEqual(File.ReadAllBytes(sourcePath));
                bool current = TryReadCurrentPortraitDerivativeManifest(sourcePath, output, out PortraitDerivativeManifestV2 readManifest);
                AddPortraitDerivativeTest(rows, "master_unchanged_and_manifest_current", masterUnchanged && current
                    && readManifest.CropHeight == 32 && readManifest.WideCropHeight == 32 && readManifest.FocusMethod == "test",
                    "The source bytes were preserved and the committed manifest validates against all four derivatives.");

                File.SetLastWriteTimeUtc(sourcePath, File.GetLastWriteTimeUtc(sourcePath).AddSeconds(2));
                bool staleRejected = !TryReadCurrentPortraitDerivativeManifest(sourcePath, output, out var _);
                AddPortraitDerivativeTest(rows, "stale_master_rejected", staleRejected, "A changed source signature invalidates the derivative set.");

                string scanRoot = Path.Combine(tempRoot, "scan");
                CreatePortraitTestMaster(Path.Combine(scanRoot, "_shared", "shared_hero"), sourcePng);
                CreatePortraitTestMaster(Path.Combine(scanRoot, "campaign_a", "campaign_hero"), sourcePng);
                CreatePortraitTestMaster(Path.Combine(scanRoot, "__backup", "ignored"), sourcePng);
                CreatePortraitTestMaster(Path.Combine(scanRoot, "old.backup", "ignored"), sourcePng);
                CreatePortraitTestMaster(Path.Combine(scanRoot, "campaign_a", "unresolved"), sourcePng);
                List<string> discovered = DiscoverPortraitDerivativeFolders(scanRoot, out int scanned);
                AddPortraitDerivativeTest(rows, "cache_root_filtering", discovered.Count == 2 && scanned == 2,
                    "Discovered=" + discovered.Count + ", scanned=" + scanned + "; backup and unresolved folders were excluded.");

                Dictionary<string, object> oldSettings = new Dictionary<string, object>
                {
                    ["portraitNanoGptImageModel"] = "step-image-edit-2",
                    ["portraitNanoGptImageEditsUrl"] = "https://unused.invalid"
                };
                bool migrated = NormalizePortraitImageSettings(oldSettings);
                AddPortraitDerivativeTest(rows, "step_model_migration", migrated
                    && ReadString(oldSettings, "portraitNanoGptImageModel", "") == "gpt-image-1.5"
                    && !oldSettings.ContainsKey("portraitNanoGptImageEditsUrl"),
                    "Legacy Step settings migrate to gpt-image-1.5 and the unused edits URL is removed.");

                string recoveryDir = Path.Combine(tempRoot, "recovery");
                Directory.CreateDirectory(recoveryDir);
                string recoveryPortrait = Path.Combine(recoveryDir, "portrait.png");
                string recoverySource = Path.Combine(recoveryDir, "source.png");
                string recoveryPrompt = Path.Combine(recoveryDir, "prompt.txt");
                string recoveryMetadata = Path.Combine(recoveryDir, "portrait.json");
                string recoveryOperationId = "0123456789abcdef0123456789abcdef";
                File.WriteAllBytes(recoveryPortrait, sourcePng);
                File.WriteAllBytes(recoverySource, sourcePng);
                File.WriteAllText(recoveryPrompt, "recovery prompt", Encoding.UTF8);
                WriteJsonObject(recoveryMetadata, new Dictionary<string, object>
                {
                    ["campaignId"] = "campaign_recovery",
                    ["heroStringId"] = "hero_recovery",
                    ["cacheKey"] = "Recovery Hero (hero_recovery)",
                    ["generationOperationId"] = recoveryOperationId,
                    ["generationStatus"] = "completed",
                    ["provider"] = "contract-provider",
                    ["model"] = "contract-model",
                    ["adapter"] = "contract-adapter",
                    ["portraitInput"] = new Dictionary<string, object> { ["schema"] = "reign-portrait-input-v1" },
                    ["productReceipt"] = new Dictionary<string, object>
                    {
                        ["schema"] = "reign-portrait-product-v1",
                        ["version"] = 1,
                        ["accepted"] = true
                    }
                });
                Dictionary<string, object> recovered = BuildStoredPortraitGenerationResponseFromPaths(
                    "campaign_recovery", "hero_recovery", recoveryOperationId,
                    recoveryPortrait, recoverySource, recoveryPrompt, recoveryMetadata);
                Dictionary<string, object> wrongOperation = BuildStoredPortraitGenerationResponseFromPaths(
                    "campaign_recovery", "hero_recovery", "ffffffffffffffffffffffffffffffff",
                    recoveryPortrait, recoverySource, recoveryPrompt, recoveryMetadata);
                bool recoveryValid = ReadBool(recovered, "ok", false)
                    && ReadBool(recovered, "recovered", false)
                    && ReadString(recovered, "generationStatus", "") == "completed"
                    && ReadString(recovered, "generationOperationId", "") == recoveryOperationId
                    && ReadString(recovered, "effectivePrompt", "") == "recovery prompt"
                    && Convert.FromBase64String(ReadString(recovered, "imageBase64", "")).SequenceEqual(sourcePng)
                    && ReadString(wrongOperation, "generationStatus", "") == "not_found";
                AddPortraitDerivativeTest(rows, "completed_generation_recovery_contract", recoveryValid,
                    "Only the matching durable operation id can recover the accepted stored master, source, prompt, and receipt.");
                int nativeCalls = 0;
                var compositionFocus = new PortraitFaceFocus { Confidence = .99d, CandidateCount = 1, X = .4583457112312317d,
                    Y = .14977461099624634d, Width = .08709597587585449d, Height = .08431899547576904d };
                AddPortraitDerivativeTest(rows, "composition_downloaded_grok_face", PortraitCompositionError(compositionFocus) == null,
                    "The measured 8.43% Grok face passes without changing its original master or derivative crop.");
                var vaminesaFocus = new PortraitFaceFocus { Confidence = .999956488609314d, CandidateCount = 1,
                    X = .43885672092437744d, Y = .07708021998405457d,
                    Width = .10259813070297241d, Height = .08894810080528259d };
                AddPortraitDerivativeTest(rows, "composition_vaminesa_client_server_parity",
                    PortraitCompositionError(vaminesaFocus) == null
                    && PortraitDerivativeCore.PortraitCompositionError(vaminesaFocus) == null,
                    "The saved Vaminesa receipt's 8.89481008% face passes the one shared client/server gate without regenerating its image.");
                string compositionSourceRoot = FindVerificationSourceRoot();
                string compositionClientPath = string.IsNullOrEmpty(compositionSourceRoot) ? "" : Path.Combine(compositionSourceRoot,
                    "ReignBeta", "src", "Modules", "Portraits", "AIPortraits", "PortraitCache.cs");
                string compositionClientSource = File.Exists(compositionClientPath) ? File.ReadAllText(compositionClientPath) : "";
                AddPortraitDerivativeTest(rows, "composition_client_shared_gate_wiring",
                    compositionClientSource.Contains("PortraitDerivativeCore.PortraitCompositionError(product.FaceFocus)")
                    && !compositionClientSource.Contains("product.FaceFocus.Height <")
                    && !compositionClientSource.Contains("product.FaceFocus.Y <"),
                    "The compiled client import source invokes the shared gate without a second divergent numeric threshold.");
                int validationStart = compositionClientSource.IndexOf("ValidatePortraitProduct(product);", StringComparison.Ordinal);
                int commitStart = compositionClientSource.IndexOf("string outputDirectory = DirFor(id);", validationStart < 0 ? 0 : validationStart, StringComparison.Ordinal);
                string rejectionHandling = validationStart >= 0 && commitStart > validationStart
                    ? compositionClientSource.Substring(validationStart, commitStart - validationStart) : "";
                AddPortraitDerivativeTest(rows, "composition_rejection_retires_matching_operation",
                    rejectionHandling.Contains("ex is InvalidDataException")
                    && rejectionHandling.Contains("!string.IsNullOrWhiteSpace(product.GenerationOperationId)")
                    && rejectionHandling.Contains("ClearPendingGenerationOperation(id, product.GenerationOperationId)")
                    && rejectionHandling.Contains("catch (Exception ex)"),
                    "Terminal product validation retires its matching pending operation before commit; separate resource/commit failure handling keeps delivery recovery available. Native import acceptance remains separate.");
                compositionFocus.Height = .08d;
                bool lowerBoundary = PortraitCompositionError(compositionFocus) == null;
                compositionFocus.Height = .0799d;
                string smallError = PortraitCompositionError(compositionFocus);
                compositionFocus.Height = .1801d;
                string largeError = PortraitCompositionError(compositionFocus);
                compositionFocus.Height = .12d;
                compositionFocus.CandidateCount = 2;
                bool multipleRejected = PortraitCompositionError(compositionFocus).Contains("detected 2 faces");
                compositionFocus.CandidateCount = 0;
                bool noneRejected = PortraitCompositionError(compositionFocus).Contains("detected 0 faces");
                compositionFocus.CandidateCount = 1;
                compositionFocus.X = .8d;
                bool offCenterRejected = PortraitCompositionError(compositionFocus).Contains("face center");
                compositionFocus.X = .45d;
                compositionFocus.Y = .3d;
                bool lowHeadRejected = PortraitCompositionError(compositionFocus).Contains("face top");
                compositionFocus.Y = .1d;
                compositionFocus.Height = double.NaN;
                AddPortraitDerivativeTest(rows, "composition_boundaries_and_specific_errors", lowerBoundary
                    && smallError.Contains("7.99%") && largeError.Contains("18.01%")
                    && multipleRejected && noneRejected && offCenterRejected && lowHeadRejected
                    && PortraitCompositionError(compositionFocus) != null && PortraitCompositionError(null) != null,
                    "Small/large/multiple/missing/non-finite/off-center/low faces remain rejected with measured failure details.");
                bool invalidMetadataRejected = true;
                foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                {
                    compositionFocus.Height = .12d; compositionFocus.X = .45d; compositionFocus.Width = .1d; compositionFocus.Y = .1d;
                    compositionFocus.Confidence = invalid;
                    invalidMetadataRejected &= PortraitCompositionError(compositionFocus) != null;
                    compositionFocus.Confidence = .99d; compositionFocus.X = invalid;
                    invalidMetadataRejected &= PortraitCompositionError(compositionFocus) != null;
                    compositionFocus.X = .45d; compositionFocus.Width = invalid;
                    invalidMetadataRejected &= PortraitCompositionError(compositionFocus) != null;
                    compositionFocus.Width = .1d; compositionFocus.Y = invalid;
                    invalidMetadataRejected &= PortraitCompositionError(compositionFocus) != null;
                }
                AddPortraitDerivativeTest(rows, "composition_nonfinite_metadata_rejected", invalidMetadataRejected,
                    "Both consumers reject non-finite confidence and coordinates through the shared composition gate.");
                Func<byte[]> native = () => { nativeCalls++; return sourcePng; };
                byte[] generatedWithoutCapture = ResolveImageRequestSource("portrait", null, native);
                byte[] ignoredCapture = ResolveImageRequestSource("portrait", new byte[] { 1, 2 }, native);
                byte[] scenerySource = new byte[] { 3, 4 };
                bool sourceRouting = ReferenceEquals(generatedWithoutCapture, sourcePng)
                    && ReferenceEquals(ignoredCapture, sourcePng)
                    && ReferenceEquals(ResolveImageRequestSource("scenery", scenerySource, native), scenerySource)
                    && nativeCalls == 2;
                bool missingSceneRejected = false, nativeFailurePropagated = false;
                try { ResolveImageRequestSource("scenery", null, native); } catch (InvalidDataException) { missingSceneRejected = true; }
                try { ResolveImageRequestSource("portrait", sourcePng, () => throw new InvalidDataException("render failure")); }
                catch (InvalidDataException) { nativeFailurePropagated = true; }
                AddPortraitDerivativeTest(rows, "background_source_routing", sourceRouting && missingSceneRejected && nativeFailurePropagated,
                    "Portraits need no game capture, always use the native resolver, propagate renderer failure, and scenery bypasses native rendering.");
            }
            catch (Exception ex)
            {
                AddPortraitDerivativeTest(rows, "unexpected_exception", false, ex.ToString());
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempRoot))
                    {
                        Directory.Delete(tempRoot, true);
                    }
                }
                catch
                {
                }
            }
            return rows;
        }

        private static bool ContainsStrongBlue(byte[] rgba)
        {
            if (rgba == null)
            {
                return false;
            }
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                if (rgba[i + 2] > 200 && rgba[i] < 50 && rgba[i + 1] < 50)
                {
                    return true;
                }
            }
            return false;
        }

        private static void CreatePortraitTestMaster(string folder, byte[] png)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "portrait.png"), png);
        }

        private static void AddPortraitDerivativeTest(List<Dictionary<string, object>> rows, string name, bool passed, string detail)
        {
            rows.Add(new Dictionary<string, object>
            {
                ["name"] = name,
                ["passed"] = passed,
                ["detail"] = detail ?? ""
            });
        }
    }
}
