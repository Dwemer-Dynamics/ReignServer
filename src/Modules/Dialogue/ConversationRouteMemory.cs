using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Thin route adapters keep domain authority in their original handlers.
        // These contracts own local evidence checks and source publication.
        private sealed class RouteConversationMemory
        {
            internal string Campaign, Observer, Player, Channel, Session, Unit, Generation, InputHash;
            internal Dictionary<string, object> Payload, Request, Settings, Result;
            internal List<Dictionary<string, object>> Prior;
            internal List<string> Audience;
            internal bool ChildhoodOnly;
            internal int ProviderCalls;
        }

        private static void EnsureRouteConversationAcceptanceSchema(ReignDbConnection connection) => ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_route_acceptance (
unit_id TEXT PRIMARY KEY, input_hash TEXT NOT NULL, accepted_reply_hash TEXT NOT NULL,
result_json TEXT NOT NULL, restore_generation TEXT NOT NULL, created_ts BIGINT NOT NULL);");

        private static RouteConversationMemory PrepareRouteConversationMemory(Dictionary<string, object> source,
            string observer, string channel, string nativeSession, string nativeTurn, IEnumerable<string> audience,
            bool childhoodOnly = false, Dictionary<string, object> settingsOverride = null)
        {
            var payload = CloneDictionary(source ?? TestDict());
            CopyPlayerInputSource(source, payload);
            string campaign = ReadString(payload, "campaignId", "");
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            double day = ReadDouble(payload, "worldDay", double.NaN);
            if (campaign.Length == 0 || observer.Length == 0 || player.Length == 0
                || nativeSession.Length == 0 || nativeTurn.Length == 0 || !double.IsFinite(day) || day < 0)
                throw new InvalidOperationException("Conversation memory requires the campaign, actual speakers, session, turn and current world day.");
            var context = new RouteConversationMemory { Campaign = campaign, Observer = observer, Player = player, Channel = channel,
                Session = "route_" + PromptHash(channel + "|" + ContinuityTimeline(payload) + "|" + nativeSession).Substring(0, 32),
                Unit = "route_turn_" + PromptHash(channel + "|" + ContinuityTimeline(payload) + "|" + nativeSession + "|" + nativeTurn + "|" + observer).Substring(0, 32),
                Payload = payload, ChildhoodOnly = childhoodOnly, Settings = settingsOverride ?? LoadSettings(),
                Audience = (audience ?? Enumerable.Empty<string>()).Concat(new[] { player, observer })
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
            payload["conversationSessionId"] = context.Session;
            payload["contributionId"] = FirstNonEmpty(ReadFirstString(payload, "contributionId", "sceneTurnId", "turnId"), nativeTurn);
            payload["turnId"] = context.Unit;
            payload["sceneTurnId"] = context.Unit;
            payload["channel"] = channel;
            payload["heroStringId"] = observer;
            payload["playerHeroStringId"] = player;
            payload["participants"] = context.Audience;
            using var connection = OpenCampaignConnection(campaign);
            ReadMemoryPrecisionState(connection);
            EnsureRouteConversationAcceptanceSchema(connection);
            using (var transaction = connection.BeginTransaction())
            {
                context.Generation = ReadString(QuerySql(connection,
                    "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR SHARE;").Single(), "restore_generation", "");
                payload["memoryAsOfAcceptedSequence"] = ReadAcceptedMemorySourceWatermark(connection);
                payload["memoryAsOfTimestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                transaction.Commit();
            }
            AdmitConversationInput(campaign, payload, context.Generation, observer);
            ApplyPlayerInputAudienceToPayload(payload, observer);
            var prior = ReadDictionaryList(payload, "transcript");
            if (prior.Count == 0) prior = ReadDictionaryList(payload, "groupTranscript");
            context.Prior = ProjectConversationLinesForObserver(prior, observer, payload);
            payload["transcript"] = context.Prior;
            payload["groupTranscript"] = context.Prior;
            context.InputHash = PromptHash(CanonicalJson(TestDict("route", channel, "observer", observer, "player", player,
                "session", nativeSession, "turn", nativeTurn, "phase", ReadString(payload, "phase", ""),
                "playerText", OriginalPlayerInput(payload), "audience", context.Audience.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                "outcome", ReadString(payload, "outcome", ""), "domainPacket", ReadDictionary(payload, "domainPacket"),
                "requestTerms", ReadString(payload, "requestTerms", ""))));
            payload["routeInputHash"] = context.InputHash;
            return context;
        }

        private static Dictionary<string, object> GenerateRouteConversation(RouteConversationMemory context,
            Dictionary<string, object> request, string domainContract, int maximumReplyCharacters,
            Func<Dictionary<string, object>, Dictionary<string, object>> responder = null)
        {
            context.Request = request;
            request["heroStringId"] = context.Observer;
            request["model"] = FirstNonEmpty(ReadString(request, "model", ""), ModelForRequest(context.Settings, ReadString(request, "requestType", "dialogue")));
            request["routeMemoryContract"] = domainContract;
            using (var connection = OpenCampaignConnection(context.Campaign))
            {
                var prior = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", context.Unit + "_npc")).SingleOrDefault();
                if (prior != null)
                {
                    if (ReadString(TryParseJsonObject(ReadString(prior, "payload_json", "{}")), "routeInputHash", "") != context.InputHash)
                        return TestDict("ok", false, "error", "This accepted conversation turn belongs to different input.");
                    var receipt = QuerySql(connection, "SELECT * FROM conversation_route_acceptance WHERE unit_id=$unit;", TestDict("unit", context.Unit)).SingleOrDefault();
                    if (receipt != null && (ReadString(receipt, "input_hash", "") != context.InputHash
                        || ReadString(receipt, "accepted_reply_hash", "") != PromptHash(ReadString(prior, "text", ""))))
                        return TestDict("ok", false, "error", "The accepted route receipt does not match its original response.");
                    context.Result = receipt == null
                        ? TestDict("ok", true, "content", Json.Serialize(TestDict("reply", ReadString(prior, "text", ""))), "receiptStatus", "legacy_receipt_unavailable")
                        : TryParseJsonObject(ReadString(receipt, "result_json", "{}"));
                    if (!ReadBool(context.Result, "ok", false)
                        || ReadString(TryParseJsonObject(ReadString(context.Result, "content", "")), "reply", "") != ReadString(prior, "text", ""))
                        return TestDict("ok", false, "error", "The stored route response is incomplete or changed.");
                    context.Result["idempotentReplay"] = true;
                    context.Result["providerCallCount"] = 0;
                    return context.Result;
                }
            }
            var evidence = new StringBuilder("Conversation evidence below records statements, observations and interpretations, not fresh native authority. The route's current authoritative facts and terms remain controlling. An answer can be received without being convincing; a new question does not prove earlier evasion.\n");
            if (!context.Channel.Contains("correspond", StringComparison.OrdinalIgnoreCase)
                && ReadDictionaryList(context.Payload, "sceneParticipants").Count > 0)
                PrepareConversationSceneState(context.Campaign, context.Payload, context.Observer,
                    ReadString(context.Payload, "playerText", ""), context.Prior, context.Channel);
            if (!context.ChildhoodOnly)
            {
                var payload = context.Payload;
                payload["deferContinuityAllocation"] = true;
                payload["memoryQueryText"] = ReadString(payload, "playerText", "");
                SetMemoryTranscriptContext(payload, context.Prior);
                var packet = BuildNpcMemoryPacket(context.Campaign, context.Observer, context.Player,
                    ReadFirstString(payload, "locationId", "currentSettlementId", "settlementId"),
                    ReadString(payload, "playerText", ""), 2500, payload, context.Settings);
                payload["memoryPrecisionEvidence"] = ReadDictionary(packet, "precisionEvidence");
                evidence.AppendLine(ReadString(packet, "memoryPacket", ""));
                // Personal memory supplies conversational continuity only; it does
                // not alter a court's options, treasury, powers or current facts.
                evidence.AppendLine(BuildProtectedConversationContinuity(context.Campaign, context.Observer, payload, TestDict()));
                request["promptEnvelope"] = TestDict("continuityPreflight", TestDict(
                    "memoryPrecisionEvidence", ReadDictionary(payload, "memoryPrecisionEvidence"),
                    "continuityEvidence", ReadDictionary(payload, "continuityEvidence")));
            }
            else evidence.AppendLine("Only the supplied childhood sources and the observed family scene are eligible. Adult relationship material is excluded.")
                .AppendLine(BuildCurrentConversationGrounding(context.Payload, context.Observer));
            foreach (var line in context.Prior)
            {
                string text = ReadString(line, "text", ""), who = ReadFirstString(line, "speakerHeroStringId", "speakerId", "speaker");
                if (text.Length == 0) continue;
                evidence.AppendLine(RenderContinuityRecord(FirstNonEmpty(ReadFirstString(line, "turnId", "id"), "route_prior_" + PromptHash(who + "|" + text)),
                    "attributed_turn", who, context.Session, ReadDouble(context.Payload, "worldDay", 0d), 100, true,
                    "SPEAKER " + who + "; ROLE " + ReadString(line, "role", "unknown") + ": " + text));
            }
            var messages = ReadDictionaryList(request, "messages");
            messages.Add(TestDict("role", "user", "content", evidence.ToString()));
            request["messages"] = messages;
            bool Valid(Dictionary<string, object> draft)
            {
                string reply = ConversationSpeechForRecall(ReadString(draft, "reply", ""));
                return !string.IsNullOrWhiteSpace(reply) && reply.Length <= maximumReplyCharacters
                    && !Reign.Core.Contracts.Court.ReignRulerDocketRules.ContainsForbiddenNobleDocketStatistics(reply);
            }
            var call = responder ?? ChatWithLlm;
            Dictionary<string, object> Counted(Dictionary<string, object> input) { context.ProviderCalls++; return call(input); }
            Dictionary<string, object> Failed(Dictionary<string, object> failure)
            {
                context.Result = EnsureConversationDelivery(failure);
                try
                {
                    PublishRouteConversation(context, "", "", _ => { }, inputOnly: true);
                    context.Result["inputRecovery"] = TestDict("status", "player_input_retained",
                        "npcResponseStored", false, "effectsConfirmed", false);
                }
                catch (Exception ex)
                {
                    context.Result["inputRecovery"] = TestDict("status", "player_input_unavailable",
                        "coverageGap", LimitText(ex.Message, 500), "npcResponseStored", false);
                }
                return context.Result;
            }
            var result = EnsureConversationDelivery(Counted(request));
            if (!ReadBool(result, "ok", false)) return Failed(result);
            var parsed = TryParseJsonObject(ReadString(result, "content", ""));
            if (!Valid(parsed))
                return Failed(TestDict("ok", false, "error", "The speaker did not return a complete bounded dialogue response."));
            result = RetryDialogueIntegrityViolation(result, request, context.Payload, context.Prior,
                context.Campaign, EnsureCorrelationId(request), context.Channel, context.Observer, context.Unit, Counted, persist: false);
            if (!ReadBool(result, "ok", false)) return Failed(result);
            result = RetryRoleplayContinuityViolation(result, request, context.Payload, TestDict(), context.Prior,
                context.Campaign, EnsureCorrelationId(request), context.Channel, context.Observer, context.Observer, "Player", context.Unit, Counted);
            if (!ReadBool(result, "ok", false)) return Failed(result);
            parsed = TryParseJsonObject(ReadString(result, "content", ""));
            if (!Valid(parsed)) return Failed(TestDict("ok", false, "error", "The corrected route reply did not satisfy its original bounded contract."));
            parsed["reply"] = SanitizeVisibleReply(ReadString(parsed, "reply", ""));
            string finalReply = ReadString(parsed, "reply", "");
            finalReply = RenderGroundedSceneHeading(context.Payload, context.Observer, parsed, finalReply);
            FinalizeConversationGrounding(context.Payload, context.Observer, parsed, ref finalReply);
            parsed["reply"] = finalReply;
            result["content"] = Json.Serialize(parsed);
            result["providerCallCount"] = context.ProviderCalls;
            context.Result = result;
            return result;
        }

        private static void PublishRouteConversation(RouteConversationMemory context, string reply, string speakerName,
            Action<ReignDbConnection> writeRoute, Action<string> boundary = null, bool inputOnly = false)
        {
            if (inputOnly ? ReadBool(context.Result, "ok", false) || reply.Length > 0
                : !ReadBool(context.Result, "ok", false) || string.IsNullOrWhiteSpace(reply)
                    || ReadString(TryParseJsonObject(ReadString(context.Result, "content", "")), "reply", "") != reply)
                throw new InvalidOperationException("Only the exact accepted route response may be published.");
            using var connection = OpenCampaignConnection(context.Campaign);
            using var transaction = connection.BeginTransaction();
            LockConversationSourcePublication(connection, context.Session);
            var current = QuerySql(connection, "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR SHARE;").Single();
            if (ReadString(current, "restore_generation", "") != context.Generation)
                throw new InvalidOperationException("The campaign changed before this route's response could be accepted.");
            var payload = context.Payload;
            double day = ReadDouble(payload, "worldDay", 0d);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string location = ReadFirstString(payload, "locationId", "currentSettlementId", "settlementId");
            ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,participants_json,payload_json)
VALUES($session,$campaign,$npc,$player,$channel,'open',$now,$now,$day,$day,$location,$audience,$payload);",
                TestDict("session", context.Session, "campaign", context.Campaign, "npc", context.Observer, "player", context.Player,
                    "channel", context.Channel, "now", now, "day", day, "location", location,
                    "audience", Json.Serialize(context.Audience), "payload", SerializeConversationTurnStoragePayload(payload)));
            int order = ReadInt(QuerySql(connection, "SELECT COALESCE(MAX(turn_order),-1)+1 AS n FROM conversation_turns WHERE session_id=$session;",
                TestDict("session", context.Session)).Single(), "n", 0);
            string playerText = ReadString(payload, "publicPlayerText", ReadString(payload, "playerText", ""));
            PreservePlayerInputSource(connection, payload, context.Session, context.Unit, context.Unit, context.Player,
                ReadString(payload, "playerName", "Player"), day, now, location);
            InsertConversationTurn(connection, context.Unit + "_player", context.Session, context.Unit, order++, context.Unit, "player",
                context.Player, ReadString(payload, "playerName", "Player"), playerText, context.Channel, day, now, location, context.Audience, payload);
            string heardPlayer = ReadString(payload, "playerText", "");
            if (!string.IsNullOrWhiteSpace(heardPlayer) && heardPlayer != playerText)
                InsertConversationTurn(connection, context.Unit + "_private_player", context.Session, context.Unit, order++, context.Unit, "player",
                    context.Player, ReadString(payload, "playerName", "Player"), heardPlayer, context.Channel, day, now, location,
                    new List<string> { context.Player, context.Observer }, payload);
            if (inputOnly)
            {
                transaction.Commit();
                return;
            }
            InsertConversationTurn(connection, context.Unit + "_npc", context.Session, context.Unit, order, context.Unit, "npc",
                context.Observer, speakerName, reply, context.Channel, day, now, location, context.Audience, payload);
            BindAcceptedInputSource(connection, payload, context.Session, context.Unit,
                new[] { context.Unit + "_player", context.Unit + "_private_player", context.Unit + "_npc" });
            // A replay must retain the accepted emotion/intent and verification
            // receipt. Storing just prose loses those fields on reconnect.
            var accepted = TryParseJsonObject(ReadString(context.Result, "content", ""));
            var replayFields = TestDict("reply", reply);
            foreach (string field in new[] { "emotion", "intent", "repairStatus", "conversationRepair", "dialogueClaimValidation" })
                if (accepted.TryGetValue(field, out object value)) replayFields[field] = value;
            var replayResult = TestDict("ok", true, "content", Json.Serialize(replayFields));
            foreach (string field in new[] { "visibleRepairFailure", "technicalFailureClass", "conversationRecovery" })
                if (context.Result.TryGetValue(field, out object value)) replayResult[field] = value;
            string replayJson = Json.Serialize(replayResult);
            if (replayJson.Length > 65536)
                throw new InvalidOperationException("The complete route acceptance receipt exceeded its storage bound.");
            ExecuteSql(connection, @"INSERT INTO conversation_route_acceptance(unit_id,input_hash,accepted_reply_hash,result_json,restore_generation,created_ts)
VALUES($unit,$input,$reply,$result,$generation,$now) ON CONFLICT(unit_id) DO NOTHING;",
                TestDict("unit", context.Unit, "input", context.InputHash, "reply", PromptHash(reply),
                    "result", replayJson, "generation", context.Generation, "now", now));
            var stored = QuerySql(connection, "SELECT input_hash,accepted_reply_hash FROM conversation_route_acceptance WHERE unit_id=$unit;", TestDict("unit", context.Unit)).Single();
            if (ReadString(stored, "input_hash", "") != context.InputHash || ReadString(stored, "accepted_reply_hash", "") != PromptHash(reply))
                throw new InvalidOperationException("Another accepted response already owns this route turn.");
            boundary?.Invoke("after_sources");
            if (!ReadBool(payload, "conversationGroundingFailed", false))
            {
                writeRoute(connection);
                if (ReadDictionary(payload, "conversationSceneState") != null)
                    FinalizeConversationSceneState(context.Campaign, payload, context.Observer, ReadString(payload, "playerText", ""),
                        reply, context.Channel, accepted, connection, context.Audience);
            }
            boundary?.Invoke("after_route_write");
            ExecuteSql(connection, @"UPDATE conversation_sessions SET end_ts=$now,end_world_day=$day,
status=CASE WHEN $phase='closing' THEN 'closed' ELSE status END WHERE session_id=$session;",
                TestDict("now", now, "day", day, "phase", ReadString(payload, "phase", ""), "session", context.Session));
            transaction.Commit();
        }

        private static Dictionary<string, object> CompleteRouteConversationDelivery(RouteConversationMemory context, Dictionary<string, object> response)
        {
            var parsed = TryParseJsonObject(ReadString(context.Result, "content", "{}"));
            if (parsed.TryGetValue("repairStatus", out var status)) response["repairStatus"] = status;
            if (context.Payload.TryGetValue("conversationGrounding", out var grounding)) response["conversationGrounding"] = grounding;
            CompleteConversationInputDelivery(context.Campaign, context.Payload, context.Observer, response);
            return response;
        }
    }
}
