using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Reign.Core.Contracts.Relationships
{
    // Process-local work only. Payload scope is captured by the native caller;
    // no campaign object or durable input lives here; completed responses are
    // never reused by a later upload attempt.
    public sealed class RelationshipUploadGate<T>
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, Task<T>> active = new Dictionary<string, Task<T>>(StringComparer.Ordinal);
        private readonly int capacity;
        public RelationshipUploadGate(int capacity = 4)
        { if (capacity < 1 || capacity > 16) throw new ArgumentOutOfRangeException(nameof(capacity)); this.capacity = capacity; }

        public Task<T> Run(string scope, Func<Task<T>> work)
        {
            if (string.IsNullOrEmpty(scope)) throw new ArgumentException("Upload scope is required.", nameof(scope));
            if (work == null) throw new ArgumentNullException(nameof(work));
            lock (sync)
            {
                foreach (string key in active.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToArray()) active.Remove(key);
                if (active.TryGetValue(scope, out Task<T> running)) return running;
                if (active.Count >= capacity) throw new InvalidOperationException("Relationship upload scope capacity is occupied.");
                // An async method's pre-await work must not execute on the
                // application thread that starts or joins an upload.
                var task = Task.Run(work);
                active.Add(scope, task);
                return task;
            }
        }
    }
}
