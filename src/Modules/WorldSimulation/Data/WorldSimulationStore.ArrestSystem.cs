using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The WorldSimulation module's SQL statements; every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class WorldSimulationStore
    {
        internal static List<Dictionary<string, object>> SelectExposedForeignAgentArrestEvidence(ReignDbConnection connection,
            Dictionary<string, object> parameters)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT agent_hero_id,sponsor_kingdom_id,status
FROM foreign_agent_assignments
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND agent_hero_id=$accused AND status='exposed';", parameters);
        }

        internal static void Begin(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND tag_id=$tag LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
INSERT INTO character_reputations(campaign_id,timeline_id,subject_id,tag_id,
source_occurrence_id,archetype_id,subject_role,description,reputation_value,
acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,'',$archetype,'accused',$description,
$value,$day,0,$snapshot,'active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET
description=$description,reputation_value=$value,acquired_day=$day,
snapshot_json=$snapshot,status='active',updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpdateCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
UPDATE character_reputations SET status='rescinded_by_accuser',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND tag_id=$tag;", parameters, transaction);
        }

        internal static void Commit(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "COMMIT;", parameters, transaction);
        }

        internal static void Rollback(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK;", parameters, transaction);
        }
    }
}
