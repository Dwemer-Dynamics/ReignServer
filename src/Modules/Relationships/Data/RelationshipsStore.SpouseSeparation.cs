using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectLetters14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT letter_id FROM letters WHERE letter_id=$id;", parameters, transaction);
        }

        internal static void UpsertRelationshipPairLifecycle2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_lifecycle(pair_key,hero_a_id,hero_b_id,married,divorced,divorce_day,updated_ts)
VALUES($pair,$a,$b,0,1,$day,$ts) ON CONFLICT(pair_key) DO UPDATE SET married=0,divorced=1,divorce_day=$day,
lover_active=0,affair_active=0,romance_stage='',estranged_a_to_b=0,estranged_b_to_a=0,divorce_action_id='',updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCorrespondenceThreads2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT thread_id FROM correspondence_threads WHERE participant_a=$a AND participant_b=$b;", parameters, transaction);
        }

        internal static void InsertCorrespondenceThreads2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO correspondence_threads(thread_id,participant_a,participant_b,created_ts,updated_ts)
VALUES($thread,$a,$b,$ts,$ts);", parameters, transaction);
        }

        internal static void InsertLetters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO letters(letter_id,thread_id,sender_id,sender_name,recipient_id,recipient_name,body,
status,source,reason,dispatch_day,delivery_day,delivered_day,relationship_evaluated,history_written,payload_json,created_ts,updated_ts,timeline_id)
VALUES($id,$thread,$actor,$actorName,$spouse,$spouseName,$body,'delivered',$source,$operation,$day,$day,$day,1,1,$payload,$ts,$ts,$timeline);", parameters, transaction);
        }

        internal static void UpdateCorrespondenceThreads2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE correspondence_threads SET last_letter_id=$id,last_activity_day=$day,updated_ts=$ts WHERE thread_id=$thread;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE source='explicit_spouse_letter';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters;", parameters, transaction);
        }
    }
}
