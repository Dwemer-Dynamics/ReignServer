using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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

        /// <summary>
        /// The text of a logical server source file together with the partial files it was split into beside it
        /// (<c>Name.cs</c> first, then every <c>Name.*.cs</c> in the same folder in ordinal name order), so a check that
        /// pinned one file keeps exactly the same scope after a behaviour-neutral split. Returns an empty string when the
        /// search root or the file is unavailable.
        /// </summary>
        public static string ReadServerSplitSourceText(
            string serverRoot,
            string logicalFileName,
            params string[] preferredPathFragments)
        {
            return string.Join("\n\n", ServerSplitSourceFiles(serverRoot, logicalFileName, preferredPathFragments)
                .Select(path => File.ReadAllText(path)));
        }

        /// <summary>
        /// The declaration text of every method named <paramref name="memberName"/> found in a logical server source
        /// file and its split partial files (see <see cref="ReadServerSplitSourceText"/>). Returns an empty string when
        /// the files or the member are unavailable.
        /// </summary>
        public static string ReadServerSplitMemberSourceText(
            string serverRoot,
            string logicalFileName,
            string memberName,
            params string[] preferredPathFragments)
        {
            if (string.IsNullOrWhiteSpace(memberName))
                throw new ArgumentException("A server member name is required.", "memberName");
            return string.Join("\n\n", ServerSplitSourceFiles(serverRoot, logicalFileName, preferredPathFragments)
                .SelectMany(path => ClientSourceSnapshot.ExtractMemberDeclarations(File.ReadAllText(path), memberName)));
        }

        private static List<string> ServerSplitSourceFiles(
            string serverRoot,
            string logicalFileName,
            string[] preferredPathFragments)
        {
            string mainPath = ResolveUnique(serverRoot, logicalFileName, preferredPathFragments);
            if (string.IsNullOrEmpty(mainPath) || !File.Exists(mainPath)) return new List<string>();
            string stem = Path.GetFileNameWithoutExtension(logicalFileName) + ".";
            List<string> files = new List<string> { mainPath };
            files.AddRange(Directory.EnumerateFiles(Path.GetDirectoryName(mainPath), "*.cs")
                .Where(path =>
                {
                    string name = Path.GetFileName(path);
                    return name.Length > stem.Length + 3
                        && name.StartsWith(stem, StringComparison.Ordinal)
                        && name.EndsWith(".cs", StringComparison.Ordinal);
                })
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal));
            return files;
        }

        // Client (ReignBeta) source-text contracts must not depend on which file holds the code. The client source
        // is read once into a snapshot (every src/**/*.cs, ordered by relative path) and checks either search all
        // of it or the files that declare a named type, so behaviour-neutral file moves keep the contracts valid.
        private static readonly TimeSpan ClientSnapshotLifetime = TimeSpan.FromSeconds(30);
        private static readonly object ClientSnapshotGate = new object();
        private static readonly Dictionary<string, ClientSourceSnapshot> ClientSnapshots =
            new Dictionary<string, ClientSourceSnapshot>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ClientSourceSnapshot> ServerProductSnapshots =
            new Dictionary<string, ClientSourceSnapshot>(StringComparer.Ordinal);
        // Sources compiled only into the verification build (the ReignExcludeVerification Compile Remove list in
        // ReignServer.csproj, besides the *SelfTests*, *Tests.cs and *Fixtures.cs patterns). They quote the very text
        // the server contracts look for, so the server type reader leaves them out.
        private static readonly HashSet<string> VerificationOnlyServerSources =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Modules/Platform/VerificationLab.cs", "Modules/Platform/VerificationLab.Contracts.cs",
                "Modules/Platform/VerificationLab.Suites.cs", "Modules/Platform/VerificationLab.Support.cs",
                "Modules/Platform/VerificationSourceLocator.cs",
                "Modules/Platform/Providers/CodexPerformanceVerification.cs", "Modules/Court/CapitalAmbassadorTestSystem.cs",
                "Modules/Portraits/BackgroundPortraitContracts.cs", "Modules/Dialogue/Gossip/PersonalGossipCompositionProbe.cs"
            };
        private static readonly Regex TypeDeclarationPattern = new Regex(
            @"^[ \t]*(?:\[[^\]\r\n]*\][ \t]*)*"
            + @"(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|unsafe|new|file|ref)\s+)*"
            + @"(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex NamespacePattern = new Regex(
            @"^[ \t]*namespace\s+(?<name>[A-Za-z_][A-Za-z0-9_.]*)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>
        /// All client source files beneath <c>clientRoot/src</c>, concatenated in ordinal relative-path order.
        /// Returns an empty string when the client root is unavailable.
        /// </summary>
        public static string ReadClientSourceText(string clientRoot)
        {
            ClientSourceSnapshot snapshot = GetClientSnapshot(clientRoot);
            return snapshot == null ? string.Empty : snapshot.AllText;
        }

        /// <summary>
        /// The text of every client source file that declares <paramref name="typeName"/> (all partial declarations),
        /// concatenated in ordinal relative-path order, wherever those files live. A namespace-qualified name
        /// (<c>ReignBeta.SubModule</c>) selects declarations in that namespace. Returns an empty string when the client
        /// root is unavailable; throws when the type is not declared or the unqualified name spans namespaces.
        /// </summary>
        public static string ReadClientTypeSourceText(string clientRoot, string typeName)
        {
            ClientSourceSnapshot snapshot = GetClientSnapshot(clientRoot);
            return snapshot == null ? string.Empty : snapshot.TypeText(typeName);
        }

        // The code that was the Program partial class before the module services and providers took it over: files
        // declaring Program or one of the Platform area services, the domain handler files that were
        // Program.<Area>.cs, and the module route files.
        private static readonly Regex ProgramAreaTypePattern = new Regex(
            @"\bpartial\s+class\s+(Program|LlmProviderService|SettingsService|PromptService|ActionPipelineService|"
            + @"ConversationResponseService|CampaignFileService|OperationsService|VerificationService)\b",
            RegexOptions.CultureInvariant);
        private static readonly HashSet<string> FormerProgramHandlerFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "CharacterConstruction.cs", "CharacterProfiles.cs", "ContextPulls.cs", "DialogueTurns.cs",
            "EventConversations.cs", "MemoryPacket.cs", "MemoryRetrieval.cs", "MemoryStore.cs", "PromptContext.cs",
            "PortraitRequests.cs"
        };

        internal static bool IsProgramAreaSource(string relativePath, string text)
        {
            string name = Path.GetFileName(relativePath);
            return FormerProgramHandlerFiles.Contains(name)
                || name.EndsWith("Routes.cs", StringComparison.Ordinal)
                || ProgramAreaTypePattern.IsMatch(text ?? "");
        }

        /// <summary>
        /// The product server source of the former <c>Program</c> area (see <see cref="IsProgramAreaSource"/>),
        /// concatenated in ordinal relative-path order; verification-only sources are left out, as they were from the
        /// <c>Program</c> type text. Empty when the server root is unavailable.
        /// </summary>
        public static string ReadServerProgramAreaSourceText(string serverRoot)
        {
            ClientSourceSnapshot snapshot = GetServerProductSnapshot(serverRoot);
            return snapshot == null ? string.Empty : string.Join("\n\n", snapshot.Files
                .Where(file => IsProgramAreaSource(file.Key, file.Value)).Select(file => file.Value));
        }

        /// <summary>
        /// The text of every product server source file beneath <c>serverRoot/src</c> that declares
        /// <paramref name="typeName"/> (all partial declarations, for example every <c>partial class Program</c> file),
        /// concatenated in ordinal relative-path order, wherever those files live. Verification-only sources are left
        /// out. Server source-text contracts read a type this way so behaviour-neutral moves between its partial files
        /// keep them valid. Returns an empty string when the server root is unavailable; throws when the type is not
        /// declared or the unqualified name spans namespaces.
        /// </summary>
        public static string ReadServerTypeSourceText(string serverRoot, string typeName)
        {
            ClientSourceSnapshot snapshot = GetServerProductSnapshot(serverRoot);
            return snapshot == null ? string.Empty : snapshot.TypeText(typeName);
        }

        /// <summary>
        /// Every client source file beneath <c>clientRoot/src</c> as (relative path, text) pairs in ordinal path order,
        /// for checks that must attribute a finding to an individual file. Empty when the client root is unavailable.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> ReadClientSourceFiles(string clientRoot)
        {
            ClientSourceSnapshot snapshot = GetClientSnapshot(clientRoot);
            return snapshot == null ? new List<KeyValuePair<string, string>>() : snapshot.Files;
        }

        /// <summary>Whether a source text declares <paramref name="namespaceName"/> or one of its child namespaces.</summary>
        public static bool SourceDeclaresNamespace(string sourceText, string namespaceName)
        {
            if (string.IsNullOrWhiteSpace(namespaceName))
                throw new ArgumentException("A client namespace is required.", "namespaceName");
            return !string.IsNullOrEmpty(sourceText)
                && NamespacePattern.Matches(sourceText).Cast<Match>()
                    .Select(match => match.Groups["name"].Value)
                    .Any(name => string.Equals(name, namespaceName, StringComparison.Ordinal)
                        || name.StartsWith(namespaceName + ".", StringComparison.Ordinal));
        }

        /// <summary>Whether a source text declares a type with the given simple name.</summary>
        public static bool SourceDeclaresType(string sourceText, string typeName)
        {
            return !string.IsNullOrEmpty(sourceText)
                && ClientSourceSnapshot.DeclaresType(sourceText, typeName);
        }

        /// <summary>
        /// The declaration text (signature through closing brace) of every method named <paramref name="memberName"/>
        /// found in the files that declare <paramref name="typeName"/>, wherever those files live. Returns an empty
        /// string when the client root is unavailable; throws when the type or member is not found.
        /// </summary>
        public static string ReadClientMemberSourceText(string clientRoot, string typeName, string memberName)
        {
            ClientSourceSnapshot snapshot = GetClientSnapshot(clientRoot);
            return snapshot == null ? string.Empty : snapshot.MemberText(typeName, memberName);
        }

        /// <summary>
        /// Diagnostic only: the current path of a logical client source file, or an empty string when it is missing
        /// or ambiguous. Contracts never depend on this value.
        /// </summary>
        public static string DescribeClientSourcePath(
            string clientRoot,
            string logicalFileName,
            params string[] preferredPathFragments)
        {
            try
            {
                return ResolveUnique(
                    clientRoot,
                    logicalFileName,
                    new[] { "src" }.Concat(preferredPathFragments ?? new string[0]).ToArray());
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (InvalidDataException)
            {
                return string.Empty;
            }
        }

        private static ClientSourceSnapshot GetClientSnapshot(string clientRoot)
        {
            if (string.IsNullOrWhiteSpace(clientRoot)) return null;
            string sourceRoot = Path.Combine(Path.GetFullPath(clientRoot), "src");
            if (!Directory.Exists(sourceRoot)) return null;
            lock (ClientSnapshotGate)
            {
                if (ClientSnapshots.TryGetValue(sourceRoot, out ClientSourceSnapshot cached)
                    && DateTime.UtcNow - cached.CreatedUtc < ClientSnapshotLifetime)
                    return cached;
                ClientSourceSnapshot created = ClientSourceSnapshot.Load(sourceRoot);
                ClientSnapshots[sourceRoot] = created;
                return created;
            }
        }

        private static ClientSourceSnapshot GetServerProductSnapshot(string serverRoot)
        {
            if (string.IsNullOrWhiteSpace(serverRoot)) return null;
            string sourceRoot = Path.Combine(Path.GetFullPath(serverRoot), "src");
            if (!Directory.Exists(sourceRoot)) return null;
            lock (ClientSnapshotGate)
            {
                if (ServerProductSnapshots.TryGetValue(sourceRoot, out ClientSourceSnapshot cached)
                    && DateTime.UtcNow - cached.CreatedUtc < ClientSnapshotLifetime)
                    return cached;
                ClientSourceSnapshot created = ClientSourceSnapshot.Load(sourceRoot, IsServerProductSource);
                ServerProductSnapshots[sourceRoot] = created;
                return created;
            }
        }

        private static bool IsServerProductSource(string relativePath)
        {
            string fileName = Path.GetFileName(relativePath);
            return fileName.IndexOf("SelfTests", StringComparison.OrdinalIgnoreCase) < 0
                && !fileName.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)
                && !fileName.EndsWith("Fixtures.cs", StringComparison.OrdinalIgnoreCase)
                && !VerificationOnlyServerSources.Contains(relativePath);
        }

        private sealed class ClientSourceSnapshot
        {
            private readonly List<KeyValuePair<string, string>> files;
            private readonly Dictionary<string, string> typeTexts = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly object gate = new object();
            private string allText;

            private ClientSourceSnapshot(string sourceRoot, List<KeyValuePair<string, string>> files)
            {
                SourceRoot = sourceRoot;
                this.files = files;
                CreatedUtc = DateTime.UtcNow;
            }

            public string SourceRoot { get; }

            public DateTime CreatedUtc { get; }

            public IReadOnlyList<KeyValuePair<string, string>> Files => files.AsReadOnly();

            public static bool DeclaresType(string text, string simpleName)
            {
                return DeclarationNamespaces(text, simpleName).Any();
            }

            public string AllText
            {
                get
                {
                    lock (gate)
                    {
                        return allText ??= string.Join("\n\n", files.Select(file => file.Value));
                    }
                }
            }

            public static ClientSourceSnapshot Load(string sourceRoot, Func<string, bool> includeRelativePath = null)
            {
                List<KeyValuePair<string, string>> files = Enumerate(sourceRoot, "*.cs")
                    .Select(path => new KeyValuePair<string, string>(
                        Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'), path))
                    .Where(pair => includeRelativePath == null || includeRelativePath(pair.Key))
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new KeyValuePair<string, string>(pair.Key, File.ReadAllText(pair.Value)))
                    .ToList();
                return new ClientSourceSnapshot(sourceRoot, files);
            }

            public string TypeText(string typeName)
            {
                lock (gate)
                {
                    if (typeTexts.TryGetValue(typeName ?? string.Empty, out string cached)) return cached;
                    string text = string.Join("\n\n", TypeFiles(typeName));
                    typeTexts[typeName] = text;
                    return text;
                }
            }

            public string MemberText(string typeName, string memberName)
            {
                if (string.IsNullOrWhiteSpace(memberName))
                    throw new ArgumentException("A client member name is required.", "memberName");
                List<string> typeFiles;
                lock (gate)
                {
                    typeFiles = TypeFiles(typeName);
                }
                List<string> members = typeFiles
                    .SelectMany(text => ExtractMemberDeclarations(text, memberName))
                    .ToList();
                if (members.Count == 0)
                    throw new FileNotFoundException(
                        "Could not locate member " + memberName + " of client type " + typeName + " beneath " + SourceRoot + ".");
                return string.Join("\n\n", members);
            }

            private List<string> TypeFiles(string typeName)
            {
                if (string.IsNullOrWhiteSpace(typeName))
                    throw new ArgumentException("A client type name is required.", "typeName");
                int separator = typeName.LastIndexOf('.');
                string requiredNamespace = separator < 0 ? null : typeName.Substring(0, separator);
                string simpleName = separator < 0 ? typeName : typeName.Substring(separator + 1);
                var declaring = new List<string>();
                var namespaces = new HashSet<string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> file in files)
                {
                    List<string> found = DeclarationNamespaces(file.Value, simpleName)
                        .Where(name => requiredNamespace == null || string.Equals(name, requiredNamespace, StringComparison.Ordinal))
                        .ToList();
                    if (found.Count == 0) continue;
                    declaring.Add(file.Value);
                    namespaces.UnionWith(found);
                }
                if (declaring.Count == 0)
                    throw new FileNotFoundException(
                        "Could not locate a declaration of client type " + typeName + " beneath " + SourceRoot + ".");
                if (namespaces.Count > 1)
                    throw new InvalidDataException(
                        "Client type " + typeName + " is declared in several namespaces beneath " + SourceRoot
                        + "; qualify it with one of: " + string.Join(", ", namespaces.OrderBy(name => name, StringComparer.Ordinal)));
                return declaring;
            }

            // Every method, constructor or operator-like member named memberName in one file, from the start of its
            // declaration line through its closing brace (or the semicolon of an expression body).
            internal static IEnumerable<string> ExtractMemberDeclarations(string text, string memberName)
            {
                var pattern = new Regex(
                    @"^[ \t]*(?:\[[^\]\r\n]*\][ \t]*)*"
                    + @"(?:(?:public|private|protected|internal|static|async|override|virtual|sealed|abstract|extern|unsafe|new|partial|readonly)\s+)+"
                    + @"[^\r\n;={}()]*?\b" + Regex.Escape(memberName) + @"\s*(?:<[^>\r\n]*>)?\s*\(",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant);
                foreach (Match match in pattern.Matches(text))
                {
                    int end = FindMemberEnd(text, match.Index + match.Length - 1);
                    if (end > match.Index) yield return text.Substring(match.Index, end - match.Index);
                }
            }

            // Given the index of a parameter list's opening parenthesis, returns the index just past the member body,
            // or -1 when no body follows (an abstract, extern or partial declaration).
            private static int FindMemberEnd(string text, int openParen)
            {
                int index = SkipBalanced(text, openParen, '(', ')');
                while (index >= 0 && index < text.Length)
                {
                    int skipped = SkipNonCode(text, index);
                    if (skipped != index)
                    {
                        index = skipped;
                        continue;
                    }
                    char current = text[index];
                    if (current == '{') return SkipBalanced(text, index, '{', '}');
                    if (current == ';') return -1;
                    if (current == '=' && index + 1 < text.Length && text[index + 1] == '>')
                    {
                        for (int cursor = index + 2; cursor < text.Length;)
                        {
                            int next = SkipNonCode(text, cursor);
                            if (next != cursor)
                            {
                                cursor = next;
                                continue;
                            }
                            char c = text[cursor];
                            if (c == '(' || c == '{' || c == '[')
                            {
                                cursor = SkipBalanced(text, cursor, c, c == '(' ? ')' : c == '{' ? '}' : ']');
                                if (cursor < 0) return -1;
                                continue;
                            }
                            if (c == ';') return cursor + 1;
                            cursor++;
                        }
                        return -1;
                    }
                    index++;
                }
                return -1;
            }

            // Given the index of an opening delimiter, returns the index just past its matching closing delimiter,
            // ignoring delimiters inside comments, strings and character literals; -1 when unbalanced.
            private static int SkipBalanced(string text, int open, char opening, char closing)
            {
                int depth = 0;
                for (int index = open; index < text.Length;)
                {
                    int skipped = SkipNonCode(text, index);
                    if (skipped != index)
                    {
                        index = skipped;
                        continue;
                    }
                    if (text[index] == opening) depth++;
                    else if (text[index] == closing && --depth == 0) return index + 1;
                    index++;
                }
                return -1;
            }

            // Returns the index just past a comment, string or character literal starting at index, or index itself.
            private static int SkipNonCode(string text, int index)
            {
                if (index >= text.Length) return index;
                char current = text[index];
                if (current == '/' && index + 1 < text.Length)
                {
                    if (text[index + 1] == '/')
                    {
                        int lineEnd = text.IndexOf('\n', index);
                        return lineEnd < 0 ? text.Length : lineEnd;
                    }
                    if (text[index + 1] == '*')
                    {
                        int commentEnd = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                        return commentEnd < 0 ? text.Length : commentEnd + 2;
                    }
                    return index;
                }
                if (current == '\'')
                {
                    int cursor = index + 1;
                    if (cursor < text.Length && text[cursor] == '\\') cursor += 2;
                    else cursor++;
                    while (cursor < text.Length && text[cursor] != '\'') cursor++;
                    return Math.Min(text.Length, cursor + 1);
                }
                int quote = index;
                bool verbatim = false;
                bool interpolated = false;
                while (quote < text.Length && (text[quote] == '@' || text[quote] == '$'))
                {
                    if (text[quote] == '@') verbatim = true;
                    else interpolated = true;
                    quote++;
                }
                if (quote >= text.Length || text[quote] != '"') return index;
                int quoteRun = 0;
                while (quote + quoteRun < text.Length && text[quote + quoteRun] == '"') quoteRun++;
                if (quoteRun >= 3)
                {
                    string delimiter = new string('"', quoteRun);
                    int rawEnd = text.IndexOf(delimiter, quote + quoteRun, StringComparison.Ordinal);
                    return rawEnd < 0 ? text.Length : rawEnd + quoteRun;
                }
                for (int cursor = quote + 1; cursor < text.Length;)
                {
                    char c = text[cursor];
                    if (!verbatim && c == '\\')
                    {
                        cursor += 2;
                        continue;
                    }
                    if (c == '"')
                    {
                        if (verbatim && cursor + 1 < text.Length && text[cursor + 1] == '"')
                        {
                            cursor += 2;
                            continue;
                        }
                        return cursor + 1;
                    }
                    if (interpolated && c == '{')
                    {
                        if (cursor + 1 < text.Length && text[cursor + 1] == '{')
                        {
                            cursor += 2;
                            continue;
                        }
                        cursor = SkipBalanced(text, cursor, '{', '}');
                        if (cursor < 0) return text.Length;
                        continue;
                    }
                    cursor++;
                }
                return text.Length;
            }

            // The namespace enclosing each declaration of the named type in one file (the nearest preceding namespace
            // declaration, or an empty string for the global namespace).
            private static IEnumerable<string> DeclarationNamespaces(string text, string simpleName)
            {
                List<Match> namespaceMatches = NamespacePattern.Matches(text).Cast<Match>().ToList();
                foreach (Match declaration in TypeDeclarationPattern.Matches(text))
                {
                    if (!string.Equals(declaration.Groups["name"].Value, simpleName, StringComparison.Ordinal)) continue;
                    string enclosing = string.Empty;
                    foreach (Match candidate in namespaceMatches)
                    {
                        if (candidate.Index < declaration.Index) enclosing = candidate.Groups["name"].Value;
                    }
                    yield return enclosing;
                }
            }
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
