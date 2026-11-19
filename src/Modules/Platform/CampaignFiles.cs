using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class CampaignFileService
    {
        internal static string CharacterIdFrom(Dictionary<string, object> hero)
        {
            string value = ReadFirstString(hero, "heroStringId", "heroId", "characterId", "id");
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        internal static string CampaignDirectory(string campaignId)
        {
            PersistenceService.ThrowIfCampaignRequestReplaced();
            return Path.Combine(CampaignsRoot(), SafePathSegment(campaignId, "default"));
        }

        internal static string CampaignsRoot()
        {
            string overridden = Program.CampaignsRootOverride.Value;
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Program.DataDir, "campaigns")
                : Path.GetFullPath(overridden);
        }

        internal static bool IsInternalCampaignId(string campaignId)
        {
            string value = (campaignId ?? "").Trim();
            if (value.Length == 0) return true;
            if (value.Equals(".save-sync-work", StringComparison.OrdinalIgnoreCase))
                return true;
            if (value.StartsWith("__", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Equals("default", StringComparison.OrdinalIgnoreCase)
                || value.Equals("test_campaign", StringComparison.OrdinalIgnoreCase)
                || value.Equals("campaign_test", StringComparison.OrdinalIgnoreCase)
                || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                || value.Equals("reign_memory_lab", StringComparison.OrdinalIgnoreCase)
                || value.Equals("reign_director_lab", StringComparison.OrdinalIgnoreCase)
                || value.Equals("reign_rumor_lab", StringComparison.OrdinalIgnoreCase)) return true;
            return value.StartsWith("semantic_memory_test_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("_spymaster_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("_architecture_test_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("categorized_memory_test_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("world_history_selftest_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("world_test_selftest_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("mr_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("rp_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("sst_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("sss_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("ssp_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("mbti_materialize_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("mbti_batch_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("mbti_bulk_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("opening_seed_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("cadence_batch_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("cr_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("rc_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("rl_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("wh_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("whr_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("initialization_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("world_model_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("final-gauntlet-store-self-test-", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("_relationship_fixture_test_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("_shared_relationship_history_test_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("codex_", StringComparison.OrdinalIgnoreCase);
        }

        internal static void CleanupInternalCampaignArtifactsAtStartup()
        {
            string root = CampaignsRoot();
            ReignPostgreSqlStorage.ClearAllPools();
            // Internal campaigns still owned by another live server process (for example its
            // running self-tests) are kept; orphans of exited processes are removed.
            HashSet<string> liveOwned =
                ReignPostgreSqlStorage.DropInternalCampaignsWithoutLiveOwner(
                    ReignPostgreSqlStorage.ListCampaignMetadata()
                        .Select(registered => ReadString(
                            registered, "campaignId", ""))
                        .Where(IsInternalCampaignId)
                        .ToList());
            if (Directory.Exists(root))
            {
                foreach (DirectoryInfo directory
                    in new DirectoryInfo(root).GetDirectories())
                {
                    if (!IsInternalCampaignId(directory.Name)) continue;
                    if (liveOwned.Contains(directory.Name)) continue;
                    try { directory.Delete(true); } catch { }
                }
            }
        }

        internal static bool IsRealCampaignDirectory(DirectoryInfo directory)
        {
            if (directory == null || IsInternalCampaignId(directory.Name)) return false;
            if (directory.Name.IndexOf(".incoming_", StringComparison.OrdinalIgnoreCase) >= 0
                || directory.Name.IndexOf(".restore_", StringComparison.OrdinalIgnoreCase) >= 0
                || directory.Name.IndexOf(".deleted_", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            string metadataPath = Path.Combine(directory.FullName, "campaign.json");
            if (!File.Exists(metadataPath)) return false;
            Dictionary<string, object> metadata = ReadJsonObject(metadataPath);
            return !IsInternalCampaignId(ReadString(metadata, "campaignId", directory.Name));
        }

        internal static bool HasLocalCampaignMetadata(string campaignId)
        {
            if (string.IsNullOrWhiteSpace(campaignId)
                || IsInternalCampaignId(campaignId)) return false;
            string directoryPath = CampaignDirectory(campaignId);
            return Directory.Exists(directoryPath)
                && File.Exists(Path.Combine(directoryPath, "campaign.json"))
                && IsRealCampaignDirectory(new DirectoryInfo(directoryPath));
        }

        internal static bool HasRealCampaignStorage(string campaignId)
        {
            if (string.IsNullOrWhiteSpace(campaignId)) return false;
            try
            {
                string directoryPath = CampaignDirectory(campaignId);
                if (!Directory.Exists(directoryPath)
                    || !IsRealCampaignDirectory(
                        new DirectoryInfo(directoryPath))) return false;
                return ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                    ReadString(row, "campaignId", "")
                        .Equals(
                            campaignId,
                            StringComparison.OrdinalIgnoreCase)
                    && !IsInternalCampaignId(campaignId));
            }
            catch { return false; }
        }

        internal static string CampaignFile(string campaignId, params string[] segments)
        {
            List<string> parts = new List<string> { CampaignDirectory(campaignId) };
            parts.AddRange(segments ?? new string[0]);
            return Path.Combine(parts.ToArray());
        }

        internal static string CharacterDirectory(string campaignId, string heroId)
        {
            return Path.Combine(CampaignDirectory(campaignId), "characters", SafePathSegment(heroId, "unknown"));
        }

        internal static string CharacterFile(string campaignId, string heroId, params string[] segments)
        {
            List<string> parts = new List<string> { CharacterDirectory(campaignId, heroId) };
            parts.AddRange(segments ?? new string[0]);
            return Path.Combine(parts.ToArray());
        }

        internal static string SafePathSegment(string value, string fallback)
        {
            string clean = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                clean = clean.Replace(invalid, '_');
            }

            return string.IsNullOrWhiteSpace(clean) ? fallback : clean;
        }

        internal static string ResolveLogCampaignId(string campaignId)
        {
            if (!string.IsNullOrWhiteSpace(campaignId))
            {
                return campaignId;
            }

            string campaignsRoot = CampaignsRoot();
            if (!Directory.Exists(campaignsRoot))
            {
                return "default";
            }

            DirectoryInfo latest = new DirectoryInfo(campaignsRoot)
                .GetDirectories()
                .Where(IsRealCampaignDirectory)
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .FirstOrDefault();
            return latest == null ? "default" : latest.Name;
        }

        internal static List<Dictionary<string, object>> ReadCampaignMemoryLines(string campaignId, List<string> actorIds)
        {
            Dictionary<string, Dictionary<string, object>> byId = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            AddMemoryRows(byId, Program.ReadJsonLinesFromPath(CampaignFile(campaignId, "world", "memories.jsonl")));

            foreach (string actorId in actorIds ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(actorId))
                {
                    AddMemoryRows(byId, Program.ReadJsonLinesFromPath(CharacterFile(campaignId, actorId, "memory", "memories.jsonl")));
                }
            }

            return byId.Values.ToList();
        }

        private static void AddMemoryRows(Dictionary<string, Dictionary<string, object>> byId, List<Dictionary<string, object>> rows)
        {
            foreach (Dictionary<string, object> row in rows ?? new List<Dictionary<string, object>>())
            {
                if (!DurableDialogueMemoryRowIsUsable(row))
                    continue;
                string id = ReadString(row, "id", "");
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = ReadString(row, "ownerId", "") + ":" + ReadLong(row, "ts", 0) + ":" + ReadString(row, "text", "").GetHashCode();
                }

                byId[id] = row;
            }
        }

        internal static bool DurableDialogueMemoryRowIsUsable(Dictionary<string, object> row)
        {
            if (row == null)
                return false;
            string source = ActionPipelineService.NormalizeLookup(ReadString(row, "source", ""));
            if (!source.Equals("dialogue llm", StringComparison.OrdinalIgnoreCase))
                return true;
            string text = FirstNonEmpty(
                ReadFirstString(row, "text", "summary", "memory", "content"), "");
            return !Regex.IsMatch((text ?? "").Trim(),
                @"^(?:[+-]?(?:\d+(?:\.\d+)?|\.\d+)|true|false|null|undefined)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        internal static void WriteCampaignMetadata(string campaignId, Dictionary<string, object> profile)
        {
            lock (Program.FileLock)
            {
                string path = CampaignFile(campaignId, "campaign.json");
                Dictionary<string, object> meta = ReadJsonObject(path);
                meta["campaignId"] = campaignId;
                meta["folder"] = SafePathSegment(campaignId, "default");
                meta["updatedUtc"] = DateTime.UtcNow.ToString("o");
                if (profile.ContainsKey("narrativeVersion")) meta["narrativeVersion"] = ReadInt(profile, "narrativeVersion", 0);
                CopyNonEmpty(profile, meta, "campaignLabel");
                CopyNonEmpty(profile, meta, "mainHeroStringId");
                CopyNonEmpty(profile, meta, "mainHeroName");
                CopyNonEmpty(profile, meta, "playerClanId");
                CopyNonEmpty(profile, meta, "playerClanName");
                CopyNonEmpty(profile, meta, "playerKingdomId");
                CopyNonEmpty(profile, meta, "playerKingdomName");
                WriteJsonObject(path, meta);
            }
        }

        private static void CopyNonEmpty(Dictionary<string, object> source, Dictionary<string, object> target, string key)
        {
            string value = ReadString(source, key, "");
            if (!string.IsNullOrWhiteSpace(value))
            {
                target[key] = value;
            }
        }

        internal static Dictionary<string, object> DefaultCharacteristics(Dictionary<string, object> hero)
        {
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["source"] = "reignbeta_default",
                ["personality"] = "",
                ["speechStyle"] = "",
                ["ambitions"] = new ArrayList(),
                ["loyalties"] = new ArrayList(),
                ["fears"] = new ArrayList(),
                ["grudges"] = new ArrayList(),
                ["traitHints"] = hero != null && hero.ContainsKey("traits") ? hero["traits"] : new Dictionary<string, object>()
            };
        }

        internal static Dictionary<string, object> DefaultInventory(Dictionary<string, object> hero)
        {
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["source"] = "none",
                ["partyId"] = ReadString(hero, "partyId", ""),
                ["totalValue"] = 0,
                ["tradeGoodsValue"] = 0,
                ["totalFood"] = 0,
                ["foodVariety"] = 0,
                ["mounts"] = 0,
                ["packAnimals"] = 0,
                ["livestock"] = 0,
                ["topItems"] = new ArrayList()
            };
        }

        internal static Dictionary<string, object> DefaultAppearance(Dictionary<string, object> hero)
        {
            string trueStatus = ReadBool(hero, "isLord", false)
                ? "noble or very wealthy"
                : ReadBool(hero, "isNotable", false)
                    ? "respectable or prosperous"
                    : "ordinary";
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["source"] = "none",
                ["trueStatusScore"] = ReadBool(hero, "isLord", false) ? 3 : ReadBool(hero, "isNotable", false) ? 1 : 0,
                ["trueStatusLabel"] = trueStatus,
                ["visibleStatusScore"] = 0,
                ["visibleStatusLabel"] = "unknown",
                ["statusMismatch"] = 0,
                ["presentationEffect"] = "unknown",
                ["firstView"] = "No equipment-based first view has been captured yet.",
                ["civilianEquipmentValue"] = 0,
                ["battleEquipmentValue"] = 0,
                ["civilianEquipment"] = new ArrayList(),
                ["battleEquipment"] = new ArrayList()
            };
        }

        internal static Dictionary<string, object> DefaultCharacterState(Dictionary<string, object> hero)
        {
            return new Dictionary<string, object>
            {
                ["version"] = 1,
                ["mood"] = "",
                ["currentPlan"] = "",
                ["currentCrisis"] = "",
                ["politicalAddressConvention"] =
                    new Dictionary<string, object>
                    {
                        ["convention"] = "formal_default",
                        ["sourceExchangeId"] = "",
                        ["worldDay"] = 0d,
                        ["source"] = "default"
                    },
                ["lastKnownSettlementId"] = ReadString(hero, "currentSettlementId", ""),
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            };
        }

        internal static void UpdateCharacterIndex(string campaignId, Dictionary<string, object> profile)
        {
            UpdateCharacterIndexBatch(campaignId, new[] { profile });
        }

        internal static void UpdateCharacterIndexBatch(
            string campaignId,
            IEnumerable<Dictionary<string, object>> profiles)
        {
            string charactersDir = Path.Combine(CampaignDirectory(campaignId), "characters");
            Directory.CreateDirectory(charactersDir);
            string indexPath = Path.Combine(charactersDir, "index.json");
            List<Dictionary<string, object>> incoming = (profiles
                    ?? Enumerable.Empty<Dictionary<string, object>>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(CharacterIdFrom(x)))
                .GroupBy(CharacterIdFrom, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Last())
                .ToList();
            if (incoming.Count == 0) return;

            lock (Program.FileLock)
            {
                HashSet<string> replaced = new HashSet<string>(
                    incoming.Select(CharacterIdFrom),
                    StringComparer.OrdinalIgnoreCase);
                ArrayList index = ReadJsonArray(indexPath);
                ArrayList next = new ArrayList();
                foreach (object item in index)
                {
                    Dictionary<string, object> existing = item as Dictionary<string, object>;
                    if (existing == null || !replaced.Contains(CharacterIdFrom(existing)))
                    {
                        next.Add(item);
                    }
                }

                string updatedUtc = DateTime.UtcNow.ToString("o");
                foreach (Dictionary<string, object> profile in incoming)
                {
                    string heroId = CharacterIdFrom(profile);
                    next.Add(new Dictionary<string, object>
                    {
                        ["heroStringId"] = heroId,
                        ["name"] = ReadString(profile, "name", heroId),
                        ["clanId"] = ReadString(profile, "clanId", ""),
                        ["kingdomId"] = ReadString(profile, "kingdomId", ""),
                        ["updatedUtc"] = updatedUtc
                    });
                }
                WriteJsonObject(indexPath, next);
            }
        }

        internal static Dictionary<string, object> ReadJsonObject(string path)
        {
            lock (Program.FileLock)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        return new Dictionary<string, object>();
                    }

                    string text = File.ReadAllText(path, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return new Dictionary<string, object>();
                    }

                    return Json.Deserialize<Dictionary<string, object>>(text) ?? new Dictionary<string, object>();
                }
                catch
                {
                    return new Dictionary<string, object>();
                }
            }
        }

        internal static ArrayList ReadJsonArray(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new ArrayList();
                }

                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return new ArrayList();
                }

                return Json.Deserialize<ArrayList>(text) ?? new ArrayList();
            }
            catch
            {
                return new ArrayList();
            }
        }

        internal static void WriteJsonObject(string path, object value)
        {
            PersistenceService.ThrowIfCampaignRequestReplaced();
            lock (Program.FileLock)
            {
                string directory = Path.GetDirectoryName(path);
                string serialized = Json.Serialize(value);
                for (int attempt = 0; ; attempt++)
                {
                    Directory.CreateDirectory(directory);
                    // Use a short same-directory name so installed Bannerlord paths
                    // remain below the legacy Win32 path limit.
                    string temp = Path.Combine(
                        directory,
                        "~" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    try
                    {
                        File.WriteAllText(temp, serialized, new UTF8Encoding(false));
                        if (File.Exists(path))
                        {
                            const uint moveFileReplaceExisting = 0x1;
                            const uint moveFileWriteThrough = 0x8;
                            if (!PersistenceService.MoveFileEx(temp, path,
                                moveFileReplaceExisting | moveFileWriteThrough))
                                throw new IOException(
                                    "Atomic JSON replacement failed.",
                                    new System.ComponentModel.Win32Exception(
                                        System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
                        }
                        else File.Move(temp, path);
                        break;
                    }
                    catch (DirectoryNotFoundException) when (attempt < 7)
                    {
                        // Save Sync and verification sandboxes replace whole campaign
                        // directories atomically. Retry if the leaf disappears between
                        // the write and replace.
                        Thread.Yield();
                    }
                    catch (IOException) when (attempt < 20)
                    {
                        // Another Reign process (for example a parallel verification
                        // fixture or the vector worker) can briefly have the destination
                        // open. Keep atomic replacement semantics and retry the bounded
                        // transient sharing violation instead of corrupting the profile.
                        Thread.Sleep(Math.Min(100, 5 * (attempt + 1)));
                    }
                    catch (UnauthorizedAccessException) when (attempt < 20)
                    {
                        Thread.Sleep(Math.Min(100, 5 * (attempt + 1)));
                    }
                    finally { PersistenceService.TryDeleteFile(temp); }
                }
            }
            PersistenceService.MarkSaveSyncActiveStateDirtyForPath(path);
        }

        internal static void EnsureJsonObjectFile(string path, Dictionary<string, object> fallback)
        {
            PersistenceService.ThrowIfCampaignRequestReplaced();
            lock (Program.FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, Json.Serialize(fallback ?? new Dictionary<string, object>()), Encoding.UTF8);
                }
            }
        }

        internal static void EnsureFile(string path)
        {
            PersistenceService.ThrowIfCampaignRequestReplaced();
            lock (Program.FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, string.Empty, Encoding.UTF8);
                }
            }
        }

        internal static void AppendJsonLineToPath(string path, Dictionary<string, object> row)
        {
            PersistenceService.ThrowIfCampaignRequestReplaced();
            lock (Program.FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, Json.Serialize(row ?? new Dictionary<string, object>()) + Environment.NewLine, Encoding.UTF8);
            }
            PersistenceService.MarkSaveSyncActiveStateDirtyForPath(path);
        }

        internal static void AppendBoundedJsonLineToPath(
            string path,
            Dictionary<string, object> row,
            long maximumBytes,
            long retainedBytes)
        {
            lock (Program.FileLock)
            {
                AppendBoundedJsonLineToPathLocked(path, row, maximumBytes, retainedBytes);
            }
            PersistenceService.MarkSaveSyncActiveStateDirtyForPath(path);
        }

        internal static void AppendBoundedJsonLineToPathLocked(
            string path,
            Dictionary<string, object> row,
            long maximumBytes,
            long retainedBytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            CompactTextLogTail(path, maximumBytes, retainedBytes);
            File.AppendAllText(path, Json.Serialize(row ?? new Dictionary<string, object>()) + Environment.NewLine, Encoding.UTF8);
        }

        private static void CompactTextLogTail(string path, long maximumBytes, long retainedBytes)
        {
            if (!File.Exists(path)) return;
            FileInfo info = new FileInfo(path);
            if (info.Length <= Math.Max(1024L, maximumBytes)) return;
            long keep = Math.Max(1024L, Math.Min(retainedBytes, maximumBytes));
            int bufferLength = (int)Math.Min(int.MaxValue, Math.Min(info.Length, keep + 4096L));
            byte[] buffer = new byte[bufferLength];
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                stream.Seek(-bufferLength, SeekOrigin.End);
                int read = 0;
                while (read < buffer.Length)
                {
                    int amount = stream.Read(buffer, read, buffer.Length - read);
                    if (amount <= 0) break;
                    read += amount;
                }
                int start = 0;
                if (info.Length > bufferLength)
                {
                    while (start < read && buffer[start] != (byte)'\n') start++;
                    if (start < read) start++;
                }
                stream.SetLength(0);
                stream.Position = 0;
                stream.Write(buffer, start, Math.Max(0, read - start));
                stream.Flush(true);
            }
        }

        internal static void CompactOperationalLogsAtStartup()
        {
            lock (Program.FileLock)
            {
                Directory.CreateDirectory(Program.LogsDir);
                CompactTextLogTail(Path.Combine(Program.LogsDir, "server-log.jsonl"),
                    16L * 1024L * 1024L, 8L * 1024L * 1024L);
                CompactTextLogTail(Path.Combine(Program.LogsDir, "llm-log.jsonl"),
                    16L * 1024L * 1024L, 8L * 1024L * 1024L);
            }
        }

        internal static List<string> SimpleTags(string text)
        {
            string lowered = (text ?? "").ToLowerInvariant();
            string[] candidates = { "war", "peace", "tribute", "siege", "army", "betrayal", "oath", "marriage", "fief", "ransom", "trade", "recruit" };
            return candidates.Where(lowered.Contains).ToList();
        }

        internal static Dictionary<string, object> RelationshipStatusPayload(Program.HttpRequest request)
        {
            if (request.Method == "POST") return request.JsonBody ?? new Dictionary<string, object>();
            var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in request.Query ?? new Dictionary<string, string>()) payload[pair.Key] = pair.Value;
            return payload;
        }
    }
}
