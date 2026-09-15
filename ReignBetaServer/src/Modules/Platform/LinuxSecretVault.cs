#if REIGN_LINUX
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Linux has no DPAPI: authenticate vaults with a separate, owner-only local key.
        private static byte[] LinuxVaultTransform(string vaultPath, byte[] input, bool encrypt)
        {
            string keyPath = vaultPath + ".key";
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (!File.Exists(keyPath))
            {
                if (!encrypt) throw new InvalidDataException("Reign secret key is missing. Restore the key with its vault or re-enter credentials.");
                using (var file = new FileStream(keyPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    UnixCreateMode = ownerOnly
                })) file.Write(RandomNumberGenerator.GetBytes(32));
            }
            if (new FileInfo(keyPath).LinkTarget != null || (File.GetUnixFileMode(keyPath) & ~ownerOnly) != 0)
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
#endif
