using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private sealed class CodexRequestTimings
        {
            public readonly DateTime CreatedUtc = DateTime.UtcNow;
            public DateTime ThreadStartUtc;
            public DateTime ThreadReadyUtc;
            public DateTime TurnStartUtc;
            public DateTime FirstTextUtc;
            public DateTime TurnCompletedUtc;
            public DateTime CleanupQueuedUtc;
            public DateTime CleanupCompletedUtc;

            public Dictionary<string, object> ToDictionary()
            {
                object queue = ElapsedMilliseconds(CreatedUtc, ThreadStartUtc);
                object threadStartup = ElapsedMilliseconds(ThreadStartUtc, ThreadReadyUtc);
                object firstText = ElapsedMilliseconds(CreatedUtc, FirstTextUtc);
                object generation = ElapsedMilliseconds(TurnStartUtc, TurnCompletedUtc);
                object cleanup = ElapsedMilliseconds(CleanupQueuedUtc, CleanupCompletedUtc);
                return new Dictionary<string, object>
                {
                    ["createdUtc"] = CreatedUtc.ToString("o"),
                    ["queueMs"] = queue,
                    ["threadStartupMs"] = threadStartup,
                    ["threadStartMs"] = threadStartup,
                    ["turnStartMs"] = ElapsedMilliseconds(CreatedUtc, TurnStartUtc),
                    ["firstTextMs"] = firstText,
                    ["generationMs"] = generation,
                    ["cleanupQueueMs"] = ElapsedMilliseconds(TurnCompletedUtc, CleanupQueuedUtc),
                    ["cleanupMs"] = cleanup,
                    ["stages"] = new Dictionary<string, object>
                    {
                        ["queue"] = queue,
                        ["threadStartup"] = threadStartup,
                        ["firstText"] = firstText,
                        ["generation"] = generation,
                        ["cleanup"] = cleanup
                    }
                };
            }

            private static object ElapsedMilliseconds(DateTime start, DateTime end)
            {
                return start == default(DateTime) || end == default(DateTime) ? null : Math.Max(0d, (end - start).TotalMilliseconds);
            }
        }

        private sealed class CodexThreadIdentity
        {
            public string CampaignId;
            public string TimelineId;
            public string LoadGenerationId;
            public string SessionId;
            public string SpeakerId;
            public string ConversationMode;
            public string VisibilityScope;
            public string Model;
            public string ReasoningEffort;
            public bool FastMode;
            public string SchemaId;
            public string SchemaState;
            public string AuthorityHash;
            public string PromptContractHash;
            public string Key;
            public bool PersistentRoleplay;

            public static bool TryCreate(CodexRuntimeRequestContext context, out CodexThreadIdentity identity)
            {
                identity = null;
                if (context == null || !context.Options.ReuseThreads || !context.HasExplicitThreadMetadata) return false;
                string timeline = FirstNonEmpty(context.TimelineId, context.LoadGenerationId);
                if (string.IsNullOrWhiteSpace(timeline) || string.IsNullOrWhiteSpace(context.AuthorityHash)) return false;
                identity = new CodexThreadIdentity
                {
                    CampaignId = context.CampaignId,
                    TimelineId = context.TimelineId,
                    LoadGenerationId = context.LoadGenerationId,
                    SessionId = context.SessionId,
                    SpeakerId = context.SpeakerId,
                    ConversationMode = context.ConversationMode,
                    VisibilityScope = context.VisibilityScope,
                    Model = context.Model,
                    ReasoningEffort = context.ResolvedReasoningEffort,
                    FastMode = context.Options.FastMode,
                    SchemaId = context.SchemaId,
                    SchemaState = context.SchemaState,
                    AuthorityHash = context.AuthorityHash,
                    PromptContractHash = context.PromptContractHash
                };
                identity.PersistentRoleplay = context.PersistentRoleplay;
                identity.Key = string.Join("\u001f", new[]
                {
                    identity.CampaignId, timeline, identity.SessionId, identity.ConversationMode,
                    identity.SpeakerId, identity.VisibilityScope, identity.Model, identity.ReasoningEffort,
                    identity.FastMode ? "fast" : "standard", identity.SchemaId, identity.SchemaState, identity.AuthorityHash, identity.PromptContractHash
                });
                return true;
            }
        }

        internal sealed class CodexThreadLease
        {
            public string ThreadId;
            public string CorrelationId;
            public string IdentityKey;
            public bool NewThread;
            public DateTime AcquiredUtc;
            public string PreviousPromptText = "";
            public string PreviousAcceptedHistorySignature = "";
        }

        internal sealed class CodexThreadReusePool
        {
            private sealed class Entry
            {
                public CodexThreadIdentity Identity;
                public string ThreadId;
                public int Turns;
                public long InputTokens;
                public int ContextInputLimitTokens;
                public DateTime LastUsedUtc;
                public bool Leased;
                public bool Finalized;
                public bool Invalidated;
                public string LastPromptText = "";
                public string PendingPromptText = "";
                public string LastAcceptedHistorySignature = "";
                public string PendingCanonicalHistorySignature = "";
            }

            private readonly object Sync = new object();
            private readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            private readonly List<string> EvictedThreadIds = new List<string>();
            private const int MaxEntries = 64;
            private const int MaxTurns = 8;
            private const long MaxInputTokens = 32000;
            private static readonly TimeSpan IdleExpiry = TimeSpan.FromMinutes(10);

            public CodexThreadLease Acquire(CodexRuntimeRequestContext context)
            {
                if (!CodexThreadIdentity.TryCreate(context, out CodexThreadIdentity identity)) return null;
                lock (Sync)
                {
                    EvictExpiredLocked();
                    EvictInvalidatedLocked();
                    Entry entry;
                    if (Entries.TryGetValue(identity.Key, out entry) && !entry.Leased && !entry.Invalidated && entry.Finalized
                        && entry.Identity.Model.Equals(identity.Model, StringComparison.OrdinalIgnoreCase)
                        && entry.Identity.ReasoningEffort.Equals(identity.ReasoningEffort, StringComparison.OrdinalIgnoreCase)
                        && entry.Identity.FastMode == identity.FastMode
                        && entry.Identity.SchemaId.Equals(identity.SchemaId, StringComparison.OrdinalIgnoreCase)
                        && entry.Identity.AuthorityHash.Equals(identity.AuthorityHash, StringComparison.Ordinal)
                        && entry.Identity.PromptContractHash.Equals(identity.PromptContractHash, StringComparison.Ordinal))
                    {
                        if (context.ContextInputLimitTokens > 0) entry.ContextInputLimitTokens = context.ContextInputLimitTokens;
                        entry.Leased = true;
                        entry.LastUsedUtc = DateTime.UtcNow;
                        return new CodexThreadLease
                        {
                            ThreadId = entry.ThreadId,
                            CorrelationId = context.CorrelationId,
                            IdentityKey = identity.Key,
                            AcquiredUtc = DateTime.UtcNow,
                            PreviousPromptText = entry.LastPromptText ?? "",
                            PreviousAcceptedHistorySignature = entry.LastAcceptedHistorySignature ?? ""
                        };
                    }
                    return null;
                }
            }

            public CodexThreadLease RegisterNew(CodexRuntimeRequestContext context, string threadId)
            {
                if (!CodexThreadIdentity.TryCreate(context, out CodexThreadIdentity identity) || string.IsNullOrWhiteSpace(threadId)) return null;
                lock (Sync)
                {
                    Entry prior;
                    if (Entries.TryGetValue(identity.Key, out prior))
                    {
                        // A leased entry belongs to an in-flight request. Never replace it with
                        // a second thread under the same identity; the caller will retain the
                        // fresh thread only for this request and clean it up on completion.
                        if (prior.Leased) return null;
                        if (!string.IsNullOrWhiteSpace(prior.ThreadId)) EvictedThreadIds.Add(prior.ThreadId);
                        Entries.Remove(identity.Key);
                    }
                    EvictToBoundLocked();
                    if (Entries.Count >= MaxEntries) return null;
                    Entry entry = new Entry
                    {
                        Identity = identity,
                        ThreadId = threadId,
                        LastUsedUtc = DateTime.UtcNow,
                        Leased = true,
                        ContextInputLimitTokens = context.ContextInputLimitTokens,
                        Finalized = false,
                        PendingPromptText = context.PromptText ?? "",
                        PendingCanonicalHistorySignature = context.CanonicalHistorySignature ?? ""
                    };
                    Entries[identity.Key] = entry;
                    return new CodexThreadLease { ThreadId = threadId, CorrelationId = context.CorrelationId, IdentityKey = identity.Key, NewThread = true, AcquiredUtc = DateTime.UtcNow };
                }
            }

            public bool TryGetAppendOnlyPrompt(CodexThreadLease lease, CodexRuntimeRequestContext context, out string delta)
            {
                delta = "";
                if (lease == null) return false;
                if (context == null || string.IsNullOrWhiteSpace(lease.PreviousAcceptedHistorySignature)
                    || string.IsNullOrWhiteSpace(context.CanonicalHistorySignature)
                    || !lease.PreviousAcceptedHistorySignature.Equals(context.CanonicalHistorySignature, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(context.CurrentTurnText)) return false;
                delta = context.PersistentRoleplay ? context.IncrementalContext : BuildCodexIncrementalTurnPrompt(context.CurrentTurnText);
                if (string.IsNullOrWhiteSpace(delta)) return false;
                return true;
            }

            public void RecordPrompt(CodexThreadLease lease, CodexRuntimeRequestContext context)
            {
                if (lease == null) return;
                lock (Sync)
                {
                    Entry entry;
                    if (Entries.TryGetValue(lease.IdentityKey, out entry) && entry.Leased
                        && entry.ThreadId.Equals(lease.ThreadId, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.PendingPromptText = context == null ? "" : context.PromptText ?? "";
                        entry.PendingCanonicalHistorySignature = context == null ? "" : context.CanonicalHistorySignature ?? "";
                    }
                }
            }

            public void RecordAcceptedHistorySignature(CodexThreadLease lease, string signature)
            {
                if (lease == null) return;
                lock (Sync)
                {
                    Entry entry;
                    if (Entries.TryGetValue(lease.IdentityKey, out entry) && entry.Leased
                        && entry.ThreadId.Equals(lease.ThreadId, StringComparison.OrdinalIgnoreCase))
                        entry.PendingCanonicalHistorySignature = signature ?? "";
                }
            }

            public string Abandon(CodexThreadLease lease)
            {
                if (lease == null) return "";
                lock (Sync)
                {
                    Entry entry;
                    if (!Entries.TryGetValue(lease.IdentityKey, out entry)) return lease.ThreadId;
                    if (entry.ThreadId.Equals(lease.ThreadId, StringComparison.OrdinalIgnoreCase))
                    {
                        Entries.Remove(lease.IdentityKey);
                        return entry.ThreadId;
                    }
                    return lease.ThreadId;
                }
            }

            public string Finalize(CodexThreadLease lease, bool accepted, bool substantiveChanged, long inputTokens)
            {
                return Finalize(lease, accepted, substantiveChanged, inputTokens, inputTokens > 0);
            }

            public string Finalize(CodexThreadLease lease, bool accepted, bool substantiveChanged, long inputTokens, bool usageKnown)
            {
                if (lease == null) return "";
                lock (Sync)
                {
                    Entry entry;
                    if (!Entries.TryGetValue(lease.IdentityKey, out entry)) return lease.ThreadId;
                    if (!entry.ThreadId.Equals(lease.ThreadId, StringComparison.OrdinalIgnoreCase)) return lease.ThreadId;
                    entry.Leased = false;
                    entry.LastUsedUtc = DateTime.UtcNow;
                    bool capacityReached = entry.Identity.PersistentRoleplay
                        ? entry.Turns + 1 >= 32 || inputTokens >= (entry.ContextInputLimitTokens > 0 ? entry.ContextInputLimitTokens : 128000)
                        : entry.Turns + 1 >= MaxTurns || entry.InputTokens + Math.Max(0, inputTokens) >= MaxInputTokens;
                    if (entry.Invalidated || !accepted || substantiveChanged || !usageKnown || capacityReached)
                    {
                        Entries.Remove(lease.IdentityKey);
                        return entry.ThreadId;
                    }
                    entry.Turns++;
                    entry.InputTokens += Math.Max(0, inputTokens);
                    if (!string.IsNullOrWhiteSpace(entry.PendingPromptText)) entry.LastPromptText = entry.PendingPromptText;
                    if (!string.IsNullOrWhiteSpace(entry.PendingCanonicalHistorySignature)) entry.LastAcceptedHistorySignature = entry.PendingCanonicalHistorySignature;
                    entry.PendingPromptText = "";
                    entry.PendingCanonicalHistorySignature = "";
                    entry.Finalized = true;
                    return "";
                }
            }

            public List<string> InvalidateAll()
            {
                lock (Sync)
                {
                    List<string> result = Entries.Values.Where(x => !x.Leased).Select(x => x.ThreadId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (Entry entry in Entries.Values.ToList())
                    {
                        if (entry.Leased) entry.Invalidated = true;
                        else Entries.Remove(entry.Identity.Key);
                    }
                    return result;
                }
            }

            public void ObserveCompaction(string threadId)
            {
                lock (Sync)
                    foreach (var entry in Entries.Values.Where(e => e.ThreadId == threadId)) entry.Invalidated = true;
            }

            public List<string> TakeEvictedThreadIds()
            {
                lock (Sync)
                {
                    List<string> result = EvictedThreadIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    EvictedThreadIds.Clear();
                    return result;
                }
            }

            public Dictionary<string, object> Diagnostics()
            {
                lock (Sync)
                {
                    EvictExpiredLocked();
                    return new Dictionary<string, object>
                    {
                        ["count"] = Entries.Count,
                        ["max"] = MaxEntries,
                        ["maxTurns"] = MaxTurns,
                        ["idleSeconds"] = (int)IdleExpiry.TotalSeconds,
                        ["maxInputTokens"] = MaxInputTokens,
                        ["leased"] = Entries.Values.Count(x => x.Leased)
                    };
                }
            }

            private void EvictExpiredLocked()
            {
                DateTime cutoff = DateTime.UtcNow.Subtract(IdleExpiry);
                foreach (string key in Entries.Where(pair => !pair.Value.Leased && pair.Value.LastUsedUtc < cutoff).Select(pair => pair.Key).ToList())
                {
                    EvictedThreadIds.Add(Entries[key].ThreadId);
                    Entries.Remove(key);
                }
            }

            private void EvictInvalidatedLocked()
            {
                foreach (string key in Entries.Where(pair => !pair.Value.Leased && pair.Value.Invalidated).Select(pair => pair.Key).ToList())
                {
                    if (!string.IsNullOrWhiteSpace(Entries[key].ThreadId)) EvictedThreadIds.Add(Entries[key].ThreadId);
                    Entries.Remove(key);
                }
            }

            private void EvictToBoundLocked()
            {
                EvictExpiredLocked();
                EvictInvalidatedLocked();
                while (Entries.Count >= MaxEntries)
                {
                    Entry oldest = Entries.Values.Where(x => !x.Leased).OrderBy(x => x.LastUsedUtc).FirstOrDefault();
                    if (oldest == null) return;
                    EvictedThreadIds.Add(oldest.ThreadId);
                    Entries.Remove(oldest.Identity.Key);
                }
            }
        }

        private sealed class CodexThreadCleanupQueue
        {
            private sealed class Item
            {
                public string ThreadId;
                public Func<string, bool> Delete;
            }

            private readonly object Sync = new object();
            private readonly Queue<Item> Queue = new Queue<Item>();
            private readonly HashSet<string> Queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private Thread Worker;
            private bool Stopping;
            private const int Capacity = 256;
            private const int MaxAttempts = 2;
            private const int TimeoutMs = 10000;

            public bool Enqueue(string threadId, Func<string, bool> delete)
            {
                if (string.IsNullOrWhiteSpace(threadId) || delete == null) return true;
                lock (Sync)
                {
                    if (Stopping) return false;
                    if (Queued.Contains(threadId)) return true;
                    if (Queue.Count >= Capacity) return false;
                    Queued.Add(threadId);
                    Queue.Enqueue(new Item { ThreadId = threadId, Delete = delete });
                    if (Worker == null || !Worker.IsAlive)
                    {
                        Worker = new Thread(Drain) { IsBackground = true, Name = "ReignCodexThreadCleanup" };
                        Worker.Start();
                    }
                    Monitor.PulseAll(Sync);
                    return true;
                }
            }

            public Dictionary<string, object> Diagnostics()
            {
                lock (Sync) return new Dictionary<string, object> { ["queued"] = Queue.Count, ["capacity"] = Capacity, ["timeoutMs"] = TimeoutMs, ["maxAttempts"] = MaxAttempts, ["stopping"] = Stopping };
            }

            public void Stop()
            {
                Thread worker;
                lock (Sync)
                {
                    Stopping = true;
                    Queue.Clear();
                    Queued.Clear();
                    worker = Worker;
                    Monitor.PulseAll(Sync);
                }
                if (worker != null && worker != Thread.CurrentThread)
                {
                    // Deletion is performed directly by the single worker and each RPC is
                    // bounded to TimeoutMs. Waiting for both bounded attempts prevents a
                    // detached Task from surviving shutdown.
                    try { worker.Join((TimeoutMs * MaxAttempts) + 1000); } catch { }
                }
                lock (Sync)
                {
                    if (Worker == null || !Worker.IsAlive)
                    {
                        Worker = null;
                        Stopping = false;
                    }
                }
            }

            private void Drain()
            {
                while (true)
                {
                    Item item;
                    lock (Sync)
                    {
                        while (!Stopping && Queue.Count == 0) Monitor.Wait(Sync, 1000);
                        if (Stopping && Queue.Count == 0) return;
                        if (Queue.Count == 0) continue;
                        item = Queue.Dequeue();
                    }
                    bool completed = false;
                    for (int attempt = 0; attempt < MaxAttempts && !completed; attempt++)
                    {
                        try
                        {
                            // QueueCodexThreadCleanup supplies a bounded RPC. Calling it on
                            // this one owned worker avoids creating an unbounded orphan task
                            // when a deletion times out.
                            completed = item.Delete(item.ThreadId);
                        }
                        catch { completed = false; }
                    }
                    lock (Sync) Queued.Remove(item.ThreadId);
                }
            }
        }

        private static void QueueCodexThreadCleanup(string threadId, Dictionary<string, object> settings, ref bool synchronousFallback)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return;
            Func<string, bool> delete = value =>
            {
                try
                {
                    CodexRpc("thread/delete", new Dictionary<string, object> { ["threadId"] = value }, Math.Min(CodexRpcTimeout(settings), 10000));
                    return true;
                }
                catch { return false; }
            };
            if (ReadBool(settings, "codexOptions.asyncThreadCleanup", false) || ReadBool(ReadDictionary(settings, "codexOptions"), "asyncThreadCleanup", false))
            {
                if (!CodexRuntimeCleanup.Enqueue(threadId, delete))
                {
                    synchronousFallback = true;
                    delete(threadId);
                }
            }
            else
            {
                synchronousFallback = true;
                delete(threadId);
            }
        }

        private static void DrainCodexThreadEvictions(Dictionary<string, object> settings, ref bool synchronousFallback)
        {
            if (settings == null) settings = LoadSettings();
            foreach (string threadId in CodexRuntimeThreads.TakeEvictedThreadIds())
                QueueCodexThreadCleanup(threadId, settings, ref synchronousFallback);
        }

        internal static void FinalizeCodexThreadRequest(Dictionary<string, object> result, Dictionary<string, object> context)
        {
            CodexRuntimeRequestContext request = CaptureCodexRequestContext(LoadSettings(), result, ReadString(context, "requestType", "dialogue"), ReadString(context, "model", ""), ReadString(context, "correlationId", ""), context);
            CodexThreadLease lease;
            lock (CodexRuntimeStateLock)
            {
                if (!CodexPendingThreadLeases.TryGetValue(request.CorrelationId, out lease))
                {
                    if (context != null) context["threadFinalization"] = TestDict("status", "no_pending_thread");
                    return;
                }
                CodexPendingThreadLeases.Remove(request.CorrelationId);
            }
            bool accepted = ReadBool(result, "accepted", ReadBool(context, "accepted", ReadBool(ReadDictionary(result, "codexFinalization"), "accepted", request.FinalizationApproved)));
            Dictionary<string, object> finalization = ReadDictionary(result, "codexFinalization");
            bool substantiveChanged = request.Repaired;
            if (finalization != null && finalization.ContainsKey("substantiveChanged")) substantiveChanged = ReadBool(finalization, "substantiveChanged", substantiveChanged);
            if (finalization != null && finalization.ContainsKey("substantivelyChanged")) substantiveChanged = ReadBool(finalization, "substantivelyChanged", substantiveChanged);
            if (context != null && context.ContainsKey("substantiveChanged")) substantiveChanged = ReadBool(context, "substantiveChanged", substantiveChanged);
            if (context != null && context.ContainsKey("substantivelyChanged")) substantiveChanged = ReadBool(context, "substantivelyChanged", substantiveChanged);
            if (result != null && result.ContainsKey("substantiveChanged")) substantiveChanged = ReadBool(result, "substantiveChanged", substantiveChanged);
            if (result != null && result.ContainsKey("substantivelyChanged")) substantiveChanged = ReadBool(result, "substantivelyChanged", substantiveChanged);
            Dictionary<string, object> usage = ReadDictionary(result, "usage") ?? ReadDictionary(ReadDictionary(result, "raw"), "usage");
            bool usageKnown = usage != null && (usage.ContainsKey("prompt_tokens") || usage.ContainsKey("input_tokens") || usage.ContainsKey("inputTokens"));
            long inputTokens = usage == null ? 0 : ReadLong(usage, "prompt_tokens", ReadLong(usage, "input_tokens", ReadLong(usage, "inputTokens", 0)));
            string acceptedHistorySignature = FirstNonEmpty(ReadString(result, "acceptedHistorySignature", ""), ReadString(context, "acceptedHistorySignature", ""), request.CanonicalHistorySignature);
            if (accepted && !substantiveChanged && !string.IsNullOrWhiteSpace(acceptedHistorySignature))
                CodexRuntimeThreads.RecordAcceptedHistorySignature(lease, acceptedHistorySignature);
            string cleanupId = CodexRuntimeThreads.Finalize(lease, accepted, substantiveChanged, inputTokens, usageKnown);
            if (context != null) context["threadFinalization"] = TestDict("status", cleanupId.Length == 0 ? "retained" : "retired",
                "accepted", accepted, "substantivelyChanged", substantiveChanged,
                "changeReason", ReadString(context, "threadChangeReason", substantiveChanged ? "changed" : "unchanged"),
                "usageKnown", usageKnown, "inputTokens", inputTokens, "threadId", lease.ThreadId);
            bool fallback = false;
            QueueCodexThreadCleanup(cleanupId, LoadSettings(), ref fallback);
        }

        private static void InvalidateCodexThreads(string reason)
        {
            List<string> threadIds = CodexRuntimeThreads.InvalidateAll();
            Dictionary<string, object> settings = LoadSettings();
            foreach (string threadId in threadIds)
            {
                bool fallback = false;
                QueueCodexThreadCleanup(threadId, settings, ref fallback);
            }
            bool evictionFallback = false;
            DrainCodexThreadEvictions(settings, ref evictionFallback);
        }

        private static void StopCodexRuntime()
        {
            InvalidateCodexThreads("runtime stopped");
            CodexRuntimeCleanup.Stop();
        }

        private static Dictionary<string, object> CodexCapabilityStatusSnapshot()
        {
            lock (CodexRuntimeStateLock)
                return new Dictionary<string, object>(CodexLastCapabilityStatus);
        }

        private static void RecordCodexCapabilityStatus(CodexRuntimeRequestContext context)
        {
            if (context == null) return;
            Dictionary<string, object> status = new Dictionary<string, object>
            {
                ["model"] = context.Model,
                ["requestedReasoningEffort"] = context.RequestedReasoningEffort,
                ["resolvedReasoningEffort"] = context.ResolvedReasoningEffort,
                ["disabledReasoningRequested"] = context.DisabledReasoningRequested,
                ["reasoningCapabilityKnown"] = context.ReasoningCapabilityKnown,
                ["reasoningResolution"] = context.ReasoningResolution,
                ["fastMode"] = new Dictionary<string, object>
                {
                    ["requested"] = context.Fast.Requested,
                    ["state"] = context.Fast.State,
                    ["applied"] = context.Fast.Applied,
                    ["field"] = context.Fast.Field,
                    ["reason"] = context.Fast.Reason
                },
                ["structuredOutput"] = new Dictionary<string, object>
                {
                    ["requested"] = context.SchemaRequested,
                    ["applied"] = context.SchemaApplied,
                    ["state"] = context.SchemaState,
                    ["schemaId"] = context.SchemaId
                },
                ["requestType"] = context.RequestType,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            };
            lock (CodexRuntimeStateLock) CodexLastCapabilityStatus = status;
        }

        private static Dictionary<string, object> CodexRuntimeDiagnostics(CodexRuntimeRequestContext context, CodexRequestTimings timings, CodexTurnAssembly assembly, bool cleanupFallback)
        {
            RecordCodexCapabilityStatus(context);
            return new Dictionary<string, object>
            {
                ["schema"] = CodexRuntimeDiagnosticsSchema,
                ["provider"] = CodexSubscriptionProvider,
                ["request"] = context == null ? new Dictionary<string, object>() : context.ToDictionary(),
                ["protocol"] = CodexRuntimeProtocol.ToDictionary(),
                ["timings"] = timings == null ? new Dictionary<string, object>() : timings.ToDictionary(),
                ["authoritativeModel"] = assembly == null ? "" : assembly.AuthoritativeModel,
                ["reportedModel"] = assembly == null ? "" : assembly.ReportedModel,
                ["servedModel"] = assembly == null ? "" : assembly.ServedModel,
                ["modelEvidence"] = assembly == null ? "" : assembly.ModelEvidence,
                ["effectiveModelKnown"] = assembly != null && !string.IsNullOrWhiteSpace(assembly.ServedModel),
                ["firstTextObserved"] = assembly != null && assembly.FirstTextUtc != default(DateTime),
                ["usageKnown"] = assembly != null && assembly.Usage != null,
                ["cleanupFallbackSynchronous"] = cleanupFallback,
                ["threadPool"] = CodexRuntimeThreads.Diagnostics(),
                ["cleanup"] = CodexRuntimeCleanup.Diagnostics()
            };
        }

        private static Dictionary<string, object> CodexRuntimeSafeModel(Dictionary<string, object> model)
        {
            string id = CodexModelId(model);
            List<string> modalities = ReadStringList(model, "inputModalities");
            if (modalities.Count == 0) modalities = new List<string> { "text", "image" };
            Dictionary<string, object> safe = new Dictionary<string, object>
            {
                ["id"] = id,
                ["displayName"] = ReadString(model, "displayName", ReadString(model, "name", id)),
                ["description"] = ReadString(model, "description", ""),
                ["hidden"] = ReadBool(model, "hidden", false),
                ["defaultReasoningEffort"] = ReadString(model, "defaultReasoningEffort", ""),
                ["supportedReasoningEfforts"] = ReadReasoningEfforts(model),
                ["inputModalities"] = modalities,
                ["supportsPersonality"] = ReadBool(model, "supportsPersonality", false),
                ["isDefault"] = ReadBool(model, "isDefault", false),
                ["upgrade"] = ReadString(model, "upgrade", ""),
                ["capabilityMetadataPreserved"] = true
            };
            foreach (string key in new[] { "model", "defaultServiceTier", "serviceTiers", "additionalSpeedTiers", "modelSpecialty", "multiAgentVersion", "upgradeInfo", "availabilityNux", "supportsFastMode", "supportedServiceTiers", "supportsParallelToolCalls", "supports_parallel_tool_calls", "modelProvider" })
                if (model != null && model.ContainsKey(key)) safe[key] = model[key];
            // Keep capability fields introduced by newer runtimes available to the UI and
            // diagnostics without copying credential-like metadata from an untrusted payload.
            if (model != null)
            {
                foreach (KeyValuePair<string, object> pair in model)
                {
                    string lower = (pair.Key ?? "").ToLowerInvariant();
                    if (safe.ContainsKey(pair.Key) || lower.Contains("token") || lower.Contains("secret")
                        || lower.Contains("password") || lower.Contains("credential") || lower.Contains("api_key") || lower.Contains("apikey"))
                        continue;
                    safe[pair.Key] = pair.Value;
                }
            }
            return safe;
        }

        private static List<Dictionary<string, object>> ReadReasoningEfforts(Dictionary<string, object> model)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            if (model == null || !model.ContainsKey("supportedReasoningEfforts") || model["supportedReasoningEfforts"] == null) return result;
            IEnumerable values = model["supportedReasoningEfforts"] as IEnumerable;
            if (values == null) return result;
            foreach (object value in values)
            {
                Dictionary<string, object> entry = value as Dictionary<string, object>;
                if (entry != null) result.Add(new Dictionary<string, object>
                {
                    ["reasoningEffort"] = ReadString(entry, "reasoningEffort", ReadString(entry, "effort", "")),
                    ["description"] = ReadString(entry, "description", "")
                });
                else if (!string.IsNullOrWhiteSpace(Convert.ToString(value))) result.Add(new Dictionary<string, object> { ["reasoningEffort"] = Convert.ToString(value), ["description"] = "" });
            }
            return result;
        }

        private static bool? ReadNullableBool(Dictionary<string, object> source, string key, bool? fallback)
        {
            if (source == null || !source.ContainsKey(key) || source[key] == null) return fallback;
            bool value;
            return bool.TryParse(Convert.ToString(source[key]), out value) ? (bool?)value : fallback;
        }

        private static string NormalizeCodexReasoningMode(string value)
        {
            string mode = (value ?? "").Trim().ToLowerInvariant();
            return mode == "direct" || mode == "off" || mode == "none" ? "direct" : "selective";
        }

        private static string NormalizeCodexEffort(string value)
        {
            string effort = (value ?? "").Trim().ToLowerInvariant();
            return effort == "none" || effort == "minimal" || effort == "low" || effort == "medium" || effort == "high" || effort == "xhigh" || effort == "max" || effort == "ultra" ? effort : "medium";
        }

        private static string BuildPromptTextForRuntime(Dictionary<string, object> payload)
        {
            if (payload == null) return "";
            List<Dictionary<string, object>> messages = ReadDictionaryList(payload, "messages");
            if (messages == null || messages.Count == 0) return "";
            try { return BuildCodexPrompt(messages); }
            catch { return ""; }
        }

        private static string BuildCodexIncrementalTurnPrompt(string currentTurnText)
        {
            return "Continue the established Reign conversation using the authoritative prior thread context. Process only this new canonical turn; preserve the existing identity, secrecy, relationships, and world state. Return only the requested response.\n\n--- CURRENT CANONICAL TURN ---\n" + (currentTurnText ?? "").Trim();
        }

        private static string HashCodexText(string value)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
            }
            catch { return ""; }
        }

        private static List<string> SupportedCodexReasoningEfforts(string model, out bool known)
        {
            List<string> supported = new List<string>();
            known = false;
            lock (CodexStateLock)
            {
                Dictionary<string, object> entry = CodexModels.FirstOrDefault(x => CodexModelId(x).Equals(model ?? "", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return supported;
                List<Dictionary<string, object>> advertised = ReadReasoningEfforts(entry);
                known = advertised.Count > 0;
                foreach (Dictionary<string, object> effort in advertised)
                {
                    string value = NormalizeCodexEffort(ReadString(effort, "reasoningEffort", ""));
                    if (!string.IsNullOrWhiteSpace(value) && !supported.Contains(value, StringComparer.OrdinalIgnoreCase)) supported.Add(value);
                }
            }
            return supported;
        }

        private static string ResolveRequestedReasoningEffort(string model, string requested, bool disabled,
            out bool capabilityKnown, out string resolution)
        {
            requested = NormalizeCodexEffort(requested);
            List<string> supported = SupportedCodexReasoningEfforts(model, out capabilityKnown);
            if (supported.Count == 0)
            {
                resolution = "The selected model did not expose supported reasoning efforts; the requested value was preserved.";
                capabilityKnown = false;
                return requested;
            }
            if (disabled)
            {
                if (supported.Any(value => value.Equals("none", StringComparison.OrdinalIgnoreCase)))
                {
                    resolution = "Disabled reasoning is supported by the selected model.";
                    return "none";
                }
                string lowestDisabled = LowestCodexReasoningEffort(supported);
                resolution = "The selected model cannot disable reasoning; its lowest advertised effort was applied.";
                return lowestDisabled;
            }
            if (supported.Any(value => value.Equals(requested, StringComparison.OrdinalIgnoreCase)))
            {
                resolution = "The requested reasoning effort is advertised by the selected model.";
                return requested;
            }
            string nearest = ClosestCodexReasoningEffort(supported, requested);
            resolution = "The requested reasoning effort is not advertised by the selected model; the closest advertised effort was applied.";
            return nearest;
        }

        private static string LowestCodexReasoningEffort(List<string> supported)
        {
            string[] order = { "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra" };
            return order.FirstOrDefault(value => supported.Contains(value, StringComparer.OrdinalIgnoreCase)) ?? supported[0];
        }

        private static string ClosestCodexReasoningEffort(List<string> supported, string requested)
        {
            string[] order = { "minimal", "low", "medium", "high", "xhigh", "max", "ultra" };
            int requestedIndex = Array.IndexOf(order, requested);
            if (requestedIndex < 0) return LowestCodexReasoningEffort(supported);
            string lower = order.Take(requestedIndex + 1).Reverse().FirstOrDefault(value => supported.Contains(value, StringComparer.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(lower) ? LowestCodexReasoningEffort(supported) : lower;
        }
    }
}
