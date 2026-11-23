using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Platform module's schema statements (tables, indexes and columns) used only by its self-tests (verification build only); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class PlatformSelfTestSchema
    {
        internal static void DropGroupConversationContributions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TABLE group_conversation_contributions;", parameters, transaction);
        }

        internal static void DropGroupConversationState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TABLE group_conversation_state;", parameters, transaction);
        }
    }
}
