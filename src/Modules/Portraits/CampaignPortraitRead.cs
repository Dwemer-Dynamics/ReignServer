using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ReignBetaServer
{
    internal static partial class PortraitsService
    {
        private const int MaxCampaignPortraitBytes = 24 * 1024 * 1024;
        private const int MaxCampaignPortraitMetadataBytes = 512 * 1024;

        private sealed record CampaignPortraitSnapshot(byte[] Image, Dictionary<string, object> Receipt, bool Custom);

        internal static Dictionary<string, object> CampaignPortraitProductApi(Dictionary<string, object> payload)
        {
            if (payload == null || !payload.TryGetValue("campaignId", out object rawCampaign) || rawCampaign is not string campaign
                || !IsOutfitStorageId(campaign) || !payload.TryGetValue("heroStringId", out object rawHero) || rawHero is not string hero
                || !IsOutfitStorageId(hero) || !payload.TryGetValue("timelineId", out object rawTimeline) || rawTimeline is not string timeline
                || !OutfitSafeId(timeline) || timeline.Trim() != timeline || !payload.TryGetValue("sharedActivityGeneration", out object rawGeneration)
                || rawGeneration is not string generation)
                return TestDict("ok", false, "status", "invalid_scope");

            using var timelineScope = WorldSimulationService.EnterWorldHistoryTimelineScope(campaign);
            CampaignPortraitSnapshot snapshot;
            try
            {
                using (var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign, initializeInfrastructure: false))
                {
                    if (connection == null || !WorldSimulationService.NoticeAcknowledgementOwnerMatches(connection, null, payload, campaign, timeline))
                        return TestDict("ok", false, "status", "not_current");
                }
                lock (Program.FileLock)
                {
                    PersistenceService.RequireSaveSyncCampaignAvailable(campaign);
                    snapshot = ReadCampaignPortraitSnapshot(CampaignFileService.CampaignsRoot(), campaign, hero);
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or PersistenceService.SaveSyncCampaignFencedException)
            {
                return TestDict("ok", false, "status", "recovery_required");
            }

            if (snapshot == null) return TestDict("ok", true, "status", "missing", "campaignId", campaign,
                "heroStringId", hero, "timelineId", timeline, "sharedActivityGeneration", generation);
            return TestDict("ok", true, "status", "current", "campaignId", campaign, "heroStringId", hero,
                "timelineId", timeline, "sharedActivityGeneration", generation, "custom", snapshot.Custom,
                "sha256", Sha256Hex(snapshot.Image), "imageBase64", Convert.ToBase64String(snapshot.Image),
                "portraitReceipt", snapshot.Receipt);
        }

        // Own FileLock through selection, all witness reads and the image copy.
        // Render-only readers may use the retained assets after Save Sync rolls
        // profile.json back. Do not use the current profile as their commit point.
        private static CampaignPortraitSnapshot ReadCampaignPortraitSnapshot(string root, string campaign, string hero)
        {
            string directory = Path.Combine(OutfitCharacterRoot(root, campaign, hero), "portraits");
            if (!RequireOutfitPhysicalPath(root, directory, directory: true)) return null;

            byte[] custom = ReadCampaignPortraitFile(root, directory, "custom.png", MaxCampaignPortraitBytes);
            var outfit = ReadCampaignPortraitMetadata(root, directory, "outfit.json");
            var marker = ReadCampaignPortraitMetadata(root, directory, OutfitConfirmationFileName);
            var customReceipt = ReadCampaignPortraitMetadata(root, directory, OutfitPortraitReceiptFileName);
            bool hasOutfitFormat = outfit?.Keys.Any(key => key.Equals(OutfitConfirmationFormatKey, StringComparison.OrdinalIgnoreCase)) == true;
            if ((hasOutfitFormat || marker != null) && (!hasOutfitFormat || marker == null
                || !OutfitConfirmationMatches(directory, campaign, hero, outfit)))
                throw new InvalidDataException("The campaign portrait has no matching outfit confirmation.");

            if (custom != null)
            {
                string digest = Sha256Hex(custom);
                bool acceptedCrop = CampaignPortraitReceiptMatches(customReceipt, digest);
                if (outfit == null && (customReceipt == null || acceptedCrop))
                {
                    if (customReceipt == null
                        && ReadCampaignPortraitMetadata(root, directory, "portrait.json")?.ContainsKey(PortraitRefreshPublicationKey) != true)
                        return new CampaignPortraitSnapshot(custom, null, true); // Original markerless custom storage.
                    if (acceptedCrop && !HasCampaignPortraitRefresh(directory, root, out _))
                        return new CampaignPortraitSnapshot(custom, customReceipt, true);
                }
                if (outfit != null && acceptedCrop && ReadString(outfit, "portraitSha256", "") == digest
                    && (hasOutfitFormat || OutfitCommitProfileMatches(outfit,
                        ReadCampaignPortraitMetadata(root, Path.GetDirectoryName(directory), "profile.json"))))
                    return new CampaignPortraitSnapshot(custom, customReceipt, true);

                if (acceptedCrop && HasCampaignPortraitRefresh(directory, root, out object refresh))
                {
                    byte[] refreshedMaster = ReadCampaignPortraitFile(root, directory, "portrait.png", MaxCampaignPortraitBytes);
                    if (refreshedMaster != null && PortraitRefreshPublicationMatches(directory, refreshedMaster, refresh))
                        return new CampaignPortraitSnapshot(custom, customReceipt, true);
                }
                throw new InvalidDataException("The campaign custom portrait is incomplete.");
            }

            if (outfit != null || marker != null || customReceipt != null)
                throw new InvalidDataException("The campaign portrait disappeared before its ownership metadata.");

            byte[] master = ReadCampaignPortraitFile(root, directory, "portrait.png", MaxCampaignPortraitBytes);
            if (master == null) return null;
            var metadata = ReadCampaignPortraitMetadata(root, directory, "portrait.json");
            if (metadata != null && metadata.TryGetValue(PortraitRefreshPublicationKey, out object publication)
                && !PortraitRefreshPublicationMatches(directory, master, publication))
                throw new InvalidDataException("The campaign portrait refresh is incomplete.");
            var receipt = ReadDictionary(metadata, "productReceipt");
            if (receipt != null && !CampaignPortraitReceiptMatches(receipt, Sha256Hex(master)))
                throw new InvalidDataException("The campaign portrait bytes do not match the stored receipt.");
            return new CampaignPortraitSnapshot(master, receipt, false);
        }

        private static bool HasCampaignPortraitRefresh(string directory, string root, out object refresh)
        {
            refresh = null;
            return ReadCampaignPortraitMetadata(root, directory, "portrait.json")?.TryGetValue(PortraitRefreshPublicationKey, out refresh) == true;
        }

        private static bool CampaignPortraitReceiptMatches(Dictionary<string, object> receipt, string imageSha) =>
            receipt?.TryGetValue("accepted", out object accepted) == true && accepted is true
            && ReadString(receipt, "schema", "") == "reign-portrait-product-v1"
            && ReadString(ReadDictionary(receipt, "master"), "sha256", "") == imageSha;

        private static Dictionary<string, object> ReadCampaignPortraitMetadata(string root, string directory, string name)
        {
            byte[] bytes = ReadCampaignPortraitFile(root, directory, name, MaxCampaignPortraitMetadataBytes);
            return bytes == null ? null : Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes))
                ?? throw new InvalidDataException("The campaign portrait metadata is malformed.");
        }

        private static byte[] ReadCampaignPortraitFile(string root, string directory, string name, int limit)
        {
            string path = Path.Combine(directory, name);
            if (!RequireOutfitPhysicalPath(root, path, directory: false)) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length <= 0 || file.Length > limit) throw new InvalidDataException("The campaign portrait artifact is out of bounds.");
            byte[] content = new byte[(int)file.Length];
            file.ReadExactly(content);
            if (file.ReadByte() != -1) throw new InvalidDataException("The campaign portrait artifact changed during its read.");
            return content;
        }
    }
}