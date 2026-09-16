using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int PostgreSqlPublicStandingSchemaRevision = 2;

        private static void EnsurePublicStandingSchema(ReignDbConnection connection)
        {
            const string marker =
                "postgresql_public_standing_schema_revision";
            if (IsPostgreSqlComponentSchemaReady(connection, marker,
                PostgreSqlPublicStandingSchemaRevision))
                return;
            EnsurePublicStandingSchemaCore(connection);
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_public_standing_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                    new Dictionary<string, object>
                    {
                        ["revision"] =
                            PostgreSqlPublicStandingSchemaRevision.ToString()
                    });
                MarkPostgreSqlComponentSchemaReady(connection, marker,
                    PostgreSqlPublicStandingSchemaRevision);
            }
        }

        private static void EnsurePublicStandingSchemaCore(
            ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_public_standing (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,
standing_value INTEGER NOT NULL DEFAULT 0,sources_json TEXT NOT NULL DEFAULT '[]',
revision INTEGER NOT NULL DEFAULT 1,last_changed_day REAL NOT NULL DEFAULT 0,
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id));");
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_character_public_standing_subject
ON character_public_standing(campaign_id,timeline_id,subject_id);");
            EnsureDatabaseColumn(connection, "character_public_standing", "calculation_status",
                "TEXT NOT NULL DEFAULT 'ready'");
            EnsureDatabaseColumn(connection, "character_public_standing", "last_error",
                "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "character_public_standing",
                "calculated_revision", "INTEGER NOT NULL DEFAULT 1");
        }

        private static int EnsurePublicStandingForRoster(ReignDbConnection connection,
            string campaignId, string timelineId, double worldDay,
            bool recomputeTaggedSubjects = true)
        {
            EnsurePublicStandingSchema(connection);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"INSERT OR IGNORE INTO character_public_standing(
campaign_id,timeline_id,subject_id,standing_value,sources_json,revision,last_changed_day,updated_ts)
SELECT $campaign,$timeline,hero_id,0,'[]',1,$day,$ts
FROM identity_roster WHERE hero_id<>'';",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["day"] = worldDay,
                    ["ts"] = ts
                });

            if (recomputeTaggedSubjects)
            {
                List<string> taggedSubjects = QuerySql(connection, @"
SELECT DISTINCT rst.subject_id
FROM rumor_subject_tags rst
JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline
UNION
SELECT DISTINCT subject_id
FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline; ",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                })
                .Select(row => ReadString(row, "subject_id", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
                RecomputePublicStandingForSubjects(connection, campaignId, timelineId,
                    taggedSubjects, worldDay);
            }
            return ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                }).FirstOrDefault(), "count", 0);
        }

        private static void RecomputePublicStandingForSubjects(ReignDbConnection connection,
            string campaignId, string timelineId, IEnumerable<string> subjectIds, double worldDay)
        {
            EnsurePublicStandingSchema(connection);
            foreach (string subjectId in (subjectIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                UpsertPublicStandingTrait(connection, campaignId, timelineId,
                    subjectId, worldDay);
            }
        }

        private static Dictionary<string, object> ReadPublicStandingTrait(
            ReignDbConnection connection, string campaignId, string timelineId,
            string subjectId, double worldDay)
        {
            EnsurePublicStandingSchema(connection);
            Dictionary<string, object> row = QuerySql(connection, @"
SELECT subject_id,standing_value,sources_json,revision,calculated_revision,last_changed_day,
calculation_status,last_error
FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
LIMIT 1;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["subject"] = subjectId
                }).FirstOrDefault();
            if (row == null)
            {
                return new Dictionary<string, object>
                {
                    ["subjectId"] = subjectId,
                    ["standingValue"] = 0,
                    ["sources"] = new List<object>(),
                    ["revision"] = 1,
                    ["lastChangedCampaignDay"] = worldDay,
                    ["calculationStatus"] = "missing",
                    ["lastError"] = ""
                };
            }
            return new Dictionary<string, object>
            {
                ["subjectId"] = ReadString(row, "subject_id", subjectId),
                ["standingValue"] = ReadInt(row, "standing_value", 0),
                ["sources"] = ParsePublicStandingSources(
                    ReadString(row, "sources_json", "[]")),
                ["revision"] = ReadInt(row, "revision", 1),
                ["calculatedRevision"] = ReadInt(row,
                    "calculated_revision", 1),
                ["lastChangedCampaignDay"] = ReadDouble(row, "last_changed_day", worldDay),
                ["calculationStatus"] = ReadString(row, "calculation_status", "ready"),
                ["lastError"] = ReadString(row, "last_error", "")
            };
        }

        private static List<Dictionary<string, object>> ParsePublicStandingSources(
            string json)
        {
            try
            {
                return Json.Deserialize<List<Dictionary<string, object>>>(
                    string.IsNullOrWhiteSpace(json) ? "[]" : json)
                    ?? new List<Dictionary<string, object>>();
            }
            catch
            {
                return new List<Dictionary<string, object>>();
            }
        }

        private static void UpsertPublicStandingTrait(ReignDbConnection connection,
            string campaignId, string timelineId, string subjectId, double worldDay)
        {
            if (string.IsNullOrWhiteSpace(subjectId))
                return;

            Dictionary<string, object> roster = QuerySql(connection, @"
SELECT current_charm FROM identity_roster WHERE hero_id=$subject LIMIT 1;",
                new Dictionary<string, object> { ["subject"] = subjectId }).FirstOrDefault();
            int charm = ReadInt(roster, "current_charm", 0);
            double mitigation = SocialCharmMitigation(charm);
            List<Dictionary<string, object>> sources = new List<Dictionary<string, object>>();

            foreach (Dictionary<string, object> rumor in QuerySql(connection, @"
SELECT rst.tag_id,rst.description,rst.rumor_value,rst.snapshot_json,
ro.occurrence_id,ro.archetype_id,ro.world_day,ro.expires_day,
ro.catalog_revision,ro.provenance_summary
FROM rumor_subject_tags rst
JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline
AND rst.subject_id=$subject AND rst.status='active'
AND ro.status='active' AND ro.expires_day>$day
AND ro.world_day=(
    SELECT MAX(ro2.world_day)
    FROM rumor_subject_tags rst2
    JOIN rumor_occurrences ro2 ON ro2.occurrence_id=rst2.occurrence_id
    WHERE rst2.subject_id=rst.subject_id AND rst2.tag_id=rst.tag_id
    AND rst2.status='active' AND ro2.campaign_id=ro.campaign_id
    AND ro2.timeline_id=ro.timeline_id AND ro2.status='active'
    AND ro2.expires_day>$day)
ORDER BY rst.tag_id,ro.world_day,ro.occurrence_id;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["subject"] = subjectId,
                    ["day"] = worldDay
                }))
            {
                int rawValue = ReadInt(rumor, "rumor_value", 0);
                Dictionary<string, object> snapshot =
                    TryParseJsonObject(ReadString(rumor, "snapshot_json", "{}"))
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> source = PublicStandingSource(
                    "rumor",
                    ReadString(rumor, "tag_id", ""),
                    ReadString(snapshot, "label",
                        ReadString(rumor, "tag_id", "Rumor")),
                    ReadString(rumor, "description", ""),
                    rawValue,
                    rawValue < 0 ? rawValue * (1d - mitigation) : rawValue,
                    ReadString(rumor, "occurrence_id", ""),
                    ReadString(rumor, "archetype_id", ""),
                    ReadDouble(rumor, "world_day", 0d),
                    ReadDouble(rumor, "expires_day", 0d),
                    ReadInt(rumor, "catalog_revision", 0),
                    ReadString(rumor, "provenance_summary", ""));
                AddParameterizedPublicStandingMetadata(source, snapshot);
                sources.Add(source);
            }

            foreach (Dictionary<string, object> reputation in QuerySql(connection, @"
SELECT tag_id,description,reputation_value,snapshot_json,source_occurrence_id,
archetype_id,acquired_day,catalog_revision
FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND status='active'
ORDER BY tag_id,acquired_day,source_occurrence_id;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["subject"] = subjectId
                }))
            {
                int rawValue = ReadInt(reputation, "reputation_value", 0);
                Dictionary<string, object> snapshot =
                    TryParseJsonObject(ReadString(reputation, "snapshot_json", "{}"))
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> source = PublicStandingSource(
                    "reputation",
                    ReadString(reputation, "tag_id", ""),
                    ReadString(snapshot, "label",
                        ReadString(reputation, "tag_id", "Reputation")),
                    ReadString(reputation, "description", ""),
                    rawValue,
                    rawValue < 0 ? rawValue * (1d - mitigation) : rawValue,
                    ReadString(reputation, "source_occurrence_id", ""),
                    ReadString(reputation, "archetype_id", ""),
                    ReadDouble(reputation, "acquired_day", 0d),
                    0d,
                    ReadInt(reputation, "catalog_revision", 0),
                    "");
                AddParameterizedPublicStandingMetadata(source, snapshot);
                sources.Add(source);
            }

            CollapseRulerFavoringPublicStanding(sources, mitigation);
            CollapseWhoremongerPublicStanding(sources);

            sources = sources
                .OrderBy(source => ReadString(source, "sourceType", ""))
                .ThenBy(source => ReadString(source, "tagId", ""))
                .ThenBy(source => ReadString(source, "sourceId", ""))
                .ToList();
            string sourcesJson = Json.Serialize(sources);
            int standingValue = RoundAwayFromZero(sources.Sum(source =>
                ReadDouble(source, "effectiveContribution", 0d)));
            Dictionary<string, object> existing = QuerySql(connection, @"
SELECT standing_value,sources_json,revision,last_changed_day
FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
LIMIT 1;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["subject"] = subjectId
                }).FirstOrDefault();
            bool changed = existing == null
                || ReadInt(existing, "standing_value", 0) != standingValue
                || !string.Equals(ReadString(existing, "sources_json", "[]"),
                    sourcesJson, StringComparison.Ordinal);
            if (!changed)
                return;
            bool valueChanged = existing == null
                || ReadInt(existing, "standing_value", 0) != standingValue;

            int revision = existing == null ? 1 : ReadInt(existing, "revision", 1) + 1;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"INSERT INTO character_public_standing(
