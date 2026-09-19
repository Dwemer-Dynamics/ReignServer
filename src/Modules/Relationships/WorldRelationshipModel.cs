using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int PublicStandingInvalidationBatchSize = 500;
        private const int PostgreSqlWorldRelationshipSchemaRevision = 1;

        // A connection-bound scope, never a process-wide snapshot: restores and
        // later days must observe their own standing/roster state. ExecuteSql
        // invalidates it when this transaction changes either input table.
        [ThreadStatic]
        private static RelationshipStandingContext CurrentRelationshipStandingContext;

        private sealed class RelationshipStandingContext : IDisposable
        {
            private readonly ReignDbConnection connection;
            private readonly string campaignId;
            private readonly string timelineId;
            private readonly RelationshipStandingContext previous;
            private readonly Dictionary<string, Dictionary<string, object>> standing =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, Dictionary<string, object>> roster =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<Dictionary<string, object>, List<Dictionary<string, object>>> sources =
                new Dictionary<Dictionary<string, object>, List<Dictionary<string, object>>>();
            private HashSet<string> playerIds;
            private bool standingStale;
            private bool rosterStale;
            public int StandingReads;
            public int RosterReads;
            public int PlayerReads;

            public RelationshipStandingContext(ReignDbConnection connection,
                string campaignId, string timelineId)
            {
                this.connection = connection;
                this.campaignId = campaignId;
                this.timelineId = timelineId;
                previous = CurrentRelationshipStandingContext;
                CurrentRelationshipStandingContext = this;
            }

            public static RelationshipStandingContext For(ReignDbConnection connection,
                string campaignId = null, string timelineId = null)
            {
                RelationshipStandingContext context = CurrentRelationshipStandingContext;
                return context != null && ReferenceEquals(context.connection, connection)
                    && (campaignId == null || context.campaignId == campaignId)
                    && (timelineId == null || context.timelineId == timelineId) ? context : null;
            }

            public void Prefetch(IEnumerable<string> heroIds)
            {
                List<string> ids = heroIds.Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (ids.Count == 0) return;
                PrefetchStanding(standingStale ? standing.Keys.Concat(ids) : ids);
                PrefetchRoster(rosterStale ? roster.Keys.Concat(ids) : ids);
            }

            private void PrefetchStanding(IEnumerable<string> heroIds)
            {
                List<string> ids = heroIds.Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (standingStale) standing.Clear();
                foreach (string id in ids) standing[id] = null;
                StandingReads++;
                foreach (Dictionary<string, object> row in QueryRelationshipHeroRows(connection,
                    "SELECT subject_id,standing_value,sources_json,revision,last_changed_day,updated_ts,calculation_status,last_error FROM character_public_standing WHERE campaign_id=$campaign AND timeline_id=$timeline",
                    "subject_id", ids, new Dictionary<string, object>
                    { ["campaign"] = campaignId, ["timeline"] = timelineId }))
                    standing[ReadString(row, "subject_id", "")] = row;
                standingStale = false;
            }

            private void PrefetchRoster(IEnumerable<string> heroIds)
            {
                List<string> ids = heroIds.Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (rosterStale) roster.Clear();
                foreach (string id in ids) roster[id] = null;
                RosterReads++;
                foreach (Dictionary<string, object> row in QueryRelationshipHeroRows(connection,
                    "SELECT hero_id,kingdom_id,current_charm,is_player FROM identity_roster",
                    "hero_id", ids))
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
                    row = QuerySql(connection, @"SELECT subject_id,standing_value,sources_json,
revision,last_changed_day,updated_ts,calculation_status,last_error FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId,
                            ["timeline"] = timelineId, ["subject"] = heroId }).FirstOrDefault();
                    standing[heroId] = row;
                }
                return row;
            }

            public List<Dictionary<string, object>> ReadSources(Dictionary<string, object> row)
            {
                if (row == null) return new List<Dictionary<string, object>>();
                if (!sources.TryGetValue(row, out List<Dictionary<string, object>> parsed))
                    sources[row] = parsed = ParsePublicStandingSources(ReadString(row, "sources_json", "[]"));
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
                    roster[heroId] = row = QuerySql(connection,
                        "SELECT hero_id,kingdom_id,current_charm,is_player FROM identity_roster WHERE hero_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = heroId }).FirstOrDefault();
                }
                return row;
            }

            public HashSet<string> PlayerIds()
            {
                if (playerIds == null)
                {
                    PlayerReads++;
                    playerIds = new HashSet<string>(QuerySql(connection,
                        "SELECT hero_id FROM identity_roster WHERE is_player=1 AND hero_id<>'';")
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

        private static List<Dictionary<string, object>> QueryRelationshipHeroRows(
            ReignDbConnection connection, string query, string idColumn,
            IEnumerable<string> heroIds, Dictionary<string, object> parameters = null)
        {
            HashSet<string> ids = new HashSet<string>(heroIds
                .Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
            if (ids.Count == 0) return new List<Dictionary<string, object>>();
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection))
                return QuerySql(connection, query, parameters)
                    .Where(row => ids.Contains(ReadString(row, idColumn, ""))).ToList();
            Dictionary<string, object> requested = parameters == null
                ? new Dictionary<string, object>() : new Dictionary<string, object>(parameters);
            requested["requestedHeroes"] = Json.Serialize(ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList());
            return QuerySql(connection, "SELECT source.* FROM (" + query + @") source
JOIN jsonb_array_elements_text(CAST($requestedHeroes AS jsonb)) requested
ON requested.value=source." + idColumn + ";", requested);
        }

        private static void EnsureWorldRelationshipSchema(ReignDbConnection connection)
        {
            const string marker =
                "postgresql_world_relationship_schema_revision";
            if (IsPostgreSqlComponentSchemaReady(connection, marker,
                PostgreSqlWorldRelationshipSchemaRevision))
                return;

            EnsureWorldRelationshipSchemaCore(connection);
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_world_relationship_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                    new Dictionary<string, object>
                    {
                        ["revision"] =
                            PostgreSqlWorldRelationshipSchemaRevision.ToString()
                    });
                MarkPostgreSqlComponentSchemaReady(connection, marker,
                    PostgreSqlWorldRelationshipSchemaRevision);
            }
        }

        private static void EnsureWorldRelationshipSchemaCore(
            ReignDbConnection connection)
        {
            EnsurePublicStandingSchema(connection);
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_pair_provenance (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,pair_key TEXT NOT NULL,
source TEXT NOT NULL,active INTEGER NOT NULL DEFAULT 1,required INTEGER NOT NULL DEFAULT 0,
first_day REAL NOT NULL,last_day REAL NOT NULL,details_json TEXT NOT NULL DEFAULT '{}',
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,pair_key,source));");
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_pair_provenance_active
ON relationship_pair_provenance(campaign_id,timeline_id,source,active,pair_key);");
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_pair_provenance_pair
ON relationship_pair_provenance(pair_key,active,required,source);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS public_standing_invalidations (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,
standing_revision INTEGER NOT NULL,status TEXT NOT NULL DEFAULT 'pending',
pair_cursor TEXT NOT NULL DEFAULT '',affected_pairs INTEGER NOT NULL DEFAULT 0,
processed_pairs INTEGER NOT NULL DEFAULT 0,created_day REAL NOT NULL,
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,last_error TEXT NOT NULL DEFAULT '',
PRIMARY KEY(campaign_id,timeline_id,subject_id));");
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_public_standing_invalidations_status
ON public_standing_invalidations(status,updated_ts,campaign_id,timeline_id,subject_id);");
            EnsureDatabaseColumn(connection, "character_public_standing", "calculation_status",
                "TEXT NOT NULL DEFAULT 'ready'");
            EnsureDatabaseColumn(connection, "character_public_standing", "last_error",
                "TEXT NOT NULL DEFAULT ''");
        }

        private static void RecordRelationshipPairProvenance(ReignDbConnection connection,
            string campaignId, string timelineId, string pairKey, string source,
            bool required, double worldDay, Dictionary<string, object> details = null)
        {
            if (string.IsNullOrWhiteSpace(pairKey) || string.IsNullOrWhiteSpace(source))
                return;
            EnsureWorldRelationshipSchema(connection);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"INSERT INTO relationship_pair_provenance(
campaign_id,timeline_id,pair_key,source,active,required,first_day,last_day,details_json,updated_ts)
VALUES($campaign,$timeline,$pair,$source,1,$required,$day,$day,$details,$ts)
ON CONFLICT(campaign_id,timeline_id,pair_key,source) DO UPDATE SET
active=1,required=$required,last_day=$day,details_json=$details,updated_ts=$ts
WHERE relationship_pair_provenance.active<>1
OR relationship_pair_provenance.required<>$required
OR relationship_pair_provenance.last_day<>$day
OR relationship_pair_provenance.details_json<>$details;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["pair"] = pairKey, ["source"] = source,
                    ["required"] = required ? 1 : 0, ["day"] = worldDay,
                    ["details"] = Json.Serialize(details ?? new Dictionary<string, object>()),
                    ["ts"] = ts
                });
        }

        private static Dictionary<string, object> ReadPublicStandingRow(
            ReignDbConnection connection, string campaignId, string timelineId,
            string subjectId)
        {
            RelationshipStandingContext context = RelationshipStandingContext.For(connection, campaignId, timelineId);
            if (context != null) return context.ReadStanding(subjectId);
            EnsurePublicStandingSchema(connection);
            return QuerySql(connection, @"SELECT subject_id,standing_value,sources_json,
revision,last_changed_day,updated_ts,calculation_status,last_error
FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
LIMIT 1;", new Dictionary<string, object>
            {
                ["campaign"] = campaignId, ["timeline"] = timelineId,
                ["subject"] = subjectId ?? string.Empty
            }).FirstOrDefault();
        }

        private static int PublicStandingValue(ReignDbConnection connection,
            string campaignId, string timelineId, string subjectId)
        {
            return ReadInt(ReadPublicStandingRow(connection, campaignId, timelineId,
                subjectId), "standing_value", 0);
        }

        private static Dictionary<string, object> ResolveObserverPublicStanding(
            ReignDbConnection connection, string campaignId, string timelineId,
            string observerId, string subjectId, Dictionary<string, object> standing = null)
        {
            standing = standing ?? ReadPublicStandingRow(connection, campaignId, timelineId, subjectId);
            int stored = ReadInt(standing, "standing_value", 0);
            RelationshipStandingContext context = RelationshipStandingContext.For(connection, campaignId, timelineId);
            List<Dictionary<string, object>> sources = context != null
                ? context.ReadSources(standing)
                : ParsePublicStandingSources(ReadString(standing, "sources_json", "[]"));
            List<Dictionary<string, object>> favoring = sources.Where(IsRulerFavoringStandingSource).ToList();
            double spouseAdjustment = WhoremongerSpouseStandingAdjustment(campaignId,
                observerId, subjectId, sources);
            if (favoring.Count == 0)
            {
                int adjusted = spouseAdjustment == 0d ? stored : RoundAwayFromZero(
                    sources.Sum(source => ReadDouble(source, "effectiveContribution", 0d)) + spouseAdjustment);
                return new Dictionary<string, object> { ["value"] = adjusted, ["nonFavoring"] = adjusted,
                    ["favoring"] = 0, ["scope"] = "none", ["jealousyFactor"] = 1d,
                    ["whoremongerSpouseAdjustment"] = spouseAdjustment };
            }
            double nonFavoring = sources.Where(source => !IsRulerFavoringStandingSource(source))
                .Sum(source => ReadDouble(source, "effectiveContribution", 0d)) + spouseAdjustment;
            Dictionary<string, object> subject = context != null ? context.ReadRoster(subjectId) : QuerySql(connection,
                "SELECT kingdom_id,current_charm FROM identity_roster WHERE hero_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = subjectId }).FirstOrDefault();
            Dictionary<string, object> observer = context != null ? context.ReadRoster(observerId) : QuerySql(connection,
                "SELECT kingdom_id,is_player FROM identity_roster WHERE hero_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = observerId }).FirstOrDefault();
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
                jealousy = RelationshipTraitPercent(ReadJsonObject(CharacterFile(campaignId,
                    observerId, "traits.json")), "jealousy");
            double jealousyFactor = 0.5d + ClampDouble(jealousy, 0d, 100d) / 100d;
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
                favorValue = raw * (1d - SocialCharmMitigation(ReadInt(subject, "current_charm", 0)))
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
                ["charmMitigation"] = SocialCharmMitigation(ReadInt(subject, "current_charm", 0))
            };
        }

        private static Dictionary<string, object> ResolveEffectiveAttitude(
            ReignDbConnection connection, string campaignId, string timelineId,
            string observerId, string targetId, string context = "")
        {
            EnsureWorldRelationshipSchema(connection);
            string pairKey = AmbientPairKey(observerId, targetId);
            Dictionary<string, object> pair = QuerySql(connection,
                "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
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
            List<string> provenance = hasPair
                ? QuerySql(connection, @"SELECT source FROM relationship_pair_provenance
WHERE campaign_id=$campaign AND timeline_id=$timeline AND pair_key=$pair AND active=1
ORDER BY source;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["pair"] = pairKey
                }).Select(row => ReadString(row, "source", ""))
                  .Where(value => !string.IsNullOrWhiteSpace(value)).ToList()
                : new List<string>();
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

        private static Dictionary<string, object> ResolveRulerDiplomaticAttitude(
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

        private static void QueuePublicStandingInvalidation(ReignDbConnection connection,
            string campaignId, string timelineId, string subjectId, int revision,
            double worldDay)
        {
            EnsureWorldRelationshipSchema(connection);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int affected = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_pair_chemistry
WHERE hero_a_id=$subject OR hero_b_id=$subject;",
                new Dictionary<string, object> { ["subject"] = subjectId })
                .FirstOrDefault(), "count", 0);
            ExecuteSql(connection, @"INSERT INTO public_standing_invalidations(
campaign_id,timeline_id,subject_id,standing_revision,status,pair_cursor,
affected_pairs,processed_pairs,created_day,created_ts,updated_ts,last_error)
VALUES($campaign,$timeline,$subject,$revision,'pending','',$affected,0,$day,$ts,$ts,'')
ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
standing_revision=$revision,status='pending',pair_cursor='',affected_pairs=$affected,
processed_pairs=0,created_day=$day,created_ts=$ts,updated_ts=$ts,last_error='';",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["subject"] = subjectId, ["revision"] = revision,
                    ["affected"] = affected, ["day"] = worldDay, ["ts"] = ts
                });
            SignalContinuousRelationshipWorker();
        }

        private static bool TryProcessNextPublicStandingInvalidation()
        {
            CampaignDataGate.EnterReadLock();
            try
            {
                return TryProcessNextPublicStandingInvalidationUnderCampaignGate();
            }
            finally
            {
                CampaignDataGate.ExitReadLock();
            }
        }

        private static bool TryProcessNextPublicStandingInvalidationUnderCampaignGate()
        {
            string root = CampaignsRoot();
            if (!System.IO.Directory.Exists(root)) return false;
            foreach (string directory in System.IO.Directory.GetDirectories(root))
            {
                string campaignId = System.IO.Path.GetFileName(directory);
                try
                {
                    using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    {
                        EnsureMbtiRelationshipSchema(connection);
                        EnsureWorldRelationshipSchema(connection);
                        Dictionary<string, object> job = QuerySql(connection, @"
SELECT * FROM public_standing_invalidations
WHERE status='pending' ORDER BY updated_ts,subject_id LIMIT 1;").FirstOrDefault();
                        if (job == null) continue;
                        lock (CampaignRelationshipWriteLock(campaignId))
                            ProcessPublicStandingInvalidationBatch(connection, job);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LogOperational("relationships.public_standing_invalidation_failed",
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["error"] = LimitText(ex.Message, 1000)
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
            List<Dictionary<string, object>> pairs = QuerySql(connection, @"
SELECT * FROM relationship_pair_chemistry
WHERE (hero_a_id=$subject OR hero_b_id=$subject) AND pair_key>$cursor
ORDER BY pair_key LIMIT " + PublicStandingInvalidationBatchSize + ";",
                new Dictionary<string, object>
                {
                    ["subject"] = subjectId, ["cursor"] = cursor
                });
            RefreshEffectivePairProjectionsBulk(connection, campaignId,
                timelineId, pairs, ReadDouble(job, "created_day", 0d),
                "public_standing_revision");
            int processed = ReadInt(job, "processed_pairs", 0) + pairs.Count;
            bool complete = pairs.Count < PublicStandingInvalidationBatchSize;
            ExecuteSql(connection, @"UPDATE public_standing_invalidations SET
status=$status,pair_cursor=$cursor,processed_pairs=$processed,updated_ts=$ts,last_error=''
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
AND standing_revision=$revision;",
                new Dictionary<string, object>
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
                    : QuerySql(connection,
                        "SELECT * FROM relationship_native_targets WHERE pair_key IN ("
                        + string.Join(",", targetPlaceholders) + ");",
                        targetParameters)
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
                    ExecutePostgreSqlJsonCommand(connection, @"
    WITH x AS (
        SELECT * FROM jsonb_to_recordset(@rows) AS r(
            pair text,projected integer,pending integer,action text,ts bigint)
    )
    UPDATE relationship_pair_chemistry AS chemistry
    SET projected_native_relation=x.projected,
        native_action_pending=x.pending,
        native_action_id=x.action,
        updated_ts=x.ts
    FROM x WHERE chemistry.pair_key=x.pair;",
                        PostgreSqlRelationshipRowsJson(pairUpdates));
                    InvalidateRelationshipPairStateCache(campaignId);
                }
                ExecutePostgreSqlRelationshipWriteSet(connection, null,
                    targetUpserts, targetDeletes);
                return targetUpserts.Count + targetDeletes.Count;
            }
        }

        private static bool RefreshEffectivePairProjection(ReignDbConnection connection,
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
            Dictionary<string, object> target = QuerySql(connection,
                "SELECT * FROM relationship_native_targets WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
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
                ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
projected_native_relation=$projected,native_action_pending=$pending,
native_action_id=$action,updated_ts=$ts WHERE pair_key=$pair;",
                    new Dictionary<string, object>
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
                    ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,
last_sync_day,attempt_count,claimed_ts,last_error,updated_ts,revision,requires_observation)
VALUES($pair,$a,$b,$target,$observed,'pending',$day,-1000,0,0,'',$ts,1,$requiresObservation)
ON CONFLICT(pair_key) DO UPDATE SET hero_a_id=$a,hero_b_id=$b,
target_relation=$target,observed_relation=$observed,status='pending',world_day=$day,
requires_observation=CASE WHEN relationship_native_targets.requires_observation=1 OR $requiresObservation=1 THEN 1 ELSE 0 END,
claimed_ts=0,last_error='',
attempt_count=CASE WHEN relationship_native_targets.target_relation=$target
THEN relationship_native_targets.attempt_count ELSE 0 END,
revision=CASE WHEN relationship_native_targets.target_relation=$target
THEN relationship_native_targets.revision
ELSE relationship_native_targets.revision+1 END,updated_ts=$ts;",
                    new Dictionary<string, object>
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
                ExecuteSql(connection,
                    "DELETE FROM relationship_native_targets WHERE pair_key=$pair;",
                    new Dictionary<string, object> { ["pair"] = pairKey });
                nativeTargetChanged = true;
            }
            return nativeTargetChanged;
        }

        private static Dictionary<string, object> ReconcilePoliticalRelationshipNetwork(
            ReignDbConnection connection, string campaignId, string timelineId,
            double worldDay, string reason, bool deferNativeProjection = false)
        {
            EnsureMbtiRelationshipSchema(connection);
            EnsureWorldRelationshipSchema(connection);
            List<Dictionary<string, object>> leaders = QuerySql(connection, @"
SELECT * FROM identity_roster
WHERE is_alive=1 AND is_adult=1 AND is_player=0
AND (is_clan_leader=1 OR is_ruler=1)
AND hero_id<>'';");
            Dictionary<string, List<Dictionary<string, object>>> desired =
                BuildRequiredPoliticalPairDefinitions(leaders);

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, Dictionary<string, object>> existingPolitical =
                QuerySql(connection, @"SELECT * FROM relationship_pair_provenance
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND source IN ('kingdom_leadership','ruler_network');",
                    new Dictionary<string, object>
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
                QuerySql(connection,
                        "SELECT pair_key FROM relationship_pair_chemistry;")
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
                ExecuteSql(connection, @"UPDATE relationship_pair_provenance SET
active=0,required=0,last_day=$day,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND pair_key=$pair AND source=$source
AND (active<>0 OR required<>0);",
                    new Dictionary<string, object>
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

        private static Dictionary<string, List<Dictionary<string, object>>>
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

        private static bool ReadPoliticalFlag(Dictionary<string, object> row,
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

        private static bool EnsurePoliticalRelationshipPair(ReignDbConnection connection,
            string campaignId, string timelineId, string pairKey, string heroA, string heroB,
            double worldDay, bool deferNativeProjection = false,
            bool pairKnownMissing = false)
        {
            if (!pairKnownMissing && QuerySql(connection,
                "SELECT 1 AS present FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey }).Any())
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
            bool inserted = QuerySql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,
base_chance_b_to_a,chance_a_to_b,chance_b_to_a,affinity_a_to_b,
affinity_b_to_a,tag_a_to_b,tag_b_to_a,first_day,last_day,
processing_shard,last_presence_day,projected_native_relation,
compatibility_version,updated_ts)
VALUES($pair,$a,$b,$typeA,$typeB,$baseAB,$baseBA,$chanceAB,$chanceBA,
0,0,'neutral','neutral',$day,$day,$shard,-1,0,$version,$ts)
ON CONFLICT(pair_key) DO NOTHING
RETURNING pair_key;",
                new Dictionary<string, object>
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
                    QuerySql(connection,
                        "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = pairKey }).First(),
                    worldDay, "political_pair_created");
            return true;
        }

        private static Dictionary<string, object> EffectiveAttitudeBatchApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            List<Dictionary<string, object>> requested = ReadDictionaryList(payload, "pairs");
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["timelineId"] = timelineId,
                    ["attitudes"] = requested.Take(5000).Select(pair =>
                        ResolveEffectiveAttitude(connection, campaignId, timelineId,
                            ReadFirstString(pair, "observerId", "subjectId", "actorHeroId"),
                            ReadFirstString(pair, "targetId", "targetHeroId"),
                            ReadString(pair, "context", "batch"))).ToList()
                };
            }
        }

        private static List<Dictionary<string, object>>
            RunWorldRelationshipModelSelfTests()
        {
            List<Dictionary<string, object>> results =
                RunRelationshipThroughputSelfTests();
            Action<string, bool, string> add = (id, passed, summary) =>
                results.Add(new Dictionary<string, object>
                {
                    ["ok"] = true, ["passed"] = passed,
                    ["suite"] = "world_relationship_model",
                    ["caseId"] = id, ["name"] = id,
                    ["summary"] = summary, ["durationMs"] = 0
                });
            string campaignId = "world_model_"
                + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                using (ReignDbConnection connection =
                    OpenCampaignConnection(campaignId))
                {
                    EnsureIdentitySchema(connection);
                    EnsureMbtiRelationshipSchema(connection);
                    EnsureWorldRelationshipSchema(connection);

                    // A full native roster replacement must preserve every field
                    // and its transaction boundary when routed through the batch writer.
                    List<Dictionary<string, object>> rosterFixture = Enumerable.Range(0, 128)
                        .Select(index => new Dictionary<string, object>
                        {
                            ["heroStringId"] = "batch_" + index,
                            ["name"] = "NPC '" + index + " — Élan",
                            ["clanId"] = "clan_" + index % 7,
                            ["kingdomId"] = "kingdom_" + index % 3,
                            ["isLord"] = index % 2 == 0, ["isRuler"] = index % 11 == 0,
                            ["fatherId"] = "father_" + index % 4,
                            ["motherId"] = "mother_" + index % 5,
                            ["spouseId"] = index % 2 == 0 ? "spouse_" + index : "",
                            ["childrenIds"] = new List<object> { "child_" + index, "child_" + index },
                            ["isAlive"] = index % 13 != 0, ["isChild"] = index % 17 == 0,
                            ["isPlayer"] = index == 0, ["isFemale"] = index % 2 == 1,
                            ["clanTier"] = index % 9 - 1, ["currentCharm"] = index - 10,
                            ["occupation"] = index % 2 == 0 ? "Lord" : "Wanderer",
                            ["isNotable"] = index % 3 == 0, ["isWanderer"] = index % 5 == 0,
                            ["isClanLeader"] = index % 7 == 0, ["isMercenaryClan"] = index % 11 == 0,
                            ["governorOfSettlementId"] = "town_" + index % 6,
                            ["governorOfSettlementName"] = "Town '" + index % 6
                        }).ToList();
                    string[] rosterResults = new string[2];
                    bool rosterRollbackClean = true;
                    for (int route = 0; route < rosterResults.Length; route++)
                    {
                        ExecuteSql(connection, "BEGIN IMMEDIATE;");
                        try
                        {
                            ExecuteSql(connection, "DELETE FROM identity_roster;");
                            if (route == 0)
                                foreach (Dictionary<string, object> hero in rosterFixture)
                                    UpsertIdentityRosterHero(connection, hero, 424242);
                            else
                                UpsertIdentityRosterHeroesBatch(connection, rosterFixture, 424242);
                            rosterResults[route] = Json.Serialize(QuerySql(connection,
                                "SELECT * FROM identity_roster ORDER BY hero_id;"));
                        }
                        finally { ExecuteSql(connection, "ROLLBACK;"); }
                        rosterRollbackClean &= ReadInt(QuerySql(connection,
                            "SELECT COUNT(*) AS count FROM identity_roster;").FirstOrDefault(), "count", -1) == 0;
                    }
                    add("roster_batch_matches_complete_serial_rows",
                        rosterResults[0] == rosterResults[1] && rosterRollbackClean,
                        "128 diverse roster entries preserve all columns, names, family, role and clamped trait values, timestamps and rollback.");

                    ExecuteSql(connection, @"INSERT INTO character_public_standing(
campaign_id,timeline_id,subject_id,standing_value,sources_json,revision,
calculated_revision,last_changed_day,updated_ts,calculation_status,last_error) VALUES
($campaign,'main','noop_a',-5,'[]',2,2,4,10,'ready',''),
($campaign,'main','noop_b',11,'[]',3,3,4,10,'ready',''),
($campaign,'main','orphan',30,'[]',4,4,4,10,'ready','');",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId
                        });
                    ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,
base_chance_b_to_a,chance_a_to_b,chance_b_to_a,affinity_a_to_b,
affinity_b_to_a,tag_a_to_b,tag_b_to_a,first_day,last_day,
projected_native_relation,native_action_pending,native_action_id,
compatibility_version,updated_ts)
VALUES('noop_a|noop_b','noop_a','noop_b','INTJ','ENFP',80,80,70,70,
7,-3,'neutral','neutral',1,4,5,1,'native_target:noop_a|noop_b',
$version,101);",
                        new Dictionary<string, object>
                        {
                            ["version"] = MbtiChemistryVersion
                        });
                    Dictionary<string, object> resolved =
                        ResolveEffectiveAttitude(connection, campaignId, "main",
                            "noop_a", "noop_b", "self_test");
                    Dictionary<string, object> reverse =
                        ResolveEffectiveAttitude(connection, campaignId, "main",
                            "noop_b", "noop_a", "self_test");
                    add("standing_changes_effective_view_not_personal_affinity",
                        ReadBool(resolved, "hasPair", false)
                        && ReadInt(resolved, "personalAffinity", 0) == 7
                        && ReadInt(resolved, "targetPublicStanding", 0) == 11
                        && ReadInt(resolved, "effectiveAttitude", 0) == 18
                        && ReadInt(reverse, "personalAffinity", 0) == -3
                        && ReadInt(reverse, "targetPublicStanding", 0) == -5
                        && ReadInt(reverse, "effectiveAttitude", 0) == -8,
                        "Public Standing is added dynamically and directionally without rewriting personal affinity.");

                    string[] observers = { "favorite", "local_observer", "foreign_observer", "player_observer" };
                    foreach (string id in observers.Concat(new[] { "noop_b" }))
                        ExecuteSql(connection, @"INSERT INTO identity_roster(hero_id,canonical_name,kingdom_id,current_charm,is_player,updated_ts)
VALUES($id,$id,$kingdom,150,$player,1);", new Dictionary<string, object>
                        {
                            ["id"] = id, ["kingdom"] = id == "foreign_observer" ? "foreign" : "local",
                            ["player"] = id == "player_observer" ? 1 : 0
                        });
                    string favoringSources = Json.Serialize(new[]
                    {
                        new Dictionary<string, object> { ["baseTagId"] = "fixture", ["effectiveContribution"] = 11d },
                        new Dictionary<string, object> { ["baseTagId"] = "ruler_favoring", ["sourceType"] = "reputation",
                            ["linkedHeroId"] = "favorite", ["effectiveContribution"] = -10d }
                    });
                    ExecuteSql(connection, "UPDATE character_public_standing SET standing_value=1,sources_json=$sources WHERE subject_id='noop_b';",
                        new Dictionary<string, object> { ["sources"] = favoringSources });
                    Dictionary<string, string> expectedStanding = observers.ToDictionary(id => id,
                        id => Json.Serialize(ResolveObserverPublicStanding(connection, campaignId, "main", id, "noop_b")));
                    using (RelationshipStandingContext context = new RelationshipStandingContext(connection, campaignId, "main"))
                    {
                        context.Prefetch(observers.Concat(new[] { "noop_a", "noop_b", "missing_hero" }));
                        bool equivalent = true;
                        for (int iteration = 0; iteration < 128; iteration++)
                        {
                            foreach (string id in observers)
                                equivalent &= Json.Serialize(ResolveObserverPublicStanding(connection,
                                    campaignId, "main", id, "noop_b")) == expectedStanding[id];
                            equivalent &= ProjectNativeRelation(connection, "noop_b", "player_observer", 37, -81, 5, 6) == 37;
                        }
                        equivalent &= ReadInt(ReadPublicStandingRow(connection, campaignId, "main", "missing_hero"), "standing_value", 0) == 0;
                        add("standing_batch_preserves_observer_rules_and_bounded_reads",
                            equivalent && context.StandingReads == 1 && context.RosterReads == 1 && context.PlayerReads == 1,
                            "512 observer-specific resolutions and 128 native projections reuse three batch reads, preserving Favoring, foreign/player views and missing rows.");
                        ExecuteSql(connection, "UPDATE character_public_standing SET standing_value=17,sources_json='[]' WHERE subject_id='noop_b';");
                        bool mutationVisible = ReadInt(ResolveObserverPublicStanding(connection,
                            campaignId, "main", "favorite", "noop_b"), "value", 0) == 17;
                        foreach (string id in observers.Concat(new[] { "noop_a", "noop_b", "missing_hero" }))
                            context.ReadStanding(id);
                        mutationVisible &= ReadInt(context.ReadStanding("noop_a"), "standing_value", 0) == -5
                            && context.ReadStanding("missing_hero") == null;
                        ExecuteSql(connection, "UPDATE identity_roster SET current_charm=210,is_player=0 WHERE hero_id='player_observer';");
                        mutationVisible &= ReadInt(context.ReadRoster("player_observer"), "current_charm", 0) == 210;
                        foreach (string id in observers.Concat(new[] { "noop_b", "missing_hero" }))
                            context.ReadRoster(id);
                        mutationVisible &= context.ReadRoster("missing_hero") == null
                            && !context.PlayerIds().Contains("player_observer");
                        // A nested projection must invalidate the outer day as well,
                        // including previously absent subjects inserted by the write.
                        using (RelationshipStandingContext nested = new RelationshipStandingContext(connection, campaignId, "main"))
                        {
                            nested.Prefetch(new[] { "noop_b", "missing_hero" });
                            ExecuteSql(connection, @"INSERT INTO character_public_standing(
campaign_id,timeline_id,subject_id,standing_value,sources_json,revision,
calculated_revision,last_changed_day,updated_ts,calculation_status,last_error)
VALUES($campaign,'main','missing_hero',23,'[]',1,1,4,10,'ready','');",
                                new Dictionary<string, object> { ["campaign"] = campaignId });
                            mutationVisible &= ReadInt(nested.ReadStanding("missing_hero"), "standing_value", 0) == 23;
                            nested.ReadStanding("noop_b");
                            mutationVisible &= nested.StandingReads == 2;
                        }
                        foreach (string id in observers.Concat(new[] { "noop_a", "noop_b", "missing_hero" }))
                            context.ReadStanding(id);
                        mutationVisible &= ReadInt(context.ReadStanding("missing_hero"), "standing_value", 0) == 23;
                        add("standing_batch_observes_transaction_mutations",
                            mutationVisible && context.StandingReads == 3
                                && context.RosterReads == 2 && context.PlayerReads == 2,
                            "Standing, roster and player mutations refresh immediately in batches, preserving absent/inserted rows and invalidating nested and outer scopes.");
                    }
                    add("standing_batch_scope_does_not_escape_day", RelationshipStandingContext.For(connection) == null,
                        "The batch context is disposed before another day, timeline or restored state can use it.");
                    ExecuteSql(connection, "DELETE FROM character_public_standing WHERE subject_id='missing_hero';");
                    ExecuteSql(connection, "UPDATE identity_roster SET current_charm=150,is_player=1 WHERE hero_id='player_observer';");
                    ExecuteSql(connection, "UPDATE character_public_standing SET standing_value=11,sources_json='[]' WHERE subject_id='noop_b';");

                    List<Dictionary<string, object>> presenceFixture = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["heroIds"] = new[] { "a", "B", "a" } },
                        new Dictionary<string, object> { ["heroIds"] = new[] { "b", "c" } },
                        new Dictionary<string, object> { ["heroIds"] = new string[0] }
                    };
                    Dictionary<string, HashSet<int>> membership = BuildRelationshipCoPresenceIndex(presenceFixture);
                    string[] presenceIds = { "a", "b", "B", "c", "absent", "", null };
                    add("copresence_index_matches_group_scan",
                        presenceIds.All(a => presenceIds.All(b => RelationshipHeroesShareGroup(membership, a, b)
                            == AreRelationshipHeroesCoPresent(presenceFixture, a, b))),
                        "Indexed membership preserves overlaps, case-insensitivity, duplicates, self-membership and missing-ID behavior.");

                    add("relationship_scheduler_active_campaign_and_fairness",
                        RelationshipCampaignWorkPriority("active", "current", "active", "current", false)
                            < RelationshipCampaignWorkPriority("other", "main", "active", "current", false)
                        && RelationshipCampaignWorkPriority("active", "current", "active", "current", false)
                            < RelationshipCampaignWorkPriority("active", "older", "active", "current", false)
                        && RelationshipCampaignWorkPriority("other", "main", "active", "current", true)
                            < RelationshipCampaignWorkPriority("active", "current", "active", "current", true)
                        && RelationshipCampaignWorkPriority("other", "main", "", "", false) == 0,
                        "Fresh native campaign/timeline wins normally; the periodic maintenance slot serves another campaign and an absent heartbeat preserves chronological selection.");

                    int pairsBeforeOrphanRead = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry;")
                        .FirstOrDefault(), "count", -1);
                    Dictionary<string, object> orphan =
                        ResolveEffectiveAttitude(connection, campaignId, "main",
                            "noop_a", "orphan", "self_test");
                    int pairsAfterOrphanRead = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry;")
                        .FirstOrDefault(), "count", -1);
                    add("standing_read_never_creates_social_pair",
                        !ReadBool(orphan, "hasPair", true)
                        && ReadInt(orphan, "effectiveAttitude", -1) == 0
                        && pairsAfterOrphanRead == pairsBeforeOrphanRead,
                        "Reading a subject's Public Standing without an established relationship remains a pure read.");

                    ExecuteSql(connection, @"UPDATE character_public_standing
SET standing_value=0 WHERE subject_id IN ('noop_a','noop_b');");
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET affinity_a_to_b=4,affinity_b_to_a=6,updated_ts=101
WHERE pair_key='noop_a|noop_b';");
                    ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,
world_day,last_sync_day,attempt_count,claimed_ts,last_error,updated_ts,revision)
VALUES('noop_a|noop_b','noop_a','noop_b',5,0,'pending',4,-1000,0,0,'',202,7);");
                    Dictionary<string, object> noOpPair = QuerySql(connection,
                        "SELECT * FROM relationship_pair_chemistry WHERE pair_key='noop_a|noop_b';")
                        .First();
                    bool targetChanged = RefreshEffectivePairProjection(connection,
                        campaignId, "main", noOpPair, 5,
                        "unchanged_projection_self_test");
                    Dictionary<string, object> unchangedPair = QuerySql(connection,
                        "SELECT updated_ts FROM relationship_pair_chemistry WHERE pair_key='noop_a|noop_b';")
                        .FirstOrDefault();
                    Dictionary<string, object> unchangedTarget = QuerySql(connection,
                        "SELECT updated_ts,revision FROM relationship_native_targets WHERE pair_key='noop_a|noop_b';")
                        .FirstOrDefault();
                    add("unchanged_projection_does_not_rewrite_pair_or_target",
                        !targetChanged
                        && ReadLong(unchangedPair, "updated_ts", -1) == 101
                        && ReadLong(unchangedTarget, "updated_ts", -1) == 202
                        && ReadInt(unchangedTarget, "revision", -1) == 7,
                        "An unchanged effective/native result preserves both row timestamps and the native target revision.");

                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET affinity_a_to_b=3,affinity_b_to_a=7,updated_ts=303
