using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class ReputationStore
    {
        internal static void UpsertSchemaMeta5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_public_standing_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;", parameters, transaction);
        }

        internal static void InsertOrIgnoreCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO character_public_standing(
campaign_id,timeline_id,subject_id,standing_value,sources_json,revision,last_changed_day,updated_ts)
SELECT $campaign,$timeline,hero_id,0,'[]',1,$day,$ts
FROM identity_roster WHERE hero_id<>'';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT DISTINCT rst.subject_id
FROM rumor_subject_tags rst
JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline
UNION
SELECT DISTINCT subject_id
FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline; ", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT subject_id,standing_value,sources_json,revision,calculated_revision,last_changed_day,
calculation_status,last_error
FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT current_charm FROM identity_roster WHERE hero_id=$subject LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
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
ORDER BY rst.tag_id,ro.world_day,ro.occurrence_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT tag_id,description,reputation_value,snapshot_json,source_occurrence_id,
archetype_id,acquired_day,catalog_revision
FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND status='active'
ORDER BY tag_id,acquired_day,source_occurrence_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPublicStanding2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT standing_value,sources_json,revision,last_changed_day
FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_public_standing(
campaign_id,timeline_id,subject_id,standing_value,sources_json,revision,calculated_revision,last_changed_day,updated_ts)
VALUES($campaign,$timeline,$subject,$value,$sources,$revision,$revision,$day,$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
standing_value=$value,sources_json=$sources,revision=$revision,
calculated_revision=$revision,
last_changed_day=$day,updated_ts=$ts;", parameters, transaction);
        }
    }
}
