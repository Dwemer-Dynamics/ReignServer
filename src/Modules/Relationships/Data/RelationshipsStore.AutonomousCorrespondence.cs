using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a
FROM relationship_pair_chemistry
WHERE (hero_a_id=$player AND affinity_b_to_a>=10)
   OR (hero_b_id=$player AND affinity_a_to_b>=10);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT dispatch_day FROM letters
WHERE source='npc_introduction' AND reason LIKE $prefix
ORDER BY dispatch_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT status FROM letters
WHERE sender_id=$sender AND recipient_id=$player AND source='npc_introduction'
ORDER BY dispatch_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAutonomousCorrespondenceCooldowns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT next_eligible_day FROM autonomous_correspondence_cooldowns
WHERE sender_id=$sender LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAutonomousCorrespondenceChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM autonomous_correspondence_checks
WHERE timeline_id=$timeline AND slot_day=$slot AND sender_id=$sender LIMIT 1;", parameters, transaction);
        }

        internal static void InsertAutonomousCorrespondenceChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO autonomous_correspondence_checks
(timeline_id,slot_day,sender_id,chance,roll,status,updated_ts)
VALUES($timeline,$slot,$sender,$chance,$roll,$status,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT letter_id FROM letters
WHERE sender_id=$sender AND recipient_id=$player AND source=$source AND reason=$reason LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateAutonomousCorrespondenceChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE autonomous_correspondence_checks
SET status='generation_failed',updated_ts=$ts WHERE timeline_id=$timeline AND slot_day=$slot AND sender_id=$sender;", parameters, transaction);
        }

        internal static void UpsertAutonomousCorrespondenceCooldowns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO autonomous_correspondence_cooldowns
(sender_id,last_sent_day,next_eligible_day,cooldown_days,letter_id,updated_ts)
VALUES($sender,$day,$next,$days,$letter,$ts)
ON CONFLICT(sender_id) DO UPDATE SET last_sent_day=excluded.last_sent_day,
next_eligible_day=excluded.next_eligible_day,cooldown_days=excluded.cooldown_days,
letter_id=excluded.letter_id,updated_ts=excluded.updated_ts;", parameters, transaction);
        }

        internal static void UpdateAutonomousCorrespondenceChecks2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE autonomous_correspondence_checks
SET status='sent',letter_id=$letter,updated_ts=$ts
WHERE timeline_id=$timeline AND slot_day=$slot AND sender_id=$sender;", parameters, transaction);
        }

        internal static void InsertRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry
(pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,first_day,last_day,updated_ts)
VALUES($key,$sender,'player', $affinity,0,1,12,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT letter_id FROM letters WHERE sender_id=$sender;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAutonomousCorrespondenceChecks2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM autonomous_correspondence_checks;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAutonomousCorrespondenceCooldowns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM autonomous_correspondence_cooldowns;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters ORDER BY source, sender_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAutonomousCorrespondenceChecks3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM autonomous_correspondence_checks
WHERE sender_id='unmet_low';", parameters, transaction);
        }
    }
}
