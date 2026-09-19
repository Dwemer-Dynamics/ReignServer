using System;
using Npgsql;

namespace ReignBetaServer
{
    // All campaign schemas share one configured database endpoint. During an
    // outage they must share one recovery probe, rather than one timeout each.
    internal sealed class DatabaseAvailability
    {
        internal static readonly DatabaseAvailability Shared = new DatabaseAvailability();
        private readonly object gate = new object();
        private readonly Func<DateTime> clock;
        private DateTime retryAt;
        private int failures;
        private long generation;
        private bool probe;

        internal DatabaseAvailability(Func<DateTime> clock = null)
        {
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        internal bool BackingOff { get { lock (gate) return failures > 0; } }

        internal long Enter()
        {
            lock (gate)
            {
                if (failures > 0)
                {
                    if (probe || clock() < retryAt)
                        throw new InvalidOperationException("PostgreSQL is unavailable; the shared recovery probe is backing off.");
                    probe = true;
                }
                return generation;
            }
        }

        internal void Complete(long ticket, bool unavailable)
        {
            lock (gate)
            {
                // A connection begun before the current failure cannot close the breaker.
                if (ticket != generation) return;
                probe = false;
                if (unavailable)
                {
                    failures = Math.Min(failures + 1, 6);
                    retryAt = clock().AddSeconds(Math.Min(30, 1 << (failures - 1)));
                    generation++;
                }
                else { failures = 0; retryAt = DateTime.MinValue; }
            }
        }

        internal static void Open(NpgsqlConnection connection)
        {
            long ticket = Shared.Enter();
            try { connection.Open(); Shared.Complete(ticket, false); }
            catch (Exception error)
            {
                var postgres = error as PostgresException;
                bool unavailable = error is TimeoutException
                    || error is System.IO.IOException
                    || (error is NpgsqlException && postgres == null)
                    || (postgres != null && (postgres.SqlState.StartsWith("08", StringComparison.Ordinal)
                        || postgres.SqlState == "57P01" || postgres.SqlState == "57P02" || postgres.SqlState == "57P03"));
                Shared.Complete(ticket, unavailable);
                throw;
            }
        }
    }
}
