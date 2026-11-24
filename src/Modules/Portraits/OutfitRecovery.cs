using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReignBetaServer
{
    internal static partial class PortraitsService
    {
        private const string OutfitFileJournalSchema = "reign-outfit-file-journal-v1";
        private const string OutfitFileJournalName = "active.json";
        private const string OutfitFilesPrepared = "prepared";
        private const string OutfitFilesCommitted = "committed";

        // The journal stores roles, never paths supplied by a retained manifest.
        private static readonly string[] OutfitFileRoles =
        {
            "portraits/custom.png",
            "portraits/formal_outfit.json",
            "portraits/outfit.json",
            "portraits/" + OutfitPortraitReceiptFileName,
            "profile.json",
            "portraits/" + OutfitConfirmationFileName
        };

        private static readonly JsonSerializerOptions OutfitJournalJson = new()
        {
            AllowDuplicateProperties = false,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        private sealed record OutfitJournalEntry(string Role, string BeforeSha256, string AfterSha256, UnixFileMode? BeforeMode);
        private sealed record OutfitFileJournal(string Schema, string CampaignId, string HeroId, string OperationId,
            string PlayerId, string TimelineId, string SourceSha256, string PublicationId, string Phase, OutfitJournalEntry[] Entries);
        private sealed record OutfitFileSettlement(bool Committed, bool CleanupPending);

        private static string OutfitRolePath(string characterRoot, string role) =>
            Path.Combine(characterRoot, role.Replace('/', Path.DirectorySeparatorChar));

        private static string OutfitBeforePath(string journalRoot, OutfitFileJournal journal, int index) =>
            Path.Combine(journalRoot, journal.PublicationId, "before-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin");

        private static string OutfitHashOrAbsent(byte[] bytes) => bytes == null ? null : Sha256Hex(bytes);

        private static bool OutfitJournalHash(string value) =>
            value != null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

        private static bool OutfitActiveFileExists(string path)
        {
            try
            {
                const FileAttributes invalid = FileAttributes.Directory | FileAttributes.ReparsePoint;
                if ((File.GetAttributes(path) & invalid) != 0)
                    throw new InvalidDataException("The active outfit journal is not an ordinary file.");
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static void TryRemoveOwnedOutfitPreimages(string root, OutfitFileJournal journal)
        {
            for (int i = 0; i < OutfitFileRoles.Length; i++)
                PersistenceService.TryDeleteFile(OutfitBeforePath(root, journal, i));
            try { Directory.Delete(Path.Combine(root, journal.PublicationId), recursive: false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Caller must own the physical campaign/product roots and keep the campaign fenced
        // until the active manifest has been settled. The same role order is used for rollback.
        private static OutfitFileJournal StageOutfitFileJournal(string journalRoot, string characterRoot,
            string campaign, string hero, string operation, string player, string timeline, string sourceSha256,
            IReadOnlyList<KeyValuePair<string, byte[]>> writes)
        {
            if (!OutfitSafeId(campaign) || campaign == "_shared" || campaign != campaign.Trim() || !OutfitSafeId(hero) || hero != hero.Trim()
                || !Guid.TryParseExact(operation, "N", out _) || !OutfitSafeId(player) || !OutfitSafeId(timeline)
                || !OutfitJournalHash(sourceSha256) || writes.Count != OutfitFileRoles.Length
                || writes.Where((entry, i) => entry.Key != OutfitRolePath(characterRoot, OutfitFileRoles[i]) || entry.Value == null).Any())
                throw new InvalidDataException("Outfit publication does not match the known file roles.");

            string publication = Guid.NewGuid().ToString("N");
            var entries = new OutfitJournalEntry[OutfitFileRoles.Length];
            var intent = new OutfitFileJournal(OutfitFileJournalSchema, campaign, hero, operation, player, timeline, sourceSha256,
                publication, OutfitFilesPrepared, entries);
            var active = Path.Combine(journalRoot, OutfitFileJournalName);
            if (OutfitActiveFileExists(active))
                throw new InvalidOperationException("An outfit publication already needs settlement.");

            try
            {
                for (int i = 0; i < writes.Count; i++)
                {
                    byte[] before = ReadOutfitFileOrAbsent(writes[i].Key);
                    UnixFileMode? mode = before != null && OperatingSystem.IsLinux() ? File.GetUnixFileMode(writes[i].Key) : null;
                    if (mode.HasValue && ((int)mode.Value & ~511) != 0)
                        throw new InvalidDataException("An outfit file has unsupported permission bits.");
                    entries[i] = new OutfitJournalEntry(OutfitFileRoles[i], OutfitHashOrAbsent(before), Sha256Hex(writes[i].Value), mode);
                    if (before != null)
                        PersistenceService.WriteRecoveryFile(OutfitBeforePath(journalRoot, intent, i), before, replace: false);
                }
                PersistenceService.WriteRecoveryFile(active, JsonSerializer.SerializeToUtf8Bytes(intent), replace: false);
                return intent;
            }
            catch
            {
                // A failed publication can be ambiguous. Remove only this known generation
                // and only when there is no published active manifest.
                try { if (!OutfitActiveFileExists(active)) TryRemoveOwnedOutfitPreimages(journalRoot, intent); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        private static OutfitFileJournal ReadOutfitFileJournal(string journalRoot, string campaign, string hero)
        {
            var active = Path.Combine(journalRoot, OutfitFileJournalName);
            if (!OutfitActiveFileExists(active))
                throw new FileNotFoundException("The active outfit journal disappeared.", active);
            using var source = new FileStream(active, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length <= 0 || source.Length > 32768)
                throw new InvalidDataException("The retained outfit journal has an invalid size.");
            var journal = JsonSerializer.Deserialize<OutfitFileJournal>(source, OutfitJournalJson);
            if (journal == null || journal.Schema != OutfitFileJournalSchema || journal.CampaignId != campaign || journal.HeroId != hero
                || !Guid.TryParseExact(journal.OperationId, "N", out _) || !OutfitSafeId(journal.PlayerId)
                || !OutfitSafeId(journal.TimelineId) || !OutfitJournalHash(journal.SourceSha256)
                || !Guid.TryParseExact(journal.PublicationId, "N", out Guid publication) || journal.PublicationId != publication.ToString("N")
                || (journal.Phase != OutfitFilesPrepared && journal.Phase != OutfitFilesCommitted)
                || journal.Entries == null || journal.Entries.Length != OutfitFileRoles.Length
                || journal.Entries.Where((entry, i) => entry == null || entry.Role != OutfitFileRoles[i]
                    || (entry.BeforeSha256 != null && !OutfitJournalHash(entry.BeforeSha256)) || !OutfitJournalHash(entry.AfterSha256)
                    || (entry.BeforeMode.HasValue && (entry.BeforeSha256 == null || ((int)entry.BeforeMode.Value & ~511) != 0))).Any())
                throw new InvalidDataException("The retained outfit file journal is invalid.");
            return journal;
        }

        // Called after the live writer has installed all six roles. Once the committed
        // manifest is durable, recovery never replaces the committed product with old bytes.
        private static void CommitOutfitFileJournal(string journalRoot, string characterRoot, OutfitFileJournal journal)
        {
            ValidateOutfitJournalLiveFiles(characterRoot, journal, allowBefore: false);
            var committed = journal with { Phase = OutfitFilesCommitted };
            PersistenceService.WriteRecoveryFile(Path.Combine(journalRoot, OutfitFileJournalName),
                JsonSerializer.SerializeToUtf8Bytes(committed));
        }

        private static void ValidateOutfitJournalLiveFiles(string characterRoot, OutfitFileJournal journal, bool allowBefore)
        {
            for (int i = 0; i < OutfitFileRoles.Length; i++)
            {
                var entry = journal.Entries[i];
                string actual = OutfitHashOrAbsent(ReadOutfitFileOrAbsent(OutfitRolePath(characterRoot, entry.Role)));
                if (actual != entry.AfterSha256 && (!allowBefore || actual != entry.BeforeSha256))
                    throw new InvalidDataException("The retained outfit journal no longer owns the " + entry.Role + " bytes.");
            }
        }

        private static OutfitFileSettlement SettleOutfitFileJournal(string root, string journalRoot, string characterRoot,
            string campaign, string hero, string operation)
        {
            if (!RequireOutfitPhysicalPath(root, Path.Combine(journalRoot, OutfitFileJournalName), directory: false))
                throw new FileNotFoundException("The fenced outfit journal disappeared.");
            var journal = ReadOutfitFileJournal(journalRoot, campaign, hero);
            if (journal.OperationId != operation) throw new InvalidDataException("Outfit recovery operation identity changed.");
            foreach (string role in OutfitFileRoles)
                RequireOutfitPhysicalPath(root, OutfitRolePath(characterRoot, role), directory: false);
            bool committed = journal.Phase == OutfitFilesCommitted;
            ValidateOutfitJournalLiveFiles(characterRoot, journal, allowBefore: !committed);
            if (!committed)
            {
                var preimages = new byte[OutfitFileRoles.Length][];
                for (int i = 0; i < preimages.Length; i++)
                {
                    if (journal.Entries[i].BeforeSha256 == null) continue;
                    string beforePath = OutfitBeforePath(journalRoot, journal, i);
                    if (!RequireOutfitPhysicalPath(root, beforePath, directory: false))
                        throw new FileNotFoundException("The owned outfit before-image is missing.");
                    byte[] bytes = File.ReadAllBytes(beforePath);
                    if (Sha256Hex(bytes) != journal.Entries[i].BeforeSha256)
                        throw new InvalidDataException("A retained outfit file preimage is unavailable or has changed.");
                    preimages[i] = bytes;
                }

                var failures = new List<Exception>();
                for (int i = preimages.Length - 1; i >= 0; i--)
                {
                    var entry = journal.Entries[i];
                    string target = OutfitRolePath(characterRoot, entry.Role);
                    string actual = OutfitHashOrAbsent(ReadOutfitFileOrAbsent(target));
                    if (actual == entry.BeforeSha256) continue;
                    if (actual != entry.AfterSha256)
                        throw new InvalidDataException("The outfit rollback no longer owns the " + entry.Role + " bytes.");
                    try
                    {
                        if (preimages[i] == null) File.Delete(target);
                        else PersistenceService.WriteRecoveryFile(target, preimages[i], mode: entry.BeforeMode);
                    }
                    catch (IOException ex) { failures.Add(ex); }
                    catch (UnauthorizedAccessException ex) { failures.Add(ex); }
                }
                if (failures.Count != 0)
                    throw new AggregateException("The outfit rollback remains pending.", failures);
                for (int i = 0; i < preimages.Length; i++)
                {
                    string actual = OutfitHashOrAbsent(ReadOutfitFileOrAbsent(OutfitRolePath(characterRoot, journal.Entries[i].Role)));
                    if (actual != journal.Entries[i].BeforeSha256)
                        throw new IOException("Outfit rollback did not restore the retained file set.");
                }
            }

            // Unlink the trusted manifest before removing its payload. A cleanup failure
            // leaves only unreferenced bytes. It must not turn the settled product into a retry.
            try { File.Delete(Path.Combine(journalRoot, OutfitFileJournalName)); }
            catch (IOException) { return new OutfitFileSettlement(committed, CleanupPending: true); }
            catch (UnauthorizedAccessException) { return new OutfitFileSettlement(committed, CleanupPending: true); }
            TryRemoveOwnedOutfitPreimages(journalRoot, journal);
            return new OutfitFileSettlement(committed, CleanupPending: false);
        }

        private sealed record OutfitRecoveryFence(string Campaign, string Hero, string Journal, string Operation, bool Ambiguous = false);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, OutfitRecoveryFence> OutfitRecoveryFences =
            new(StringComparer.Ordinal);

        private static string OutfitJournalsRoot(string campaignsRoot) =>
            Path.Combine(campaignsRoot, ".save-sync-work", "outfits");

        private static string OutfitCampaignJournalRoot(string campaignsRoot, string campaign) =>
            Path.Combine(OutfitJournalsRoot(campaignsRoot), campaign);

        private static string OutfitCharacterRoot(string campaignsRoot, string campaign, string hero) =>
            Path.Combine(campaignsRoot, campaign, "characters", hero);

        private static bool IsOutfitStorageId(string id) => OutfitSafeId(id) && id == id.Trim()
            && id != "_shared" && !id.Equals(".save-sync-work", StringComparison.OrdinalIgnoreCase);

        private static string OutfitFenceKey(string campaignsRoot, string campaign) =>
            Path.GetFullPath(OutfitCampaignJournalRoot(campaignsRoot, campaign));

        internal static bool HasOutfitRecoveryFences => !OutfitRecoveryFences.IsEmpty;

        internal static IEnumerable<string> FencedOutfitCampaigns() => OutfitRecoveryFences
            .Where(pair => Path.GetDirectoryName(pair.Key) == OutfitJournalsRoot(CampaignFileService.CampaignsRoot()))
            .Select(pair => pair.Value.Campaign).ToArray();

        internal static Dictionary<string, object> OutfitRecoveryStatus(string campaign)
        {
            if (!IsOutfitStorageId(campaign) || !TryFindOutfitFence(CampaignFileService.CampaignsRoot(), campaign, out var fence)) return null;
            return TestDict("ok", false, "result", "recovery_required", "outcome", "recovery_required",
                "status", "recovery_required", "storageCategory", "recovery_required", "eligibleForPermanentDeletion", false,
                "campaignId", campaign, "backupId", fence.Operation, "operationId", fence.Operation,
                "journalKind", "outfit", "journalPath", Path.Combine(fence.Journal, OutfitFileJournalName),
                "error", "An interrupted outfit publication needs recovery before this campaign can be changed.");
        }

        private static bool TryFindOutfitFence(string root, string campaign, out OutfitRecoveryFence fence)
        {
            if (OutfitRecoveryFences.TryGetValue(OutfitFenceKey(root, campaign), out fence)) return true;
            // Distinct Linux spellings are distinct owners. A spelling that resolves
            // only through a folded directory still shares the physical owner's fence.
            string journalRoot = OutfitJournalsRoot(root);
            var folded = OutfitRecoveryFences.FirstOrDefault(pair => Path.GetDirectoryName(pair.Key) == journalRoot
                && pair.Value.Campaign.Equals(campaign, StringComparison.OrdinalIgnoreCase));
            if (folded.Value == null) return false;
            try { RequireOutfitPhysicalPath(root, Path.Combine(root, campaign), directory: true); }
            catch (InvalidDataException) { fence = folded.Value; return true; }
            return false;
        }

        // The configured runtime root may be mounted elsewhere. Below that root, owned
        // directories and files must have their exact spelling and never traverse a link.
        private static bool RequireOutfitPhysicalPath(string root, string path, bool directory)
        {
            string relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Outfit storage escaped its physical root.");
            try
            {
                var rootAttributes = File.GetAttributes(root);
                if ((rootAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new InvalidDataException("Outfit storage root is not an ordinary directory.");
                if (relative == ".") return directory;
                string[] parts = relative.Split(Path.DirectorySeparatorChar);
                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i];
                    string candidate = Path.Combine(root, part);
                    var attributes = File.GetAttributes(candidate);
                    if (!Directory.EnumerateFileSystemEntries(root).Any(entry => Path.GetFileName(entry) == part))
                        throw new InvalidDataException("Outfit storage resolves through a differently spelled directory entry.");
                    root = candidate;
                    var expected = i < parts.Length - 1 || directory ? FileAttributes.Directory : 0;
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != expected)
                        throw new InvalidDataException("Outfit storage has an unexpected path kind.");
                }
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        internal static FileStream AcquireOutfitRuntimeOwnership(string dataRoot)
        {
            Directory.CreateDirectory(dataRoot);
            var path = Path.Combine(dataRoot, ".campaign-runtime-owner");
            RequireOutfitPhysicalPath(dataRoot, path, directory: false);
            // Keep the inode permanently. Separate open file descriptions serialize server start
            // and offline mutators, including when a hosted server uses a different port.
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private static IEnumerable<OutfitRecoveryFence> ReadOutfitCampaignFences(string campaignsRoot, string campaignRoot)
        {
            string campaign = Path.GetFileName(campaignRoot);
            if (!IsOutfitStorageId(campaign) || !RequireOutfitPhysicalPath(campaignsRoot, campaignRoot, directory: true))
                throw new InvalidDataException("Outfit campaign journal ownership is ambiguous.");
            foreach (string heroRoot in Directory.EnumerateFileSystemEntries(campaignRoot))
            {
                string hero = Path.GetFileName(heroRoot);
                if (!IsOutfitStorageId(hero) || !RequireOutfitPhysicalPath(campaignsRoot, heroRoot, directory: true))
                    throw new InvalidDataException("Outfit character journal ownership is ambiguous.");
                string active = Path.Combine(heroRoot, OutfitFileJournalName);
                if (!RequireOutfitPhysicalPath(campaignsRoot, active, directory: false)) continue;

                OutfitFileJournal journal;
                try { journal = ReadOutfitFileJournal(heroRoot, campaign, hero); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    journal = null;
                }
                yield return new OutfitRecoveryFence(campaign, hero, heroRoot, journal?.OperationId ?? "unreadable", journal == null);
            }
        }

        private static IEnumerable<string> OutfitCampaignJournalDirectories(string campaignsRoot)
        {
            var root = OutfitJournalsRoot(campaignsRoot);
            return RequireOutfitPhysicalPath(campaignsRoot, root, directory: true)
                ? Directory.EnumerateFileSystemEntries(root) : Array.Empty<string>();
        }

        internal static void RequireOfflineOutfitCampaignAvailable(string campaignsRoot, string campaign)
        {
            // Live campaigns and local recovery work may be on mounts with different casing rules.
            // Reject aliases at the live root before treating a missing journal spelling as absence.
            RequireOutfitPhysicalPath(campaignsRoot, Path.Combine(campaignsRoot, campaign), directory: true);
            string path = OutfitCampaignJournalRoot(campaignsRoot, campaign);
            if (RequireOutfitPhysicalPath(campaignsRoot, path, directory: true)
                && ReadOutfitCampaignFences(campaignsRoot, path).Any())
                throw new InvalidOperationException("Outfit recovery is pending for campaign " + campaign + ".");
        }

        internal static bool AnyOfflineOutfitPublication(string campaignsRoot) =>
            OutfitCampaignJournalDirectories(campaignsRoot).Any(path => ReadOutfitCampaignFences(campaignsRoot, path).Any());

        internal static void DiscoverOutfitPublicationsAtStartup()
        {
            string campaignsRoot = CampaignFileService.CampaignsRoot();
            foreach (string path in OutfitCampaignJournalDirectories(campaignsRoot))
            {
                string campaign = Path.GetFileName(path);
                OutfitRecoveryFence[] active;
                try { active = ReadOutfitCampaignFences(campaignsRoot, path).ToArray(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    if (!IsOutfitStorageId(campaign)) throw;
                    active = new[] { new OutfitRecoveryFence(campaign, "", path, "unreadable", Ambiguous: true) };
                }
                if (active.Length == 0) continue;
                var fence = active.Length == 1 ? active[0] : active[0] with { Ambiguous = true };
                OutfitRecoveryFences.AddOrUpdate(OutfitFenceKey(campaignsRoot, campaign), fence,
                    (_, existing) => existing with { Ambiguous = true });
            }
        }

        internal static void RecoverOutfitPublicationsAtStartup()
        {
            string campaignsRoot = CampaignFileService.CampaignsRoot();
            lock (Program.FileLock)
            {
                foreach (var fence in OutfitRecoveryFences.Values)
                {
                    if (fence.Ambiguous || PersistenceService.HasSaveSyncJournalFence(fence.Campaign)) continue;
                    TrySettleOutfitFence(campaignsRoot, fence);
                }
            }
        }

        private static OutfitFileSettlement TrySettleOutfitFence(string campaignsRoot, OutfitRecoveryFence fence)
        {
            try
            {
                var characterRoot = OutfitCharacterRoot(campaignsRoot, fence.Campaign, fence.Hero);
                var settled = SettleOutfitFileJournal(campaignsRoot, fence.Journal, characterRoot,
                    fence.Campaign, fence.Hero, fence.Operation);
                if (!settled.CleanupPending)
                    OutfitRecoveryFences.TryRemove(new KeyValuePair<string, OutfitRecoveryFence>(
                        OutfitFenceKey(campaignsRoot, fence.Campaign), fence));
                return settled;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or AggregateException)
            {
                try
                {
                    OperationsService.LogOperational("outfit.recovery_pending", TestDict(
                        "campaignId", fence.Campaign, "operationId", fence.Operation, "error", ex.Message));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return null;
            }
        }

        private sealed class OutfitRecoveryPendingException : IOException
        {
            internal OutfitRecoveryPendingException(Exception inner) : base("The outfit result needs recovery.", inner) { }
        }

        private static bool CommitOutfitWithJournal(OutfitChangeOperation op, string characterRoot,
            IReadOnlyList<KeyValuePair<string, byte[]>> writes, Action markProfileDirty)
        {
            string campaignsRoot = CampaignFileService.CampaignsRoot();
            if (!IsOutfitStorageId(op.Campaign) || !IsOutfitStorageId(op.Hero)
                || characterRoot != OutfitCharacterRoot(campaignsRoot, op.Campaign, op.Hero))
                throw new InvalidDataException("The outfit destination is not an exact campaign path.");
            string journalRoot = Path.Combine(OutfitCampaignJournalRoot(campaignsRoot, op.Campaign), op.Hero);
            var fence = new OutfitRecoveryFence(op.Campaign, op.Hero, journalRoot, op.Id);
            string key = OutfitFenceKey(campaignsRoot, op.Campaign);
            string active = Path.Combine(journalRoot, OutfitFileJournalName);
            PersistenceService.RequireSaveSyncCampaignAvailable(op.Campaign);
            if (RequireOutfitPhysicalPath(campaignsRoot, active, directory: false))
                throw new InvalidDataException("An active outfit journal exists without its recovery fence.");
            foreach (var file in writes) RequireOutfitPhysicalPath(campaignsRoot, file.Key, directory: false);
            markProfileDirty?.Invoke();
            if (!OutfitRecoveryFences.TryAdd(key, fence)) throw new InvalidOperationException("The campaign is already fenced.");
            try
            {
                var journal = StageOutfitFileJournal(journalRoot, characterRoot, op.Campaign, op.Hero, op.Id,
                    op.Player, op.Timeline, op.SourceHash, writes);
                for (int i = 0; i < writes.Count; i++)
                    PersistenceService.WriteRecoveryFile(writes[i].Key, writes[i].Value, mode: journal.Entries[i].BeforeMode);
                CommitOutfitFileJournal(journalRoot, characterRoot, journal);
            }
            catch (Exception ex)
            {
                if (PendingOutfitJournalOrUncertain(campaignsRoot, active))
                {
                    var settled = TrySettleOutfitFence(campaignsRoot, fence);
                    if (settled?.Committed == true) return settled.CleanupPending;
                    if (settled == null || settled.CleanupPending) throw new OutfitRecoveryPendingException(ex);
                }
                OutfitRecoveryFences.TryRemove(new KeyValuePair<string, OutfitRecoveryFence>(key, fence));
                throw;
            }

            // Committed bytes are authoritative even when unlinking the journal fails.
            // Unknown bytes have a different disposition and keep the campaign held.
            var completed = TrySettleOutfitFence(campaignsRoot, fence);
            if (completed?.Committed != true)
                throw new OutfitRecoveryPendingException(new IOException("The committed outfit could not be verified."));
            return completed.CleanupPending;
        }

        private static bool PendingOutfitJournalOrUncertain(string root, string active)
        {
            try { return RequireOutfitPhysicalPath(root, active, directory: false); }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            catch (InvalidDataException) { return true; }
        }

        private static Dictionary<string, object> TryAdmitOutfitRequest(string action, string campaign, string hero,
            string operation, Dictionary<string, object> payload)
        {
            string root = CampaignFileService.CampaignsRoot();
            if (!TryFindOutfitFence(root, campaign, out var fence)) return null;
            if (action != "confirm" || fence.Ambiguous || fence.Campaign != campaign || fence.Hero != hero
                || fence.Operation != operation || PersistenceService.HasSaveSyncJournalFence(campaign))
                return OutfitRecoveryStatus(campaign);
            lock (Program.FileLock)
            {
                OutfitFileJournal journal;
                try { journal = ReadOutfitFileJournal(fence.Journal, campaign, hero); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                { return OutfitRecoveryStatus(campaign); }

                if (journal.OperationId != operation || journal.PlayerId != ReadString(payload, "playerHeroId", "")
                    || journal.TimelineId != ReadString(payload, "timelineId", "")
                    || journal.SourceSha256 != ReadString(payload, "sourceSha256", ""))
                    return OutfitRecoveryStatus(campaign);

                var settled = TrySettleOutfitFence(root, fence);
                if (settled?.Committed == true)
                {
                    var response = CommittedOutfitResponse(payload, OutfitCharacterRoot(root, campaign, hero));
                    if (response == null) return OutfitFailure("The completed outfit does not match this confirmation.");
                    if (settled.CleanupPending) response["cleanupPending"] = true;
                    return response;
                }
                return settled == null || settled.CleanupPending ? OutfitRecoveryStatus(campaign) : null;
            }
        }

    }
}
