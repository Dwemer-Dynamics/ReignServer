using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private delegate Dictionary<string, object> ConversationSourceAcceptance(
            ReignDbConnection connection, Dictionary<string, object> source);

        private sealed class AcceptedConversationUnit
        {
            internal string Campaign, Hero, Player, Location, EventId, EventType, PlayerText, Reply, Generation;
            internal long Timestamp;
            internal bool CommitEffects = true, RetrievalEligible = true, GroupConversation, PrivateWhisper;
            internal bool PublishScene, PublishSocial, PublishIntroductions;
            internal string SceneMode;
            internal Dictionary<string, object> ConceptionGate;
            internal string PlayerName, HeroName;
            internal Dictionary<string, object> Payload, Motive, Profile, Characteristics, Parsed;
            internal List<string> Participants;
            internal List<Dictionary<string, object>> Memories, Beliefs, Relationships, Obligations, Comprehensions, Continuity;
        }

        private static string AcceptedConversationEventId(string campaign, string hero, Dictionary<string, object> payload) =>
            "accepted_" + PromptHash(string.Join("|", campaign, ContinuityTimeline(payload), hero,
                ReadFirstString(payload, "conversationSessionId", "sessionId", "conversationId"),
                FirstNonEmpty(ReadFirstString(payload, "sceneTurnId", "turnId", "requestId"), EnsureCorrelationId(payload)))).Substring(0, 32);

        private static void EnsureAcceptedConversationUnitSchema(ReignDbConnection connection) => ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_accepted_units (
unit_id TEXT PRIMARY KEY, response_hash TEXT NOT NULL, restore_generation TEXT NOT NULL,
receipt_json TEXT NOT NULL, created_ts BIGINT NOT NULL);");

        // All operations inside the source callback use that SAME connection and
        // transaction. File projections, social incidents and native execution
        // have separate owners; this receipt never claims distributed atomicity.
        private static Dictionary<string, object> PublishAcceptedConversationUnit(AcceptedConversationUnit unit,
            Func<ConversationSourceAcceptance, Dictionary<string, object>> publishSources, Action<string> boundary = null)
        {
            if (string.IsNullOrWhiteSpace(unit.Generation))
                throw new InvalidOperationException("Conversation publication requires its pre-generation restore boundary.");
            var finalOpening = CloneDictionary(unit.Parsed ?? TestDict()); finalOpening["reply"] = unit.Reply;
            bool groundingFailed = ReadBool(unit.Payload, "conversationGroundingFailed", false);
            // Recheck exact accepted bytes after every rewrite; never publish a stale
            // successful receipt for a different final response.
            if (!groundingFailed && FindCriticalConversationContinuityViolations(finalOpening, unit.Payload, unit.Hero).Count > 0)
                throw new InvalidOperationException("The final response changed after its grounding check; the original contribution remains saved.");
            if (ApproachOpeningViolations(finalOpening, unit.Payload).Count > 0)
                throw new InvalidOperationException("The final spoken approach no longer matches its saved opening evidence.");
            lock (CampaignRelationshipWriteLock(unit.Campaign))
            {
                using (var setup = OpenCampaignConnection(unit.Campaign)) EnsureAcceptedConversationUnitSchema(setup);
                var exchange = publishSources((connection, source) =>
                {
                    var sourceRows = ReadStringList(source, "turnIds").SelectMany(id => QuerySql(connection,
                        "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id))).ToList();
                    source["turnIds"] = sourceRows.Select(row => ReadString(row, "turn_id", "")).ToList();
                    BindAcceptedInputSource(connection, unit.Payload, ReadString(source, "sessionId", ""), unit.EventId,
                        ReadStringList(source, "turnIds"));
                    string generation = ReadString(QuerySql(connection,
                        "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR SHARE;").Single(), "restore_generation", "");
                    if (generation != unit.Generation)
                        throw new InvalidOperationException("The campaign changed before the conversation could be accepted.");
                    string hash = PromptHash(CanonicalJson(TestDict("reply", unit.Reply, "playerText", unit.PlayerText,
                        "hero", unit.Hero, "player", unit.Player, "timeline", ContinuityTimeline(unit.Payload),
                        "sourceIds", ReadStringList(source, "turnIds"))));
                    var previous = QuerySql(connection, "SELECT * FROM conversation_accepted_units WHERE unit_id=$unit;",
                        TestDict("unit", unit.EventId)).SingleOrDefault();
                    if (previous != null)
                    {
                        if (ReadString(previous, "response_hash", "") != hash)
                            throw new InvalidOperationException("This conversation unit already has a different accepted response.");
                        var replay = TryParseJsonObject(ReadString(previous, "receipt_json", "{}"));
                        replay["idempotentReplay"] = true;
                        return replay;
                    }
                    var privateExchange = unit.PrivateWhisper ? StoreAcceptedWhisperSources(connection, unit, source) : TestDict();
                    boundary?.Invoke("after_sources");
                    // Commit methods may update their request-local receipts. Keep
                    // those changes private until the outer source transaction wins.
                    var effectPayload = CloneDictionary(unit.Payload);
                    CopyPlayerInputSource(unit.Payload, effectPayload);
                    var effectParsed = CloneDictionary(unit.Parsed);
                    bool technicalNotice = IsTechnicalConversationNotice(unit.Reply) || groundingFailed;
                    var effects = unit.CommitEffects && !technicalNotice
                        ? CommitFinalConversationEffects(unit.Campaign, unit.Hero, effectPayload, unit.Motive,
                            unit.Profile, unit.Characteristics, effectParsed, unit.Reply, unit.Generation, existingConnection: connection)
                        : TestDict("ok", true, "status", "suppressed_by_route", "scope", "negotiation_and_drinking");
                    if (!ReadBool(effects, "ok", false)) throw new InvalidOperationException("Conversation effects were not accepted.");
                    boundary?.Invoke("after_effects");
                    string session = ReadString(source, "sessionId", "");
                    var audience = unit.PrivateWhisper ? new[] { unit.Hero, unit.Player }
                        : TextListFromJson(ReadString(sourceRows.LastOrDefault(row => ReadString(row, "speaker_id", "") == unit.Hero),
                            "participants_json", "[]")).ToArray();
                    var scene = unit.PublishScene && !technicalNotice
                        ? FinalizeConversationSceneState(unit.Campaign, unit.Payload, unit.Hero, unit.PlayerText,
                            unit.Reply, unit.SceneMode ?? unit.EventType, unit.Parsed, connection, audience)
                        : TestDict("status", "suppressed_by_route");
                    var social = unit.PublishSocial && !technicalNotice
                        ? NormalizeAndPersistConversationSocialSignals(unit.Campaign, unit.Parsed, unit.Payload,
                            unit.Hero, unit.Player, unit.PlayerText, unit.Reply, unit.ConceptionGate,
                            unit.EventId, ReadDouble(unit.Payload, "worldDay", 0d), existingConnection: connection)
                        : TestDict("status", "suppressed_by_route", "signals", new List<Dictionary<string, object>>());
                    var introductions = unit.PublishIntroductions && !technicalNotice
                        ? ApplyIdentityIntroductionWrites(unit.Campaign, unit.Payload, unit.Parsed, session,
                            unit.Hero, unit.Player, unit.PlayerText, unit.Reply, audience, connection)
                        : new List<Dictionary<string, object>>();
                    boundary?.Invoke("after_context_effects");
                    string group = unit.GroupConversation || unit.EventType == "dialogue_turn"
                        ? "conversation:" + session
                        : BuildMemoryGroupKey(unit.Payload, unit.Hero, unit.Player, unit.Location, "social_event", unit.Timestamp);
                    var memory = technicalNotice ? TestDict("status", "technical_notice_excluded", "continuity", TestDict("status", "complete"))
                        : StoreTurnMemoryLayers(unit.Campaign, unit.EventId, unit.EventType,
                        unit.Hero, unit.Player, unit.Location, unit.PlayerText, unit.Reply,
                        (unit.Memories ?? new List<Dictionary<string, object>>()).Select(CloneDictionary).ToList(),
                        unit.Beliefs, unit.Relationships, unit.Obligations, unit.Comprehensions, group, unit.Timestamp,
                        unit.Participants, unit.RetrievalEligible, unit.Payload, unit.Continuity, connection);
                    boundary?.Invoke("after_memory");
                    var attention = technicalNotice ? TestDict("status", "technical_notice_excluded")
                        : RecordAcceptedPlayerAttention(connection, unit, sourceRows);
                    bool memoryComplete = !technicalNotice
                        && ReadString(ReadDictionary(memory, "continuity"), "status", "complete") == "complete"
                        && ReadString(memory, "sourceEvidenceStatus", "complete") == "complete";
                    var receipt = TestDict("ok", true, "status", memoryComplete ? "committed" : "committed_memory_incomplete", "unitId", unit.EventId,
                        "memoryUpdateComplete", memoryComplete,
                        "scope", "transcript_memory_negotiation_drinking_scene_social_evidence_identity", "responseHash", hash,
                        "acceptedReplyHash", PromptHash(unit.Reply ?? ""), "restoreGeneration", generation,
                        "memory", memory, "effects", effects, "privateExchange", privateExchange,
                        "scene", scene, "social", social, "introductions", introductions, "playerAttention", attention,
                        "sourceIds", ReadStringList(source, "turnIds").Concat(ReadStringList(privateExchange, "turnIds")).ToArray(),
                        "effectPayload", TestDict("conversationAgencyReceipt", ReadDictionary(effectPayload, "conversationAgencyReceipt"),
                            "intoxicationReceipt", ReadDictionary(effectPayload, "intoxicationReceipt")));
                    boundary?.Invoke("before_unit_receipt");
                    ExecuteSql(connection, @"INSERT INTO conversation_accepted_units(unit_id,response_hash,restore_generation,receipt_json,created_ts)
VALUES($unit,$hash,$generation,$receipt,$ts);", TestDict("unit", unit.EventId, "hash", hash,
                        "generation", generation, "receipt", Json.Serialize(receipt), "ts", unit.Timestamp));
                    SaveConversationInputAcceptance(connection, unit, source, receipt);
                    return receipt;
                });
                var accepted = ReadDictionary(exchange, "acceptedUnit");
                if (!ReadBool(exchange, "ok", false) || !ReadBool(accepted, "ok", false))
                    throw new InvalidOperationException("The conversation's source publication did not accept its dependent records.");
                var effectReceipt = ReadDictionary(accepted, "effects");
                foreach (var item in ReadDictionary(accepted, "effectPayload") ?? TestDict())
                    if (item.Value is Dictionary<string, object> value && value.Count > 0) unit.Payload[item.Key] = CloneDictionary(value);
                if (unit.Parsed != null && ReadDictionary(unit.Payload, "conversationAgencyReceipt") is { } agency && agency.Count > 0)
                    unit.Parsed["conversationAgencyReceipt"] = CloneDictionary(agency);
                unit.Payload["conversationEffectAcceptance"] = effectReceipt;
                unit.Payload["conversationAcceptedUnit"] = accepted;
                return exchange;
            }
        }

        private static Dictionary<string, object> StoreAcceptedWhisperSources(ReignDbConnection connection,
            AcceptedConversationUnit unit, Dictionary<string, object> publicSource)
        {
            string turn = ReadFirstString(unit.Payload, "sceneTurnId", "turnId", "exchangeId");
            string session = ReadString(publicSource, "sessionId", "") + "_whisper_" + SafeMemoryKey(unit.Hero) + "_" + SafeMemoryKey(turn);
            string exchange = turn + "_whisper_" + SafeMemoryKey(unit.Hero);
            var request = CloneDictionary(unit.Payload);
            CopyPlayerInputSource(unit.Payload, request);
            var audience = new List<string> { unit.Hero, unit.Player };
            request["conversationSessionId"] = session; request["sceneTurnId"] = exchange;
            request["participants"] = audience; request["activeHeroIds"] = new[] { unit.Hero };
            double day = ReadDouble(request, "worldDay", 0d);
            ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,participants_json,payload_json)
VALUES($session,$campaign,$hero,$player,'private_whisper','open',$ts,$ts,$day,$day,$location,$audience,$payload);",
                TestDict("session", session, "campaign", unit.Campaign, "hero", unit.Hero, "player", unit.Player,
                    "ts", unit.Timestamp, "day", day, "location", unit.Location, "audience", Json.Serialize(audience),
                    "payload", SerializeConversationTurnStoragePayload(request)));
            string playerTurn = exchange + "_player", npcTurn = exchange + "_npc_" + SafeMemoryKey(unit.Hero);
            PreservePlayerInputSource(connection, request, session, unit.EventId, exchange, unit.Player, unit.PlayerName, day, unit.Timestamp, unit.Location);
            InsertConversationTurn(connection, playerTurn, session, unit.EventId, 0, exchange, "player", unit.Player,
                unit.PlayerName, unit.PlayerText, "private_whisper", day, unit.Timestamp, unit.Location, audience, request);
            InsertConversationTurn(connection, npcTurn, session, unit.EventId, 1, exchange, "npc", unit.Hero,
                unit.HeroName, unit.Reply, "private_whisper", day, unit.Timestamp, unit.Location, audience, request);
            return TestDict("ok", true, "sessionId", session, "exchangeId", exchange, "turnIds", new[] { playerTurn, npcTurn });
        }
    }
}
