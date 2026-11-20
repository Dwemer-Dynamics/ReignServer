using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        internal delegate Dictionary<string, object> ConversationSourceAcceptance(
            ReignDbConnection connection, Dictionary<string, object> source);

        internal const string OllamaDelimitedSpeechContract = "ollama_delimited_speech";

        // Only read the disposition issued with the server's accepted source, including on replay.
        internal static bool IsDelimitedSpeechOnlyResponse(Dictionary<string, object> response) =>
            ReadString(ReadDictionary(ReadDictionary(response, "conversationExchange"), "acceptedUnit"), "outputContract", "") == OllamaDelimitedSpeechContract;

        internal sealed class AcceptedConversationUnit
        {
            internal string Campaign, Hero, Player, Location, EventId, EventType, PlayerText, Reply, Generation;
            internal long Timestamp;
            internal bool CommitEffects = true, RetrievalEligible = true, GroupConversation, PrivateWhisper;
            internal bool DelimitedSpeechOnly;
            internal bool PublishScene, PublishSocial, PublishIntroductions;
            // Only ordinary individual /dialogue/respond sets this, from an exact client opt-in.
            internal bool ViewInsertionRequested;
            internal string SceneMode;
            internal Dictionary<string, object> ConceptionGate;
            internal string PlayerName, HeroName;
            internal Dictionary<string, object> Payload, Motive, Profile, Characteristics, Parsed;
            internal List<string> Participants;
            internal List<Dictionary<string, object>> Memories, Beliefs, Relationships, Obligations, Comprehensions, Continuity;
        }

        internal static string AcceptedConversationEventId(string campaign, string hero, Dictionary<string, object> payload) =>
            "accepted_" + PromptHash(string.Join("|", campaign, ContinuityTimeline(payload), hero,
                ReadFirstString(payload, "conversationSessionId", "sessionId", "conversationId"),
                FirstNonEmpty(ReadFirstString(payload, "sceneTurnId", "turnId", "requestId"), OperationsService.EnsureCorrelationId(payload)))).Substring(0, 32);

        internal static void EnsureAcceptedConversationUnitSchema(ReignDbConnection connection) => DialogueSchema.CreateConversationAcceptedUnits(connection);

        // All operations inside the source callback use that SAME connection and
        // transaction. File projections, social incidents and native execution
        // have separate owners; this receipt never claims distributed atomicity.
        internal static Dictionary<string, object> PublishAcceptedConversationUnit(AcceptedConversationUnit unit,
            Func<ConversationSourceAcceptance, Dictionary<string, object>> publishSources, Action<string> boundary = null)
        {
            if (string.IsNullOrWhiteSpace(unit.Generation))
                throw new InvalidOperationException("Conversation publication requires its pre-generation restore boundary.");
            var finalOpening = CloneDictionary(unit.Parsed ?? TestDict()); finalOpening["reply"] = unit.Reply;
            bool groundingFailed = ReadBool(unit.Payload, "conversationGroundingFailed", false);
            bool intentionalSilence = (unit.GroupConversation || unit.EventType == "social_event_turn")
                && IsIntentionalConversationSilence(unit.Parsed) && string.IsNullOrWhiteSpace(unit.Reply);
            // Recheck exact accepted bytes after every rewrite; never publish a stale
            // successful receipt for a different final response.
            if (!IsSinglePassConversation(unit.Payload) && !groundingFailed && FindCriticalConversationContinuityViolations(finalOpening, unit.Payload, unit.Hero).Count > 0)
                throw new InvalidOperationException("The final response changed after its grounding check; the original contribution remains saved.");
            if (!IsSinglePassConversation(unit.Payload) && RelationshipsService.ApproachOpeningViolations(finalOpening, unit.Payload).Count > 0)
                throw new InvalidOperationException("The final spoken approach no longer matches its saved opening evidence.");
            lock (RelationshipsService.CampaignRelationshipWriteLock(unit.Campaign))
            {
                using (var setup = ReignDatabase.OpenCampaignConnection(unit.Campaign)) EnsureAcceptedConversationUnitSchema(setup);
                var exchange = publishSources((connection, source) =>
                {
                    var sourceRows = ReadStringList(source, "turnIds").SelectMany(id => DialogueStore.SelectConversationTurns6(connection, TestDict("id", id))).ToList();
                    source["turnIds"] = sourceRows.Select(row => ReadString(row, "turn_id", "")).ToList();
                    BindAcceptedInputSource(connection, unit.Payload, ReadString(source, "sessionId", ""), unit.EventId,
                        ReadStringList(source, "turnIds"));
                    string generation = ReadString(DialogueStore.SelectMemoryPrecisionState(connection).Single(), "restore_generation", "");
                    if (generation != unit.Generation)
                        throw new InvalidOperationException("The campaign changed before the conversation could be accepted.");
                    var responseIdentity = TestDict("reply", unit.Reply, "playerText", unit.PlayerText,
                        "hero", unit.Hero, "player", unit.Player, "timeline", ContinuityTimeline(unit.Payload),
                        "sourceIds", ReadStringList(source, "turnIds"));
                    if (unit.DelimitedSpeechOnly) responseIdentity["outputContract"] = OllamaDelimitedSpeechContract;
                    string hash = PromptHash(CanonicalJson(responseIdentity));
                    var previous = DialogueStore.SelectConversationAcceptedUnits2(connection, TestDict("unit", unit.EventId)).SingleOrDefault();
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
                    var effectPayload = unit.DelimitedSpeechOnly ? TestDict() : CloneDictionary(unit.Payload);
                    if (ReadString(ReadDictionary(unit.Payload, "npcLeaveResponseGuard"), "repair", "") == "bounded_local")
                    {
                        unit.Memories = unit.Beliefs = unit.Relationships = unit.Obligations = unit.Comprehensions = unit.Continuity = new List<Dictionary<string, object>>();
                        unit.PublishScene = unit.PublishSocial = unit.PublishIntroductions = false;
                    }
                    CopyPlayerInputSource(unit.Payload, effectPayload);
                    var effectParsed = CloneDictionary(unit.Parsed);
                    bool technicalNotice = IsTechnicalConversationNotice(unit.Reply) || groundingFailed;
                    bool suppressReplyEffects = technicalNotice || intentionalSilence || unit.DelimitedSpeechOnly;
                    string excludedStatus = unit.DelimitedSpeechOnly ? "speech_only_excluded"
                        : intentionalSilence ? "intentional_silence_excluded" : "technical_notice_excluded";
                    var effects = unit.CommitEffects && !suppressReplyEffects
                        ? CommitFinalConversationEffects(unit.Campaign, unit.Hero, effectPayload, unit.Motive,
                            unit.Profile, unit.Characteristics, effectParsed, unit.Reply, unit.Generation, existingConnection: connection)
                        : TestDict("ok", true, "status", "suppressed_by_route", "scope", "negotiation_and_drinking");
                    if (!ReadBool(effects, "ok", false)) throw new InvalidOperationException("Conversation effects were not accepted.");
                    boundary?.Invoke("after_effects");
                    string session = ReadString(source, "sessionId", "");
                    var audience = unit.PrivateWhisper ? new[] { unit.Hero, unit.Player }
                        : DialogueService.TextListFromJson(ReadString(sourceRows.LastOrDefault(row => ReadString(row, "speaker_id", "") == unit.Hero),
                            "participants_json", "[]")).ToArray();
                    var scene = unit.PublishScene && !suppressReplyEffects
                        ? FinalizeConversationSceneState(unit.Campaign, unit.Payload, unit.Hero, unit.PlayerText,
                            unit.Reply, unit.SceneMode ?? unit.EventType, unit.Parsed, connection, audience)
                        : TestDict("status", "suppressed_by_route");
                    var viewWardrobe = unit.PublishScene && !suppressReplyEffects && !unit.PrivateWhisper
                        ? PortraitsService.StoreChatViewWardrobeEvidence(connection, unit.Payload, unit.Parsed, sourceRows, unit.Hero)
                        : TestDict("status", "suppressed_by_route");
                    var social = unit.PublishSocial && !suppressReplyEffects
                        ? ReputationService.NormalizeAndPersistConversationSocialSignals(unit.Campaign, unit.Parsed, unit.Payload,
                            unit.Hero, unit.Player, unit.PlayerText, unit.Reply, unit.ConceptionGate,
                            unit.EventId, ReadDouble(unit.Payload, "worldDay", 0d), existingConnection: connection)
                        : TestDict("status", "suppressed_by_route", "signals", new List<Dictionary<string, object>>());
                    var introductions = unit.PublishIntroductions && !suppressReplyEffects
                        ? CharactersService.ApplyIdentityIntroductionWrites(unit.Campaign, unit.Payload, unit.Parsed, session,
                            unit.Hero, unit.Player, unit.PlayerText, unit.Reply, audience, connection)
                        : new List<Dictionary<string, object>>();
                    if (!suppressReplyEffects)
                        RelationshipsService.AcceptPregnancyDisclosures(connection, ContinuityTimeline(unit.Payload), unit.Hero, unit.Reply, unit.Parsed,
                            audience, ReadString(sourceRows.LastOrDefault(row => ReadString(row, "speaker_id", "") == unit.Hero), "turn_id", ""),
                            ReadDouble(unit.Payload, "worldDay", 0d));
                    boundary?.Invoke("after_context_effects");
                    string group = unit.GroupConversation || unit.EventType == "dialogue_turn"
                        ? "conversation:" + session
                        : DialogueService.BuildMemoryGroupKey(unit.Payload, unit.Hero, unit.Player, unit.Location, "social_event", unit.Timestamp);
                    var personalState = CharactersService.ReadNpcLives(connection, unit.Campaign, ContinuityTimeline(unit.Payload));
                    bool personalMatter = CharactersService.NpcLivesOwnsRelationshipReward(personalState, unit.Hero, unit.Payload);
                    if (personalMatter) unit.Relationships = new List<Dictionary<string, object>>();
                    // A quiet turn preserves the player's sources and an idempotent
                    // receipt, but supplies no NPC speech or attention evidence.
                    var memory = suppressReplyEffects ? TestDict("status", excludedStatus, "continuity", TestDict("status", "complete"))
                        : DialogueService.StoreTurnMemoryLayers(unit.Campaign, unit.EventId, unit.EventType,
                        unit.Hero, unit.Player, unit.Location, unit.PlayerText, unit.Reply,
                        (unit.Memories ?? new List<Dictionary<string, object>>()).Select(CloneDictionary).ToList(),
                        unit.Beliefs, unit.Relationships, unit.Obligations, unit.Comprehensions, group, unit.Timestamp,
                        unit.Participants, unit.RetrievalEligible, unit.Payload, unit.Continuity, connection);
                    boundary?.Invoke("after_memory");
                    var attention = suppressReplyEffects ? TestDict("status", excludedStatus)
                        : RelationshipsService.RecordAcceptedPlayerAttention(connection, unit, sourceRows);
                    var npcLives = suppressReplyEffects ? TestDict("status", excludedStatus)
                        : CharactersService.RecordAcceptedNpcLives(connection, unit, sourceRows);
                    var communications = suppressReplyEffects ? TestDict("status", excludedStatus)
                        : AcceptCommunicationCommitments(connection, unit, source, sourceRows.Concat(
                            ReadStringList(privateExchange, "turnIds").SelectMany(id => DialogueStore.SelectConversationTurns6(connection, TestDict("id", id)))).ToList());
                    var references = StoreAcceptedConversationReferences(connection, unit, sourceRows.Concat(
                        ReadStringList(privateExchange, "turnIds").SelectMany(id => DialogueStore.SelectConversationTurns6(connection, TestDict("id", id)))).ToList(),
                        suppressReplyEffects || ReadString(ReadDictionary(unit.Payload, "npcLeaveResponseGuard"), "repair", "") == "bounded_local");
                    boundary?.Invoke("after_reference_graph");
                    bool memoryComplete = !technicalNotice
                        && ReadString(ReadDictionary(memory, "continuity"), "status", "complete") == "complete"
                        && ReadString(memory, "sourceEvidenceStatus", "complete") == "complete";
                    var receipt = TestDict("ok", true, "status", memoryComplete ? "committed" : "committed_memory_incomplete", "unitId", unit.EventId,
                        "memoryUpdateComplete", memoryComplete,
                        "scope", "transcript_memory_negotiation_drinking_scene_social_evidence_identity", "responseHash", hash,
                        "acceptedReplyHash", PromptHash(unit.Reply ?? ""), "restoreGeneration", generation,
                        "memory", memory, "effects", effects, "privateExchange", privateExchange,
                        "personalRelationshipOwner", personalMatter ? "npc_lives" : "",
                        "scene", scene, "viewWardrobe", viewWardrobe, "social", social, "introductions", introductions, "playerAttention", attention, "npcLives", npcLives,
                        "communications", communications, "references", references,
                        "sourceIds", ReadStringList(source, "turnIds").Concat(ReadStringList(privateExchange, "turnIds")).ToArray(),
                        "effectPayload", TestDict("conversationAgencyReceipt", ReadDictionary(effectPayload, "conversationAgencyReceipt"),
                            "intoxicationReceipt", ReadDictionary(effectPayload, "intoxicationReceipt")));
                    boundary?.Invoke("before_unit_receipt");
                    if (unit.DelimitedSpeechOnly) receipt["outputContract"] = OllamaDelimitedSpeechContract;
                    receipt["politicalReflection"] = CharactersService.AcceptedPoliticalReflection(unit, suppressReplyEffects);
                    // Optional, non-gating: binds the one exact displayed NPC source row.
                    var viewInsertion = unit.ViewInsertionRequested && !technicalNotice && !intentionalSilence && !unit.GroupConversation
                        && !unit.PrivateWhisper && unit.EventType == "dialogue_turn"
                        ? IndividualViewInsertionDescriptor(unit, session, generation, sourceRows) : null;
                    if (viewInsertion != null) receipt["viewInsertion"] = viewInsertion;
                    DialogueStore.InsertConversationAcceptedUnits(connection, TestDict("unit", unit.EventId, "hash", hash,
                        "generation", generation, "receipt", Json.Serialize(receipt), "ts", unit.Timestamp));
                    SaveConversationInputAcceptance(connection, unit, source, receipt);
                    return receipt;
                });
                var accepted = ReadDictionary(exchange, "acceptedUnit");
                if (!ReadBool(exchange, "ok", false) || !ReadBool(accepted, "ok", false))
                    throw new InvalidOperationException("The conversation's source publication did not accept its dependent records.");
                var effectReceipt = ReadDictionary(accepted, "effects");
                if (unit.DelimitedSpeechOnly)
                {
                    unit.Payload.Remove("conversationAgencyReceipt");
                    unit.Payload.Remove("intoxicationReceipt");
                    unit.Payload.Remove("validatedStructuredActionDecision");
                }
                foreach (var item in ReadDictionary(accepted, "effectPayload") ?? TestDict())
                    if (item.Value is Dictionary<string, object> value && value.Count > 0) unit.Payload[item.Key] = CloneDictionary(value);
                if (!unit.DelimitedSpeechOnly && unit.Parsed != null && ReadDictionary(unit.Payload, "conversationAgencyReceipt") is { } agency && agency.Count > 0)
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
            DialogueStore.InsertOrIgnoreConversationSessions2(connection, TestDict("session", session, "campaign", unit.Campaign, "hero", unit.Hero, "player", unit.Player,
                    "ts", unit.Timestamp, "day", day, "location", unit.Location, "audience", Json.Serialize(audience),
                    "payload", SerializeConversationTurnStoragePayload(request)));
            string playerTurn = exchange + "_player", npcTurn = exchange + "_npc_" + SafeMemoryKey(unit.Hero);
            PreservePlayerInputSource(connection, request, session, unit.EventId, exchange, unit.Player, unit.PlayerName, day, unit.Timestamp, unit.Location);
            InsertConversationTurn(connection, playerTurn, session, unit.EventId, 0, exchange, "player", unit.Player,
                unit.PlayerName, unit.PlayerText, "private_whisper", day, unit.Timestamp, unit.Location, audience, request);
            InsertConversationTurn(connection, npcTurn, session, unit.EventId, 1, exchange, "npc", unit.Hero,
                unit.HeroName, unit.Reply, "private_whisper", day, unit.Timestamp, unit.Location, audience, request);
            return TestDict("ok", true, "sessionId", session, "exchangeId", exchange, "turnIds",
                new[] { playerTurn, npcTurn }.Where(id => DialogueStore.SelectConversationTurns8(connection, TestDict("id", id)).Count > 0).ToArray());
        }

        // ---- Individual chat view-insertion receipt v1. ----
        // Attests only that the client inserted the accepted NPC line into the matching
        // current individual chat view. It never attests render, audio or observation,
        // and never gates source acceptance or domain effects.
        private const string IndividualViewInsertionSchema = "reign-individual-view-insertion-v1";
        private const string IndividualViewInsertionAckSchema = "reign-individual-view-insertion-ack-v1";
        private const string IndividualViewInsertionResultSchema = "reign-individual-view-insertion-result-v1";
        internal const string IndividualViewInsertionRoute = "/conversation/view-insertion";
        private const string IndividualViewInsertionOptInField = "viewInsertionReceipt";
        private static readonly string[] IndividualViewInsertionIdentityFields = { "campaignId", "timelineId", "unitId",
            "sessionId", "sourceTurnId", "role", "speakerHeroStringId", "playerHeroStringId", "restoreGeneration", "textSha256" };

        internal static bool IndividualViewInsertionRequested(Dictionary<string, object> payload) =>
            payload != null && payload.TryGetValue(IndividualViewInsertionOptInField, out object value)
            && value is string text && text == IndividualViewInsertionSchema;

        private static bool ViewInsertionIdentifier(string value) =>
            Reign.Core.Contracts.Actions.TerminalActionReportProtocol.IsTrustedIdentifier(value);

        // Uses rows already read by the publication transaction; unknown is null, never a guess.
        private static Dictionary<string, object> IndividualViewInsertionDescriptor(AcceptedConversationUnit unit,
            string session, string generation, List<Dictionary<string, object>> sourceRows)
        {
            string reply = unit.Reply ?? "", timeline = ContinuityTimeline(unit.Payload);
            if (reply.Length == 0 || IsTechnicalConversationNotice(reply)) return null;
            var matches = (sourceRows ?? new List<Dictionary<string, object>>()).Where(row => ReadString(row, "role", "") == "npc"
                && ReadString(row, "speaker_id", "") == unit.Hero).ToList();
            if (matches.Count != 1) return null;
            var row = matches[0];
            var audience = DialogueService.TextListFromJson(ReadString(row, "participants_json", "[]"));
            string turn = ReadString(row, "turn_id", "");
            if (ReadString(row, "text", "") != reply || ReadString(row, "status", "") != "active"
                || ReadString(row, "session_id", "") != session || ReadString(row, "event_id", "") != unit.EventId
                || ReadString(row, "channel", "") == "private_whisper"
                || ContinuityTimeline(TryParseJsonObject(ReadString(row, "payload_json", "{}"))) != timeline
                || !audience.Contains(unit.Hero, StringComparer.Ordinal) || !audience.Contains(unit.Player, StringComparer.Ordinal)
                || !new[] { unit.Campaign, timeline, unit.EventId, session, turn, unit.Hero, unit.Player, generation }.All(ViewInsertionIdentifier))
                return null;
            return TestDict("schema", IndividualViewInsertionSchema, "status", "eligible",
                "campaignId", unit.Campaign, "timelineId", timeline, "unitId", unit.EventId, "sessionId", session,
                "sourceTurnId", turn, "role", "npc", "speakerHeroStringId", unit.Hero, "playerHeroStringId", unit.Player,
                "audience", audience, "restoreGeneration", generation, "textSha256", ConversationExactTextHash(reply));
        }

        // POST /conversation/view-insertion. Records evidence on the existing accepted
        // unit receipt only; publishes no effects and never regenerates or replays a reply.
        internal static Dictionary<string, object> IndividualViewInsertionApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string Exact(string key) => payload.TryGetValue(key, out object value) && value is string text ? text : "";
            var ack = IndividualViewInsertionIdentityFields.ToDictionary(field => field, Exact, StringComparer.Ordinal);
            Dictionary<string, object> Reply(string disposition, string reason, Dictionary<string, object> insertion = null) =>
                TestDict("ok", true, "schema", IndividualViewInsertionResultSchema, "disposition", disposition, "reason", reason,
                    "campaignId", ack["campaignId"], "unitId", ack["unitId"], "sourceTurnId", ack["sourceTurnId"],
                    "restoreGeneration", ack["restoreGeneration"], "textSha256", ack["textSha256"],
                    "acknowledgedTs", insertion == null ? 0L : ReadLong(insertion, "acknowledgedTs", 0));
            if (Exact("schema") != IndividualViewInsertionAckSchema || payload.Count != IndividualViewInsertionIdentityFields.Length + 1
                || !payload.Keys.All(key => key == "schema" || IndividualViewInsertionIdentityFields.Contains(key))
                || ack["role"] != "npc" || !Reign.Core.Contracts.Actions.TerminalActionReportProtocol.IsSha256Hex(ack["textSha256"])
                || !ack.Where(pair => pair.Key != "role" && pair.Key != "textSha256").All(pair => ViewInsertionIdentifier(pair.Value)))
                return TestDict("ok", false, "schema", IndividualViewInsertionResultSchema, "disposition", "invalid",
                    "error", "The view insertion acknowledgment is invalid.");
            string campaign = ack["campaignId"];
            // Never uses OpenCampaignConnection: that helper creates directories and runs cold
            // campaign initialization/migration. The registry answers unknown campaigns; a known
            // schema is read through a metadata connection with a transaction-local search_path.
            // No table or schema is created; the only write is the validated receipt update.
            if (!ReignPostgreSqlStorage.CampaignExists(campaign)) return Reply("unknown", "unknown_campaign");
            string schema = ReignPostgreSqlStorage.CampaignSchemaName(campaign);
            using var connection = ReignPostgreSqlStorage.OpenMetadataConnection();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
            // First statement of the transaction; disposal rolls back and restores search_path.
            DialogueStore.SelectStatement(connection, TestDict("path", "\"" + schema + "\", reign_meta, public"));
            // Absent prerequisites mean no descriptor can exist; an acknowledgment never creates them.
            if (!new[] { "conversation_turns", "memory_precision_state", "conversation_accepted_units" }.All(table => WorldSimulationService.TableExists(connection, table)))
                return Reply("unknown", "no_view_insertion_descriptor");
            // A publisher takes this session lock first, and its duplicate insert-or-ignore can
            // still bump the restore fence by statement trigger before it rewrites existing source
            // rows, so it may hold the fence while waiting on this turn. Taking the same session
            // lock first serializes with it; the exact-replay shortcut runs only after both row
            // locks, so it cannot avoid the inversion. Then source turn before fence, as the
            // direct editor update trigger orders them; the share lock is held through commit.
            LockConversationSourceSession(connection, ack["sessionId"]);
            var source = DialogueStore.SelectConversationTurns14(connection, TestDict("id", ack["sourceTurnId"], "session", ack["sessionId"])).FirstOrDefault();
            string current = ReadString(DialogueStore.SelectMemoryPrecisionState(connection).FirstOrDefault(), "restore_generation", "");
            var stored = DialogueStore.SelectConversationAcceptedUnits3(connection, TestDict("unit", ack["unitId"])).FirstOrDefault();
            if (stored == null) return Reply("unknown", "unknown_unit");
            var receipt = TryParseJsonObject(ReadString(stored, "receipt_json", "{}"));
            var descriptor = ReadDictionary(receipt, "viewInsertion");
            // Old receipts, other routes and ineligible replies carry no positive evidence.
            if (descriptor == null || ReadString(descriptor, "schema", "") != IndividualViewInsertionSchema)
                return Reply("unknown", "no_view_insertion_descriptor");
            if (IndividualViewInsertionIdentityFields.Any(field => ReadString(descriptor, field, "") != ack[field])
                || ReadString(stored, "restore_generation", "") != ack["restoreGeneration"]
                || ReadString(receipt, "restoreGeneration", "") != ack["restoreGeneration"])
                return Reply("conflict", "identity_mismatch");
            if (current != ack["restoreGeneration"]) return Reply("stale", "restore_generation_changed");
            var audience = DialogueService.TextListFromJson(ReadString(source, "participants_json", "[]"));
            if (source == null || ReadString(source, "role", "") != "npc" || ReadString(source, "status", "") != "active"
                || ReadString(source, "speaker_id", "") != ack["speakerHeroStringId"]
                || ReadString(source, "session_id", "") != ack["sessionId"] || ReadString(source, "event_id", "") != ack["unitId"]
                || ReadString(source, "channel", "") == "private_whisper"
                || ConversationExactTextHash(ReadString(source, "text", "")) != ack["textSha256"]
                || ContinuityTimeline(TryParseJsonObject(ReadString(source, "payload_json", "{}"))) != ack["timelineId"]
                || !new HashSet<string>(audience, StringComparer.Ordinal).SetEquals(ReadStringList(descriptor, "audience"))
                || !audience.Contains(ack["playerHeroStringId"], StringComparer.Ordinal))
                return Reply("conflict", "source_mismatch");
            // Exact repeat is a read: the first timestamp is never overwritten.
            if (ReadDictionary(descriptor, "insertion") is { } prior) return Reply("inserted", "exact_replay", prior);
            var insertion = TestDict("status", "inserted_into_current_view", "scope", "individual_view_insertion_only",
                "acknowledgedTs", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            descriptor["insertion"] = insertion;
            receipt["viewInsertion"] = descriptor;
            DialogueStore.UpdateConversationAcceptedUnits(connection, TestDict("receipt", Json.Serialize(receipt), "unit", ack["unitId"], "generation", ack["restoreGeneration"]));
            transaction.Commit();
            return Reply("inserted", "recorded", insertion);
        }
    }
}