WHERE pair_key='noop_a|noop_b';");
                    Dictionary<string, object> sameAveragePair = QuerySql(connection,
                        "SELECT * FROM relationship_pair_chemistry WHERE pair_key='noop_a|noop_b';")
                        .First();
                    bool sameAverageTargetChanged =
                        RefreshEffectivePairProjection(connection, campaignId,
                            "main", sameAveragePair, 6,
                            "same_rounded_projection_self_test");
                    Dictionary<string, object> sameAverageAfter = QuerySql(connection,
                        "SELECT updated_ts FROM relationship_pair_chemistry WHERE pair_key='noop_a|noop_b';")
                        .FirstOrDefault();
                    Dictionary<string, object> sameAverageTarget = QuerySql(connection,
                        "SELECT updated_ts,revision FROM relationship_native_targets WHERE pair_key='noop_a|noop_b';")
                        .FirstOrDefault();
                    add("same_rounded_native_result_does_not_rewrite_projection",
                        !sameAverageTargetChanged
                        && ReadLong(sameAverageAfter, "updated_ts", -1) == 303
                        && ReadLong(sameAverageTarget, "updated_ts", -1) == 202
                        && ReadInt(sameAverageTarget, "revision", -1) == 7,
                        "Directional affinity changes whose authoritative average is unchanged do not churn the native target.");

                    AmbientPairContext lifecyclePair = new AmbientPairContext
                    {
                        PairKey = "family_a|family_b",
                        HeroAId = "family_a", HeroBId = "family_b"
                    };
                    ExecuteSql(connection, @"INSERT INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,last_processed_day,version,updated_ts)
VALUES('family_a|family_b','family_a','family_b',1,$version,515);",
                        new Dictionary<string, object>
                        {
                            ["version"] = RelationshipLifecycleVersion
                        });
                    Dictionary<string, object> familyA =
                        new Dictionary<string, object>
                        {
                            ["heroStringId"] = "family_a", ["age"] = 30,
                            ["fatherId"] = "shared_father",
                            ["isFemale"] = false
                        };
                    Dictionary<string, object> familyB =
                        new Dictionary<string, object>
                        {
                            ["heroStringId"] = "family_b", ["age"] = 28,
                            ["fatherId"] = "shared_father",
                            ["isFemale"] = true
                        };
                    ProcessRelationshipLifecycle(connection, campaignId,
                        lifecyclePair, 9, familyA, familyB, 0, 0,
                        null, false, "main");
                    Dictionary<string, object> unchangedLifecycle =
                        QuerySql(connection, @"SELECT last_processed_day,updated_ts
FROM relationship_pair_lifecycle WHERE pair_key='family_a|family_b';")
                        .FirstOrDefault();
                    add("lifecycle_no_event_does_not_write_processed_date",
                        ReadInt(unchangedLifecycle, "last_processed_day", -1) == 1
                        && ReadLong(unchangedLifecycle, "updated_ts", -1) == 515,
                        "A lifecycle inspection with no state transition does not rewrite the row merely to advance a processed date.");

                    foreach (string hero in new[]
                    {
                        "political_ruler", "political_leader_a",
                        "political_leader_b", "race_pair_a",
                        "race_pair_b"
                    })
                    {
                        ExecuteSql(connection, @"INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,
template_version,traits_json,created_ts,updated_ts)
VALUES($hero,'INTJ','Architect','Self-test personality',
'pregenerated_profile',0,1,'{}',1,1);",
                            new Dictionary<string, object> { ["hero"] = hero });
                    }
                    bool firstConcurrentCreate = false;
                    bool secondConcurrentCreate = false;
                    System.Threading.Tasks.Task.WaitAll(
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            using (ReignDbConnection concurrent =
                                OpenCampaignConnection(campaignId))
                            {
                                firstConcurrentCreate = EnsurePoliticalRelationshipPair(
                                    concurrent, campaignId, "main", "race_pair_a|race_pair_b",
                                    "race_pair_a", "race_pair_b", 7,
                                    true, true);
                            }
                        }),
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            using (ReignDbConnection concurrent =
                                OpenCampaignConnection(campaignId))
                            {
                                secondConcurrentCreate = EnsurePoliticalRelationshipPair(
                                    concurrent, campaignId, "main", "race_pair_a|race_pair_b",
                                    "race_pair_a", "race_pair_b", 7,
                                    true, true);
                            }
                        }));
                    int concurrentPairRows = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_chemistry
