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
        private const string GroupConversationParticipationGuidance =
            "PARTICIPATION DECISION: Decide for yourself whether to speak or remain quiet using your personality, relationships, goals, remembered invitations and commitments, current setting, observable player actions and actual attributed contributions. "
            + "Being present or addressed is an opportunity, not a duty to fill a reply. Quiet or reserved people may take initiative when they have a personal reason. "
            + "Narrated arrival or movement is meaningful scene input even without a spoken question. You may welcome someone, follow through on your own invitation, introduce a relevant subject or advance the scene naturally without waiting to be asked. "
            + "Do not invent events, other people's reactions or knowledge, or native execution. Respect private audiences and do not react to another person's unspoken thoughts. "
            + "Return participation=quiet with an empty reply if you have no worthwhile contribution, would merely repeat a reply or naturally choose to listen. "
            + "An earlier quiet response or a later queue position is not evidence that anyone spoke, answered, agreed or refused.";

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
            EnsureDatabaseColumn(connection, "group_conversation_contributions", "timeline_id", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "group_conversation_contributions", "world_day", "REAL NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "group_conversation_contributions", "source_event_id", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "group_conversation_state", "timeline_id", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "group_conversation_state", "world_day", "REAL NOT NULL DEFAULT 0");
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
                    stored = QuerySql(connection, "SELECT * FROM group_conversation_state WHERE session_id=$id AND timeline_id=$timeline AND world_day<=$day LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = sessionId, ["timeline"] = ContinuityTimeline(payload), ["day"] = ReadDouble(payload, "worldDay", 0d) }).FirstOrDefault() ?? new Dictionary<string, object>();
                    if (!TextListFromJson(ReadString(stored,"participant_ids_json","[]")).Contains(currentSpeakerId,StringComparer.OrdinalIgnoreCase))
                        stored = new Dictionary<string,object>();
                    string beat = ReadFirstString(payload, "sceneTurnId", "exchangeId", "turnId");
                    if (beat.Length > 0)
                        delivered = QuerySql(connection, @"SELECT * FROM group_conversation_contributions
WHERE session_id=$id AND scene_turn_id=$beat AND role='npc' AND text<>'' AND speaker_id<>$observer
AND timeline_id=$timeline AND world_day<=$day
AND " + MemoryJsonContainsSql("audience_json", "$observer") + " ORDER BY delivery_order,ts,contribution_id;",
                            new Dictionary<string, object> { ["id"] = sessionId, ["beat"] = beat, ["observer"] = currentSpeakerId,
                                ["timeline"] = ContinuityTimeline(payload), ["day"] = ReadDouble(payload, "worldDay", 0d) });
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
            var floor = ReadDictionary(payload, "groupTurnPlan");
            if (floor != null && floor.Count > 0)
            {
                builder.AppendLine(GroupConversationParticipationGuidance);
                builder.AppendLine("Being a quiet listener is neither absence nor refusal, evasion, agreement or rejection. The player has not answered suggestions made later in this same beat.");
            }
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
                // The former last-speaker list was neither a question ledger nor
                // proof of missing answers. Retain exact asked questions without
                // promoting omission or a narrower follow-up into evasion.
                var state = TryParseJsonObject(ReadString(stored, "state_json", "{}"));
                foreach (var question in ReadDictionaryList(state, "questionFrames")
                    .Where(q => ReadStringList(q, "audienceIds").Contains(currentSpeakerId, StringComparer.OrdinalIgnoreCase)).TakeLast(12))
                    builder.AppendLine(RenderContinuityRecord(ReadString(question, "id", ""), "group_player_opportunity", currentSpeakerId,
                        sessionId, ReadDouble(payload, "worldDay", 0d), 110, true,
                        "Question by " + ReadString(question, "speakerId", "") + " [" + ReadString(question, "id", "")
                        + "]: " + ReadString(question, "quote", "") + " | Player opportunity: " + GroupPlayerAnswerOpportunity(question, payload, newestText)
                        + ". Check linked answer state and the exact intervening dialogue; this inventory does not classify it as unanswered."));
                List<string> topics = MemoryQueryTerms(ObserverConversationText(ReadString(stored, "last_text", ""), lastSpeakerId, currentSpeakerId));
                if (topics.Count > 0) builder.AppendLine("Active topic terms: " + string.Join(", ", topics.Take(8)));
            }
            builder.AppendLine("Only delivered contributions establish who has answered this player turn. Queue position, an empty reply or a pending request never establishes an answer.");
            foreach (var contribution in delivered)
            {
                string speech = ObserverConversationText(ReadString(contribution, "text", ""),
                    ReadString(contribution, "speaker_id", ""), currentSpeakerId);
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
            string npcId, string npcName, string reply, string sessionId, long ts, string acceptedEventId = "")
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
            string beat = ReadFirstString(payload, "sceneTurnId", "exchangeId", "turnId");
            string timeline = ContinuityTimeline(payload);
            string identity = beat.Length > 0 ? beat : acceptedEventId.Length > 0 ? acceptedEventId : "unsequenced_" + ts;
            string contributionId = "group_" + PromptHash(timeline + "|" + sessionId + "|" + identity + "|" + npcId).Substring(0, 24);
            bool repairFailed = ConversationRepairFailed(payload, reply);
            var questionFrames = repairFailed ? new List<Dictionary<string, object>>() : GroupSpokenQuestionFrames(reply, contributionId, npcId, beat, participants);
            List<string> questions = questionFrames.Select(q => ReadString(q, "quote", "")).ToList();
            string observableReply = ObserverConversationText(reply, npcId, "");
            List<string> topics = MemoryQueryTerms(observableReply).Take(8).ToList();
            Dictionary<string, object> snapshot = new Dictionary<string, object>
            {
                ["lastSpeakerId"] = npcId, ["lastSpeakerName"] = npcName, ["lastText"] = observableReply,
                ["questionFrames"] = questionFrames, ["topics"] = topics, ["participantIds"] = participants
            };
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            using (ReignDbTransaction transaction = connection.BeginTransaction())
            {
                ExecuteSql(connection, "SELECT pg_advisory_xact_lock(hashtextextended($key,0));",
                    TestDict("key", "group_state|" + campaignId + "|" + timeline + "|" + sessionId));
                var inserted = QuerySql(connection, @"INSERT INTO group_conversation_contributions
(contribution_id,session_id,speaker_id,speaker_name,role,text,addressed_ids_json,ts,scene_turn_id,audience_json,validation_status,delivery_order,timeline_id,world_day,source_event_id)
VALUES($id,$session,$speaker,$name,'npc',$text,$addressed,$ts,$beat,$audience,$validation,
(SELECT COALESCE(MAX(delivery_order),0)+1 FROM group_conversation_contributions WHERE session_id=$session),$timeline,$day,$event)
ON CONFLICT(contribution_id) DO NOTHING RETURNING contribution_id;", new Dictionary<string, object> {
                    ["id"] = contributionId, ["session"] = sessionId, ["speaker"] = npcId ?? "", ["name"] = npcName ?? "",
                    ["text"] = reply, ["addressed"] = Json.Serialize(addressed), ["ts"] = ts, ["beat"] = beat,
                    ["audience"] = Json.Serialize(participants), ["validation"] = repairFailed ? "visible_failed_repair" : "accepted_speech",
                    ["timeline"] = timeline, ["day"] = ReadDouble(payload, "worldDay", 0d), ["event"] = acceptedEventId });
                if (inserted.Count == 0) { transaction.Commit(); return; }
                var old = QuerySql(connection, "SELECT state_json FROM group_conversation_state WHERE session_id=$id AND timeline_id=$timeline AND world_day<=$day;",
                    TestDict("id", sessionId, "timeline", timeline, "day", ReadDouble(payload, "worldDay", 0d))).FirstOrDefault();
                snapshot["speakingFloorPlans"] = ReadDictionaryList(TryParseJsonObject(ReadString(old, "state_json", "{}")), "speakingFloorPlans");
                questionFrames = ReadDictionaryList(TryParseJsonObject(ReadString(old, "state_json", "{}")), "questionFrames")
                    .Concat(questionFrames).GroupBy(q => ReadString(q, "id", "")).Select(g => g.First()).TakeLast(24).ToList();
                snapshot["questionFrames"] = questionFrames;
                string playerText = ReadFirstString(payload, "playerText", "text", "message");
                string playerId = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
                string playerName = ReadString(payload, "playerName", "Player");
                if (!string.IsNullOrWhiteSpace(playerText) && !ReadBool(payload, "suppressPlayerTranscript", false))
                {
                    ExecuteSql(connection, @"INSERT OR IGNORE INTO group_conversation_contributions
(contribution_id,session_id,speaker_id,speaker_name,role,text,addressed_ids_json,ts,scene_turn_id,audience_json,validation_status,timeline_id,world_day)
VALUES($id,$session,$speaker,$name,'player',$text,'[]',$ts,$beat,$audience,'accepted_speech',$timeline,$day);", new Dictionary<string, object>
                    {
                        ["id"] = "group_" + PromptHash(timeline + "|" + sessionId + "|" + identity + "|player|" + playerId).Substring(0, 24), ["session"] = sessionId,
                        ["speaker"] = playerId, ["name"] = playerName, ["text"] = playerText, ["ts"] = Math.Max(0, ts - 1),
                        ["beat"] = beat, ["audience"] = Json.Serialize(participants), ["timeline"] = timeline, ["day"] = ReadDouble(payload, "worldDay", 0d)
                    });
                }
                ExecuteSql(connection, @"INSERT INTO group_conversation_state
(session_id,campaign_id,mode,participant_ids_json,last_speaker_id,last_speaker_name,last_text,open_questions_json,topics_json,state_json,revision,updated_ts,timeline_id,world_day)
VALUES($session,$campaign,$mode,$participants,$speaker,$name,$text,$questions,$topics,$state,1,$ts,$timeline,$day)
ON CONFLICT(session_id) DO UPDATE SET participant_ids_json=$participants,last_speaker_id=$speaker,last_speaker_name=$name,
last_text=$text,open_questions_json=$questions,topics_json=$topics,state_json=$state,revision=group_conversation_state.revision+1,updated_ts=$ts,timeline_id=$timeline,world_day=$day;",
                    new Dictionary<string, object>
                    {
                        ["session"] = sessionId, ["campaign"] = campaignId, ["mode"] = ReadString(payload, "mode", ""),
                        ["participants"] = Json.Serialize(participants), ["speaker"] = npcId ?? "", ["name"] = npcName ?? "",
                        ["text"] = reply, ["questions"] = Json.Serialize(questions), ["topics"] = Json.Serialize(topics),
                        ["state"] = Json.Serialize(snapshot), ["ts"] = ts, ["timeline"] = timeline, ["day"] = ReadDouble(payload, "worldDay", 0d)
                    });
                transaction.Commit();
            }
        }

        private static List<Dictionary<string, object>> GroupSpokenQuestionFrames(string text, string sourceId,
            string speaker, string beat, List<string> audience)
        {
            text ??= "";
            var narration = Regex.Matches(text, @"\*[^*]*\*", RegexOptions.Singleline).Cast<Match>().ToList();
            string masked = Regex.Replace(text, @"\*[^*]*\*", m => new string(' ', m.Length), RegexOptions.Singleline);
            var frames = new List<Dictionary<string, object>>();
            foreach (Match match in Regex.Matches(masked, @"[^.!?\r\n]+(?:[.!?]+|$)"))
            {
                string quote = match.Value.Trim();
                int start = match.Index + match.Value.IndexOf(quote, StringComparison.Ordinal);
                if (quote.Length < 4 || narration.Any(n => start < n.Index + n.Length && start + quote.Length > n.Index)) continue;
                string requestForm = ConversationInformationRequestForm(masked, quote, start);
                if (requestForm.Length == 0) continue;
                frames.Add(TestDict("id", sourceId + ":q:" + start, "speakerId", speaker, "quote", quote,
                    "sourceHash", PromptHash(text), "start", start, "length", quote.Length, "requestForm", requestForm,
                    "beatId", beat, "audienceIds", audience));
            }
            return frames;
        }

        private static string GroupPlayerAnswerOpportunity(Dictionary<string, object> question, Dictionary<string, object> payload, string newestText)
        {
            string askedBeat = ReadString(question, "beatId", ""), currentBeat = ReadFirstString(payload, "sceneTurnId", "exchangeId", "turnId");
            if (askedBeat.Length == 0 || currentBeat.Length == 0) return "unknown (sequence unavailable; do not infer evasion)";
            if (askedBeat == currentBeat) return "none yet — asked during this same player beat";
            if (string.IsNullOrWhiteSpace(newestText) || ReadBool(payload, "suppressPlayerTranscript", false)) return "not established by this request";
            return "a later player contribution exists; evaluate its actual answer, separately from whether it convinces you";
        }
    }
}
