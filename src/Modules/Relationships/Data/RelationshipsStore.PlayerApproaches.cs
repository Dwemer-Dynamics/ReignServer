using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectPlayerApproachState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value_json FROM player_approach_state WHERE timeline_id=$timeline AND state_key=$key;", parameters, transaction);
        }

        internal static void UpsertPlayerApproachState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO player_approach_state(timeline_id,state_key,value_json) VALUES($timeline,$key,$value)
ON CONFLICT(timeline_id,state_key) DO UPDATE SET value_json=EXCLUDED.value_json;", parameters, transaction);
        }

        internal static void InsertOrIgnorePlayerAttentionEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO player_attention_events(timeline_id,player_id,observer_id,target_id,calendar_day,target_name,location_id,exchange_id,source_id,event_text,created_ts)
VALUES($timeline,$player,$observer,$target,$day,$name,$location,$exchange,$source,$text,$ts) ON CONFLICT DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPlayerAttentionEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COALESCE(MAX(sequence_id),0) AS sequence FROM player_attention_events;", parameters, transaction);
        }

        internal static void Begin2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "BEGIN;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry18(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPlayerAttentionEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT target_id,MAX(target_name) AS target_name,COUNT(*) AS days,
MIN(calendar_day) AS first_day,MAX(calendar_day) AS last_day,
array_to_json((array_agg(source_id ORDER BY sequence_id DESC))[1:64])::text AS source_ids_json FROM player_attention_events
WHERE timeline_id=$timeline AND player_id=$player AND observer_id=$hero AND sequence_id>$sequence AND exchange_id<>$exchange
GROUP BY target_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPlayerAttentionEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_text,source_id,calendar_day,created_ts FROM player_attention_events
WHERE timeline_id=$timeline AND observer_id=$hero AND player_id=$player ORDER BY sequence_id DESC LIMIT 80;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPlayerAttentionEvents4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_text,source_id FROM player_attention_events WHERE timeline_id=$timeline
AND observer_id=$hero AND player_id=$player ORDER BY calendar_day DESC,sequence_id DESC LIMIT 64;", parameters, transaction);
        }
    }
}
