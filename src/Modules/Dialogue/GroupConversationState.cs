using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void EnsureGroupConversationStateSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS group_conversation_state (
session_id TEXT PRIMARY KEY,
campaign_id TEXT NOT NULL,
mode TEXT NOT NULL DEFAULT '',
participant_ids_json TEXT NOT NULL DEFAULT '[]',
last_speaker_id TEXT NOT NULL DEFAULT '',
last_speaker_name TEXT NOT NULL DEFAULT '',
last_text TEXT NOT NULL DEFAULT '',
open_questions_json TEXT NOT NULL DEFAULT '[]',
topics_json TEXT NOT NULL DEFAULT '[]',
state_json TEXT NOT NULL DEFAULT '{}',
revision INTEGER NOT NULL DEFAULT 0,
updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS group_conversation_contributions (
contribution_id TEXT PRIMARY KEY,
session_id TEXT NOT NULL,
speaker_id TEXT NOT NULL DEFAULT '',
speaker_name TEXT NOT NULL DEFAULT '',
role TEXT NOT NULL DEFAULT '',
text TEXT NOT NULL DEFAULT '',
addressed_ids_json TEXT NOT NULL DEFAULT '[]',
ts INTEGER NOT NULL);" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_group_contributions_session ON group_conversation_contributions(session_id,ts,contribution_id);");
            EnsureDatabaseColumn(connection, "group_conversation_contributions", "scene_turn_id", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "group_conversation_contributions", "audience_json", "TEXT NOT NULL DEFAULT '[]'");
            EnsureDatabaseColumn(connection, "group_conversation_contributions", "validation_status", "TEXT NOT NULL DEFAULT 'legacy_unknown'");
            EnsureDatabaseColumn(connection, "group_conversation_contributions", "delivery_order", "BIGINT NOT NULL DEFAULT 0");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_group_contributions_beat ON group_conversation_contributions(session_id,scene_turn_id,ts);");
        }

        private static string GroupConversationSessionId(Dictionary<string, object> payload)
        {
            return FirstNonEmpty(ReadFirstString(payload, "conversationSessionId", "sessionId", "conversationId"),
                ReadFirstString(payload, "eventId", "socialEventId"), ReadString(payload, "sceneTurnId", ""));
        }

        private static List<string> GroupConversationParticipantIds(Dictionary<string, object> payload)
        {
            Dictionary<string, object> scene = ReadDictionary(payload, "conversationSceneState") ?? new Dictionary<string, object>();
            return ReadDictionaryList(scene, "participants").Concat(ReadDictionaryList(payload, "sceneParticipants"))
                .Select(item => ReadFirstString(item, "heroStringId", "heroId", "id"))
                .Concat(ReadStringList(payload, "activeHeroIds"))
                .Concat(ReadStringList(payload, "participantHeroIds"))
                // NPC active-id lists omit the player. Include an explicitly
                // present player profile in the same observer/audience contract.
                .Concat(ReadDictionaryList(payload,"participantProfiles")
                    .Where(p=>ReadString(p,"role","").Equals("player",StringComparison.OrdinalIgnoreCase))
                    .Select(IdentityHeroId))
                .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsSharedGroupConversation(Dictionary<string, object> payload)
        {
            string mode = NormalizeLookup(ReadString(payload, "mode", ReadString(payload, "requestType", "")));
            return mode.Contains("party chat")
                || mode.Contains("social event")
                || mode.Contains("wilderness event")
                || GroupConversationParticipantIds(payload).Count >= 3;
        }

        private static string BuildSharedGroupConversationPrompt(string campaignId, Dictionary<string, object> payload,
            string currentSpeakerId, List<Dictionary<string, object>> recentLines, string newestText)
        {
            if (!IsSharedGroupConversation(payload)) return "";
            string sessionId = GroupConversationSessionId(payload);
            List<string> participants = GroupConversationParticipantIds(payload);
            Dictionary<string, object> stored = new Dictionary<string, object>();
            var delivered = new List<Dictionary<string, object>>();
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    stored = QuerySql(connection, "SELECT * FROM group_conversation_state WHERE session_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = sessionId }).FirstOrDefault() ?? new Dictionary<string, object>();
                    if (!TextListFromJson(ReadString(stored,"participant_ids_json","[]")).Contains(currentSpeakerId,StringComparer.OrdinalIgnoreCase))
                        stored = new Dictionary<string,object>();
                    string beat = ReadFirstString(payload, "sceneTurnId", "exchangeId", "turnId");
                    if (beat.Length > 0)
                        delivered = QuerySql(connection, @"SELECT * FROM group_conversation_contributions
WHERE session_id=$id AND scene_turn_id=$beat AND role='npc' AND text<>'' AND speaker_id<>$observer
AND " + MemoryJsonContainsSql("audience_json", "$observer") + " ORDER BY delivery_order,ts,contribution_id;",
                            new Dictionary<string, object> { ["id"] = sessionId, ["beat"] = beat, ["observer"] = currentSpeakerId });
                }
            }

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("SHARED GROUP CONVERSATION STATE - COMPACT DERIVED STATE");
            builder.AppendLine("This is one shared scene. The separately supplied canonical attributed transcript is the sole source for exact words. Respond to the player when appropriate, but also acknowledge, answer, challenge, support, interrupt, or build on relevant NPC statements. Never behave as though only the player has spoken.");
            builder.AppendLine("Before claiming that an earlier speaker omitted, misstated, agreed to, or denied a detail, check that speaker's exact canonical contribution. If the detail is present, acknowledge it instead of claiming omission.");
            builder.AppendLine("An earlier speaker's account of a tournament is dialogue, not a result record. If authoritative world history names the current speaker on a winning team, do not echo another speaker's claim that they lost or only heard of that victory.");
            builder.AppendLine("Epistemic boundary: attendance in this shared scene proves only hearing this scene. It does not make a participant a firsthand witness to an older event discussed here. Treat another NPC's 'we/everyone/all three' wording as that NPC's claim, never as a witness link, and never let it override the current speaker's own earlier denial or absent source evidence.");
            builder.AppendLine("Current speaker id: " + currentSpeakerId);
            builder.AppendLine("Present participant ids: " + string.Join(", ", participants));
            if (stored.Count > 0)
            {
                string lastSpeakerId =
                    ReadString(stored, "last_speaker_id", "");
                builder.AppendLine(
                    "Last contributing speaker: "
                    + ObserverSafeParticipantName(
                        payload,
                        currentSpeakerId,
                        lastSpeakerId,
                        ReadString(
                            stored,
                            "last_speaker_name",
                            lastSpeakerId)));
                List<string> questions = TextListFromJson(ReadString(stored, "open_questions_json", "[]"));
                if (questions.Count > 0) builder.AppendLine("Unresolved questions: " + string.Join(" | ", questions.Take(4)));
                List<string> topics = TextListFromJson(ReadString(stored, "topics_json", "[]"));
                if (topics.Count > 0) builder.AppendLine("Active topic terms: " + string.Join(", ", topics.Take(8)));
            }
            builder.AppendLine("Only delivered contributions establish who has answered this player turn. Queue position, an empty reply or a pending request never establishes an answer.");
            foreach (var contribution in delivered)
            {
                string speech = ReadString(contribution, "text", "");
                builder.Append("Delivered by ").Append(ObserverSafeParticipantName(payload, currentSpeakerId,
                    ReadString(contribution, "speaker_id", ""), ReadString(contribution, "speaker_name", "")))
                    .Append(" [").Append(ReadString(contribution, "validation_status", "legacy_unknown")).Append("]: ");
                bool alreadyQuoted = (recentLines ?? new List<Dictionary<string, object>>()).Any(r => ReadString(r, "text", "") == speech);
                builder.AppendLine(alreadyQuoted ? "complete speech is in the attributed current transcript" : speech);
            }
            builder.AppendLine("A visible failed repair is speech only and grants no accepted fact, promise or native-action authority. The latest player contribution appears once in the live-turn section below.");
            return builder.ToString().TrimEnd();
        }

        private static void UpdateSharedGroupConversationState(string campaignId, Dictionary<string, object> payload,
            string npcId, string npcName, string reply, string sessionId, long ts)
        {
            if (!IsSharedGroupConversation(payload) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(reply)) return;
            RecordPresentNpcSelfIntroduction(
                campaignId,
                payload,
                npcId,
                npcName,
                reply,
                sessionId);
            List<string> participants = GroupConversationParticipantIds(payload);
            List<string> addressed = participants.Where(id => !id.Equals(npcId, StringComparison.OrdinalIgnoreCase)
                && (reply ?? "").IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            List<string> questions = (reply ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()).Where(line => line.EndsWith("?", StringComparison.Ordinal)).Take(4).ToList();
            List<string> topics = MemoryQueryTerms(reply).Take(8).ToList();
            Dictionary<string, object> snapshot = new Dictionary<string, object>
            {
                ["lastSpeakerId"] = npcId, ["lastSpeakerName"] = npcName, ["lastText"] = LimitText(reply, 2000),
                ["openQuestions"] = questions, ["topics"] = topics, ["participantIds"] = participants
            };
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            using (ReignDbTransaction transaction = connection.BeginTransaction())
            {
                string playerText = ReadFirstString(payload, "playerText", "text", "message");
                string playerId = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
                string playerName = ReadString(payload, "playerName", "Player");
                if (!string.IsNullOrWhiteSpace(playerText))
                {
                    ExecuteSql(connection, @"INSERT OR IGNORE INTO group_conversation_contributions
(contribution_id,session_id,speaker_id,speaker_name,role,text,addressed_ids_json,ts)
VALUES($id,$session,$speaker,$name,'player',$text,'[]',$ts);", new Dictionary<string, object>
                    {
                        ["id"] = "group_" + PromptHash(sessionId + "|player|" + playerText).Substring(0, 24), ["session"] = sessionId,
                        ["speaker"] = playerId, ["name"] = playerName, ["text"] = LimitText(playerText, 4000), ["ts"] = Math.Max(0, ts - 1)
                    });
                }
                ExecuteSql(connection, @"INSERT INTO group_conversation_state
(session_id,campaign_id,mode,participant_ids_json,last_speaker_id,last_speaker_name,last_text,open_questions_json,topics_json,state_json,revision,updated_ts)
VALUES($session,$campaign,$mode,$participants,$speaker,$name,$text,$questions,$topics,$state,1,$ts)
ON CONFLICT(session_id) DO UPDATE SET participant_ids_json=$participants,last_speaker_id=$speaker,last_speaker_name=$name,
last_text=$text,open_questions_json=$questions,topics_json=$topics,state_json=$state,revision=group_conversation_state.revision+1,updated_ts=$ts;",
                    new Dictionary<string, object>
                    {
                        ["session"] = sessionId, ["campaign"] = campaignId, ["mode"] = ReadString(payload, "mode", ""),
                        ["participants"] = Json.Serialize(participants), ["speaker"] = npcId ?? "", ["name"] = npcName ?? "",
                        ["text"] = reply, ["questions"] = Json.Serialize(questions), ["topics"] = Json.Serialize(topics),
                        ["state"] = Json.Serialize(snapshot), ["ts"] = ts
                    });
                ExecuteSql(connection, @"INSERT OR IGNORE INTO group_conversation_contributions
(contribution_id,session_id,speaker_id,speaker_name,role,text,addressed_ids_json,ts,scene_turn_id,audience_json,validation_status,delivery_order)
VALUES($id,$session,$speaker,$name,'npc',$text,$addressed,$ts,$beat,$audience,$validation,
(SELECT COALESCE(MAX(delivery_order),0)+1 FROM group_conversation_contributions WHERE session_id=$session));", new Dictionary<string, object>
                {
                    ["id"] = "group_" + PromptHash(sessionId + "|" + ReadFirstString(payload, "sceneTurnId", "exchangeId", "turnId") + "|" + npcId + "|" + reply).Substring(0, 24),
                    ["session"] = sessionId, ["speaker"] = npcId ?? "", ["name"] = npcName ?? "",
                    ["text"] = reply, ["addressed"] = Json.Serialize(addressed), ["ts"] = ts,
                    ["beat"] = ReadFirstString(payload, "sceneTurnId", "exchangeId", "turnId"),
                    ["audience"] = Json.Serialize(participants),
                    ["validation"] = reply.TrimEnd().EndsWith(".,", StringComparison.Ordinal) ? "visible_failed_repair" : "accepted_speech"
                });
                transaction.Commit();
            }
        }
    }
}
