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

            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("Server verification runs on Linux or inside WSL.");
            if (Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") != "1"
                || Environment.GetEnvironmentVariable("REIGN_DB_NAME") != "ReignValidation"
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REIGN_DATA_ROOT")))
                throw new InvalidOperationException("Use canonical validation with isolated ReignValidation and run-owned data.");
            string server = FindServerExecutable();
            if (string.IsNullOrWhiteSpace(server))
            {
                Console.Error.WriteLine("ReignBetaServer.dll could not be found. Set REIGN_SERVER_DLL or build ReignBetaServer first.");
                return 2;
            }

            List<string> forwarded = new List<string> { "--run-verification" };
            forwarded.AddRange(args);
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = "dotnet",

                WorkingDirectory = Path.GetDirectoryName(server) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false
            };
            start.ArgumentList.Add(server);
            foreach (string argument in forwarded) start.ArgumentList.Add(argument);
            using (Process process = Process.Start(start))
            {
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                string output = outputTask.GetAwaiter().GetResult();
                string error = errorTask.GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(output)) Console.WriteLine(output.TrimEnd());
                if (!string.IsNullOrWhiteSpace(error)) Console.Error.WriteLine(error.TrimEnd());
                return process.ExitCode;
            }
        }

        private static string FindServerExecutable()
        {
            string configured = Environment.GetEnvironmentVariable("REIGN_SERVER_DLL") ?? "";
            if (File.Exists(configured)) return Path.GetFullPath(configured);
            DirectoryInfo cursor = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; cursor != null && i < 8; i++, cursor = cursor.Parent)
            {
                string[] candidates =
                {
                    Path.Combine(cursor.FullName, "ReignBeta", "server", "app", "ReignBetaServer.dll"),
                    Path.Combine(cursor.FullName, "ReignBetaServer", "bin", "Release", "ReignBetaServer.dll"),
                    Path.Combine(cursor.FullName, "ReignBetaServer.dll")
                };
                string match = candidates.FirstOrDefault(File.Exists);
                if (!string.IsNullOrWhiteSpace(match)) return match;
            }
            return "";
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Bannerlord Reign Offline-First Verification Lab");
            Console.WriteLine("Usage: dotnet ReignVerification.dll [options]");
            Console.WriteLine("  --tier quick|offline|live-llm|game|all");
            Console.WriteLine("         commit|full-regression|release-candidate");
            Console.WriteLine("         exhaustive-final-review|human-immersion");
            Console.WriteLine("  --suite <name>  --seed <number>  --repeat <count>");
            Console.WriteLine("  --fail-fast  --json  --cancel");
            Console.WriteLine("  --live-case-cap <count>  --game-timeout-seconds <seconds>");
            Console.WriteLine();
            Console.WriteLine("Real campaign folders are read-only. All mutable verification state is written under the isolated REIGN_DATA_ROOT.");
        }
    }
}
