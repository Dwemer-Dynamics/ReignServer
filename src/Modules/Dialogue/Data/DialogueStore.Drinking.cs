using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueStore
    {
        internal static List<Dictionary<string, object>> SelectConversationIntoxication2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT state_json FROM conversation_intoxication WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationDrinkScene(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR IGNORE INTO conversation_drink_scene(scene_id,state_json) VALUES('active','{}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationDrinkScene(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT state_json FROM conversation_drink_scene WHERE scene_id='active'" + (part), parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationDrinkScene2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT state_json FROM conversation_drink_scene WHERE scene_id='active' FOR UPDATE;", parameters, transaction);
        }

        internal static void UpdateConversationDrinkScene(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_drink_scene SET state_json=$state WHERE scene_id='active';", parameters, transaction);
        }
    }
}
