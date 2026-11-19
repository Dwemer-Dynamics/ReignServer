using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        internal static void EnsureTemporalKnowledgeGraphSchema(ReignDbConnection connection)
        {
            DialogueSchema.CreateTemporalKnowledgeNodes(connection);
            DialogueSchema.CreateTemporalKnowledgeAssertions(connection);
            DialogueSchema.CreateIndexTemporalAssertionsCurrent(connection);
            DialogueSchema.CreateIndexTemporalAssertionsSubject(connection);
            DialogueSchema.CreateTemporalKnowledgeEdges(connection);
            DialogueSchema.CreateIndexTemporalEdgesNodes(connection);
        }

        internal static string UpsertTemporalAssertion(ReignDbConnection connection, string campaignId, string sourceEventId,
            string sourceRecordId, string kind, Dictionary<string, object> item, long ts)
        {
            item = item ?? new Dictionary<string, object>();
            string claim = ReadFirstString(item, "claim", "text", "belief", "summary", "description", "interpretation");
            string subject = ReadFirstString(item, "subjectId", "subject_id", "subject", "aboutHeroId", "heroStringId");
            string predicate = Program.NormalizeLookup(ReadFirstString(item, "predicate", "relation", "property", "factType", "type"));
            string objectId = ReadFirstString(item, "objectId", "object_id", "object", "targetId", "target_id");
            string owner = ReadFirstString(item, "believer", "believerId", "believer_id", "ownerId", "owner_id", "heroStringId");
            if (IsPrivateMentalLayer(kind) && IsUnsupportedRecallAbsenceState(claim)
                && !HasStoredRecallLossSupport(connection, Program.BuildKnowledgeAccessContext(owner, "", "", item), claim)) return "";
            string explicitFactKey = ReadFirstString(item, "factKey", "fact_key", "propositionId", "proposition_id");
            if (string.IsNullOrWhiteSpace(claim) && string.IsNullOrWhiteSpace(subject)) return "";
            AcquirePostgreSqlTransactionMutationLock(connection);

            string timeline = ContinuityTimeline(item);
            string identity = StableMemoryFactIdentity(item, subject, predicate, objectId, claim);
            string factKey = "fact_" + PromptHash(identity).Substring(0, 24);
            Dictionary<string, object> previous = DialogueStore.SelectTemporalKnowledgeAssertions(connection, new Dictionary<string, object>
            {
                ["campaign"] = campaignId ?? "", ["owner"] = owner ?? "", ["fact"] = factKey, ["timeline"] = timeline
            }).FirstOrDefault();
            string previousId = previous == null ? "" : ReadString(previous, "assertion_id", "");
            double confidence = Program.ClampDouble(ReadDouble(item, "confidence", 0.5d), 0d, 1d);
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
            if (previous != null && string.Equals(Program.NormalizeLookup(ReadString(previous, "claim", "")), Program.NormalizeLookup(claim), StringComparison.Ordinal)
                && Math.Abs(ReadDouble(previous, "confidence", 0d) - confidence) < 0.02d)
            {
                // Repeating a recalled claim does not move its original learning
                // time or discard the evidence supporting the prior version.
                return previousId;
            }

            if (!string.IsNullOrWhiteSpace(previousId))
                DialogueStore.UpdateTemporalKnowledgeAssertions2(connection, new Dictionary<string, object> { ["ts"] = ts, ["id"] = previousId, ["day"] = ReadDouble(item, "worldDay", 0d) });

            string assertionId = "assert_" + Guid.NewGuid().ToString("N");
            List<string> knownBy = Program.MergeStringLists(ReadStringList(item, "known_by"), ReadStringList(item, "knownBy"));
            if (knownBy.Count == 0 && !string.IsNullOrWhiteSpace(owner)) knownBy.Add(owner);
            List<string> hiddenFrom = Program.MergeStringLists(ReadStringList(item, "hidden_from"), ReadStringList(item, "hiddenFrom"));
            string truthStatus = IsPrivateMentalLayer(kind)
                ? (kind == "belief" ? "believed" : "interpretation")
                : kind; // Dialogue cannot self-certify a native effect or objective truth.
            if (IsPrivateMentalLayer(kind)) knownBy = new List<string> { owner };
            DialogueStore.InsertTemporalKnowledgeAssertions(connection, new Dictionary<string, object>
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
            DialogueStore.UpsertTemporalKnowledgeNodes(connection, new Dictionary<string, object> { ["id"] = id, ["type"] = type, ["label"] = label ?? "", ["payload"] = Json.Serialize(payload ?? new Dictionary<string, object>()), ["ts"] = ts });
        }

        private static void InsertTemporalEdge(ReignDbConnection connection, string from, string to, string type, string assertionId, long ts)
        {
            string id = "edge_" + PromptHash(from + "|" + to + "|" + type + "|" + assertionId).Substring(0, 24);
            DialogueStore.InsertOrIgnoreTemporalKnowledgeEdges(connection, new Dictionary<string, object>
            { ["id"] = id, ["from"] = from, ["to"] = to, ["type"] = type, ["assertion"] = assertionId, ["ts"] = ts });
        }

        internal static List<Dictionary<string, object>> LoadTemporalKnowledgeForPrompt(ReignDbConnection connection, string campaignId,
            Program.KnowledgeAccessContext knowledge, List<string> queryTerms, int limit)
        {
            knowledge ??= new Program.KnowledgeAccessContext();
            var result = new List<Dictionary<string, object>>();
            var resolved = new HashSet<string>(StringComparer.Ordinal);
            const int batch = 128;
            // Search historical wording to discover the fact, then resolve its
            // latest eligible revision independently of the search terms. A child
            // correction must win over a restored parent without modifying that
            // parent's snapshot hash or hiding it from an earlier as-of read.
            // Obligation projections retain the evidence of their own revision.
            string eligible = @"
WHERE campaign_id=$campaign AND ($timeline='' OR " + MemorySourceTimelineSql("temporal_knowledge_assertions") + @")
AND event_day<=$day AND learned_day<=$day
AND COALESCE(NULLIF(recorded_ts,0),observed_ts)<=$asOfTimestamp
AND (valid_to_ts IS NULL OR valid_to_day IS NOT NULL OR valid_to_ts>$asOfTimestamp)
AND (assertion_kind NOT IN ('belief','comprehension','interpretation') OR perspective_owner_id=$observer)
AND ((NOT (" + PromptObligationPayloadSql + @" ?| ARRAY['owed_by','owedBy','owed_to','owedTo'])
    AND NOT EXISTS (SELECT 1 FROM obligations o WHERE o.obligation_id=temporal_knowledge_assertions.source_record_id))
    OR (" + PromptObligationEvidenceSql + @"))
AND NOT EXISTS (SELECT 1 FROM jsonb_array_elements_text(hidden_from_json::jsonb) h(value) WHERE lower(h.value)=lower($observer))
AND (perspective_owner_id=$observer OR lower(visibility) IN ('world','global','common','public','local','nearby','kingdom','clan','settlement','party','army') OR lower(visibility) LIKE 'public_%' OR EXISTS (SELECT 1 FROM jsonb_array_elements_text(known_by_json::jsonb) k(value) WHERE lower(k.value)=lower($observer)))";
            var parameters = TestDict("campaign", campaignId ?? "", "observer", knowledge.NpcId,
                "timeline", knowledge.TimelineId, "day", knowledge.WorldDay, "asOfTimestamp", knowledge.AsOfTimestamp,
                "query", Program.BuildFtsQuery(queryTerms ?? new()), "batch", batch);
            bool Visible(Dictionary<string, object> row) => MemoryTimeVisible(row, knowledge, connection)
                && !Program.KnowledgeListContains(row, "hidden_from_json", knowledge.NpcId)
                && (!IsPrivateMentalLayer(ReadString(row, "assertion_kind", ""))
                    || Program.KnowledgeIdEquals(ReadString(row, "perspective_owner_id", ""), knowledge.NpcId))
                && (Program.KnowledgeVisibilityAllows(row, knowledge)
                    || Program.KnowledgeIdEquals(ReadString(row, "perspective_owner_id", ""), knowledge.NpcId)
                    || Program.KnowledgeListContains(row, "known_by_json", knowledge.NpcId));
            for (int offset = 0; result.Count < Math.Max(1, limit); offset += batch)
            {
                parameters["offset"] = offset;
                var rows = DialogueStore.SelectTemporalKnowledgeAssertions2(connection, eligible,parameters);
                foreach (var hit in rows)
                {
                    if (!Visible(hit)) continue;
                    string fact = ReadString(hit, "fact_key", ""), owner = ReadString(hit, "perspective_owner_id", "");
                    string key = CanonicalJson(new[] { owner, fact.Length > 0 ? fact : ReadString(hit, "assertion_id", "") });
                    if (!resolved.Add(key)) continue;
                    Dictionary<string, object> latest = fact.Length == 0 ? hit : null;
                    var revisionParameters = new Dictionary<string, object>(parameters) { ["fact"] = fact, ["owner"] = owner };
                    for (int revisionOffset = 0; latest == null; revisionOffset += batch)
                    {
                        revisionParameters["offset"] = revisionOffset;
                        var revisions = DialogueStore.SelectTemporalKnowledgeAssertions3(connection, eligible,revisionParameters);
                        latest = revisions.FirstOrDefault(Visible);
                        if (revisions.Count < batch) break;
                    }
                    if (latest != null) result.Add(latest);
                    if (result.Count >= Math.Max(1, limit)) break;
                }
                if (rows.Count < batch) break;
            }
            return result;
        }

        internal static string FormatTemporalKnowledgeForPrompt(List<Dictionary<string, object>> rows)
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
