using System;
using System.Collections.Generic;

namespace ReignBeta.Shared.Characters
{
    /// <summary>Suppresses settlement-entry commerce only for a synchronously relocated party.</summary>
    public sealed class NpcPartyRelocationScope : IDisposable
    {
        [ThreadStatic] private static Dictionary<string, int>? _active;
        private readonly string _partyId;
        private readonly int _threadId;
        private bool _disposed;

        private NpcPartyRelocationScope(string partyId)
        {
            if (string.IsNullOrWhiteSpace(partyId)) throw new ArgumentException("A native party ID is required.", nameof(partyId));
            _partyId = partyId;
            _threadId = Environment.CurrentManagedThreadId;
            if (_active == null) _active = new Dictionary<string, int>(StringComparer.Ordinal);
            _active.TryGetValue(partyId, out int count);
            _active[partyId] = count + 1;
        }

        public static NpcPartyRelocationScope Enter(string partyId) => new NpcPartyRelocationScope(partyId);
        public static bool SuppressesCommerceFor(string? partyId) => partyId != null && _active != null && _active.ContainsKey(partyId);

        public void Dispose()
        {
            if (_disposed) return;
            if (_threadId != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("A synchronous relocation scope must be disposed on its owning thread.");
            var active = _active ?? throw new InvalidOperationException("The relocation scope is missing on its owning thread.");
            int count = active[_partyId];
            if (count == 1) active.Remove(_partyId);
            else active[_partyId] = count - 1;
            _disposed = true;
        }
    }
}
