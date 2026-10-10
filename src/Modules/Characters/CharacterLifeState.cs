using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string CharacterLifeStateSchema = "reign-character-life-state-v1";

        private static bool IsLastingCharacterDecision(Dictionary<string, object> decision) =>
            AgencyAccepted(ReadString(decision, "disposition", ""))
            && (ReadString(decision, "topic", "") == "allegiance"
                || ReadString(decision, "stakes", "ordinary") == "life_changing");

        private static Dictionary<string, object> CharacterDecisionTransition(Dictionary<string, object> decision,
            string owner, string peer, string sourceId, string sourceHash, string timeline)
        {
            string disposition = ReadString(decision, "disposition", ""), topic = ReadString(decision, "topic", "");
            bool accepted = AgencyAccepted(disposition);
            return TestDict("schema", CharacterLifeStateSchema, "id", "life_decision_" + PromptHash(sourceId + "|" + ReadString(decision, "id", "")),
                "stateKey", "decision:" + ReadString(decision, "id", ""), "ownerId", owner, "peerId", peer,
                "timelineId", timeline, "sourceId", sourceId, "sourceHash", sourceHash, "sourceKind", "accepted_decision",
                "revision", ReadInt(decision, "revision", 1), "worldDay", ReadDouble(decision, "worldDay", 0),
                "kind", topic, "status", accepted ? "active_commitment" : "reconsidered", "disposition", disposition,
                "subject", ReadString(decision, "subject", ""), "terms", ReadDictionary(decision, "terms") ?? TestDict(),
                "reason", ReadString(decision, "reason", ""), "objections", ReadStringList(decision, "objections"),
                "reconsideration", ReadString(decision, "reconsideration", ""), "playerQuote", ReadString(decision, "playerQuote", ""),
                "reply", ReadString(decision, "reply", ""), "executionConfirmed", false,
                "knownBy", new[] { owner }, "authority", "recorded_personal_decision");
        }

        private static void StoreCharacterLifeProjection(ReignDbConnection connection, Dictionary<string, object> transition)
        {
            string id = ReadString(transition, "id", ""), owner = ReadString(transition, "ownerId", "");
            string text = ReadString(transition, "subject", ""), timeline = ReadString(transition, "timelineId", "");
            if (id.Length == 0 || owner.Length == 0 || text.Length == 0 || timeline.Length == 0) return;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // This is a rebuildable projection of retained receipts/native events.
            // It grants no new membership, action, emotion, or public knowledge.
            ExecuteSql(connection, @"INSERT INTO dynamic_characteristics
(characteristic_id,owner_id,category,topic_key,text,normalized_key,status,confidence,importance,provenance,
compatibility_status,source_event_id,world_day,known_by_json,first_ts,last_ts,payload_json)
VALUES($id,$owner,'life_transition',$topic,$text,$id,'active',1,1,'source_backed_life_transition',
'verified_source_projection',$source,$day,$known,$ts,$ts,$payload)
ON CONFLICT(owner_id,normalized_key) DO UPDATE SET text=$text,payload_json=$payload,last_ts=$ts
WHERE dynamic_characteristics.payload_json<>excluded.payload_json;",
                TestDict("id", id, "owner", owner, "topic", ReadString(transition, "stateKey", id), "text", text,
                    "source", ReadString(transition, "sourceId", ""), "day", ReadDouble(transition, "worldDay", 0),
                    "known", Json.Serialize(new[] { owner }), "ts", ts, "payload", Json.Serialize(TestDict("lifeTransition", transition))));
        }

        private static List<Dictionary<string, object>> ReadCharacterDecisionTransitions(ReignDbConnection connection,
            string owner, KnowledgeAccessContext access)
        {
            var result = new List<Dictionary<string, object>>();
            if (!TableExists(connection, "conversation_negotiation_turns")) return result;
            var receipts = QuerySql(connection, @"SELECT n.*,d.value AS decision_json FROM conversation_negotiation_turns n
CROSS JOIN LATERAL jsonb_array_elements(COALESCE(n.payload_json::jsonb->'updates','[]'::jsonb)) d(value)
WHERE n.owner_id=$owner
ORDER BY n.receipt_id;",
                TestDict("owner", owner, "day", access.WorldDay));
            foreach (var row in receipts)
            {
                var receipt = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                var decision = TryParseJsonObject(ReadString(row, "decision_json", "{}"));
                if (!ReadBool(receipt, "ok", false) || ReadString(receipt, "commitState", "") != "committed"
                    || decision == null || ReadDouble(decision, "worldDay", double.MaxValue) > access.WorldDay) continue;
                string turn = ReadString(row, "turn_id", ""), reply = ReadString(decision, "reply", "");
                // Membership in a group or an unchecked generated summary cannot
                // supply this NPC's acceptance. Follow their own original words.
                var sources = QuerySql(connection, @"SELECT * FROM conversation_turns WHERE speaker_id=$owner
AND (turn_id=$turn OR turn_id=$npcTurn OR event_id=$turn OR exchange_id=$turn) ORDER BY turn_order;",
                    TestDict("owner", owner, "turn", turn, "npcTurn", turn + "_npc"));
                var source = sources.FirstOrDefault(s => PrecisionTurnVisible(s, owner, access, connection)
                    && reply.Length > 0 && AgencyContainsExactQuote(ConversationTurnTextForRecall(s), reply));
                Dictionary<string, object> letter = null;
                if (source == null && TableExists(connection, "letters"))
                {
                    letter = QuerySql(connection, "SELECT * FROM letters WHERE sender_id=$owner AND (letter_id=$turn OR 'turn_letter_'||letter_id=$turn);",
                        TestDict("owner", owner, "turn", turn)).FirstOrDefault();
                    if (letter == null || !LetterVisibleToObserver(connection, letter, owner, access, out _)
                        || reply.Length == 0 || !AgencyContainsExactQuote(ReadString(letter, "body", ""), reply)) continue;
                }
                if (source == null && letter == null) continue;
                string decisionQuote = ReadString(decision, "replyQuote", "");
                if (decisionQuote.Length > 0 && !AgencyContainsExactQuote(reply, decisionQuote)) continue;
                var transition = CharacterDecisionTransition(decision, owner, ReadString(row, "peer_id", ""),
                    source != null ? ReadString(source, "turn_id", "") : ReadString(letter, "letter_id", ""),
                    source != null ? MemorySpecialistSourceHash(source) : LetterContentHash(letter),
                    source != null ? FirstNonEmpty(ReadString(source, "timeline_id", ""), ReadString(TryParseJsonObject(ReadString(source, "payload_json", "{}")), "timelineId", ""), access.TimelineId) : LetterSourceTimeline(letter));
                transition["receiptId"] = ReadString(row, "receipt_id", "");
                transition["lastingAcceptance"] = IsLastingCharacterDecision(decision);
                transition["decisionQuote"] = decisionQuote;
                transition["sourceSequence"] = source != null ? ReadLong(source, "accepted_sequence", 0) : ReadLong(letter, "dispatch_sequence", 0);
                result.Add(transition);
            }
            var lasting = new HashSet<string>(result.Where(r => ReadBool(r, "lastingAcceptance", false)).Select(r => ReadString(r, "stateKey", "")), StringComparer.Ordinal);
            return result.Where(r => lasting.Contains(ReadString(r, "stateKey", ""))).ToList();
        }

        private static List<Dictionary<string, object>> ReadCharacterNativeLifeTransitions(ReignDbConnection connection,
            string owner, KnowledgeAccessContext access, Dictionary<string, object> profile = null)
        {
            var result = new List<Dictionary<string, object>>();
            if (!TableExists(connection, "world_history_events") || !TableExists(connection, "world_history_entities")
                || !TableExists(connection, "world_history_knowledge_rules")) return result;
            var family = CharacterIdFrom(profile) == owner ? ReadStringList(profile, "childrenIds").Concat(new[] {
                ReadString(profile, "fatherId", ""), ReadString(profile, "motherId", ""), ReadString(profile, "spouseId", "") })
                .Where(id => id.Length > 0 && id != owner).Distinct().ToList() : new List<string>();
            var rows = QuerySql(connection, @"SELECT e.* FROM world_history_events e WHERE e.timeline_id=$timeline
AND e.world_day<=$day AND e.is_complete=1 AND e.phase='completed' AND e.source IN ('native','bannerlord_native')
AND e.event_type ~* '(marri|divorc|birth|concep|death|killed|executed|prisoner|captur|releas|rescu|escape|wounded|clan_join|clan_leave|changed_clan|changed_kingdom|kingdom_join|kingdom_leave|kingdom_created|ruling_clan|ruler|succession|fief|settlement_owner|parentage|affair|lover|betray)'
AND EXISTS(SELECT 1 FROM world_history_entities p WHERE p.event_id=e.event_id AND (p.entity_id=$owner
OR (e.event_type ~* '(death|killed|executed|birth|prisoner|captur|rescu)' AND p.entity_id IN (SELECT jsonb_array_elements_text($family::jsonb))))
AND p.entity_type IN ('hero','character') AND p.role<>'' AND p.role NOT IN ('observer','witness','spectator','informed'))
AND EXISTS(SELECT 1 FROM world_history_knowledge_rules k WHERE k.event_id=e.event_id AND k.available_day<=$day
AND ((k.audience_type='entity' AND k.audience_id=$owner) OR k.audience_type='global'
OR (k.audience_type='kingdom' AND k.audience_id=$kingdom))) ORDER BY e.world_day,e.sequence;",
                TestDict("timeline", access.TimelineId, "day", access.WorldDay, "owner", owner, "kingdom", access.KingdomId, "family", Json.Serialize(family)));
            foreach (var row in rows)
            {
                string id = ReadString(row, "event_id", ""), summary = ReadString(row, "summary", "");
                if (id.Length == 0 || summary.Length == 0) continue;
                if (!DateTimeOffset.TryParse(ReadString(row, "created_utc", ""), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var created) || created.ToUnixTimeSeconds() > access.AsOfTimestamp) continue;
                result.Add(TestDict("schema", CharacterLifeStateSchema, "id", "life_native_" + PromptHash(owner + "|" + id),
                    "stateKey", "native:" + id, "ownerId", owner, "peerId", "", "timelineId", access.TimelineId,
                    "sourceId", id, "sourceHash", MemorySourceHash(new[] { row }), "sourceKind", "native_confirmed",
                    "revision", 1, "worldDay", ReadDouble(row, "world_day", 0), "kind", ReadString(row, "event_type", ""),
                    "status", "completed_life_event", "subject", summary, "knownBy", new[] { owner },
                    "executionConfirmed", true, "authority", "completed_native_event", "personalMeaning", "not_inferred"));
            }
            return result;
        }

        private static List<Dictionary<string, object>> CurrentCharacterLifeState(IEnumerable<Dictionary<string, object>> transitions) =>
            transitions.GroupBy(t => ReadString(t, "stateKey", ""), StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(t => ReadDouble(t, "worldDay", 0)).ThenByDescending(t => ReadLong(t, "sourceSequence", 0))
                    .ThenByDescending(t => ReadInt(t, "revision", 0)).First())
                .OrderBy(t => ReadString(t, "sourceKind", "") == "accepted_decision" ? 0 : 1)
                .ThenByDescending(t => ReadDouble(t, "worldDay", 0)).ToList();

        private const string NativeLifeHistoryPromptPolicy = "Native life events are part of your history. Current native facts determine present circumstances; an event alone implies no emotional response or permanent personality change.";

        private static string FormatCharacterLifeTransition(Dictionary<string, object> row, bool includeNativePolicy = true)
        {
            var text = new StringBuilder("CURRENT DYNAMIC CHARACTER — ").Append(ReadString(row, "status", ""))
                .Append("; recorded day ").Append(ReadDouble(row, "worldDay", 0).ToString("0.#####", CultureInfo.InvariantCulture))
                .Append("; ").Append(ReadString(row, "subject", ""));
            if (ReadString(row, "sourceKind", "") == "accepted_decision")
            {
                text.Append("\nYour recorded decision about ").Append(ReadString(row, "peerId", ""))
                    .Append(": ").Append(ReadString(row, "disposition", ""))
                    .Append(". Actual terms: ").Append(CanonicalJson(ReadDictionary(row, "terms") ?? TestDict()))
                    .Append("\nYour stated reason: ").Append(ReadString(row, "reason", ""))
                    .Append("\nRetained reservations: ").Append(string.Join(", ", ReadStringList(row, "objections")))
                    .Append("; reconsideration: ").Append(ReadString(row, "reconsideration", ""))
                    .Append("\nOffer as recorded: ").Append(ReadString(row, "playerQuote", ""))
                    .Append("\nYour recorded response: ").Append(FirstNonEmpty(ReadString(row, "decisionQuote", ""), ReadString(row, "reply", "")))
                    .Append(ReadString(row, "status", "") == "active_commitment"
                        ? "\nThis decision remains part of your present commitments until superseded. Preserve its scope and conditions. Acceptance records intention; native execution has separate evidence."
                        : "\nThis later decision supersedes the earlier acceptance. Do not portray the previous commitment as unconditional or still accepted.")
                    .Append(" Your personality and right to disagree remain intact. This is private character context; do not disclose it to people who did not hear it. You need not restate it in each reply.");
                if (ReadString(row, "disposition", "") == "accepted_under_duress")
                    text.Append(" This records coerced compliance, not willing loyalty or affection.");
            }
            else if (includeNativePolicy) text.Append("\n" + NativeLifeHistoryPromptPolicy);
            return text.ToString();
        }

        private static string BuildCharacterLifeStatePrompt(ReignDbConnection connection, string owner, string peer,
            Dictionary<string, object> payload, List<string> sourceIds)
        {
            var access = BuildKnowledgeAccessContext(owner, peer, "", payload);
            bool official = ReadBool(payload, "officialMemoryFirewall", false);
            var transitions = official ? new List<Dictionary<string, object>>() : ReadCharacterDecisionTransitions(connection, owner, access);
            transitions.AddRange(ReadCharacterNativeLifeTransitions(connection, owner, access,
                ReadDictionary(payload, "hero") ?? ReadDictionary(payload, "profile") ?? ReadDictionary(payload, "speaker")));
            var current = CurrentCharacterLifeState(transitions);
            var query = ReadFirstString(payload, "playerText", "text", "message");
            var terms = MemoryQueryTerms(query);
            var selected = current.Where(t => ReadString(t, "sourceKind", "") == "accepted_decision")
                .Concat(current.Where(t => ReadString(t, "sourceKind", "") == "native_confirmed")
                    .OrderByDescending(t => terms.Count(term => ReadString(t, "subject", "").Contains(term, StringComparison.OrdinalIgnoreCase)))
                    .ThenByDescending(t => ReadDouble(t, "worldDay", 0)).Take(8)).ToList();
            // No writes occur on prompt reads. The background projection is
            // rebuildable and is never authoritative for recall or eligibility.
            payload["characterLifeState"] = TestDict("schema", CharacterLifeStateSchema, "ownerId", owner,
                "retainedTransitionCount", transitions.Count, "selectedCount", selected.Count,
                "sourceIds", selected.Select(t => ReadString(t, "sourceId", "")).ToList(), "remoteCalls", 0);
            return RenderCharacterLifePromptRows(selected, owner, sourceIds);
        }

        private static string RenderCharacterLifePromptRows(IEnumerable<Dictionary<string, object>> selected, string owner, List<string> sourceIds)
        {
            // Relevance chooses membership above, not render order. Unchanged selected
            // records must not churn the prefix when the latest query ranks them differently.
            var ordered = selected.OrderBy(t => ReadString(t, "sourceKind", "") == "accepted_decision" ? 0 : 1)
                .ThenBy(t => ReadDouble(t, "worldDay", 0))
                .ThenBy(t => ReadString(t, "sourceId", ""), StringComparer.Ordinal).ToList();
            var text = new StringBuilder();
            if (ordered.Any(t => ReadString(t, "sourceKind", "") != "accepted_decision")) text.AppendLine(NativeLifeHistoryPromptPolicy);
            foreach (var row in ordered)
            {
                string id = ReadString(row, "sourceId", "");
                sourceIds.Add(id);
                text.AppendLine(RenderContinuityRecord(id, "dynamic_character_life_state", owner, "", ReadDouble(row, "worldDay", 0),
                    120, true, FormatCharacterLifeTransition(row, false)));
            }
            return text.ToString();
        }

        private static string TryBuildCharacterLifeStatePrompt(string campaign, string owner, string peer,
            Dictionary<string, object> payload, List<string> sourceIds)
        {
            try
            {
                using var connection = OpenCampaignConnection(campaign);
                return BuildCharacterLifeStatePrompt(connection, owner, peer, payload, sourceIds);
            }
            catch (Exception ex)
            {
                payload["characterLifeState"] = TestDict("schema", CharacterLifeStateSchema, "status", "unavailable", "remoteCalls", 0);
                LogOperational("character.life_read_unavailable", TestDict("ownerId", owner, "errorType", ex.GetType().Name));
                return "";
            }
        }

        private static void RefreshCharacterLifeProjection(string campaign, string owner, KnowledgeAccessContext access)
        {
            try
            {
                using var connection = OpenCampaignConnection(campaign);
                using var transaction = connection.BeginTransaction();
                AcquirePostgreSqlTransactionMutationLock(connection);
                var transitions = ReadCharacterDecisionTransitions(connection, owner, access);
                transitions.AddRange(ReadCharacterNativeLifeTransitions(connection, owner, access));
                var current = new HashSet<string>(CurrentCharacterLifeState(transitions).Select(r => ReadString(r, "id", "")));
                ExecuteSql(connection, "UPDATE dynamic_characteristics SET status='source_unavailable' WHERE owner_id=$owner AND category='life_transition';", TestDict("owner", owner));
                foreach (var row in transitions)
                {
                    StoreCharacterLifeProjection(connection, row);
                    ExecuteSql(connection, "UPDATE dynamic_characteristics SET status=$status WHERE characteristic_id=$id;",
                        TestDict("id", ReadString(row, "id", ""), "status", current.Contains(ReadString(row, "id", "")) ? "active" : "superseded"));
                }
                transaction.Commit();
            }
            catch (Exception ex) { LogOperational("character.life_projection_deferred", TestDict("ownerId", owner, "errorType", ex.GetType().Name)); }
        }
    }
}
