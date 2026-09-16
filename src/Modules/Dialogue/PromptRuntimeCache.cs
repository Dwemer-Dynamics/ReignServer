using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private sealed class CachedPromptText
        {
            public DateTime LastWriteUtc;
            public long Length;
            public string Text;
        }

        private static readonly object PromptRuntimeCacheLock = new object();
        private static readonly Dictionary<string, CachedPromptText> PromptRuntimeCache =
            new Dictionary<string, CachedPromptText>(StringComparer.OrdinalIgnoreCase);

        private static string LoadPromptTemplateFromRuntimeCache(string name)
        {
            string path = PromptPath(name);
            if (!File.Exists(path))
            {
                Dictionary<string, string> defaults = DefaultPromptTemplates();
                return DefaultPromptText(name, defaults);
            }

            FileInfo info = new FileInfo(path);
            lock (PromptRuntimeCacheLock)
            {
                if (PromptRuntimeCache.TryGetValue(path, out CachedPromptText cached)
                    && cached.LastWriteUtc == info.LastWriteTimeUtc && cached.Length == info.Length)
                {
                    return cached.Text;
                }

                string text = File.ReadAllText(path, Encoding.UTF8);
                PromptRuntimeCache[path] = new CachedPromptText
                {
                    LastWriteUtc = info.LastWriteTimeUtc,
                    Length = info.Length,
                    Text = text
                };
                return text;
            }
        }

        private static void InvalidatePromptRuntimeCache(string name = "")
        {
            lock (PromptRuntimeCacheLock)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    PromptRuntimeCache.Clear();
                    return;
                }
                PromptRuntimeCache.Remove(PromptPath(name));
            }
        }
    }
}
