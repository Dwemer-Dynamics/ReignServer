using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ReignVerification
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            args = args ?? new string[0];
            if (args.Any(x => x == "--help" || x == "-h" || x == "/?"))
            {
                PrintHelp();
                return 0;
            }

            string server = FindServerExecutable();
            if (string.IsNullOrWhiteSpace(server))
            {
                Console.Error.WriteLine("ReignBetaServer.exe could not be found. Set REIGN_SERVER_EXE or build ReignBetaServer first.");
                return 2;
            }

            List<string> forwarded = new List<string> { "--run-verification" };
            forwarded.AddRange(args);
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = server,
                Arguments = string.Join(" ", forwarded.Select(Quote)),
                WorkingDirectory = Path.GetDirectoryName(server) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false
            };
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (!string.IsNullOrWhiteSpace(output)) Console.WriteLine(output.TrimEnd());
                if (!string.IsNullOrWhiteSpace(error)) Console.Error.WriteLine(error.TrimEnd());
                return process.ExitCode;
            }
        }

        private static string FindServerExecutable()
        {
            string configured = Environment.GetEnvironmentVariable("REIGN_SERVER_EXE") ?? "";
            if (File.Exists(configured)) return Path.GetFullPath(configured);
            DirectoryInfo cursor = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; cursor != null && i < 8; i++, cursor = cursor.Parent)
            {
                string[] candidates =
                {
                    Path.Combine(cursor.FullName, "ReignBeta", "server", "app", "ReignBetaServer.exe"),
                    Path.Combine(cursor.FullName, "ReignBetaServer", "bin", "Release", "ReignBetaServer.exe"),
                    Path.Combine(cursor.FullName, "ReignBetaServer.exe")
                };
                string match = candidates.FirstOrDefault(File.Exists);
                if (!string.IsNullOrWhiteSpace(match)) return match;
            }
            return "";
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return value.Any(char.IsWhiteSpace) || value.Contains("\"") ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Bannerlord Reign Offline-First Verification Lab");
            Console.WriteLine("Usage: ReignVerification.exe [options]");
            Console.WriteLine("  --tier quick|offline|live-llm|game|all");
            Console.WriteLine("         commit|full-regression|release-candidate");
            Console.WriteLine("         exhaustive-final-review|human-immersion");
            Console.WriteLine("  --suite <name>  --seed <number>  --repeat <count>");
            Console.WriteLine("  --fail-fast  --json  --cancel");
            Console.WriteLine("  --live-case-cap <count>  --game-timeout-seconds <seconds>");
            Console.WriteLine();
            Console.WriteLine("Real campaign folders are read-only. All mutable verification state is written under server/app/data/tests/verification.");
        }
    }
}
