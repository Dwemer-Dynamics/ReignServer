using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectRelationshipInteractionReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_interaction_receipts WHERE event_id=$event AND subject_id=$subject AND target_id=$target LIMIT 1;", parameters, transaction);
        }

        internal static void InsertRelationshipInteractionReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_interaction_receipts(
event_id,subject_id,target_id,native_delta,world_day,reason,source,created_ts)
VALUES($event,$subject,$target,$delta,$day,$reason,$source,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationships3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationships WHERE subject_id=$subject AND target_id=$target LIMIT 1;", parameters, transaction);
        }

        internal static void InsertRelationships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationships(subject_id,target_id,trust,fear,respect,resentment,loyalty,debt,affection,rivalry,envy,attraction,jealousy,dependence,interest_alignment,native_relation,confidence_json,evidence_json,initialized_ts,last_eval_ts,last_updated_from_json,updated_ts,payload_json)
VALUES($subject,$target,$trust,$fear,$respect,$resentment,$loyalty,$debt,$affection,$rivalry,$envy,$attraction,$jealousy,$dependence,$interest_alignment,$native,$confidence,$evidence,$ts,0,'[]',$ts,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPressures2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pressures WHERE subject_id=$subject AND target_id=$target AND kind=$kind AND status='active' ORDER BY updated_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static void InsertRelationshipPressures2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pressures(pressure_id,subject_id,target_id,kind,status,intensity,secrecy,summary,related_entities_json,trigger_json,evidence_json,created_day,updated_day,updated_ts,payload_json)
VALUES($id,$subject,$target,$kind,'active',$intensity,$secrecy,$summary,$entities,$trigger,$evidence,$day,$day,$ts,$payload);", parameters, transaction);
        }

        internal static void UpdateRelationshipPressures(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pressures SET status=$status,intensity=$intensity,summary=$summary,updated_day=$day,updated_ts=$ts,payload_json=$payload WHERE pressure_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT kind,world_day,summary,rumor_id
FROM relationship_incidents WHERE pair_key=$pair ORDER BY world_day DESC,created_ts DESC LIMIT 12;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDevelopments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_developments WHERE status='ready' AND available_day<=$day AND (expires_day=0 OR expires_day>=$day) ORDER BY priority DESC,updated_ts ASC LIMIT 30;", parameters, transaction);
        }

        internal static void UpdateRelationshipDevelopments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_developments SET status='claimed',claimed_day=$day,updated_ts=$ts WHERE development_id=$id AND status='ready';", parameters, transaction);
        }

        internal static void UpdateRelationshipDevelopments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_developments SET status=$status,resolved_day=$day,payload_json=$payload,updated_ts=$ts WHERE development_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE sender_id=$sender AND recipient_id=$recipient AND source=$source AND reason=$reason ORDER BY created_ts DESC,letter_id;", parameters, transaction);
        }

        internal static void UpdateLetters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE letters SET status='delivered',delivery_day=$day,delivered_day=$day,updated_ts=$ts WHERE letter_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM letters
WHERE sender_id=$commander AND recipient_id=$player
  AND source='campaign_command_urgent_report'
ORDER BY dispatch_day DESC,created_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE parent_letter_id=$parent AND sender_id=$sender AND recipient_id=$recipient AND source='npc_reply' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCorrespondenceThreads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM correspondence_threads WHERE participant_a=$a AND participant_b=$b LIMIT 1;", parameters, transaction);
        }

        internal static void InsertCorrespondenceThreads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO correspondence_threads(thread_id,participant_a,participant_b,last_activity_day,created_ts,updated_ts) VALUES($id,$a,$b,$day,$ts,$ts);", parameters, transaction);
        }

        internal static void InsertLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO letters(letter_id,thread_id,sender_id,sender_name,recipient_id,recipient_name,body,status,source,reason,parent_letter_id,origin_id,destination_id,dispatch_day,delivery_day,payload_json,created_ts,updated_ts,transit_policy_version,base_transit_days)
VALUES($id,$thread,$sender,$senderName,$recipient,$recipientName,$body,'in_transit',$source,$reason,$parent,$origin,$destination,$dispatch,$delivery,$payload,$ts,$ts,$policy,$baseTransit);", parameters, transaction);
        }

        internal static void UpdateCorrespondenceThreads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE correspondence_threads SET last_letter_id=$letter,last_activity_day=$day,updated_ts=$ts WHERE thread_id=$thread;", parameters, transaction);
        }

        internal static void UpdateLetters3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE letters SET timeline_id=$timeline,dispatch_sequence=nextval('memory_accepted_turn_sequence') WHERE letter_id=$id;", parameters, transaction);
        }

        internal static void UpdateLetters4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE letters SET payload_json=$payload WHERE letter_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters12(ReignDbConnection connection, object whereValue, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT DISTINCT ON (contact_id) contact_id,
SUM(CASE WHEN recipient_id=$player AND status='delivered' THEN 1 ELSE 0 END) OVER (PARTITION BY contact_id) AS unread_count,status
FROM (SELECT sender_id,recipient_id,status,dispatch_day,created_ts,letter_id,CASE WHEN sender_id=$player THEN recipient_id ELSE sender_id END AS contact_id
FROM letters WHERE " + whereValue + ") visible ORDER BY contact_id,dispatch_day DESC,created_ts DESC,letter_id DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters13(ReignDbConnection connection, object displayColumns, object whereValue, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM (SELECT " + displayColumns + " FROM letters WHERE " + whereValue
                        + " ORDER BY dispatch_day DESC,created_ts DESC,letter_id DESC LIMIT 1000) recent ORDER BY dispatch_day ASC,created_ts ASC,letter_id ASC;", parameters, transaction);
        }

        internal static void UpdateLetters5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE letters SET status='read',read_day=$day,updated_ts=$ts WHERE letter_id=$id AND status='delivered';", parameters, transaction);
        }

        internal static void UpdateLetters6(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE letters SET status='read',read_day=$day,updated_ts=$ts WHERE recipient_id=$player AND status='delivered' AND letter_id IN ("
                        + part + ");", parameters, transaction);
        }
    }
}
