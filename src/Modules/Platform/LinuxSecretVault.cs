using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class SettingsService
    {
        private const string ManagedSettingsInstallationRoot = "/var/www/html/ReignServer";
        private const string SettingsSecretVaultFileName = "settings-secrets.dpapi"; // Retain the historical name and format.
        private const UnixFileMode VaultPrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        private const uint LinuxFileTypeMask = 0xF000, LinuxDirectory = 0x4000, LinuxRegularFile = 0x8000, LinuxSymbolicLink = 0xA000;

        [DllImport("libc", EntryPoint = "geteuid")]
        private static extern uint SettingsVaultEffectiveUserId();

        private static string InstalledSettingsSecretVaultPath()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
                || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE")?.Trim() == "1"
                || !string.IsNullOrEmpty(Program.CampaignsRootOverride.Value)) return null;

            uint uid = SettingsVaultEffectiveUserId();
            return uid == 0 ? null : ManagedSettingsSecretVaultPath(ManagedSettingsInstallationRoot, Program.DataDir,
                AppContext.BaseDirectory, Environment.ProcessPath, uid);
        }

        private static string ManagedSettingsSecretVaultPath(string installRoot, string dataRoot, string baseDirectory,
            string executablePath, uint uid)
        {
            if (!Path.IsPathFullyQualified(dataRoot) || !Path.IsPathFullyQualified(baseDirectory)
                || string.IsNullOrEmpty(executablePath) || !Path.IsPathFullyQualified(executablePath)) return null;

            string runtime = Path.Combine(installRoot, "runtime");
            string current = Path.Combine(runtime, "current");
            string data = Path.Combine(installRoot, "data");
            string executable = Path.GetFileName(executablePath);
            if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)) != data
                || (executable != "ReignServer" && executable != "ReignBetaServer")) return null;

            // The only permitted redirect is the controller's legacy current -> versions/<id> link.
            // Its parent and all other directory nodes must be real and protected from other users.
            for (string dir = runtime; dir != null; dir = Path.GetDirectoryName(dir))
            {
                if (!LinuxLstat(dir, out uint mode, out uint owner) || (mode & LinuxFileTypeMask) != LinuxDirectory
                    || (owner != 0 && owner != uid)
                    || ((mode & 0x12) != 0 && !(owner == 0 && (mode & 0x200) != 0
                        && dir != runtime && dir != installRoot))) return null;
            }

            if (!LinuxLstat(current, out uint currentMode, out uint currentOwner)
                || (currentOwner != 0 && currentOwner != uid)) return null;
            string active = current;
            if ((currentMode & LinuxFileTypeMask) == LinuxSymbolicLink)
            {
                string target = new DirectoryInfo(current).LinkTarget;
                if (string.IsNullOrEmpty(target)) return null;
                active = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target, runtime));
                string versions = Path.Combine(runtime, "versions");
                if (Path.GetDirectoryName(active) != versions
                    || !Regex.IsMatch(Path.GetFileName(active), @"\A[0-9]{14}-[0-9]+\z", RegexOptions.CultureInvariant)
                    || !TrustedVaultEntry(versions, LinuxDirectory, uid)) return null;
            }

            string app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
            string processDir = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if ((app != current && app != active) || (processDir != current && processDir != active)
                || !TrustedVaultEntry(active, LinuxDirectory, uid)
                || !TrustedVaultEntry(Path.Combine(active, executable), LinuxRegularFile, uid)
                || !LinuxLstat(data, out uint dataMode, out uint dataOwner)
                || dataOwner != uid || (dataMode & LinuxFileTypeMask) != LinuxDirectory
                || (dataMode & 0xFFF) != (uint)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
                return null;

            string vault = Path.Combine(data, SettingsSecretVaultFileName);
            return IsPrivateVaultFileOrMissing(vault, uid) && IsPrivateVaultFileOrMissing(vault + ".key", uid) ? vault : null;
        }

        private static bool TrustedVaultEntry(string path, uint type, uint uid) =>
            LinuxLstat(path, out uint mode, out uint owner) && (mode & LinuxFileTypeMask) == type
            && (owner == 0 || owner == uid) && (mode & 0x12) == 0;

        private static bool IsPrivateVaultFileOrMissing(string path, uint uid)
        {
            if (!LinuxLstat(path, out uint mode, out uint owner, out ulong links, out int error))
                return error == 2; // ENOENT. Fail closed on inaccessible paths and non-Linux filesystems.
            return owner == uid && (mode & LinuxFileTypeMask) == LinuxRegularFile && links == 1
                && (mode & 0xFFF & ~(uint)VaultPrivateFileMode) == 0;
        }

        // Linux has no DPAPI: authenticate vaults with a separate, owner-only local key.
        private static byte[] LinuxVaultTransform(string vaultPath, byte[] input, bool encrypt)
        {
            string keyPath = vaultPath + ".key";
            if (!File.Exists(keyPath))
            {
                if (!encrypt) throw new InvalidDataException("Reign secret key is missing. Restore the key with its vault or re-enter credentials.");
                using (var file = new FileStream(keyPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    UnixCreateMode = VaultPrivateFileMode
                })) file.Write(RandomNumberGenerator.GetBytes(32));
            }
            if (new FileInfo(keyPath).LinkTarget != null || (File.GetUnixFileMode(keyPath) & ~VaultPrivateFileMode) != 0)
                throw new InvalidDataException("Reign secret key must be a private regular file.");
            byte[] key = File.ReadAllBytes(keyPath);
            if (key.Length != 32) throw new InvalidDataException("Invalid Reign secret key.");
            byte[] header = Encoding.ASCII.GetBytes("REIGN-LINUX-1");
            try
            {
                using (var aes = new AesGcm(key, 16))
                {
                    if (encrypt)
                    {
                        byte[] output = new byte[header.Length + 12 + 16 + input.Length];
                        header.CopyTo(output, 0);
                        var nonce = output.AsSpan(header.Length, 12);
                        RandomNumberGenerator.Fill(nonce);
                        aes.Encrypt(nonce, input, output.AsSpan(header.Length + 28),
                            output.AsSpan(header.Length + 12, 16), SettingsSecretVaultEntropy);
                        return output;
                    }
                    if (input.Length < header.Length + 28 || !input.AsSpan(0, header.Length).SequenceEqual(header))
                        throw new InvalidDataException("Windows credentials must be migrated or re-entered; this is not a Linux vault.");
                    byte[] clear = new byte[input.Length - header.Length - 28];
                    aes.Decrypt(input.AsSpan(header.Length, 12), input.AsSpan(header.Length + 28),
                        input.AsSpan(header.Length + 12, 16), clear, SettingsSecretVaultEntropy);
                    return clear;
                }
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }
}
