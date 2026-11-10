using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Reign.Core.Contracts.Relationships
{
    // A receipt for exactly the immutable durable texts selected for one HTTP
    // attempt. The publisher supplies its existing mutation lock. Parsing and
    // HTTP never retain that lock; retirement cannot erase a newer replacement.
    public sealed class RelationshipInputOutbox
    {
        private readonly object mutationLock;
        private readonly string[] paths, texts;
        public IReadOnlyList<string> Paths { get; }
        public IReadOnlyList<string> Texts { get; }
        private RelationshipInputOutbox(object mutationLock, string[] paths, string[] texts)
        {
            this.mutationLock = mutationLock; this.paths = paths; this.texts = texts;
            Paths = Array.AsReadOnly(paths); Texts = Array.AsReadOnly(texts);
        }
        public static RelationshipInputOutbox Capture(string directory, object mutationLock, int maximum = 8)
        {
            if (maximum < 1 || maximum > 8) throw new ArgumentOutOfRangeException(nameof(maximum));
            if (mutationLock == null) throw new ArgumentNullException(nameof(mutationLock));
            lock (mutationLock)
            {
                string[] paths = Directory.Exists(directory)
                    ? Directory.GetFiles(directory, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(maximum).ToArray()
                    : Array.Empty<string>();
                return new RelationshipInputOutbox(mutationLock, paths, paths.Select(x => File.ReadAllText(x, Encoding.UTF8)).ToArray());
            }
        }
        public T[] Parse<T>(Func<string, T> parse) => texts.Select(parse).ToArray();
        public void RetireAccepted(Action<Exception> onFailure)
        {
            lock (mutationLock)
                for (int i = 0; i < paths.Length; i++)
                    try
                    {
                        if (File.Exists(paths[i]) && string.Equals(File.ReadAllText(paths[i], Encoding.UTF8), texts[i], StringComparison.Ordinal))
                            File.Delete(paths[i]);
                    }
                    catch (Exception ex) { onFailure(ex); }
        }
    }
}
