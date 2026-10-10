using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> PrepareLogicalConversation(ReignDbConnection connection,
            Dictionary<string, object> payload, string campaign, string npc, string player, string session)
        {
            string parentId = ReadString(payload, "continuedFromSessionId", "");
            string reason = ReadString(payload, "continuationReason", "");
            string logical = ReadString(payload, "logicalConversationId", "");
            string timeline = ContinuityTimeline(payload);
            var existing = QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id;",
                new Dictionary<string, object> { ["id"] = session }).FirstOrDefault();
            bool Owns(Dictionary<string, object> row) => row != null && ReadString(row, "campaign_id", "") == campaign
                && ReadString(row, "npc_id", "") == npc && ReadString(row, "player_id", "") == player
                && ContinuityTimeline(TryParseJsonObject(ReadString(row, "payload_json", "{}"))) == timeline;
            if (existing != null)
            {
                if (!Owns(existing)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Conversation session ownership mismatch." };
                return new Dictionary<string, object> { ["ok"] = true, ["idempotent"] = true, ["sessionId"] = session,
                    ["logicalConversationId"] = ReadString(TryParseJsonObject(ReadString(existing, "payload_json", "{}")), "logicalConversationId", session),
                    ["status"] = ReadString(existing, "status", "") };
            }
            if (parentId.Length > 0)
            {
                var parent = QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id;",
                    new Dictionary<string, object> { ["id"] = parentId }).FirstOrDefault();
                if (!Owns(parent) || !new[] { "scene_transition", "transport_reconnect", "ui_continuation", "campaign_reload" }.Contains(reason)
                    || !new[] { "closed", "interrupted" }.Contains(ReadString(parent, "status", ""))
                    || !new[] { "scene_transition", "transport_reconnect", "ui_continuation", "interrupted_by_campaign_reload" }
                        .Contains(ReadString(parent, "close_reason", "")))
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Continuation requires an eligible source session, matching participants/timeline and an explicit continuation reason." };
                string previousLogical = ReadString(TryParseJsonObject(ReadString(parent, "payload_json", "{}")), "logicalConversationId", parentId);
                if (logical.Length > 0 && logical != previousLogical)
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Logical conversation does not match its continuation source." };
                if (ReadString(parent, "channel", "in_person") != ReadString(payload, "channel", "in_person")
                    || QuerySql(connection, "SELECT session_id FROM conversation_sessions WHERE payload_json::jsonb->>'continuedFromSessionId'=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = parentId }).Count > 0)
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Conversation handoff was already consumed or changed channel." };
                logical = previousLogical;
            }
            else
            {
                // A fresh conversation cannot borrow a logical ID merely because
                // the same people are present. Require the explicit parent chain.
                if (logical.Length > 0 && QuerySql(connection,
                    "SELECT session_id FROM conversation_sessions WHERE payload_json::jsonb->>'logicalConversationId'=$logical LIMIT 1;",
                    new Dictionary<string, object> { ["logical"] = logical }).Count > 0)
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "An existing logical conversation requires continuedFromSessionId." };
                if (logical.Length == 0) logical = "logical_" + session;
                reason = "new_conversation";
            }
            payload["logicalConversationId"] = logical;
            payload["continuationReason"] = reason;
            payload["conversationSequenceVersion"] = 1;
            return new Dictionary<string, object> { ["ok"] = true, ["sessionId"] = session, ["logicalConversationId"] = logical };
        }

        private static List<string> LogicalConversationAncestors(ReignDbConnection connection, string sessionId)
        {
            var result = new List<string>();
            var current = QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id;",
                new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault();
            if (current == null) return result;
            var identity = current;
            string logical = ReadString(TryParseJsonObject(ReadString(current, "payload_json", "{}")), "logicalConversationId", sessionId);
            string timeline = ContinuityTimeline(TryParseJsonObject(ReadString(current, "payload_json", "{}")));
            // Walk only explicit parents, never descendants, neighboring sessions,
            // or other meetings with these same participants.
            while (current != null && result.Count < 64)
            {
                string id = ReadString(current, "session_id", "");
                var payload = TryParseJsonObject(ReadString(current, "payload_json", "{}"));
                if (id.Length == 0 || result.Contains(id) || ReadString(current, "campaign_id", "") != ReadString(identity, "campaign_id", "")
                    || ReadString(current, "npc_id", "") != ReadString(identity, "npc_id", "")
                    || ReadString(current, "player_id", "") != ReadString(identity, "player_id", "")
                    || ReadString(current, "channel", "") != ReadString(identity, "channel", "")
                    || ContinuityTimeline(payload) != timeline || ReadString(payload, "logicalConversationId", id) != logical) break;
                result.Add(id);
                string parent = ReadString(payload, "continuedFromSessionId", "");
                current = parent.Length == 0 ? null : QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id;",
                    new Dictionary<string, object> { ["id"] = parent }).FirstOrDefault();
            }
            return result;
        }

        private static List<Dictionary<string, object>> ReadLogicalConversationTurns(ReignDbConnection connection, string sessionId,
            int limit, KnowledgeAccessContext knowledge = null)
        {
            var sessions = LogicalConversationAncestors(connection, sessionId);
            if (sessions.Count == 0) return new List<Dictionary<string, object>>();
            var parameters = new Dictionary<string, object>();
            for (int i = 0; i < sessions.Count; i++) parameters["s" + i] = sessions[i];
            string eligible = "session_id IN (" + string.Join(",", parameters.Keys.Select(k => "$" + k)) + ") AND status='active'";
            if (knowledge != null)
            {
                eligible += " AND world_day<=$day AND accepted_sequence<=$sequence AND ts<=$timestamp"
                    + " AND (speaker_id=$observer OR participants_json::jsonb ? $observer)"
                    + " AND " + MemorySourceTimelineSql("conversation_turns");
                parameters["day"] = knowledge.WorldDay; parameters["sequence"] = knowledge.AsOfAcceptedSequence;
                parameters["timestamp"] = knowledge.AsOfTimestamp; parameters["observer"] = knowledge.NpcId;
                parameters["timeline"] = knowledge.TimelineId;
            }
            parameters["limit"] = Math.Clamp(limit, 2, 100);
            // Apply as-of and observer filters before the bounded LIMIT.
            return QuerySql(connection, "SELECT * FROM conversation_turns WHERE " + eligible
                + " ORDER BY accepted_sequence DESC,ts DESC,turn_order DESC,turn_id DESC LIMIT $limit;", parameters)
                .Where(r => !string.IsNullOrWhiteSpace(ConversationTurnTextForRecall(r)))
                .Where(r => knowledge == null || PrecisionTurnVisible(r, knowledge.NpcId, knowledge, connection))
                .OrderBy(r => ReadLong(r, "accepted_sequence", 0)).ThenBy(r => ReadLong(r, "ts", 0))
                .ThenBy(r => ReadInt(r, "turn_order", 0)).Select(ProjectConversationTurnForRecall).ToList();
        }
    }
}
