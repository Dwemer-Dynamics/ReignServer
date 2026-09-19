using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<string> SelectIdentityPopularitySubjects(ReignDbConnection connection,
            List<Dictionary<string, object>> previous, List<Dictionary<string, object>> current)
        {
            // These are precisely the roster columns used by the popularity aggregate.
            // Charm, names, family and clan tier do not change incoming affinities.
            string[] dependencies = { "kingdom_id", "is_alive", "is_adult", "is_lord",
                "is_player", "is_notable", "is_wanderer", "is_mercenary_clan" };
            var oldRows = previous.ToDictionary(x => ReadString(x, "hero_id", ""), StringComparer.OrdinalIgnoreCase);
            var newRows = current.ToDictionary(x => ReadString(x, "hero_id", ""), StringComparer.OrdinalIgnoreCase);
            var changed = new HashSet<string>(oldRows.Keys.Union(newRows.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(id => !oldRows.ContainsKey(id) || !newRows.ContainsKey(id)
                    || dependencies.Any(field => !string.Equals(ReadString(oldRows[id], field, ""),
                        ReadString(newRows[id], field, ""), StringComparison.Ordinal))), StringComparer.OrdinalIgnoreCase);
            if (changed.Count == 0) return new List<string>();
            var affected = new HashSet<string>(changed, StringComparer.OrdinalIgnoreCase);
            foreach (var row in QuerySql(connection, @"WITH changed AS (
 SELECT value AS id FROM jsonb_array_elements_text(CAST($heroes AS jsonb)))
SELECT hero_b_id AS subject_id FROM relationship_pair_chemistry JOIN changed ON hero_a_id=id
UNION SELECT hero_a_id FROM relationship_pair_chemistry JOIN changed ON hero_b_id=id;",
                new Dictionary<string, object> { ["heroes"] = Json.Serialize(changed.ToList()) }))
                affected.Add(ReadString(row, "subject_id", ""));
            // Match the original full sweep's subject eligibility. A removed observer
            // still invalidates all of its remaining neighbours.
            return current.Where(x => ReadInt(x, "is_alive", 0) == 1 && ReadInt(x, "is_adult", 0) == 1
                && ReadInt(x, "is_lord", 0) == 1 && affected.Contains(ReadString(x, "hero_id", "")))
                .Select(x => ReadString(x, "hero_id", "")).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The previous roster is immutable for this preparation. Preserve its ordering:
        // the first serial upgrade wins if both endpoints changed in the same census.
        private static List<Dictionary<string, object>> PrepareHistoricalIdentityBatch(
            List<Dictionary<string, object>> previous,
            List<Dictionary<string, object>> nextHeroes)
        {
            var next = nextHeroes.ToDictionary(IdentityHeroId, x => x, StringComparer.OrdinalIgnoreCase);
            var families = previous.ToDictionary(x => ReadString(x, "hero_id", ""),
                x => new HashSet<string>(TextListFromJson(ReadString(x, "family_ids_json", "[]")),
                    StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            var rows = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Action<Dictionary<string, object>, Dictionary<string, object>> add = (observer, subject) =>
            {
                string a = ReadString(observer, "hero_id", ""), b = ReadString(subject, "hero_id", "");
                if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)
                    || a.Equals(b, StringComparison.OrdinalIgnoreCase)) return;
                string clan = ReadString(observer, "clan_id", "");
                string kingdom = ReadString(observer, "kingdom_id", "");
                bool knows = families[a].Contains(b) || families[b].Contains(a)
                    || (!string.IsNullOrWhiteSpace(clan) && clan.Equals(ReadString(subject, "clan_id", ""), StringComparison.OrdinalIgnoreCase))
                    || (ReadInt(observer, "is_lord", 0) != 0 && ReadInt(subject, "is_ruler", 0) != 0
                        && !string.IsNullOrWhiteSpace(kingdom) && kingdom.Equals(ReadString(subject, "kingdom_id", ""), StringComparison.OrdinalIgnoreCase));
                if (knows && seen.Add(Json.Serialize(new[] { a, b }))) rows.Add(new Dictionary<string, object>
                {
                    ["observer"] = a, ["subject"] = b, ["name"] = ReadString(subject, "canonical_name", "")
                });
            };
            foreach (var old in previous)
            {
                string id = ReadString(old, "hero_id", "");
                if (!next.TryGetValue(id, out var current)) continue;
                bool changed = !ReadString(old, "clan_id", "").Equals(ReadString(current, "clanId", ""), StringComparison.OrdinalIgnoreCase)
                    || !ReadString(old, "kingdom_id", "").Equals(ReadString(current, "kingdomId", ""), StringComparison.OrdinalIgnoreCase)
                    || ReadInt(old, "is_lord", 0) != (IdentityIsLord(current) ? 1 : 0)
                    || ReadInt(old, "is_ruler", 0) != (ReadBool(current, "isRuler", false) ? 1 : 0)
                    || !ReadString(old, "family_ids_json", "[]").Equals(Json.Serialize(IdentityFamilyIds(current)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()), StringComparison.Ordinal);
                if (!changed) continue;
                foreach (var other in previous) { add(old, other); add(other, old); }
            }
            return rows;
        }

        private static void WriteHistoricalIdentityBatch(ReignDbConnection connection,
            List<Dictionary<string, object>> rows, double day, long ts)
        {
            // Bounded transport; no duplicate conflict keys in a statement. Existing
            // verified rows and first_met_day have precisely the serial semantics.
            for (int offset = 0; offset < rows.Count; offset += 1000)
                ExecuteSql(connection, @"INSERT INTO acquaintances(
observer_id,subject_id,identity_state,canonical_name,verification_source,confidence,first_met_day,last_met_day,updated_ts)
SELECT r.observer,r.subject,'verified',r.name,'historical_group_identity',1,$day,$day,$ts
FROM jsonb_to_recordset(CAST($rows AS jsonb)) AS r(observer text,subject text,name text)
ON CONFLICT(observer_id,subject_id) DO UPDATE SET
identity_state='verified',
canonical_name=CASE WHEN excluded.canonical_name<>'' THEN excluded.canonical_name ELSE acquaintances.canonical_name END,
verification_source='historical_group_identity',confidence=1,last_met_day=$day,updated_ts=$ts
WHERE acquaintances.identity_state<>'verified';", new Dictionary<string, object>
                {
                    ["rows"] = Json.Serialize(rows.Skip(offset).Take(1000).ToList()), ["day"] = day, ["ts"] = ts
                });
        }
    }
}
