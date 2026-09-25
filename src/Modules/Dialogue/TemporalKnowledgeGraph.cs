using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void EnsureTemporalKnowledgeGraphSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS temporal_knowledge_nodes (
node_id TEXT PRIMARY KEY,
node_type TEXT NOT NULL,
label TEXT NOT NULL DEFAULT '',
payload_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,
updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS temporal_knowledge_assertions (
assertion_id TEXT PRIMARY KEY,
campaign_id TEXT NOT NULL,
fact_key TEXT NOT NULL,
subject_id TEXT NOT NULL DEFAULT '',
predicate TEXT NOT NULL DEFAULT '',
object_id TEXT NOT NULL DEFAULT '',
claim TEXT NOT NULL DEFAULT '',
perspective_owner_id TEXT NOT NULL DEFAULT '',
assertion_kind TEXT NOT NULL DEFAULT 'belief',
confidence REAL NOT NULL DEFAULT 0.5,
truth_status TEXT NOT NULL DEFAULT 'believed',
valid_from_ts INTEGER NOT NULL,
valid_to_ts INTEGER NULL,
observed_ts INTEGER NOT NULL,
supersedes_assertion_id TEXT NOT NULL DEFAULT '',
source_event_id TEXT NOT NULL DEFAULT '',
source_record_id TEXT NOT NULL DEFAULT '',
known_by_json TEXT NOT NULL DEFAULT '[]',
hidden_from_json TEXT NOT NULL DEFAULT '[]',
visibility TEXT NOT NULL DEFAULT 'private',
payload_json TEXT NOT NULL DEFAULT '{}');" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_temporal_assertions_current ON temporal_knowledge_assertions(campaign_id,perspective_owner_id,fact_key,valid_to_ts,observed_ts);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_temporal_assertions_subject ON temporal_knowledge_assertions(subject_id,predicate,object_id,valid_to_ts);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS temporal_knowledge_edges (
edge_id TEXT PRIMARY KEY,
from_node_id TEXT NOT NULL,
to_node_id TEXT NOT NULL,
edge_type TEXT NOT NULL,
assertion_id TEXT NOT NULL,
valid_from_ts INTEGER NOT NULL,
valid_to_ts INTEGER NULL,
payload_json TEXT NOT NULL DEFAULT '{}');" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_temporal_edges_nodes ON temporal_knowledge_edges(from_node_id,to_node_id,valid_to_ts);");
        }

        private static string UpsertTemporalAssertion(ReignDbConnection connection, string campaignId, string sourceEventId,
            string sourceRecordId, string kind, Dictionary<string, object> item, long ts)
        {
            item = item ?? new Dictionary<string, object>();
            string claim = ReadFirstString(item, "claim", "text", "belief", "summary", "description", "interpretation");
            string subject = ReadFirstString(item, "subjectId", "subject_id", "subject", "aboutHeroId", "heroStringId");
            string predicate = NormalizeLookup(ReadFirstString(item, "predicate", "relation", "property", "factType", "type"));
            string objectId = ReadFirstString(item, "objectId", "object_id", "object", "targetId", "target_id");
            string owner = ReadFirstString(item, "believer", "believerId", "believer_id", "ownerId", "owner_id", "heroStringId");
            string explicitFactKey = ReadFirstString(item, "factKey", "fact_key", "propositionId", "proposition_id");
            if (string.IsNullOrWhiteSpace(claim) && string.IsNullOrWhiteSpace(subject)) return "";
            AcquirePostgreSqlTransactionMutationLock(connection);

            string timeline = ContinuityTimeline(item);
            string identity = StableMemoryFactIdentity(item, subject, predicate, objectId, claim);
            string factKey = "fact_" + PromptHash(identity).Substring(0, 24);
            Dictionary<string, object> previous = QuerySql(connection, @"SELECT * FROM temporal_knowledge_assertions
WHERE campaign_id=$campaign AND timeline_id=$timeline AND perspective_owner_id=$owner AND fact_key=$fact AND valid_to_ts IS NULL
ORDER BY revision DESC LIMIT 1 FOR UPDATE;", new Dictionary<string, object>
            {
                ["campaign"] = campaignId ?? "", ["owner"] = owner ?? "", ["fact"] = factKey, ["timeline"] = timeline
            }).FirstOrDefault();
            string previousId = previous == null ? "" : ReadString(previous, "assertion_id", "");
            double confidence = ClampDouble(ReadDouble(item, "confidence", 0.5d), 0d, 1d);
            if (item.ContainsKey("expectedRevision") && ReadInt(item, "expectedRevision", 0) != ReadInt(previous, "revision", 0))
                throw new InvalidOperationException("Memory assertion revision changed.");
            string acceptedText = ReadString(item, "sourceAcceptedText", "");
            if (!IsPrivateMentalLayer(kind))
            {
                string claimedKind = ReadString(item,"assertionKind","reported");
                kind = new[] { "reported", "proposed", "committed" }.Contains(claimedKind) ? claimedKind : "reported";
            }
            string evidenceQuote = ReadString(item, "evidenceQuote", "");
            if (evidenceQuote.Length > 0 && (acceptedText.Length == 0 || !acceptedText.Contains(evidenceQuote, StringComparison.Ordinal)))
                throw new InvalidOperationException("Memory assertion quote is not present in its accepted source.");
            if (previous != null && string.Equals(NormalizeLookup(ReadString(previous, "claim", "")), NormalizeLookup(claim), StringComparison.Ordinal)
                && Math.Abs(ReadDouble(previous, "confidence", 0d) - confidence) < 0.02d)
            {
                // Repeating a recalled claim does not move its original learning
                // time or discard the evidence supporting the prior version.
                return previousId;
            }

            if (!string.IsNullOrWhiteSpace(previousId))
                ExecuteSql(connection, "UPDATE temporal_knowledge_assertions SET valid_to_ts=$ts,valid_to_day=$day WHERE assertion_id=$id AND valid_to_ts IS NULL;",
                    new Dictionary<string, object> { ["ts"] = ts, ["id"] = previousId, ["day"] = ReadDouble(item, "worldDay", 0d) });

            string assertionId = "assert_" + Guid.NewGuid().ToString("N");
            List<string> knownBy = MergeStringLists(ReadStringList(item, "known_by"), ReadStringList(item, "knownBy"));
            if (knownBy.Count == 0 && !string.IsNullOrWhiteSpace(owner)) knownBy.Add(owner);
            List<string> hiddenFrom = MergeStringLists(ReadStringList(item, "hidden_from"), ReadStringList(item, "hiddenFrom"));
            string truthStatus = IsPrivateMentalLayer(kind)
                ? (kind == "belief" ? "believed" : "interpretation")
                : kind; // Dialogue cannot self-certify a native effect or objective truth.
            if (IsPrivateMentalLayer(kind)) knownBy = new List<string> { owner };
            ExecuteSql(connection, @"INSERT INTO temporal_knowledge_assertions
(assertion_id,campaign_id,fact_key,subject_id,predicate,object_id,claim,perspective_owner_id,assertion_kind,confidence,truth_status,
valid_from_ts,valid_to_ts,observed_ts,supersedes_assertion_id,source_event_id,source_record_id,known_by_json,hidden_from_json,visibility,payload_json,
timeline_id,revision,source_hash,event_day,learned_day,recorded_ts,cardinality,processing_version)
VALUES($id,$campaign,$fact,$subject,$predicate,$object,$claim,$owner,$kind,$confidence,$truth,$ts,NULL,$ts,$supersedes,$event,$record,$known,$hidden,$visibility,$payload,
$timeline,$revision,$hash,$day,$learned,$ts,$cardinality,$version);",
                new Dictionary<string, object>
                {
                    ["id"] = assertionId, ["campaign"] = campaignId ?? "", ["fact"] = factKey,
                    ["subject"] = subject ?? "", ["predicate"] = predicate ?? "", ["object"] = objectId ?? "",
                    ["claim"] = claim ?? "", ["owner"] = owner ?? "", ["kind"] = kind ?? "belief",
                    ["confidence"] = confidence, ["truth"] = truthStatus, ["ts"] = ts, ["supersedes"] = previousId,
                    ["event"] = sourceEventId ?? "", ["record"] = sourceRecordId ?? "", ["known"] = Json.Serialize(knownBy),
                    ["hidden"] = Json.Serialize(hiddenFrom), ["visibility"] = ReadString(item, "visibility", "private"),
                    ["payload"] = Json.Serialize(item), ["timeline"] = timeline, ["revision"] = ReadInt(previous, "revision", 0) + 1,
                    ["hash"] = acceptedText.Length == 0 ? "" : PromptHash(acceptedText),
                    ["day"] = ReadDouble(item, "eventWorldDay", ReadDouble(item, "worldDay", 0d)),
                    ["learned"] = ReadDouble(item, "worldDay", 0d), ["cardinality"] = ReadString(item, "cardinality", "many"),
                    ["version"] = acceptedText.Length == 0 ? "legacy_unverified" : MemoryPrecisionVersion
                });
            LinkMemorySource(connection, "assertion", assertionId, "event", sourceEventId ?? "", 0);
            if (!string.IsNullOrWhiteSpace(sourceRecordId)) LinkMemorySource(connection, "assertion", assertionId, kind, sourceRecordId, 0);

            EnsureTemporalNode(connection, assertionId, "assertion", FirstNonEmpty(claim, predicate), item, ts);
            if (!string.IsNullOrWhiteSpace(subject))
            {
                EnsureTemporalNode(connection, subject, "entity", subject, null, ts);
                InsertTemporalEdge(connection, subject, assertionId, "subject_of", assertionId, ts);
            }
            if (!string.IsNullOrWhiteSpace(objectId))
            {
                EnsureTemporalNode(connection, objectId, "entity", objectId, null, ts);
                InsertTemporalEdge(connection, assertionId, objectId, "object_of", assertionId, ts);
            }
            if (!string.IsNullOrWhiteSpace(previousId))
                InsertTemporalEdge(connection, assertionId, previousId, "supersedes", assertionId, ts);
            return assertionId;
        }

        private static void EnsureTemporalNode(ReignDbConnection connection, string id, string type, string label, Dictionary<string, object> payload, long ts)
        {
            ExecuteSql(connection, @"INSERT INTO temporal_knowledge_nodes(node_id,node_type,label,payload_json,created_ts,updated_ts)
VALUES($id,$type,$label,$payload,$ts,$ts) ON CONFLICT(node_id) DO UPDATE SET label=$label,updated_ts=$ts;",
                new Dictionary<string, object> { ["id"] = id, ["type"] = type, ["label"] = label ?? "", ["payload"] = Json.Serialize(payload ?? new Dictionary<string, object>()), ["ts"] = ts });
        }

        private static void InsertTemporalEdge(ReignDbConnection connection, string from, string to, string type, string assertionId, long ts)
        {
            string id = "edge_" + PromptHash(from + "|" + to + "|" + type + "|" + assertionId).Substring(0, 24);
            ExecuteSql(connection, @"INSERT OR IGNORE INTO temporal_knowledge_edges(edge_id,from_node_id,to_node_id,edge_type,assertion_id,valid_from_ts,valid_to_ts,payload_json)
VALUES($id,$from,$to,$type,$assertion,$ts,NULL,'{}');", new Dictionary<string, object>
            { ["id"] = id, ["from"] = from, ["to"] = to, ["type"] = type, ["assertion"] = assertionId, ["ts"] = ts });
        }

        private static List<Dictionary<string, object>> LoadTemporalKnowledgeForPrompt(ReignDbConnection connection, string campaignId,
            KnowledgeAccessContext knowledge, List<string> queryTerms, int limit)
        {
            var result = new List<Dictionary<string,object>>();
            string npc=knowledge?.NpcId ?? "";
            const int batch=128;
            for(int offset=0;result.Count<Math.Max(1,limit);offset+=batch)
            {
            var rows = QuerySql(connection, @"SELECT * FROM temporal_knowledge_assertions
WHERE campaign_id=$campaign AND ($timeline='' OR timeline_id=$timeline)
AND event_day<=$day AND learned_day<=$day AND (valid_to_day IS NULL OR valid_to_day>$day)
AND (valid_to_ts IS NULL OR valid_to_day IS NOT NULL)
AND (assertion_kind NOT IN ('belief','comprehension','interpretation') OR perspective_owner_id=$observer)
AND NOT EXISTS (SELECT 1 FROM jsonb_array_elements_text(hidden_from_json::jsonb) h(value) WHERE lower(h.value)=lower($observer))
AND (perspective_owner_id=$observer OR lower(visibility) IN ('world','global','common','public','local','nearby','kingdom','clan','settlement','party','army') OR lower(visibility) LIKE 'public_%' OR EXISTS (SELECT 1 FROM jsonb_array_elements_text(known_by_json::jsonb) k(value) WHERE lower(k.value)=lower($observer)))
AND ($query='' OR to_tsvector('simple',claim || ' ' || subject_id || ' ' || predicate || ' ' || object_id) @@ websearch_to_tsquery('simple',$query))
ORDER BY ts_rank(to_tsvector('simple',claim),websearch_to_tsquery('simple',$query)) DESC,learned_day DESC,revision DESC,assertion_id LIMIT $batch OFFSET $offset;",
                new Dictionary<string, object> { ["campaign"] = campaignId ?? "", ["observer"] = knowledge == null ? "" : knowledge.NpcId,
                    ["timeline"] = knowledge?.TimelineId ?? "", ["day"] = knowledge?.WorldDay ?? double.MaxValue,
                    ["query"] = BuildFtsQuery(queryTerms ?? new List<string>()), ["batch"] = batch, ["offset"] = offset });
            result.AddRange(rows.Where(row =>
                {
                    if (KnowledgeListContains(row, "hidden_from_json", npc)) return false;
                    if (IsPrivateMentalLayer(ReadString(row, "assertion_kind", ""))
                        && !KnowledgeIdEquals(ReadString(row, "perspective_owner_id", ""), npc)) return false;
                    bool visible = KnowledgeVisibilityAllows(row, knowledge ?? new KnowledgeAccessContext())
                        || KnowledgeIdEquals(ReadString(row, "perspective_owner_id", ""), npc)
                        || KnowledgeListContains(row, "known_by_json", npc);
                    if (!visible) return false;
                    if (queryTerms == null || queryTerms.Count == 0) return true;
                    string haystack = NormalizeLookup(ReadString(row, "claim", "") + " " + ReadString(row, "subject_id", "") + " "
                        + ReadString(row, "predicate", "") + " " + ReadString(row, "object_id", ""));
                    return queryTerms.Any(term => haystack.Contains(NormalizeLookup(term)));
                }).Take(Math.Max(1,limit)-result.Count));
            if(rows.Count<batch) break;
            }
            return result;
        }

        private static string FormatTemporalKnowledgeForPrompt(List<Dictionary<string, object>> rows)
        {
            if (rows == null || rows.Count == 0) return "";
            StringBuilder text = new StringBuilder("Current time-versioned knowledge (respect perspective and confidence):\n");
            foreach (Dictionary<string, object> row in rows)
            {
                string claim = FirstNonEmpty(ReadString(row, "claim", ""),
                    ReadString(row, "subject_id", "") + " " + ReadString(row, "predicate", "") + " " + ReadString(row, "object_id", ""));
                string kind = ReadString(row, "assertion_kind", "");
                string status = IsPrivateMentalLayer(kind) ? (kind == "belief" ? "believed" : "interpretation")
                    : ReadString(row, "truth_status", "asserted");
                text.Append("- [").Append(status).Append(", owner=")
                    .Append(ReadString(row, "perspective_owner_id", "unknown")).Append(", confidence ")
                    .Append(ReadDouble(row, "confidence", 0.5d).ToString("0.00", CultureInfo.InvariantCulture)).Append("] ")
                    .Append(claim).Append(" (source ").Append(ReadString(row, "source_event_id", "unknown")).AppendLine(")");
            }
            return text.ToString().TrimEnd();
        }
    }
}
