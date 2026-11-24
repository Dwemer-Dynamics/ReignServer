using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private const int PublicStandingInvalidationBatchSize = 500;
        private const int PostgreSqlWorldRelationshipSchemaRevision = 1;

        // A connection-bound scope, never a process-wide snapshot: restores and
        // later days must observe their own standing/roster state. ExecuteSql
        // invalidates it when this transaction changes either input table.
        [ThreadStatic]
        private static RelationshipStandingContext CurrentRelationshipStandingContext;

        internal sealed class RelationshipStandingContext : IDisposable
        {
            private readonly ReignDbConnection connection;
            private readonly string campaignId;
            private readonly string timelineId;
            private readonly RelationshipStandingContext previous;
            private readonly Dictionary<string, Dictionary<string, object>> standing;
            private readonly Dictionary<string, Dictionary<string, object>> roster;
            private readonly Dictionary<Dictionary<string, object>, List<Dictionary<string, object>>> sources =
                new Dictionary<Dictionary<string, object>, List<Dictionary<string, object>>>();
            private HashSet<string> playerIds;
            private bool standingStale;
            private bool rosterStale;
            // Present only on a file-input scope. Such a scope answers file-derived
            // relationship inputs strictly from its snapshot; its database reads
            // pass through unchanged (For skips it), so standing/roster stay transactional.
            private readonly RelationshipFileSnapshot files;
            private readonly bool deferPublication;
            public int StandingReads;
            public int RosterReads;
            public int PlayerReads;

            public RelationshipStandingContext(ReignDbConnection connection,
                string campaignId, string timelineId, RelationshipFileSnapshot files = null,
                StringComparer idComparer = null, bool deferPublication = false)
            {
                this.connection = connection;
                this.campaignId = campaignId;
                this.timelineId = timelineId;
                this.files = files;
                this.deferPublication = deferPublication;
                standing = new Dictionary<string, Dictionary<string, object>>(idComparer ?? StringComparer.OrdinalIgnoreCase);
                roster = new Dictionary<string, Dictionary<string, object>>(standing.Comparer);
                previous = CurrentRelationshipStandingContext;
                CurrentRelationshipStandingContext = this;
            }

            public static RelationshipStandingContext For(ReignDbConnection connection,
                string campaignId = null, string timelineId = null)
            {
                RelationshipStandingContext context = CurrentRelationshipStandingContext;
                while (context != null && context.files != null) context = context.previous;
                return context != null && ReferenceEquals(context.connection, connection)
                    && (campaignId == null || context.campaignId == campaignId)
                    && (timelineId == null || context.timelineId == timelineId) ? context : null;
            }

            // The nearest file-input scope on this connection, through any nested
            // ordinary scope. Null outside one: ordinary readers keep their own path.
            // Inside one, a different campaign or timeline is refused, never read.
            public static RelationshipFileSnapshot FilesFor(ReignDbConnection connection,
                string campaignId, string timelineId = null)
            {
                for (RelationshipStandingContext context = CurrentRelationshipStandingContext;
                    context != null; context = context.previous)
                {
                    if (context.files == null || !ReferenceEquals(context.connection, connection)) continue;
                    if (context.campaignId != campaignId || context.files.CampaignId != campaignId
                        || (timelineId != null && (context.timelineId != timelineId || context.files.TimelineId != timelineId)))
                        throw new InvalidOperationException("A relationship file snapshot was requested outside its campaign or timeline scope.");
                    return context.files;
                }
                return null;
            }

            internal static bool DefersPublicationFor(ReignDbConnection connection)
            {
                for (RelationshipStandingContext context = CurrentRelationshipStandingContext;
                    context != null; context = context.previous)
                    if (context.deferPublication && ReferenceEquals(context.connection, connection)) return true;
                return false;
            }

            public void Prefetch(IEnumerable<string> heroIds)
            {
                List<string> ids = heroIds.Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(standing.Comparer).ToList();
                if (ids.Count == 0) return;
                PrefetchStanding(standingStale ? standing.Keys.Concat(ids) : ids);
                PrefetchRoster(rosterStale ? roster.Keys.Concat(ids) : ids);
            }

            private void PrefetchStanding(IEnumerable<string> heroIds)
            {
                List<string> ids = heroIds.Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(standing.Comparer).ToList();
                if (standingStale) standing.Clear();
                foreach (string id in ids) standing[id] = null;
                StandingReads++;
                foreach (Dictionary<string, object> row in QueryRelationshipHeroRows(connection,
                    RelationshipsStore.SelectCharacterPublicStandingSql,
                    "subject_id", ids, new Dictionary<string, object>
                    { ["campaign"] = campaignId, ["timeline"] = timelineId }, standing.Comparer))
                    standing[ReadString(row, "subject_id", "")] = row;
                standingStale = false;
            }

            private void PrefetchRoster(IEnumerable<string> heroIds)
            {
                List<string> ids = heroIds.Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(roster.Comparer).ToList();
                if (rosterStale) roster.Clear();
                foreach (string id in ids) roster[id] = null;
                RosterReads++;
                foreach (Dictionary<string, object> row in QueryRelationshipHeroRows(connection,
                    RelationshipsStore.SelectIdentityRosterSql,
                    "hero_id", ids, idComparer: roster.Comparer))
                    roster[ReadString(row, "hero_id", "")] = row;
                rosterStale = false;
            }

            public Dictionary<string, object> ReadStanding(string heroId)
            {
                heroId = heroId ?? string.Empty;
                if (standingStale)
                    PrefetchStanding(standing.Keys.Concat(new[] { heroId }));
                if (!standing.TryGetValue(heroId, out Dictionary<string, object> row))
                {
                    StandingReads++;
                    row = RelationshipsStore.SelectCharacterPublicStanding4(connection, new Dictionary<string, object> { ["campaign"] = campaignId,
                            ["timeline"] = timelineId, ["subject"] = heroId }).FirstOrDefault();
                    standing[heroId] = row;
                }
                return row;
            }

            public List<Dictionary<string, object>> ReadSources(Dictionary<string, object> row)
            {
                if (row == null) return new List<Dictionary<string, object>>();
                if (!sources.TryGetValue(row, out List<Dictionary<string, object>> parsed))
                    sources[row] = parsed = ReputationService.ParsePublicStandingSources(ReadString(row, "sources_json", "[]"));
                return parsed;
            }

            public Dictionary<string, object> ReadRoster(string heroId)
            {
                heroId = heroId ?? string.Empty;
                if (rosterStale)
                    PrefetchRoster(roster.Keys.Concat(new[] { heroId }));
                if (!roster.TryGetValue(heroId, out Dictionary<string, object> row))
                {
                    RosterReads++;
                    roster[heroId] = row = RelationshipsStore.SelectIdentityRoster4(connection, new Dictionary<string, object> { ["id"] = heroId }).FirstOrDefault();
                }
                return row;
            }

            public HashSet<string> PlayerIds()
            {
                if (playerIds == null)
                {
                    PlayerReads++;
                    playerIds = new HashSet<string>(RelationshipsStore.SelectIdentityRoster(connection)
                        .Select(row => ReadString(row, "hero_id", "")), StringComparer.OrdinalIgnoreCase);
                }
                return playerIds;
            }

            public void Invalidate(string sql)
            {
                if (previous != null && ReferenceEquals(previous.connection, connection)) previous.Invalidate(sql);
                // Retain the participating IDs so the first read after a mutation
                // reloads this context in one batch. Clearing the dictionaries
                // silently restored thousands of point reads for the rest of a day.
                if (sql.IndexOf("character_public_standing", StringComparison.OrdinalIgnoreCase) >= 0)
                { standingStale = true; sources.Clear(); }
                if (sql.IndexOf("identity_roster", StringComparison.OrdinalIgnoreCase) >= 0)
                { rosterStale = true; playerIds = null; }
            }

            public void Dispose()
            {
                CurrentRelationshipStandingContext = previous;
            }
        }

        // File-derived relationship inputs for one exact hero set: each hero's
        // traits.json and profile.json, read together under one FileLock hold, plus
        // the shipped profile catalog reference. Captured before any database or
        // relationship lock, so a caller holding row locks never waits on FileLock.
        // A missing entry is refused, never read from disk. Each document is
        // parsed once at capture and the same instance is returned on every
        // read, like the shared catalog: readers must not mutate either.
        internal sealed class RelationshipFileSnapshot
        {
            private static readonly string[] FileNames = { "traits.json", "profile.json" };
            private readonly Dictionary<string, Dictionary<string, Dictionary<string, object>>> documents =
                new Dictionary<string, Dictionary<string, Dictionary<string, object>>>(StringComparer.Ordinal);
            private readonly Dictionary<string, Dictionary<string, object>> catalog;
            public readonly string CampaignId;
            public readonly string TimelineId;

            private RelationshipFileSnapshot(string campaignId, string timelineId,
                Dictionary<string, Dictionary<string, object>> catalog)
            {
                CampaignId = campaignId;
                TimelineId = timelineId;
                this.catalog = catalog;
            }

            public Dictionary<string, Dictionary<string, object>> Catalog => catalog
                ?? throw new InvalidOperationException("The relationship file snapshot has no captured profile catalog.");

            public static RelationshipFileSnapshot Capture(string campaignId, string timelineId,
                IEnumerable<string> heroIds)
            {
                // Lock order: no FileLock, profile-library or relationship lock may be
                // held here. The library lock (which may log under FileLock) is taken
                // and released first; FileLock is then held alone.
                if (System.Threading.Monitor.IsEntered(Program.FileLock)
                    || System.Threading.Monitor.IsEntered(CharactersService.CharacterProfileLibraryLock)
                    || System.Threading.Monitor.IsEntered(CampaignRelationshipWriteLock(campaignId)))
                    throw new InvalidOperationException("A relationship file snapshot must be captured before any file, catalog or relationship lock.");
                var snapshot = new RelationshipFileSnapshot(campaignId, timelineId, CharactersService.LoadCharacterProfileLibrary());
                List<string> ids = (heroIds ?? Enumerable.Empty<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
                lock (Program.FileLock)
                    foreach (string heroId in ids)
                        snapshot.documents[heroId] = FileNames.ToDictionary(name => name,
                            name => CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, name)), StringComparer.Ordinal);
                return snapshot;
            }

            // An empty snapshot: every file-derived read in its scope, including the
            // catalog, is refused. It takes no lock, so it may be built under row locks.
            public static RelationshipFileSnapshot Refusing(string campaignId, string timelineId) =>
                new RelationshipFileSnapshot(campaignId, timelineId, null);

            public Dictionary<string, object> Document(string heroId, string fileName)
            {
                if (heroId != null && documents.TryGetValue(heroId, out var files)
                    && files.TryGetValue(fileName, out Dictionary<string, object> document))
                    return document;
                throw new InvalidOperationException("The relationship file snapshot has no captured " + fileName + " for this hero.");
            }
        }

        private static List<Dictionary<string, object>> QueryRelationshipHeroRows(
            ReignDbConnection connection, string query, string idColumn,
            IEnumerable<string> heroIds, Dictionary<string, object> parameters = null,
            IEqualityComparer<string> idComparer = null)
        {
            var ids = new HashSet<string>(heroIds.Where(id => !string.IsNullOrWhiteSpace(id)),
                idComparer ?? StringComparer.OrdinalIgnoreCase);
            if (ids.Count == 0) return new List<Dictionary<string, object>>();
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection))
                return RelationshipsStore.QuerySql(connection, query, parameters)
                    .Where(row => ids.Contains(ReadString(row, idColumn, ""))).ToList();
            Dictionary<string, object> requested = parameters == null
                ? new Dictionary<string, object>() : new Dictionary<string, object>(parameters);
            requested["requestedHeroes"] = Json.Serialize(ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList());
            return RelationshipsStore.SelectStatement2(connection, query, idColumn,requested);
        }

        internal static void EnsureWorldRelationshipSchema(ReignDbConnection connection)
        {
            const string marker =
                "postgresql_world_relationship_schema_revision";
            if (ReignDatabase.IsPostgreSqlComponentSchemaReady(connection, marker,
                PostgreSqlWorldRelationshipSchemaRevision))
                return;

            EnsureWorldRelationshipSchemaCore(connection);
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                RelationshipsStore.UpsertSchemaMeta17(connection, new Dictionary<string, object>
                    {
                        ["revision"] =
                            PostgreSqlWorldRelationshipSchemaRevision.ToString()
                    });
                ReignDatabase.MarkPostgreSqlComponentSchemaReady(connection, marker,
                    PostgreSqlWorldRelationshipSchemaRevision);
            }
        }

        private static void EnsureWorldRelationshipSchemaCore(
            ReignDbConnection connection)
        {
            ReputationService.EnsurePublicStandingSchema(connection);
            RelationshipsSchema.CreateRelationshipPairProvenance(connection);
            RelationshipsSchema.CreateIndexRelationshipPairProvenanceActive(connection);
            RelationshipsSchema.CreateIndexRelationshipPairProvenancePair(connection);
            RelationshipsSchema.CreatePublicStandingInvalidations(connection);
            RelationshipsSchema.CreateIndexPublicStandingInvalidationsStatus(connection);
            RelationshipsSchema.EnsureColumnCharacterPublicStandingCalculationStatus(connection);
            RelationshipsSchema.EnsureColumnCharacterPublicStandingLastError(connection);
        }

        internal static void RecordRelationshipPairProvenance(ReignDbConnection connection,
            string campaignId, string timelineId, string pairKey, string source,
            bool required, double worldDay, Dictionary<string, object> details = null)
        {
            if (string.IsNullOrWhiteSpace(pairKey) || string.IsNullOrWhiteSpace(source))
                return;
            EnsureWorldRelationshipSchema(connection);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            RelationshipsStore.UpsertRelationshipPairProvenance(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["pair"] = pairKey, ["source"] = source,
                    ["required"] = required ? 1 : 0, ["day"] = worldDay,
                    ["details"] = Json.Serialize(details ?? new Dictionary<string, object>()),
                    ["ts"] = ts
                });
        }

        internal static Dictionary<string, object> ReadPublicStandingRow(
            ReignDbConnection connection, string campaignId, string timelineId,
            string subjectId)
        {
            RelationshipStandingContext context = RelationshipStandingContext.For(connection, campaignId, timelineId);
            if (context != null) return context.ReadStanding(subjectId);
            ReputationService.EnsurePublicStandingSchema(connection);
            return RelationshipsStore.SelectCharacterPublicStanding5(connection, new Dictionary<string, object>
            {
                ["campaign"] = campaignId, ["timeline"] = timelineId,
                ["subject"] = subjectId ?? string.Empty
            }).FirstOrDefault();
        }

        internal static int PublicStandingValue(ReignDbConnection connection,
            string campaignId, string timelineId, string subjectId)
        {
            return ReadInt(ReadPublicStandingRow(connection, campaignId, timelineId,
                subjectId), "standing_value", 0);
        }

        internal static Dictionary<string, object> ResolveObserverPublicStanding(
            ReignDbConnection connection, string campaignId, string timelineId,
            string observerId, string subjectId, Dictionary<string, object> standing = null)
        {
            standing = standing ?? ReadPublicStandingRow(connection, campaignId, timelineId, subjectId);
            int stored = ReadInt(standing, "standing_value", 0);
            RelationshipStandingContext context = RelationshipStandingContext.For(connection, campaignId, timelineId);
            List<Dictionary<string, object>> sources = context != null
                ? context.ReadSources(standing)
                : ReputationService.ParsePublicStandingSources(ReadString(standing, "sources_json", "[]"));
            List<Dictionary<string, object>> favoring = sources.Where(ReputationService.IsRulerFavoringStandingSource).ToList();
            // Inside a file-input scope (terminal acceptance) the traits/profile
            // documents come only from its snapshot; FileLock is never entered there.
            RelationshipFileSnapshot files = RelationshipStandingContext.FilesFor(connection, campaignId, timelineId);
            double spouseAdjustment = ReputationService.WhoremongerSpouseStandingAdjustment(campaignId,
                observerId, subjectId, sources, files);
            if (favoring.Count == 0)
            {
                int adjusted = spouseAdjustment == 0d ? stored : RoundAwayFromZero(
                    sources.Sum(source => ReadDouble(source, "effectiveContribution", 0d)) + spouseAdjustment);
                return new Dictionary<string, object> { ["value"] = adjusted, ["nonFavoring"] = adjusted,
                    ["favoring"] = 0, ["scope"] = "none", ["jealousyFactor"] = 1d,
                    ["whoremongerSpouseAdjustment"] = spouseAdjustment };
            }
            double nonFavoring = sources.Where(source => !ReputationService.IsRulerFavoringStandingSource(source))
                .Sum(source => ReadDouble(source, "effectiveContribution", 0d)) + spouseAdjustment;
            Dictionary<string, object> subject = context != null ? context.ReadRoster(subjectId) : RelationshipsStore.SelectIdentityRoster5(connection, new Dictionary<string, object> { ["id"] = subjectId }).FirstOrDefault();
            Dictionary<string, object> observer = context != null ? context.ReadRoster(observerId) : RelationshipsStore.SelectIdentityRoster6(connection, new Dictionary<string, object> { ["id"] = observerId }).FirstOrDefault();
            string subjectKingdom = ReadString(subject, "kingdom_id", "");
            string observerKingdom = ReadString(observer, "kingdom_id", "");
            bool sameKingdom = !string.IsNullOrWhiteSpace(subjectKingdom)
                && subjectKingdom.Equals(observerKingdom, StringComparison.OrdinalIgnoreCase);
            bool established = favoring.Any(source => ReadString(source, "sourceType", "")
                .Equals("reputation", StringComparison.OrdinalIgnoreCase));
            bool favorite = sameKingdom && favoring.Any(source => ReadString(source, "linkedHeroId", "")
                .Equals(observerId, StringComparison.OrdinalIgnoreCase));
            double jealousy = 50d;
            if (observer != null && ReadInt(observer, "is_player", 0) == 0)
                jealousy = RelationshipTraitPercent(files != null ? files.Document(observerId, "traits.json")
                    : CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, observerId, "traits.json")), "jealousy");
            double jealousyFactor = 0.5d + DialogueService.ClampDouble(jealousy, 0d, 100d) / 100d;
            double favorValue = 0d;
            string scope = "foreign";
            if (favorite)
            {
                favorValue = established ? 15d : 10d;
                scope = "favorite";
            }
            else if (sameKingdom)
            {
                int raw = established ? -10 : -5;
                favorValue = raw * (1d - ReputationService.SocialCharmMitigation(ReadInt(subject, "current_charm", 0)))
                    * jealousyFactor;
                scope = "kingdom_non_favorite";
            }
            int favorRounded = RoundAwayFromZero(favorValue);
            return new Dictionary<string, object>
            {
                ["value"] = RoundAwayFromZero(nonFavoring + favorRounded),
                ["nonFavoring"] = RoundAwayFromZero(nonFavoring), ["favoring"] = favorRounded,
                ["rawFavoring"] = established ? -10 : -5, ["stage"] = established ? "reputation" : "rumor",
                ["scope"] = scope, ["favorite"] = favorite, ["sameKingdom"] = sameKingdom,
                ["jealousy"] = ReadInt(new Dictionary<string, object> { ["value"] = jealousy }, "value", 50),
                ["jealousyFactor"] = jealousyFactor,
                ["charmMitigation"] = ReputationService.SocialCharmMitigation(ReadInt(subject, "current_charm", 0))
            };
        }

        internal static Dictionary<string, object> ResolveEffectiveAttitude(
            ReignDbConnection connection, string campaignId, string timelineId,
            string observerId, string targetId, string context = "")
        {
            EnsureWorldRelationshipSchema(connection);
            string pairKey = AmbientPairKey(observerId, targetId);
            Dictionary<string, object> pair = RelationshipsStore.SelectRelationshipPairChemistry8(connection, new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
            List<string> provenance = pair != null
                ? RelationshipsStore.SelectRelationshipPairProvenance3(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["pair"] = pairKey
                }).Select(row => ReadString(row, "source", ""))
                  .Where(value => !string.IsNullOrWhiteSpace(value)).ToList()
                : new List<string>();
            return DescribeEffectiveAttitude(connection, campaignId, timelineId, observerId, targetId,
                context, pairKey, pair, provenance);
        }

        private static Dictionary<string, object> DescribeEffectiveAttitude(
            ReignDbConnection connection, string campaignId, string timelineId, string observerId, string targetId,
            string context, string pairKey, Dictionary<string, object> pair, List<string> provenance)
        {
            Dictionary<string, object> standing = ReadPublicStandingRow(connection,
                campaignId, timelineId, targetId);
            bool hasPair = pair != null;
            bool observerIsA = hasPair && ReadString(pair, "hero_a_id", "")
                .Equals(observerId, StringComparison.OrdinalIgnoreCase);
            int personal = !hasPair ? 0 : ReadInt(pair,
                observerIsA ? "affinity_a_to_b" : "affinity_b_to_a", 0);
            int reversePersonal = !hasPair ? 0 : ReadInt(pair,
                observerIsA ? "affinity_b_to_a" : "affinity_a_to_b", 0);
            Dictionary<string, object> standingResolution = hasPair
                ? ResolveObserverPublicStanding(connection, campaignId, timelineId, observerId, targetId, standing)
                : new Dictionary<string, object> { ["value"] = 0 };
            int standingValue = ReadInt(standingResolution, "value", 0);
            int effective = hasPair ? Clamp(personal + standingValue, -100, 100) : 0;
            return new Dictionary<string, object>
            {
                ["observerId"] = observerId, ["targetId"] = targetId,
                ["pairKey"] = pairKey, ["hasPair"] = hasPair,
                ["personalAffinity"] = personal,
                ["targetPublicStanding"] = standingValue,
                ["publicStandingBreakdown"] = standingResolution,
                ["effectiveAttitude"] = effective,
                ["publicStandingRevision"] = ReadInt(standing, "revision", 1),
                ["publicStandingDay"] = ReadDouble(standing, "last_changed_day", 0d),
                ["personalBand"] = RelationshipBand(personal),
                ["effectiveBand"] = RelationshipBand(effective),
                ["personalTag"] = hasPair
                    ? DirectionalRelationshipTag(campaignId, pairKey,
                        observerIsA ? "a_to_b" : "b_to_a", personal, reversePersonal)
                    : "",
                ["provenance"] = provenance,
                ["context"] = context ?? string.Empty
            };
        }

        internal static Dictionary<string, object> ResolveRulerDiplomaticAttitude(
            ReignDbConnection connection, string campaignId, string timelineId,
            string observerId, string targetId, string context = "")
        {
            Dictionary<string, object> attitude = ResolveEffectiveAttitude(connection,
                campaignId, timelineId, observerId, targetId, context);
            int personal = ReadInt(attitude, "personalAffinity", 0);
            attitude["effectiveAttitude"] = personal;
            attitude["effectiveBand"] = RelationshipBand(personal);
            attitude["publicStandingApplied"] = false;
            attitude["attitudeBasis"] = "personal_affinity_only";
            return attitude;
        }

        internal static void QueuePublicStandingInvalidation(ReignDbConnection connection,
            string campaignId, string timelineId, string subjectId, int revision,
            double worldDay)
        {
            EnsureWorldRelationshipSchema(connection);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int affected = ReadInt(RelationshipsStore.CountRelationshipPairChemistry6(connection, new Dictionary<string, object> { ["subject"] = subjectId })
                .FirstOrDefault(), "count", 0);
            RelationshipsStore.UpsertPublicStandingInvalidations(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["subject"] = subjectId, ["revision"] = revision,
                    ["affected"] = affected, ["day"] = worldDay, ["ts"] = ts
                });
            SignalContinuousRelationshipWorker();
        }

        private static bool TryProcessNextPublicStandingInvalidation()
        {
            Program.CampaignDataGate.EnterReadLock();
            try
            {
                return TryProcessNextPublicStandingInvalidationUnderCampaignGate();
            }
            finally
            {
                Program.CampaignDataGate.ExitReadLock();
            }
        }

        private static bool TryProcessNextPublicStandingInvalidationUnderCampaignGate()
        {
            string root = CampaignFileService.CampaignsRoot();
            if (!System.IO.Directory.Exists(root)) return false;
            foreach (string directory in System.IO.Directory.GetDirectories(root).Where(CampaignFileService.CanScanCampaignDirectory))
            {
                string campaignId = System.IO.Path.GetFileName(directory);
                try
                {
                    using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        EnsureMbtiRelationshipSchema(connection);
                        EnsureWorldRelationshipSchema(connection);
                        Dictionary<string, object> job = RelationshipsStore.SelectPublicStandingInvalidations(connection).FirstOrDefault();
                        if (job == null) continue;
                        lock (CampaignRelationshipWriteLock(campaignId))
                            ProcessPublicStandingInvalidationBatch(connection, job);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    OperationsService.LogOperational("relationships.public_standing_invalidation_failed",
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["error"] = DialogueService.LimitText(ex.Message, 1000)
                        });
                    if (DatabaseAvailability.Shared.BackingOff) break;
                }
            }
            return false;
        }

        private static void ProcessPublicStandingInvalidationBatch(
            ReignDbConnection connection, Dictionary<string, object> job)
        {
            string campaignId = ReadString(job, "campaign_id", "default");
            string timelineId = ReadString(job, "timeline_id", "main");
            string subjectId = ReadString(job, "subject_id", "");
            string cursor = ReadString(job, "pair_cursor", "");
            int revision = ReadInt(job, "standing_revision", 1);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<Dictionary<string, object>> pairs = RelationshipsStore.SelectRelationshipPairChemistry25(connection, PublicStandingInvalidationBatchSize,                new Dictionary<string, object>
                {
                    ["subject"] = subjectId, ["cursor"] = cursor
                });
            RefreshEffectivePairProjectionsBulk(connection, campaignId,
                timelineId, pairs, ReadDouble(job, "created_day", 0d),
                "public_standing_revision");
            int processed = ReadInt(job, "processed_pairs", 0) + pairs.Count;
            bool complete = pairs.Count < PublicStandingInvalidationBatchSize;
            RelationshipsStore.UpdatePublicStandingInvalidations(connection, new Dictionary<string, object>
                {
                    ["status"] = complete ? "completed" : "pending",
                    ["cursor"] = pairs.Count == 0 ? cursor
                        : ReadString(pairs[pairs.Count - 1], "pair_key", cursor),
                    ["processed"] = processed, ["ts"] = ts,
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["subject"] = subjectId, ["revision"] = revision
                });
        }

        private static int RefreshEffectivePairProjectionsBulk(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            List<Dictionary<string, object>> pairs,
            double worldDay,
            string reason)
        {
            if (pairs == null || pairs.Count == 0)
                return 0;
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                int changed = 0;
                foreach (Dictionary<string, object> pair in pairs)
                {
                    if (RefreshEffectivePairProjection(connection,
                        campaignId, timelineId, pair, worldDay, reason))
                        changed++;
                }
                return changed;
            }

            List<string> heroIds = pairs.SelectMany(pair => new[]
                {
                    ReadString(pair, "hero_a_id", ""),
                    ReadString(pair, "hero_b_id", "")
                }).Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            using (RelationshipStandingContext standingContext = new RelationshipStandingContext(connection, campaignId, timelineId))
            {
                standingContext.Prefetch(heroIds);
                List<string> pairKeys = pairs.Select(pair => ReadString(pair,
                        "pair_key", ""))
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Dictionary<string, object> targetParameters =
                    new Dictionary<string, object>();
                List<string> targetPlaceholders = new List<string>();
                for (int index = 0; index < pairKeys.Count; index++)
                {
                    string key = "pair" + index.ToString(
                        CultureInfo.InvariantCulture);
                    targetParameters[key] = pairKeys[index];
                    targetPlaceholders.Add("$" + key);
                }
                Dictionary<string, Dictionary<string, object>> targets =
                    pairKeys.Count == 0
                    ? new Dictionary<string, Dictionary<string, object>>(
                        StringComparer.OrdinalIgnoreCase)
                    : RelationshipsStore.SelectRelationshipNativeTargets12(connection, string.Join(",", targetPlaceholders),                        targetParameters)
                    .ToDictionary(row => ReadString(row, "pair_key", ""),
                        row => row, StringComparer.OrdinalIgnoreCase);
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                List<Dictionary<string, object>> pairUpdates =
                    new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> targetUpserts =
                    new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> targetDeletes =
                    new List<Dictionary<string, object>>();

                foreach (Dictionary<string, object> pair in pairs)
                {
                    string pairKey = ReadString(pair, "pair_key", "");
                    string heroA = ReadString(pair, "hero_a_id", "");
                    string heroB = ReadString(pair, "hero_b_id", "");
                    int standingA = ReadInt(ResolveObserverPublicStanding(connection,
                        campaignId, timelineId, heroB, heroA), "value", 0);
                    int standingB = ReadInt(ResolveObserverPublicStanding(connection,
                        campaignId, timelineId, heroA, heroB), "value", 0);
                    int effectiveAB = Clamp(
                        ReadInt(pair, "affinity_a_to_b", 0) + standingB,
                        -100, 100);
                    int effectiveBA = Clamp(
                        ReadInt(pair, "affinity_b_to_a", 0) + standingA,
                        -100, 100);
                    int projected = ProjectNativeRelation(connection, heroA, heroB,
                        ReadInt(pair, "affinity_a_to_b", 0),
                        ReadInt(pair, "affinity_b_to_a", 0), effectiveAB, effectiveBA);
                    targets.TryGetValue(pairKey,
                        out Dictionary<string, object> target);
                    int observed = target != null
                        ? ReadInt(target, "observed_relation", 0)
                        : ReadInt(pair, "native_action_pending", 0) == 0
                            ? ReadInt(pair, "projected_native_relation", 0)
                            : 0;
                    bool observationRequired = RelationshipNativeObservationRequired(pair, target);
                    bool pending = projected != observed || observationRequired;
                    string action = pending ? "native_target:" + pairKey : "";
                    bool pairChanged =
                        ReadInt(pair, "projected_native_relation",
                            int.MinValue) != projected
                        || ReadInt(pair, "native_action_pending", 0)
                            != (pending ? 1 : 0)
                        || !ReadString(pair, "native_action_id", "")
                            .Equals(action, StringComparison.Ordinal);
                    if (pairChanged)
                    {
                        pairUpdates.Add(new Dictionary<string, object>
                        {
                            ["pair"] = pairKey,
                            ["projected"] = projected,
                            ["pending"] = pending ? 1 : 0,
                            ["action"] = action,
                            ["ts"] = ts
                        });
                    }

                    if (pending)
                    {
                        bool targetChanged = target == null
                            || ReadInt(target, "target_relation", int.MinValue)
                                != projected
                            || ReadInt(target, "observed_relation", int.MinValue)
                                != observed
                            || !new[] { "pending", "claimed" }.Contains(
                                ReadString(target, "status", ""),
                                StringComparer.OrdinalIgnoreCase);
                        if (targetChanged)
                        {
                            targetUpserts.Add(new Dictionary<string, object>
                            {
                                ["pair"] = pairKey,
                                ["actor"] = heroA,
                                ["targethero"] = heroB,
                                ["targetrelation"] = projected,
                                ["observed"] = observed,
                                ["requiresObservation"] = observationRequired ? 1 : 0,
                                ["day"] = worldDay,
                                ["ts"] = ts
                            });
                        }
                    }
                    else if (target != null)
                    {
                        targetDeletes.Add(new Dictionary<string, object>
                        {
                            ["pair"] = pairKey
                        });
                    }
                }

                if (pairUpdates.Count > 0)
                {
                    ExecutePostgreSqlJsonCommand(connection, RelationshipsStore.SelectSql5,
                        PostgreSqlRelationshipRowsJson(pairUpdates));
                    InvalidateRelationshipPairStateCache(campaignId);
                }
                ExecutePostgreSqlRelationshipWriteSet(connection, null,
                    targetUpserts, targetDeletes);
                return targetUpserts.Count + targetDeletes.Count;
            }
        }

        internal static bool RefreshEffectivePairProjection(ReignDbConnection connection,
            string campaignId, string timelineId, Dictionary<string, object> pair,
            double worldDay, string reason)
        {
            string pairKey = ReadString(pair, "pair_key", "");
            string heroA = ReadString(pair, "hero_a_id", "");
            string heroB = ReadString(pair, "hero_b_id", "");
            int affinityAB = ReadInt(pair, "affinity_a_to_b", 0);
            int affinityBA = ReadInt(pair, "affinity_b_to_a", 0);
            int effectiveAB = Clamp(affinityAB + ReadInt(ResolveObserverPublicStanding(connection,
                campaignId, timelineId, heroA, heroB), "value", 0), -100, 100);
            int effectiveBA = Clamp(affinityBA + ReadInt(ResolveObserverPublicStanding(connection,
                campaignId, timelineId, heroB, heroA), "value", 0), -100, 100);
            int projected = ProjectNativeRelation(connection, heroA, heroB,
                affinityAB, affinityBA, effectiveAB, effectiveBA);
            Dictionary<string, object> target = RelationshipsStore.SelectRelationshipNativeTargets8(connection, new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
            int observed = target != null
                ? ReadInt(target, "observed_relation", 0)
                : ReadInt(pair, "native_action_pending", 0) == 0
                    ? ReadInt(pair, "projected_native_relation", 0) : 0;
            bool observationRequired = RelationshipNativeObservationRequired(pair, target);
            bool pending = projected != observed || observationRequired;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool pairProjectionChanged =
                ReadInt(pair, "projected_native_relation", int.MinValue) != projected
                || ReadInt(pair, "native_action_pending", 0) != (pending ? 1 : 0)
                || !ReadString(pair, "native_action_id", "").Equals(
                    pending ? "native_target:" + pairKey : "",
                    StringComparison.Ordinal);
            if (pairProjectionChanged)
            {
                RelationshipsStore.UpdateRelationshipPairChemistry30(connection, new Dictionary<string, object>
                    {
                        ["projected"] = projected, ["pending"] = pending ? 1 : 0,
                        ["action"] = pending ? "native_target:" + pairKey : "",
                        ["ts"] = ts, ["pair"] = pairKey
                    });
            }
            bool nativeTargetChanged = false;
            if (pending)
            {
                bool targetChanged = target == null
                    || ReadInt(target, "target_relation", int.MinValue) != projected
                    || ReadInt(target, "observed_relation", int.MinValue) != observed
                    || !new[] { "pending", "claimed" }.Contains(
                        ReadString(target, "status", ""),
                        StringComparer.OrdinalIgnoreCase);
                if (targetChanged)
                {
                    RelationshipsStore.UpsertRelationshipNativeTargets3(connection, new Dictionary<string, object>
                    {
                        ["pair"] = pairKey, ["a"] = heroA, ["b"] = heroB,
                        ["target"] = projected, ["observed"] = observed,
                        ["requiresObservation"] = observationRequired ? 1 : 0,
                        ["day"] = worldDay, ["ts"] = ts
                    });
                    nativeTargetChanged = true;
                }
            }
            else if (target != null)
            {
                RelationshipsStore.DeleteRelationshipNativeTargets2(connection, new Dictionary<string, object> { ["pair"] = pairKey });
                nativeTargetChanged = true;
            }
            return nativeTargetChanged;
        }

        internal static Dictionary<string, object> ReconcilePoliticalRelationshipNetwork(
            ReignDbConnection connection, string campaignId, string timelineId,
            double worldDay, string reason, bool deferNativeProjection = false)
        {
            EnsureMbtiRelationshipSchema(connection);
            EnsureWorldRelationshipSchema(connection);
            List<Dictionary<string, object>> leaders = RelationshipsStore.SelectIdentityRoster7(connection);
            Dictionary<string, List<Dictionary<string, object>>> desired =
                BuildRequiredPoliticalPairDefinitions(leaders);

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, Dictionary<string, object>> existingPolitical =
                RelationshipsStore.SelectRelationshipPairProvenance4(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).ToDictionary(row => ReadString(row, "pair_key", "") + "|"
                        + ReadString(row, "source", ""), row => row,
                        StringComparer.OrdinalIgnoreCase);
            HashSet<string> desiredPoliticalKeys = new HashSet<string>(
                desired.SelectMany(item => item.Value.Select(provenance =>
                    item.Key + "|" + ReadString(provenance, "source",
                        "kingdom_leadership"))),
                StringComparer.OrdinalIgnoreCase);
            // Political reconciliation used to issue one existence query before
            // every ensure and another query for every missing-pair check. A
            // normal kingdom network contains thousands of pairs, so a single
            // leadership change generated thousands of PostgreSQL round trips
            // while holding the campaign relationship lock. Snapshot the compact
            // primary-key set once and keep it current as new pairs are inserted.
            HashSet<string> existingPairKeys = new HashSet<string>(
                RelationshipsStore.SelectRelationshipPairChemistry23(connection)
                    .Select(row => ReadString(row, "pair_key", ""))
                    .Where(pairKey => !string.IsNullOrWhiteSpace(pairKey)),
                StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Dictionary<string, object>> existing
                in existingPolitical)
            {
                if (desiredPoliticalKeys.Contains(existing.Key)
                    || (ReadInt(existing.Value, "active", 0) == 0
                        && ReadInt(existing.Value, "required", 0) == 0))
                    continue;
                RelationshipsStore.UpdateRelationshipPairProvenance2(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["pair"] = ReadString(existing.Value, "pair_key", ""),
                        ["source"] = ReadString(existing.Value, "source", ""),
                        ["day"] = worldDay, ["ts"] = ts
                    });
            }
            int created = 0;
            foreach (KeyValuePair<string, List<Dictionary<string, object>>> item in desired)
            {
                Dictionary<string, object> definition = item.Value[0];
                if (!existingPairKeys.Contains(item.Key)
                    && EnsurePoliticalRelationshipPair(connection, campaignId,
                        timelineId, item.Key,
                        ReadString(definition, "heroAId", ""),
                        ReadString(definition, "heroBId", ""), worldDay,
                        deferNativeProjection, true))
                {
                    created++;
                    existingPairKeys.Add(item.Key);
                }
                foreach (Dictionary<string, object> provenance in item.Value)
                {
                    string source = ReadString(provenance, "source",
                        "kingdom_leadership");
                    string lookupKey = item.Key + "|" + source;
                    string details = Json.Serialize(provenance);
                    bool unchanged = existingPolitical.TryGetValue(lookupKey,
                            out Dictionary<string, object> current)
                        && ReadInt(current, "active", 0) == 1
                        && ReadInt(current, "required", 0) == 1
                        && ReadString(current, "details_json", "{}")
                            .Equals(details, StringComparison.Ordinal);
                    if (!unchanged)
                        RecordRelationshipPairProvenance(connection, campaignId,
                            timelineId, item.Key, source, true, worldDay,
                            provenance);
                }
            }
            int missing = desired.Keys.Count(pairKey =>
                !existingPairKeys.Contains(pairKey));
            return new Dictionary<string, object>
            {
                ["requiredPairs"] = desired.Count, ["createdPairs"] = created,
                ["missingPairs"] = missing, ["reason"] = reason ?? string.Empty
            };
        }

        internal static Dictionary<string, List<Dictionary<string, object>>>
            BuildRequiredPoliticalPairDefinitions(
                IEnumerable<Dictionary<string, object>> sourceLeaders)
        {
            List<Dictionary<string, object>> leaders = (sourceLeaders
                    ?? Enumerable.Empty<Dictionary<string, object>>())
                .Where(row => !string.IsNullOrWhiteSpace(ReadFirstString(row,
                    "hero_id", "heroStringId", "heroId")))
                .Where(row => ReadPoliticalFlag(row, "is_clan_leader",
                        "isClanLeader")
                    || ReadPoliticalFlag(row, "is_ruler", "isRuler"))
                .Where(row => !ReadPoliticalFlag(row, "is_player", "isPlayer")
                    || ReadPoliticalFlag(row, "is_ruler", "isRuler"))
                .ToList();
            Dictionary<string, List<Dictionary<string, object>>> desired =
                new Dictionary<string, List<Dictionary<string, object>>>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, Dictionary<string, object>> kingdom in
                leaders.Where(row => !string.IsNullOrWhiteSpace(
                        FirstNonEmpty(ReadString(row, "kingdom_id", ""),
                            ReadString(row, "kingdomId", ""))))
                    .GroupBy(row => FirstNonEmpty(
                            ReadString(row, "kingdom_id", ""),
                            ReadString(row, "kingdomId", "")),
                        StringComparer.OrdinalIgnoreCase))
            {
                List<Dictionary<string, object>> members = kingdom
                    .GroupBy(row => ReadFirstString(row, "hero_id",
                            "heroStringId", "heroId"),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(row => ReadFirstString(row, "hero_id",
                            "heroStringId", "heroId"),
                        StringComparer.OrdinalIgnoreCase).ToList();
                AddRequiredPoliticalPairs(desired, members,
                    "kingdom_leadership", kingdom.Key);
            }
            List<Dictionary<string, object>> rulers = leaders
                .Where(row => ReadPoliticalFlag(row, "is_ruler", "isRuler"))
                .Where(row => !ReadPoliticalFlag(row, "is_player", "isPlayer"))
                .GroupBy(row => ReadFirstString(row, "hero_id",
                        "heroStringId", "heroId"),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(row => ReadFirstString(row, "hero_id",
                        "heroStringId", "heroId"),
                    StringComparer.OrdinalIgnoreCase).ToList();
            AddRequiredPoliticalPairs(desired, rulers, "ruler_network",
                "all_kingdoms");
            return desired;
        }

        internal static bool ReadPoliticalFlag(Dictionary<string, object> row,
            string databaseKey, string payloadKey)
        {
            // PostgreSQL identity rows expose integer 0/1 flags while heartbeat
            // payloads expose JSON booleans. Normalize both representations when
            // constructing the authoritative political relationship topology.
            return ReadInt(row, databaseKey, 0) != 0
                || ReadBool(row, databaseKey, false)
                || ReadBool(row, payloadKey, false)
                || ReadInt(row, payloadKey, 0) != 0;
        }

        private static void AddRequiredPoliticalPairs(
            Dictionary<string, List<Dictionary<string, object>>> desired,
            List<Dictionary<string, object>> heroes, string source, string groupId)
        {
            for (int first = 0; first < heroes.Count; first++)
            for (int second = first + 1; second < heroes.Count; second++)
            {
                string heroA = ReadFirstString(heroes[first], "hero_id",
                    "heroStringId", "heroId");
                string heroB = ReadFirstString(heroes[second], "hero_id",
                    "heroStringId", "heroId");
                string pairKey = AmbientPairKey(heroA, heroB);
                if (!desired.TryGetValue(pairKey,
                    out List<Dictionary<string, object>> provenance))
                {
                    provenance = new List<Dictionary<string, object>>();
                    desired[pairKey] = provenance;
                }
                if (provenance.Any(item => ReadString(item, "source", "")
                    .Equals(source, StringComparison.OrdinalIgnoreCase)))
                    continue;
                provenance.Add(new Dictionary<string, object>
                {
                    ["heroAId"] = pairKey.StartsWith(heroA + "|",
                        StringComparison.OrdinalIgnoreCase) ? heroA : heroB,
                    ["heroBId"] = pairKey.StartsWith(heroA + "|",
                        StringComparison.OrdinalIgnoreCase) ? heroB : heroA,
                    ["source"] = source, ["groupId"] = groupId
                });
            }
        }

        internal static bool EnsurePoliticalRelationshipPair(ReignDbConnection connection,
            string campaignId, string timelineId, string pairKey, string heroA, string heroB,
            double worldDay, bool deferNativeProjection = false,
            bool pairKnownMissing = false)
        {
            if (!pairKnownMissing && RelationshipsStore.SelectRelationshipPairChemistry26(connection, new Dictionary<string, object> { ["pair"] = pairKey }).Any())
                return false;
            int day = (int)Math.Floor(worldDay + 0.000001d);
            string typeA = ResolvePermanentMbtiTypeById(connection, campaignId,
                heroA, day);
            string typeB = ResolvePermanentMbtiTypeById(connection, campaignId,
                heroB, day);
            if (!MbtiDefinitions.ContainsKey(typeA) || !MbtiDefinitions.ContainsKey(typeB))
                throw new InvalidOperationException("Required political relationship "
                    + pairKey + " is missing an immutable MBTI assignment.");
            int baseAB = MbtiCompatibility(typeA, typeB);
            int baseBA = MbtiCompatibility(typeB, typeA);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool inserted = RelationshipsStore.InsertOrIgnoreRelationshipPairChemistry(connection, new Dictionary<string, object>
                {
                    ["pair"] = pairKey, ["a"] = heroA, ["b"] = heroB,
                    ["typeA"] = typeA, ["typeB"] = typeB,
                    ["baseAB"] = baseAB, ["baseBA"] = baseBA,
                    ["chanceAB"] = AdjustedMbtiCompatibility(baseAB),
                    ["chanceBA"] = AdjustedMbtiCompatibility(baseBA),
                    ["day"] = day,
                    ["shard"] = RelationshipCadenceShard(campaignId, pairKey),
                    ["version"] = MbtiChemistryVersion, ["ts"] = ts
                }).Count == 1;
            if (!inserted)
                return false;
            if (!deferNativeProjection)
                RefreshEffectivePairProjection(connection, campaignId, timelineId,
                    RelationshipsStore.SelectRelationshipPairChemistry18(connection, new Dictionary<string, object> { ["pair"] = pairKey }).First(),
                    worldDay, "political_pair_created");
            return true;
        }

        internal static Dictionary<string, object> EffectiveAttitudeBatchApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            var requested = new List<(string Observer, string Target, string Context, string Key)>();
            payload.TryGetValue("pairs", out object raw);
            // Retain the single-pair request shape accepted by earlier servers.
            IEnumerable<object> items = raw switch
            {
                null => Enumerable.Empty<object>(),
                Dictionary<string, object> single => new object[] { single },
                System.Collections.IEnumerable list when !(raw is string) => list.Cast<object>(),
                _ => null
            };
            if (items == null) return BatchError("pairs must contain relationship objects.");
            foreach (var item in items)
            {
                if (requested.Count == 5000) return BatchError("At most 5000 directed pairs are allowed per batch.");
                if (!(item is Dictionary<string, object> pair)) return BatchError("Invalid relationship pair.");
                string observer = ReadFirstString(pair, "observerId", "subjectId", "actorHeroId");
                string target = ReadFirstString(pair, "targetId", "targetHeroId");
                if (string.IsNullOrWhiteSpace(observer) || string.IsNullOrWhiteSpace(target))
                    return BatchError("Each pair requires observerId and targetId.");
                requested.Add((observer, target, ReadString(pair, "context", "batch"), AmbientPairKey(observer, target)));
            }

            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                EnsureWorldRelationshipSchema(connection);
                // Match the scalar SQL predicates, including exact spelling of public API inputs.
                var keys = requested.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).ToList();
                var pairRows = RelationshipsStore.SelectEffectiveAttitudePairs(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["pairs"] = Json.Serialize(keys)
                }).ToLookup(row => ReadString(row, "pair_key", ""), StringComparer.Ordinal);
                using (var standing = new RelationshipStandingContext(connection, campaignId, timelineId, idComparer: StringComparer.Ordinal))
                {
                    standing.Prefetch(requested.Select(pair => pair.Observer).Concat(requested.Select(pair => pair.Target)));
                    var attitudes = requested.Select(pair =>
                    {
                        var rows = pairRows[pair.Key];
                        return DescribeEffectiveAttitude(connection, campaignId, timelineId, pair.Observer, pair.Target,
                            pair.Context, pair.Key, rows.FirstOrDefault(), rows.Select(row => ReadString(row, "source", ""))
                                .Where(source => !string.IsNullOrWhiteSpace(source)).ToList());
                    }).ToList();
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true, ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["attitudes"] = attitudes
                    };
                }
            }

            Dictionary<string, object> BatchError(string message) => new Dictionary<string, object>
            {
                ["ok"] = false, ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["error"] = message
            };
        }
    }
}