WHERE pair_key='race_pair_a|race_pair_b';")
                        .FirstOrDefault(), "count", -1);
                    add("political_pair_creation_is_concurrency_safe",
                        firstConcurrentCreate != secondConcurrentCreate
                        && concurrentPairRows == 1,
                        "Concurrent topology workers create one chemistry row and only its owner reports a new pair.");
                    ExecuteSql(connection, @"INSERT INTO identity_roster(
hero_id,canonical_name,clan_id,kingdom_id,is_lord,is_ruler,
family_ids_json,updated_ts,is_alive,is_adult,is_player,is_clan_leader)
VALUES
('political_ruler','Ruler','clan_r','kingdom_test',1,1,'[]',1,1,1,0,1),
('political_leader_a','Leader A','clan_a','kingdom_test',1,0,'[]',1,1,1,0,1),
('political_leader_b','Leader B','clan_b','kingdom_test',1,0,'[]',1,1,1,0,1);");
                    Dictionary<string, object> firstPolitical =
                        ReconcilePoliticalRelationshipNetwork(connection,
                            campaignId, "main", 7, "self_test_first");
                    ExecuteSql(connection, @"UPDATE relationship_pair_provenance
SET updated_ts=404 WHERE source='kingdom_leadership' AND active=1;");
                    ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET updated_ts=405 WHERE pair_key LIKE 'political_%';");
                    Dictionary<string, object> secondPolitical =
                        ReconcilePoliticalRelationshipNetwork(connection,
                            campaignId, "main", 7, "self_test_repeat");
                    int unchangedProvenance = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_provenance
WHERE source='kingdom_leadership' AND active=1 AND updated_ts=404;")
                        .FirstOrDefault(), "count", -1);
                    int unchangedPoliticalPairs = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_chemistry
