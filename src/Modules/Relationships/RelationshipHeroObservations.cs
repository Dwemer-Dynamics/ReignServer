using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private static void WriteRelationshipHeroObservations(ReignDbConnection connection,
            string timelineId, List<Dictionary<string, object>> rows)
        {
            if (rows.Count == 0) return;
            // Preserve a pre-upgrade predecessor before replacing the latest document.
            // Both history and latest state commit with their durable daily input.
            RelationshipsStore.SelectRelationshipObservedHeroes4(connection, new Dictionary<string, object> { ["rows"] = Json.Serialize(rows), ["timeline"] = timelineId });
        }

        private static List<Dictionary<string, object>> LoadRelationshipHeroObservations(
            ReignDbConnection connection, string timelineId, int day, IEnumerable<string> heroIds)
        {
            var ids = heroIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ids.Count == 0) return new List<Dictionary<string, object>>();
            var parameters = new Dictionary<string, object>
            { ["heroes"] = Json.Serialize(ids), ["timeline"] = timelineId, ["day"] = day };
            if (RelationshipThroughputOriginalReadPath)
                return RelationshipsStore.SelectRelationshipObservedHeroes5(connection, parameters);

            var rows = RelationshipsStore.SelectJsonbArrayElementsText(connection, parameters);
            foreach (var row in rows)
                if (row["profile_json"] == null && ReadInt(row, "last_observed_day", -1) > day)
                    throw new InvalidOperationException("Relationship input history is unavailable for hero "
                        + ReadString(row, "hero_id", "") + " on day " + day
                        + "; refusing to apply a newer profile to an older queued day.");
            return rows.Where(row => row["profile_json"] != null).ToList();
        }

        private static void PruneRelationshipHeroObservations(ReignDbConnection connection,
            string timelineId, int processedDay)
        {
            // Keep the newest predecessor plus all changes in/after the retained
            // window. A backlog's future inputs are never eligible for removal.
            RelationshipsStore.SelectStatement(connection, new Dictionary<string, object>
                { ["timeline"] = timelineId, ["cutoff"] = processedDay - Math.Max(7, RelationshipCadenceDays),
                    ["window"] = Math.Max(7, RelationshipCadenceDays) });
        }
    }
}
