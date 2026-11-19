using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectLetters6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM letters WHERE status='in_transit' AND delivery_day<=$day
ORDER BY delivery_day,letter_id LIMIT 100 FOR UPDATE SKIP LOCKED;", parameters, transaction);
        }

        internal static void UpdateLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE letters SET status=$status,delivered_day=$day,read_day=$read,
delivery_timeline_id=$timeline,updated_ts=$ts WHERE letter_id=$id AND status='in_transit';", parameters, transaction);
        }

        internal static void InsertOrIgnoreCorrespondenceDeliveryJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO correspondence_delivery_jobs
(letter_id,player_id,timeline_id,restore_generation,source_hash,status,result_json,created_ts,updated_ts)
VALUES($id,$player,$timeline,$generation,$hash,$status,$result,$now,$now) ON CONFLICT(letter_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryRestoredLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT content_hash FROM memory_restored_letters
WHERE letter_id=$id AND restore_generation=$generation;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCorrespondenceDeliveryJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM correspondence_delivery_jobs WHERE letter_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", parameters, transaction);
        }

        internal static void UpdateCorrespondenceDeliveryJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE correspondence_delivery_jobs SET lease_until=$until
WHERE letter_id=$id AND status='running' AND lease_token=$token AND restore_generation=$generation;", parameters, transaction);
        }

        internal static void UpdateCorrespondenceDeliveryJobs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE correspondence_delivery_jobs SET status='incomplete',lease_token='',lease_until=0,
last_error='Reply attempt limit reached after interrupted work.',updated_ts=$now
WHERE player_id=$player AND timeline_id=$timeline AND restore_generation=$generation
AND attempt_count>=2 AND (status IN ('pending','failed') OR (status='running' AND lease_until<=$now));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> UpdateCorrespondenceDeliveryJobs3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"UPDATE correspondence_delivery_jobs SET status='running',
attempt_count=attempt_count+1,lease_token=$token,lease_until=$until,updated_ts=$now
WHERE letter_id=(SELECT letter_id FROM correspondence_delivery_jobs WHERE player_id=$player AND timeline_id=$timeline
AND restore_generation=$generation AND attempt_count<2 AND next_attempt_ts<=$now
AND (status IN ('pending','failed') OR (status='running' AND lease_until<=$now))
ORDER BY created_ts,letter_id LIMIT 1 FOR UPDATE SKIP LOCKED) RETURNING *;", parameters, transaction);
        }

        internal static void UpdateCorrespondenceDeliveryJobs4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE correspondence_delivery_jobs SET reply_json=$reply WHERE letter_id=$id AND lease_token=$token;", parameters, transaction);
        }

        internal static void UpdateCorrespondenceDeliveryJobs5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE correspondence_delivery_jobs SET status='completed',result_json=$result,
last_error='',lease_token='',lease_until=0,updated_ts=$now WHERE letter_id=$id AND lease_token=$token;", parameters, transaction);
        }

        internal static void UpdateCorrespondenceDeliveryJobs6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE correspondence_delivery_jobs SET status=$status,last_error=$error,
lease_token='',lease_until=0,next_attempt_ts=$next,updated_ts=$now WHERE letter_id=$id AND lease_token=$token;", parameters, transaction);
        }

        internal static void UpdateCorrespondenceDeliveryJobs7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE correspondence_delivery_jobs SET acknowledged=1 WHERE letter_id=$id
AND player_id=$player AND timeline_id=$timeline AND restore_generation=$generation AND status='completed';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCorrespondenceDeliveryJobs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM correspondence_delivery_jobs WHERE player_id=$player AND timeline_id=$timeline
AND restore_generation=$generation AND status='completed' AND acknowledged=0 ORDER BY created_ts,letter_id LIMIT 100;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCorrespondenceDeliveryJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) count FROM correspondence_delivery_jobs
WHERE player_id=$player AND timeline_id=$timeline AND restore_generation=$generation AND status IN ('pending','running','failed');", parameters, transaction);
        }

        internal static void UpdateCorrespondenceDeliveryJobs8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE correspondence_delivery_jobs SET timeline_id=$timeline,restore_generation=$generation,
status=CASE WHEN status='running' AND attempt_count>=2 THEN 'incomplete' WHEN status='running' THEN 'pending' ELSE status END,lease_token='',lease_until=0;", parameters, transaction);
        }
    }
}
