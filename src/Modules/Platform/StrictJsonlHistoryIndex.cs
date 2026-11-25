using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ReignBetaServer
{
    // Derived, process-local state. Campaign files remain the sole authority.
    // Callers hold Program.FileLock while reading or invalidating an entry.
    internal static class StrictJsonlHistoryIndex
    {
        private const int RecentRowsLimit = 100;
        private const int CachedFilesLimit = 128;
        private const int CachedPromptRowsLimit = 512;
        private const int CachedAuthenticatedFilesLimit = 16;
        private const int CachedAuthorityRowsLimit = 1024;
        private const int CachedAuthorityCharactersLimit = 262144;
        private static readonly Dictionary<(string Path, bool RankMemory), HistoryIndex> IndexedHistory = new();
        private static readonly Dictionary<string, SocialPromptIndex> IndexedSocialPrompts = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, AuthenticatedIndex> IndexedAuthenticatedHistory = new(StringComparer.Ordinal);

        [ThreadStatic] internal static Action<int> SourceBytesReadForTests;

        private readonly record struct RowRange(long Start, int Length);
        private readonly record struct RankedRow(RowRange Range, long Timestamp, long ReverseOrdinal)
        {
            internal (long, long) Priority => (Timestamp, ReverseOrdinal);
        }

        private sealed record HistoryIndex(FileVersion Version, long CampaignGeneration, CultureInfo Culture, RowRange[] Rows);
        private sealed record SocialPromptIndex(FileVersion Version, long CampaignGeneration, CultureInfo Culture, int Limit,
            DialogueService.SocialEventPromptWindowRow<RowRange>[] Rows, int[] SeekOrder);
        private sealed record AuthenticatedIndex(FileVersion Version, long CampaignGeneration, CultureInfo Culture,
            string Timeline, long WorldDayBits, int Limit, DialogueService.OriginalHistorySeed[] Authority,
            DialogueService.OriginalHistorySlot<RowRange>[] Window);

        internal static bool TryReadRecentMemoryRows(string path, int limit, out List<Dictionary<string, object>> result)
            => TryReadRows(path, limit, rankMemory: true, out result);

        internal static bool TryReadPhysicalTail(string path, int limit, out List<Dictionary<string, object>> result)
            => TryReadRows(path, limit, rankMemory: false, out result);

        private static bool TryReadRows(string path, int limit, bool rankMemory, out List<Dictionary<string, object>> result)
        {
            result = null;
            CultureInfo culture = rankMemory ? CultureInfo.CurrentCulture : null;
            if (limit < 1 || limit > RecentRowsLimit || culture is { IsReadOnly: false }
                || CampaignFileService.IsDialogueHistoryPath(path))
                return false;

            using var stream = OpenRead(path);
            if (!TryGetFileVersion(stream, out var version)) return false;
            long generation = Volatile.Read(ref PersistenceService.CampaignReplacementGeneration);
            var key = (path, rankMemory);
            if (!IndexedHistory.TryGetValue(key, out var index)
                || index.Version != version || index.CampaignGeneration != generation || !ReferenceEquals(index.Culture, culture))
            {
                IndexedHistory.Remove(key);
                var ranges = rankMemory ? BuildMemoryRanges(stream) : BuildTailRanges(stream);
                if (ranges == null) return false;
                index = new HistoryIndex(version, generation, culture, ranges);
            }

            var rows = new List<Dictionary<string, object>>(Math.Min(limit, index.Rows.Length));
            try
            {
                foreach (var range in index.Rows.TakeLast(limit))
                    rows.Add(ReadRow(stream, range));
            }
            catch (Exception ex) when (ex is EndOfStreamException or JsonException)
            {
                // The selected rows were already parsed during indexing. Let
                // the legacy read report any error against the current file.
                IndexedHistory.Remove(key);
                return false;
            }
            if (!IsCurrentSnapshot(stream, version, generation))
            {
                IndexedHistory.Remove(key);
                return false;
            }

            if (!IndexedHistory.ContainsKey(key)) ReserveCacheSlot();
            IndexedHistory[key] = index;
            result = rows;
            return true;
        }

        internal static bool TryReadSocialEventPrompt(FileStream stream, string path, int configuredLimit,
            out List<Dictionary<string, object>> result)
        {
            result = null;
            CultureInfo culture = CultureInfo.CurrentCulture;
            if (!culture.IsReadOnly || !TryGetFileVersion(stream, out var version)) return false;
            long generation = Volatile.Read(ref PersistenceService.CampaignReplacementGeneration);
            int limit = DialogueService.SocialEventPromptLimit(configuredLimit);
            if (!IndexedSocialPrompts.TryGetValue(path, out var index)
                || index.Version != version || index.CampaignGeneration != generation
                || index.Limit != limit || !ReferenceEquals(index.Culture, culture))
            {
                IndexedSocialPrompts.Remove(path);
                var selector = new DialogueService.SocialEventPromptSelector<RowRange>();
                long ordinal = 0;
                bool Collect(Dictionary<string, object> row, RowRange range)
                {
                    selector.Add(ordinal++, row, range);
                    return true;
                }

                if (!TryScanStrictRows(stream, Collect)) return false;
                var window = selector.Complete(limit, CachedPromptRowsLimit);
                // A long boundary exchange is legal. Keep the original complete
                // selection when it exceeds the cache's retained offset budget.
                if (window == null) return false;
                var seekOrder = Enumerable.Range(0, window.Length).OrderBy(slot => window[slot].Position.Start).ToArray();
                index = new SocialPromptIndex(version, generation, culture, limit, window, seekOrder);
            }

            var rows = new Dictionary<string, object>[index.Rows.Length];
            try
            {
                // Visit selected source ranges in physical order, just as the
                // old second pass does. Return the original chronological slots.
                foreach (int slot in index.SeekOrder)
                {
                    var range = index.Rows[slot];
                    var row = ReadRow(stream, range.Position);
                    if (row == null)
                    {
                        IndexedSocialPrompts.Remove(path);
                        return false;
                    }
                    if (range.Unbound)
                        DialogueService.BindUnidentifiedPromptTranscriptRow(row, range.Rank);
                    rows[slot] = row;
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or JsonException)
            {
                IndexedSocialPrompts.Remove(path);
                return false;
            }
            if (!IsCurrentSnapshot(stream, version, generation))
            {
                IndexedSocialPrompts.Remove(path);
                return false;
            }

            if (!IndexedSocialPrompts.ContainsKey(path)) ReserveCacheSlot();
            IndexedSocialPrompts[path] = index;
            result = rows.ToList();
            return true;
        }

        // Call only after the authenticated SQL read and any required cold-file
        // prevalidation. Never acquire campaign SQL while holding FileLock.
        internal static bool TryReadAuthenticatedWindow(string path, string timeline, double worldDay, int limit,
            List<Dictionary<string, object>> authoritative, out List<Dictionary<string, object>> result)
        {
            result = null;
            CultureInfo culture = CultureInfo.CurrentCulture;
            if (limit < 1 || limit > RecentRowsLimit || !culture.IsReadOnly
                || authoritative.Count > CachedAuthorityRowsLimit || !File.Exists(path))
                return false;

            var seeds = new DialogueService.OriginalHistorySeed[authoritative.Count];
            long seedCharacters = 0;
            for (int slot = 0; slot < seeds.Length; slot++)
            {
                var seed = DialogueService.OriginalHistorySeed.FromRow(authoritative[slot]);
                seedCharacters += (long)seed.Role.Length + seed.Text.Length + seed.Exchange.Length;
                if (seedCharacters > CachedAuthorityCharactersLimit) return false;
                seeds[slot] = seed;
            }

            lock (Program.FileLock)
            {
                using var stream = OpenRead(path);
                if (!TryGetFileVersion(stream, out var version)) return false;
                long generation = Volatile.Read(ref PersistenceService.CampaignReplacementGeneration);
                long dayBits = BitConverter.DoubleToInt64Bits(worldDay);
                if (!IndexedAuthenticatedHistory.TryGetValue(path, out var index)
                    || index.Version != version || index.CampaignGeneration != generation
                    || index.Timeline != timeline || index.WorldDayBits != dayBits || index.Limit != limit
                    || !ReferenceEquals(index.Culture, culture) || !index.Authority.SequenceEqual(seeds))
                {
                    IndexedAuthenticatedHistory.Remove(path);
                    var selection = new DialogueService.OriginalHistorySelection<RowRange>(timeline, worldDay, limit);
                    for (int slot = 0; slot < seeds.Length; slot++)
                        selection.AddAuthoritative(seeds[slot], slot);
                    bool Collect(Dictionary<string, object> row, RowRange range)
                    {
                        selection.AddLegacy(row, range);
                        return true;
                    }

                    if (!TryScanStrictRows(stream, Collect)) return false;
                    index = new AuthenticatedIndex(version, generation, culture, timeline, dayBits, limit, seeds,
                        selection.Complete().ToArray());
                }

                var rows = new List<Dictionary<string, object>>(index.Window.Length);
                try
                {
                    foreach (var slot in index.Window)
                    {
                        // A cached legacy offset may legally decode to null.
                        // The existing history envelope maps it to an empty row.
                        rows.Add(slot.IsLegacy ? DialogueService.MarkLegacyOriginal(ReadRow(stream, slot.Legacy))
                            : authoritative[slot.AuthoritativeIndex]);
                    }
                }
                catch (Exception ex) when (ex is EndOfStreamException or JsonException)
                {
                    IndexedAuthenticatedHistory.Remove(path);
                    return false;
                }
                if (!IsCurrentSnapshot(stream, version, generation))
                {
                    IndexedAuthenticatedHistory.Remove(path);
                    return false;
                }

                if (IndexedAuthenticatedHistory.Count >= CachedAuthenticatedFilesLimit && !IndexedAuthenticatedHistory.ContainsKey(path))
                    IndexedAuthenticatedHistory.Remove(IndexedAuthenticatedHistory.Keys.First());
                IndexedAuthenticatedHistory[path] = index;
                result = rows;
                return true;
            }
        }

        private static Dictionary<string, object> ReadRow(FileStream stream, RowRange range)
        {
            byte[] bytes = new byte[range.Length];
            stream.Position = range.Start;
            stream.ReadExactly(bytes);
            SourceBytesReadForTests?.Invoke(bytes.Length);
            return ServerCore.Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes));
        }

        private static bool IsCurrentSnapshot(FileStream stream, FileVersion version, long generation)
            => TryGetFileVersion(stream, out var observed) && observed == version
                && generation == Volatile.Read(ref PersistenceService.CampaignReplacementGeneration);

        private static void ReserveCacheSlot()
        {
            if (IndexedHistory.Count + IndexedSocialPrompts.Count < CachedFilesLimit) return;
            if (IndexedHistory.Count >= IndexedSocialPrompts.Count)
                IndexedHistory.Remove(IndexedHistory.Keys.First());
            else
                IndexedSocialPrompts.Remove(IndexedSocialPrompts.Keys.First());
        }

        private static RowRange[] BuildMemoryRanges(FileStream stream)
        {
            var rows = new PriorityQueue<RankedRow, (long, long)>();
            long ordinal = 0;
            bool Collect(Dictionary<string, object> row, RowRange range)
            {
                long reverseOrdinal = -ordinal++;
                try
                {
                    if (!CampaignFileService.DurableDialogueMemoryRowIsUsable(row)) return true;
                }
                catch (ArgumentException)
                {
                    // The original reader checks the rest of the file after a
                    // classification failure so a later JSON parse error wins.
                    return false;
                }
                var ranked = new RankedRow(range, ServerCore.ReadLong(row, "ts", 0), reverseOrdinal);
                if (rows.Count < RecentRowsLimit) rows.Enqueue(ranked, ranked.Priority);
                else rows.EnqueueDequeue(ranked, ranked.Priority);
                return true;
            }

            if (!TryScanStrictRows(stream, Collect)) return null;
            var ordered = new RowRange[rows.Count];
            for (int i = 0; i < ordered.Length; i++) ordered[i] = rows.Dequeue().Range;
            return ordered;
        }

        private static RowRange[] BuildTailRanges(FileStream stream)
        {
            var rows = new Queue<RowRange>(RecentRowsLimit);
            bool Collect(Dictionary<string, object> _, RowRange range)
            {
                // A nonblank literal null has a physical slot in the raw tail.
                if (rows.Count == RecentRowsLimit) rows.Dequeue();
                rows.Enqueue(range);
                return true;
            }

            return TryScanStrictRows(stream, Collect) ? rows.ToArray() : null;
        }

        private static bool TryScanStrictRows(FileStream stream, Func<Dictionary<string, object>, RowRange, bool> visit)
        {
            Span<byte> prefix = stackalloc byte[4];
            int prefixLength = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
            SourceBytesReadForTests?.Invoke(prefixLength);
            // The legacy StreamReader handles wide BOMs. Leave those files on
            // that reader rather than interpreting their line boundaries as bytes.
            if (prefixLength >= 2 && ((prefix[0] == 0xff && prefix[1] == 0xfe)
                    || (prefix[0] == 0xfe && prefix[1] == 0xff))
                || prefixLength == 4 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xfe && prefix[3] == 0xff)
                return false;
            long lineStart = prefixLength >= 3 && prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf ? 3 : 0;
            stream.Position = lineStart;

            var pending = new ArrayBufferWriter<byte>();
            byte[] buffer = new byte[8192];
            long blockStart = lineStart;
            bool skipLf = false;
            bool VisitLine()
            {
                string line = Encoding.UTF8.GetString(pending.WrittenSpan);
                if (string.IsNullOrWhiteSpace(line)) return true;
                var row = ServerCore.Json.Deserialize<Dictionary<string, object>>(line);
                return visit(row, new RowRange(lineStart, pending.WrittenCount));
            }

            int size;
            while ((size = stream.Read(buffer)) != 0)
            {
                SourceBytesReadForTests?.Invoke(size);
                int segmentStart = 0;
                for (int i = 0; i < size; i++)
                {
                    byte next = buffer[i];
                    if (skipLf)
                    {
                        skipLf = false;
                        if (next == '\n')
                        {
                            lineStart = blockStart + i + 1;
                            segmentStart = i + 1;
                            continue;
                        }
                    }
                    if (next != '\r' && next != '\n') continue;
                    pending.Write(buffer.AsSpan(segmentStart, i - segmentStart));
                    if (!VisitLine()) return false;
                    pending.Clear();
                    lineStart = blockStart + i + 1;
                    segmentStart = i + 1;
                    skipLf = next == '\r';
                }
                pending.Write(buffer.AsSpan(segmentStart, size - segmentStart));
                blockStart += size;
            }
            return pending.WrittenCount == 0 || VisitLine();
        }

        internal static void InvalidateLocked(string path)
        {
            IndexedHistory.Remove((path, true));
            IndexedHistory.Remove((path, false));
            IndexedSocialPrompts.Remove(path);
            IndexedAuthenticatedHistory.Remove(path);
        }

        private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);

        private readonly record struct FileIdentity(ulong Volume, ulong IdLow, ulong IdHigh);
        private readonly record struct FileVersion(FileIdentity Identity, long Length,
            long Changed, uint ChangedNanoseconds, long Modified, uint ModifiedNanoseconds);

        private static bool TryGetFileVersion(FileStream stream, out FileVersion version)
        {
            version = default;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    const int fileBasicInfo = 0;
                    const int fileIdInfo = 18;
                    var handle = stream.SafeFileHandle;
                    if (!ReadWindowsBasicInfo(handle, fileBasicInfo, out var basic, 40) || basic.ChangeTime == 0
                        || !ReadWindowsFileId(handle, fileIdInfo, out var id, 24) || (id.Low == 0 && id.High == 0)) return false;
                    version = new FileVersion(new FileIdentity(id.Volume, id.Low, id.High), stream.Length,
                        basic.ChangeTime, 0, basic.LastWriteTime, 0);
                    return true;
                }

                const uint requiredStatx = 0x3c1; // type, mtime, ctime, inode and size
                const int statxByDescriptor = 0x1000 | 0x2000; // AT_EMPTY_PATH | AT_STATX_FORCE_SYNC
                if (!OperatingSystem.IsLinux()
                    || ReadLinuxFileVersion(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), "",
                        statxByDescriptor, requiredStatx, out var linux) != 0
                    || (linux.Mask & requiredStatx) != requiredStatx || (linux.Mode & 0xf000) != 0x8000
                    || linux.Inode == 0 || linux.Size > long.MaxValue) return false;
                ulong device = ((ulong)linux.DeviceMajor << 32) | linux.DeviceMinor;
                version = new FileVersion(new FileIdentity(device, linux.Inode, 0), (long)linux.Size,
                    linux.ChangeTime, linux.ChangeNanoseconds, linux.WriteTime, linux.WriteNanoseconds);
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException)
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct WindowsBasicInfo
        {
            [FieldOffset(16)] internal long LastWriteTime;
            [FieldOffset(24)] internal long ChangeTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowsFileId
        {
            internal ulong Volume;
            internal ulong Low;
            internal ulong High;
        }

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct LinuxStatx
        {
            [FieldOffset(0)] internal uint Mask;
            [FieldOffset(28)] internal ushort Mode;
            [FieldOffset(32)] internal ulong Inode;
            [FieldOffset(40)] internal ulong Size;
            [FieldOffset(96)] internal long ChangeTime;
            [FieldOffset(104)] internal uint ChangeNanoseconds;
            [FieldOffset(112)] internal long WriteTime;
            [FieldOffset(120)] internal uint WriteNanoseconds;
            [FieldOffset(136)] internal uint DeviceMajor;
            [FieldOffset(140)] internal uint DeviceMinor;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadWindowsBasicInfo(SafeFileHandle handle, int kind, out WindowsBasicInfo info, uint size);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadWindowsFileId(SafeFileHandle handle, int kind, out WindowsFileId info, uint size);

        [DllImport("libc", EntryPoint = "statx")]
        private static extern int ReadLinuxFileVersion(int fd, string name, int flags, uint mask, out LinuxStatx info);
    }
}
