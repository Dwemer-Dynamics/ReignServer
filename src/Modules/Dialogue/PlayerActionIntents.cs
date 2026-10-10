using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ReignBeta.Shared.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        [ThreadStatic] private static Dictionary<string, object> _playerActionTurn;
        [ThreadStatic] private static List<Dictionary<string, object>> _stagedTurnActions;
        [ThreadStatic] private static string _playerActionGeneration;
        private const string PlayerActionPrompt = "PLAYER ACTIONS AND RECEIPTS: Current player-authored handovers, including *stage directions*, are independent of your employment or travel decision. Never invent the player's action or treat an earlier claim as a new handover. Gold/item transfers are attempts until native execution confirms them. Do not narrate taking/counting/pocketing coins or owning an item without an applied nativePaymentReceipts entry. A recruitment advance does not move you out of town or make you a companion. Discuss a delayed departure explicitly; permanent companion consent is allegiance, temporary travel is travel. Quote exact fact keys, without '=value'.\n";

        private static bool SinglePlayerTransferRecipient(Dictionary<string, object> payload)
        {
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "attendees", "participants", "sceneParticipants", "activeHeroIds" })
            {
                foreach (var row in ReadDictionaryList(payload, key)) ids.Add(CharacterIdFrom(row));
                if (payload.TryGetValue(key, out object raw) && raw is System.Collections.IEnumerable items && !(raw is string))
                    foreach (object item in items) if (item is string id) ids.Add(id);
            }
            ids.Remove(""); ids.Remove(player);
            return ids.Count <= 1;
        }

        private static List<PlayerTransferIntent> CurrentPlayerTransfers(Dictionary<string, object> payload)
        {
            // These intents are native gold/item handovers. Non-native social gifts
            // remain in the ordinary social/memory pipeline, outside this executor.
            if (UsesStructuredActions(payload)) return new List<PlayerTransferIntent>();
            string channel = ReadString(payload, "channel", "in_person");
            if (channel == "letter" || channel == "correspondence" || ReadBool(payload, "guardedPromptOverride", false)) return new List<PlayerTransferIntent>();
            var hero = ReadDictionary(payload, "hero") ?? ReadDictionary(payload, "speaker") ?? new Dictionary<string, object>();
            return PlayerActionRules.Extract(ReadFirstString(payload, "playerText", "text", "message"),
                ReadFirstString(hero, "name", "heroName"), SinglePlayerTransferRecipient(payload))
                .Where(intent => !Regex.IsMatch(intent.Asset,
                    @"^(?:a|an|the|my)\s+(?:[\p{L}'-]+\s+){0,3}(?:smile|grin|nod|look|glance|sigh|laugh|chuckle|shrug|wink|hug|embrace|handshake|kiss|word)\b(?:\s+(?:back|so|as|while|when|because|that|to)\b.*)?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        }

        private static void PreparePlayerActionPrompt(Dictionary<string, object> payload)
        {
            string context = (UsesStructuredActions(payload) ? StructuredActionPrompt(payload) : PlayerActionPrompt)
                + "NATIVE PAYMENT RECEIPTS (only applied is a completed payment): "
                + Json.Serialize(ReadDictionaryList(payload, "nativePaymentReceipts"))
                + "\nNATIVE ACTION OUTCOMES: " + Json.Serialize(ReadDictionaryList(payload, "nativeActionReceipts"))
                + "\nMemory/belief/continuity writes must distinguish offered, accepted, pending, failed and fulfilled. Only applied native receipts establish fulfilled payment or membership.";
            var scene = ReadDictionary(payload, "sceneContext");
            if (UsesStructuredActions(payload))
            {
                // Keep gameplay mechanics out of scene/topic retrieval and motive
                // selection. Only the final writer template receives this block.
                payload["structuredActionPromptBlock"] = context;
                if (scene != null) payload["sceneContext"] = Json.Serialize(scene);
            }
            else if (scene != null) scene["playerActionContext"] = context;
            else payload["sceneContext"] = ReadString(payload, "sceneContext", "") + "\n" + context;
        }

        private static Dictionary<string, object> RunPlayerActionTurn(Dictionary<string, object> payload, Func<Dictionary<string, object>> respond)
        {
            string key = ReadString(payload, "campaignId", "default") + "|" + ContinuityTimeline(payload) + "|"
                + FirstNonEmpty(ReadFirstString(payload, "contributionId", "sceneTurnId", "turnId"), EnsureCorrelationId(payload)) + "|"
                + ReadFirstString(payload, "speakerHeroStringId", "heroStringId");
            lock (ConversationInputDispatchLocks[(uint)StringComparer.Ordinal.GetHashCode(key) % ConversationInputDispatchLocks.Length])
                return RunPlayerActionTurnCore(payload, respond);
        }

        private static readonly object[] ConversationInputDispatchLocks = Enumerable.Range(0, 128).Select(_ => new object()).ToArray();

        private static Dictionary<string, object> RunPlayerActionTurnCore(Dictionary<string, object> payload, Func<Dictionary<string, object>> respond)
        {
            CapturePlayerInput(payload);
            var intents = CurrentPlayerTransfers(payload);
            if (_playerActionTurn != null) return respond();
            PreparePlayerActionPrompt(payload);
            string campaign = ReadString(payload, "campaignId", "default");
            string publicationGeneration;
            using (var connection = OpenCampaignConnection(campaign))
                publicationGeneration = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            string inputObserver = FirstNonEmpty(ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                CharacterIdFrom(ReadDictionary(payload, "hero") ?? ReadDictionary(payload, "speaker") ?? TestDict()));
            AdmitConversationInput(campaign, payload, publicationGeneration, inputObserver);
            var inputReplay = ReadConversationInputReplay(campaign, payload, inputObserver);
            if (inputReplay != null) return inputReplay;
            var outcomes = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> playerRows;
            var staged = new List<Dictionary<string, object>>();
            Dictionary<string, object> response;
            _playerActionTurn = payload; _stagedTurnActions = staged; _playerActionGeneration = publicationGeneration;
            try
            {
                playerRows = QueuePlayerTransfers(campaign, payload, intents, outcomes);
                try
                {
                    response = respond();
                    if (UsesStructuredActions(payload))
                        response["structuredActionDecision"] = ReadDictionary(payload, "validatedStructuredActionDecision")
                            ?? TestDict("version", 1, "kind", "none", "valid", false, "error", "No explicit action decision was returned.");
                    var recruited = !ReadBool(payload, "conversationGroundingFailed", false) && !UsesStructuredActions(payload) && ReadBool(response, "ok", false)
                        ? QueueExplicitResidentCompanion(payload, response) : null;
                    if (recruited != null)
                    {
                        var ordinaryRows = ReadDictionaryList(response, "queuedDialogueActions");
                        ordinaryRows.Add(recruited); response["queuedDialogueActions"] = ordinaryRows;
                    }
                }
                catch (Exception ex) when (intents.Count > 0 && !(ex is CampaignRequestReplacedException) && !(ex is OperationCanceledException))
                { LogOperational("dialogue.player_action_reply_failed", TestDict("correlationId", EnsureCorrelationId(payload), "error", ex.GetType().Name));
                    response = TestDict("ok", false, "error", "The NPC response failed. Your explicit handover is evaluated separately."); }
            }
            finally { _playerActionTurn = null; _stagedTurnActions = null; _playerActionGeneration = null; }
            // A separately authorized player handover can survive an NPC failure.
            // NPC-owned actions cannot: the failed response never accepted them.
            if (!ReadBool(response, "ok", false) || ReadBool(payload, "conversationGroundingFailed", false))
            {
                var independent = new HashSet<string>(playerRows.Select(r => ReadString(r, "id", "")), StringComparer.Ordinal);
                staged.RemoveAll(row => !independent.Contains(ReadString(row, "id", "")));
                foreach (string key in new[] { "queuedDialogueActions", "queuedActions", "queuedEventActions" })
                    if (response.ContainsKey(key)) response[key] = new List<Dictionary<string, object>>();
            }
            string refusal = ReadString(response, "reply", "");
            if (Regex.IsMatch(refusal, @"\bi (?:will not|won't|cannot|can't|refuse to|do not|don't) (?:accept|take|keep) (?:your |the |this )?(?:gold|coins|money|payment|gift|item)\b", RegexOptions.IgnoreCase))
            {
                foreach (var row in playerRows.Where(r => ReadString(r, "status", "") == "queued").ToList())
                {
                    row["status"] = "rejected";
                    row["report"] = TestDict("message", "The recipient declined this handover; no transfer was made.");
                    SaveDurableConversationAction(campaign, row);
                    if (ReadString(row, "status", "") != "rejected") continue;
                    staged.RemoveAll(r => ReadString(r, "id", "") == ReadString(row, "id", ""));
                    foreach (var dependent in staged.Where(r => ReadStringList(TryParseJsonObject(ReadString(ReadDictionary(r, "record"), "termsJson", "{}")), "paymentDependencies").Contains(ReadString(row, "id", ""))).ToList())
                    {
                        dependent["status"] = "rejected";
                        dependent["report"] = TestDict("message", "The required recruitment advance was declined.");
                        SaveDurableConversationAction(campaign, dependent); staged.Remove(dependent);
                    }
                    playerRows.Remove(row);
                    var outcome = outcomes.FirstOrDefault(o => ReadString(o, "intentId", "") == ReadString(row, "id", ""));
                    if (outcome != null) { outcome["status"] = "unresolved"; outcome["message"] = "The recipient declined this handover; no transfer was made."; }
                }
            }
            // The durable batch becomes visible to polling in one database commit.
            // A cache/audit projection is never the authority for native execution.
            try
            {
                response["actionPublication"] = PublishConversationActionBatch(campaign, payload, response, staged, publicationGeneration);
            }
            catch (Exception ex) when (!(ex is CampaignRequestReplacedException) && !(ex is OperationCanceledException))
            {
                LogOperational("dialogue.action_publication_failed", TestDict("campaignId", campaign,
                    "correlationId", EnsureCorrelationId(payload), "error", ex.GetType().Name));
                response["ok"] = false;
                response["stage"] = "action_publication";
                response["actionPublication"] = TestDict("status", "failed", "committed", false,
                    "error", "The action batch was not published. Retry the same turn.");
                foreach (string key in new[] { "queuedDialogueActions", "queuedActions", "queuedEventActions" })
                    response[key] = new List<Dictionary<string, object>>();
                response["playerActionOutcomes"] = outcomes.Select(o => TestDict("intentId", ReadString(o, "intentId", ""),
                    "status", "unresolved", "message", "Action publication failed; no new action batch was confirmed.")).ToList();
                CompleteConversationInputDelivery(campaign, payload, inputObserver, response);
                return response;
            }
            foreach (string key in new[] { "queuedDialogueActions", "queuedActions", "queuedEventActions" })
                if (response.ContainsKey(key)) response[key] = ReadDictionaryList(response, key).Where(r =>
                    ReadString(ReadDurableConversationAction(campaign, ReadString(r, "id", "")), "status", "queued") != "rejected").ToList();
            var ordinary = ReadDictionaryList(response, "queuedDialogueActions");
            // Player intents have their own slots; planner alternatives for the same handover
            // may not make a second debit. Recruitment remains a separate action.
            var merged = playerRows.Concat(ordinary).GroupBy(r => ReadString(r, "id", "")).Select(g => g.First()).ToList();
            response["queuedDialogueActions"] = merged;
            response["playerActionOutcomes"] = outcomes;
            response["correlationId"] = EnsureCorrelationId(payload);
            if (intents.Count > 0 && !ReadBool(response, "ok", false))
            {
                response["dialogueSucceeded"] = false;
                response["ok"] = true;
                response["reply"] = "I hear you. Let us keep talking while that is checked.\n\n"
                    + "[The NPC reply could not be completed. Your handover has been processed separately; its result is shown below.]";
            }
            string errors = string.Join(" ", outcomes.Where(o => ReadString(o, "status", "") == "unresolved").Select(o => ReadString(o, "message", "")));
            if (errors.Length > 0) response["reply"] = ReadString(response, "reply", "") + "\n\n[Handover not queued: " + errors + "]";
            CompleteConversationInputDelivery(campaign, payload, inputObserver, response);
            return response;
        }

        private static List<Dictionary<string, object>> QueuePlayerTransfers(string campaign, Dictionary<string, object> payload,
            List<PlayerTransferIntent> intents, List<Dictionary<string, object>> outcomes)
        {
            var rows = new List<Dictionary<string, object>>();
            var hero = ReadDictionary(payload, "hero") ?? ReadDictionary(payload, "speaker") ?? new Dictionary<string, object>();
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            string recipient = FirstNonEmpty(ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"), ReadString(hero, "heroStringId", ""));
            string turn = FirstNonEmpty(ReadFirstString(payload, "sceneTurnId", "turnId", "requestId"), EnsureCorrelationId(payload));
            for (int slot = 0; slot < intents.Count; slot++)
            {
                var intent = intents[slot];
                string id = PlayerActionRules.ActionId(campaign, ContinuityTimeline(payload), turn, slot);
                var result = TestDict("intentId", id, "sourceQuote", intent.Quote, "status", "unresolved", "message", intent.Error);
                outcomes.Add(result);
                if (intent.Error.Length > 0) continue;
                if (player.Length == 0 || recipient.Length == 0 || player == recipient)
                { result["message"] = "The handover requires an exact player and recipient."; continue; }
                var terms = TestDict("fromHeroStringId", player, "toHeroStringId", recipient,
                    "delivery", "personal", "paymentPurpose", intent.Purpose,
                    "playerAction", TestDict("schema", PlayerActionRules.Schema, "turnId", turn, "slot", slot,
                        "singleRecipient", SinglePlayerTransferRecipient(payload),
                        "timelineId", ContinuityTimeline(payload), "campaignId", campaign, "quote", intent.Quote,
                        "playerText", ReadFirstString(payload, "playerText", "text", "message"), "recipientName", ReadFirstString(hero, "name", "heroName")));
                if (intent.Command == "transfer_gold") terms["gold"] = intent.Amount;
                if (intent.Purpose == "recruitment_advance")
                {
                    string agreement = ReadString(ReadDictionary(hero, "encounteredResident"), "recruitmentAgreementId", "");
                    if (agreement.Length == 0) { result["message"] = "The resident's recruitment agreement is unavailable; no advance was taken."; continue; }
                    terms["paymentAgreementId"] = agreement;
                }
                var candidate = TestDict("command", intent.Command, "actionId", id, "source", "dialogue_player_action",
                    "FromHero", player, "ToHero", recipient, "terms", terms, "requiresAcceptance", false,
                    "reason", "Player initiated a current handover: " + LimitText(intent.Quote, 400));
                if (intent.Command == "transfer_item")
                {
                    var copies = MentionedActionItems(ReadDictionary(payload, "actionResolutionIndex"), intent.Asset)
                        .Where(r => ReadString(r, "holder", "") == "player").ToList();
                    if (copies.Select(r => ReadString(r, "itemId", "") + "|" + ReadString(r, "itemModifierId", "") + "|" + ReadString(r, "sourceEquipmentSet", "")).Distinct().Count() != 1)
                    { result["message"] = "Specify one exact owned item and variant."; continue; }
                    CompleteNamedGiftTerms(candidate, terms, payload, intent.Asset);
                    if (ReadString(terms, "itemId", "").Length == 0 || ReadInt(terms, "amount", 0) <= 0)
                    { result["message"] = "Specify one exact owned item, variant and quantity."; continue; }
                }
                var normalized = NormalizeActionCommand(candidate, campaign, out var errors, payload, intent.Quote);
                if (normalized == null || errors.Count > 0) { result["message"] = string.Join(" ", errors); continue; }
                try
                {
                    var row = QueueAction(campaign, normalized);
                    string status = ReadString(row, "status", "queued");
                    if (!IsTerminalActionReportStatus(status) || status == "completed") rows.Add(row);
                    result["status"] = IsTerminalActionReportStatus(status) && status != "completed" ? "unresolved" : status;
                    result["message"] = ReadString(ReadDictionary(row, "report"), "message", "Awaiting native handover result.");
                }
                catch (InvalidOperationException ex) { result["message"] = ex.Message; }
            }
            if (intents.Count > 0) WriteAudit(campaign, EnsureCorrelationId(payload), "server", "dialogue", "action.player_intents", recipient, "", "", "completed", 0,
                "Player handovers evaluated independently of NPC negotiation.", TestDict("outcomes", outcomes));
            return rows;
        }

        private static bool BindPlayerTransferAuthority(Dictionary<string, object> record, Dictionary<string, object> terms,
            Dictionary<string, object> payload, string command, List<string> errors)
        {
            var grant = ReadDictionary(terms, "playerAction");
            if (grant == null) return false;
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            string recipient = FirstNonEmpty(ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"), ReadString(ReadDictionary(payload, "hero"), "heroStringId", ""));
            var intents = CurrentPlayerTransfers(payload);
            int slot = ReadInt(grant, "slot", -1);
            string turn = FirstNonEmpty(ReadFirstString(payload, "sceneTurnId", "turnId", "requestId"), EnsureCorrelationId(payload));
            if (ReadString(grant, "schema", "") != PlayerActionRules.Schema || ReadString(grant, "turnId", "") != turn
                || ReadString(grant, "campaignId", "") != ReadString(payload, "campaignId", "default")
                || ReadString(grant, "timelineId", "") != ContinuityTimeline(payload)
                || ReadString(record, "serverActionId", "") != PlayerActionRules.ActionId(ReadString(payload, "campaignId", "default"), ContinuityTimeline(payload), turn, slot)
                || slot < 0 || slot >= intents.Count || intents[slot].Error.Length > 0 || intents[slot].Command != command
                || ReadString(grant, "quote", "") != intents[slot].Quote
                || ReadString(grant, "playerText", "") != ReadFirstString(payload, "playerText", "text", "message")
                || ReadString(record, "actorHeroStringId", "") != player || ReadString(record, "targetHeroStringId", "") != recipient
                || ReadString(terms, "fromHeroStringId", "") != player || ReadString(terms, "toHeroStringId", "") != recipient
                || (command == "transfer_gold" && ReadInt(terms, "gold", -1) != intents[slot].Amount)
                || (command == "transfer_item" && ReadString(terms, "delivery", "") != "personal"))
            { errors.Add("Player transfer authority does not match the exact current handover."); return true; }
            record["authorizationMode"] = PlayerActionRules.Policy;
            record["acceptedByHeroStringId"] = player;
            record["negotiationId"] = ReadString(grant, "turnId", "");
            terms["authorityReceipt"] = TestDict("policy", PlayerActionRules.Policy, "acceptedByHeroStringId", player,
                "actorHeroStringId", player, "targetHeroStringId", recipient, "command", command);
            record["termsJson"] = Json.Serialize(terms); record["termsHash"] = ComputeActionTermsHash(command, terms);
            return true;
        }

        private static void BindPaymentRefund(Dictionary<string, object> record, Dictionary<string, object> terms,
            Dictionary<string, object> payload, string command, List<string> errors)
        {
            if ((command != "transfer_gold" && command != "give_gold_to_player") || !IsDialogueActionSource(ReadString(record, "source", ""))) return;
            string playerText = ReadFirstString(payload, "playerText", "text", "message");
            if (!Regex.IsMatch(playerText, @"\b(?:refund|return (?:my|the) (?:gold|coins|money|advance|payment))\b", RegexOptions.IgnoreCase)) return;
            string from = FirstNonEmpty(ReadString(terms, "fromHeroStringId", ""), ReadString(record, "actorHeroStringId", ""));
            string to = FirstNonEmpty(ReadString(terms, "toHeroStringId", ""), ReadString(payload, "playerHeroStringId", ""));
            var receipts = ReadDictionaryList(payload, "nativePaymentReceipts").Where(r => ReadString(r, "status", "") == "applied"
                && ReadString(r, "fromHeroStringId", "") == to && ReadString(r, "toHeroStringId", "") == from
                && ReadInt(r, "amount", 0) > ReadInt(r, "refunded", 0)).ToList();
            string supplied = ReadString(terms, "refundOfReceiptId", "");
            if (supplied.Length > 0) receipts = receipts.Where(r => ReadString(r, "actionId", "") == supplied).ToList();
            if (receipts.Count != 1) { errors.Add("A refund requires one exact verified original payment; clarify which payment to return."); return; }
            terms["paymentPurpose"] = "refund"; terms["refundOfReceiptId"] = ReadString(receipts[0], "actionId", "");
        }

        private static bool IsCurrentPlayerTransferAlternative(Dictionary<string, object> action, Dictionary<string, object> payload)
        {
            string command = CanonicalCommand(ReadString(action, "command", ""));
            if (command != "transfer_gold" && command != "transfer_item") return false;
            var terms = ReadDictionary(action, "terms");
            string from = FirstNonEmpty(ReadFirstString(action, "FromHero", "actorHeroStringId"), ReadString(terms, "fromHeroStringId", ""));
            return (from == ReadString(payload, "playerHeroStringId", "") || from == "player") && CurrentPlayerTransfers(payload).Any(i => i.Command == command);
        }

        private static void EnsureConversationActionOutbox(ReignDbConnection connection)
            => ExecuteSql(connection, "CREATE TABLE IF NOT EXISTS conversation_action_outbox (action_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,fingerprint TEXT NOT NULL,payload_json TEXT NOT NULL);");
        private static bool DurableConversationAction(string id) => id.StartsWith("pa_", StringComparison.Ordinal) || id.StartsWith("da_", StringComparison.Ordinal);
        private static Dictionary<string, object> PublishConversationActionBatch(string campaign,
            Dictionary<string, object> payload, Dictionary<string, object> response,
            List<Dictionary<string, object>> staged, string expectedGeneration, Action<int> beforeCommit = null,
            Action<Dictionary<string, object>> auditWriter = null)
        {
            var rows = staged ?? new List<Dictionary<string, object>>();
            if (rows.Count == 0) return TestDict("schema", "reign-conversation-action-publication-v1",
                "status", "no_actions", "committed", true, "actionIds", new string[0]);
            // Explicit non-dialogue test directives retain their separate queue policy.
            // Do not describe a mixed legacy/database batch as an atomic publication.
            bool anyLegacy = rows.Any(row => !DurableConversationAction(ReadString(row, "id", "")));
            if (anyLegacy)
            {
                if (rows.Any(row => DurableConversationAction(ReadString(row, "id", ""))))
                    throw new InvalidOperationException("A dialogue publication cannot mix durable actions and legacy directives.");
                using (var connection = OpenCampaignConnection(campaign))
                    if (ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "") != expectedGeneration)
                        throw new InvalidOperationException("The campaign was restored while the directive was being prepared.");
                lock (FileLock)
                    foreach (var row in rows) QueueAction(campaign, ReadDictionary(row, "record"));
                return TestDict("schema", "reign-conversation-action-publication-v1", "status", "legacy_directive_path",
                    "committed", true, "atomic", false, "actionIds", rows.Select(r => ReadString(r, "id", "")).ToList());
            }
            if (string.IsNullOrWhiteSpace(expectedGeneration))
                throw new InvalidOperationException("An action publication requires the captured campaign generation.");
            if (!ReadBool(response, "ok", false) && rows.Any(row => !ReadString(row, "id", "").StartsWith("pa_", StringComparison.Ordinal)))
                throw new InvalidOperationException("A failed NPC response cannot publish NPC-owned actions.");
            if (ReadString(ReadDictionary(payload, "conversationAgencyReceipt"), "commitState", "") == "prepared"
                && rows.Any(row => !ReadString(row, "id", "").StartsWith("pa_", StringComparison.Ordinal)))
                throw new InvalidOperationException("Prepared negotiation evidence cannot authorize native publication.");
            string replyHash = PromptHash(ReadString(response, "reply", ""));
            string publicationId = "actionpub_" + PromptHash(ContinuityTimeline(payload) + "|"
                + ReadFirstString(payload, "sceneTurnId", "turnId", "requestId", "correlationId") + "|"
                + string.Join("|", rows.Select(r => ReadString(r, "id", "")).OrderBy(id => id, StringComparer.Ordinal)));
            var created = new List<Dictionary<string, object>>();
            lock (FileLock)
            using (var connection = OpenCampaignConnection(campaign))
            {
                EnsureConversationActionOutbox(connection);
                using (var transaction = connection.BeginTransaction())
                {
                    string currentGeneration = ReadString(QuerySql(connection,
                        "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR SHARE;").Single(), "restore_generation", "");
                    if (currentGeneration != expectedGeneration)
                        throw new InvalidOperationException("The campaign was restored while the conversation was being prepared.");
                    int index = 0;
                    foreach (var row in rows)
                    {
                        string id = ReadString(row, "id", "");
                        var record = ReadDictionary(row, "record");
                        if (ReadString(row, "campaignId", "") != campaign || ReadString(record, "campaignId", "") != campaign
                            || ReadString(record, "termsHash", "").Length == 0)
                            throw new InvalidOperationException("The staged action is missing its immutable campaign or terms binding.");
                        bool existing = QuerySql(connection, "SELECT action_id FROM conversation_action_outbox WHERE action_id=$id;", TestDict("id", id)).Count > 0;
                        row["publicationId"] = publicationId;
                        row["publicationGeneration"] = expectedGeneration;
                        row["publicationEvidence"] = id.StartsWith("pa_", StringComparison.Ordinal)
                            ? "independent_player_intent" : "accepted_npc_response";
                        if (!id.StartsWith("pa_", StringComparison.Ordinal)) row["acceptedResponseHash"] = replyHash;
                        SaveDurableConversationAction(connection, row);
                        if (!existing) created.Add(row);
                        beforeCommit?.Invoke(++index);
                    }
                    beforeCommit?.Invoke(rows.Count + 1);
                    transaction.Commit();
                }
            }
            // The database alone is sufficient for polling and Save Sync. If an audit
            // projection fails after commit, report that gap without retrying effects.
            int auditPending = 0;
            foreach (var row in created)
                try
                {
                    if (auditWriter != null) auditWriter(row);
                    else WriteAudit(campaign, ReadString(row, "correlationId", EnsureCorrelationId(payload)), "server", "action", "action.queued",
                        ReadFirstString(ReadDictionary(row, "record"), "actorHeroStringId", "targetHeroStringId"),
                        ReadString(row, "id", ""), "", "queued", 0, "Accepted conversation action batch committed to the durable outbox.",
                        TestDict("publicationId", publicationId, "row", row));
                }
                catch { auditPending++; }
            return TestDict("schema", "reign-conversation-action-publication-v1", "publicationId", publicationId,
                "status", "committed", "committed", true, "atomic", true, "acceptedResponseHash", replyHash,
                "actionIds", rows.Select(r => ReadString(r, "id", "")).Distinct().ToList(),
                "createdCount", created.Count, "auditProjectionPending", auditPending);
        }
        private static Dictionary<string, object> ReadDurableConversationAction(string campaign, string id)
        {
            if (!DurableConversationAction(id)) return null;
            using (var connection = OpenCampaignConnection(campaign))
            {
                EnsureConversationActionOutbox(connection);
                var row = QuerySql(connection, "SELECT payload_json FROM conversation_action_outbox WHERE action_id=$id;", TestDict("id", id)).FirstOrDefault();
                return row == null ? null : TryParseJsonObject(ReadString(row, "payload_json", "{}"));
            }
        }
        private static void SaveDurableConversationAction(string campaign, Dictionary<string, object> row)
        {
            string id = ReadString(row, "id", ""); if (!DurableConversationAction(id)) return;
            lock (FileLock)
            using (var connection = OpenCampaignConnection(campaign))
            {
                EnsureConversationActionOutbox(connection);
                using (var transaction = connection.BeginTransaction())
                {
                    SaveDurableConversationAction(connection, row);
                    transaction.Commit();
                }
            }
        }
        private static void SaveDurableConversationAction(ReignDbConnection connection, Dictionary<string, object> row)
        {
            string id = ReadString(row, "id", "");
            var record = ReadDictionary(row, "record"); var terms = TryParseJsonObject(ReadString(record, "termsJson", "{}"));
            string fingerprint = ReadString(record, "termsHash", "");
            var previous = QuerySql(connection, "SELECT fingerprint,payload_json FROM conversation_action_outbox WHERE action_id=$id FOR UPDATE;", TestDict("id", id)).FirstOrDefault();
            if (previous != null)
            {
                if (ReadString(previous, "fingerprint", "") != fingerprint) throw new InvalidOperationException("This action ID already has different immutable terms.");
                var original = TryParseJsonObject(ReadString(previous, "payload_json", "{}"));
                string acceptedHash = ReadString(original, "acceptedResponseHash", "");
                string proposedHash = ReadString(row, "acceptedResponseHash", "");
                if (acceptedHash.Length > 0 && proposedHash.Length > 0 && acceptedHash != proposedHash)
                    throw new InvalidOperationException("This action already belongs to a different accepted response.");
                // Status acknowledgments must retain the original publication evidence.
                foreach (string key in new[] { "publicationId", "publicationGeneration", "publicationEvidence", "acceptedResponseHash" })
                    if (original.ContainsKey(key)) row[key] = original[key];
                if (!ShouldAcceptActionStatusTransition(ReadString(original, "status", ""), ReadString(row, "status", "")))
                { row.Clear(); foreach (var pair in original) row[pair.Key] = pair.Value; return; }
            }
            ExecuteSql(connection, @"INSERT INTO conversation_action_outbox(action_id,timeline_id,fingerprint,payload_json) VALUES($id,$timeline,$fingerprint,$payload)
ON CONFLICT(action_id) DO UPDATE SET payload_json=excluded.payload_json
WHERE conversation_action_outbox.fingerprint=excluded.fingerprint
AND (COALESCE(conversation_action_outbox.payload_json::jsonb->>'acceptedResponseHash','')=''
 OR COALESCE(excluded.payload_json::jsonb->>'acceptedResponseHash','')=''
 OR conversation_action_outbox.payload_json::jsonb->>'acceptedResponseHash'=excluded.payload_json::jsonb->>'acceptedResponseHash');",
                TestDict("id", id, "timeline", ReadString(terms, "timelineId", ReadString(ReadDictionary(terms, "playerAction"), "timelineId", "main")), "fingerprint", fingerprint, "payload", Json.Serialize(row)));
            var saved = QuerySql(connection, "SELECT fingerprint,payload_json FROM conversation_action_outbox WHERE action_id=$id;", TestDict("id", id)).First();
            if (ReadString(saved, "fingerprint", "") != fingerprint) throw new InvalidOperationException("This action ID already has different immutable terms.");
            string expectedHash = ReadString(row, "acceptedResponseHash", "");
            if (expectedHash.Length > 0 && ReadString(TryParseJsonObject(ReadString(saved, "payload_json", "{}")), "acceptedResponseHash", "") != expectedHash)
                throw new InvalidOperationException("This action already belongs to a different accepted response.");
        }
        private static List<Dictionary<string, object>> ReadDurableConversationActions(string campaign)
        {
            using (var connection = OpenCampaignConnection(campaign))
            {
                if (!TableExists(connection, "conversation_action_outbox")) return new List<Dictionary<string, object>>();
                return QuerySql(connection, "SELECT payload_json FROM conversation_action_outbox;").Select(r => TryParseJsonObject(ReadString(r, "payload_json", "{}"))).Where(r => r != null).ToList();
            }
        }
    }
}
