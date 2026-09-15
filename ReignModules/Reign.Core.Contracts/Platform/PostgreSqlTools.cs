using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Reign.Core.Contracts.Platform
{
    public static class PostgreSqlTools
    {
        public static ProcessStartInfo CreateCommand(string nativeBin, string wslDistro,
            IList<string> arguments, string password, string workingDirectory)
        {
            if (arguments == null || arguments.Count == 0
                || arguments[0] != "pg_dump" && arguments[0] != "pg_restore")
                throw new ArgumentException("Only pg_dump and pg_restore are supported.", nameof(arguments));
            var values = arguments.ToList();
            string executable;
            bool linux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            bool native = linux || !string.IsNullOrWhiteSpace(nativeBin);
            if (linux)
            {
                executable = Path.Combine("/usr/bin", values[0]);
                values.RemoveAt(0);
            }
            else if (native)
            {
                if (!Path.IsPathRooted(nativeBin)) throw new ArgumentException("PostgreSQL bin must be absolute.", nameof(nativeBin));
                executable = Path.Combine(nativeBin, values[0] + ".exe");
                values.RemoveAt(0);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(wslDistro)) throw new ArgumentException("An explicit legacy WSL distribution is required.", nameof(wslDistro));
                executable = "wsl.exe";
                values.InsertRange(0, new[] { "-d", wslDistro, "--exec" });
            }
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = string.Join(" ", values.Select(QuoteWindowsArgument)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };
            // Do not place credentials in process arguments, command logs or URLs.
            start.EnvironmentVariables["PGPASSWORD"] = password ?? string.Empty;
            if (!native)
            {
                string existing = start.EnvironmentVariables.ContainsKey("WSLENV")
                    ? start.EnvironmentVariables["WSLENV"] ?? string.Empty : string.Empty;
                start.EnvironmentVariables["WSLENV"] = string.Join(":", existing.Split(':')
                    .Where(v => v.Length > 0 && !v.Split('/')[0].Equals("PGPASSWORD", StringComparison.Ordinal))
                    .Concat(new[] { "PGPASSWORD/u" }));
            }
            return start;
        }

        public static string ArchivePath(string path, bool native)
        {
            string full = Path.GetFullPath(path ?? string.Empty);
            if (native || RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return full;
            if (full.Length < 3 || full[1] != ':' || full[2] != '\\' && full[2] != '/')
                throw new InvalidDataException("Legacy WSL archives require an absolute Windows drive path.");
            return "/mnt/" + char.ToLowerInvariant(full[0]) + "/" + full.Substring(3).Replace('\\', '/');
        }

        // Windows CRT quoting, including embedded quotes and trailing backslashes.
        public static string QuoteWindowsArgument(string value)
        {
            value = value ?? string.Empty;
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else result.Append('\\', slashes).Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
