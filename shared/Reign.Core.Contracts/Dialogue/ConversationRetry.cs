#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Reign.Core.Contracts.Dialogue
{
    // A retry belongs to one visible screen/save scope and one original turn.
    // It is never serialized or automatically replayed after reopening/loading.
    public sealed class ConversationRetryTurn
    {
        public string Scope { get; }
        public string Text { get; }
        public string CorrelationId { get; }
        public string SceneTurnId { get; }
        public ConversationRetryTurn(string scope, string text, string correlationId, string sceneTurnId)
        { Scope = scope ?? ""; Text = text ?? ""; CorrelationId = correlationId ?? ""; SceneTurnId = sceneTurnId ?? ""; }
    }

    public sealed class ConversationRetryState
    {
        private readonly object _gate = new object();
        private ConversationRetryTurn _pending;
        private bool _inFlight;
        public bool HasPending { get { lock (_gate) return _pending != null && !_inFlight; } }
        public void Failed(ConversationRetryTurn turn)
        {
            if (turn == null || string.IsNullOrWhiteSpace(turn.Scope) || string.IsNullOrWhiteSpace(turn.CorrelationId))
                throw new ArgumentException("Retry requires an original turn and exact scope.");
            lock (_gate) { _pending = turn; _inFlight = false; }
        }
        public ConversationRetryTurn Begin(string scope)
        {
            lock (_gate)
            {
                if (_pending == null || _inFlight) return null;
                if (_pending.Scope != scope) { _pending = null; return null; }
                _inFlight = true;
                return _pending;
            }
        }
        public void Clear() { lock (_gate) { _pending = null; _inFlight = false; } }
    }

    // Serializes only the SAME logical turn, including a transport retry while
    // its original request is still running. Unrelated conversations stay free.
    public sealed class ConversationTurnDispatchGate
    {
        private sealed class Entry { internal readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1); internal int Users; }
        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private sealed class Lease : IDisposable
        {
            private Action _release;
            internal Lease(Action release) { _release = release; }
            public void Dispose() { Interlocked.Exchange(ref _release, null)?.Invoke(); }
        }
        public IDisposable Enter(string key, int timeoutMs, Action checkCurrent = null)
        {
            Entry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out entry))
                {
                    if (_entries.Count >= 256) return null;
                    _entries[key] = entry = new Entry();
                }
                if (entry.Users >= 16) return null;
                entry.Users++;
            }
            bool acquired = false, leased = false;
            void Release()
            {
                if (acquired) entry.Semaphore.Release();
                lock (_gate) if (--entry.Users == 0) { _entries.Remove(key); entry.Semaphore.Dispose(); }
            }
            try
            {
                var elapsed = Stopwatch.StartNew();
                while (!(acquired = entry.Semaphore.Wait(0)))
                {
                    checkCurrent?.Invoke();
                    if (elapsed.ElapsedMilliseconds >= timeoutMs) return null;
                    Thread.Sleep(Math.Min(25, Math.Max(1, timeoutMs - (int)elapsed.ElapsedMilliseconds)));
                }
                checkCurrent?.Invoke();
                leased = true;
                return new Lease(Release);
            }
            finally { if (!leased) Release(); }
        }
    }
}
