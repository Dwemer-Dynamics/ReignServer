using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class ReputationService
    {
        private const int PostgreSqlPublicStandingSchemaRevision = 2;

        internal static void EnsurePublicStandingSchema(ReignDbConnection connection)
        {
            const string marker =
                "postgresql_public_standing_schema_revision";
            if (ReignDatabase.IsPostgreSqlComponentSchemaReady(connection, marker,
                PostgreSqlPublicStandingSchemaRevision))
                return;
            EnsurePublicStandingSchemaCore(connection);
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                ReputationStore.UpsertSchemaMeta5(connection, new Dictionary<string, object>
                    {
                        ["revision"] =
                            PostgreSqlPublicStandingSchemaRevision.ToString()
                    });
                ReignDatabase.MarkPostgreSqlComponentSchemaReady(connection, marker,
                    PostgreSqlPublicStandingSchemaRevision);
            }
        }

        private static void EnsurePublicStandingSchemaCore(
            ReignDbConnection connection)
        {
            ReputationSchema.CreateCharacterPublicStanding(connection);
            ReputationSchema.CreateIndexCharacterPublicStandingSubject(connection);
            ReputationSchema.EnsureColumnCharacterPublicStandingCalculationStatus(connection);
            ReputationSchema.EnsureColumnCharacterPublicStandingLastError(connection);
            ReputationSchema.EnsureColumnCharacterPublicStandingCalculatedRevision(connection);
        }

        internal static int EnsurePublicStandingForRoster(ReignDbConnection connection,
            string campaignId, string timelineId, double worldDay,
            bool recomputeTaggedSubjects = true)
        {
            EnsurePublicStandingSchema(connection);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ReputationStore.InsertOrIgnoreCharacterPublicStanding(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["day"] = worldDay,
                    ["ts"] = ts
                });

            if (recomputeTaggedSubjects)
            {
                List<string> taggedSubjects = ReputationStore.SelectRumorSubjectTags2(connection, new Dictionary<string, object>
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
            return ReadInt(ReputationStore.CountCharacterPublicStanding(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                }).FirstOrDefault(), "count", 0);
        }

        internal static void RecomputePublicStandingForSubjects(ReignDbConnection connection,
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

        internal static Dictionary<string, object> ReadPublicStandingTrait(
            ReignDbConnection connection, string campaignId, string timelineId,
            string subjectId, double worldDay)
        {
            EnsurePublicStandingSchema(connection);
            Dictionary<string, object> row = ReputationStore.SelectCharacterPublicStanding(connection, new Dictionary<string, object>
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

        internal static List<Dictionary<string, object>> ParsePublicStandingSources(
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

            Dictionary<string, object> roster = ReputationStore.SelectIdentityRoster4(connection, new Dictionary<string, object> { ["subject"] = subjectId }).FirstOrDefault();
            int charm = ReadInt(roster, "current_charm", 0);
            double mitigation = SocialCharmMitigation(charm);
            List<Dictionary<string, object>> sources = new List<Dictionary<string, object>>();

            foreach (Dictionary<string, object> rumor in ReputationStore.SelectRumorSubjectTags3(connection, new Dictionary<string, object>
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

            foreach (Dictionary<string, object> reputation in ReputationStore.SelectCharacterReputations9(connection, new Dictionary<string, object>
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
            int standingValue = RelationshipsService.RoundAwayFromZero(sources.Sum(source =>
                ReadDouble(source, "effectiveContribution", 0d)));
            Dictionary<string, object> existing = ReputationStore.SelectCharacterPublicStanding2(connection, new Dictionary<string, object>
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
            ReputationStore.UpsertCharacterPublicStanding(connection, new Dictionary<string, object>
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
                RelationshipsService.QueuePublicStandingInvalidation(connection, campaignId, timelineId,
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

        internal static bool IsRulerFavoringStandingSource(Dictionary<string, object> source)
        {
            return ReadString(source, "baseTagId", ReadString(source, "tagId", ""))
                .Equals("ruler_favoring", StringComparison.OrdinalIgnoreCase);
        }
    }
}
