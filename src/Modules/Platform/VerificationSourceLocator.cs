using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static class VerificationSourceLocator
    {
        private static readonly HashSet<string> ExcludedDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", ".codex-build", "artifacts", "bin", "obj",
                "staging", "deployment-backups", "verification_contracts",
                "publish", "third_party", "server", "PortraitCache"
            };

        public static string ResolveUnique(
            string searchRoot,
            string logicalFileName,
            params string[] preferredPathFragments)
        {
            if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
                return string.Empty;
            if (string.IsNullOrWhiteSpace(logicalFileName)
                || logicalFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("A logical source file name is required.", "logicalFileName");

            string fullRoot = Path.GetFullPath(searchRoot);
            List<string> candidates = Enumerate(fullRoot, logicalFileName).ToList();
            foreach (string fragment in preferredPathFragments ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(fragment)) continue;
                string normalized = fragment.Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                List<string> preferred = candidates
                    .Where(path => path.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                if (preferred.Count == 1) return preferred[0];
                if (preferred.Count > 1) candidates = preferred;
            }
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count == 0)
                throw new FileNotFoundException(
                    "Could not locate logical verification source " + logicalFileName + " beneath " + fullRoot + ".");
            throw new InvalidDataException(
                "Logical verification source " + logicalFileName + " is ambiguous beneath " + fullRoot
                + ": " + string.Join(", ", candidates));
        }

        private static IEnumerable<string> Enumerate(string root, string fileName)
        {
            Stack<DirectoryInfo> pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(root));
            while (pending.Count > 0)
            {
                DirectoryInfo directory = pending.Pop();
                if (!directory.Exists
                    || (directory.Attributes & FileAttributes.ReparsePoint) != 0
                    || ExcludedDirectories.Contains(directory.Name))
                    continue;
                FileInfo[] files;
                DirectoryInfo[] children;
                try
                {
                    files = directory.GetFiles(fileName);
                    children = directory.GetDirectories();
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (FileInfo file in files)
                    yield return file.FullName;
                foreach (DirectoryInfo child in children)
                    pending.Push(child);
            }
        }
    }
}
