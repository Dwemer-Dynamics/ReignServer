using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Shared.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunPlayerActionIntentsSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> add = (id, pass, detail) => results.Add(TestDict("caseId", "player_actions_" + id, "passed", pass, "summary", id, "data", detail));
            string campaign = "player_actions_test_" + Guid.NewGuid().ToString("N");
            Dictionary<string, object> NewContribution(Dictionary<string, object> prior, string id)
            {
                var next = CloneDictionary(prior);
                PlayerInputSources.Remove(next); // A new authored contribution is not an observer clone of the prior one.
                next["sceneTurnId"] = id; next["contributionId"] = id; next["correlationId"] = id;
                next.Remove("inputReceipt"); next.Remove("inputSourceId");
                return next;
            }
            var payload = CloneDictionary(ReadDictionary(DefaultActionTestSnapshot(), "payload"));
            payload["campaignId"] = campaign; payload["timelineId"] = "player_actions_timeline";
            payload["sceneTurnId"] = "captured"; payload["correlationId"] = "captured";
            payload["playerText"] = "*I give her the 2000 denars to join as a companion* You can leave tomorrow.";
            var hero = ReadDictionary(payload, "hero");
            hero["encounteredResident"] = TestDict("recruitmentAgreementId", "resident:fixture");
            var failure = RunPlayerActionTurn(payload, () => TestDict("ok", false, "error", "Injected provider failure"));
            var rows = ReadDictionaryList(failure, "queuedDialogueActions");
            add("captured_payment_survives_reply_failure", ReadBool(failure, "ok", false) && rows.Count == 1
                && ReadString(ReadDictionary(rows.FirstOrDefault(), "record"), "authorizationMode", "") == PlayerActionRules.Policy, failure);
            var repeat = RunPlayerActionTurn(payload, () => TestDict("ok", false));
            add("retry_keeps_one_outbox_record", ReadDictionaryList(repeat, "queuedDialogueActions").Count == 1 && ReadDurableConversationActions(campaign).Count == 1, repeat);
            var first = rows.FirstOrDefault();
            if (first != null)
            {
                var poll = NextActions(TestDict("campaignId", campaign), new Dictionary<string, string>());
                var delivered = ReadDurableConversationAction(campaign, ReadString(first, "id", ""));
                delivered["deliveredTs"] = 1L; SaveDurableConversationAction(campaign, delivered);
                var retry = NextActions(TestDict("campaignId", campaign), new Dictionary<string, string>());
                add("lost_delivery_ack_reuses_id", ReadDictionaryList(poll, "actions").Count == 1
                    && ReadDictionaryList(retry, "actions").Count == 1
                    && ReadString(ReadDictionaryList(retry, "actions").FirstOrDefault(), "serverActionId", "") == ReadString(first, "id", ""), retry);
                // SF5: a row the client keeps holding backs off (capped) but is never expired or dropped.
                string heldId = ReadString(first, "id", "");
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var delays = Enumerable.Range(0, 64).Select(n => ActionRedeliveryDelaySeconds(n)).ToList();
                add("redelivery_cadence_bounded_backoff", delays.Take(5).All(d => d == 30) && delays[5] == 60 && delays[8] == 480
                    && delays.Zip(delays.Skip(1), (a, b) => b >= a).All(x => x) && delays.Max() == 1800 && delays[63] == 1800
                    && ActionRedeliveryDelaySeconds(long.MaxValue) == 1800, delays);
                Func<long, long, string, int> pollHeld = (count, sinceDelivery, rowGeneration) =>
                {
                    var held = ReadDurableConversationAction(campaign, heldId);
                    held["ts"] = 1L; held["deliveryCount"] = count; held["deliveredTs"] = now - sinceDelivery;
                    held["deliveryGeneration"] = rowGeneration; SaveDurableConversationAction(campaign, held);
                    return ReadDictionaryList(NextActions(TestDict("campaignId", campaign), new Dictionary<string, string>()), "actions")
                        .Count(a => ReadString(a, "serverActionId", "") == heldId);
                };
                string liveGeneration = ReadString(ReadDurableConversationAction(campaign, heldId), "deliveryGeneration", "");
                int backedOff = pollHeld(8, 100, liveGeneration);
                var afterBackedOff = ReadDurableConversationAction(campaign, heldId);
                add("redelivery_backed_off_row_waits_without_write", backedOff == 0 && ReadLong(afterBackedOff, "deliveryCount", 0) == 8
                    && ReadLong(afterBackedOff, "deliveredTs", 0) == now - 100 && ReadString(afterBackedOff, "status", "") == "delivered", afterBackedOff);
                int dueHeld = pollHeld(8, 500, liveGeneration);
                var afterDue = ReadDurableConversationAction(campaign, heldId);
                add("redelivery_backed_off_row_delivered_when_due_never_expired", dueHeld == 1 && ReadLong(afterDue, "deliveryCount", 0) == 9
                    && ReadLong(afterDue, "deliveredTs", 0) >= now && ReadString(afterDue, "status", "") == "delivered"
                    && !afterDue.ContainsKey("expiredTs"), afterDue);
                add("redelivery_capped_row_still_delivered", pollHeld(40, 1800, liveGeneration) == 1 && pollHeld(41, 1799, liveGeneration) == 0, null);
                add("redelivery_restore_generation_resets_wait", pollHeld(12, 100, "superseded_generation") == 1
                    && ReadString(ReadDurableConversationAction(campaign, heldId), "deliveryGeneration", "") == liveGeneration, null);
                var executing = ReadDurableConversationAction(campaign, heldId); executing["status"] = "executing"; SaveDurableConversationAction(campaign, executing);
                int executingDelivered = pollHeld(9, 1000, liveGeneration);
                var afterExecuting = ReadDurableConversationAction(campaign, heldId);
                add("redelivery_keeps_later_status_and_records_delivery", executingDelivered == 1 && ReadString(afterExecuting, "status", "") == "executing"
                    && ReadLong(afterExecuting, "deliveryCount", 0) == 10 && ReadLong(afterExecuting, "deliveredTs", 0) >= now
                    && pollHeld(10, 100, liveGeneration) == 0, afterExecuting);
                var holdSummary = UnresolvedActionDeliverySummary(campaign);
                var holdSample = ReadDictionaryList(holdSummary, "samples").FirstOrDefault(s => ReadString(s, "actionId", "") == heldId);
                add("redelivery_diagnostic_reports_backed_off_row", ReadBool(holdSummary, "ok", false) && ReadInt(holdSummary, "unresolvedCount", 0) == 1
                    && ReadInt(holdSummary, "backedOffCount", 0) == 1 && ReadInt(ReadDictionary(holdSummary, "byStatus"), "executing", 0) == 1
                    && ReadLong(holdSummary, "oldestAgeSeconds", 0) >= now - 1 && holdSample != null && ReadLong(holdSample, "deliveryCount", 0) == 10
                    && ReadString(holdSample, "command", "").Length > 0
                    && ReadInt(ReadDictionary(ActionFailures(TestDict("campaignId", campaign), new Dictionary<string, string>()), "unresolvedDeliveries"), "backedOffCount", 0) == 1, holdSummary);
                ReportAction(TestDict("campaignId", campaign, "actionId", ReadString(first, "id", ""), "serverActionId", ReadString(first, "id", ""), "status", "completed", "message", "Verified fixture transfer."));
                add("terminal_report_stops_delivery", ReadDictionaryList(NextActions(TestDict("campaignId", campaign), new Dictionary<string, string>()), "actions").Count == 0, null);
                var resolvedSummary = UnresolvedActionDeliverySummary(campaign);
                add("redelivery_backed_off_row_accepts_terminal_report", ReadString(ReadDurableConversationAction(campaign, heldId), "status", "") == "completed"
                    && ReadBool(resolvedSummary, "ok", false) && ReadInt(resolvedSummary, "unresolvedCount", -1) == 0, resolvedSummary);
                var stale = CloneDictionary(first); stale["status"] = "rejected";
                SaveDurableConversationAction(campaign, stale);
                add("late_reply_cannot_overwrite_applied_receipt", ReadString(stale, "status", "") == "completed"
                    && ReadString(ReadDurableConversationAction(campaign, ReadString(first, "id", "")), "status", "") == "completed", stale);
            }
            var alteredRetry = CloneDictionary(payload); alteredRetry["playerText"] = "I give you 2500 gold to join as a companion.";
            PlayerInputSources.Remove(alteredRetry);
            bool changedInputRejected = false, changedInputGenerated = false;
            try { RunPlayerActionTurn(alteredRetry, () => { changedInputGenerated = true; return TestDict("ok", true, "reply", "I will consider joining."); }); }
            catch (InvalidOperationException) { changedInputRejected = true; }
            add("same_turn_changed_terms_conflict", changedInputRejected && !changedInputGenerated
                && ReadDurableConversationActions(campaign).Count == 1, null);

            payload = NewContribution(payload, "two_gifts"); payload["playerText"] = "I give you 100 gold and I give you 50 coins.";
            var two = RunPlayerActionTurn(payload, () => throw new InvalidOperationException("Injected reply exception"));
            add("two_independent_intents_survive_exception", ReadDictionaryList(two, "queuedDialogueActions").Count == 2, two);
            // Opt-in terminal report: interruption, exact replay, identity conflict and the staged action delta.
            string protocolId = ReadString(ReadDictionaryList(two, "queuedDialogueActions").FirstOrDefault(), "id", "");
            var protocolRecord = ReadDictionary(ReadDurableConversationAction(campaign, protocolId), "record");
            string protocolActor = ReadString(protocolRecord, "actorHeroStringId", ""), protocolGeneration;
            using (var connection = OpenCampaignConnection(campaign))
                protocolGeneration = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            Func<string, string, string> protocolReport = (player, message) => Json.Serialize(TestDict("campaignId", campaign,
                "serverActionId", protocolId, "actionId", protocolId, "timelineId", "player_actions_timeline", "playerHeroStringId", player,
                "sharedActivityGeneration", protocolGeneration, "status", "completed", "type", "RegularTransferGold", "message", message,
                "actorHeroStringId", protocolActor, "targetHeroStringId", ReadString(protocolRecord, "targetHeroStringId", ""),
                "termsHash", ReadString(protocolRecord, "termsHash", ""), "worldDay", 20d));
            Func<string, string, Dictionary<string, object>> protocolEnvelope = (player, json) =>
            {
                Reign.Core.Contracts.Actions.TerminalActionReportProtocol.TryReportSha256(json, out string sha, out _);
                return TestDict("schema", Reign.Core.Contracts.Actions.TerminalActionReportProtocol.RequestSchema, "campaignId", campaign,
                    "timelineId", "player_actions_timeline", "playerHeroStringId", player, "generation", protocolGeneration, "actionId", protocolId,
                    "termsHash", ReadString(protocolRecord, "termsHash", ""), "status", "completed", "reportJson", json, "reportSha256", sha);
            };
            string protocolJson = protocolReport(protocolActor, "A kind gift was handed over with thanks.");
            Func<string, int> protocolCount = sql => { using (var c = OpenCampaignConnection(campaign)) return QuerySql(c, sql, TestDict("id", protocolId)).Count; };
            const string directionalSql = "SELECT event_id FROM relationship_interaction_receipts WHERE event_id LIKE 'action_relationship_' || $id || '_%';";
            const string actionReceiptSql = "SELECT receipt_id FROM conversation_relationship_receipts WHERE source_turn_id=$id AND mode='completed_action' AND status='action_effect' AND severity_tier='routine';";
            // Derived court-popularity metrics and dependent standing/reputation rows for the action pair.
            var derivedArgs = TestDict("campaign", campaign, "actor", protocolActor, "target", ReadString(protocolRecord, "targetHeroStringId", ""));
            const string derivedMetricSql = "SELECT subject_id,positive_count,negative_count,calculated_day FROM court_dynamic_reputation_metrics WHERE campaign_id=$campaign AND timeline_id='player_actions_timeline' AND domain='popularity' AND subject_id IN ($actor,$target) ORDER BY subject_id;";
            Func<ReignDbConnection, string> derivedSnapshot = c => Json.Serialize(new object[] { QuerySql(c, derivedMetricSql, derivedArgs),
                QuerySql(c, "SELECT subject_id,standing_value,revision FROM character_public_standing WHERE campaign_id=$campaign AND timeline_id='player_actions_timeline' AND subject_id IN ($actor,$target) ORDER BY subject_id;", derivedArgs),
                QuerySql(c, "SELECT subject_id,tag_id,status,reputation_value FROM character_reputations WHERE campaign_id=$campaign AND timeline_id='player_actions_timeline' AND subject_id IN ($actor,$target) ORDER BY subject_id,tag_id;", derivedArgs) });
            Func<string> derivedCommitted = () => { using (var c = OpenCampaignConnection(campaign)) { EnsureSocialReputationSchema(c); EnsureCourtSocialReputationSchema(c); return derivedSnapshot(c); } };
            string derivedBefore = derivedCommitted();
            int derivedMetricsBefore, derivedMetricsInTransaction = -1;
            using (var c = OpenCampaignConnection(campaign)) derivedMetricsBefore = QuerySql(c, derivedMetricSql, derivedArgs).Count;
            bool interrupted = false;
            try
            {
                TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson), (step, c) =>
                {
                    if (step != 2) return;
                    derivedMetricsInTransaction = QuerySql(c, derivedMetricSql, derivedArgs).Count(r => ReadDouble(r, "calculated_day", 0d) == 20d);
                    throw new InvalidOperationException("Injected pre-commit failure.");
                });
            }
            catch (InvalidOperationException) { interrupted = true; }
            var afterInterrupt = ReadDurableConversationAction(campaign, protocolId);
            add("terminal_report_interruption_rolls_back_everything", interrupted && !IsProtocolOwnedTerminalAction(afterInterrupt)
                && !IsTerminalActionReportStatus(ReadString(afterInterrupt, "status", "")) && protocolCount(directionalSql) == 0 && protocolCount(actionReceiptSql) == 0, afterInterrupt);
            add("terminal_report_interruption_rolls_back_derived_standing", derivedMetricsBefore == 0 && derivedMetricsInTransaction > 0
                && derivedCommitted() == derivedBefore, TestDict("inTransaction", derivedMetricsInTransaction, "before", derivedBefore));
            // Read-only preflight: the same predicates, distinct dispositions, no writes and no campaign creation.
            Func<string, string, string, string, Dictionary<string, object>> preflight = (camp, id, gen, terms) => TerminalActionReportPreflightApi(
                new Dictionary<string, string> { ["campaignId"] = camp, ["timelineId"] = "player_actions_timeline", ["playerHeroStringId"] = protocolActor,
                    ["generation"] = gen, ["actionId"] = id, ["termsHash"] = terms });
            var changedType = TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson.Replace("\"RegularTransferGold\"", "\"RegularDuelPlayer\"")));
            // The stored record carries the normalized type; a different stored type or any typeValue
            // override is rejected by GET and POST, then the exact original row text is restored.
            Func<string> storedPayload = () => { using (var c = OpenCampaignConnection(campaign)) return ReadString(QuerySql(c,
                "SELECT payload_json FROM conversation_action_outbox WHERE action_id=$id;", TestDict("id", protocolId)).Single(), "payload_json", ""); };
            string originalPayload = storedPayload();
            Func<string, bool> storedOverrideRejected = patch =>
            {
                using (var c = OpenCampaignConnection(campaign)) ExecuteSql(c,
                    "UPDATE conversation_action_outbox SET payload_json=jsonb_set(payload_json::jsonb, '{record}', (payload_json::jsonb->'record') || $patch::jsonb)::text WHERE action_id=$id;",
                    TestDict("id", protocolId, "patch", patch));
                var get = preflight(campaign, protocolId, protocolGeneration, ReadString(protocolRecord, "termsHash", ""));
                var post = TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson));
                using (var c = OpenCampaignConnection(campaign)) ExecuteSql(c,
                    "UPDATE conversation_action_outbox SET payload_json=$payload WHERE action_id=$id;", TestDict("id", protocolId, "payload", originalPayload));
                return ReadString(get, "disposition", "") == "conflict" && ReadString(get, "reason", "") == "action_type_mismatch"
                    && ReadString(post, "reason", "") == "action_type_mismatch";
            };
            bool storedOverridesRejected = storedOverrideRejected("{\"type\":\"RegularDuelPlayer\"}") & storedOverrideRejected("{\"typeValue\":30}");
            add("terminal_report_changed_action_type_rejected", ReadString(changedType, "reason", "") == "action_type_mismatch"
                && ReadString(protocolRecord, "command", "") == "transfer_gold" && ReadString(protocolRecord, "type", "") == "RegularTransferGold"
                && !protocolRecord.ContainsKey("typeValue") && storedOverridesRejected && storedPayload() == originalPayload
                && !IsProtocolOwnedTerminalAction(ReadDurableConversationAction(campaign, protocolId)) && protocolCount(directionalSql) == 0, changedType);
            string protocolTerms = ReadString(protocolRecord, "termsHash", ""), rowBeforePreflight = Json.Serialize(ReadDurableConversationAction(campaign, protocolId));
            var eligible = preflight(campaign, protocolId, protocolGeneration, protocolTerms);
            string absentCampaign = "player_actions_absent_" + Guid.NewGuid().ToString("N");
            add("terminal_report_preflight_distinguishes_dispositions", ReadString(eligible, "disposition", "") == "eligible"
                && ReadString(eligible, "schema", "") == Reign.Core.Contracts.Actions.TerminalActionReportProtocol.PreflightSchema
                && ReadString(eligible, "reportType", "") == "RegularTransferGold" && ReadString(eligible, "actionId", "") == protocolId
                && ReadString(preflight(campaign, protocolId, "other_generation", protocolTerms), "disposition", "") == "stale"
                && ReadString(preflight(campaign, protocolId, protocolGeneration, new string('0', 64)), "disposition", "") == "conflict"
                && ReadString(preflight(campaign, "pa_absent_action", protocolGeneration, protocolTerms), "disposition", "") == "unknown"
                && ReadString(preflight(campaign, "da_other_class", protocolGeneration, protocolTerms), "disposition", "") == "unknown"
                && ReadString(preflight(absentCampaign, protocolId, protocolGeneration, protocolTerms), "disposition", "") == "unknown", eligible);
            // A known row made ineligible by a foreign projection owner answers ineligible only for
            // its exact current scope; stale or mismatched scope never permits legacy fallback.
            Action<string, bool> foreignOwner = (id, present) => { using (var c = OpenCampaignConnection(campaign)) { EnsureRebellionSchema(c);
                ExecuteSql(c, present ? @"INSERT INTO negotiated_action_drafts(negotiation_id,campaign_id,timeline_id,broker_hero_id,first_kingdom_id,
second_kingdom_id,first_ruler_id,second_ruler_id,command,terms_json,terms_hash,status,created_day,updated_day,expires_day,queued_action_id)
VALUES($n,$campaign,'player_actions_timeline','b','k1','k2','r1','r2','make_peace','{}','h','queued',20,20,30,$id);"
                    : "DELETE FROM negotiated_action_drafts WHERE negotiation_id=$n AND campaign_id=$campaign AND queued_action_id=$id;",
                    TestDict("n", "terminal_report_foreign_" + id, "campaign", campaign, "id", id)); } };
            string foreignId = ReadString(ReadDictionaryList(two, "queuedDialogueActions").ElementAtOrDefault(1), "id", "");
            string foreignTerms = ReadString(ReadDictionary(ReadDurableConversationAction(campaign, foreignId), "record"), "termsHash", "");
            foreignOwner(foreignId, true);
            var foreignIneligible = preflight(campaign, foreignId, protocolGeneration, foreignTerms);
            add("terminal_report_preflight_ineligible_requires_exact_current_scope", foreignId.Length > 0
                && ReadString(foreignIneligible, "disposition", "") == "ineligible" && ReadString(foreignIneligible, "reason", "") == "foreign_projection_owner"
                && ReadString(preflight(campaign, foreignId, "other_generation", foreignTerms), "disposition", "") == "stale"
                && ReadString(preflight(campaign, foreignId, protocolGeneration, new string('0', 64)), "disposition", "") == "conflict", foreignIneligible);
            foreignOwner(foreignId, false);
            add("terminal_report_preflight_is_write_free", Json.Serialize(ReadDurableConversationAction(campaign, protocolId)) == rowBeforePreflight
                && !ReignPostgreSqlStorage.CampaignExists(absentCampaign), null);
            // FileLock contention on the real first acceptance. Once the acceptance holds its
            // generation and outbox row locks (seam 0), a peer takes FileLock and makes an actual
            // durable publication, which queues behind that transaction. A whoremonger source on
            // the target's standing plus a spouse profile makes the pair resolution read
            // profile.json; the baseline read it under FileLock with those row locks held, so the
            // acceptance waited on the peer while the peer waited on the acceptance. Waits are
            // bounded: if evaluation stalls, a watchdog cancels only the peer's blocked statement
            // (through a metadata connection, never FileLock), the peer releases FileLock, and the
            // acceptance finishes late with the assertion failed.
            string pairTarget = ReadString(protocolRecord, "targetHeroStringId", "");
            string profilePath = CharacterFile(campaign, pairTarget, "profile.json");
            string originalProfile;
            lock (FileLock) originalProfile = System.IO.File.Exists(profilePath) ? System.IO.File.ReadAllText(profilePath) : null;
            var spouseProfile = ReadJsonObject(profilePath); spouseProfile["spouseId"] = protocolActor;
            WriteJsonObject(profilePath, spouseProfile);
            var standingKey = TestDict("campaign", campaign, "timeline", "player_actions_timeline", "subject", pairTarget);
            Dictionary<string, object> priorStanding, ordinaryStanding;
            using (var c = OpenCampaignConnection(campaign))
            {
                EnsureSocialReputationSchema(c);
                priorStanding = QuerySql(c, @"SELECT standing_value,sources_json,revision,calculated_revision,last_changed_day,updated_ts,calculation_status,last_error
FROM character_public_standing WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", standingKey).FirstOrDefault();
                ExecuteSql(c, @"INSERT INTO character_public_standing(campaign_id,timeline_id,subject_id,standing_value,sources_json,revision,calculated_revision,last_changed_day,updated_ts)
VALUES($campaign,$timeline,$subject,-5,$sources,1,1,20,$ts) ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
standing_value=-5,sources_json=$sources,revision=character_public_standing.revision+1,calculated_revision=character_public_standing.revision+1,updated_ts=$ts;",
                    new Dictionary<string, object>(standingKey) { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["sources"] = Json.Serialize(new List<Dictionary<string, object>> { TestDict("sourceType", "rumor", "tagId", "whoremonger",
                            "rawValue", -5, "effectiveContribution", -5d, "sourceId", "filelock_fixture") }) });
                // The ordinary reader, outside any snapshot scope, reads the same profiles from disk.
                ordinaryStanding = ResolveObserverPublicStanding(c, campaign, "player_actions_timeline", protocolActor, pairTarget);
            }
            const string peerActionId = "da_filelock_peer";
            var peerRow = TestDict("id", peerActionId, "campaignId", campaign, "status", "queued", "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                "record", TestDict("serverActionId", peerActionId, "actionId", peerActionId, "campaignId", campaign, "source", "dialogue_auto_commit",
                    "command", "accept_temporary_party_guest", "actorHeroStringId", "npc_batch", "targetHeroStringId", "player_batch",
                    "termsHash", "fixture_terms", "termsJson", Json.Serialize(TestDict("timelineId", "player_actions_timeline"))));
            var peerHolds = new System.Threading.ManualResetEventSlim();
            var evaluated = new System.Threading.ManualResetEventSlim();
            System.Threading.Tasks.Task peer = null, watchdog = null;
            bool peerQueued = false, peerQueuedAtCommit = false, fileLockHeldByPeer = false, unsnappedRefused = false, watchdogFired = false;
            Dictionary<string, object> protocolFirst = null, snapshotStanding = null, peerPublication = null, chemistryAtEvaluation = null;
            string peerError = "", acceptanceError = "", watchdogError = "", restoreError = "";
            int peerPid = 0, watchdogCancelled = 0;
            // Backends currently waiting on a lock held by this connection's backend. pg_locks
            // and pg_blocking_pids are read live on every call; pg_stat_activity is a snapshot
            // fixed at its first read in a transaction and would miss a peer backend opened later.
            Func<ReignDbConnection, List<int>> blockedWaiters = c => QuerySql(c,
                "SELECT DISTINCT pid FROM pg_locks WHERE NOT granted AND pg_backend_pid()=ANY(pg_blocking_pids(pid));")
                .Select(r => ReadInt(r, "pid", 0)).Where(pid => pid != 0).ToList();
            try
            {
                protocolFirst = TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson), (step, c) =>
                {
                    if (step == 0)
                    {
                        int acceptancePid = ReadInt(QuerySql(c, "SELECT pg_backend_pid() AS pid;").Single(), "pid", 0);
                        peer = System.Threading.Tasks.Task.Run(() =>
                        {
                            lock (FileLock)
                            {
                                peerHolds.Set();
                                try
                                {
                                    peerPublication = PublishConversationActionBatch(campaign, TestDict("campaignId", campaign,
                                        "timelineId", "player_actions_timeline", "sceneTurnId", "filelock_peer"),
                                        TestDict("ok", true, "reply", "Agreed."), new List<Dictionary<string, object>> { peerRow }, protocolGeneration);
                                }
                                catch (Exception ex) { peerError = ex.GetType().Name; }
                            }
                        });
                        try
                        {
                            // Continue only once the peer, holding FileLock, is the one backend queued
                            // behind this transaction; that exact pid is the only one ever cancelled.
                            if (peerHolds.Wait(10000))
                                for (int i = 0; i < 400 && !peerQueued && !peer.IsCompleted; i++)
                                {
                                    List<int> waiters = blockedWaiters(c);
                                    if (waiters.Count == 1) { peerPid = waiters[0]; peerQueued = true; }
                                    else peer.Wait(25);
                                }
                        }
                        finally
                        {
                            // Started whenever the peer exists, even if the handshake failed. Bounded:
                            // one 10 s wait, then at most one cancel of the identified peer backend while
                            // it is still waiting on this acceptance. An unidentified peer is never
                            // cancelled; its blocked statement is then bounded by its command timeout.
                            int knownPeer = peerPid;
                            watchdog = System.Threading.Tasks.Task.Run(() =>
                            {
                                try
                                {
                                    if (evaluated.Wait(10000)) return;
                                    watchdogFired = true;
                                    if (knownPeer == 0) return;
                                    using (var meta = ReignPostgreSqlStorage.OpenMetadataConnection())
                                        watchdogCancelled = QuerySql(meta, @"SELECT pg_cancel_backend(pid) AS cancelled FROM (SELECT DISTINCT pid FROM pg_locks
WHERE pid=$peer AND NOT granted AND $acceptance=ANY(pg_blocking_pids(pid))) AS waiting;",
                                            TestDict("peer", knownPeer, "acceptance", acceptancePid)).Count(r => ReadBool(r, "cancelled", false));
                                }
                                catch (Exception ex) { watchdogError = ex.GetType().Name; }
                            });
                        }
                    }
                    else if (step == 1)
                    {
                        evaluated.Set();
                        bool entered = System.Threading.Monitor.TryEnter(FileLock);
                        if (entered) System.Threading.Monitor.Exit(FileLock);
                        fileLockHeldByPeer = !entered;
                        // The pair row exactly as the production evaluation just wrote it in this
                        // transaction, before reputation recompute can change the target's standing.
                        chemistryAtEvaluation = QuerySql(c, @"SELECT hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a FROM relationship_pair_chemistry WHERE pair_key=$pair;",
                            TestDict("pair", AmbientPairKey(protocolActor, pairTarget))).FirstOrDefault();
                        // Inside the acceptance scope the pair resolves from its snapshot; any other hero is refused.
                        snapshotStanding = ResolveObserverPublicStanding(c, campaign, "player_actions_timeline", protocolActor, pairTarget);
                        try { ResolveObserverPublicStanding(c, campaign, "player_actions_timeline", "player_actions_unsnapped_observer", pairTarget); }
                        catch (InvalidOperationException) { unsnappedRefused = true; }
                    }
                    else peerQueuedAtCommit = peerPid != 0 && blockedWaiters(c).Contains(peerPid);
                });
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { acceptanceError = ex.GetType().Name; }
            finally
            {
                // Release and join every worker before restoring the fixture inputs, whatever happened.
                evaluated.Set();
                Func<System.Threading.Tasks.Task, bool> joinWorker = task =>
                {
                    try { return task == null || task.Wait(30000); }
                    catch (AggregateException) { peerError = FirstNonEmpty(peerError, "worker_faulted"); return true; } // Completed.
                };
                bool workersJoined = joinWorker(peer) & joinWorker(watchdog);
                if (!workersJoined) peerError = FirstNonEmpty(peerError, "worker_not_joined");
                else { peerHolds.Dispose(); evaluated.Dispose(); }
                // Never wait unboundedly on FileLock here: a peer that failed to join may still hold it.
                if (System.Threading.Monitor.TryEnter(FileLock, 30000))
                    try
                    {
                        if (originalProfile == null) System.IO.File.Delete(profilePath);
                        else System.IO.File.WriteAllText(profilePath, originalProfile);
                    }
                    finally { System.Threading.Monitor.Exit(FileLock); }
                else restoreError = "profile_restore_skipped_file_lock_busy";
                using (var c = OpenCampaignConnection(campaign))
                    if (priorStanding == null)
                        ExecuteSql(c, "DELETE FROM character_public_standing WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", standingKey);
                    else ExecuteSql(c, @"UPDATE character_public_standing SET standing_value=$value,sources_json=$priorSources,revision=$revision,
calculated_revision=$calculated,last_changed_day=$day,updated_ts=$updated,calculation_status=$status,last_error=$error
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;",
                        new Dictionary<string, object>(standingKey) { ["value"] = ReadInt(priorStanding, "standing_value", 0),
                            ["priorSources"] = ReadString(priorStanding, "sources_json", "[]"), ["revision"] = ReadInt(priorStanding, "revision", 1),
                            ["calculated"] = ReadInt(priorStanding, "calculated_revision", 1), ["day"] = ReadDouble(priorStanding, "last_changed_day", 0d),
                            ["updated"] = ReadLong(priorStanding, "updated_ts", 0), ["status"] = ReadString(priorStanding, "calculation_status", "ready"),
                            ["error"] = ReadString(priorStanding, "last_error", "") });
            }
            add("terminal_report_acceptance_never_waits_on_file_lock_holder", peerQueued && peerQueuedAtCommit && fileLockHeldByPeer
                && !watchdogFired && peerError.Length == 0 && acceptanceError.Length == 0 && watchdogError.Length == 0 && restoreError.Length == 0
                && ReadString(protocolFirst, "reason", "") == "first_acceptance" && ReadInt(ReadDictionary(protocolFirst, "nativeEffect"), "delta", 0) != 0
                && IsProtocolOwnedTerminalAction(ReadDurableConversationAction(campaign, protocolId)) && protocolCount(actionReceiptSql) == 1
                && ReadBool(peerPublication, "committed", false) && ReadInt(peerPublication, "createdCount", 0) == 1
                && ReadDurableConversationAction(campaign, peerActionId) != null,
                TestDict("peerQueued", peerQueued, "peerQueuedAtCommit", peerQueuedAtCommit, "fileLockHeldByPeer", fileLockHeldByPeer,
                    "watchdogFired", watchdogFired, "watchdogCancelled", watchdogCancelled, "watchdogError", watchdogError,
                    "peerPid", peerPid, "peerError", peerError, "acceptanceError", acceptanceError, "restoreError", restoreError,
                    "acceptance", protocolFirst, "peerPublication", peerPublication));
            // Production consumed the snapshot: ApplyAtomicDirectionalRelationshipDelta wrote each
            // effective column as Clamp(affinity + ResolveObserverPublicStanding(row hero A/B).value)
            // inside the file scope. The actor->target column therefore carries the stored -5 plus the
            // spouse adjustment that only the snapshotted profile.json can supply in that scope (a
            // missing snapshot entry throws); without the adjustment it would be affinity - 5.
            bool actorIsA = ReadString(chemistryAtEvaluation, "hero_a_id", "").Equals(protocolActor, StringComparison.OrdinalIgnoreCase);
            int pairAffinity = ReadInt(chemistryAtEvaluation, actorIsA ? "affinity_a_to_b" : "affinity_b_to_a", 0);
            int pairEffective = ReadInt(chemistryAtEvaluation, actorIsA ? "effective_affinity_a_to_b" : "effective_affinity_b_to_a", int.MinValue);
            bool effectiveConsumedSnapshot = chemistryAtEvaluation != null
                && new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ReadString(chemistryAtEvaluation, "hero_a_id", ""), ReadString(chemistryAtEvaluation, "hero_b_id", "") }
                    .SetEquals(new[] { protocolActor, pairTarget })
                && pairEffective == Clamp(pairAffinity + ReadInt(ordinaryStanding, "value", 0), -100, 100)
                && pairEffective != Clamp(pairAffinity - 5, -100, 100);
            add("terminal_report_pair_snapshot_matches_ordinary_reader", snapshotStanding != null && unsnappedRefused
                && ReadDouble(ordinaryStanding, "whoremongerSpouseAdjustment", 0d) != 0d
                && Json.Serialize(snapshotStanding) == Json.Serialize(ordinaryStanding) && effectiveConsumedSnapshot,
                TestDict("snapshot", snapshotStanding, "ordinary", ordinaryStanding, "unsnappedRefused", unsnappedRefused,
                    "chemistryAtEvaluation", chemistryAtEvaluation, "actorAffinity", pairAffinity, "actorEffective", pairEffective,
                    "effectiveConsumedSnapshot", effectiveConsumedSnapshot));
            // Keep the remaining cases independent of a failed contention case.
            protocolFirst = protocolFirst ?? TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson));
            var protocolEffect = ReadDictionary(protocolFirst, "nativeEffect");
            List<int> directionalDeltas;
            using (var connection = OpenCampaignConnection(campaign))
                directionalDeltas = QuerySql(connection, "SELECT native_delta FROM relationship_interaction_receipts WHERE event_id LIKE 'action_relationship_' || $id || '_%';",
                    TestDict("id", protocolId)).Select(r => ReadInt(r, "native_delta", 0)).Where(d => d != 0).ToList();
            add("terminal_report_stages_one_isolated_action_delta", ReadString(protocolFirst, "disposition", "") == "accepted"
                && ReadInt(protocolEffect, "delta", 0) != 0 && ReadInt(protocolEffect, "delta", 0) == CompletedActionSharedDelta(directionalDeltas)
                && protocolCount(directionalSql) == 2 && protocolCount(actionReceiptSql) == 1
                && IsProtocolOwnedTerminalAction(ReadDurableConversationAction(campaign, protocolId)), protocolFirst);
            // The retried first acceptance commits exactly one derived metric per changed target, at the report day.
            List<string> derivedTargets;
            List<Dictionary<string, object>> derivedMetrics;
            using (var connection = OpenCampaignConnection(campaign))
            {
                derivedTargets = QuerySql(connection, "SELECT target_id FROM relationship_interaction_receipts WHERE event_id LIKE 'action_relationship_' || $id || '_%' AND native_delta<>0;",
                    TestDict("id", protocolId)).Select(r => ReadString(r, "target_id", "")).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                derivedMetrics = QuerySql(connection, derivedMetricSql, derivedArgs);
            }
            string derivedAfterFirst = derivedCommitted();
            add("terminal_report_commits_one_set_of_derived_writes", derivedTargets.Count > 0
                && derivedMetrics.Select(r => ReadString(r, "subject_id", "")).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(derivedTargets)
                && derivedMetrics.All(r => ReadDouble(r, "calculated_day", 0d) == 20d)
                && ReadString(protocolFirst, "memoryProjectionStatus", "").Length > 0 && !protocolFirst.ContainsKey("projectionStatus"),
                TestDict("targets", derivedTargets, "metrics", derivedMetrics));
            // Action memory commits with projectedTs under the exact outbox row lock: one row per owner, stable IDs.
            const string memorySql = "SELECT owner_id,memory_id FROM memories WHERE event_id='action_' || $id ORDER BY owner_id,memory_id;";
            Func<ReignDbConnection, string> memoryIn = c => Json.Serialize(QuerySql(c, memorySql, TestDict("id", protocolId)));
            Func<string> memoryRows = () => { using (var c = OpenCampaignConnection(campaign)) return memoryIn(c); };
            Func<bool> onePerOwner = () => { using (var c = OpenCampaignConnection(campaign)) { var rows = QuerySql(c, memorySql, TestDict("id", protocolId));
                return rows.Count > 0 && rows.Select(r => ReadString(r, "owner_id", "")).Distinct().Count() == rows.Count; } };
            Func<bool> markerProjected = () => ReadDictionary(ReadDurableConversationAction(campaign, protocolId), "terminalReport")?.ContainsKey("projectedTs") == true;
            Action clearProjected = () => { using (var c = OpenCampaignConnection(campaign)) ExecuteSql(c,
                "UPDATE conversation_action_outbox SET payload_json=(payload_json::jsonb #- '{terminalReport,projectedTs}')::text WHERE action_id=$id;", TestDict("id", protocolId)); };
            Func<Action<ReignDbConnection>, bool> project = seam => ProjectAcceptedTerminalReport(campaign, protocolId, ReadString(protocolFirst, "reportSha256", ""),
                protocolGeneration, TryParseJsonObject(protocolJson), protocolRecord, ReadString(protocolFirst, "authoritativeStatus", ""), seam);
            string firstMemory = memoryRows();
            bool repeatsCompleted = new[] { 0, 1 }.All(_ => ReadString(TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson)), "memoryProjectionStatus", "") == "completed");
            add("terminal_report_exact_repeat_keeps_one_stable_memory_per_owner", ReadString(protocolFirst, "memoryProjectionStatus", "") == "completed"
                && markerProjected() && onePerOwner() && repeatsCompleted && memoryRows() == firstMemory, firstMemory);
            // A failure after the memory write but before projectedTs rolls both back and stays pending.
            clearProjected();
            bool rewrittenInTransaction = false;
            bool failedProjection = project(c => { rewrittenInTransaction = memoryIn(c) != firstMemory; throw new InvalidOperationException("Injected pre-marker failure."); });
            add("terminal_report_pre_marker_failure_rolls_back_memory", !failedProjection && rewrittenInTransaction
                && memoryRows() == firstMemory && !markerProjected(), memoryRows());
            bool resumed = project(null);
            string resumedMemory = memoryRows();
            add("terminal_report_resumed_projection_is_stable", resumed && markerProjected() && onePerOwner() && project(null)
                && ReadString(TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson)), "memoryProjectionStatus", "") == "completed"
                && memoryRows() == resumedMemory, resumedMemory);
            // Concurrent exact projection: the second caller waits on the row lock, then sees projectedTs.
            clearProjected();
            string lockedMemory = "";
            System.Threading.Tasks.Task<bool> concurrent = null;
            bool lockHolder = project(c => { lockedMemory = memoryIn(c); concurrent = System.Threading.Tasks.Task.Run(() => project(null)); concurrent.Wait(250); });
            bool waiter = concurrent != null && concurrent.Wait(30000) && concurrent.Result;
            add("terminal_report_concurrent_projection_writes_once", lockHolder && waiter && markerProjected() && onePerOwner()
                && lockedMemory.Length > 0 && memoryRows() == lockedMemory, TestDict("locked", lockedMemory, "committed", memoryRows()));
            // Generation upgrade under contention: a writer holds the generation shared and the outbox row, a
            // projection queues behind it, then the holder's memory trigger updates the generation row. A
            // projection that queued on the outbox while holding the generation shared deadlocks that upgrade.
            clearProjected();
            System.Threading.Tasks.Task<bool> queued = null;
            bool queuedObserved = false, holderUpgraded = false;
            string holderError = "";
            using (var c = OpenCampaignConnection(campaign))
            using (var transaction = c.BeginTransaction())
            {
                Func<long> revision = () => ReadLong(QuerySql(c, "SELECT source_revision FROM memory_precision_state WHERE state_id='current';").Single(), "source_revision", 0);
                QuerySql(c, "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR SHARE;");
                QuerySql(c, "SELECT action_id FROM conversation_action_outbox WHERE action_id=$id FOR UPDATE;", TestDict("id", protocolId));
                long revisionBefore = revision();
                queued = System.Threading.Tasks.Task.Run(() => project(null));
                // Proceed only once the projection is actually blocked by this transaction.
                for (int i = 0; i < 300 && !queuedObserved && !queued.IsCompleted; i++)
                {
                    queuedObserved = ReadLong(QuerySql(c, "SELECT COUNT(*) AS n FROM pg_stat_activity WHERE pg_backend_pid()=ANY(pg_blocking_pids(pid));").Single(), "n", 0) > 0;
                    if (!queuedObserved) System.Threading.Thread.Sleep(50);
                }
                try
                {
                    // A statement trigger fires with zero rows: the real memory trigger upgrades the generation row.
                    ExecuteSql(c, "DELETE FROM memories WHERE false;");
                    holderUpgraded = revision() > revisionBefore;
                    transaction.Commit();
                }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { holderUpgraded = false; holderError = ex.GetType().Name; }
            }
            bool queuedProjected = queued != null && queued.Wait(30000) && queued.Result;
            add("terminal_report_queued_projection_never_blocks_generation_upgrade", queuedObserved && holderUpgraded && queuedProjected
                && markerProjected() && onePerOwner(), TestDict("queued", queuedObserved, "upgraded", holderUpgraded, "projected", queuedProjected, "error", holderError));
            var protocolReplay =TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson));
            add("terminal_report_exact_replay_leaves_derived_writes", derivedCommitted() == derivedAfterFirst, null);
            add("terminal_report_exact_replay_reuses_receipt", ReadString(protocolReplay, "reason", "") == "exact_replay"
                && ReadString(ReadDictionary(protocolReplay, "nativeEffect"), "receiptId", "") == ReadString(protocolEffect, "receiptId", "")
                && ReadInt(ReadDictionary(protocolReplay, "nativeEffect"), "delta", 0) == ReadInt(protocolEffect, "delta", 0)
                && protocolCount(directionalSql) == 2 && protocolCount(actionReceiptSql) == 1, protocolReplay);
            // A foreign projection owner appearing after acceptance cannot downgrade the protocol owner.
            foreignOwner(protocolId, true);
            var foreignReplay = TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson));
            var foreignOwned = preflight(campaign, protocolId, protocolGeneration, protocolTerms);
            foreignOwner(protocolId, false);
            add("terminal_report_accepted_owner_survives_later_foreign_owner", ReadString(foreignReplay, "reason", "") == "exact_replay"
                && ReadString(foreignOwned, "disposition", "") == "accepted" && ReadString(foreignOwned, "reason", "") == "protocol_owned"
                && protocolCount(directionalSql) == 2 && protocolCount(actionReceiptSql) == 1, foreignReplay);
            add("terminal_report_changed_payload_or_owner_conflicts",
                ReadString(TerminalActionReportApi(protocolEnvelope(protocolActor, protocolReport(protocolActor, "A different result."))), "disposition", "") == "conflict"
                && ReadString(TerminalActionReportApi(protocolEnvelope("intruder_hero", protocolReport("intruder_hero", "A kind gift was handed over with thanks."))), "disposition", "") == "conflict"
                && ReadString(ReadDictionary(ReadDurableConversationAction(campaign, protocolId), "terminalReport"), "sha256", "") == ReadString(protocolFirst, "reportSha256", ""), null);
            var ownedPreflight = preflight(campaign, protocolId, protocolGeneration, protocolTerms);
            add("terminal_report_preflight_reports_protocol_owner", ReadString(ownedPreflight, "disposition", "") == "accepted"
                && ReadString(ownedPreflight, "reason", "") == "protocol_owned", ownedPreflight);
            // A late legacy report of the same status is answered from the protocol row without projections.
            var legacyLate = ReportAction(TestDict("campaignId", campaign, "serverActionId", protocolId, "status", "completed", "result", TestDict("message", "Late legacy report.")));
            add("terminal_report_legacy_report_defers_to_protocol_owner", ReadString(legacyLate, "terminalReportOwner", "") == "protocol"
                && ReadString(legacyLate, "status", "") == "completed" && !legacyLate.ContainsKey("memoryStore")
                && ReadString(ReadDictionary(ReadDurableConversationAction(campaign, protocolId), "report"), "message", "") == "A kind gift was handed over with thanks."
                && protocolCount(directionalSql) == 2 && protocolCount(actionReceiptSql) == 1, legacyLate);
            // A legacy terminal winner cannot be upgraded into the protocol.
            string legacyId = ReadString(ReadDictionaryList(two, "queuedDialogueActions").ElementAtOrDefault(1), "id", "");
            ReportAction(TestDict("campaignId", campaign, "serverActionId", legacyId, "status", "completed", "result", TestDict("message", "Legacy handover.")));
            var legacyWinner = preflight(campaign, legacyId, protocolGeneration, ReadString(ReadDictionary(ReadDurableConversationAction(campaign, legacyId), "record"), "termsHash", ""));
            add("terminal_report_preflight_keeps_legacy_winner", legacyId.Length > 0 && ReadString(legacyWinner, "disposition", "") == "conflict"
                && ReadString(legacyWinner, "reason", "") == "legacy_terminal_status"
                && !IsProtocolOwnedTerminalAction(ReadDurableConversationAction(campaign, legacyId)), legacyWinner);
            var legacyOverwrite = ReadDurableConversationAction(campaign, protocolId); legacyOverwrite["report"] = TestDict("message", "Saved action result.");
            SaveDurableConversationAction(campaign, legacyOverwrite);
            add("terminal_report_evidence_survives_same_status_legacy_save", ReadString(ReadDictionary(ReadDurableConversationAction(campaign, protocolId), "report"), "message", "")
                == "A kind gift was handed over with thanks.", null);
            Func<string, Dictionary<string, object>> protocolAck = ackGeneration => ConversationRelationshipNativeReceiptApi(TestDict("campaignId", campaign,
                "receiptIds", new List<string> { ReadString(protocolEffect, "receiptId", "") }, "nativeReceipt", TestDict(
                    "schema", "reign-native-relationship-receipt-v2", "subjectId", ReadString(protocolEffect, "subjectId", ""), "targetId", ReadString(protocolEffect, "targetId", ""),
                    "priorNativeRelation", 0, "nativeRelation", ReadInt(protocolEffect, "delta", 0), "delta", ReadInt(protocolEffect, "delta", 0),
                    "timelineId", "player_actions_timeline", "reportGeneration", ackGeneration, "reportSha256", ReadString(protocolFirst, "reportSha256", ""))));
            add("terminal_report_ack_requires_generation_binding", !ReadBool(protocolAck("other_generation"), "ok", true)
                && ReadBool(protocolAck(protocolGeneration), "ok", false), null);
            payload = NewContribution(payload, "refused_gift"); payload["playerText"] = "I give you 100 gold.";
            var refused = RunPlayerActionTurn(payload, () => TestDict("ok", true, "reply", "I refuse to accept your gold."));
            add("specific_gift_refusal_has_no_transfer", ReadDictionaryList(refused, "queuedDialogueActions").Count == 0, refused);
            payload = NewContribution(payload, "refused_job");
            var job = RunPlayerActionTurn(payload, () => TestDict("ok", true, "reply", "I will not join your clan."));
            add("employment_refusal_does_not_cancel_gift", ReadDictionaryList(job, "queuedDialogueActions").Count == 1, job);
            payload = NewContribution(payload, "gesture_only");
            payload["playerText"] = "*I give her a confident glance back so she knows that I am with her as I wait for the responses.*";
            var gesture = RunPlayerActionTurn(payload, () => TestDict("ok", true, "reply", "I am ready to hear them."));
            add("described_glance_is_not_an_item_handover", CurrentPlayerTransfers(payload).Count == 0
                && ReadDictionaryList(gesture, "playerActionOutcomes").Count == 0
                && ReadString(gesture, "reply", "") == "I am ready to hear them.", gesture);
            payload["playerText"] = "I give her an iron sword and offer a smile.";
            add("tangible_gift_with_gesture_is_still_detected", CurrentPlayerTransfers(payload).Count == 1, null);
            payload["channel"] = "correspondence";
            add("letter_cannot_deliver_physical_coins", CurrentPlayerTransfers(payload).Count == 0, null);
            payload["channel"] = "in_person"; payload["guardedPromptOverride"] = true;
            add("test_directive_keeps_existing_gate", CurrentPlayerTransfers(payload).Count == 0, null);
            payload.Remove("guardedPromptOverride");
            var group = CloneDictionary(payload);
            group["attendees"] = new List<Dictionary<string, object>> { TestDict("heroStringId", "npc_a"), TestDict("heroStringId", "npc_b") };
            add("two_npc_attendees_make_you_ambiguous", !SinglePlayerTransferRecipient(group)
                && CurrentPlayerTransfers(group).All(i => i.Error.Length > 0), null);
            var hypothetical = CloneDictionary(payload);
            hypothetical["playerText"] = "For example:\nI give you 2000 gold.";
            var hypotheticalRecord = TestDict("source", "dialogue_auto_commit", "actorHeroStringId", ReadString(payload, "playerHeroStringId", ""));
            var hypotheticalErrors = new List<string>();
            BindDialogueActionAuthority(hypotheticalRecord, TestDict(), hypothetical, "transfer_gold", "RegularTransferGold", "hypothetical", hypotheticalErrors);
            add("npc_planner_cannot_turn_example_into_player_payment", hypotheticalErrors.Any(e => e.Contains("hypothetical")), hypotheticalErrors);
            var together = NewContribution(payload, "pay_and_join");
            together["playerText"] = "I give you 2000 gold to join as a companion.";
            ReadDictionary(ReadDictionary(together, "hero"), "encounteredResident")["schema"] = "reign-encountered-resident-v1";
            together["conversationAgencyReceipt"] = TestDict("ok", true, "receiptId", "fixture_accepted", "updates",
                new List<Dictionary<string, object>> { TestDict("topic", "allegiance", "disposition", "accepted", "id", "agreement_fixture", "revision", 1,
                    "terms", TestDict("gold", 2000)) });
            var joined = RunPlayerActionTurn(together, () => TestDict("ok", true, "reply", "I join you as your companion. My husband could visit later."));
            var joinedRows = ReadDictionaryList(joined, "queuedDialogueActions");
            add("payment_and_companion_are_separate_actions", joinedRows.Count == 2
                && joinedRows.Any(r => ReadString(r, "command", "") == "transfer_gold")
                && joinedRows.Any(r => ReadString(r, "command", "") == "recruit_encountered_resident"), joined);
            var recruitment = joinedRows.FirstOrDefault(r => ReadString(r, "command", "") == "recruit_encountered_resident");
            var recruitmentTerms = TryParseJsonObject(ReadString(ReadDictionary(recruitment, "record"), "termsJson", "{}"));
            add("recruitment_depends_on_verified_advance", ReadStringList(recruitmentTerms, "paymentDependencies").Count == 1
                && ReadInt(recruitmentTerms, "agreedGold", -1) == 2000, recruitmentTerms);
            foreach (string decision in new[] { "I join you as your companion. My husband could visit later.", "I accept. If my family visits, they will need a room." })
                add("unrelated_condition_" + results.Count, ResidentHasUnconditionalConsent(decision), decision);
            foreach (string decision in new[] { "I could join if you pay.", "I will join tomorrow.", "I agree, provided you release me first.", "I join you. But only if my husband agrees.", "I join you. If you pay me." })
                add("real_condition_" + results.Count, !ResidentHasUnconditionalConsent(decision), decision);
            add("purse_claim_requires_receipt", VisibleReplyClaimsCompletedWorldAction("Two thousand denars in my purse.", out _), null);
            var visible = VisibleFailedRepairWithoutEffects(TestDict("reply", "Two thousand denars in my purse. I am your companion."));
            add("invalid_reply_cannot_preserve_success_claim", !ReadString(visible, "reply", "").Contains("in my purse")
                && !ReadString(visible, "reply", "").Contains("I am your companion")
                && ReadString(visible, "reply", "").StartsWith("I hear you.", StringComparison.Ordinal)
                && ReadString(visible, "reply", "").Contains("could not be verified"), visible);
            var facts = TestDict("trait.judgment", 83);
            add("only_matching_evidence_value_normalizes", AgencyEvidenceKey("trait.judgment=83", facts) == "trait.judgment"
                && AgencyEvidenceKey("trait.judgment=82", facts) != "trait.judgment" && AgencyEvidenceKey("trait.pragmatism=83", facts) == "trait.pragmatism=83", null);
            add("typography_does_not_remove_words", AgencyContainsExactQuote("I'm willing to join.", "I’m willing to join.")
                && !AgencyContainsExactQuote("I am not willing to join.", "I am willing to join."), null);
            Func<string, Dictionary<string, object>> stagedNpc = id => TestDict("serverActionId", id, "actionId", id,
                "campaignId", campaign, "source", "dialogue_auto_commit", "command", "accept_temporary_party_guest",
                "actorHeroStringId", "npc_batch", "targetHeroStringId", "player_batch", "termsHash", "fixture_terms",
                "termsJson", Json.Serialize(TestDict("timelineId", "player_actions_timeline")));
            var failedNpcPayload = NewContribution(payload, "failed_npc_without_handover");
            failedNpcPayload["playerText"] = "The weather is pleasant.";
            string failedNpcId = "";
            var failedNpcResult = RunPlayerActionTurn(failedNpcPayload, () => {
                var row = QueueAction(campaign, stagedNpc("unbound")); failedNpcId = ReadString(row, "id", "");
                return TestDict("ok", false, "queuedDialogueActions", new List<Dictionary<string, object>> { row });
            });
            add("failed_npc_response_discards_staged_native_action", !ReadBool(failedNpcResult, "ok", true)
                && ReadDictionaryList(failedNpcResult, "queuedDialogueActions").Count == 0
                && ReadDurableConversationAction(campaign, failedNpcId) == null, failedNpcResult);
            var mixedFailurePayload = NewContribution(payload, "failed_npc_with_handover");
            mixedFailurePayload["playerText"] = "I give you 17 gold.";
            string discardedNpcId = "";
            var mixedFailure = RunPlayerActionTurn(mixedFailurePayload, () => {
                discardedNpcId = ReadString(QueueAction(campaign, stagedNpc("unbound")), "id", "");
                throw new InvalidOperationException("Injected storage failure after action staging.");
            });
            add("failed_npc_keeps_only_independent_player_handover", ReadBool(mixedFailure, "ok", false)
                && ReadDictionaryList(mixedFailure, "queuedDialogueActions").Count == 1
                && ReadString(mixedFailure, "reply", "").StartsWith("I hear you. Let us keep talking", StringComparison.Ordinal)
                && ReadString(ReadDictionaryList(mixedFailure, "queuedDialogueActions").Single(), "id", "").StartsWith("pa_", StringComparison.Ordinal)
                && ReadDurableConversationAction(campaign, discardedNpcId) == null, mixedFailure);

            string generation;
            using (var connection = OpenCampaignConnection(campaign))
                generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            var batchPayload = TestDict("campaignId", campaign, "timelineId", "player_actions_timeline", "sceneTurnId", "atomic_batch");
            Func<string, Dictionary<string, object>> batchRow = id => TestDict("id", id, "campaignId", campaign,
                "status", "queued", "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "record", stagedNpc(id));
            Func<List<Dictionary<string, object>>> batchRows = () => new List<Dictionary<string, object>> { batchRow("da_batch_a"), batchRow("da_batch_b") };
            var acceptedResponse = TestDict("ok", true, "reply", "I agree to the described terms.");
            for (int boundary = 1; boundary <= 3; boundary++)
            {
                bool failed = false, invisible = true;
                try { PublishConversationActionBatch(campaign, batchPayload, acceptedResponse, batchRows(), generation, index => {
                    invisible &= !ReadDurableConversationActions(campaign).Any(r => ReadString(r, "id", "").StartsWith("da_batch_", StringComparison.Ordinal));
                    if (index == boundary) throw new InvalidOperationException("Injected batch boundary failure.");
                }); } catch (InvalidOperationException) { failed = true; }
                add("publication_fault_rolls_back_all_actions_" + boundary, failed && invisible
                    && ReadDurableConversationAction(campaign, "da_batch_a") == null && ReadDurableConversationAction(campaign, "da_batch_b") == null, null);
            }
            var batch = PublishConversationActionBatch(campaign, batchPayload, acceptedResponse, batchRows(), generation);
            var duplicateBatch = PublishConversationActionBatch(campaign, batchPayload, acceptedResponse, batchRows(), generation);
            add("atomic_batch_commits_once_without_json_cache", ReadBool(batch, "atomic", false) && ReadInt(batch, "createdCount", 0) == 2
                && ReadInt(duplicateBatch, "createdCount", -1) == 0
                && ReadActionQueueUnlocked(campaign).Count(r => ReadString(r, "id", "").StartsWith("da_batch_", StringComparison.Ordinal)) == 2, batch);
            var changedBatch = new List<Dictionary<string, object>> { batchRow("da_batch_must_rollback"), batchRow("da_batch_b") };
            bool changedReplyRejected = false;
            try { PublishConversationActionBatch(campaign, batchPayload, TestDict("ok", true, "reply", "I refuse those terms."), changedBatch, generation); }
            catch (InvalidOperationException) { changedReplyRejected = true; }
            add("accepted_prose_binding_rejects_changed_reply_and_partial_batch", changedReplyRejected
                && ReadDurableConversationAction(campaign, "da_batch_must_rollback") == null
                && ReadString(ReadDurableConversationAction(campaign, "da_batch_b"), "acceptedResponseHash", "") == PromptHash(ReadString(acceptedResponse, "reply", "")), null);
            bool mixedBatchRejected = false;
            try { PublishConversationActionBatch(campaign, batchPayload, acceptedResponse,
                new List<Dictionary<string, object>> { batchRow("da_mixed_must_rollback"), batchRow("legacy_directive_fixture") }, generation); }
            catch (InvalidOperationException) { mixedBatchRejected = true; }
            add("mixed_legacy_and_durable_batch_rejected_before_write", mixedBatchRejected
                && ReadDurableConversationAction(campaign, "da_mixed_must_rollback") == null, null);
            var auditRows = new List<Dictionary<string, object>> { batchRow("da_audit_a"), batchRow("da_audit_b") };
            var auditFailure = PublishConversationActionBatch(campaign, batchPayload, acceptedResponse, auditRows, generation,
                auditWriter: _ => throw new System.IO.IOException("Injected postcommit audit projection failure."));
            var auditRetry = PublishConversationActionBatch(campaign, batchPayload, acceptedResponse, auditRows, generation);
            add("postcommit_audit_failure_keeps_truthful_committed_receipt", ReadBool(auditFailure, "committed", false)
                && ReadInt(auditFailure, "auditProjectionPending", 0) == 2 && ReadInt(auditRetry, "createdCount", -1) == 0
                && ReadDurableConversationAction(campaign, "da_audit_a") != null && ReadDurableConversationAction(campaign, "da_audit_b") != null, auditFailure);
            AdvanceMemoryRestoreGeneration(campaign);
            bool staleBatchRejected = false;
            try { PublishConversationActionBatch(campaign, batchPayload, acceptedResponse, new List<Dictionary<string, object>> { batchRow("da_stale_batch") }, generation); }
            catch (InvalidOperationException) { staleBatchRejected = true; }
            add("restored_campaign_rejects_pre_restore_action_publication", staleBatchRejected
                && ReadDurableConversationAction(campaign, "da_stale_batch") == null, null);
            var staleReplay = TerminalActionReportApi(protocolEnvelope(protocolActor, protocolJson));
            add("terminal_report_stale_generation_offers_no_effect", ReadString(staleReplay, "disposition", "") == "stale"
                && ReadDictionary(staleReplay, "nativeEffect") == null && !ReadBool(protocolAck(protocolGeneration), "ok", true), staleReplay);
            // Stage 3 recovery: the exact restored report is admitted only under a verified load
            // context bound inside the transaction; every refusal mutates nothing.
            {
                var P = Reign.Core.Contracts.Actions.TerminalActionReportProtocol.RecoverySchema;
                Func<string> liveGeneration = () => { using (var c = OpenCampaignConnection(campaign)) return ReadString(ReadMemoryPrecisionState(c), "restore_generation", ""); };
                Func<Dictionary<string, object>> restoreContext = () => { using (var c = OpenCampaignConnection(campaign)) return TerminalReportRestoreContext(c); };
                Action<string, string> setContext = (column, value) => { using (var c = OpenCampaignConnection(campaign))
                    ExecuteSql(c, "UPDATE memory_restored_source_context SET " + column + "=$v WHERE state_id='current';", TestDict("v", value)); };
                Func<string, Dictionary<string, object>> markerOf = id => ReadDictionary(ReadDurableConversationAction(campaign, id), "terminalReport");
                Func<Dictionary<string, object>, string, string, string, Dictionary<string, object>> recover = (envelope, live, session, point) =>
                {
                    var e = CloneDictionary(envelope); e["schema"] = P;
                    e["recovery"] = TestDict("liveGeneration", live, "savePointId", point, "loadSessionId", session);
                    e["loadProof"] = TestDict("loadSessionId", session, "savePointId", point, "restoreGeneration", live);
                    return e;
                };
                // A second nonterminal pa_ row from the original generation, never reported.
                var jobRow = ReadDictionaryList(job, "queuedDialogueActions").FirstOrDefault();
                string jobId = ReadString(jobRow, "id", "");
                var jobRecord = ReadDictionary(ReadDurableConversationAction(campaign, jobId), "record");
                string jobJson = Json.Serialize(TestDict("campaignId", campaign, "serverActionId", jobId, "actionId", jobId,
                    "timelineId", "player_actions_timeline", "playerHeroStringId", ReadString(jobRecord, "actorHeroStringId", ""),
                    "sharedActivityGeneration", protocolGeneration, "status", "completed", "type", "RegularTransferGold", "message", "Recovered handover.",
                    "actorHeroStringId", ReadString(jobRecord, "actorHeroStringId", ""), "targetHeroStringId", ReadString(jobRecord, "targetHeroStringId", ""),
                    "termsHash", ReadString(jobRecord, "termsHash", ""), "worldDay", 21d));
                Reign.Core.Contracts.Actions.TerminalActionReportProtocol.TryReportSha256(jobJson, out string jobSha, out _);
                var jobEnvelope = TestDict("schema", Reign.Core.Contracts.Actions.TerminalActionReportProtocol.RequestSchema, "campaignId", campaign,
                    "timelineId", "player_actions_timeline", "playerHeroStringId", ReadString(jobRecord, "actorHeroStringId", ""), "generation", protocolGeneration,
                    "actionId", jobId, "termsHash", ReadString(jobRecord, "termsHash", ""), "status", "completed", "reportJson", jobJson, "reportSha256", jobSha);
                // Fixture precondition: the second row is an eligible gold transfer, so later
                // dispositions are about recovery authority, not fixture shape.
                string jobIneligible = "missing";
                using (var c = OpenCampaignConnection(campaign))
                {
                    var storedJob = QuerySql(c, "SELECT timeline_id,payload_json FROM conversation_action_outbox WHERE action_id=$id;", TestDict("id", jobId)).FirstOrDefault();
                    var jobPayload = storedJob == null ? null : TryParseJsonObject(ReadString(storedJob, "payload_json", "{}"));
                    if (jobPayload != null) jobIneligible = TerminalReportIneligibility(c, jobPayload, campaign, jobId, ReadString(storedJob, "timeline_id", ""));
                }
                add("terminal_report_recovery_fixture_row_is_eligible", jobId.StartsWith("pa_", StringComparison.Ordinal) && jobIneligible.Length == 0
                    && TerminalReportExpectedType(jobRecord) == "RegularTransferGold", TestDict("id", jobId, "ineligible", jobIneligible));
                var protocolBase = protocolEnvelope(protocolActor, protocolJson);
                var stage3Point = TestDict("savePointId", "stage3_point", "timelineId", "player_actions_timeline");
                string beforeRollback = liveGeneration();
                // Snapshot taken before the rollback, so its own generation (beforeRollback) differs from the live one.
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "stage3_noop_snapshot");
                AdvanceMemoryRestoreGeneration(campaign, stage3Point, "", "stage3_l1");
                string g2 = liveGeneration();
                var context = restoreContext();
                add("terminal_report_rollback_context_binds_load_session", ReadString(context, "load_session_id", "") == "stage3_l1"
                    && ReadString(context, "source_generation", "") == beforeRollback && ReadString(context, "restore_generation", "") == g2
                    && ReadString(context, "save_point_id", "") == "stage3_point" && g2 != beforeRollback, context);
                // A verified no-op load always takes source_generation from the snapshot itself, so a
                // value left stale by an older server build cannot survive; other save points are untouched.
                try
                {
                    setContext("source_generation", "stale_downgrade_generation");
                    BindVerifiedNoOpLoadSession(campaign, "stage3_other_point", "stage3_noop_snapshot", "stage3_noop_other");
                    var otherPoint = restoreContext();
                    BindVerifiedNoOpLoadSession(campaign, "stage3_point", "stage3_noop_snapshot", "stage3_noop");
                    var noOpBound = restoreContext();
                    add("terminal_report_noop_bind_rewrites_stale_source_generation", ReadString(otherPoint, "source_generation", "") == "stale_downgrade_generation"
                        && ReadString(otherPoint, "load_session_id", "") == "stage3_l1"
                        && ReadString(noOpBound, "source_generation", "") == beforeRollback && ReadString(noOpBound, "load_session_id", "") == "stage3_noop"
                        && ReadString(noOpBound, "save_point_id", "") == "stage3_point" && ReadString(noOpBound, "restore_generation", "") == g2,
                        TestDict("otherPoint", otherPoint, "bound", noOpBound));
                }
                finally
                {
                    setContext("source_generation", beforeRollback);
                    setContext("load_session_id", "stage3_l1");
                    ReignPostgreSqlStorage.DropSnapshot(campaign, "stage3_noop_snapshot");
                }
                string markerBefore = Json.Serialize(markerOf(protocolId));
                var wrongSource = TerminalActionReportApi(recover(protocolBase, g2, "stage3_l1", "stage3_point"));
                var wrongSourceFirst = TerminalActionReportApi(recover(jobEnvelope, g2, "stage3_l1", "stage3_point"));
                var staleProof = TerminalActionReportApi(recover(protocolBase, g2, "stage3_other", "stage3_point"));
                add("terminal_report_recovery_refusals_mutate_nothing", ReadString(wrongSource, "reason", "") == "recovery_unproven:source_generation"
                    && ReadString(wrongSourceFirst, "reason", "") == "recovery_unproven:source_generation"
                    && ReadString(staleProof, "disposition", "") == "conflict" && ReadString(staleProof, "reason", "") == "load_proof_stale"
                    && Json.Serialize(markerOf(protocolId)) == markerBefore && markerOf(jobId) == null
                    && new[] { wrongSource, wrongSourceFirst, staleProof }.All(r => ReadString(r, "disposition", "") != "stale"),
                    TestDict("source", wrongSource, "first", wrongSourceFirst, "proof", staleProof));
                // The context now names a restore of the report's own generation.
                setContext("source_generation", protocolGeneration);
                var admitted = TerminalActionReportApi(recover(protocolBase, g2, "stage3_l1", "stage3_point"));
                var admission = ReadDictionary(markerOf(protocolId), "admission");
                var replayed = TerminalActionReportApi(recover(protocolBase, g2, "stage3_l1", "stage3_point"));
                add("terminal_report_recovery_adopts_marker_once_with_stable_receipt", ReadString(admitted, "reason", "") == "recovery_admitted"
                    && ReadString(replayed, "reason", "") == "recovery_replay" && ReadString(admitted, "generation", "") == protocolGeneration
                    && ReadString(admission, "generation", "") == g2 && ReadString(admission, "loadSessionId", "") == "stage3_l1"
                    && Json.Serialize(ReadDictionary(markerOf(protocolId), "admission")) == Json.Serialize(admission)
                    && ReadString(markerOf(protocolId), "generation", "") == protocolGeneration
                    && ReadString(markerOf(protocolId), "reportJson", "") == protocolJson
                    && ReadString(ReadDictionary(admitted, "nativeEffect"), "receiptId", "") == ReadString(protocolEffect, "receiptId", "")
                    && protocolCount(directionalSql) == 2 && protocolCount(actionReceiptSql) == 1, TestDict("admitted", admitted, "replayed", replayed));
                var firstRecovered = TerminalActionReportApi(recover(jobEnvelope, g2, "stage3_l1", "stage3_point"));
                var jobMarker = markerOf(jobId);
                string jobReceipt = ReadString(ReadDictionary(firstRecovered, "nativeEffect"), "receiptId", "");
                add("terminal_report_recovery_first_acceptance_keeps_report_generation", ReadString(firstRecovered, "reason", "") == "first_acceptance"
                    && ReadString(jobMarker, "generation", "") == protocolGeneration && ReadString(ReadDictionary(jobMarker, "admission"), "generation", "") == g2
                    && (jobReceipt.Length == 0 || jobReceipt == Reign.Core.Contracts.Actions.TerminalActionReportProtocol.ReceiptId(jobId, "player_actions_timeline", protocolGeneration, jobSha))
                    && ReadString(firstRecovered, "memoryProjectionStatus", "") == "completed" && jobMarker.ContainsKey("projectedShared"), firstRecovered);
                Func<Dictionary<string, object>, Dictionary<string, object>, Dictionary<string, object>> admittedAck = (echo, proof) =>
                {
                    var body = TestDict("campaignId", campaign, "receiptIds", new List<string> { ReadString(protocolEffect, "receiptId", "") }, "nativeReceipt", TestDict(
                        "schema", "reign-native-relationship-receipt-v2", "subjectId", ReadString(protocolEffect, "subjectId", ""), "targetId", ReadString(protocolEffect, "targetId", ""),
                        "priorNativeRelation", 0, "nativeRelation", ReadInt(protocolEffect, "delta", 0), "delta", ReadInt(protocolEffect, "delta", 0),
                        "timelineId", "player_actions_timeline", "reportGeneration", protocolGeneration, "reportSha256", ReadString(protocolFirst, "reportSha256", ""),
                        "admission", echo));
                    if (proof != null) body["loadProof"] = proof;
                    return ConversationRelationshipNativeReceiptApi(body);
                };
                var l1Echo = TestDict("generation", g2, "loadSessionId", "stage3_l1");
                var l1Proof = TestDict("loadSessionId", "stage3_l1", "savePointId", "stage3_point", "restoreGeneration", g2);
                add("terminal_report_admitted_ack_requires_admission_echo", ReadBool(admittedAck(l1Echo, l1Proof), "ok", false)
                    && !ReadBool(admittedAck(null, l1Proof), "ok", true) && !ReadBool(protocolAck(protocolGeneration), "ok", true), null);
                // A later verified restore re-adopts by the earlier admission; earlier proofs and echoes fail closed.
                AdvanceMemoryRestoreGeneration(campaign, stage3Point, "", "stage3_l2");
                string g3 = liveGeneration();
                var readopted = TerminalActionReportApi(recover(protocolBase, g3, "stage3_l2", "stage3_point"));
                var oldAck = admittedAck(l1Echo, l1Proof);
                add("terminal_report_multi_hop_readoption_rejects_older_load", ReadString(readopted, "reason", "") == "recovery_admitted"
                    && ReadString(ReadDictionary(markerOf(protocolId), "admission"), "generation", "") == g3
                    && !ReadBool(oldAck, "ok", true) && ReadString(oldAck, "reason", "") == "load_proof_stale"
                    && !ReadBool(admittedAck(l1Echo, null), "ok", true)
                    && ReadBool(admittedAck(TestDict("generation", g3, "loadSessionId", "stage3_l2"),
                        TestDict("loadSessionId", "stage3_l2", "savePointId", "stage3_point", "restoreGeneration", g3)), "ok", false), readopted);
                // A backup import stamps the context; no proof (including the stamp itself) matches it.
                setContext("load_session_id", Reign.Core.Contracts.Actions.TerminalActionReportProtocol.ImportedLoadSession);
                string markerBeforeImport = Json.Serialize(markerOf(protocolId));
                var afterImport = TerminalActionReportApi(recover(protocolBase, g3, "stage3_l2", "stage3_point"));
                var forgedStamp = TerminalActionReportApi(recover(protocolBase, g3,
                    Reign.Core.Contracts.Actions.TerminalActionReportProtocol.ImportedLoadSession, "stage3_point"));
                add("terminal_report_import_invalidates_load_proof", ReadString(afterImport, "reason", "") == "load_proof_stale"
                    && ReadString(forgedStamp, "reason", "") == "load_proof_stale" && Json.Serialize(markerOf(protocolId)) == markerBeforeImport,
                    TestDict("import", afterImport, "forged", forgedStamp));
                // Remaining held/rollback cases run on an unreported pa_ row from the original
                // generation. The context row is saved exactly and restored afterwards.
                string savedContext;
                using (var cx = OpenCampaignConnection(campaign))
                    savedContext = ReadString(QuerySql(cx, "SELECT to_jsonb(c)::text AS saved FROM memory_restored_source_context c WHERE c.state_id='current';").Single(), "saved", "");
                Action restoreSavedContext = () => { using (var cx = OpenCampaignConnection(campaign)) ExecuteSql(cx,
                    "DELETE FROM memory_restored_source_context; INSERT INTO memory_restored_source_context SELECT * FROM jsonb_populate_record(CAST(NULL AS memory_restored_source_context), CAST($saved AS jsonb));",
                    TestDict("saved", savedContext)); };
                const string reboundTimeline = "stage3_rebound_timeline";
                try
                {
                    string giftId = ReadString(ReadDictionaryList(mixedFailure, "queuedDialogueActions").FirstOrDefault(), "id", "");
                    var giftRecord = ReadDictionary(ReadDurableConversationAction(campaign, giftId), "record");
                    string giftActor = ReadString(giftRecord, "actorHeroStringId", "");
                    string giftJson = Json.Serialize(TestDict("campaignId", campaign, "serverActionId", giftId, "actionId", giftId,
                        "timelineId", "player_actions_timeline", "playerHeroStringId", giftActor,
                        "sharedActivityGeneration", protocolGeneration, "status", "completed", "type", "RegularTransferGold", "message", "A kind gift was recovered with thanks.",
                        "actorHeroStringId", giftActor, "targetHeroStringId", ReadString(giftRecord, "targetHeroStringId", ""),
                        "termsHash", ReadString(giftRecord, "termsHash", ""), "worldDay", 22d));
                    Reign.Core.Contracts.Actions.TerminalActionReportProtocol.TryReportSha256(giftJson, out string giftSha, out _);
                    var giftEnvelope = TestDict("schema", Reign.Core.Contracts.Actions.TerminalActionReportProtocol.RequestSchema, "campaignId", campaign,
                        "timelineId", "player_actions_timeline", "playerHeroStringId", giftActor, "generation", protocolGeneration,
                        "actionId", giftId, "termsHash", ReadString(giftRecord, "termsHash", ""), "status", "completed", "reportJson", giftJson, "reportSha256", giftSha);
                    Func<string, string, int> countFor = (sql, id) => { using (var cx = OpenCampaignConnection(campaign)) return QuerySql(cx, sql, TestDict("id", id)).Count; };
                    const string giftMemorySql = "SELECT owner_id,memory_id FROM memories WHERE event_id='action_' || $id ORDER BY owner_id,memory_id;";
                    const string sharedRowSql = "SELECT activity_id FROM shared_activity_events WHERE source_id LIKE 'action_receipt_%' AND (payload_json::jsonb->'facts'->>'actionId')=$id;";
                    Func<string> giftRow = () => Json.Serialize(ReadDurableConversationAction(campaign, giftId));
                    Func<bool> giftUntouched = () => markerOf(giftId) == null && countFor(directionalSql, giftId) == 0
                        && countFor(actionReceiptSql, giftId) == 0 && countFor(giftMemorySql, giftId) == 0 && countFor(sharedRowSql, giftId) == 0;
                    string giftIneligible = "missing";
                    using (var cx = OpenCampaignConnection(campaign))
                    {
                        var storedGift = QuerySql(cx, "SELECT timeline_id,payload_json FROM conversation_action_outbox WHERE action_id=$id;", TestDict("id", giftId)).FirstOrDefault();
                        var storedGiftPayload = storedGift == null ? null : TryParseJsonObject(ReadString(storedGift, "payload_json", "{}"));
                        if (storedGiftPayload != null) giftIneligible = TerminalReportIneligibility(cx, storedGiftPayload, campaign, giftId, ReadString(storedGift, "timeline_id", ""));
                    }
                    // A verified load bound to this generation whose snapshot came from the report's own generation.
                    setContext("load_session_id", "stage3_l2");
                    setContext("source_generation", protocolGeneration);
                    string giftRowBefore = giftRow(), protocolMarkerBefore = Json.Serialize(markerOf(protocolId)), protocolAdmissionBefore = Json.Serialize(ReadDictionary(markerOf(protocolId), "admission"));
                    add("terminal_report_recovery_second_fixture_row_is_eligible", giftId.StartsWith("pa_", StringComparison.Ordinal) && giftIneligible.Length == 0
                        && TerminalReportExpectedType(giftRecord) == "RegularTransferGold" && giftUntouched()
                        && giftActor == protocolActor && ReadString(giftRecord, "targetHeroStringId", "") == ReadString(protocolRecord, "targetHeroStringId", "")
                        && ReadString(restoreContext(), "timeline_id", "") == "player_actions_timeline", TestDict("id", giftId, "ineligible", giftIneligible));

                    // Empty context: with no restore context row no proof can be bound, so both a first
                    // acceptance and an adoption fail closed as load_proof_stale and nothing is written.
                    using (var cx = OpenCampaignConnection(campaign)) ExecuteSql(cx, "DELETE FROM memory_restored_source_context;");
                    var emptyFirst = TerminalActionReportApi(recover(giftEnvelope, g3, "stage3_l2", "stage3_point"));
                    var emptyAdopt = TerminalActionReportApi(recover(protocolBase, g3, "stage3_l2", "stage3_point"));
                    restoreSavedContext();
                    setContext("load_session_id", "stage3_l2");
                    setContext("source_generation", protocolGeneration);
                    add("terminal_report_recovery_empty_context_fails_closed", new[] { emptyFirst, emptyAdopt }.All(r =>
                            ReadString(r, "disposition", "") == "conflict" && ReadString(r, "reason", "") == "load_proof_stale" && ReadDictionary(r, "nativeEffect") == null)
                        && giftUntouched() && giftRow() == giftRowBefore && Json.Serialize(markerOf(protocolId)) == protocolMarkerBefore,
                        TestDict("first", emptyFirst, "adopt", emptyAdopt));

                    // Recovery needs both of its parts: the recovery schema without recovery or loadProof
                    // (what a v1-only request shape would send) is an invalid envelope. A plain v1
                    // envelope that carries the recovery parts stays on the v1 path, where an older
                    // generation is stale; it is never admitted.
                    Func<Action<Dictionary<string, object>>, Dictionary<string, object>> malformed = edit =>
                    {
                        var e = recover(giftEnvelope, g3, "stage3_l2", "stage3_point");
                        edit(e);
                        return TerminalActionReportApi(e);
                    };
                    var noParts = malformed(e => { e.Remove("recovery"); e.Remove("loadProof"); });
                    var noProof = malformed(e => e.Remove("loadProof"));
                    var noRecovery = malformed(e => e.Remove("recovery"));
                    var sameGeneration = TerminalActionReportApi(recover(giftEnvelope, protocolGeneration, "stage3_l2", "stage3_point"));
                    var v1First = recover(giftEnvelope, g3, "stage3_l2", "stage3_point");
                    v1First["schema"] = Reign.Core.Contracts.Actions.TerminalActionReportProtocol.RequestSchema;
                    var v1Adopt = recover(protocolBase, g3, "stage3_l2", "stage3_point");
                    v1Adopt["schema"] = Reign.Core.Contracts.Actions.TerminalActionReportProtocol.RequestSchema;
                    var v1FirstReply = TerminalActionReportApi(v1First);
                    var v1AdoptReply = TerminalActionReportApi(v1Adopt);
                    add("terminal_report_recovery_requires_recovery_schema_parts", new[] { noParts, noProof, noRecovery, sameGeneration }.All(r => !ReadBool(r, "ok", true))
                        && new[] { v1FirstReply, v1AdoptReply }.All(r => ReadString(r, "disposition", "") == "stale" && ReadString(r, "reason", "") == "generation_mismatch")
                        && giftUntouched() && giftRow() == giftRowBefore && Json.Serialize(markerOf(protocolId)) == protocolMarkerBefore,
                        TestDict("noParts", noParts, "noProof", noProof, "noRecovery", noRecovery, "sameGeneration", sameGeneration, "v1First", v1FirstReply, "v1Adopt", v1AdoptReply));

                    // Rebound timeline: once BindRecoveredMemorySources moves the verified context to another
                    // timeline, reports filed under the requested timeline are held (nothing mutated); binding
                    // the context to the report's timeline admits them again.
                    var headContext = restoreContext();
                    long headSequence = ReadLong(headContext, "head_sequence", 0);
                    string headEvent = ReadString(headContext, "head_event_id", "");
                    using (var cx = OpenCampaignConnection(campaign)) BindRecoveredMemorySources(cx, "player_actions_timeline", reboundTimeline, headSequence, headEvent);
                    string reboundTo = ReadString(restoreContext(), "timeline_id", "");
                    var reboundAdopt = TerminalActionReportApi(recover(protocolBase, g3, "stage3_l2", "stage3_point"));
                    var reboundFirst = TerminalActionReportApi(recover(giftEnvelope, g3, "stage3_l2", "stage3_point"));
                    using (var cx = OpenCampaignConnection(campaign)) BindRecoveredMemorySources(cx, reboundTimeline, "player_actions_timeline", headSequence, headEvent);
                    string reboundBack = ReadString(restoreContext(), "timeline_id", "");
                    var reboundAdmitted = TerminalActionReportApi(recover(protocolBase, g3, "stage3_l2", "stage3_point"));
                    add("terminal_report_recovery_rebound_timeline_held_then_admitted", reboundTo == reboundTimeline && reboundBack == "player_actions_timeline"
                        && new[] { reboundAdopt, reboundFirst }.All(r => ReadString(r, "disposition", "") == "unknown"
                            && ReadString(r, "reason", "") == Reign.Core.Contracts.Actions.TerminalActionReportProtocol.RecoveryUnprovenPrefix + "timeline")
                        && ReadString(reboundAdmitted, "disposition", "") == "accepted" && ReadString(reboundAdmitted, "reason", "") == "recovery_replay"
                        && giftUntouched() && giftRow() == giftRowBefore
                        && Json.Serialize(ReadDictionary(markerOf(protocolId), "admission")) == protocolAdmissionBefore,
                        TestDict("reboundTo", reboundTo, "adopt", reboundAdopt, "first", reboundFirst, "admitted", reboundAdmitted));

                    // projectedTs-only marker (written before projectedShared existed, its shared row
                    // missing): an adoption replay writes only the shared row and the projectedShared
                    // stamp; the existing action memory and projectedTs are never rewritten.
                    string memoryBeforeLegacy = memoryRows();
                    Func<object> projectedTsOf = () => { var m = markerOf(protocolId); return m != null && m.TryGetValue("projectedTs", out object v) ? v : null; };
                    object projectedTsBefore = projectedTsOf();
                    int sharedOriginal = countFor(sharedRowSql, protocolId);
                    using (var cx = OpenCampaignConnection(campaign))
                    {
                        ExecuteSql(cx, "UPDATE conversation_action_outbox SET payload_json=(payload_json::jsonb #- '{terminalReport,projectedShared}')::text WHERE action_id=$id;", TestDict("id", protocolId));
                        var sharedIds = QuerySql(cx, sharedRowSql, TestDict("id", protocolId)).Select(r => ReadString(r, "activity_id", "")).ToList();
                        foreach (string sharedId in sharedIds)
                            ExecuteSql(cx, "DELETE FROM shared_activity_observers WHERE activity_id=$a; DELETE FROM shared_activity_events WHERE activity_id=$a;", TestDict("a", sharedId));
                    }
                    bool legacyShape = markerOf(protocolId)?.ContainsKey("projectedTs") == true && markerOf(protocolId)?.ContainsKey("projectedShared") == false
                        && countFor(sharedRowSql, protocolId) == 0;
                    var legacyReplay = TerminalActionReportApi(recover(protocolBase, g3, "stage3_l2", "stage3_point"));
                    var legacyRepeat = TerminalActionReportApi(recover(protocolBase, g3, "stage3_l2", "stage3_point"));
                    add("terminal_report_projected_ts_only_marker_writes_only_shared_row", legacyShape && sharedOriginal == 1
                        && ReadString(legacyReplay, "reason", "") == "recovery_replay" && ReadString(legacyReplay, "memoryProjectionStatus", "") == "completed"
                        && ReadString(legacyRepeat, "memoryProjectionStatus", "") == "completed"
                        && markerOf(protocolId)?.ContainsKey("projectedShared") == true
                        && projectedTsBefore != null && Json.Serialize(projectedTsOf()) == Json.Serialize(projectedTsBefore)
                        && countFor(sharedRowSql, protocolId) == 1 && memoryRows() == memoryBeforeLegacy && onePerOwner(),
                        TestDict("sharedOriginal", sharedOriginal, "replay", legacyReplay, "memoryBefore", memoryBeforeLegacy, "memoryAfter", memoryRows()));

                    // Injected restore during a recovery first acceptance: a concurrent generation write
                    // inside the transaction plus a failure at each seam rolls back marker, admission,
                    // receipts, derived standing and the generation; a clean retry then accepts once.
                    string derivedBeforeRecovery = derivedCommitted();
                    var injectionFailures = new List<int>();
                    foreach (int seam in new[] { 1, 2 })
                    {
                        bool injected = false;
                        try
                        {
                            TerminalActionReportApi(recover(giftEnvelope, g3, "stage3_l2", "stage3_point"), (step, cx) =>
                            {
                                if (step != seam) return;
                                ExecuteSql(cx, "UPDATE memory_precision_state SET restore_generation='stage3_injected_restore' WHERE state_id='current';");
                                throw new InvalidOperationException("Injected restore during recovery acceptance.");
                            });
                        }
                        catch (InvalidOperationException) { injected = true; }
                        if (!injected || !giftUntouched() || giftRow() != giftRowBefore || liveGeneration() != g3 || derivedCommitted() != derivedBeforeRecovery)
                            injectionFailures.Add(seam);
                    }
                    var giftAccepted = TerminalActionReportApi(recover(giftEnvelope, g3, "stage3_l2", "stage3_point"));
                    var giftMarker = markerOf(giftId);
                    int giftDirectional = countFor(directionalSql, giftId), giftActionReceipts = countFor(actionReceiptSql, giftId);
                    string giftMemory; using (var cx = OpenCampaignConnection(campaign)) giftMemory = Json.Serialize(QuerySql(cx, giftMemorySql, TestDict("id", giftId)));
                    var giftReplay = TerminalActionReportApi(recover(giftEnvelope, g3, "stage3_l2", "stage3_point"));
                    string giftMemoryAfterReplay; using (var cx = OpenCampaignConnection(campaign)) giftMemoryAfterReplay = Json.Serialize(QuerySql(cx, giftMemorySql, TestDict("id", giftId)));
                    string giftReceipt = ReadString(ReadDictionary(giftAccepted, "nativeEffect"), "receiptId", "");
                    // The gift shares the protocol pair (fixture precondition), so derivedArgs reads its standing;
                    // the clean acceptance must change it, which makes the rollback comparison above meaningful.
                    string derivedAfterRecovery = derivedCommitted();
                    add("terminal_report_recovery_injected_restore_rolls_back_first_acceptance", injectionFailures.Count == 0
                        && ReadString(giftAccepted, "reason", "") == "first_acceptance" && ReadString(giftAccepted, "memoryProjectionStatus", "") == "completed"
                        && ReadString(giftMarker, "generation", "") == protocolGeneration && ReadString(giftMarker, "reportJson", "") == giftJson
                        && ReadString(ReadDictionary(giftMarker, "admission"), "generation", "") == g3
                        && ReadString(ReadDictionary(giftMarker, "admission"), "loadSessionId", "") == "stage3_l2"
                        && giftReceipt.Length > 0 && giftReceipt == Reign.Core.Contracts.Actions.TerminalActionReportProtocol.ReceiptId(giftId, "player_actions_timeline", protocolGeneration, giftSha)
                        && derivedAfterRecovery != derivedBeforeRecovery
                        && ReadString(giftReplay, "reason", "") == "recovery_replay"
                        && ReadString(ReadDictionary(giftReplay, "nativeEffect"), "receiptId", "") == giftReceipt
                        && Json.Serialize(markerOf(giftId)) == Json.Serialize(giftMarker)
                        && giftActionReceipts == 1 && giftDirectional == 2 && countFor(actionReceiptSql, giftId) == 1 && countFor(directionalSql, giftId) == 2
                        && giftMemory != "[]" && giftMemoryAfterReplay == giftMemory && countFor(sharedRowSql, giftId) == 1,
                        TestDict("failedSeams", injectionFailures, "accepted", giftAccepted, "replay", giftReplay, "memory", giftMemory,
                            "receipts", giftActionReceipts, "directional", giftDirectional, "derivedBefore", derivedBeforeRecovery, "derivedAfter", derivedAfterRecovery));
                }
                finally
                {
                    restoreSavedContext();
                    using (var cx = OpenCampaignConnection(campaign))
                        ExecuteSql(cx, "DELETE FROM memory_continuity_branches WHERE timeline_id=$t;", TestDict("t", reboundTimeline));
                }
            }
            var point = SaveSyncTestPointPayload(campaign, "player_action_snapshot", 20d, "Player action isolated snapshot");
            point["timelineId"] = "player_actions_timeline";
            var registered = SaveSyncRegisterApi(point);
            int before = ReadDurableConversationActions(campaign).Count;
            payload = NewContribution(payload, "future_gift");
            RunPlayerActionTurn(payload, () => TestDict("ok", true, "reply", "Thank you for the offer."));
            MarkSaveSyncActiveStateDirty(campaign);
            var restored = SaveSyncLoadApi(point, false);
            add("save_sync_rewinds_outbox_and_ignores_newer_json", ReadBool(registered, "ok", false) && ReadBool(restored, "ok", false)
                && ReadDurableConversationActions(campaign).Count == before
                && !ReadActionQueueUnlocked(campaign).Any(r => ReadString(ReadDictionary(r, "record"), "negotiationId", "") == "future_gift"), restored);
            return results;
        }
    }
}
