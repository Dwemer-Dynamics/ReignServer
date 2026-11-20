#if !REIGN_EXCLUDE_VERIFICATION
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class PersistenceService
    {
        private static void VerifyCalendarCheckpointInstallation(string campaignId, Action<string, bool, string> add)
        {
            string campaign = StrictCampaignDirectory(campaignId);
            string calendar = Path.Combine(campaign, "world", "calendar.json");
            string syncRoot = SaveSyncCampaignRoot(campaignId);
            var oldNative = SaveSyncTestPointPayload(campaignId, "older", 10, "Older calendar");
            var newer = SaveSyncTestPointPayload(campaignId, "newer", 20, "Newer calendar");
            try
            {
                PrepareSaveSyncTestCampaign(campaignId, "old");
                byte[] olderBytes = File.ReadAllBytes(calendar);
                string olderSha = SaveSyncCalendarDigest(olderBytes);
                var protectedNative = new Dictionary<string, object>(oldNative) { ["calendarSha256"] = olderSha };
                var badRegistration = new Dictionary<string, object>(oldNative) { ["calendarSha256"] = new string('0', 64) };
                bool denied = false;
                try { SaveSyncRegisterApi(badRegistration); }
                catch (InvalidDataException) { denied = true; }
                add("calendar_register_checks_native_binding", denied && SaveSyncPoint(ReadSaveSyncLedger(campaignId), "older") == null,
                    "A different calendar cannot be registered under the native save's digest.");

                SaveSyncRegisterApi(protectedNative);
                AppendSaveSyncTestFutureState(campaignId);
                SaveSyncRegisterApi(newer);
                byte[] newerBytes = File.ReadAllBytes(calendar);
                var ledger = ReadSaveSyncLedger(campaignId);
                var point = SaveSyncPoint(ledger, "older");
                string storedRoot = SaveSyncSnapshotRoot(campaignId, point);
                string storedCampaign = Path.Combine(storedRoot, "campaign");
                string stored = Path.Combine(storedCampaign, "world", "calendar.json");
                string manifestPath = Path.Combine(storedRoot, "manifest.json");
                var manifest = CampaignFileService.ReadJsonObject(manifestPath);
                add("calendar_raw_capture_binds_staged_bytes", olderSha == ReadString(point, "calendarSha256", "")
                    && olderSha == ReadString(manifest, "calendarSha256", "") && FileSha256(stored) == olderSha,
                    "The captured calendar, raw manifest, point and native save agree.");

                void ExpectRefusal(string test, Dictionary<string, object> payload, string reason, string targetCampaign = null)
                {
                    targetCampaign ??= storedCampaign;
                    string active = SaveSyncActiveFingerprintForTests(campaignId);
                    string schema = SaveSyncTestSqlValue(campaignId, "SELECT current_schema()::regnamespace::oid::text AS oid", "oid");
                    string ledgerSha = FileSha256(Path.Combine(syncRoot, "ledger.json"));
                    string targetSha = SaveSyncCalendarDigest(ReadSaveSyncCalendar(targetCampaign));
                    string recovery = Path.Combine(syncRoot, "r");
                    string[] backups = Directory.Exists(recovery) ? Directory.GetDirectories(recovery) : Array.Empty<string>();
                    string message = "";
                    try { SaveSyncLoadApi(payload, false); }
                    catch (InvalidDataException ex) { message = ex.Message; }
                    add(test, message.Contains(reason, StringComparison.OrdinalIgnoreCase)
                        && active == SaveSyncActiveFingerprintForTests(campaignId)
                        && schema == SaveSyncTestSqlValue(campaignId, "SELECT current_schema()::regnamespace::oid::text AS oid", "oid")
                        && ledgerSha == FileSha256(Path.Combine(syncRoot, "ledger.json"))
                        && targetSha == SaveSyncCalendarDigest(ReadSaveSyncCalendar(targetCampaign))
                        && backups.SequenceEqual(Directory.Exists(recovery) ? Directory.GetDirectories(recovery) : Array.Empty<string>()),
                        "The rejected load preserves the active campaign, SQL schema, ledger, stored calendar and recovery inventory.");
                }

                File.WriteAllBytes(stored, newerBytes);
                ExpectRefusal("calendar_registered_native_rejects_wrong_point", protectedNative, "SHA-256");
                ExpectRefusal("calendar_older_native_uses_server_binding", oldNative, "SHA-256");
                File.WriteAllBytes(stored, olderBytes);
                File.WriteAllText(calendar, "{damaged active");
                var repaired = SaveSyncLoadApi(oldNative, false);
                add("calendar_bound_restore_installs_original_bytes", ReadString(repaired, "result", "") == "rolled_back"
                    && File.ReadAllBytes(calendar).SequenceEqual(olderBytes) && !SaveSyncTestMarkerExists(campaignId, "future"),
                    "A digest-free native save restores the recorded calendar and matching PostgreSQL state over a damaged active calendar.");
                SaveSyncLoadApi(newer, false);

                // Reproduce a historical raw point in this disposable fixture. Its fingerprint
                // identifies the database snapshot; it has no calendar or file-table digest.
                ledger = ReadSaveSyncLedger(campaignId);
                point = SaveSyncPoint(ledger, "older");
                point.Remove("calendarSha256");
                UpsertSaveSyncPoint(ledger, point);
                WriteSaveSyncLedger(campaignId, ledger);
                manifest.Remove("calendarSha256");
                CampaignFileService.WriteJsonObject(manifestPath, manifest);
                foreach (var input in new[]
                {
                    ("damaged", "{", "Invalid checkpoint"),
                    ("future", "{\"Version\":2}", "Unsupported"),
                    ("foreign", "{\"Version\":1,\"CampaignId\":\"different\"}", "different campaign"),
                    ("unrepresentable", "{\"Version\":1,\"CampaignId\":\"" + campaignId
                        + "\",\"NativeDayAnchor\":1e308,\"CalendarDayAnchor\":-1e308,\"AgeAnchors\":{}}", "day anchors")
                })
                {
                    File.WriteAllText(stored, input.Item2);
                    ExpectRefusal("calendar_legacy_rejects_" + input.Item1, oldNative, input.Item3);
                }
                File.WriteAllBytes(stored, newerBytes);
                ExpectRefusal("calendar_native_binding_detects_substituted_old_raw", protectedNative, "SHA-256");
                File.WriteAllBytes(stored, olderBytes);
                ExpectRefusal("calendar_unbound_divergence_requires_evidence", oldNative, "no trusted calendar SHA-256");
                manifest["calendarSha256"] = olderSha;
                CampaignFileService.WriteJsonObject(manifestPath, manifest);
                ExpectRefusal("calendar_raw_manifest_cannot_authenticate_itself", oldNative, "no trusted calendar SHA-256");
                manifest.Remove("calendarSha256");
                CampaignFileService.WriteJsonObject(manifestPath, manifest);
                File.Delete(stored);
                ExpectRefusal("calendar_missing_cannot_displace_active_sidecar", oldNative, "no calendar");

                File.WriteAllBytes(stored, olderBytes);
                var nativeBound = SaveSyncLoadApi(protectedNative, false);
                add("calendar_native_binding_allows_old_raw_restore", ReadString(nativeBound, "result", "") == "rolled_back"
                    && File.ReadAllBytes(calendar).SequenceEqual(olderBytes), "A native digest authenticates a raw legacy checkpoint.");
                MarkSaveSyncActiveStateDirty(campaignId);
                var identical = SaveSyncLoadApi(oldNative, false);
                add("calendar_unbound_identical_active_v1_restores", ReadString(identical, "result", "") == "rolled_back"
                    && File.ReadAllBytes(calendar).SequenceEqual(olderBytes),
                    "A valid legacy sidecar identical to the active bytes survives installation.");

                SaveSyncLoadApi(newer, false);
                var files = BackupFileManifest(storedCampaign);
                var optimized = new Dictionary<string, object>(manifest)
                {
                    ["files"] = files, ["integrityDeferred"] = false, ["fingerprint"] = SaveSyncManifestFingerprint(files)
                };
                ledger = ReadSaveSyncLedger(campaignId);
                point = SaveSyncPoint(ledger, "older");
                point["snapshotFingerprint"] = optimized["fingerprint"];
                UpsertSaveSyncPoint(ledger, point);
                WriteSaveSyncLedger(campaignId, ledger);
                CampaignFileService.WriteJsonObject(manifestPath, optimized);
                File.WriteAllBytes(stored, newerBytes);
                ExpectRefusal("calendar_older_optimized_table_rejects_substitution", oldNative, "SHA-256");
                File.WriteAllBytes(stored, olderBytes);
                var optimizedLoad = SaveSyncLoadApi(oldNative, false);
                add("calendar_older_optimized_table_installs_valid_v1", ReadString(optimizedLoad, "result", "") == "rolled_back"
                    && File.ReadAllBytes(calendar).SequenceEqual(olderBytes), "The old file table authenticates a divergent legacy calendar.");

                ledger = ReadSaveSyncLedger(campaignId);
                point = SaveSyncPoint(ledger, "older");
                point["snapshotFingerprint"] = manifest["fingerprint"];
                UpsertSaveSyncPoint(ledger, point);
                WriteSaveSyncLedger(campaignId, ledger);
                CampaignFileService.WriteJsonObject(manifestPath, manifest);
                File.Delete(stored);
                File.Delete(calendar);
                MarkSaveSyncActiveStateDirty(campaignId);
                var absent = SaveSyncLoadApi(oldNative, false);
                add("calendar_legacy_absence_remains_absent", ReadString(absent, "result", "") == "rolled_back"
                    && ReadSaveSyncCalendar(campaign) == null && ReadSaveSyncCalendar(storedCampaign) == null,
                    "A genuine first-use pair installs without fabricating a calendar.");

                var vanilla = SaveSyncTestPointPayload(campaignId, "vanilla", 8, "Original pre-Reign native save");
                vanilla["savePointKind"] = "legacy_baseline";
                vanilla["registrationConfirmed"] = false;
                vanilla["calendarFirstUse"] = true;
                var baseline = SaveSyncLoadApi(vanilla, false);
                var vanillaPoint = SaveSyncPoint(ReadSaveSyncLedger(campaignId), "vanilla");
                string vanillaTarget = Path.Combine(SaveSyncSnapshotRoot(campaignId, vanillaPoint), "campaign");
                File.WriteAllBytes(calendar, olderBytes);
                MarkSaveSyncActiveStateDirty(campaignId);
                var established = new Dictionary<string, object>(vanilla) { ["calendarFirstUse"] = false };
                ExpectRefusal("calendar_bound_absence_protects_established_native_save", established, "no calendar", vanillaTarget);
                var reload = SaveSyncLoadApi(vanilla, false);
                string backup = ReadString(reload, "backupPath", "");
                add("calendar_vanilla_reload_reinstalls_captured_absence",
                    ReadString(baseline, "result", "") == "legacy_baseline_registered"
                    && vanillaPoint.ContainsKey("calendarSha256") && ReadString(vanillaPoint, "calendarSha256", "missing") == ""
                    && ReadString(reload, "result", "") == "rolled_back" && ReadSaveSyncCalendar(campaign) == null
                    && backup.Length > 0 && (ReadSaveSyncCalendar(Path.Combine(backup, "campaign")) ?? Array.Empty<byte>()).SequenceEqual(olderBytes),
                    "Reloading the original vanilla save restores its witnessed first-use absence and retains the displaced calendar in recovery.");
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaignId);
                TryDeleteDirectory(syncRoot);
                TryDeleteDirectory(campaign);
                TryDeleteDirectory(CampaignPortraitCacheDirectory(campaignId));
            }
        }
    }
}
#endif
