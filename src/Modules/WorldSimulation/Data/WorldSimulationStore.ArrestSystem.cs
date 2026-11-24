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

        internal static void EnsureArrestCaseEffects(ReignDbConnection connection)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS arrest_case_effect_state (
campaign_id TEXT NOT NULL, timeline_id TEXT NOT NULL, case_id TEXT NOT NULL,
accused_id TEXT NOT NULL, player_id TEXT NOT NULL, tag_id TEXT NOT NULL, rescinded BOOLEAN NOT NULL,
PRIMARY KEY (campaign_id, timeline_id, case_id),
UNIQUE (campaign_id, timeline_id, accused_id, tag_id));");
        }

        internal static List<Dictionary<string, object>> SelectArrestCaseEffects(ReignDbConnection connection,
            Dictionary<string, object> parameters)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT case_id,accused_id,player_id,tag_id,rescinded
FROM arrest_case_effect_state WHERE campaign_id=$campaign AND timeline_id=$timeline
AND (case_id=$case OR (accused_id=$subject AND tag_id=$tag));", parameters);
        }

        internal static void UpsertArrestCaseEffect(ReignDbConnection connection, Dictionary<string, object> parameters)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO arrest_case_effect_state
(campaign_id,timeline_id,case_id,accused_id,player_id,tag_id,rescinded)
VALUES($campaign,$timeline,$case,$subject,$player,$tag,$rescinded)
ON CONFLICT (campaign_id,timeline_id,case_id) DO UPDATE
SET rescinded=arrest_case_effect_state.rescinded OR excluded.rescinded;", parameters);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND tag_id=$tag LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectOtherArrestCaseReputations(ReignDbConnection connection,
            Dictionary<string, object> parameters)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT snapshot_json FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='arrest_accusation'
AND (subject_id<>$subject OR tag_id<>$tag);", parameters);
        }

        internal static List<Dictionary<string, object>> LockArrestRelationshipPair(ReignDbConnection connection,
            Dictionary<string, object> parameters)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a
FROM relationship_pair_chemistry WHERE pair_key=$pair FOR UPDATE;", parameters);
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
UPDATE character_reputations SET status='rescinded_by_accuser',snapshot_json=$snapshot,updated_ts=$ts
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
