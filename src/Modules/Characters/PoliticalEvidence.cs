using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Reign.Core.Contracts.Government;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // The receipt is written inside the accepted conversation's existing transaction.
        // A generated answer or proposed deal alone never changes a political profile.
        private static Dictionary<string, object> AcceptedPoliticalReflection(AcceptedConversationUnit unit, bool technicalNotice)
        {
            var rows = ReadDictionaryList(unit.Parsed, "politicalReflection");
            int interval = ReadInt(unit.Profile, "politicalInterval", -1);
            if (technicalNotice || !unit.CommitEffects || interval < 0 || rows.Count == 0 || rows.Count > 2) return TestDict();
            var accepted = new List<object>();
            var values = new HashSet<ReignGovernmentPlank>();
            foreach (var row in rows)
            {
                string quote = ReadString(row, "evidenceQuote", ""), source = ReadString(row, "sourceQuote", "");
                int stance = ReadInt(row, "stanceChange", 0), importance = ReadInt(row, "importanceChange", 0);
                if (!Enum.TryParse(ReadString(row, "value", ""), out ReignGovernmentPlank value) || !Enum.IsDefined(typeof(ReignGovernmentPlank), value)
                    || !values.Add(value) || Math.Abs(stance) + Math.Abs(importance) != 1 || quote.Length < 12 || source.Length < 12
                    || !(unit.Reply ?? "").Contains(quote, StringComparison.Ordinal) || !(unit.PlayerText ?? "").Contains(source, StringComparison.Ordinal))
                    return TestDict();
                accepted.Add(TestDict("value", (int)value, "stance", stance, "importance", importance, "evidenceQuote", quote, "sourceQuote", source));
            }
            string identity = PromptHash(string.Join("|", rows.Select(x => ReadString(x, "sourceQuote", "").Trim().ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal)));
            return TestDict("heroId", unit.Hero, "timelineId", ContinuityTimeline(unit.Payload), "interval", interval,
                "worldDay", ReadDouble(unit.Payload, "worldDay", -1), "receiptId", "political_discussion:" + identity,
                "acceptedUnitId", unit.EventId, "changes", accepted);
        }

        private static GovernmentPoliticalProfile ApplyAcceptedPoliticalReflections(string campaign, string hero,
            GovernmentPoliticalProfile profile, double worldDay)
        {
            if (worldDay < 0) return profile;
            using var connection = OpenCampaignConnection(campaign);
            EnsureAcceptedConversationUnitSchema(connection);
            var receipts = QuerySql(connection, @"SELECT receipt_json FROM conversation_accepted_units
WHERE receipt_json::jsonb->'politicalReflection'->>'heroId'=$hero
AND receipt_json::jsonb->'politicalReflection'->>'timelineId'=$timeline
ORDER BY created_ts,unit_id;", TestDict("hero", hero, "timeline", profile.TimelineId));
            var revised = JsonSerializer.Deserialize<GovernmentPoliticalProfile>(JsonSerializer.Serialize(profile));
            foreach (var row in receipts)
            {
                var evidence = ReadDictionary(TryParseJsonObject(ReadString(row, "receipt_json", "{}")), "politicalReflection");
                if (ReadDouble(evidence, "worldDay", double.MaxValue) > worldDay) continue;
                var deltas = ReadDictionaryList(evidence, "changes").Select(x => new GovernmentPoliticalValue {
                    Value = (ReignGovernmentPlank)ReadInt(x, "value", -1), Stance = ReadInt(x, "stance", 0), Importance = ReadInt(x, "importance", 0) }).ToList();
                GovernmentPoliticalBeliefs.Evolve(revised, ReadString(evidence, "receiptId", ""), "discussion:" + ReadString(evidence, "acceptedUnitId", ""),
                    ReadInt(evidence, "interval", -1), true, true, deltas);
            }
            if (revised.Revision == profile.Revision) return profile;
            WritePoliticalRevision(campaign, hero, revised);
            return revised;
        }

        private static GovernmentPoliticalProfile ApplyPoliticalWorldEvidence(string campaign, string hero,
            GovernmentPoliticalProfile profile, List<Dictionary<string, object>> records, double worldDay)
        {
            if (records.Count == 0 || worldDay < 0) return profile;
            var revised = JsonSerializer.Deserialize<GovernmentPoliticalProfile>(JsonSerializer.Serialize(profile));
            foreach (var record in records)
            {
                var evidence = JsonSerializer.Deserialize<GovernmentPoliticalWorldEvidence>(Json.Serialize(record));
                if (evidence == null || evidence.CampaignId != campaign || evidence.WorldDay > worldDay
                    || !evidence.Applied || string.IsNullOrEmpty(evidence.TimelineId)) continue;
                var changes = GovernmentPoliticalBeliefs.WorldEventChanges(evidence, hero);
                if (changes.Count == 0) continue;
                string path = CharacterFile(campaign, hero, "politics", "event-" + SafeMemoryKey(profile.TimelineId)
                    + "-" + PromptHash(evidence.Id) + ".json");
                var prior = ReadJsonObject(path);
                if (prior.Count > 0 && CanonicalJson(prior) != CanonicalJson(record))
                    throw new InvalidOperationException("A completed political event cannot change its facts.");
                if (prior.Count == 0) WriteJsonObject(path, record);
                GovernmentPoliticalBeliefs.Evolve(revised, evidence.Id, evidence.Kind + ":" + evidence.TargetId,
                    evidence.Interval, true, false, changes);
            }
            if (revised.Revision == profile.Revision) return profile;
            WritePoliticalRevision(campaign, hero, revised);
            return revised;
        }
    }
}
