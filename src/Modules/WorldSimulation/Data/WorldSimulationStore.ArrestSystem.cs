using System;
using System.Collections.Generic;
using System.Linq;

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
            if (PersistenceService.IsCampaignMigrationReady(connection, PersistenceService.ArrestEffectReceiptMigrationVersion)) return;
            CreateArrestCaseEffectState(connection);
        }

        private static void CreateArrestCaseEffectState(ReignDbConnection connection)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS arrest_case_effect_state (
campaign_id TEXT NOT NULL, timeline_id TEXT NOT NULL, case_id TEXT NOT NULL,
accused_id TEXT NOT NULL, player_id TEXT NOT NULL, tag_id TEXT NOT NULL, rescinded BOOLEAN NOT NULL,
PRIMARY KEY (campaign_id, timeline_id, case_id),
UNIQUE (campaign_id, timeline_id, accused_id, tag_id));");
        }

        internal static void ApplyArrestEffectReceiptSchemaDdl(ReignDbConnection connection)
        {
            CreateArrestCaseEffectState(connection);
            ReignDatabase.ExecuteSql(connection, @"ALTER TABLE arrest_case_effect_state
ADD COLUMN IF NOT EXISTS material_revision BIGINT NOT NULL DEFAULT 0,
ADD COLUMN IF NOT EXISTS effect_intent_id TEXT NOT NULL DEFAULT '',
ADD COLUMN IF NOT EXISTS effect_intent_material TEXT NOT NULL DEFAULT '',
ADD COLUMN IF NOT EXISTS effect_receipt_json TEXT NOT NULL DEFAULT '{}';");
        }

        internal static bool ArrestEffectReceiptSchemaReady(ReignDbConnection connection)
        {
            var columns = ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count FROM information_schema.columns
WHERE table_schema=current_schema() AND table_name='arrest_case_effect_state'
AND column_name IN ('material_revision','effect_intent_id','effect_intent_material','effect_receipt_json');");
            return ReadInt(columns[0], "count", 0) == 4;
        }

        internal static void ApplyArrestLateEvidenceReceiptSchemaDdl(ReignDbConnection connection)
        {
            ReignDatabase.ExecuteSql(connection, @"ALTER TABLE arrest_case_effect_state
ADD COLUMN IF NOT EXISTS late_discovery_id TEXT NOT NULL DEFAULT '',
ADD COLUMN IF NOT EXISTS late_effect_intent_id TEXT NOT NULL DEFAULT '',
ADD COLUMN IF NOT EXISTS late_effect_material TEXT NOT NULL DEFAULT '',
ADD COLUMN IF NOT EXISTS late_effect_receipt_json TEXT NOT NULL DEFAULT '{}';");
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_owned_justice_admissions (
event_id TEXT PRIMARY KEY REFERENCES world_history_events(event_id) ON DELETE CASCADE,
previous_sequence BIGINT NOT NULL CHECK(previous_sequence>=0), previous_event_id TEXT NOT NULL,
settles_discovery_id TEXT UNIQUE);");
        }

        internal static Dictionary<string, object> SelectArrestLateEvidenceReceipt(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT material_revision,rescinded,late_discovery_id,
late_effect_intent_id,late_effect_material,late_effect_receipt_json
FROM arrest_case_effect_state WHERE campaign_id=$campaign AND timeline_id=$timeline
AND case_id=$case AND accused_id=$subject AND player_id=$player AND tag_id=$tag
AND late_discovery_id<>'' FOR UPDATE;", parameters, transaction).SingleOrDefault();
        }

        internal static bool WriteArrestLateEvidenceReceipt(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"UPDATE arrest_case_effect_state
SET late_discovery_id=$discovery,late_effect_intent_id=$intent,late_effect_material=$material,
late_effect_receipt_json=$lateReceipt
WHERE campaign_id=$campaign AND timeline_id=$timeline AND case_id=$case
AND accused_id=$subject AND player_id=$player AND tag_id=$tag AND late_discovery_id=''
RETURNING case_id;", parameters, transaction).Count == 1;
        }

        internal static List<Dictionary<string, object>> SelectArrestCaseEffects(ReignDbConnection connection,
            Dictionary<string, object> parameters, bool includeReceipt = false, ReignDbTransaction transaction = null,
            bool lockRows = false)
        {
            string receiptColumns = includeReceipt ? ",material_revision,effect_intent_id,effect_intent_material,effect_receipt_json" : "";
            string rowLock = lockRows ? " FOR UPDATE" : "";
            return ReignDatabase.QuerySql(connection, @"SELECT case_id,accused_id,player_id,tag_id,rescinded" + receiptColumns + @"
FROM arrest_case_effect_state WHERE campaign_id=$campaign AND timeline_id=$timeline
AND (case_id=$case OR (accused_id=$subject AND tag_id=$tag))" + rowLock + ";", parameters, transaction);
        }

        internal static void UpsertArrestCaseEffect(ReignDbConnection connection, Dictionary<string, object> parameters)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO arrest_case_effect_state
(campaign_id,timeline_id,case_id,accused_id,player_id,tag_id,rescinded)
VALUES($campaign,$timeline,$case,$subject,$player,$tag,$rescinded)
ON CONFLICT (campaign_id,timeline_id,case_id) DO UPDATE
SET rescinded=arrest_case_effect_state.rescinded OR excluded.rescinded;", parameters);
        }

        internal static void UpsertVersionedArrestCaseEffect(ReignDbConnection connection, Dictionary<string, object> parameters)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO arrest_case_effect_state
(campaign_id,timeline_id,case_id,accused_id,player_id,tag_id,rescinded,material_revision,
 effect_intent_id,effect_intent_material,effect_receipt_json)
VALUES($campaign,$timeline,$case,$subject,$player,$tag,$rescinded,$revision,$intent,$material,$receipt)
ON CONFLICT (campaign_id,timeline_id,case_id) DO UPDATE
SET rescinded=arrest_case_effect_state.rescinded OR excluded.rescinded,
material_revision=excluded.material_revision,effect_intent_id=excluded.effect_intent_id,
effect_intent_material=excluded.effect_intent_material,effect_receipt_json=excluded.effect_receipt_json;", parameters);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations(ReignDbConnection connection,
            Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null, bool lockRows = false)
        {
            string rowLock = lockRows ? " FOR UPDATE" : "";
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND tag_id=$tag LIMIT 1" + rowLock + ";", parameters, transaction);
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
