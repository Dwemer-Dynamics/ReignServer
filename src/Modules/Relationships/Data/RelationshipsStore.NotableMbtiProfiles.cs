using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectNotableMbtiProfiles4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM notable_mbti_profiles WHERE hero_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static bool InsertOrIgnoreNotableMbtiProfiles(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null) =>
            ReignDatabase.QuerySql(connection, @"INSERT INTO notable_mbti_profiles(
hero_id,mbti_type,title,description,foundation_json,percentages_json,native_traits_json,source,template_version,
first_observed_day,native_sync_status,updated_ts)
VALUES($id,$type,$title,$description,$foundation,$percentages,$native,'notable_mbti_template',$version,$day,'pending',$ts)
ON CONFLICT (hero_id) DO NOTHING RETURNING hero_id;", parameters, transaction).Count == 1;

        internal static List<Dictionary<string, object>> SelectNotableMbtiProfiles5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT hero_id,native_action_id FROM notable_mbti_profiles
WHERE native_sync_status='failed';", parameters, transaction);
        }

        internal static void UpdateNotableMbtiProfiles(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE notable_mbti_profiles
SET native_sync_status='obsolete',updated_ts=$ts WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static void UpdateCharacterEditorNativeCommands(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_editor_native_commands
SET status='completed',resolved_ts=$ts,result_json=$result
WHERE command_id=$id AND status IN ('pending','claimed');", parameters, transaction);
        }

        internal static void UpdateNotableMbtiProfiles2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE notable_mbti_profiles
SET native_sync_status='obsolete',last_native_observed_day=$day,updated_ts=$ts
WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static void UpdateNotableMbtiProfiles3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE notable_mbti_profiles
SET native_sync_status='awaiting_confirmation',updated_ts=$ts WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static void UpdateNotableMbtiProfiles4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE notable_mbti_profiles SET native_sync_status='contradicted',
last_native_observed_json=$observed,last_native_observed_day=$day,updated_ts=$ts WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static void UpdateNotableMbtiProfiles5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE notable_mbti_profiles SET native_sync_status='synchronized',
last_native_observed_json=$observed,last_native_observed_day=$day,updated_ts=$ts WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static void UpdateNotableMbtiProfiles6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE notable_mbti_profiles SET native_sync_status='manual_override',
last_native_observed_json=$observed,last_native_observed_day=$day,updated_ts=$ts WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterEditorNativeCommands(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM character_editor_native_commands WHERE command_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void InsertOrIgnoreCharacterEditorNativeCommands(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO character_editor_native_commands(
command_id,revision_id,hero_id,status,changes_json,inverse_json,dangerous,confirmation,created_ts)
VALUES($id,$revision,$hero,'pending',$changes,$inverse,0,'automatic_notable_mbti_sync',$ts);", parameters, transaction);
        }

        internal static Dictionary<string, object> LockAutomaticNotableProfile(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction) =>
            ReignDatabase.QuerySql(connection, "SELECT * FROM notable_mbti_profiles WHERE hero_id=$hero FOR UPDATE;",
                parameters, transaction).SingleOrDefault();

        internal static bool ReserveStockNotableNativeStartup(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction) =>
            ReignDatabase.QuerySql(connection, @"UPDATE notable_mbti_profiles
SET startup_native_session_id=$newSession,native_sync_status='startup_direct_pending',updated_ts=$ts
WHERE hero_id=$hero AND native_action_id='' AND startup_native_session_id='' AND native_sync_status='pending'
AND source='notable_mbti_template' AND template_version=$version AND native_traits_json=$expected
RETURNING hero_id;", parameters, transaction).Count == 1;

        internal static bool ConsumeStockNotableNativeStartup(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction) =>
            ReignDatabase.QuerySql(connection, @"UPDATE notable_mbti_profiles
SET startup_native_session_id='',native_sync_status=$status,updated_ts=$ts,
last_native_observed_json=CASE WHEN $hasObservation THEN $observed ELSE last_native_observed_json END,
last_native_observed_day=CASE WHEN $hasObservation THEN $day ELSE last_native_observed_day END
WHERE hero_id=$hero AND native_action_id='' AND startup_native_session_id=$session AND native_sync_status='startup_direct_pending'
AND source='notable_mbti_template' AND template_version=$version AND native_traits_json=$expected
RETURNING hero_id;", parameters, transaction).Count == 1;

        internal static bool InsertTypedAutomaticNotableCommand(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction) =>
            ReignDatabase.QuerySql(connection, @"INSERT INTO character_editor_native_commands(
command_id,revision_id,hero_id,status,changes_json,inverse_json,dangerous,confirmation,created_ts)
VALUES($id,$revision,$hero,'pending',$changes,$inverse,0,'automatic_notable_mbti_sync',$ts)
ON CONFLICT (command_id) DO NOTHING RETURNING command_id;", parameters, transaction).Count == 1;

        internal static bool StampTypedAutomaticNotableOrigin(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction) =>
            ReignDatabase.QuerySql(connection, @"UPDATE character_editor_native_commands SET automatic_origin_json=$origin
WHERE command_id=$id AND hero_id=$hero AND revision_id=$revision AND status='pending'
AND automatic_origin_json='' AND manual_origin_json='' RETURNING command_id;", parameters, transaction).Count == 1;

        internal static bool LinkTypedAutomaticNotableCommand(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction) =>
            ReignDatabase.QuerySql(connection, @"UPDATE notable_mbti_profiles SET native_action_id=$id,native_sync_status='pending',
native_action_created_day=CASE WHEN native_action_created_day<0 THEN $day ELSE native_action_created_day END,updated_ts=$ts
WHERE hero_id=$hero AND source='notable_mbti_template' AND template_version=$version AND native_action_id=''
AND startup_native_session_id='' AND native_traits_json=$expected
AND native_sync_status IN ('pending','claimed','awaiting_confirmation')
RETURNING hero_id;", parameters, transaction).Count == 1;

        internal static bool ClearTypedAutomaticNotableCommandLink(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction) =>
            ReignDatabase.QuerySql(connection, @"UPDATE notable_mbti_profiles SET native_action_id='',native_sync_status=$profileStatus,
last_native_observed_json=CASE WHEN $success THEN $expected ELSE last_native_observed_json END,updated_ts=$ts
WHERE hero_id=$hero AND native_action_id=$id AND source='notable_mbti_template' AND template_version=$version
AND native_traits_json=$expected AND native_sync_status IN ('pending','claimed','awaiting_confirmation') RETURNING hero_id;",
                parameters, transaction).Count == 1;

        internal static void UpdateNotableMbtiProfiles7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE notable_mbti_profiles SET native_sync_status=$status,native_action_id=$action,
native_action_created_day=CASE WHEN $hasAction=1 AND native_action_created_day<0 THEN $day ELSE native_action_created_day END,
last_native_observed_json=CASE WHEN $hasObservation=1 THEN $observed ELSE last_native_observed_json END,
last_native_observed_day=CASE WHEN $hasObservation=1 THEN $day ELSE last_native_observed_day END,
updated_ts=$ts WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountNotableMbtiProfiles2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS total FROM notable_mbti_profiles;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCharacterEditorNativeCommands(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS total FROM character_editor_native_commands;", parameters, transaction);
        }

        internal static void UpdateCharacterEditorNativeCommands2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE character_editor_native_commands SET status='completed' WHERE command_id=$id;", parameters, transaction);
        }

        internal static void UpdateNotableMbtiProfiles8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE notable_mbti_profiles SET native_sync_status='synchronized' WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static void UpdateCharacterEditorNativeCommands3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE character_editor_native_commands SET status='failed' WHERE command_id=$id;", parameters, transaction);
        }

        internal static void UpdateNotableMbtiProfiles9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE notable_mbti_profiles SET native_sync_status='failed' WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectNotableMbtiProfiles6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT native_sync_status FROM notable_mbti_profiles WHERE hero_id='batch_departed_notable' LIMIT 1;", parameters, transaction);
        }
    }
}