campaign_id,timeline_id,subject_id,standing_value,sources_json,revision,calculated_revision,last_changed_day,updated_ts)
VALUES($campaign,$timeline,$subject,$value,$sources,$revision,$revision,$day,$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
standing_value=$value,sources_json=$sources,revision=$revision,
calculated_revision=$revision,
last_changed_day=$day,updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["subject"] = subjectId,
                    ["value"] = standingValue,
                    ["sources"] = sourcesJson,
                    ["revision"] = revision,
                    ["day"] = worldDay,
                    ["ts"] = ts
                });
            if (valueChanged)
                QueuePublicStandingInvalidation(connection, campaignId, timelineId,
                    subjectId, revision, worldDay);
        }

        private static Dictionary<string, object> PublicStandingSource(
            string sourceType, string tagId, string label, string description,
            int rawValue, double effectiveContribution, string sourceId,
            string archetypeId, double acquiredDay, double expiresDay,
            int catalogRevision, string provenance)
        {
            return new Dictionary<string, object>
            {
                ["sourceType"] = sourceType,
                ["tagId"] = tagId,
                ["label"] = label,
                ["description"] = description,
                ["rawValue"] = rawValue,
                ["effectiveContribution"] = effectiveContribution,
                ["sourceId"] = sourceId,
                ["archetypeId"] = archetypeId,
                ["acquiredDay"] = acquiredDay,
                ["expiresDay"] = expiresDay,
                ["catalogRevision"] = catalogRevision,
                ["provenance"] = provenance
            };
        }

        private static void AddParameterizedPublicStandingMetadata(
            Dictionary<string, object> source, Dictionary<string, object> snapshot)
        {
            if (source == null || snapshot == null) return;
            string baseTagId = ReadString(snapshot, "baseTagId", "");
            string linkedHeroId = ReadString(snapshot, "linkedHeroId", "");
            string linkedHeroName = ReadString(snapshot, "linkedHeroName", "");
            if (!string.IsNullOrWhiteSpace(baseTagId)) source["baseTagId"] = baseTagId;
            if (!string.IsNullOrWhiteSpace(linkedHeroId)) source["linkedHeroId"] = linkedHeroId;
            if (!string.IsNullOrWhiteSpace(linkedHeroName)) source["linkedHeroName"] = linkedHeroName;
        }

        private static void CollapseRulerFavoringPublicStanding(
            List<Dictionary<string, object>> sources, double charmMitigation)
        {
            List<Dictionary<string, object>> favoring = (sources ?? new List<Dictionary<string, object>>())
                .Where(IsRulerFavoringStandingSource).ToList();
            if (favoring.Count == 0) return;
            bool established = favoring.Any(source => ReadString(source, "sourceType", "")
                .Equals("reputation", StringComparison.OrdinalIgnoreCase));
            Dictionary<string, object> representative = favoring
                .OrderByDescending(source => ReadString(source, "sourceType", "") == "reputation")
                .ThenBy(source => ReadString(source, "linkedHeroId", ""), StringComparer.OrdinalIgnoreCase)
                .First();
            foreach (Dictionary<string, object> source in favoring)
            {
                source["collapsedFamily"] = "ruler_favoring";
                source["effectiveContribution"] = 0d;
            }
            int raw = established ? -10 : -5;
            representative["collapsedRepresentative"] = true;
            representative["collapsedStage"] = established ? "reputation" : "rumor";
            representative["rawValue"] = raw;
            representative["effectiveContribution"] = raw * (1d - charmMitigation);
        }

        private static bool IsRulerFavoringStandingSource(Dictionary<string, object> source)
        {
            return ReadString(source, "baseTagId", ReadString(source, "tagId", ""))
                .Equals("ruler_favoring", StringComparison.OrdinalIgnoreCase);
        }
    }
}
