using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string TownNotableProfileSource = "town_entry_prewarm";
        [ThreadStatic] private static TownProfileWork CurrentTownProfileWork;
        private static readonly TownProfileQueue TownProfiles = new TownProfileQueue();

        private sealed class TownProfileWork
        {
            internal string Key, Campaign, HeroId, SettingsHash;
            internal long Generation;
            internal DateTime CapturedUtc = DateTime.UtcNow;
            internal Dictionary<string, object> Hero;
            internal volatile bool Foreground;
            internal Func<TownProfileWork, bool> Run;
            internal Action<Exception> Failed;
        }

        // One bounded pool across visits/campaigns. No unbounded Task per hero and
        // no occupation of the single shared maintenance-scheduler thread.
        private sealed class TownProfileQueue
        {
            private readonly object gate = new object();
            private readonly Queue<TownProfileWork> waiting = new Queue<TownProfileWork>();
            private readonly Dictionary<string, TownProfileWork> pending = new Dictionary<string, TownProfileWork>(StringComparer.Ordinal);
            private readonly Dictionary<string, (string Settings, DateTime Until)> failures = new Dictionary<string, (string, DateTime)>();
            private int workers;
            private readonly int capacity;
            internal TownProfileQueue(int capacity = 256) { this.capacity = capacity; }
            internal int Count { get { lock (gate) return pending.Count; } }
            internal void Promote(string key) { lock (gate) if (pending.TryGetValue(key, out var item)) item.Foreground = true; }
            internal string Enqueue(TownProfileWork item, int concurrency, Func<TownProfileWork, bool> run, Action<Exception> failed)
            {
                lock (gate)
                {
                    foreach (var key in failures.Where(p => p.Value.Until <= DateTime.UtcNow).Select(p => p.Key).ToList()) failures.Remove(key);
                    if (pending.ContainsKey(item.Key)) return "pending";
                    if (failures.TryGetValue(item.Key, out var failure) && failure.Settings == item.SettingsHash) return "cooldown";
                    if (pending.Count >= capacity) return "full";
                    item.Run = run; item.Failed = failed;
                    pending.Add(item.Key, item); waiting.Enqueue(item);
                    int limit = Math.Clamp(concurrency, 1, 16);
                    while (workers < Math.Min(limit, pending.Count))
                    {
                        workers++;
                        var worker = new Thread(Drain) { IsBackground = true, Name = "Reign town notable profiles" };
                        // Provider I/O is synchronous and can last minutes. Dedicated,
                        // bounded workers fill the allowance immediately without
                        // starving HTTP/foreground work or inheriting its trace.
                        try
                        {
                            if (ExecutionContext.IsFlowSuppressed()) worker.Start();
                            else using (ExecutionContext.SuppressFlow()) worker.Start();
                        }
                        catch
                        {
                            workers--;
                            if (workers == 0) { waiting.Clear(); pending.Clear(); }
                            throw;
                        }
                    }
                    return "queued";
                }
            }
            private void Drain()
            {
                while (true)
                {
                    TownProfileWork item;
                    lock (gate)
                    {
                        if (waiting.Count == 0) { workers--; return; }
                        item = waiting.Dequeue();
                    }
                    bool ok = false;
                    try { ok = item.Run(item); }
                    catch (Exception ex) { try { item.Failed(ex); } catch { } }
                    finally
                    {
                        lock (gate)
                        {
                            pending.Remove(item.Key);
                            if (!ok)
                            {
                                // Speculative failures must never disable normal first
                                // contact or trigger a charge on every repeated visit.
                                if (failures.Count >= capacity) failures.Remove(failures.OrderBy(p => p.Value.Until).First().Key);
                                failures[item.Key] = (item.SettingsHash, DateTime.UtcNow.AddMinutes(10));
                            }
                        }
                    }
                }
            }
        }

        private static string TownProfileKey(long generation, string campaign, string hero)
            => generation + "|" + campaign.ToLowerInvariant() + "|" + hero.ToLowerInvariant();

        private static Dictionary<string, object> WithCharacterConstructionGate(string campaign, string hero, string source,
            Func<Dictionary<string, object>> construct)
        {
            if (source != TownNotableProfileSource)
                TownProfiles.Promote(TownProfileKey(Interlocked.Read(ref CampaignReplacementGeneration), campaign, hero));
            using (EnterCampaignWorkLock(NarrativeConstructionLocks.GetOrAdd(campaign + "|" + hero, _ => new object())))
                return construct();
        }

        private static bool TownProfileGenerationCurrent(long generation)
            => generation == Interlocked.Read(ref CampaignReplacementGeneration);

        private static Dictionary<string, object> PrewarmTownNotableProfiles(Dictionary<string, object> payload)
        {
            string campaign = ReadString(payload, "campaignId", "");
            if (string.IsNullOrWhiteSpace(campaign)) return TestDict("ok", false, "error", "campaignId is required.");
            long generation = Interlocked.Read(ref CampaignReplacementGeneration);
            if (ReadString(payload, "operation", "enqueue") == "scope")
                return TestDict("ok", true, "generation", generation);
            if (ReadString(payload, "operation", "enqueue") != "enqueue")
                return TestDict("ok", false, "error", "Unknown prewarm operation.");
            if (ReadLong(payload, "generation", -1) != generation)
                return TestDict("ok", false, "result", "campaign_replaced");
            string town = ReadString(payload, "settlementId", "");
            var heroes = ReadDictionaryList(payload, "heroes");
            if (string.IsNullOrWhiteSpace(town) || ReadString(payload, "settlementType", "") != "town" || heroes.Count > 128)
                return TestDict("ok", false, "error", "One town and at most 128 notable profiles are required.");
            var settings = LoadSettings();
            if (!ReadBool(settings, "constructCharactersWithLlm", true))
                return TestDict("ok", true, "result", "construction_disabled", "queued", 0);
            int concurrency = Math.Clamp(ReadInt(settings, "providerMaxConcurrency", 4), 1, 16);
            string settingsHash = SettingsFingerprint(settings);
            DateTime captured = DateTime.TryParse(ReadString(payload, "capturedUtc", ""), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var capture) ? capture.ToUniversalTime() : DateTime.MinValue;
            if (captured > DateTime.UtcNow) captured = DateTime.UtcNow;
            var counts = new Dictionary<string, object>();
            foreach (var hero in heroes.Where(h => TownNotableCandidate(h, town))
                .GroupBy(CharacterIdFrom, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                string heroId = CharacterIdFrom(hero);
                var work = new TownProfileWork { Campaign = campaign, HeroId = heroId, Generation = generation,
                    Key = TownProfileKey(generation, campaign, heroId), SettingsHash = settingsHash,
                    CapturedUtc = captured, Hero = DeepCloneProfileDictionary(hero) };
                string result = TownProfiles.Enqueue(work, concurrency, RunTownProfileWork,
                    ex => LogOperational("notable_prewarm.failed", TestDict("campaignId", campaign, "error", LimitText(ex.Message, 500))));
                counts[result] = ReadInt(counts, result, 0) + 1;
            }
            var response = TestDict("ok", true, "schema", "reign-town-notable-prewarm-v1", "generation", generation,
                "settlementId", town, "concurrency", concurrency, "counts", counts, "pending", TownProfiles.Count);
            LogOperational("notable_prewarm.queued", response);
            return response;
        }

        private static bool TownNotableCandidate(Dictionary<string, object> hero, string town)
            => !string.IsNullOrWhiteSpace(CharacterIdFrom(hero)) && ReadBool(hero, "isNotable", false)
                && ReadBool(hero, "isAlive", false)
                && (ReadString(hero, "currentSettlementId", "") == town || ReadString(hero, "homeSettlementId", "") == town);

        private static bool RunTownProfileWork(TownProfileWork work)
            => RunTownProfileWorkCore(work, (campaign, heroId, hero) => ReadInt(hero, "narrativeVersion", 0) >= 5
                ? EnsureCharacterConstructed(campaign, heroId, hero, TownNotableProfileSource)
                : ConstructCharacterFiles(campaign, heroId, hero, true, true, TownNotableProfileSource));

        private static bool RunTownProfileWorkCore(TownProfileWork work,
            Func<string, string, Dictionary<string, object>, Dictionary<string, object>> construct)
        {
            // The body stays synchronous on this worker. HTTP-equivalent leases
            // release around provider/hero-lock waits and fence every publication
            // after restore, import or deletion, including authoring checkpoints.
            var access = EnterCampaignRequestAccess("/notables/profiles/prewarm");
            try
            {
                if (!TownProfileGenerationCurrent(work.Generation) || ShutdownRequested) return true;
                CurrentTownProfileWork = work;
                using (EnterCampaignWorkLock(NarrativeConstructionLocks.GetOrAdd(work.Campaign + "|" + work.HeroId, _ => new object())))
                {
                    ThrowIfCampaignRequestReplaced();
                    var latest = ReadJsonObject(CharacterFile(work.Campaign, work.HeroId, "profile.json"));
                    var hero = MergeProfileForConstruction(work.Campaign, work.HeroId, work.Hero);
                    if (DateTime.TryParse(ReadString(latest, "updatedUtc", ""), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var updated) && updated.ToUniversalTime() > work.CapturedUtc)
                        foreach (var pair in latest) hero[pair.Key] = pair.Value;
                    if (!ReadBool(latest, "isAlive", true)) return true;
                    if (CharacterHasEditorCorrections(work.Campaign, work.HeroId) || !ReadBool(hero, "isAlive", true)) return true;
                    if (TownNotableProfileComplete(work.Campaign, work.HeroId, hero)) return true;
                    lock (NarrativeStateLock)
                    {
                        if (CharacterHasEditorCorrections(work.Campaign, work.HeroId)) return true;
                        UpsertCharacterProfile(work.Campaign, hero);
                    }
                    bool ok = RunSpeculativeTownProfile(work.Campaign, work.HeroId, () => construct(work.Campaign, work.HeroId, hero),
                        () => TownNotableProfileComplete(work.Campaign, work.HeroId, hero));
                    if (ok)
                        lock (NarrativeStateLock)
                        {
                            if (CharacterHasEditorCorrections(work.Campaign, work.HeroId)) return true;
                            var enrichment = ReadJsonObject(CharacterFile(work.Campaign, work.HeroId, "enrichment.json"));
                            enrichment["version"] = 2; enrichment["status"] = "completed_on_town_entry";
                            enrichment["source"] = TownNotableProfileSource; enrichment["campaignId"] = work.Campaign;
                            enrichment["heroStringId"] = work.HeroId; enrichment["completedUtc"] = DateTime.UtcNow.ToString("o");
                            WriteJsonObject(CharacterFile(work.Campaign, work.HeroId, "enrichment.json"), enrichment);
                        }
                    LogOperational("notable_prewarm.completed", TestDict("campaignId", work.Campaign, "heroStringId", work.HeroId, "ok", ok));
                    return ok;
                }
            }
            catch (CampaignRequestReplacedException) { return true; }
            catch (Exception ex)
            {
                LogOperational("notable_prewarm.failed", TestDict("campaignId", work.Campaign,
                    "heroStringId", work.HeroId, "error", LimitText(ex.Message, 500)));
                return false;
            }
            finally { CurrentTownProfileWork = null; ExitCampaignRequestAccess(access); }
        }

        private static bool TownNotableProfileComplete(string campaign, string hero, Dictionary<string, object> profile)
        {
            var status = ReadJsonObject(CharacterFile(campaign, hero, "constructed.json"));
            return IsCharacterConstructionReady(status) && (ReadInt(profile, "narrativeVersion", 0) >= 5
                ? NarrativeDocumentReady(ReadJsonObject(CharacterFile(campaign, hero, "narrative.json")))
                : ReadBool(status, "llmUsed", false));
        }

        private static bool TownProfilePublicationAllowed(string campaign, string hero)
            => !CharacterHasEditorCorrections(campaign, hero)
                && ReadBool(ReadJsonObject(CharacterFile(campaign, hero, "profile.json")), "isAlive", true);

        private static bool RunSpeculativeTownProfile(string campaign, string hero, Func<Dictionary<string, object>> construct, Func<bool> complete = null)
        {
            bool ownsLease = CurrentCampaignRequestLease == null;
            var access = ownsLease ? EnterCampaignRequestAccess("/notables/profiles/prewarm") : CampaignRequestAccess.None;
            var lease = CurrentCampaignRequestLease;
            try
            {
                string path = CharacterFile(campaign, hero, "constructed.json");
                byte[] previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
                bool succeeded = false, replaced = false;
                try { succeeded = IsCharacterConstructionReady(construct()) && (complete?.Invoke() ?? true); return succeeded; }
                catch (CampaignRequestReplacedException) { replaced = true; throw; }
                finally
                {
                    // The same read lease excludes restore during this rollback.
                    // Never infer validity from a null lease or mask replacement.
                    if (!succeeded && !replaced && lease != null && !lease.Invalid
                        && TownProfileGenerationCurrent(lease.Generation) && CampaignDataGate.IsReadLockHeld)
                        lock (NarrativeStateLock)
                        {
                            ThrowIfCampaignRequestReplaced();
                            if (!CharacterHasEditorCorrections(campaign, hero))
                            lock (FileLock)
                            {
                                if (previous != null)
                                {
                                    string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                                    try { File.WriteAllBytes(temp, previous); File.Move(temp, path, true); }
                                    finally { if (File.Exists(temp)) File.Delete(temp); }
                                }
                                else if (File.Exists(path)) File.Delete(path);
                            }
                        }
                }
            }
            finally
            {
                if (ownsLease) ExitCampaignRequestAccess(access);
            }
        }
    }
}
