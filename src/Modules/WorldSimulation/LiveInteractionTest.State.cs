using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        private static string NormalizeLiveTestMode(string value) { return (value ?? "").Trim().ToLowerInvariant().Replace('-', '_'); }
        private static string NormalizeLiveTestPresentation(string value) { return string.Equals(value, "visible", StringComparison.OrdinalIgnoreCase) ? "visible" : "headless"; }
        private static string NormalizeLiveTestEffects(string value) { return string.Equals(value, "full", StringComparison.OrdinalIgnoreCase) ? "full" : "guarded"; }
        private static Dictionary<string, object> LiveTestError(string error) { return new Dictionary<string, object> { ["ok"] = false, ["error"] = error ?? "Live interaction request failed." }; }

        private static bool IsFatalLiveTestInfrastructureFailure(string error)
        {
            string normalized = (error ?? "").ToLowerInvariant();
            return normalized.Contains("insufficient_balance")
                || normalized.Contains("insufficient balance")
                || normalized.Contains("account quota is exhausted")
                || normalized.Contains("quota exhausted")
                || normalized.Contains("billing hard limit")
                || normalized.Contains("payment required")
                || normalized.Contains("status 402")
                || normalized.Contains("http 402")
                || normalized.Contains("status 401")
                || normalized.Contains("http 401")
                || normalized.Contains("unauthorized api key")
                || normalized.Contains("invalid api key");
        }

        internal static bool IsLiveTestArmed(Dictionary<string, object> arm) { return ParseLiveTestUtc(ReadString(arm, "expiresUtc", "")) > DateTimeOffset.UtcNow; }
        private static bool ShouldInterruptLiveTestRunForInstance(Dictionary<string, object> run, string gameInstanceId)
        {
            if (run == null || string.IsNullOrWhiteSpace(gameInstanceId)
                || string.Equals(ReadString(run, "status", ""), "paused", StringComparison.OrdinalIgnoreCase))
                return false;
            string activeInstance = ReadString(run, "gameInstanceId", "");
            return !string.IsNullOrWhiteSpace(activeInstance)
                && !string.Equals(activeInstance, gameInstanceId, StringComparison.OrdinalIgnoreCase);
        }
        internal static bool IsFreshLiveTestRuntime(Dictionary<string, object> runtime) { return runtime != null && runtime.Count > 0 && ParseLiveTestUtc(ReadString(runtime, "receivedUtc", "")) >= DateTimeOffset.UtcNow.AddSeconds(-LiveTestHeartbeatFreshSeconds); }
        internal static DateTimeOffset ParseLiveTestUtc(string value) { return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) ? parsed.ToUniversalTime() : DateTimeOffset.MinValue; }
        private static readonly AsyncLocal<string> LiveTestFixtureRoot = new AsyncLocal<string>();
        private static string LiveTestRoot() => LiveTestFixtureRoot.Value ?? Path.Combine(Program.TestsDir, "live-interaction");
        internal static string LiveTestArmPath() { return Path.Combine(LiveTestRoot(), "arm.json"); }
        private static string LiveTestLatestRuntimePath() { return Path.Combine(LiveTestRoot(), "runtime-latest.json"); }
        private static string LiveTestRuntimePath(string campaignId) { return Path.Combine(LiveTestRoot(), "runtime", CampaignFileService.SafePathSegment(campaignId, "unknown") + ".json"); }
        internal static string LiveTestRunsRoot(string campaignId) { return CampaignFileService.CampaignFile(campaignId, "tests", "live-interaction", "runs"); }
        private static string LiveTestActiveRunPath(string campaignId) { return CampaignFileService.CampaignFile(campaignId, "tests", "live-interaction", "active-run.json"); }
        private static string LiveTestRunPath(string campaignId, string runId) { return Path.Combine(LiveTestRunsRoot(campaignId), CampaignFileService.SafePathSegment(runId, "run") + ".json"); }
        internal static Dictionary<string, object> LoadLiveTestRuntime(string campaignId) { return string.IsNullOrWhiteSpace(campaignId) ? CampaignFileService.ReadJsonObject(LiveTestLatestRuntimePath()) : CampaignFileService.ReadJsonObject(LiveTestRuntimePath(campaignId)); }
        internal static Dictionary<string, object> LoadLiveTestRun(string campaignId, string runId) { return string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(runId) ? new Dictionary<string, object>() : CampaignFileService.ReadJsonObject(LiveTestRunPath(campaignId, runId)); }
        internal static void SaveLiveTestRun(Dictionary<string, object> run)
        {
            lock (LiveTestLock) SaveLiveTestRunLocked(run);
        }

        private static void SaveLiveTestRunLocked(Dictionary<string, object> run)
        {
            string campaignId = ReadString(run, "campaignId", "");
            string runId = ReadString(run, "runId", "");
            CampaignFileService.WriteJsonObject(LiveTestRunPath(campaignId, runId), run);
            string indexPath = LiveTestActiveRunPath(campaignId);
            if (!LiveTestTerminalRunStates.Contains(ReadString(run, "status", "")))
            {
                CampaignFileService.WriteJsonObject(indexPath, new Dictionary<string, object>
                {
                    ["runId"] = runId,
                    ["status"] = ReadString(run, "status", ""),
                    ["updatedUtc"] = ReadString(run, "updatedUtc", "")
                });
                return;
            }
            Dictionary<string, object> indexed = CampaignFileService.ReadJsonObject(indexPath);
            if (ReadString(indexed, "runId", "").Equals(
                    runId, StringComparison.OrdinalIgnoreCase))
            {
                WriteIdleLiveTestRunIndex(indexPath);
            }
        }

        private static Dictionary<string, object> FindLatestLiveTestRun(string campaignId)
        {
            string root = LiveTestRunsRoot(campaignId);
            if (!Directory.Exists(root)) return null;
            string path = Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            return string.IsNullOrWhiteSpace(path) ? null : CampaignFileService.ReadJsonObject(path);
        }

        private static Dictionary<string, object> FindActiveLiveTestRun(string campaignId)
        {
            lock (LiveTestLock) return FindActiveLiveTestRunLocked(campaignId);
        }

        private static Dictionary<string, object> FindActiveLiveTestRunLocked(string campaignId)
        {
            string indexPath = LiveTestActiveRunPath(campaignId);
            Dictionary<string, object> indexed = CampaignFileService.ReadJsonObject(indexPath);
            string indexedRunId = ReadString(indexed, "runId", "");
            if (string.IsNullOrWhiteSpace(indexedRunId)
                && ReadBool(indexed, "scanComplete", false))
                return null;
            if (!string.IsNullOrWhiteSpace(indexedRunId))
            {
                Dictionary<string, object> indexedRun =
                    LoadLiveTestRun(campaignId, indexedRunId);
                if (indexedRun.Count > 0
                    && !LiveTestTerminalRunStates.Contains(
                        ReadString(indexedRun, "status", "")))
                    return indexedRun;
                WriteIdleLiveTestRunIndex(indexPath);
                return null;
            }

            // One backward-compatibility scan upgrades campaigns created before
            // the active-run index existed. Normal heartbeats never deserialize
            // the historical report corpus.
            string root = LiveTestRunsRoot(campaignId);
            if (!Directory.Exists(root))
            {
                WriteIdleLiveTestRunIndex(indexPath);
                return null;
            }
            foreach (string path in Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc))
            {
                Dictionary<string, object> run = CampaignFileService.ReadJsonObject(path);
                if (!LiveTestTerminalRunStates.Contains(ReadString(run, "status", "")))
                {
                    CampaignFileService.WriteJsonObject(indexPath, new Dictionary<string, object>
                    {
                        ["runId"] = ReadString(run, "runId", ""),
                        ["status"] = ReadString(run, "status", ""),
                        ["updatedUtc"] = ReadString(run, "updatedUtc", "")
                    });
                    return run;
                }
            }
            WriteIdleLiveTestRunIndex(indexPath);
            return null;
        }

        private static void WriteIdleLiveTestRunIndex(string indexPath)
        {
            CampaignFileService.WriteJsonObject(indexPath, new Dictionary<string, object>
            {
                ["runId"] = string.Empty,
                ["status"] = "idle",
                ["scanComplete"] = true,
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o")
            });
        }
    }
}
