using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ReignBeta.Shared.Dialogue;
using TerminalActionReportProtocol = Reign.Core.Contracts.Actions.TerminalActionReportProtocol;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        [ThreadStatic] internal static Dictionary<string, object> _playerActionTurn;
        [ThreadStatic] internal static List<Dictionary<string, object>> _stagedTurnActions;
        [ThreadStatic] internal static string _playerActionGeneration;
        private const string PlayerActionPrompt = "PLAYER ACTIONS AND RECEIPTS: Current player-authored handovers, including *stage directions*, are independent of your employment or travel decision. Never invent the player's action or treat an earlier claim as a new handover. Gold/item transfers are attempts until native execution confirms them. Do not narrate taking/counting/pocketing coins or owning an item without an applied nativePaymentReceipts entry. A recruitment advance does not move you out of town or make you a companion. Discuss a delayed departure explicitly; permanent companion consent is allegiance, temporary travel is travel. Quote exact fact keys, without '=value'.\n";

        private static bool SinglePlayerTransferRecipient(Dictionary<string, object> payload)
        {
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "attendees", "participants", "sceneParticipants", "activeHeroIds" })
            {
                foreach (var row in ReadDictionaryList(payload, key)) ids.Add(Program.CharacterIdFrom(row));
                if (payload.TryGetValue(key, out object raw) && raw is System.Collections.IEnumerable items && !(raw is string))
                    foreach (object item in items) if (item is string id) ids.Add(id);
            }
            ids.Remove(""); ids.Remove(player);
            return ids.Count <= 1;
        }

        internal static List<PlayerTransferIntent> CurrentPlayerTransfers(Dictionary<string, object> payload)
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

        internal static Dictionary<string, object> RunPlayerActionTurn(Dictionary<string, object> payload, Func<Dictionary<string, object>> respond)
        {
            string key = ReadString(payload, "campaignId", "default") + "|" + ContinuityTimeline(payload) + "|"
                + FirstNonEmpty(ReadFirstString(payload, "contributionId", "sceneTurnId", "turnId"), Program.EnsureCorrelationId(payload)) + "|"
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
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                publicationGeneration = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            string inputObserver = FirstNonEmpty(ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                Program.CharacterIdFrom(ReadDictionary(payload, "hero") ?? ReadDictionary(payload, "speaker") ?? TestDict()));
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
                        ? CharactersService.QueueExplicitResidentCompanion(payload, response) : null;
                    if (recruited != null)
                    {
                        var ordinaryRows = ReadDictionaryList(response, "queuedDialogueActions");
                        ordinaryRows.Add(recruited); response["queuedDialogueActions"] = ordinaryRows;
                    }
                }
                catch (Exception ex) when (intents.Count > 0 && !(ex is PersistenceService.CampaignRequestReplacedException) && !(ex is OperationCanceledException))
                { Program.LogOperational("dialogue.player_action_reply_failed", TestDict("correlationId", Program.EnsureCorrelationId(payload), "error", ex.GetType().Name));
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
            catch (Exception ex) when (!(ex is PersistenceService.CampaignRequestReplacedException) && !(ex is OperationCanceledException))
            {
                Program.LogOperational("dialogue.action_publication_failed", TestDict("campaignId", campaign,
                    "correlationId", Program.EnsureCorrelationId(payload), "error", ex.GetType().Name));
                if (!IsSinglePassConversation(payload) || string.IsNullOrWhiteSpace(ReadString(response, "reply", "")))
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
            response["correlationId"] = Program.EnsureCorrelationId(payload);
            if (!IsSinglePassConversation(payload) && intents.Count > 0 && !ReadBool(response, "ok", false))
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
            string turn = FirstNonEmpty(ReadFirstString(payload, "sceneTurnId", "turnId", "requestId"), Program.EnsureCorrelationId(payload));
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
                    "reason", "Player initiated a current handover: " + Program.LimitText(intent.Quote, 400));
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
                var normalized = Program.NormalizeActionCommand(candidate, campaign, out var errors, payload, intent.Quote);
                if (normalized == null || errors.Count > 0) { result["message"] = string.Join(" ", errors); continue; }
                try
                {
                    var row = Program.QueueAction(campaign, normalized);
                    string status = ReadString(row, "status", "queued");
                    if (!Program.IsTerminalActionReportStatus(status) || status == "completed") rows.Add(row);
                    result["status"] = Program.IsTerminalActionReportStatus(status) && status != "completed" ? "unresolved" : status;
                    result["message"] = ReadString(ReadDictionary(row, "report"), "message", "Awaiting native handover result.");
                }
                catch (InvalidOperationException ex) { result["message"] = ex.Message; }
            }
            if (intents.Count > 0) Program.WriteAudit(campaign, Program.EnsureCorrelationId(payload), "server", "dialogue", "action.player_intents", recipient, "", "", "completed", 0,
                "Player handovers evaluated independently of NPC negotiation.", TestDict("outcomes", outcomes));
            return rows;
        }

        internal static bool BindPlayerTransferAuthority(Dictionary<string, object> record, Dictionary<string, object> terms,
            Dictionary<string, object> payload, string command, List<string> errors)
        {
            var grant = ReadDictionary(terms, "playerAction");
            if (grant == null) return false;
            string player = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            string recipient = FirstNonEmpty(ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"), ReadString(ReadDictionary(payload, "hero"), "heroStringId", ""));
            var intents = CurrentPlayerTransfers(payload);
            int slot = ReadInt(grant, "slot", -1);
            string turn = FirstNonEmpty(ReadFirstString(payload, "sceneTurnId", "turnId", "requestId"), Program.EnsureCorrelationId(payload));
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
            record["termsJson"] = Json.Serialize(terms); record["termsHash"] = Program.ComputeActionTermsHash(command, terms);
            return true;
        }

        internal static void BindPaymentRefund(Dictionary<string, object> record, Dictionary<string, object> terms,
            Dictionary<string, object> payload, string command, List<string> errors)
        {
            if ((command != "transfer_gold" && command != "give_gold_to_player") || !Program.IsDialogueActionSource(ReadString(record, "source", ""))) return;
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

        internal static bool IsCurrentPlayerTransferAlternative(Dictionary<string, object> action, Dictionary<string, object> payload)
        {
            string command = Program.CanonicalCommand(ReadString(action, "command", ""));
            if (command != "transfer_gold" && command != "transfer_item") return false;
            var terms = ReadDictionary(action, "terms");
            string from = FirstNonEmpty(ReadFirstString(action, "FromHero", "actorHeroStringId"), ReadString(terms, "fromHeroStringId", ""));
            return (from == ReadString(payload, "playerHeroStringId", "") || from == "player") && CurrentPlayerTransfers(payload).Any(i => i.Command == command);
        }

        private static void EnsureConversationActionOutbox(ReignDbConnection connection)
            => DialogueSchema.CreateConversationActionOutbox(connection);
        internal static bool DurableConversationAction(string id) => id.StartsWith("pa_", StringComparison.Ordinal) || id.StartsWith("da_", StringComparison.Ordinal);

        // ---- Bounded redelivery of durable actions that never receive a terminal report. ----
        // Durable work is never expired, dropped or auto-resolved: a row stays deliverable and a
        // terminal report is accepted at any time. Only the redelivery cadence backs off.
        private const long ActionRedeliveryBaseSeconds = 30;
        internal const long ActionRedeliveryFreeDeliveries = 4;
        private const long ActionRedeliveryCapSeconds = 30 * 60;

        // Delay before the next redelivery after deliveryCount deliveries without a terminal report.
        internal static long ActionRedeliveryDelaySeconds(long deliveryCount)
        {
            if (deliveryCount <= ActionRedeliveryFreeDeliveries) return ActionRedeliveryBaseSeconds;
            int shift = (int)Math.Min(6, deliveryCount - ActionRedeliveryFreeDeliveries);
            return Math.Min(ActionRedeliveryCapSeconds, ActionRedeliveryBaseSeconds << shift);
        }

        // The client session a delivery belongs to: the campaign restore generation plus the
        // verified load session. Rollback loads and imports advance the generation; every verified
        // load (including a no-op or non-rollback load) binds load_session_id, and archive imports
        // stamp "imported:". A row last delivered under another key is due again immediately and
        // restarts at the base cadence, so a reloaded client receives it promptly.
        internal static string ActionDeliveryGeneration(string campaign)
        {
            try
            {
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    string generation = "";
                    if (WorldSimulationService.TableExists(connection, "memory_precision_state"))
                        generation = ReadString(DialogueStore.SelectMemoryPrecisionState2(connection).FirstOrDefault(), "restore_generation", "");
                    string session = "";
                    if (WorldSimulationService.TableExists(connection, "memory_restored_source_context"))
                        session = ReadString(DialogueStore.SelectMemoryRestoredSourceContext(connection).FirstOrDefault(),
                            "load_session_id", "");
                    return generation + "|" + session;
                }
            }
            catch (Exception ex)
            {
                if (System.Threading.Interlocked.Exchange(ref _actionDeliveryGenerationFailureLogged, 1) == 0)
                    Program.LogOperational("action.redelivery_generation_unavailable", TestDict("campaignId", campaign ?? "", "error", ex.GetType().Name));
                return "";
            }
        }
        private static int _actionDeliveryGenerationFailureLogged;

        // Rows delivered before this server process started are due again: a restart must not leave
        // a lost one-shot report waiting out a long backoff. The override is a self-test seam.
        private static readonly long ActionRedeliveryProcessStartTs = CurrentProcessStartUnixSeconds();
        [ThreadStatic] private static long? _actionRedeliveryProcessStartOverride;
        private static long CurrentProcessStartUnixSeconds()
        {
            try { return new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).ToUnixTimeSeconds(); }
            catch { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }
        }

        internal static bool DurableRedeliveryDue(Dictionary<string, object> row, long nowTs, Func<string> generation)
        {
            long deliveredTs = ReadLong(row, "deliveredTs", 0);
            long elapsed = nowTs - deliveredTs;
            if (elapsed < ActionRedeliveryBaseSeconds) return false;
            if (elapsed >= ActionRedeliveryDelaySeconds(ReadLong(row, "deliveryCount", 0))) return true;
            if (deliveredTs < (_actionRedeliveryProcessStartOverride ?? ActionRedeliveryProcessStartTs)) return true;
            return ReadString(row, "deliveryGeneration", "") != generation();
        }

        // Bounded, non-sensitive view of durable actions that were delivered and still await a
        // terminal report. The server knows only each row's last reported status, not client hold reasons.
        internal static Dictionary<string, object> UnresolvedActionDeliverySummary(string campaign, int sampleLimit = 5)
        {
            var summary = TestDict("schema", "reign-action-redelivery-holds-v1", "campaignId", campaign ?? "",
                "baseDelaySeconds", ActionRedeliveryBaseSeconds, "capDelaySeconds", ActionRedeliveryCapSeconds,
                "backoffAfterDeliveries", ActionRedeliveryFreeDeliveries);
            try
            {
                // Never creates a campaign directory; an existing campaign is read through
                // OpenCampaignConnection exactly as an action poll would.
                if (string.IsNullOrWhiteSpace(campaign) || !System.IO.Directory.Exists(Program.CampaignDirectory(campaign)))
                {
                    summary["ok"] = true; summary["unresolvedCount"] = 0; summary["backedOffCount"] = 0;
                    return summary;
                }
                long nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var unresolved = ReadDurableConversationActions(campaign)
                    .Where(r => DurableConversationAction(ReadString(r, "id", ""))
                        && !Program.IsTerminalActionReportStatus(ReadString(r, "status", ""))
                        && ReadLong(r, "deliveredTs", 0) > 0)
                    .OrderBy(r => ReadLong(r, "ts", 0)).ThenBy(r => ReadString(r, "id", ""), StringComparer.Ordinal).ToList();
                var backedOff = unresolved.Where(r => ReadLong(r, "deliveryCount", 0) > ActionRedeliveryFreeDeliveries).ToList();
                summary["ok"] = true;
                summary["unresolvedCount"] = unresolved.Count;
                summary["backedOffCount"] = backedOff.Count;
                // Bounded: at most 16 status groups, the rest folded into "other".
                var groups = unresolved.GroupBy(r => ReadString(r, "status", "unknown"), StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
                var byStatus = groups.Take(16).ToDictionary(g => g.Key, g => (object)g.Count());
                if (groups.Count > 16)
                    byStatus["other"] = groups.Skip(16).Sum(g => g.Count()) + (byStatus.TryGetValue("other", out object existingOther) ? Convert.ToInt32(existingOther) : 0);
                summary["byStatus"] = byStatus;
                summary["oldestAgeSeconds"] = unresolved.Count == 0 ? 0L
                    : Math.Max(0L, nowTs - unresolved.Min(r => ReadLong(r, "ts", nowTs)));
                summary["maxDeliveryCount"] = unresolved.Count == 0 ? 0L : unresolved.Max(r => ReadLong(r, "deliveryCount", 0));
                summary["samples"] = unresolved.Take(Math.Max(0, sampleLimit)).Select(r =>
                {
                    long count = ReadLong(r, "deliveryCount", 0);
                    long due = ReadLong(r, "deliveredTs", 0) + ActionRedeliveryDelaySeconds(count) - nowTs;
                    return (object)TestDict("actionId", ReadString(r, "id", ""), "command", ReadString(r, "command", ""),
                        "status", ReadString(r, "status", ""), "deliveryCount", count,
                        "ageSeconds", Math.Max(0L, nowTs - ReadLong(r, "ts", nowTs)), "nextDeliveryInSeconds", Math.Max(0L, due));
                }).ToList();
            }
            catch (Exception ex)
            {
                Program.LogOperational("action.redelivery_summary_failed", TestDict("campaignId", campaign, "error", ex.GetType().Name));
                summary["ok"] = false; summary["error"] = "Unresolved action deliveries are unavailable.";
            }
            return summary;
        }
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
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    if (ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "") != expectedGeneration)
                        throw new InvalidOperationException("The campaign was restored while the directive was being prepared.");
                lock (Program.FileLock)
                    foreach (var row in rows) Program.QueueAction(campaign, ReadDictionary(row, "record"));
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
            lock (Program.FileLock)
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                EnsureConversationActionOutbox(connection);
                using (var transaction = connection.BeginTransaction())
                {
                    string currentGeneration = ReadString(DialogueStore.SelectMemoryPrecisionState(connection).Single(), "restore_generation", "");
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
                        bool existing = DialogueStore.SelectConversationActionOutbox(connection, TestDict("id", id)).Count > 0;
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
                    else Program.WriteAudit(campaign, ReadString(row, "correlationId", Program.EnsureCorrelationId(payload)), "server", "action", "action.queued",
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
        internal static Dictionary<string, object> ReadDurableConversationAction(string campaign, string id)
        {
            if (!DurableConversationAction(id)) return null;
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                EnsureConversationActionOutbox(connection);
                var row = DialogueStore.SelectConversationActionOutbox2(connection, TestDict("id", id)).FirstOrDefault();
                return row == null ? null : TryParseJsonObject(ReadString(row, "payload_json", "{}"));
            }
        }
        internal static void SaveDurableConversationAction(string campaign, Dictionary<string, object> row)
        {
            string id = ReadString(row, "id", ""); if (!DurableConversationAction(id)) return;
            lock (Program.FileLock)
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                EnsureConversationActionOutbox(connection);
                using (var transaction = connection.BeginTransaction())
                {
                    SaveDurableConversationAction(connection, row);
                    transaction.Commit();
                }
            }
        }
        internal static void SaveDurableConversationAction(ReignDbConnection connection, Dictionary<string, object> row)
        {
            string id = ReadString(row, "id", "");
            var record = ReadDictionary(row, "record"); var terms = TryParseJsonObject(ReadString(record, "termsJson", "{}"));
            string fingerprint = ReadString(record, "termsHash", "");
            var previous = DialogueStore.SelectConversationActionOutbox3(connection, TestDict("id", id)).FirstOrDefault();
            if (previous != null)
            {
                if (ReadString(previous, "fingerprint", "") != fingerprint) throw new InvalidOperationException("This action ID already has different immutable terms.");
                var original = TryParseJsonObject(ReadString(previous, "payload_json", "{}"));
                string acceptedHash = ReadString(original, "acceptedResponseHash", "");
                string proposedHash = ReadString(row, "acceptedResponseHash", "");
                if (acceptedHash.Length > 0 && proposedHash.Length > 0 && acceptedHash != proposedHash)
                    throw new InvalidOperationException("This action already belongs to a different accepted response.");
                // A protocol-accepted terminal report is immutable evidence.
                if (IsProtocolOwnedTerminalAction(original))
                { row.Clear(); foreach (var pair in original) row[pair.Key] = pair.Value; return; }
                // Status acknowledgments must retain the original publication evidence.
                foreach (string key in new[] { "publicationId", "publicationGeneration", "publicationEvidence", "acceptedResponseHash" })
                    if (original.ContainsKey(key)) row[key] = original[key];
                if (!Program.ShouldAcceptActionStatusTransition(ReadString(original, "status", ""), ReadString(row, "status", "")))
                { row.Clear(); foreach (var pair in original) row[pair.Key] = pair.Value; return; }
            }
            DialogueStore.UpsertConversationActionOutbox(connection, TestDict("id", id, "timeline", ReadString(terms, "timelineId", ReadString(ReadDictionary(terms, "playerAction"), "timelineId", "main")), "fingerprint", fingerprint, "payload", Json.Serialize(row)));
            var saved = DialogueStore.SelectConversationActionOutbox4(connection, TestDict("id", id)).First();
            if (ReadString(saved, "fingerprint", "") != fingerprint) throw new InvalidOperationException("This action ID already has different immutable terms.");
            string expectedHash = ReadString(row, "acceptedResponseHash", "");
            if (expectedHash.Length > 0 && ReadString(TryParseJsonObject(ReadString(saved, "payload_json", "{}")), "acceptedResponseHash", "") != expectedHash)
                throw new InvalidOperationException("This action already belongs to a different accepted response.");
        }
        internal static List<Dictionary<string, object>> ReadDurableConversationActions(string campaign)
        {
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                if (!WorldSimulationService.TableExists(connection, "conversation_action_outbox")) return new List<Dictionary<string, object>>();
                return DialogueStore.SelectConversationActionOutbox5(connection).Select(r => TryParseJsonObject(ReadString(r, "payload_json", "{}"))).Where(r => r != null).ToList();
            }
        }

        // ---- Opt-in terminal action report v1. ----
        private static readonly HashSet<string> TerminalReportStatuses = new HashSet<string>(StringComparer.Ordinal)
            { "completed", "failed", "rejected", "cancelled", "canceled", "blocked", "invalid", "obsolete" };

        internal static bool IsProtocolOwnedTerminalAction(Dictionary<string, object> row) => ReadDictionary(row, "terminalReport") != null;

        // beforeCommit is a self-test seam; it receives the connection that owns the open transaction.
        // First acceptance invokes it with 0 (row locks held, before evaluation), 1 (after evaluation)
        // and 2 (immediately before commit).
        internal static Dictionary<string, object> TerminalActionReportApi(Dictionary<string, object> payload,
            Action<int, ReignDbConnection> beforeCommit = null)
        {
            payload = payload ?? new Dictionary<string, object>();
            Func<string, string> exact = key => payload.TryGetValue(key, out object value) && value is string text ? text : "";
            // Nested identity fields must be JSON strings; ReadString would coerce numbers and booleans.
            Func<Dictionary<string, object>, string, string, bool> same = (source, key, expected) =>
                source != null && source.TryGetValue(key, out object value) && value is string text && text == expected;
            string campaign = exact("campaignId"), timeline = exact("timelineId"), player = exact("playerHeroStringId");
            string generation = exact("generation"), actionId = exact("actionId"), termsHash = exact("termsHash");
            string status = exact("status"), reportJson = exact("reportJson"), sha = exact("reportSha256");
            // Recovery keeps the v1 envelope (generation is still the report's own generation)
            // and adds the verified load it is recovered under; loadProof (outside the SHA) binds
            // any proof-carrying write to the campaign's current verified load context.
            bool recovery = exact("schema") == TerminalActionReportProtocol.RecoverySchema;
            Func<Dictionary<string, object>, string, string> envelopeText = (source, key) =>
                source != null && source.TryGetValue(key, out object value) && value is string s ? s : "";
            var proof = ReadDictionary(payload, TerminalActionReportProtocol.LoadProofField);
            var recoveryFields = recovery ? ReadDictionary(payload, TerminalActionReportProtocol.RecoveryField) : null;
            string liveGeneration = envelopeText(recoveryFields, "liveGeneration"), recoveryPoint = envelopeText(recoveryFields, "savePointId");
            string recoverySession = envelopeText(recoveryFields, "loadSessionId");
            bool proofMalformed = payload.ContainsKey(TerminalActionReportProtocol.LoadProofField) && (proof == null
                || !new[] { envelopeText(proof, "loadSessionId"), envelopeText(proof, "savePointId"), envelopeText(proof, "restoreGeneration") }.All(TerminalActionReportProtocol.IsTrustedIdentifier));
            bool recoveryMalformed = recovery && (recoveryFields == null || proof == null
                || !new[] { liveGeneration, recoveryPoint, recoverySession }.All(TerminalActionReportProtocol.IsTrustedIdentifier)
                || liveGeneration == generation || envelopeText(proof, "restoreGeneration") != liveGeneration
                || envelopeText(proof, "savePointId") != recoveryPoint || envelopeText(proof, "loadSessionId") != recoverySession);
            // Proof-carrying and recovery requests come only from new clients, which receive the
            // staged receipt whatever its status so they can compare it with their journal.
            bool fullEffect = recovery || proof != null;
            Dictionary<string, object> Reply(string disposition, string reason, Dictionary<string, object> marker = null, Dictionary<string, object> effect = null)
            {
                var reply = TestDict("ok", true, "schema", TerminalActionReportProtocol.ResponseSchema, "disposition", disposition, "reason", reason,
                    "campaignId", campaign, "actionId", actionId, "reportSha256", sha, "generation", generation,
                    "authoritativeStatus", ReadString(marker, "authoritativeStatus", ""), "relationshipEvidence", ReadString(marker, "relationshipEvidence", ""),
                    "nativeEffect", effect, "nativeEffectStatus", effect == null ? "none" : ReadString(effect, "nativeApplicationStatus", ""));
                var admission = ReadDictionary(marker, TerminalActionReportProtocol.AdmissionField);
                if (admission != null) reply[TerminalActionReportProtocol.AdmissionField] = admission;
                return reply;
            }
            var report = TerminalActionReportProtocol.TryReportSha256(reportJson, out string computed, out int bytes) ? TryParseJsonObject(reportJson) : null;
            // The exact encoded report must restate the envelope identity it is filed under.
            if ((exact("schema") != TerminalActionReportProtocol.RequestSchema && !recovery) || proofMalformed || recoveryMalformed
                || report == null || computed != sha
                || !reportJson.StartsWith("{", StringComparison.Ordinal) || !reportJson.EndsWith("}", StringComparison.Ordinal)
                || !new[] { campaign, timeline, player, generation, actionId, termsHash }.All(TerminalActionReportProtocol.IsTrustedIdentifier)
                || !TerminalReportStatuses.Contains(status) || !same(report, "status", status)
                || !same(report, "campaignId", campaign) || !same(report, "timelineId", timeline)
                || !same(report, "playerHeroStringId", player) || !same(report, "sharedActivityGeneration", generation)
                || !same(report, "termsHash", termsHash) || !same(report, "serverActionId", actionId)
                || (report.ContainsKey("actionId") && !same(report, "actionId", actionId)))
                return TestDict("ok", false, "schema", TerminalActionReportProtocol.ResponseSchema, "error", "The terminal action report envelope is invalid.");
            // A class the durable outbox does not represent is unknown, never ineligible:
            // ineligible permits client legacy fallback only for a proven stored row.
            if (!actionId.StartsWith("pa_", StringComparison.Ordinal)) return Reply(TerminalActionReportProtocol.Unknown, "action_class_not_represented");
            // Never create or initialize a campaign for a report that names none.
            if (!ReignPostgreSqlStorage.CampaignExists(campaign)) return Reply(TerminalActionReportProtocol.Unknown, "unknown_campaign");

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> response, record, marker;
            bool firstAcceptance = false, popularityChanged = false;
            var evaluations = new List<Dictionary<string, object>>();
            // A poll or publication can hold FileLock while queued on the rows locked below,
            // so the pair's file-derived inputs are captured first, with no lock held.
            RelationshipsService.RelationshipFileSnapshot pairFiles = CaptureTerminalReportPairFiles(campaign, timeline, player,
                generation, actionId, termsHash, report);
            // Recovery evaluates under the live generation (the probe compares only that value),
            // so it snapshots the same pair for it; the v1 path never takes this second probe.
            if (recovery) pairFiles = CaptureTerminalReportPairFiles(campaign, timeline, player,
                liveGeneration, actionId, termsHash, report);
            // Lock order: campaign relationship writer, then database. Never FileLock here.
            using (PersistenceService.EnterCampaignWorkLock(RelationshipsService.CampaignRelationshipWriteLock(campaign)))
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                EnsureConversationActionOutbox(connection);
                RelationshipsService.EnsureConversationRelationshipSchema(connection);
                // Schema and its one-time legacy migration (own BEGIN) run before the
                // acceptance transaction; in-transaction recompute skips them.
                ReputationService.EnsureSocialReputationSchema(connection);
                ReputationService.EnsureCourtSocialReputationSchema(connection);
                using (var transaction = connection.BeginTransaction())
                {
                    // The receipt/memory triggers later UPDATE this row; a shared lock here could be held
                    // by a second writer queued on the outbox row and deadlock that upgrade. Take it once,
                    // still before the outbox row, in the mode the trigger needs.
                    string current = ReadString(DialogueStore.SelectMemoryPrecisionState3(connection).FirstOrDefault(), "restore_generation", "");
                    // Under the generation lock, before the outbox row: a proof that no longer names the
                    // campaign's current verified load mutates nothing. Recovery's proof is its own
                    // {loadSessionId, savePointId, liveGeneration}; its context must also name this
                    // timeline. source_generation is only read here: membership depends on the exact
                    // locked row and marker, so it is tested after identity, before any mutation.
                    string proofError = TerminalReportLoadProofError(connection, current, proof, out var restoreContext);
                    if (proofError.Length > 0) return Reply(TerminalActionReportProtocol.Conflict, proofError);
                    if (recovery && ReadString(restoreContext, "timeline_id", "") != timeline)
                        return Reply(TerminalActionReportProtocol.Unknown, TerminalActionReportProtocol.RecoveryUnprovenPrefix + "timeline");
                    string sourceGeneration = recovery ? ReadString(restoreContext, "source_generation", "") : "";
                    var stored = DialogueStore.SelectConversationActionOutbox6(connection, TestDict("id", actionId)).FirstOrDefault();
                    var row = stored == null ? null : TryParseJsonObject(ReadString(stored, "payload_json", "{}"));
                    if (row == null) return Reply(TerminalActionReportProtocol.Unknown, "unknown_action");
                    // Order: stored ownership/identity, existing protocol marker, legacy terminal
                    // evidence, current generation, then mutable admission rules and type.
                    record = ReadDictionary(row, "record");
                    if (!TerminalReportScopeMatches(stored, row, record, campaign, actionId, timeline, player, termsHash)
                        || !same(report, "actorHeroStringId", player)
                        || !same(report, "targetHeroStringId", ReadString(record, "targetHeroStringId", "")))
                        return Reply(TerminalActionReportProtocol.Conflict, "identity_mismatch");
                    string expectedType = TerminalReportExpectedType(record);
                    marker = ReadDictionary(row, "terminalReport");
                    if (marker != null)
                    {
                        // Exact replay is a read. It never re-evaluates admission (a later foreign
                        // projection owner cannot downgrade it) and never reopens delivery.
                        if (ReadString(marker, "sha256", "") != sha || ReadString(marker, "timelineId", "") != timeline
                            || ReadString(marker, "playerHeroStringId", "") != player)
                            return Reply(TerminalActionReportProtocol.Conflict, "different_terminal_report", marker);
                        if (expectedType.Length == 0 || !same(report, "type", expectedType))
                            return Reply(TerminalActionReportProtocol.Conflict, "action_type_mismatch", marker);
                        if (recovery)
                        {
                            // Adoption of the exact restored marker: its report generation stays the
                            // original one; the context must come from that generation or from the
                            // generation that last admitted it. A repeat with the same admission is a read.
                            var priorAdmission = ReadDictionary(marker, TerminalActionReportProtocol.AdmissionField);
                            if (ReadString(marker, "generation", "") != generation)
                                return Reply(TerminalActionReportProtocol.Conflict, "different_terminal_report", marker);
                            if (sourceGeneration.Length == 0 || (sourceGeneration != generation
                                && sourceGeneration != ReadString(priorAdmission, "generation", "")))
                                return Reply(TerminalActionReportProtocol.Unknown, TerminalActionReportProtocol.RecoveryUnprovenPrefix + "source_generation", marker);
                            bool admits = ReadString(priorAdmission, "generation", "") != liveGeneration
                                || ReadString(priorAdmission, "savePointId", "") != recoveryPoint
                                || ReadString(priorAdmission, "loadSessionId", "") != recoverySession;
                            if (admits)
                            {
                                var admission = TerminalReportAdmission(liveGeneration, recoveryPoint, recoverySession, now);
                                DialogueStore.UpdateConversationActionOutbox(connection, TestDict("id", actionId, "sha", sha, "admission", Json.Serialize(admission)));
                                marker[TerminalActionReportProtocol.AdmissionField] = admission;
                            }
                            string adoptedReceipt = ReadString(marker, "receiptId", "");
                            var adoptedEffect = adoptedReceipt.Length == 0 ? null
                                : RelationshipsService.CompletedActionRelationshipEffect(connection, adoptedReceipt, actionId, timeline, generation, sha);
                            if (admits) transaction.Commit();
                            response = Reply(TerminalActionReportProtocol.Accepted, admits ? "recovery_admitted" : "recovery_replay", marker, adoptedEffect);
                        }
                        else
                        {
                            if (ReadString(marker, "generation", "") != generation || current != generation)
                                return Reply(TerminalActionReportProtocol.Stale, "generation_mismatch", marker);
                            string priorReceipt = ReadString(marker, "receiptId", "");
                            var effect = priorReceipt.Length == 0 ? null
                                : RelationshipsService.CompletedActionRelationshipEffect(connection, priorReceipt, actionId, timeline, generation, sha);
                            response = Reply(TerminalActionReportProtocol.Accepted, "exact_replay", marker,
                                fullEffect || ReadString(effect, "nativeApplicationStatus", "") == "pending" ? effect : null);
                            if (effect != null && response["nativeEffect"] == null) response["nativeEffectStatus"] = ReadString(effect, "nativeApplicationStatus", "");
                        }
                    }
                    else
                    {
                        // A legacy terminal row may already carry a legacy delta; never upgrade it.
                        if (Program.IsTerminalActionReportStatus(ReadString(row, "status", "")))
                            return Reply(TerminalActionReportProtocol.Conflict, "legacy_terminal_status");
                        // Recovery never answers Stale: the generation authority is the verified context.
                        if (!recovery && (current.Length == 0 || current != generation))
                            return Reply(TerminalActionReportProtocol.Stale, "generation_mismatch");
                        string ineligible = TerminalReportIneligibility(connection, row, campaign, actionId, ReadString(stored, "timeline_id", ""));
                        // Ineligible permits legacy fallback only for a current-generation row; recovery never does.
                        if (ineligible.Length > 0) return recovery
                            ? Reply(TerminalActionReportProtocol.Unknown, TerminalActionReportProtocol.RecoveryIneligiblePrefix + ineligible)
                            : Reply(TerminalActionReportProtocol.Ineligible, ineligible);
                        if (expectedType.Length == 0 || !same(report, "type", expectedType))
                            return Reply(TerminalActionReportProtocol.Conflict, "action_type_mismatch");
                        // A first acceptance under recovery requires the restored context to come from the
                        // report's own generation (exact locked row identity is already proven above).
                        if (recovery && sourceGeneration != generation)
                            return Reply(TerminalActionReportProtocol.Unknown, TerminalActionReportProtocol.RecoveryUnprovenPrefix + "source_generation");
                        string authoritative = Program.NormalizeActionReportStatus(record, report, status);
                        var directions = RelationshipsService.CompletedActionRelationshipDirections(report, timeline);
                        string evidence = directions.Count == 0 ? "not_applicable" : "evaluated";
                        if (directions.Any(d => DialogueStore.SelectRelationshipInteractionReceipts(connection, TestDict("event", ReadString(d, "eventId", ""), "subject", ReadString(d, "subjectId", ""), "target", ReadString(d, "targetId", ""))).Count > 0))
                        { evidence = "pre_protocol"; directions.Clear(); }
                        beforeCommit?.Invoke(0, connection);
                        int shared;
                        // File-derived inputs come only from the pre-lock snapshot of this exact pair;
                        // without one every such read is refused (rollback), never taken under FileLock.
                        // Database standing/roster reads still pass through to this transaction.
                        using (new RelationshipsService.RelationshipStandingContext(connection, campaign, timeline,
                            pairFiles ?? RelationshipsService.RelationshipFileSnapshot.Refusing(campaign, timeline)))
                        {
                            shared = RelationshipsService.EvaluateCompletedActionRelationshipsInTransaction(connection, directions, evaluations);
                            beforeCommit?.Invoke(1, connection);
                            // Derived court popularity and dependent public standing commit with
                            // the directional result, receipt and marker, or roll back with them.
                            foreach (var evaluation in evaluations)
                                popularityChanged |= RelationshipsService.RecomputeRelationshipEvaluationReputation(connection, campaign, timeline,
                                    ReadString(evaluation, "targetId", ""), ReadDouble(evaluation, "worldDay", 0d),
                                    ReadString(evaluation, "eventId", ""), ReadInt(evaluation, "nativeRelationDelta", 0), schemaReady: true);
                        }
                        string receiptId = shared == 0 ? "" : RelationshipsService.StageCompletedActionRelationshipReceipt(connection, campaign, timeline, actionId,
                            generation, sha, ReadString(directions[0], "subjectId", ""), ReadString(directions[0], "targetId", ""), shared, now);
                        marker = TestDict("schema", TerminalActionReportProtocol.MarkerSchema, "sha256", sha, "utf8Bytes", bytes,
                            "generation", generation, "timelineId", timeline, "playerHeroStringId", player, "acceptedTs", now,
                            "reportedStatus", status, "authoritativeStatus", authoritative, "relationshipEvidence", evidence,
                            "receiptId", receiptId, "reportJson", reportJson);
                        // The marker keeps the report's generation; only the mutable admission names the live one.
                        if (recovery) marker[TerminalActionReportProtocol.AdmissionField] = TerminalReportAdmission(liveGeneration, recoveryPoint, recoverySession, now);
                        row["status"] = authoritative; row["reportTs"] = now; row["report"] = report; row["terminalReport"] = marker;
                        SaveDurableConversationAction(connection, row);
                        if (ReadString(ReadDictionary(row, "terminalReport"), "sha256", "") != sha)
                            throw new InvalidOperationException("The terminal report could not take ownership of its action row.");
                        var effect = receiptId.Length == 0 ? null
                            : RelationshipsService.CompletedActionRelationshipEffect(connection, receiptId, actionId, timeline, generation, sha);
                        beforeCommit?.Invoke(2, connection);
                        transaction.Commit();
                        firstAcceptance = true;
                        response = Reply(TerminalActionReportProtocol.Accepted, "first_acceptance", marker, effect);
                    }
                }
            }
            // Nontransactional scheduling stays after commit (currently a no-op).
            if (popularityChanged && !campaign.StartsWith("__", StringComparison.Ordinal))
                ReputationService.SchedulePendingReputationReasonJobs(campaign);
            if (firstAcceptance)
                try
                {
                    // Best-effort diagnostic, first acceptance only. Nothing retries it:
                    // exact replays never append, and the response does not certify it.
                    lock (Program.FileLock) Program.AppendJsonLineToPath(Program.CampaignFile(campaign, "actions", "actions.jsonl"), TestDict("id", actionId,
                        "ts", now, "campaignId", campaign, "status", ReadString(marker, "authoritativeStatus", ""), "report", report, "protocol", TerminalActionReportProtocol.RequestSchema));
                    Program.WriteAudit(campaign, ReadString(record, "correlationId", ""), "game_client", "action", "action.execution_report", player, actionId, "",
                        ReadString(marker, "authoritativeStatus", ""), 0, "Game client filed a durable terminal action report.",
                        TestDict("record", record, "report", report, "reportSha256", sha, "receiptId", ReadString(marker, "receiptId", "")));
                }
                catch (Exception ex) when (!(ex is PersistenceService.CampaignRequestReplacedException) && !(ex is OperationCanceledException))
                {
                    Program.LogOperational("action.terminal_report_audit_incomplete", TestDict("campaignId", campaign, "actionId", actionId,
                        "error", ex.GetType().Name, "bestEffort", true, "retry", "none"));
                }
            // Shared activity and action memory only; independently retryable by exact replay.
            // The snapshot shortcut is safe because both stamps are only ever added, never removed;
            // an absent snapshot is rechecked under the outbox row lock. A legacy marker with
            // projectedTs alone still owes its shared-activity receipt.
            bool projected = !firstAcceptance && marker.ContainsKey("projectedTs") && marker.ContainsKey("projectedShared")
                || ProjectAcceptedTerminalReport(campaign, actionId, sha, generation, report, record, ReadString(marker, "authoritativeStatus", ""));
            response["memoryProjectionStatus"] = projected ? "completed" : "pending";
            return response;
        }

        // Exact stored ownership: the row and its record name this action and campaign,
        // and the outbox timeline, actor and terms match the request.
        private static bool TerminalReportScopeMatches(Dictionary<string, object> stored, Dictionary<string, object> row,
            Dictionary<string, object> record, string campaign, string actionId, string timeline, string player, string termsHash) =>
            record != null && ReadString(row, "id", "") == actionId && ReadString(row, "campaignId", "") == campaign
            && ReadString(record, "serverActionId", "") == actionId && ReadString(record, "campaignId", "") == campaign
            && TerminalReportIdentityMatches(stored, record, timeline, player, termsHash);

        // ---- Terminal report load proof and recovery admission. ----
        // The campaign's single restore context row, read as JSON so a schema without the newer
        // columns simply lacks them (unproven). Plain SELECT: Save Sync loads and backup imports,
        // the only writers, run under the CampaignDataGate write lock that excludes this request.
        private static Dictionary<string, object> TerminalReportRestoreContext(ReignDbConnection connection)
        {
            if (!WorldSimulationService.TableExists(connection, "memory_restored_source_context")) return null;
            var row = DialogueStore.SelectMemoryRestoredSourceContext2(connection).FirstOrDefault();
            return row == null ? null : TryParseJsonObject(ReadString(row, "context", "{}"));
        }

        // "" when no proof was sent (older clients, or newly captured reports during unverified
        // play) or when it names the current verified load: context bound to this exact load
        // session and save point in the current generation. Otherwise load_proof_stale.
        internal static string TerminalReportLoadProofError(ReignDbConnection connection, string current,
            Dictionary<string, object> proof, out Dictionary<string, object> context)
        {
            context = null;
            if (proof == null) return "";
            context = TerminalReportRestoreContext(connection);
            string session = proof.TryGetValue("loadSessionId", out object s) && s is string sv ? sv : "";
            string point = proof.TryGetValue("savePointId", out object p) && p is string pv ? pv : "";
            string restore = proof.TryGetValue("restoreGeneration", out object g) && g is string gv ? gv : "";
            // The import stamp is never a load session a Save Sync load could have proved.
            bool bound = context != null && new[] { session, point, restore }.All(TerminalActionReportProtocol.IsTrustedIdentifier)
                && !session.StartsWith(TerminalActionReportProtocol.ImportedLoadSession, StringComparison.Ordinal)
                && ReadString(context, "load_session_id", "") == session && ReadString(context, "save_point_id", "") == point
                && ReadString(context, "restore_generation", "") == restore && current.Length > 0 && current == restore;
            return bound ? "" : TerminalActionReportProtocol.LoadProofStale;
        }

        private static Dictionary<string, object> TerminalReportAdmission(string generation, string savePointId, string loadSessionId, long now) =>
            TestDict("generation", generation, "savePointId", savePointId, "loadSessionId", loadSessionId, "admittedTs", now);

        // Save Sync load echo: the current generation only while the context is bound to this
        // exact load (so a later proof can hold); "" leaves the client's load unverified. Never
        // throws, so it cannot fail an otherwise completed load.
        internal static string TerminalReportLoadProofGeneration(string campaign, string savePointId, string loadSessionId)
        {
            if (!TerminalActionReportProtocol.IsTrustedIdentifier(savePointId) || !TerminalActionReportProtocol.IsTrustedIdentifier(loadSessionId)) return "";
            try
            {
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    if (!WorldSimulationService.TableExists(connection, "memory_precision_state")) return "";
                    string current = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                    return TerminalReportLoadProofError(connection, current, TestDict("loadSessionId", loadSessionId,
                        "savePointId", savePointId, "restoreGeneration", current), out _).Length == 0 ? current : "";
                }
            }
            catch (Exception ex) when (!(ex is PersistenceService.CampaignRequestReplacedException) && !(ex is OperationCanceledException)) { return ""; }
        }

        // The admission of this exact accepted report, read without a lock after the caller's
        // generation row lock (which also serializes adoption); null for a v1 marker.
        internal static Dictionary<string, object> TerminalReportMarkerAdmission(ReignDbConnection connection, string actionId, string sha)
        {
            if (string.IsNullOrEmpty(actionId) || !WorldSimulationService.TableExists(connection, "conversation_action_outbox")) return null;
            var stored = DialogueStore.SelectConversationActionOutbox2(connection, TestDict("id", actionId)).FirstOrDefault();
            var marker = ReadDictionary(stored == null ? null : TryParseJsonObject(ReadString(stored, "payload_json", "{}")), "terminalReport");
            return marker != null && ReadString(marker, "sha256", "") == sha ? ReadDictionary(marker, TerminalActionReportProtocol.AdmissionField) : null;
        }

        // Unlocked, read-only probe run before the relationship lock and the acceptance
        // transaction. It only decides whether a first acceptance would evaluate the pair
        // and, if so, which durable row's actor/target to snapshot; the transaction rechecks
        // every predicate under its row locks. It never reads a client-chosen path: both
        // heroes must equal the stored record and the restated report identity. Replays,
        // non-relationship reports, mismatched identity, stale generations, legacy or
        // foreign-owned rows and type mismatches read no files (null).
        // Its predicates must stay no stricter than the transaction's. The only one that can
        // turn from refusing to admitting is the foreign-owner probe: if an owner is removed
        // between this probe and the row locks, the transaction evaluates under a refusing
        // scope, and any file-derived read rolls the whole acceptance back (HTTP 500, nothing
        // committed). The client keeps the report "posted" and re-sends the exact persisted
        // envelope, which re-probes here.
        private static RelationshipsService.RelationshipFileSnapshot CaptureTerminalReportPairFiles(string campaign, string timeline,
            string player, string generation, string actionId, string termsHash, Dictionary<string, object> report)
        {
            var directions = RelationshipsService.CompletedActionRelationshipDirections(report, timeline);
            if (directions.Count == 0) return null;
            string target;
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                if (!WorldSimulationService.TableExists(connection, "conversation_action_outbox") || !WorldSimulationService.TableExists(connection, "memory_precision_state")) return null;
                string current = ReadString(DialogueStore.SelectMemoryPrecisionState2(connection).FirstOrDefault(), "restore_generation", "");
                var stored = DialogueStore.SelectConversationActionOutbox7(connection, TestDict("id", actionId)).FirstOrDefault();
                var row = stored == null ? null : TryParseJsonObject(ReadString(stored, "payload_json", "{}"));
                var record = ReadDictionary(row, "record");
                target = ReadString(record, "targetHeroStringId", "");
                string expectedType = TerminalReportExpectedType(record);
                if (row == null || !TerminalReportScopeMatches(stored, row, record, campaign, actionId, timeline, player, termsHash)
                    || ReadString(report, "actorHeroStringId", "") != player || ReadString(report, "targetHeroStringId", "") != target
                    || ReadString(directions[0], "subjectId", "") != player || ReadString(directions[0], "targetId", "") != target
                    || ReadDictionary(row, "terminalReport") != null || Program.IsTerminalActionReportStatus(ReadString(row, "status", ""))
                    || current.Length == 0 || current != generation
                    || TerminalReportIneligibility(connection, row, campaign, actionId, ReadString(stored, "timeline_id", "")).Length > 0
                    || expectedType.Length == 0 || ReadString(report, "type", "") != expectedType)
                    return null;
            }
            return RelationshipsService.RelationshipFileSnapshot.Capture(campaign, timeline, new[] { player, target });
        }

        private static bool TerminalReportIdentityMatches(Dictionary<string, object> stored, Dictionary<string, object> record,
            string timeline, string player, string termsHash) =>
            ReadString(stored, "fingerprint", "") == termsHash && ReadString(record, "termsHash", "") == termsHash
            && ReadString(stored, "timeline_id", "") == timeline && ReadString(record, "actorHeroStringId", "") == player;

        // The client reports ReignWorldActionType.ToString() for the record's exact transfer
        // command. NormalizeActionCommand stores that same mapped name as type; older rows
        // carry the command only. Any other stored type, or any typeValue (which the client
        // prefers over both), is never accepted. Bind it exactly so a changed type cannot
        // alter relationship exclusions, duel handling or memory adjudication.
        private static string TerminalReportExpectedType(Dictionary<string, object> record)
        {
            string command = ReadString(record, "command", "");
            if (record == null || record.ContainsKey("typeValue") || (command != "transfer_gold" && command != "transfer_item")) return "";
            string expected = Program.MapCommandToActionType(command);
            return !record.TryGetValue("type", out object stored) || (stored is string type && type == expected) ? expected : "";
        }

        // Protected read-only preflight on the same versioned path. It answers, before any
        // POST, whether this exact action would enter the protocol, using the POST's own
        // predicates against one consistent snapshot. It never uses OpenCampaignConnection
        // (which registers/initializes campaigns): a missing campaign is answered from the
        // registry, and the campaign schema is read through a metadata connection inside a
        // REPEATABLE READ READ ONLY transaction with a transaction-local search_path, so
        // PostgreSQL itself refuses any write. Unknown and stale are never legacy proofs.
        internal static Dictionary<string, object> TerminalActionReportPreflightApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            Func<string, string> exact = key => query.TryGetValue(key, out string value) ? value ?? "" : "";
            string campaign = exact("campaignId"), timeline = exact("timelineId"), player = exact("playerHeroStringId");
            string generation = exact("generation"), actionId = exact("actionId"), termsHash = exact("termsHash");
            Dictionary<string, object> Reply(string disposition, string reason, string authoritative = "", string reportType = "") =>
                TestDict("ok", true, "schema", TerminalActionReportProtocol.PreflightSchema, "service", "BannerlordReignServer",
                    "protocol", TerminalActionReportProtocol.RequestSchema, "disposition", disposition, "reason", reason,
                    "campaignId", campaign, "timelineId", timeline, "playerHeroStringId", player, "actionId", actionId,
                    "generation", generation, "termsHash", termsHash, "reportType", reportType, "authoritativeStatus", authoritative);
            if (!new[] { campaign, timeline, player, generation, actionId, termsHash }.All(TerminalActionReportProtocol.IsTrustedIdentifier))
                return TestDict("ok", false, "schema", TerminalActionReportProtocol.PreflightSchema, "error", "The terminal action report preflight query is invalid.");
            // Ineligible is the client's only legacy-fallback permission, so it is answered
            // only after the stored row, its exact ownership and the current generation are
            // proven. Unrepresented classes, unknown rows, conflicts and stale scope never are.
            if (!actionId.StartsWith("pa_", StringComparison.Ordinal)) return Reply(TerminalActionReportProtocol.Unknown, "action_class_not_represented");
            if (!ReignPostgreSqlStorage.CampaignExists(campaign)) return Reply(TerminalActionReportProtocol.Unknown, "unknown_campaign");
            string schema = ReignPostgreSqlStorage.CampaignSchemaName(campaign);
            using (var connection = ReignPostgreSqlStorage.OpenMetadataConnection())
            using (connection.BeginTransaction(System.Data.IsolationLevel.RepeatableRead))
            {
                // First statement of the transaction; disposal rolls back and restores search_path.
                DialogueStore.Set(connection);
                DialogueStore.SelectStatement(connection, TestDict("path", "\"" + schema + "\", reign_meta, public"));
                if (!WorldSimulationService.TableExists(connection, "conversation_action_outbox")) return Reply(TerminalActionReportProtocol.Unknown, "unknown_action");
                string current = !WorldSimulationService.TableExists(connection, "memory_precision_state") ? "" : ReadString(DialogueStore.SelectMemoryPrecisionState2(connection).FirstOrDefault(), "restore_generation", "");
                var stored = DialogueStore.SelectConversationActionOutbox7(connection, TestDict("id", actionId)).FirstOrDefault();
                var row = stored == null ? null : TryParseJsonObject(ReadString(stored, "payload_json", "{}"));
                if (row == null) return Reply(TerminalActionReportProtocol.Unknown, "unknown_action");
                // Same order as POST: ownership/identity, protocol marker, legacy terminal
                // evidence, current generation, then mutable admission rules and type.
                var record = ReadDictionary(row, "record");
                if (!TerminalReportScopeMatches(stored, row, record, campaign, actionId, timeline, player, termsHash))
                    return Reply(TerminalActionReportProtocol.Conflict, "identity_mismatch");
                string reportType = TerminalReportExpectedType(record);
                var marker = ReadDictionary(row, "terminalReport");
                if (marker != null)
                {
                    // Already protocol-owned: the exact pending report may only be replayed by POST,
                    // regardless of any later change to admission rules.
                    if (ReadString(marker, "timelineId", "") != timeline || ReadString(marker, "playerHeroStringId", "") != player)
                        return Reply(TerminalActionReportProtocol.Conflict, "different_terminal_report");
                    if (reportType.Length == 0) return Reply(TerminalActionReportProtocol.Conflict, "action_type_mismatch");
                    if (ReadString(marker, "generation", "") != generation || current != generation)
                        return Reply(TerminalActionReportProtocol.Stale, "generation_mismatch");
                    return Reply(TerminalActionReportProtocol.Accepted, "protocol_owned", ReadString(marker, "authoritativeStatus", ""), reportType);
                }
                if (Program.IsTerminalActionReportStatus(ReadString(row, "status", "")))
                    return Reply(TerminalActionReportProtocol.Conflict, "legacy_terminal_status", ReadString(row, "status", ""));
                if (current.Length == 0 || current != generation) return Reply(TerminalActionReportProtocol.Stale, "generation_mismatch");
                string ineligible = TerminalReportIneligibility(connection, row, campaign, actionId, ReadString(stored, "timeline_id", ""));
                if (ineligible.Length > 0) return Reply(TerminalActionReportProtocol.Ineligible, ineligible);
                if (reportType.Length == 0) return Reply(TerminalActionReportProtocol.Conflict, "action_type_mismatch");
                return Reply(TerminalActionReportProtocol.Eligible, "eligible", "", reportType);
            }
        }

        // First eligible class: an independent current-player gold/item handover
        // created by QueuePlayerTransfers and published atomically. Its legacy
        // projections are the shared-activity receipt, action memory and the
        // directional relationship pair. Marriage/diplomacy apply only to the
        // director source, ambassador needs a referral ID, and the negotiation and
        // rebellion rows are probed here. Everything else stays on the legacy path.
        private static string TerminalReportIneligibility(ReignDbConnection connection, Dictionary<string, object> row,
            string campaign, string actionId, string outboxTimeline)
        {
            var record = ReadDictionary(row, "record");
            var terms = TryParseJsonObject(ReadString(record, "termsJson", "{}")) ?? new Dictionary<string, object>();
            var grant = ReadDictionary(terms, "playerAction");
            string command = Program.CanonicalCommand(ReadString(record, "command", ""));
            string actor = ReadString(record, "actorHeroStringId", ""), target = ReadString(record, "targetHeroStringId", "");
            if (record == null || grant == null || ReadString(row, "id", "") != actionId || ReadString(row, "campaignId", "") != campaign
                || ReadString(record, "serverActionId", "") != actionId || ReadString(record, "campaignId", "") != campaign
                || ReadString(record, "source", "") != "dialogue_player_action" || ReadString(record, "authorizationMode", "") != PlayerActionRules.Policy
                || (command != "transfer_gold" && command != "transfer_item")
                || ReadString(row, "publicationEvidence", "") != "independent_player_intent" || ReadString(row, "publicationGeneration", "").Length == 0
                || ReadString(grant, "schema", "") != PlayerActionRules.Schema || ReadString(grant, "campaignId", "") != campaign
                || ReadString(grant, "timelineId", "") != outboxTimeline
                || PlayerActionRules.ActionId(campaign, outboxTimeline, ReadString(grant, "turnId", ""), ReadInt(grant, "slot", -1)) != actionId
                || actor.Length == 0 || target.Length == 0 || actor == target
                || ReadString(terms, "fromHeroStringId", "") != actor || ReadString(terms, "toHeroStringId", "") != target
                || FirstNonEmpty(ReadString(record, "ambassadorReferralId", ""), ReadString(terms, "ambassadorReferralId", "")).Length > 0)
                return "not_independent_player_transfer";
            foreach (var probe in new[] { "negotiated_action_drafts|queued_action_id", "rebellion_backing_requests|action_id" })
            {
                string[] part = probe.Split('|');
                if (WorldSimulationService.TableExists(connection, part[0]) && DialogueStore.SelectDynamic(connection, part[0], part[1],                    TestDict("id", actionId)).Count > 0) return "foreign_projection_owner";
            }
            return "";
        }

        // The shared-activity receipt (idempotent by fixed ID) and action memory (fresh IDs per
        // participant row) are both serialized on the exact durable outbox row and commit
        // atomically with their stamps, projectedShared and projectedTs: concurrent exact callers
        // and post-commit retries cannot duplicate, skip or re-key them. The shared receipt uses
        // the verified live generation under the lock, never the report's own generation, so a
        // recovered report is not silently skipped. A legacy marker with projectedTs alone gets
        // only its shared receipt; its memory is never rewritten. Lock order matches acceptance
        // (restore generation, then outbox row); no campaign work lock or FileLock is taken while
        // the row lock is held, and nothing here re-runs native effects, relationship acceptance
        // or audit. generation is the report's own; an admitted marker projects only in the
        // generation that admitted it.
        // beforeMarker is a self-test seam; it runs after the memory write, before the stamps.
        private static bool ProjectAcceptedTerminalReport(string campaign, string actionId, string sha, string generation,
            Dictionary<string, object> report, Dictionary<string, object> record, string status,
            Action<ReignDbConnection> beforeMarker = null)
        {
            Func<string, bool> pending = error =>
            {
                Program.LogOperational("action.terminal_report_projection_pending", TestDict("campaignId", campaign, "actionId", actionId, "error", error));
                return false;
            };
            // LogOperational takes FileLock, and legacy ReportAction can hold FileLock while
            // waiting on this outbox row. Pending reasons found under the row lock are recorded
            // here and logged only after transaction and connection disposal (outer finally).
            string deferredPending = null;
            try
            {
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    // Schema (and any one-time migration) stays outside the projection transaction.
                    EnsureSharedActivityMemorySchema(connection);
                    using (var transaction = connection.BeginTransaction())
                    {
                        // Same order and mode as acceptance: the memory trigger updates this row.
                        string current = ReadString(DialogueStore.SelectMemoryPrecisionState3(connection).FirstOrDefault(), "restore_generation", "");
                        var stored = DialogueStore.SelectConversationActionOutbox8(connection, TestDict("id", actionId)).FirstOrDefault();
                        var marker = ReadDictionary(stored == null ? null : TryParseJsonObject(ReadString(stored, "payload_json", "{}")), "terminalReport");
                        // Reread under the lock: the accepted marker must still be this exact report, and
                        // the generation it projects in (its admission, else its own) must be current.
                        string projectionGeneration = FirstNonEmpty(ReadString(ReadDictionary(marker, TerminalActionReportProtocol.AdmissionField), "generation", ""),
                            ReadString(marker, "generation", ""));
                        if (marker == null || ReadString(marker, "sha256", "") != sha || ReadString(marker, "generation", "") != generation
                            || current.Length == 0 || current != projectionGeneration)
                        {
                            deferredPending = "marker_changed";
                            return false;
                        }
                        bool memoryProjected = marker.ContainsKey("projectedTs"), sharedProjected = marker.ContainsKey("projectedShared");
                        // An earlier caller already committed both projections and their stamps together.
                        if (memoryProjected && sharedProjected) return true;
                        if (!sharedProjected && !RecordSharedActionReceipt(connection, report, record, status, current))
                        {
                            deferredPending = "shared_activity_generation_changed";
                            return false;
                        }
                        if (!memoryProjected)
                        {
                            // A refused memory write returns ok=false without throwing; disposal rolls back.
                            var memory = Program.StoreActionResultMemory(report, "actions_report", connection);
                            if (!ReadBool(memory, "ok", false))
                            {
                                deferredPending = "memory_store_refused";
                                return false;
                            }
                        }
                        beforeMarker?.Invoke(connection);
                        DialogueStore.UpdateConversationActionOutbox2(connection, TestDict("id", actionId, "sha", sha, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                        transaction.Commit();
                        return true;
                    }
                }
            }
            catch (Exception ex) when (!(ex is PersistenceService.CampaignRequestReplacedException) && !(ex is OperationCanceledException))
            {
                return pending(ex.GetType().Name);
            }
            finally
            {
                if (deferredPending != null) pending(deferredPending);
            }
        }
    }
}
