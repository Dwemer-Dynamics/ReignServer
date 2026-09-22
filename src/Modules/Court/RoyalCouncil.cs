using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly Dictionary<string, string[]> RoyalCouncilDomainKeys = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["war"] = new[] { "wars", "hostileForces", "realmForces", "recentBattles", "conflicts" },
            ["economic"] = new[] { "treasury", "income", "expenses", "shortages", "resources", "settlements", "shipments", "developments" },
            ["spymaster"] = new[] { "operations", "agents", "exposure", "rumors", "confidenceLimitations" },
            ["foreign"] = new[] { "rulerRelationships", "politicalPressure", "ambassadors", "referrals", "foreignWars", "nationalDevelopments" }
        };

        private static void EnsureRoyalCouncilSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS royal_council_turns (
turn_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,session_id TEXT NOT NULL,
speaker_role TEXT NOT NULL,speaker_hero_id TEXT NOT NULL DEFAULT '',speaker_name TEXT NOT NULL DEFAULT '',
player_text TEXT NOT NULL DEFAULT '',reply_text TEXT NOT NULL DEFAULT '',heard_by_json TEXT NOT NULL DEFAULT '[]',
domain_packet_json TEXT NOT NULL DEFAULT '{}',is_opening INTEGER NOT NULL DEFAULT 0,created_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_royal_council_session ON royal_council_turns(campaign_id,timeline_id,session_id,created_ts);");
        }

        private static Dictionary<string, object> RoyalCouncilRespond(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string role = ReadString(payload, "role", "").Trim().ToLowerInvariant();
            if (!RoyalCouncilDomainKeys.TryGetValue(role, out string[] allowedKeys))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "A supported Royal Council advisor role is required." };
            Dictionary<string, object> supplied = ReadDictionary(payload, "domainPacket") ?? new Dictionary<string, object>();
            Dictionary<string, object> bounded = RoyalCouncilBoundPacket(role, supplied);
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            RoyalCouncilEnrichServerFacts(campaignId, timelineId, role, bounded);
            string sessionId = FirstNonEmpty(ReadString(payload, "councilSessionId", ""), "royal_council_" + Guid.NewGuid().ToString("N"));
            string turnId = FirstNonEmpty(ReadString(payload, "turnId", ""), "council_turn_" + Guid.NewGuid().ToString("N"));
            Dictionary<string, object> speaker = ReadDictionary(payload, "speaker") ?? new Dictionary<string, object>();
            string speakerId = CharacterIdFrom(speaker);
            string speakerName = FirstNonEmpty(ReadString(speaker, "name", ""), role + " advisor");
            string playerText = ReadString(payload, "playerText", "");
            bool opening = ReadBool(payload, "isOpening", false), expand = ReadBool(payload, "expand", false);
            List<string> heardBy = ReadStringList(payload, "presentAdvisorIds").Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
            var prompt = new StringBuilder();
            prompt.AppendLine("Return only JSON with one field named reply.");
            prompt.AppendLine("You are the ruler's " + role + " advisor in a private official Royal Council. Use only the authorized packet. Never invent facts, reveal absent hidden facts, queue campaign actions, or alter relationships, reputation, or political pressure.");
            prompt.AppendLine("If nothing material is relevant, answer with one short sentence. Otherwise use " + (expand ? "at most two short paragraphs" : opening ? "two to four concise sentences" : "one to four concise sentences") + ". Omit ceremonial chatter.");
            prompt.AppendLine("Authorized packet: " + Json.Serialize(bounded));
            List<Dictionary<string, object>> transcript = ReadDictionaryList(payload, "transcript");
            prompt.AppendLine("Attributed council record: " + Json.Serialize(transcript.Skip(Math.Max(0, transcript.Count - 24)).ToList()));
            prompt.AppendLine(opening ? "Give the opening briefing now." : "Player question: " + playerText);
            Dictionary<string, object> request = new Dictionary<string, object>
            {
                ["requestType"] = "royal_council_advice", ["campaignId"] = campaignId, ["correlationId"] = EnsureCorrelationId(payload),
                ["messages"] = BuildSimplePromptEnvelope("royal_council_advice", role, "Give bounded advisory-only counsel and return valid JSON.", prompt.ToString()).Messages,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            };
            Dictionary<string, object> llm = ChatWithLlm(request);
            if (!ReadBool(llm, "ok", false)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = ReadString(llm, "error", "Royal Council advice failed."), ["providerCallCount"] = 1, ["routedRole"] = role };
            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", "")) ?? new Dictionary<string, object>();
            string reply = LimitText(SanitizeVisibleReply(FirstNonEmpty(ReadString(parsed, "reply", ""), ReadString(llm, "content", ""))), expand ? 2400 : 1200);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureRoyalCouncilSchema(connection);
                EnsureMemorySchema(connection);
                ExecuteSql(connection, @"INSERT OR REPLACE INTO royal_council_turns(turn_id,campaign_id,timeline_id,session_id,speaker_role,speaker_hero_id,speaker_name,player_text,reply_text,heard_by_json,domain_packet_json,is_opening,created_ts)
VALUES($id,$campaign,$timeline,$session,$role,$hero,$name,$player,$reply,$heard,$packet,$opening,$ts);", new Dictionary<string, object>
                {
                    ["id"] = turnId, ["campaign"] = campaignId, ["timeline"] = timelineId, ["session"] = sessionId, ["role"] = role,
                    ["hero"] = speakerId, ["name"] = speakerName, ["player"] = playerText, ["reply"] = reply,
                    ["heard"] = Json.Serialize(heardBy), ["packet"] = Json.Serialize(bounded), ["opening"] = opening ? 1 : 0, ["ts"] = ts
                });
                foreach (string listenerId in heardBy.Where(id => !string.IsNullOrWhiteSpace(id)))
                {
                    string memoryId = turnId + "_heard_" + RoyalCouncilStableToken(listenerId);
                    string attributed = (string.IsNullOrWhiteSpace(playerText) ? string.Empty : "The ruler asked: " + playerText + " ")
                        + speakerName + " (" + role + ") replied: " + reply;
                    ExecuteSql(connection, @"INSERT OR IGNORE INTO summaries(summary_id,scope,owner_id,summary_type,summary,source_events_json,start_ts,end_ts,event_count,participants_json,known_by_json,visibility,importance,confidence,tags_json,status,source,embedding_status,updated_ts,payload_json,memory_lane)
VALUES($id,$scope,$owner,'middle_term',$summary,$events,$ts,$ts,1,$participants,$known,'private',0.65,1.0,$tags,'active','royal_council','pending',$ts,$payload,'personal_state');",
                        new Dictionary<string, object>
                        {
                            ["id"] = memoryId, ["scope"] = "royal_council:" + sessionId, ["owner"] = listenerId,
                            ["summary"] = attributed, ["events"] = Json.Serialize(new[] { turnId }),
                            ["participants"] = Json.Serialize(heardBy), ["known"] = Json.Serialize(new[] { listenerId }),
                            ["tags"] = Json.Serialize(new[] { "royal_council", "official_council_record", role }), ["ts"] = ts,
                            ["payload"] = Json.Serialize(new Dictionary<string, object> { ["sessionId"] = sessionId, ["turnId"] = turnId, ["speakerRole"] = role, ["speakerHeroId"] = speakerId })
                        });
                }
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["reply"] = reply, ["speakerName"] = speakerName, ["speakerHeroId"] = speakerId,
                ["routedRole"] = role, ["providerCallCount"] = 1, ["officialRecordId"] = turnId,
                ["nativeActions"] = new ArrayList(), ["relationshipAssessments"] = new ArrayList(), ["reputationChanges"] = new ArrayList()
            };
        }

        private static Dictionary<string, object> RoyalCouncilBoundPacket(string role, Dictionary<string, object> supplied)
        {
            var bounded = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (!RoyalCouncilDomainKeys.TryGetValue(role ?? string.Empty, out string[] keys)) return bounded;
            foreach (string key in keys) if ((supplied ?? new Dictionary<string, object>()).TryGetValue(key, out object value)) bounded[key] = value;
            return bounded;
        }

        private static void RoyalCouncilEnrichServerFacts(string campaignId, string timelineId, string role, Dictionary<string, object> bounded)
        {
            if (!string.Equals(role, "foreign", StringComparison.OrdinalIgnoreCase)) return;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                bounded["politicalPressure"] = QuerySql(connection, @"SELECT world_day,actor_kingdom_id,target_kingdom_id,event_type,polarity,channel,severity,status,before_value,after_value,reason
FROM political_pressure_activity WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY world_day DESC,created_ts DESC LIMIT 20;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
            }
        }

        private static string RoyalCouncilStableToken(string value)
        {
            string token = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Take(64).ToArray());
            return string.IsNullOrWhiteSpace(token) ? "advisor" : token;
        }
    }
}
