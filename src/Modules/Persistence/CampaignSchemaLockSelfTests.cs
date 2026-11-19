using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class PersistenceService
    {
        private static void RunCampaignSchemaLockSelfTests(Action<string, bool, string> add)
        {
            if (!Program.HasArg(Environment.GetCommandLineArgs(), "--run-verification") || Program.ActiveServerPort > 0)
                throw new InvalidOperationException("Schema lock contracts require isolated Verification Lab state.");
            string campaign = "__schema_lock_" + Guid.NewGuid().ToString("N");
            bool workersJoined = true;
            try
            {
                RunCampaignColdSchemaOrderCase(campaign, false, add, ref workersJoined);
                if (!workersJoined) return;
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    PersistenceSelfTestStore.UpdateSchemaMeta(connection);
                RunCampaignColdSchemaOrderCase(campaign, true, add, ref workersJoined);
                if (!workersJoined) return;

                Exception warmError = null;
                int settingsReads = 0, initializations = 0, coldWaits = 0;
                bool warmJoined;
                var warm = new Thread(() =>
                {
                    try
                    {
                        ReignDatabase.CampaignSchemaSettingsReadObserverForTests = _ => Interlocked.Increment(ref settingsReads);
                        ReignDatabase.CampaignSchemaColdWaitObserverForTests = _ => Interlocked.Increment(ref coldWaits);
                        ReignDatabase.CampaignSchemaInitializingForTests = () => Interlocked.Increment(ref initializations);
                        using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                        if (connection.State != ConnectionState.Open) throw new Exception("Successful connection was closed.");
                    }
                    catch (Exception ex) { warmError = ex; }
                    finally { ClearCampaignSchemaTestHooks(); }
                }) { IsBackground = true };
                lock (Program.FileLock)
                {
                    warm.Start();
                    warmJoined = warm.Join(5000);
                }
                workersJoined &= warm.Join(10000);
                add("schema_warm_open_ignores_other_thread_file_lock", warmJoined && workersJoined
                    && warmError == null && settingsReads == 0 && initializations == 0 && coldWaits == 0,
                    "A different thread holds FileLock; a warm real connection must finish without settings or initialization. " + warmError);
                if (!workersJoined) return;

                using var invertedWaiting = new ManualResetEventSlim();
                Exception invertedError = null;
                var inverted = new Thread(() =>
                {
                    try
                    {
                        lock (Program.CampaignSchemaLock)
                        {
                            invertedWaiting.Set();
                            // Bypass the production guard to demonstrate the old monitor cycle.
                            lock (Program.FileLock) { }
                        }
                    }
                    catch (Exception ex) { invertedError = ex; }
                    finally { ClearCampaignSchemaTestHooks(); }
                }) { IsBackground = true };
                bool observed, probeAcquired = false;
                lock (Program.FileLock)
                {
                    inverted.Start();
                    observed = invertedWaiting.Wait(5000);
                    if (observed)
                    {
                        probeAcquired = Monitor.TryEnter(Program.CampaignSchemaLock, 100);
                        if (probeAcquired) Monitor.Exit(Program.CampaignSchemaLock);
                    }
                }
                workersJoined &= inverted.Join(10000);
                add("schema_old_lock_inversion_negative_control", observed && !probeAcquired
                    && workersJoined && invertedError == null,
                    "The same probe detects the deliberate old inversion, then both locks and the worker are released.");
                if (!workersJoined) return;

                bool nestedRejected = false, semanticRejected = false, settingsRejected = false;
                lock (Program.CampaignSchemaLock)
                {
                    try { using var connection = ReignDatabase.OpenCampaignConnection(campaign); }
                    catch (InvalidOperationException) { nestedRejected = true; }
                    try { DialogueService.EnsureSemanticMemorySchema(null); }
                    catch (InvalidOperationException) { semanticRejected = true; }
                    try { Program.LoadSettings(); }
                    catch (InvalidOperationException) { settingsRejected = true; }
                }
                add("schema_reverse_nested_entry_rejected", nestedRejected && semanticRejected && settingsRejected,
                    "Connection, semantic schema and settings entries reject SchemaLock ownership without prior FileLock.");

                RunCampaignSchemaFailureCase(campaign, "opened", add);
                RunCampaignSchemaFailureCase(campaign, "settings", add);
                RunCampaignSchemaFailureCase(campaign, "initializing", add);
                RunCampaignSchemaFailureCase(campaign, "after_memory", add);
            }
            catch (Exception ex) { add("schema_lock_fixture", false, ex.ToString()); }
            finally
            {
                ClearCampaignSchemaTestHooks();
                // Never drop a schema that a timed-out test worker may still use.
                if (workersJoined)
                {
                    ReignPostgreSqlStorage.DropCampaign(campaign);
                    string directory = Path.GetFullPath(Program.CampaignDirectory(campaign));
                    string root = Path.GetFullPath(Program.CampaignsRoot()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(directory) == campaign && Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            }
        }

        private static void ClearCampaignSchemaTestHooks()
        {
            ReignDatabase.CampaignSchemaSettingsReadObserverForTests = null;
            ReignDatabase.CampaignSchemaConnectionOpenedForTests = null;
            ReignDatabase.CampaignSchemaColdWaitObserverForTests = null;
            ReignDatabase.CampaignSchemaInitializingForTests = null;
            ReignDatabase.CampaignSchemaAfterMemoryForTests = null;
        }

        private static void RunCampaignColdSchemaOrderCase(string campaign, bool stale,
            Action<string, bool, string> add, ref bool workersJoined)
        {
            using var waiting = new ManualResetEventSlim();
            int unsafeReads = 0, initialized = 0;
            Exception workerError = null, mainError = null;
            bool observed = false, probeAcquired = false, mainOpened = false;
            var worker = new Thread(() =>
            {
                try
                {
                    ReignDatabase.CampaignSchemaColdWaitObserverForTests = held =>
                    {
                        if (held) Interlocked.Increment(ref unsafeReads);
                        waiting.Set();
                    };
                    ReignDatabase.CampaignSchemaSettingsReadObserverForTests = held =>
                    {
                        if (held && !Monitor.IsEntered(Program.FileLock)) Interlocked.Increment(ref unsafeReads);
                    };
                    ReignDatabase.CampaignSchemaInitializingForTests = () => Interlocked.Increment(ref initialized);
                    using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                }
                catch (Exception ex) { workerError = ex; }
                finally { ClearCampaignSchemaTestHooks(); }
            }) { IsBackground = true };
            lock (Program.FileLock)
            {
                ReignDatabase.CampaignSchemaInitializingForTests = () => Interlocked.Increment(ref initialized);
                try
                {
                    worker.Start();
                    observed = waiting.Wait(5000);
                    if (observed)
                    {
                        probeAcquired = Monitor.TryEnter(Program.CampaignSchemaLock, 1000);
                        if (probeAcquired)
                        {
                            Monitor.Exit(Program.CampaignSchemaLock);
                            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                            string generation = ReadString(PersistenceSelfTestStore.SelectSchemaMeta(connection).FirstOrDefault(), "value", "");
                            mainOpened = connection.State == ConnectionState.Open && generation ==
                                ReadString(Program.LoadSettings(), "vectorProvider", "local").ToLowerInvariant() + "|" + DialogueService.SemanticModelVersion;
                        }
                    }
                }
                catch (Exception ex) { mainError = ex; }
                finally { ClearCampaignSchemaTestHooks(); }
            }
            workersJoined &= worker.Join(20000);
            add(stale ? "schema_stale_revision_uses_safe_lock_order" : "schema_cold_open_uses_safe_lock_order",
                observed && probeAcquired && mainOpened && workersJoined && initialized == 1
                && unsafeReads == 0 && workerError == null && mainError == null,
                "Worker waits outside SchemaLock while FileLock owner initializes the same campaign; second readiness check skips duplicate work. "
                + "initialized=" + initialized + "; unsafeReads=" + unsafeReads + "; error=" + (workerError ?? mainError));
        }

        private static void RunCampaignSchemaFailureCase(string campaign, string stage,
            Action<string, bool, string> add)
        {
            campaign += "_" + stage;
            ReignDatabase.InvalidateCampaignSchemaCaches(campaign);
            ReignDbConnection opened = null;
            var sentinel = new OperationCanceledException("schema fixture " + stage);
            Exception actual = null;
            ReignDatabase.CampaignSchemaConnectionOpenedForTests = connection =>
            {
                opened = connection;
                if (stage == "opened") throw sentinel;
            };
            ReignDatabase.CampaignSchemaSettingsReadObserverForTests = _ => { if (stage == "settings") throw sentinel; };
            ReignDatabase.CampaignSchemaInitializingForTests = () => { if (stage == "initializing") throw sentinel; };
            ReignDatabase.CampaignSchemaAfterMemoryForTests = () => { if (stage == "after_memory") throw sentinel; };
            try { using var connection = ReignDatabase.OpenCampaignConnection(campaign); }
            catch (Exception ex) { actual = ex; }
            finally { ClearCampaignSchemaTestHooks(); }
            bool closed = opened != null && opened.State == ConnectionState.Closed;
            bool ready;
            lock (Program.CampaignSchemaLock) ready = Program.CampaignSchemaReady.Contains(ReignPostgreSqlStorage.CampaignSchemaName(campaign));
            bool locksFree = false;
            var probe = new Thread(() =>
            {
                if (!Monitor.TryEnter(Program.FileLock, 1000)) return;
                try
                {
                    if (!Monitor.TryEnter(Program.CampaignSchemaLock, 1000)) return;
                    try { locksFree = true; }
                    finally { Monitor.Exit(Program.CampaignSchemaLock); }
                }
                finally { Monitor.Exit(Program.FileLock); }
            }) { IsBackground = true };
            probe.Start();
            bool joined = probe.Join(5000);
            using (var retry = ReignDatabase.OpenCampaignConnection(campaign))
                add("schema_failure_" + stage + "_disposes_and_retries", ReferenceEquals(actual, sentinel)
                    && closed && !ready && joined && locksFree && retry.State == ConnectionState.Open,
                    "Original cancellation propagates, raw connection closes, both locks release and retry succeeds.");
            ReignPostgreSqlStorage.DropCampaign(campaign);
            string directory = Path.GetFullPath(Program.CampaignDirectory(campaign));
            string root = Path.GetFullPath(Program.CampaignsRoot()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(directory) == campaign && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
