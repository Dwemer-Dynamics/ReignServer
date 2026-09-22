using System;
using System.Collections.Generic;
using System.Linq;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> PatronagePublicHistoryApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaign = query.ContainsKey("campaignId") ? query["campaignId"] : "";
            string timeline = query.ContainsKey("timelineId") ? query["timelineId"] : "";
            string kingdom = query.ContainsKey("kingdomId") ? query["kingdomId"] : "";
            double day = ParseDoubleInvariant(query.ContainsKey("worldDay") ? query["worldDay"] : "", -1);
            if (string.IsNullOrWhiteSpace(campaign) || string.IsNullOrWhiteSpace(timeline) || string.IsNullOrWhiteSpace(kingdom)
                || day < 0 || double.IsNaN(day) || double.IsInfinity(day))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "An exact campaign, timeline, kingdom and finite world day are required." };
            var subjects = new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaign))
            {
                EnsureWorldHistorySchema(connection);
                // Knowledge rules are the authority. Private participant evidence and premature realm news never qualify.
                var rows = QuerySql(connection, @"SELECT e.event_id,e.event_type,e.summary,e.world_day FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.is_complete=1 AND e.phase='completed' AND e.source='bannerlord_native'
AND e.world_day<=$day AND e.event_type IN ('battle_completed','hero_prisoner_released','hero_killed','settlement_owner_changed','peace_made')
AND EXISTS(SELECT 1 FROM world_history_knowledge_rules k WHERE k.event_id=e.event_id AND k.available_day<=$day
 AND (k.audience_type='global' OR (k.audience_type='kingdom' AND k.audience_id=$kingdom)))
AND EXISTS(SELECT 1 FROM world_history_entities n WHERE n.event_id=e.event_id AND (n.kingdom_id=$kingdom OR (n.entity_type='kingdom' AND n.entity_id=$kingdom)))
ORDER BY e.sequence DESC LIMIT 200;", new Dictionary<string, object> { ["timeline"] = timeline, ["day"] = day, ["kingdom"] = kingdom });
                foreach (var row in rows)
                {
                    string id = ReadString(row, "event_id", "");
                    var own = QueryWorldHistoryEntities(connection, id).Where(x => ReadString(x, "kingdom_id", "") == kingdom).ToList();
                    Func<string, bool> role = word => own.Any(x => ReadString(x, "role", "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
                    string context = ReignPatronageRules.PublicNativeHistoryContext(ReadString(row, "event_type", ""),
                        role("winner"), role("released_prisoner"), role("killed victim"), role("new owner"));
                    if (context.Length == 0 || subjects.Count(x => ReadString(x, "context", "") == context) >= 8) continue;
                    string summary = LimitText(ReadString(row, "summary", ""), 900);
                    if (context == "reconciliation") summary += " This confirms a public peace; it does not establish private friendship or forgiveness.";
                    if (context == "settlement") summary += " This confirms the named ownership transfer; do not invent its cause or an earlier conquest.";
                    subjects.Add(new Dictionary<string, object> { ["eventId"] = id, ["eventType"] = ReadString(row, "event_type", ""),
                        ["context"] = context, ["summary"] = summary, ["worldDay"] = ReadDouble(row, "world_day", 0) });
                }
            }
            return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaign, ["timelineId"] = timeline,
                ["kingdomId"] = kingdom, ["worldDay"] = day, ["subjects"] = subjects, ["schema"] = "reign-patronage-public-history-v1" };
        }
    }
}
