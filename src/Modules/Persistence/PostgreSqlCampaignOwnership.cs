using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Npgsql;

namespace ReignBetaServer
{
    // Records the campaigns created while a scope (a verification run) is active, so that its
    // cleanup drops only what it created. Creations made by this process with no scope at all
    // (work that does not flow the run's execution context) are kept separately.
    internal sealed class CampaignCreationScope
    {
        private readonly ConcurrentDictionary<string, byte> created =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> unattributed =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        internal CampaignCreationScope(CampaignCreationScope parent)
        {
            Parent = parent;
        }

        internal CampaignCreationScope Parent { get; }

        internal IReadOnlyCollection<string> CreatedCampaignIds => created.Keys.ToList();

        internal IReadOnlyCollection<string> UnattributedCampaignIds => unattributed.Keys.ToList();

        internal void RecordCreated(string campaignId)
        {
            for (CampaignCreationScope scope = this; scope != null; scope = scope.Parent)
                scope.created[campaignId] = 0;
        }

        internal void RecordUnattributed(string campaignId)
        {
            unattributed[campaignId] = 0;
        }
    }

    internal static partial class ReignPostgreSqlStorage
    {
        // Each process holds a session-level advisory lock (on one dedicated connection) for the
        // internal campaigns it creates or opens. Startup cleanup skips internal campaigns whose
        // owner lock is held by a live session and drops the rest exactly as before. The lock ends
        // with the owning process, so orphans of dead processes are still removed.
        private const string CampaignOwnerLockPrefix = "reign_campaign_owner:";
        private const int MaximumOwnedInternalCampaigns = 1024;
        private static readonly AsyncLocal<CampaignCreationScope> CurrentCampaignCreationScope =
            new AsyncLocal<CampaignCreationScope>();
        private static readonly ConcurrentDictionary<CampaignCreationScope, byte> ActiveCampaignCreationScopes =
            new ConcurrentDictionary<CampaignCreationScope, byte>();
        private static readonly object CampaignOwnershipLock = new object();
        private static readonly HashSet<string> OwnedInternalCampaigns =
            new HashSet<string>(StringComparer.Ordinal);
        private static NpgsqlConnection CampaignOwnerConnection;
        private static bool CampaignOwnershipCapLogged;

        internal static CampaignCreationScope BeginCampaignCreationScope()
        {
            CampaignCreationScope scope = new CampaignCreationScope(CurrentCampaignCreationScope.Value);
            ActiveCampaignCreationScopes[scope] = 0;
            CurrentCampaignCreationScope.Value = scope;
            return scope;
        }

        internal static void EndCampaignCreationScope(CampaignCreationScope scope)
        {
            if (scope == null)
                return;
            ActiveCampaignCreationScopes.TryRemove(scope, out byte ignored);
            if (CurrentCampaignCreationScope.Value == scope)
                CurrentCampaignCreationScope.Value = scope.Parent;
        }

        private static void RecordCampaignCreated(string campaignId)
        {
            // A long-lived worker started during a run keeps that run's scope in its execution
            // context after the run ends. Attribute only to a scope that is still active; a
            // creation under ended scopes only is treated as unattributed.
            CampaignCreationScope current = CurrentCampaignCreationScope.Value;
            while (current != null && !ActiveCampaignCreationScopes.ContainsKey(current))
                current = current.Parent;
            if (current != null)
            {
                current.RecordCreated(campaignId);
                return;
            }
            foreach (CampaignCreationScope active in ActiveCampaignCreationScopes.Keys)
                active.RecordUnattributed(campaignId);
        }

        internal static bool OwnsInternalCampaign(string campaignId)
        {
            lock (CampaignOwnershipLock)
                return OwnedInternalCampaigns.Contains(NormalizeCampaignId(campaignId));
        }

        private static void ClaimInternalCampaignOwnership(string campaignId)
        {
            if (!Program.IsInternalCampaignId(campaignId))
                return;
            lock (CampaignOwnershipLock)
            {
                if (OwnedInternalCampaigns.Contains(campaignId))
                    return;
                if (OwnedInternalCampaigns.Count >= MaximumOwnedInternalCampaigns)
                {
                    if (!CampaignOwnershipCapLogged)
                    {
                        CampaignOwnershipCapLogged = true;
                        Program.LogOperational("campaign.ownership_cap_reached", new Dictionary<string, object>
                        {
                            ["maximum"] = MaximumOwnedInternalCampaigns,
                            ["campaignId"] = campaignId
                        });
                    }
                    return;
                }
                try
                {
                    if (CampaignOwnerConnection == null)
                        CampaignOwnerConnection = OpenCampaignOwnerSessionConnection();
                    if (TryAcquireCampaignOwnerLock(CampaignOwnerConnection, campaignId))
                        OwnedInternalCampaigns.Add(campaignId);
                }
                catch (Exception error)
                {
                    // The session, and with it every lock, may be gone (for example after a
                    // PostgreSQL restart). Reconnect once and re-claim everything still owned.
                    // Ownership only protects against other processes' cleanup; without it the
                    // campaign behaves exactly as before.
                    ReconnectCampaignOwnerSession("claim", error,
                        OwnedInternalCampaigns.Append(campaignId).ToList());
                }
            }
        }

