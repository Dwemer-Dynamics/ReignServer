using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Bannerlord stores one symmetric native-relation value. Reign is
        // directional, so NPC/player pairs deliberately project only the NPC's
        // underlying outlook toward the player. This prevents the player's
        // opinion and public standing from feeding back into how the NPC feels.
        private static int ProjectNativeRelation(
            ReignDbConnection connection,
            string heroAId,
            string heroBId,
            int affinityAToB,
            int affinityBToA,
            int effectiveAToB,
            int effectiveBToA)
        {
            HashSet<string> playerIds = LoadNativeRelationPlayerHeroIds(connection);
            return ProjectNativeRelation(heroAId, heroBId, affinityAToB, affinityBToA,
                effectiveAToB, effectiveBToA, playerIds);
        }

        private static int ProjectNativeRelation(
            string heroAId,
            string heroBId,
            int affinityAToB,
            int affinityBToA,
            int effectiveAToB,
            int effectiveBToA,
            ISet<string> playerIds)
        {
            bool aIsPlayer = playerIds != null && playerIds.Contains(heroAId ?? string.Empty);
            bool bIsPlayer = playerIds != null && playerIds.Contains(heroBId ?? string.Empty);
            if (aIsPlayer && !bIsPlayer) return Clamp(affinityBToA, -100, 100);
            if (bIsPlayer && !aIsPlayer) return Clamp(affinityAToB, -100, 100);
            return Clamp(RoundAwayFromZero((effectiveAToB + effectiveBToA) / 2d), -100, 100);
        }

        private static HashSet<string> LoadNativeRelationPlayerHeroIds(ReignDbConnection connection)
        {
            RelationshipStandingContext context = RelationshipStandingContext.For(connection);
            if (context != null) return context.PlayerIds();
            if (connection == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>(QuerySql(connection,
                    "SELECT hero_id FROM identity_roster WHERE is_player=1 AND hero_id<>'';")
                .Select(row => ReadString(row, "hero_id", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
        }

        private static List<Dictionary<string, object>> RunNativeRelationProjectionSelfTests()
        {
            HashSet<string> player = new HashSet<string>(new[] { "player" },
                StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool> add = (name, passed) => results.Add(new Dictionary<string, object>
            {
                ["name"] = "native_relation_projection_" + name,
                ["passed"] = passed,
                ["detail"] = "NPC/player native relation follows only the NPC's underlying directional outlook; NPC/NPC remains the rounded effective average."
            });
            add("npc_a_player_b", ProjectNativeRelation("npc", "player", 37, -81,
                92, -100, player) == 37);
            add("player_a_npc_b", ProjectNativeRelation("player", "npc", -74, -31,
                -100, 44, player) == -31);
            add("ignores_public_standing", ProjectNativeRelation("npc", "player", -40, 95,
                60, 100, player) == -40);
            add("npc_npc_average", ProjectNativeRelation("npc_a", "npc_b", 0, 0,
                10, 15, player) == 13);
            return results;
        }
    }
}
