#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReignBetaServer
{
    // This is an operator test tripwire, never a simulated provider or a normal game setting.
    internal sealed class NativeProviderTestGuard
    {
        internal const string Schema = "reign-native-provider-free-guard-v1";
        internal const string RelativeDirectory = "tests/war-economy";
        private readonly string _directory;
        private readonly string _dataRoot;
        private readonly object _gate = new object();
        private readonly string _instance = Guid.NewGuid().ToString("N");
        private readonly string _startedUtc = DateTime.UtcNow.ToString("O");
        private Dictionary<string, string>? _manifest;
        private string _manifestHash = "", _error = "";
        private long _allowed, _blocked;
        private bool _latched;

        internal NativeProviderTestGuard(string dataRoot)
        {
            _dataRoot = Path.GetFullPath(dataRoot);
            _directory = Path.Combine(_dataRoot, "tests", "war-economy");
        }

        private void ObserveMarker()
        {
            if (_latched) return; // Removal cannot re-enable providers in the same server process.
            if (!Directory.Exists(_dataRoot)) { _latched = true; _error = "Runtime data root unavailable."; return; }
            string path = Path.Combine(_directory, "provider-free.json");
            try
            {
                // Unlike File.Exists, permission/read failures must never mean "guard absent".
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException("Provider test guard must be a regular file.");
                _latched = true;
                var info = new FileInfo(path);
                if (info.Length > 16384) throw new InvalidDataException("Provider test guard exceeds its limit.");
                byte[] bytes = File.ReadAllBytes(path);
                _manifestHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes)
                    ?? throw new InvalidDataException("Provider test guard is empty.");
                foreach (string key in new[] { "schema", "runId", "campaignId", "timelineId", "taskId", "currentSaveName", "baselineSaveName" })
                    if (!manifest.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value) || value.Length > 512)
                        throw new InvalidDataException("Provider test guard is missing " + key + ".");
                if (manifest["schema"] != Schema || !Guid.TryParse(manifest["taskId"], out _))
                    throw new InvalidDataException("Provider test guard identity is invalid.");
                _manifest = manifest;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex)
            {
                _latched = true;
                _error = ex.GetType().Name + ": provider test guard could not be verified.";
            }
        }

        internal void BeforeDispatch(string category)
        {
            lock (_gate)
            {
                ObserveMarker();
                if (!_latched) { _allowed++; return; }
                _blocked++;
                try
                {
                    Directory.CreateDirectory(_directory);
                    string evidence = Path.Combine(_directory, "provider-free-attempts-" + _instance + ".json");
                    string temporary = evidence + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(SnapshotLocked(category)));
                    File.Move(temporary, evidence, true);
                }
                catch (Exception ex) { _error = ex.GetType().Name + ": blocked-provider evidence could not be persisted."; }
                throw new NativeProviderTestBlockedException("Provider-free native test guard blocked " + category
                    + ". This is a failed test precondition, not a provider response. Owner run: "
                    + (_manifest?.GetValueOrDefault("runId") ?? "unverified") + ".");
            }
        }

        internal Dictionary<string, object?> Snapshot()
        {
            lock (_gate) { ObserveMarker(); return SnapshotLocked(""); }
        }
        private Dictionary<string, object?> SnapshotLocked(string category) => new Dictionary<string, object?>
        {
            ["schema"] = Schema, ["armed"] = _latched, ["owner"] = _manifest,
            ["manifestSha256"] = _manifestHash, ["serverInstance"] = _instance, ["processStartedUtc"] = _startedUtc,
            ["observedUtc"] = DateTime.UtcNow.ToString("O"), ["allowedDispatchesSinceProcessStart"] = _allowed,
            ["blockedAttemptsSinceProcessStart"] = _blocked, ["lastCategory"] = category, ["error"] = _error,
            ["zeroDispatchEvidence"] = _latched && _manifest != null && _allowed == 0 && _error.Length == 0,
            ["cleanProviderFreeRun"] = _latched && _manifest != null && _allowed == 0 && _blocked == 0 && _error.Length == 0,
            ["scope"] = "Generation, streaming, subscription generation, reference uploads and image retrieval; local runtime/vector traffic is unaffected."
        };
    }

    internal sealed class NativeProviderTestBlockedException : InvalidOperationException
    {
        internal NativeProviderTestBlockedException(string message) : base(message) { }
    }
}
