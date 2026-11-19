using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class SettingsService
    {
        private static readonly object SettingsSecretVaultLock = new object();
        private static readonly byte[] SettingsSecretVaultEntropy =
            Encoding.UTF8.GetBytes("BannerlordReign.SettingsSecrets.v1");

        private static string SettingsSecretVaultPath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Bannerlord Reign", "ReignBetaServer", "settings-secrets.dpapi");
        }

        private static bool ShouldUseInstalledSettingsSecretVault()
        {
            return IsSettingsSecretVaultDeploymentPath(
                AppDomain.CurrentDomain.BaseDirectory ?? "");
        }

        private static bool IsSettingsSecretVaultDeploymentPath(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return false;
            }

            string normalized = Path.GetFullPath(baseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalized.EndsWith(
                Path.Combine("ReignBeta", "server", "app"),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool RecoverInstalledSettingsSecrets(
            Dictionary<string, object> settings,
            bool restoreFullSettingsState)
        {
            if (!ShouldUseInstalledSettingsSecretVault())
            {
                return false;
            }

            try
            {
                int restoredSettingCount;
                List<string> recoveredKeys = RecoverSettingsSecretsFromVault(
                    SettingsSecretVaultPath(),
                    settings,
                    restoreFullSettingsState,
                    out restoredSettingCount);
                if (restoredSettingCount > 0)
                {
                    OperationsService.LogOperational("settings.secrets_recovered", new Dictionary<string, object>
                    {
                        ["keyCount"] = recoveredKeys.Count,
                        ["keys"] = recoveredKeys,
                        ["restoredSettingCount"] = restoredSettingCount,
                        ["fullSettingsStateRestored"] = restoreFullSettingsState
                    });
                    return true;
                }
            }
            catch (Exception ex)
            {
                OperationsService.LogOperational("settings.secrets_recovery_failed", new Dictionary<string, object>
                {
                    ["errorType"] = ex.GetType().Name,
                    ["error"] = ex.Message
                });
            }
            return false;
        }

        private static void PersistInstalledSettingsSecrets(
            Dictionary<string, object> settings,
            bool allowEmptySecretOverwrite = false)
        {
            if (!ShouldUseInstalledSettingsSecretVault())
            {
                return;
            }

            try
            {
                string vaultPath = SettingsSecretVaultPath();
                if (!CanSafelyPersistSettingsSecretVault(
                    vaultPath,
                    settings,
                    allowEmptySecretOverwrite))
                {
                    OperationsService.LogOperational("settings.secrets_persist_skipped_empty", new Dictionary<string, object>
                    {
                        ["vaultExists"] = File.Exists(vaultPath),
                        ["explicitClear"] = allowEmptySecretOverwrite
                    });
                    return;
                }

                WriteSettingsSecretsVault(vaultPath, settings);
            }
            catch (Exception ex)
            {
                OperationsService.LogOperational("settings.secrets_persist_failed", new Dictionary<string, object>
                {
                    ["errorType"] = ex.GetType().Name,
                    ["error"] = ex.Message
                });
            }
        }

        private static bool CanSafelyPersistSettingsSecretVault(
            string vaultPath,
            Dictionary<string, object> settings,
            bool allowEmptySecretOverwrite)
        {
            if (allowEmptySecretOverwrite || HasSensitiveSettingValue(settings))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(vaultPath) || !File.Exists(vaultPath))
            {
                return false;
            }

            try
            {
                int restoredSettingCount;
                Dictionary<string, object> protectedSettings = new Dictionary<string, object>(
                    StringComparer.OrdinalIgnoreCase);
                RecoverSettingsSecretsFromVault(
                    vaultPath,
                    protectedSettings,
                    true,
                    out restoredSettingCount);
                return !HasSensitiveSettingValue(protectedSettings);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasSensitiveSettingValue(Dictionary<string, object> settings)
        {
            if (settings == null)
            {
                return false;
            }

            return SensitiveSettingsKeys().Any(
                key => !string.IsNullOrWhiteSpace(ReadString(settings, key, "")));
        }

        private static void WriteSettingsSecretsVault(string vaultPath, Dictionary<string, object> settings)
        {
            if (string.IsNullOrWhiteSpace(vaultPath))
            {
                throw new ArgumentException("A secret-vault path is required.", nameof(vaultPath));
            }

            lock (SettingsSecretVaultLock)
            {
                var vaultSettings = new Dictionary<string, object>(
                    settings ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
                SanitizeBoundKeySlots(vaultSettings);
                Dictionary<string, object> protectedState = new Dictionary<string, object>
                {
                    ["formatVersion"] = 2,
                    ["settings"] = vaultSettings
                };

                string directory = Path.GetDirectoryName(vaultPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                byte[] clearText = Encoding.UTF8.GetBytes(Json.Serialize(protectedState));
                byte[] protectedBytes = LinuxVaultTransform(vaultPath, clearText, true);
                string temporaryPath = vaultPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temporaryPath, protectedBytes);
                    if (File.Exists(vaultPath))
                    {
                        File.Replace(temporaryPath, vaultPath, null, true);
                    }
                    else
                    {
                        File.Move(temporaryPath, vaultPath);
                    }
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }
        }

        private static List<string> RecoverSettingsSecretsFromVault(
            string vaultPath,
            Dictionary<string, object> settings,
            bool restoreFullSettingsState,
            out int restoredSettingCount)
        {
            List<string> recovered = new List<string>();
            restoredSettingCount = 0;
            if (settings == null || string.IsNullOrWhiteSpace(vaultPath) || !File.Exists(vaultPath))
            {
                return recovered;
            }

            lock (SettingsSecretVaultLock)
            {
                byte[] protectedBytes = File.ReadAllBytes(vaultPath);
                byte[] clearText = LinuxVaultTransform(vaultPath, protectedBytes, false);
                Dictionary<string, object> protectedState =
                    Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(clearText))
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> savedSettings =
                    ReadDictionary(protectedState, "settings") ?? protectedState;

                if (restoreFullSettingsState)
                {
                    foreach (KeyValuePair<string, object> pair in savedSettings)
                    {
                        // A slot bound to a global key never takes a value back from an older vault, except a Reign-only key
                        // the vault recorded beside that same binding.
                        if (GlobalKeyBinding(settings, pair.Key) != null && !VaultHoldsReignOnlyKey(savedSettings, settings, pair.Key)) continue;
                        settings[pair.Key] = pair.Value;
                        restoredSettingCount++;
                        if (SensitiveSettingsKeys().Contains(pair.Key)
                            && !string.IsNullOrWhiteSpace(Convert.ToString(pair.Value)))
                        {
                            recovered.Add(pair.Key);
                        }
                    }
                    return recovered.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }

                foreach (string key in SensitiveSettingsKeys())
                {
                    if (!string.IsNullOrWhiteSpace(ReadString(settings, key, "")))
                    {
                        continue;
                    }

                    if (GlobalKeyBinding(settings, key) != null && !VaultHoldsReignOnlyKey(savedSettings, settings, key))
                    {
                        continue;
                    }

                    string recoveredValue = ReadString(savedSettings, key, "");
                    if (string.IsNullOrWhiteSpace(recoveredValue))
                    {
                        continue;
                    }

                    settings[key] = recoveredValue;
                    recovered.Add(key);
                    restoredSettingCount++;
                }
            }
            return recovered;
        }

        // True when the vault's value for a bound slot is a Reign-only key: the vault was written after the one-time shadow
        // cleanup (it carries the marker) and recorded the same binding for that slot. A value saved before the slot was
        // bound, or by an earlier version, is never restored into it.
        private static bool VaultHoldsReignOnlyKey(Dictionary<string, object> savedSettings, Dictionary<string, object> settings, string slot)
        {
            if (!ReadBool(ReadDictionary(savedSettings, "globalKeyMigrations"), BoundShadowCleanupMarker, false))
            {
                return false;
            }

            string vaultKeyId = BoundKeyId(savedSettings, slot);
            return vaultKeyId.Length > 0 && vaultKeyId == BoundKeyId(settings, slot);
        }

        internal static Dictionary<string, object> RunSettingsSecretVaultSelfTest()
        {
            string root = Path.Combine(Path.GetTempPath(), "reign-settings-vault-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "settings-secrets.dpapi");
            try
            {
                Dictionary<string, object> original = new Dictionary<string, object>
                {
                    ["apiKey"] = "diagnostic-main-secret",
                    ["portraitNanoGptApiKey"] = "diagnostic-image-secret",
                    ["portraitAtlasApiKey"] = "",
                    ["qdrantApiKey"] = "",
                    ["dialogueModel"] = "diagnostic-model"
                };
                WriteSettingsSecretsVault(path, original);

                Dictionary<string, object> restored = new Dictionary<string, object>
                {
                    ["apiKey"] = "",
                    ["portraitNanoGptApiKey"] = "",
                    ["portraitAtlasApiKey"] = "",
                    ["qdrantApiKey"] = "",
                    ["dialogueModel"] = "default-model"
                };
                int restoredSettingCount;
                List<string> recovered = RecoverSettingsSecretsFromVault(
                    path, restored, true, out restoredSettingCount);
                bool roundTrip = recovered.Count == 2
                    && ReadString(restored, "apiKey", "") == "diagnostic-main-secret"
                    && ReadString(restored, "portraitNanoGptApiKey", "") == "diagnostic-image-secret"
                    && ReadString(restored, "dialogueModel", "") == "diagnostic-model";

                Dictionary<string, object> accidentalEmptySnapshot =
                    new Dictionary<string, object>(restored, StringComparer.OrdinalIgnoreCase)
                    {
                        ["apiKey"] = "",
                        ["portraitNanoGptApiKey"] = ""
                    };
                bool emptyOverwriteBlocked = !CanSafelyPersistSettingsSecretVault(
                    path,
                    accidentalEmptySnapshot,
                    false);
                if (!emptyOverwriteBlocked)
                {
                    WriteSettingsSecretsVault(path, accidentalEmptySnapshot);
                }

                Dictionary<string, object> afterBlockedOverwrite = new Dictionary<string, object>
                {
                    ["apiKey"] = "",
                    ["portraitNanoGptApiKey"] = ""
                };
                int afterBlockedSettingCount;
                List<string> afterBlockedRecovered = RecoverSettingsSecretsFromVault(
                    path, afterBlockedOverwrite, true, out afterBlockedSettingCount);
                bool protectedFromAccidentalClear = afterBlockedRecovered.Count == 2
                    && ReadString(afterBlockedOverwrite, "apiKey", "") == "diagnostic-main-secret"
                    && ReadString(afterBlockedOverwrite, "portraitNanoGptApiKey", "") == "diagnostic-image-secret";

                bool explicitClearAllowed = CanSafelyPersistSettingsSecretVault(
                    path,
                    accidentalEmptySnapshot,
                    true);
                WriteSettingsSecretsVault(path, accidentalEmptySnapshot);
                Dictionary<string, object> afterClear = new Dictionary<string, object>
                {
                    ["apiKey"] = "",
                    ["portraitNanoGptApiKey"] = "",
                    ["dialogueModel"] = "default-model"
                };
                int afterClearSettingCount;
                List<string> afterClearRecovered = RecoverSettingsSecretsFromVault(
                    path, afterClear, true, out afterClearSettingCount);
                bool cleared = afterClearRecovered.Count == 0
                    && string.IsNullOrWhiteSpace(ReadString(afterClear, "apiKey", ""))
                    && string.IsNullOrWhiteSpace(ReadString(afterClear, "portraitNanoGptApiKey", ""))
                    && ReadString(afterClear, "dialogueModel", "") == "diagnostic-model";
                bool deploymentPathsRecognized =
                    IsSettingsSecretVaultDeploymentPath(Path.Combine(
                        "C:\\", "workspace", "ReignBeta", "server", "app"))
                    && IsSettingsSecretVaultDeploymentPath(Path.Combine(
                        "D:\\", "Games", "Modules", "ReignBeta", "server", "app"))
                    && !IsSettingsSecretVaultDeploymentPath(Path.Combine(
                        "C:\\", "workspace", ".codex-build", "server", "out"));

                return new Dictionary<string, object>
                {
                    ["passed"] = roundTrip
                        && emptyOverwriteBlocked
                        && protectedFromAccidentalClear
                        && explicitClearAllowed
                        && cleared
                        && deploymentPathsRecognized,
                    ["roundTrip"] = roundTrip,
                    ["emptyOverwriteBlocked"] = emptyOverwriteBlocked,
                    ["protectedFromAccidentalClear"] = protectedFromAccidentalClear,
                    ["explicitClearAllowed"] = explicitClearAllowed,
                    ["clearPersistsWithoutSecretResurrection"] = cleared,
                    ["deploymentPathsRecognized"] = deploymentPathsRecognized,
                    ["recoveredKeyCount"] = recovered.Count,
                    ["restoredSettingCount"] = restoredSettingCount
                };
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }
    }
}