WHERE pair_key LIKE 'political_%' AND updated_ts=405;")
                        .FirstOrDefault(), "count", -1);
                    add("political_reconciliation_is_write_idempotent",
                        ReadInt(firstPolitical, "requiredPairs", -1) == 3
                        && ReadInt(firstPolitical, "createdPairs", -1) == 3
                        && ReadInt(secondPolitical, "createdPairs", -1) == 0
                        && unchangedProvenance == 3
                        && unchangedPoliticalPairs == 3,
                        "Repeating an unchanged political topology performs no pair or provenance rewrites.");

                    ExecuteSql(connection, @"UPDATE identity_roster
SET is_clan_leader=0 WHERE hero_id='political_leader_b';");
                    ReconcilePoliticalRelationshipNetwork(connection, campaignId,
                        "main", 8, "self_test_leader_removed");
                    int retainedFormerPairs = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_chemistry
WHERE pair_key IN (
'political_leader_a|political_leader_b',
'political_leader_b|political_ruler');")
                        .FirstOrDefault(), "count", -1);
                    int inactiveFormerRequirements = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_provenance
WHERE source='kingdom_leadership' AND active=0
AND pair_key IN (
'political_leader_a|political_leader_b',
'political_leader_b|political_ruler');")
                        .FirstOrDefault(), "count", -1);
                    add("former_political_pairs_retain_personal_history",
                        retainedFormerPairs == 2
                        && inactiveFormerRequirements == 2,
                        "Leadership changes remove only political provenance while retaining the established personal pairs.");
                }
            }
            finally
            {
                ReignPostgreSqlStorage.ClearAllPools();
                try { ReignPostgreSqlStorage.DropCampaign(campaignId); } catch { }
                TryDeleteDirectory(CampaignDirectory(campaignId));
            }
            return results;
        }

        private static Dictionary<string, object> BuildWorldTestStandingAndPoliticalMetrics(
            ReignDbConnection connection, string campaignId, string timelineId,
            double latestDay)
        {
            EnsureWorldRelationshipSchema(connection);
            Dictionary<string, object> standing = QuerySql(connection, @"
SELECT COUNT(*) AS character_count,
SUM(CASE WHEN standing_value>0 THEN 1 ELSE 0 END) AS positive_count,
SUM(CASE WHEN standing_value<0 THEN 1 ELSE 0 END) AS negative_count,
SUM(CASE WHEN standing_value=0 THEN 1 ELSE 0 END) AS neutral_count,
COALESCE(MIN(standing_value),0) AS minimum,
COALESCE(MAX(standing_value),0) AS maximum,
COALESCE(AVG(standing_value),0) AS average,
SUM(CASE WHEN calculation_status<>'ready' THEN 1 ELSE 0 END) AS error_count,
SUM(CASE WHEN calculated_revision<>revision THEN 1 ELSE 0 END) AS stale_count
FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["day"] = latestDay
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            List<int> standingValues = QuerySql(connection, @"
SELECT standing_value FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY standing_value;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).Select(row => ReadInt(row, "standing_value", 0)).ToList();
            double standingMedian = standingValues.Count == 0 ? 0d
                : standingValues.Count % 2 == 1
                    ? standingValues[standingValues.Count / 2]
                    : (standingValues[standingValues.Count / 2 - 1]
                        + standingValues[standingValues.Count / 2]) / 2d;
            Dictionary<string, int> sourceCategories =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            long revisionCount = 0;
            foreach (Dictionary<string, object> row in QuerySql(connection, @"
SELECT sources_json,revision FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }))
            {
                revisionCount += Math.Max(0, ReadInt(row, "revision", 1) - 1);
                foreach (Dictionary<string, object> source in ParsePublicStandingSources(
                    ReadString(row, "sources_json", "[]")))
                {
                    string category = FirstNonEmpty(
                        ReadString(source, "sourceType", ""), "other");
                    sourceCategories[category] =
                        (sourceCategories.TryGetValue(category, out int count)
                            ? count : 0) + 1;
                }
            }
            Dictionary<string, object> invalidations = QuerySql(connection, @"
SELECT COUNT(*) AS total,
SUM(CASE WHEN status='pending' THEN 1 ELSE 0 END) AS pending,
SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END) AS failed,
COALESCE(SUM(CASE WHEN status='pending' THEN affected_pairs-processed_pairs ELSE 0 END),0)
AS remaining_pairs,
COALESCE(MAX(CASE WHEN status='pending' THEN $now-created_ts ELSE 0 END),0)
AS oldest_pending_seconds,
COALESCE(SUM(affected_pairs),0) AS affected_observers
FROM public_standing_invalidations
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> political = QuerySql(connection, @"
SELECT COUNT(*) AS active_required,
SUM(CASE WHEN source='kingdom_leadership' THEN 1 ELSE 0 END) AS kingdom_pairs,
SUM(CASE WHEN source='ruler_network' THEN 1 ELSE 0 END) AS ruler_pairs
FROM relationship_pair_provenance
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND source IN ('kingdom_leadership','ruler_network')
AND active=1 AND required=1;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, List<Dictionary<string, object>>>
                desiredPolitical = BuildRequiredPoliticalPairDefinitions(
                    QuerySql(connection, @"
SELECT * FROM identity_roster
WHERE is_alive=1 AND is_adult=1 AND is_player=0
AND (is_clan_leader=1 OR is_ruler=1) AND hero_id<>'';"));
            HashSet<string> currentPolitical = new HashSet<string>(QuerySql(
                    connection, @"
SELECT pair_key,source FROM relationship_pair_provenance
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND source IN ('kingdom_leadership','ruler_network')
AND active=1 AND required=1;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).Select(row => ReadString(row, "pair_key", "") + "|"
                    + ReadString(row, "source", "")),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> currentPairKeys = new HashSet<string>(QuerySql(
                    connection,
                    "SELECT pair_key FROM relationship_pair_chemistry;")
                .Select(row => ReadString(row, "pair_key", "")),
                StringComparer.OrdinalIgnoreCase);
            int missingPolitical = desiredPolitical.Sum(pair =>
                pair.Value.Count(provenance =>
                    !currentPolitical.Contains(pair.Key + "|"
                        + ReadString(provenance, "source", ""))
                    || !currentPairKeys.Contains(pair.Key)));
            HashSet<string> desiredPoliticalKeys = new HashSet<string>(
                desiredPolitical.SelectMany(pair => pair.Value.Select(provenance =>
                    pair.Key + "|" + ReadString(provenance, "source", ""))),
                StringComparer.OrdinalIgnoreCase);
            int obsoletePolitical = currentPolitical.Count(key =>
                !desiredPoliticalKeys.Contains(key));
            Dictionary<string, int> effectiveBands = WorldTestRelationshipBands
                .ToDictionary(value => value, value => 0,
                    StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in QuerySql(connection, @"
SELECT p.affinity_a_to_b,p.affinity_b_to_a,
COALESCE(sa.standing_value,0) AS standing_a,
COALESCE(sb.standing_value,0) AS standing_b
FROM relationship_pair_chemistry p
LEFT JOIN character_public_standing sa ON sa.campaign_id=$campaign
AND sa.timeline_id=$timeline AND sa.subject_id=p.hero_a_id
LEFT JOIN character_public_standing sb ON sb.campaign_id=$campaign
AND sb.timeline_id=$timeline AND sb.subject_id=p.hero_b_id; ",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }))
            {
                string bandAB = RelationshipBand(Clamp(ReadInt(row, "affinity_a_to_b", 0)
                    + ReadInt(row, "standing_b", 0), -100, 100));
                string bandBA = RelationshipBand(Clamp(ReadInt(row, "affinity_b_to_a", 0)
                    + ReadInt(row, "standing_a", 0), -100, 100));
                effectiveBands[bandAB]++;
                effectiveBands[bandBA]++;
            }
            return new Dictionary<string, object>
            {
                ["publicStanding"] = new Dictionary<string, object>
                {
                    ["characterCount"] = ReadInt(standing, "character_count", 0),
                    ["positiveCount"] = ReadInt(standing, "positive_count", 0),
                    ["negativeCount"] = ReadInt(standing, "negative_count", 0),
                    ["neutralCount"] = ReadInt(standing, "neutral_count", 0),
                    ["minimum"] = ReadInt(standing, "minimum", 0),
                    ["maximum"] = ReadInt(standing, "maximum", 0),
                    ["average"] = Math.Round(ReadDouble(standing, "average", 0d), 2),
                    ["median"] = standingMedian,
                    ["revisionCount"] = revisionCount,
                    ["sourceCategories"] = sourceCategories,
                    ["errorCount"] = ReadInt(standing, "error_count", 0),
                    ["staleCount"] = ReadInt(standing, "stale_count", 0),
                    ["invalidationsPending"] = ReadInt(invalidations, "pending", 0),
                    ["invalidationFailures"] = ReadInt(invalidations, "failed", 0),
                    ["invalidationPairsRemaining"] = ReadInt(invalidations,
                        "remaining_pairs", 0),
                    ["affectedObserverCount"] = ReadLong(invalidations,
                        "affected_observers", 0),
                    ["oldestPendingInvalidationSeconds"] = ReadLong(invalidations,
                        "oldest_pending_seconds", 0)
                },
                ["politicalNetwork"] = new Dictionary<string, object>
                {
                    ["activeRequiredPairs"] = ReadInt(political, "active_required", 0),
                    ["kingdomLeadershipPairs"] = ReadInt(political, "kingdom_pairs", 0),
                    ["rulerNetworkPairs"] = ReadInt(political, "ruler_pairs", 0),
                    ["missingRequiredPairs"] = missingPolitical,
                    ["obsoleteRequiredPairs"] = obsoletePolitical
                },
                ["effectiveBands"] = effectiveBands
            };
        }
    }
}
