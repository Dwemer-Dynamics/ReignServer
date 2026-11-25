using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        private static List<Dictionary<string, object>> RunTownNotableProfileSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string name, bool pass, string detail) => rows.Add(ProfileSelfTest("town_prewarm_" + name, pass, detail, null));
            foreach (int limit in new[] { 1, 4, 16 })
            {
                var pool = new TownProfileQueue();
                using var entered = new CountdownEvent(limit);
                using var release = new ManualResetEventSlim();
                using var done = new CountdownEvent(limit + 2);
                int active = 0, peak = 0, calls = 0, errors = 0;
                Func<TownProfileWork, bool> run = job =>
                {
                    int now = Interlocked.Increment(ref active);
                    int prior;
                    do { prior = Volatile.Read(ref peak); } while (now > prior && Interlocked.CompareExchange(ref peak, now, prior) != prior);
                    int call = Interlocked.Increment(ref calls);
                    if (call <= limit) entered.Signal();
                    try { return release.Wait(10000); }
                    finally { Interlocked.Decrement(ref active); done.Signal(); }
                };
                for (int i = 0; i < limit + 2; i++) pool.Enqueue(new TownProfileWork { Key = "cap" + i, SettingsHash = "v1" }, limit, run, _ => Interlocked.Increment(ref errors));
                bool filled = entered.Wait(5000);
                Check("concurrency_" + limit, filled && Volatile.Read(ref peak) == limit && Volatile.Read(ref calls) == limit,
                    "Real worker calls fill all configured slots without admitting an extra request.");
                release.Set();
                bool drained = done.Wait(5000) && SpinWait.SpinUntil(() => pool.Count == 0, 5000);
                Check("drains_" + limit, drained && peak <= limit && calls == limit + 2 && errors == 0,
                    "All queued candidates finish and release single-flight ownership.");
            }

            var queue = new TownProfileQueue(2);
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                int runs = 0;
                Func<TownProfileWork, bool> run = item => { Interlocked.Increment(ref runs); entered.Set(); return release.Wait(10000); };
                var work = new TownProfileWork { Key = "one", SettingsHash = "v1" };
                queue.Enqueue(work, 1, run, _ => { });
                bool started = entered.Wait(5000);
                string duplicate = queue.Enqueue(new TownProfileWork { Key = "one", SettingsHash = "v1" }, 1, run, _ => { });
                queue.Promote("one");
                CurrentTownProfileWork = work;
                try { Check("foreground_promotion", work.Foreground && LlmProviderService.ProviderRequestIsInteractive("character_construction"),
                    "A joined foreground request promotes remaining construction calls through the real provider priority policy."); }
                finally { CurrentTownProfileWork = null; }
                queue.Enqueue(new TownProfileWork { Key = "two", SettingsHash = "v1" }, 1, run, _ => { });
                string full = queue.Enqueue(new TownProfileWork { Key = "three", SettingsHash = "v1" }, 1, run, _ => { });
                release.Set();
                bool drained = SpinWait.SpinUntil(() => queue.Count == 0, 5000);
                Check("dedup_and_capacity", started && duplicate == "pending" && full == "full" && drained && runs == 2,
                    "Duplicate event/revisit admissions coalesce; full queues explicitly defer work without unbounded tasks.");
            }

            int failureCalls = 0;
            using (var failureEntered = new ManualResetEventSlim())
            using (var releaseFailure = new ManualResetEventSlim())
            {
                var handoff = new TownProfileQueue();
                int goodCalls = 0;
                handoff.Enqueue(new TownProfileWork { Key = "failed_batch", SettingsHash = "v1" }, 1,
                    _ => throw new IOException("first batch"), _ => { failureEntered.Set(); releaseFailure.Wait(10000); });
                bool blocked = failureEntered.Wait(5000);
                handoff.Enqueue(new TownProfileWork { Key = "next_batch", SettingsHash = "v2" }, 1,
                    _ => { Interlocked.Increment(ref goodCalls); return true; }, _ => { });
                releaseFailure.Set();
                Check("queued_batch_keeps_own_callback", blocked && SpinWait.SpinUntil(() => handoff.Count == 0, 5000) && goodCalls == 1,
                    "A worker processing consecutive batches executes each job's own work and error callbacks.");
            }
            var retryQueue = new TownProfileQueue();
            Func<TownProfileWork, bool> fail = _ => { Interlocked.Increment(ref failureCalls); throw new IOException("fixture provider failure"); };
            retryQueue.Enqueue(new TownProfileWork { Key = "retry", SettingsHash = "v1" }, 1, fail, _ => { });
            bool failedClean = SpinWait.SpinUntil(() => retryQueue.Count == 0, 5000);
            string suppressed = retryQueue.Enqueue(new TownProfileWork { Key = "retry", SettingsHash = "v1" }, 1, fail, _ => { });
            string changed = retryQueue.Enqueue(new TownProfileWork { Key = "retry", SettingsHash = "v2" }, 1, _ => true, _ => { });
            Check("speculative_failure_cooldown", failedClean && suppressed == "cooldown" && changed == "queued"
                && SpinWait.SpinUntil(() => retryQueue.Count == 0, 5000) && failureCalls == 1,
                "An exception releases ownership, repeated visits do not repeat charges, and changed settings may retry.");

            string campaign = "town_prewarm_contract_" + Guid.NewGuid().ToString("N");
            try
            {
                VerifyTownPrewarmDeferral(campaign, Check);
                string hero = "notable_fixture", path = CampaignFileService.CharacterFile(campaign, hero, "constructed.json");
                CampaignFileService.WriteJsonObject(path, TestDict("ok", true, "status", "first_contact_pending"));
                byte[] original = File.ReadAllBytes(path);
                bool ok = RunSpeculativeTownProfile(campaign, hero, () => { CampaignFileService.WriteJsonObject(path, TestDict("status", "failed")); return TestDict("ok", false, "status", "failed"); });
                Check("failure_preserves_first_contact", !ok && File.ReadAllBytes(path).SequenceEqual(original),
                    "A speculative provider failure restores exact original status bytes, so normal construction can retry.");
                bool threw = false;
                try { RunSpeculativeTownProfile(campaign, hero, () => { CampaignFileService.WriteJsonObject(path, TestDict("status", "constructing")); throw new IOException("fixture"); }); }
                catch (IOException) { threw = true; }
                Check("exception_preserves_first_contact", threw && File.ReadAllBytes(path).SequenceEqual(original), "Thrown speculative failures also restore first-contact state.");
                bool replaced = false;
                try
                {
                    RunSpeculativeTownProfile(campaign, hero, () =>
                    {
                        // Simulate the already-restored bytes and the existing
                        // provider-wait contract's invalid-lease notification.
                        CampaignFileService.WriteJsonObject(path, TestDict("status", "restored_marker"));
                        PersistenceService.CurrentCampaignRequestLease.Invalid = true;
                        throw new PersistenceService.CampaignRequestReplacedException();
                    });
                }
                catch (PersistenceService.CampaignRequestReplacedException) { replaced = true; }
                Check("replacement_never_rolls_back_new_bytes", replaced
                    && ReadString(CampaignFileService.ReadJsonObject(path), "status", "") == "restored_marker" && PersistenceService.CurrentCampaignRequestLease == null,
                    "Replacement notification skips old status restoration and releases the temporary lease.");
                int generations = 0;
                using var firstIn = new ManualResetEventSlim();
                using var firstOut = new ManualResetEventSlim();
                using var contending = new ManualResetEventSlim();
                bool ready = false;
                Func<Dictionary<string, object>> create = () => { if (!ready) { Interlocked.Increment(ref generations); firstIn.Set(); if (!firstOut.Wait(10000)) throw new TimeoutException(); ready = true; } return TestDict("ok", true, "status", "ready"); };
                var a = Task.Run(() => WithCharacterConstructionGate(campaign, hero, TownNotableProfileSource, create));
                bool active = firstIn.Wait(5000);
                var b = Task.Run(() =>
                {
                    PersistenceService.CampaignWorkLockWaitObserverForTests = _ => contending.Set();
                    try { return WithCharacterConstructionGate(campaign, hero.ToUpperInvariant(), "social_event", create); }
                    finally { PersistenceService.CampaignWorkLockWaitObserverForTests = null; }
                });
                bool sharedGate = contending.Wait(5000);
                firstOut.Set();
                Check("foreground_single_flight", active && sharedGate && Task.WaitAll(new Task[] { a, b }, 5000) && generations == 1,
                    "A real mixed-case foreground contender waits on the production hero gate and reuses one complete result.");
                using (var failedIn = new ManualResetEventSlim())
                using (var failedOut = new ManualResetEventSlim())
                using (var foregroundWait = new ManualResetEventSlim())
                {
                    string failedPath = CampaignFileService.CharacterFile(campaign, "failed_race", "constructed.json");
                    CampaignFileService.WriteJsonObject(failedPath, TestDict("ok", true, "status", "first_contact_pending"));
                    bool fallbackSawOriginal = false;
                    var speculative = Task.Run(() => WithCharacterConstructionGate(campaign, "failed_race", TownNotableProfileSource, () =>
                    {
                        RunSpeculativeTownProfile(campaign, "failed_race", () =>
                        {
                            failedIn.Set(); if (!failedOut.Wait(10000)) throw new TimeoutException();
                            CampaignFileService.WriteJsonObject(failedPath, TestDict("ok", false, "status", "failed"));
                            return TestDict("ok", false, "status", "failed");
                        });
                        return TestDict();
                    }));
                    bool started = failedIn.Wait(5000);
                    var fallback = Task.Run(() =>
                    {
                        PersistenceService.CampaignWorkLockWaitObserverForTests = _ => foregroundWait.Set();
                        try
                        {
                            return WithCharacterConstructionGate(campaign, "failed_race", "social_event", () =>
                            {
                                fallbackSawOriginal = ReadString(CampaignFileService.ReadJsonObject(failedPath), "status", "") == "first_contact_pending";
                                var result = TestDict("ok", true, "status", "ready"); CampaignFileService.WriteJsonObject(failedPath, result); return result;
                            });
                        }
                        finally { PersistenceService.CampaignWorkLockWaitObserverForTests = null; }
                    });
                    bool waited = foregroundWait.Wait(5000); failedOut.Set();
                    Check("failed_job_foreground_fallback", started && waited && Task.WaitAll(new Task[] { speculative, fallback }, 5000)
                        && fallbackSawOriginal && ReadString(CampaignFileService.ReadJsonObject(failedPath), "status", "") == "ready",
                        "A foreground request blocked behind a failed speculative job sees the restored original state and can create normally.");
                }
                var profile = TestDict("heroStringId", "prepared", "name", "Prepared notable", "age", 35,
                    "isAlive", true, "isNotable", true, "homeSettlementId", "town", "narrativeVersion", 4);
                var job = new TownProfileWork { Campaign = campaign, HeroId = "prepared", Hero = profile,
                    Generation = Interlocked.Read(ref PersistenceService.CampaignReplacementGeneration) };
                int authored = 0;
                Func<string, string, Dictionary<string, object>, Dictionary<string, object>> author = (c, h, p) =>
                {
                    Interlocked.Increment(ref authored);
                    var result = TestDict("ok", true, "status", "ready", "llmUsed", true);
                    CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(c, h, "constructed.json"), result); return result;
                };
                bool prepared = RunTownProfileWorkCore(job, author);
                byte[] readyBytes = File.ReadAllBytes(CampaignFileService.CharacterFile(campaign, "prepared", "constructed.json"));
                bool reused = RunTownProfileWorkCore(job, author);
                Check("revisit_preserves_ready_profile", prepared && reused && authored == 1
                    && readyBytes.SequenceEqual(File.ReadAllBytes(CampaignFileService.CharacterFile(campaign, "prepared", "constructed.json")))
                    && ReadString(CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign, "prepared", "enrichment.json")), "status", "") == "completed_on_town_entry",
                    "Production job publication is reused on later visits and records pre-generation rather than a first encounter.");
                job.HeroId = "editor_owned";
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, job.HeroId, "editor_authority.json"), TestDict("authoritative", true));
                bool editorSkipped = RunTownProfileWorkCore(job, author);
                job.HeroId = "dead_notable";
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, job.HeroId, "profile.json"), TestDict("isAlive", false));
                bool deadSkipped = RunTownProfileWorkCore(job, author);
                Check("queued_editor_and_death_updates", editorSkipped && deadSkipped && authored == 1,
                    "Editor authority or death observed after capture prevents speculative generation.");
                long epoch = Interlocked.Read(ref PersistenceService.CampaignReplacementGeneration);
                var stale = new TownProfileWork { Campaign = campaign, HeroId = "stale", Generation = epoch - 1, Hero = TestDict() };
                bool skipped = RunTownProfileWork(stale);
                Check("old_generation_and_lease_cleanup", skipped && !Directory.Exists(CampaignFileService.CharacterDirectory(campaign, "stale"))
                    && PersistenceService.CurrentCampaignRequestLease == null && CurrentTownProfileWork == null && !Program.CampaignDataGate.IsReadLockHeld,
                    "Old-generation jobs make no files and clear worker lease and priority state.");
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
                string directory = CampaignFileService.CampaignDirectory(campaign);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            var candidate = TestDict("heroStringId", "n", "isNotable", true, "isAlive", true, "homeSettlementId", "town");
            Check("town_candidate_scope", TownNotableCandidate(candidate, "town") && !TownNotableCandidate(candidate, "elsewhere")
                && !TownNotableCandidate(TestDict("heroStringId", "lord", "isAlive", true, "homeSettlementId", "town"), "town")
                && !TownNotableCandidate(TestDict("heroStringId", "dead", "isNotable", true, "isAlive", false, "homeSettlementId", "town"), "town"),
                "Only living notables belonging to the entered town qualify; unrelated heroes do not.");
            return rows;
        }

        private static void VerifyTownPrewarmDeferral(string campaign, Action<string, bool, string> check)
        {
            var root = Directory.CreateTempSubdirectory("reign-town-prewarm-guard-");
            try
            {
                string directory = Path.Combine(root.FullName, NativeProviderTestGuard.RelativeDirectory);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "provider-free.json"),
                    Json.Serialize(TestDict("schema", NativeProviderTestGuard.Schema, "campaignId", campaign,
                        "runId", "town_prewarm_isolation", "timelineId", "main", "taskId", Guid.NewGuid().ToString(),
                        "baselineSaveName", "original", "baselineSha256", new string('a', 64), "currentSaveName", "owned_Current")));
                var guard = new NativeProviderTestGuard(root.FullName);
                long generation = Interlocked.Read(ref PersistenceService.CampaignReplacementGeneration);
                var intake = PrewarmTownNotableProfiles(TestDict("campaignId", campaign, "generation", generation,
                    "settlementId", "town", "settlementType", "town"), guard);
                const string hero = "deferred_notable";
                var work = new TownProfileWork
                {
                    Campaign = campaign, HeroId = hero, Generation = generation,
                    Hero = TestDict("heroStringId", hero, "name", "Deferred notable", "isAlive", true,
                        "isNotable", true, "homeSettlementId", "town")
                };
                bool authored = false;
                bool released = RunTownProfileWorkCore(work, (_, _, _) =>
                {
                    authored = true;
                    return TestDict("ok", true);
                }, guard);
                check("provider_free_deferral_before_writes", ReadBool(intake, "ok", false)
                    && ReadString(intake, "result", "") == "provider_free_deferred"
                    && ReadInt(intake, "queued", -1) == 0 && released && !authored
                    && !Directory.Exists(CampaignFileService.CharacterDirectory(campaign, hero))
                    && ReadBool(guard.Snapshot(), "cleanProviderFreeRun", false),
                    "The owner-scoped test intake and queued work defer without profile writes or provider attempts.");
            }
            finally { root.Delete(true); }
        }

        private static void RunTownNotableNarrativePublicationTests(Dictionary<string, object> narrative, Action<string, bool, string> check)
        {
            string campaign = "town_narrative_contract_" + Guid.NewGuid().ToString("N"), heroId = "ready_notable";
            try
            {
                var hero = TestDict("heroStringId", heroId, "name", "Ready notable", "isNotable", true,
                    "isAlive", true, "age", 40, "homeSettlementId", "town", "narrativeVersion", 5);
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, heroId, "profile.json"), hero);
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, heroId, "narrative.json"), narrative);
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, heroId, "traits.json"), BuildTraitDocument(hero));
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, heroId, "constructed.json"), TestDict("ok", true,
                    "status", "ready", "llmUsed", true, "narrativeSchema", NarrativeSchema));
                int calls = 0;
                bool reused = RunTownProfileWorkCore(new TownProfileWork { Campaign = campaign, HeroId = heroId,
                    Hero = hero, Generation = Interlocked.Read(ref PersistenceService.CampaignReplacementGeneration) }, (c, h, p) => { calls++; throw new InvalidOperationException("Ready profile must never regenerate."); });
                check("town_complete_v5_profile_reused", reused && calls == 0, "A fully authored v5 notable is never regenerated on town entry.");
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, heroId, "editor_authority.json"), TestDict("authoritative", true));
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, heroId, "voice.json"), TestDict("editor", "preserve this exact voice"));
                byte[] before = File.ReadAllBytes(CampaignFileService.CharacterFile(campaign, heroId, "voice.json"));
                var blocked = ConstructNarrativeCharacter(campaign, heroId, hero, false, TownNotableProfileSource);
                check("town_narrative_publication_preserves_editor", !ReadBool(blocked, "ok", true)
                    && File.ReadAllBytes(CampaignFileService.CharacterFile(campaign, heroId, "voice.json")).SequenceEqual(before),
                    "The production narrative publisher refuses speculative publication after editor authority appears.");
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
                string directory = CampaignFileService.CampaignDirectory(campaign);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }
}
