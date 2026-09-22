using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string RuntimeMigrationMarkerName = ".runtime-root-v1";

        private static void MigrateLegacyRuntimeDataIfNeeded()
        {
            string configuredRoot = Environment.GetEnvironmentVariable(
                "REIGN_DATA_ROOT");
            if (string.IsNullOrWhiteSpace(configuredRoot))
                return;

            string legacyRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "data"));
            string targetRoot = Path.GetFullPath(DataDir);
            if (legacyRoot.Equals(
                    targetRoot,
                    StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(legacyRoot))
                return;

            Directory.CreateDirectory(targetRoot);
            string markerPath = Path.Combine(
                targetRoot,
                RuntimeMigrationMarkerName);
            if (File.Exists(markerPath))
                return;

            HashSet<string> redirectedDirectories = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                "ss",
                "save-sync"
            };
            foreach (string directory in Directory.EnumerateDirectories(
                legacyRoot,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                if (redirectedDirectories.Contains(Path.GetFileName(directory)))
                    continue;
                CopyDirectoryMissingOnly(
                    directory,
                    Path.Combine(targetRoot, Path.GetFileName(directory)));
            }
            foreach (string file in Directory.EnumerateFiles(
                legacyRoot,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                string destination = Path.Combine(targetRoot, Path.GetFileName(file));
                if (!File.Exists(destination))
                    File.Copy(file, destination, false);
            }

            string stableSaveSyncRoot = SaveSyncRoot();
            foreach (string legacyName in redirectedDirectories)
            {
                string source = Path.Combine(legacyRoot, legacyName);
                if (Directory.Exists(source))
                    CopyDirectoryMissingOnly(source, stableSaveSyncRoot);
            }

            File.WriteAllText(
                markerPath,
                "migratedUtc=" + DateTime.UtcNow.ToString("O")
                    + Environment.NewLine
                    + "legacyRoot=" + legacyRoot
                    + Environment.NewLine,
                Encoding.UTF8);
        }

        private static void CopyDirectoryMissingOnly(
            string sourceRoot,
            string destinationRoot)
        {
            Directory.CreateDirectory(destinationRoot);
            string sourcePrefix = Path.GetFullPath(sourceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            foreach (string sourceDirectory in Directory.EnumerateDirectories(
                sourceRoot,
                "*",
                SearchOption.AllDirectories))
            {
                string relative = Path.GetFullPath(sourceDirectory)
                    .Substring(sourcePrefix.Length);
                Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
            }
            foreach (string sourceFile in Directory.EnumerateFiles(
                sourceRoot,
                "*",
                SearchOption.AllDirectories))
            {
                string relative = Path.GetFullPath(sourceFile)
                    .Substring(sourcePrefix.Length);
                string destination = Path.Combine(destinationRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                if (!File.Exists(destination))
                    File.Copy(sourceFile, destination, false);
            }
        }
    }
}
