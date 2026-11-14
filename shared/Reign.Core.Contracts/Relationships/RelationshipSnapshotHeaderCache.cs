using System;

namespace Reign.Core.Contracts.Relationships
{
    // Exactly one immutable queued document, never a cache of campaign history.
    public sealed class RelationshipSnapshotHeaderCache
    {
        private string? document;
        private double day;
        public double ReadDay(string current, Func<string, double> readHeader)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (!ReferenceEquals(document, current))
            {
                double observed = readHeader(current);
                if (double.IsNaN(observed) || double.IsInfinity(observed)) throw new InvalidOperationException("Invalid queued relationship day.");
                document = current; day = observed;
            }
            return day;
        }
        public void Clear() { document = null; day = 0; }
    }
}
