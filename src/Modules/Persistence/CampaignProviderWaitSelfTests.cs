using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunCampaignProviderWaitSelfTests()
        {
            if (!HasArg(Environment.GetCommandLineArgs(), "--run-verification") || ActiveServerPort > 0)
                throw new InvalidOperationException("Campaign provider-wait contracts require the isolated non-listening Verification Lab CLI.");
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["caseId"] = id, ["name"] = id, ["passed"] = passed, ["ok"] = true,
                ["suite"] = "campaign_provider_wait", ["summary"] = summary
            });
            string campaign = "__provider_wait_" + Guid.NewGuid().ToString("N");
            string directory = CampaignDirectory(campaign);
            string marker = Path.Combine(directory, "provider-result.json");
            try
            {
                using (var connection = OpenCampaignConnection(campaign))
                    ExecuteSql(connection, "CREATE TABLE provider_wait_contract(value TEXT); INSERT INTO provider_wait_contract VALUES('snapshot');");
                WriteJsonObject(marker, new Dictionary<string, object> { ["value"] = "snapshot" });
                RunCampaignProviderWaitCase(campaign, marker, false, false, add);
                RunCampaignProviderWaitCase(campaign, marker, true, false, add);
                RunCampaignProviderWaitCase(campaign, marker, true, true, add);
                RunCampaignProviderWaitCase(campaign, marker, false, true, add);

                CampaignDataGate.EnterWriteLock();
                try
                {
                    bool held = WithCampaignProviderWait("character_construction", () => CampaignDataGate.IsWriteLockHeld);
                    add("writer_and_background_leases_are_never_suspended", held && CurrentCampaignRequestLease == null,
                        "Only an owned HTTP read lease is eligible; write owners retain snapshot exclusion.");
                }
                finally { CampaignDataGate.ExitWriteLock(); }
            }
            catch (Exception ex) { add("provider_wait_fixture", false, ex.ToString()); }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
                string root = Path.GetFullPath(CampaignsRoot()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string exact = Path.GetFullPath(directory);
                if (exact.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(exact) == campaign && Directory.Exists(exact)) Directory.Delete(exact, true);
            }
            return results;
        }

        private static void RunCampaignProviderWaitCase(string campaign, string marker, bool restore, bool providerFails,
            Action<string, bool, string> add)
        {
            string name = (restore ? "restore" : "snapshot") + (providerFails ? "_during_failure" : "_during_success");
            string point = Guid.NewGuid().ToString("N");
            object workLock = new object();
            bool readLeaseRestored = false, stale = false, sqlDenied = false, fileDenied = false;
            bool providerFailurePreserved = false, duplicateEntered = false;
            Exception requestError = null, duplicateError = null, saveError = null;
            Dictionary<string, object> saved = null;
            using (var providerWaiting = new ManualResetEventSlim())
            using (var finishProvider = new ManualResetEventSlim())
            using (var duplicateStarted = new ManualResetEventSlim())
            using (var snapshotFinished = new ManualResetEventSlim())
            {
                var request = new Thread(() =>
                {
                    CampaignRequestAccess access = EnterCampaignRequestAccess("/events/social/turn");
                    try
                    {
                        using (EnterCampaignWorkLock(workLock))
                        {
                            try
                            {
                                WithCampaignProviderWait("character_construction", () =>
                                {
                                    providerWaiting.Set();
                                    if (!finishProvider.Wait(30000)) throw new TimeoutException("Fixture provider wait exceeded its bound.");
                                    if (providerFails) throw new IOException("fixture provider failure");
                                    return "fixture complete response";
                                });
                            }
                            catch (CampaignRequestReplacedException)
                            {
                                stale = true;
                                // Deliberately imitate a broad caller catch trying to
                                // cache a failure. Both precomputed paths and SQL must fail.
                                try { WriteJsonObject(marker, new Dictionary<string, object> { ["value"] = "stale" }); }
                                catch (CampaignRequestReplacedException) { fileDenied = true; }
                                try { ExecuteSql(null, "INSERT INTO provider_wait_contract VALUES('stale');"); }
                                catch (CampaignRequestReplacedException) { sqlDenied = true; }
                            }
                            catch (IOException ex) { providerFailurePreserved = ex.Message == "fixture provider failure"; }
                            readLeaseRestored = CampaignDataGate.IsReadLockHeld;
                            if (!stale)
                                WriteJsonObject(marker, new Dictionary<string, object> { ["value"] = providerFails ? "failure" : "complete" });
                        }
                    }
                    catch (Exception ex) { requestError = ex; }
                    finally { ExitCampaignRequestAccess(access); }
                }) { IsBackground = true };
                var duplicate = new Thread(() =>
                {
                    CampaignRequestAccess access = EnterCampaignRequestAccess("/events/social/turn");
                    try
                    {
                        duplicateStarted.Set();
                        using (EnterCampaignWorkLock(workLock)) duplicateEntered = true;
                    }
                    catch (CampaignRequestReplacedException) { /* Expected after restore. */ }
                    catch (Exception ex) { duplicateError = ex; }
                    finally { ExitCampaignRequestAccess(access); }
                }) { IsBackground = true };
                var save = new Thread(() =>
                {
                    try
                    {
                        saved = SaveSyncRegisterApi(new Dictionary<string, object>
                        {
                            ["campaignId"] = campaign, ["savePointId"] = point, ["campaignTimeDays"] = 1d,
                            ["nativeSaveName"] = "provider_wait_fixture", ["worldHistoryOutboxDrained"] = true
                        });
                        if (restore && ReadBool(saved, "ok", false))
                        {
                            CampaignDataGate.EnterWriteLock();
                            try
                            {
                                var registered = SaveSyncPoint(ReadSaveSyncLedger(campaign), point);
                                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, SaveSyncPostgreSqlSnapshotPointId(registered));
                                WriteJsonObject(marker, new Dictionary<string, object> { ["value"] = "restored" });
                            }
                            finally { CampaignDataGate.ExitWriteLock(); }
                        }
                    }
                    catch (Exception ex) { saveError = ex; }
                    finally { snapshotFinished.Set(); }
                }) { IsBackground = true };
                bool waiting = false, concurrentSave = false, joined = false;
                request.Start();
                try
                {
                    waiting = providerWaiting.Wait(5000);
                    duplicate.Start();
                    waiting &= duplicateStarted.Wait(5000);
                    save.Start();
                    concurrentSave = snapshotFinished.Wait(15000) && !finishProvider.IsSet;
                }
                finally
                {
                    finishProvider.Set();
                    bool requestJoined = request.Join(10000);
                    bool duplicateJoined = duplicate.Join(10000);
                    bool saveJoined = save.Join(10000);
                    joined = requestJoined && duplicateJoined && saveJoined;
                }
                string value = ReadString(ReadJsonObject(marker), "value", "");
                bool correctResult = restore ? stale && fileDenied && sqlDenied && value == "restored" && !duplicateEntered
                    : !stale && duplicateEntered && value == (providerFails ? "failure" : "complete")
                        && providerFailurePreserved == providerFails;
                add(name, waiting && joined && concurrentSave && ReadBool(saved, "ok", false)
                    && readLeaseRestored && correctResult && requestError == null && duplicateError == null && saveError == null,
                    "Save Sync must finish while the provider and same-character duplicate wait; restore rejects both late success and late failure. "
                    + (requestError?.ToString() ?? duplicateError?.ToString() ?? saveError?.ToString() ?? ""));
            }
        }
    }
}
