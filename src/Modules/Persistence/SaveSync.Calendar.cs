using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReignBetaServer
{
    internal static partial class PersistenceService
    {
        private static byte[] ReadSaveSyncCalendar(string campaignRoot)
        {
            string path = Path.Combine(campaignRoot, "world", "calendar.json");
            try { return File.ReadAllBytes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static string SaveSyncCalendarDigest(byte[] bytes) =>
            bytes == null ? "" : Convert.ToHexStringLower(SHA256.HashData(bytes));

        private static void RequireSaveSyncCalendarDigest(string expected, string actual)
        {
            if (expected == null)
                throw new InvalidDataException("The checkpoint has an invalid recorded calendar SHA-256. Restore the matching checkpoint before loading this save.");
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The checkpoint calendar differs from its recorded SHA-256. Restore the matching checkpoint bytes before loading this save.");
        }

        // Return the witnessed digest, including an empty digest for witnessed absence.
        // Validate before any restore work; the caller checks the exact staged bytes again.
        private static string VerifySaveSyncCalendarCheckpoint(string campaignId, string pointRoot,
            Dictionary<string, object> point, Dictionary<string, object> manifest, Dictionary<string, object> payload)
        {
            byte[] calendar = ReadSaveSyncCalendar(Path.Combine(pointRoot, "campaign"));
            string actual = ValidateSaveSyncCalendar(calendar, campaignId, payload);
            string nativeDigest = ReadString(payload, "calendarSha256", "");
            bool bound = nativeDigest.Length != 0;
            if (bound) RequireSaveSyncCalendarDigest(nativeDigest, actual);
            if (point.TryGetValue("calendarSha256", out object recorded))
            {
                RequireSaveSyncCalendarDigest(recorded as string, actual);
                bound = true;
            }
            // A raw manifest travels with the stored file. Its own hash can corroborate
            // the ledger/native evidence, but cannot authenticate a replacement by itself.
            if (manifest.TryGetValue("calendarSha256", out object corroborating))
                RequireSaveSyncCalendarDigest(corroborating as string, actual);
            var files = ReadDictionaryList(manifest, "files");
            if (!bound && (files.Count != 0 || !ReadBool(manifest, "integrityDeferred", true)))
            {
                // An older optimized manifest binds its file table to the point's fingerprint.
                if (!string.Equals(SaveSyncManifestFingerprint(files), ReadString(point, "snapshotFingerprint", ""), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The recorded checkpoint file hashes do not match the save point.");
                var entry = files.SingleOrDefault(f => ReadString(f, "path", "").Replace('\\', '/')
                    .Equals("world/calendar.json", StringComparison.OrdinalIgnoreCase));
                if (entry != null && calendar == null)
                    throw new InvalidDataException("The checkpoint file table lists a calendar whose bytes are missing. Restore the matching checkpoint before loading this save.");
                RequireSaveSyncCalendarDigest(entry == null ? "" : entry.GetValueOrDefault("sha256") as string, actual);
                bound = true;
            }
            // The original pre-Reign native save can be loaded again after its first launch
            // has created an active sidecar. Its registered baseline positively captured absence.
            bool firstUse = ReadBool(payload, "calendarFirstUse", false);
            if (!bound || (calendar == null && !firstUse))
            {
                string active = SaveSyncCalendarDigest(ReadSaveSyncCalendar(StrictCampaignDirectory(campaignId)));
                if (!string.Equals(active, actual, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(calendar == null
                        ? "The checkpoint has no calendar while this campaign has an active calendar. Restore a matching checkpoint before loading this save."
                        : "This older raw checkpoint has no trusted calendar SHA-256 and its calendar differs from the active campaign. Use a matching native save or checkpoint with a recorded calendar hash, or independently verify and restore the matching calendar before retrying.");
            }
            return actual;
        }

        private static string ValidateSaveSyncCalendar(byte[] bytes, string campaignId, Dictionary<string, object> payload)
        {
            if (bytes == null) return "";
            try
            {
                using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
                using var json = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions
                {
                    AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, AllowDuplicateProperties = false
                });
                var state = CalendarFields(json.RootElement);
                if (state["Version"].GetInt32() != 1) throw new InvalidDataException("Unsupported checkpoint calendar version.");
                string storedCampaign = state["CampaignId"].GetString();
                if (string.IsNullOrWhiteSpace(storedCampaign)) throw new InvalidDataException("Invalid checkpoint calendar campaign ID.");
                if (!string.Equals(storedCampaign, campaignId, StringComparison.Ordinal))
                    throw new InvalidDataException("The checkpoint calendar belongs to a different campaign.");
                double nativeLimit = ReadDouble(payload, "calendarNativeDayLimit", long.MaxValue);
                double native = state["NativeDayAnchor"].GetDouble();
                double date = state["CalendarDayAnchor"].GetDouble();
                double now = ReadDouble(payload, "campaignTimeDays", 0d);
                if (!double.IsFinite(nativeLimit) || !(Math.Abs(native) < nativeLimit)
                    || !CalendarYearFits(date) || !CalendarYearFits(date + (now - native)))
                    throw new InvalidDataException("Invalid checkpoint calendar day anchors.");
                foreach (var hero in state["AgeAnchors"].EnumerateObject())
                {
                    var anchor = CalendarFields(hero.Value);
                    float age = (float)anchor["Age"].GetDouble();
                    if (string.IsNullOrWhiteSpace(hero.Name) || !(age >= 0 && age < 200)
                        || !(Math.Abs(anchor["Day"].GetDouble()) < nativeLimit))
                        throw new InvalidDataException("Invalid checkpoint calendar hero anchor.");
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
            {
                throw new InvalidDataException("Invalid checkpoint calendar data. Restore the matching calendar before loading this save.", ex);
            }
            return SaveSyncCalendarDigest(bytes);
        }

        private static Dictionary<string, JsonElement> CalendarFields(JsonElement element) =>
            element.EnumerateObject().ToDictionary(field => field.Name, field => field.Value, StringComparer.OrdinalIgnoreCase);

        private static bool CalendarYearFits(double day)
        {
            double year = Math.Floor(day / 126d);
            return year >= int.MinValue && year <= int.MaxValue;
        }
    }
}