        // Called with CampaignOwnershipLock held. Replaces the owner session and re-claims the
        // given campaigns; any that cannot be re-claimed are no longer treated as owned.
        private static void ReconnectCampaignOwnerSession(
            string operation,
            Exception error,
            List<string> campaignIds)
        {
            ResetCampaignOwnerConnection();
            int reclaimed = 0;
            try
            {
                CampaignOwnerConnection = OpenCampaignOwnerSessionConnection();
                foreach (string id in campaignIds)
                {
                    if (TryAcquireCampaignOwnerLock(CampaignOwnerConnection, id))
                    {
                        OwnedInternalCampaigns.Add(id);
                        reclaimed++;
                    }
                }
                Program.LogOperational("campaign.ownership_session_reconnected", new Dictionary<string, object>
                {
                    ["operation"] = operation,
                    ["error"] = error.GetType().Name + ": " + error.Message,
                    ["requested"] = campaignIds.Count,
                    ["reclaimed"] = reclaimed
                });
            }
            catch (Exception retryError)
            {
                ResetCampaignOwnerConnection();
                Program.LogOperational("campaign.ownership_session_failed", new Dictionary<string, object>
                {
                    ["operation"] = operation,
                    ["error"] = error.GetType().Name + ": " + error.Message,
                    ["retryError"] = retryError.GetType().Name + ": " + retryError.Message,
                    ["requested"] = campaignIds.Count,
                    ["reclaimed"] = reclaimed
                });
            }
        }

        // Self-test hook: ends the owner session as a PostgreSQL restart would, leaving the
        // process's record of owned campaigns unchanged.
        internal static void BreakCampaignOwnerSessionForSelfTest()
        {
            lock (CampaignOwnershipLock)
            {
                try
                {
                    CampaignOwnerConnection?.Dispose();
                }
                catch { }
            }
        }

        internal static void ReleaseInternalCampaignOwnership(string campaignId)
        {
            lock (CampaignOwnershipLock)
            {
                if (!OwnedInternalCampaigns.Remove(campaignId))
                    return;
                try
                {
                    ReleaseCampaignOwnerLock(CampaignOwnerConnection, campaignId);
                }
                catch (Exception error)
                {
                    ReconnectCampaignOwnerSession("release", error,
                        OwnedInternalCampaigns.ToList());
                }
            }
        }

        // Drops each given campaign unless a live session holds its owner lock, and returns the
        // ids kept for a live owner. When ownership cannot be probed, campaigns are dropped as
        // they were before owner locks existed.
        internal static HashSet<string> DropInternalCampaignsWithoutLiveOwner(
            IEnumerable<string> campaignIds)
        {
            HashSet<string> retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            NpgsqlConnection probe = null;
            try
            {
                probe = OpenCampaignOwnerSessionConnection();
            }
            catch
            {
                probe = null;
            }
            try
            {
                foreach (string campaignId in campaignIds ?? Enumerable.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(campaignId))
                        continue;
                    bool held = false;
                    if (probe != null)
                    {
                        try
                        {
                            held = TryAcquireCampaignOwnerLock(probe, campaignId);
                        }
                        catch
                        {
                            probe.Dispose();
                            probe = null;
                        }
                        if (probe != null && !held)
                        {
                            retained.Add(campaignId);
                            continue;
                        }
                    }
                    try
                    {
                        DropCampaign(campaignId);
                    }
                    catch { }
                    if (held)
                    {
                        try
                        {
                            ReleaseCampaignOwnerLock(probe, campaignId);
                        }
                        catch
                        {
                            probe.Dispose();
                            probe = null;
                        }
                    }
                }
            }
            finally
            {
                probe?.Dispose();
            }
            return retained;
        }

        // A session that holds the owner lock for a campaign, as another process would.
        internal static NpgsqlConnection OpenCampaignOwnerSessionConnection()
        {
            EnsureInfrastructure();
            NpgsqlConnectionStringBuilder builder =
                new NpgsqlConnectionStringBuilder(Options.BuildConnectionString())
                {
                    Pooling = false,
                    MinPoolSize = 0,
                    ApplicationName = "Bannerlord Reign campaign owner"
                };
            NpgsqlConnection connection = new NpgsqlConnection(builder.ConnectionString);
            try
            {
                DatabaseAvailability.Open(connection);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        internal static bool TryAcquireCampaignOwnerLock(
            NpgsqlConnection connection,
            string campaignId)
        {
            using (NpgsqlCommand command = new NpgsqlCommand(
                "SELECT pg_try_advisory_lock(hashtextextended(@key, 0));", connection))
            {
                command.CommandTimeout = 30;
                command.Parameters.AddWithValue("key", CampaignOwnerLockPrefix + campaignId);
                return Convert.ToBoolean(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private static void ReleaseCampaignOwnerLock(
            NpgsqlConnection connection,
            string campaignId)
        {
            using (NpgsqlCommand command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtextextended(@key, 0));", connection))
            {
                command.CommandTimeout = 30;
                command.Parameters.AddWithValue("key", CampaignOwnerLockPrefix + campaignId);
                command.ExecuteScalar();
            }
        }

        private static void ResetCampaignOwnerConnection()
        {
            try
            {
                CampaignOwnerConnection?.Dispose();
            }
            catch { }
            CampaignOwnerConnection = null;
            OwnedInternalCampaigns.Clear();
            CampaignOwnershipCapLogged = false;
        }
    }
}
