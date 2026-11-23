using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Npgsql;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        internal static void EnsureCorrespondenceDeliverySchema(ReignDbConnection connection)
        {
            RelationshipsSchema.CreateCorrespondenceDeliveryJobs(connection);
        }

        private static bool IsDeterministicMarriageLetter(string source) => source == "reign_marriage_offer"
            || source == "reign_marriage_answer" || source == "reign_marriage_meeting";

        private static Dictionary<string, object> EmptyCorrespondenceDeliveryResult() => TestDict(
            "ok", true, "deliveredLetters", new List<Dictionary<string, object>>(),
            "queuedReplies", new List<Dictionary<string, object>>(), "nativeRelationChanges", new List<Dictionary<string, object>>(),
            "queuedActions", new List<Dictionary<string, object>>());

        private const long CorrespondenceArrivalRetrySeconds = 30;

        private sealed class CorrespondenceHistoryMismatchException : InvalidOperationException
        {
            internal CorrespondenceHistoryMismatchException(string reason) : base(reason) { }
        }

        // This phase only runs local SQL. A failed letter rolls back to its own
        // savepoint so later due mail can commit before reply generation.
        private static void DeliverDueCorrespondence(string campaign, string player, double day, string timeline,
            Action beforeCommit = null)
        {
            var failures = new List<Dictionary<string, object>>();
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                // Favor expiry can queue public-standing invalidations. Prepare
                // that independent component before holding letter row locks.
                EnsureWorldRelationshipSchema(connection);
                using var retries = new ReignDatabase.SqlStatementRetrySuppression(connection);
                using var transaction = connection.BeginTransaction();
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var due = RelationshipsStore.SelectLetters6(connection, TestDict("day", day, "now", now), transaction);
                string generation = ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "");
                const string savepoint = "courier_arrival";
                foreach (var letter in due)
                {
                    if (!CorrespondenceDispatchOnTimeline(connection, letter, timeline)) continue;
                    string id = ReadString(letter, "letter_id", "");
                    transaction.Save(savepoint);
                    try
                    {
                        RecordCorrespondenceArrival(connection, transaction, campaign, player, timeline, generation, letter, day, now);
                    }
                    catch (PersistenceService.CampaignRequestReplacedException) { throw; }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is JsonException || ex is PostgresException)
                    {
                        transaction.Rollback(savepoint);
                        RelationshipsStore.DeferCorrespondenceArrival(connection, TestDict("id", id, "retry",
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + CorrespondenceArrivalRetrySeconds), transaction);
                        failures.Add(TestDict("campaignId", campaign, "letterId", id, "error", ex.GetType().Name));
                    }
                    transaction.Release(savepoint);
                }
                beforeCommit?.Invoke();
                transaction.Commit();
            }
            foreach (var failure in failures)
                OperationsService.LogOperational("correspondence.arrival_deferred", failure);
        }

        private static void RecordCorrespondenceArrival(ReignDbConnection connection, ReignDbTransaction transaction,
            string campaign, string player, string timeline, string generation, Dictionary<string, object> letter, double day, long now)
        {
            string id = ReadString(letter, "letter_id", "");
            bool toPlayer = ReadString(letter, "recipient_id", "") == player;
            bool immediatelyComplete = toPlayer || IsDeterministicMarriageLetter(ReadString(letter, "source", ""));
            string status = toPlayer ? "delivered" : "read";
            RelationshipsStore.UpdateLetters(connection, TestDict("id", id, "status", status, "day", day, "read", toPlayer ? 0d : day, "timeline", timeline, "ts", now), transaction);
            letter["status"] = status; letter["delivered_day"] = day;
            letter["read_day"] = toPlayer ? 0d : day; letter["delivery_timeline_id"] = timeline;

            ReputationService.RecordCompletedFavorCorrespondence(connection, campaign, timeline, letter, day);
            string npcId = DeliveredLetterHistoryNpc(player, letter);
            string history = "";
            if (!string.IsNullOrWhiteSpace(npcId))
            {
                DialogueService.StoreCorrespondenceTurn(connection, campaign, npcId, player, letter, day, now);
                RecordDeliveredLetterDisclosures(connection, letter, day, schemaPrepared: true);
                history = Json.Serialize(DeliveredLetterHistoryLine(player, letter, day, now));
            }
            var result = EmptyCorrespondenceDeliveryResult();
            if (immediatelyComplete) result["deliveredLetters"] = new[] { letter };
            RelationshipsStore.InsertOrIgnoreCorrespondenceDeliveryJobs(connection, TestDict("id", id, "player", player, "timeline", timeline, "generation", generation,
                "hash", DialogueService.LetterContentHash(letter), "status", immediatelyComplete ? "completed" : "pending",
                "result", Json.Serialize(result), "now", now, "history", history), transaction);
        }

        private static void ReconcileCorrespondenceArrivalHistory(string campaign, string player, string timeline)
        {
            // History publication and acknowledgement are independent of inbox
            // acknowledgement and reply generation, including after a restore.
            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            string generation = ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "");
            var scope = TestDict("player", player, "timeline", timeline, "generation", generation,
                "now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            foreach (var receipt in RelationshipsStore.SelectPendingCorrespondenceArrivalHistory(connection, scope))
            {
                string letterId = ReadString(receipt, "letter_id", "");
                string historyJson = ReadString(receipt, "arrival_history_json", "");
                string sourceHash = ReadString(receipt, "source_hash", "");
                var expected = TestDict("id", letterId, "player", player, "timeline", timeline, "generation", generation,
                    "hash", sourceHash, "history", historyJson);
                try
                {
                    var history = TryParseJsonObject(historyJson);
                    var letter = RelationshipsStore.SelectLetters7(connection, TestDict("id", letterId)).SingleOrDefault();
                    string status = ReadString(letter, "status", "");
                    if (letter == null || (status != "delivered" && status != "read")
                        || DialogueService.LetterContentHash(letter) != sourceHash
                        || ReadString(history, "letterId", "") != letterId
                        || ReadString(history, "channel", "") != "correspondence"
                        || ReadString(history, "senderId", "") != ReadString(letter, "sender_id", "")
                        || ReadString(history, "recipientId", "") != ReadString(letter, "recipient_id", ""))
                        throw new CorrespondenceHistoryMismatchException("source_mismatch");
                    string npcId = DeliveredLetterHistoryNpc(player, letter);
                    if (string.IsNullOrWhiteSpace(npcId))
                        throw new CorrespondenceHistoryMismatchException("missing_recipient");
                    PublishCorrespondenceArrivalHistory(campaign, npcId, letterId, history);
                    RelationshipsStore.CompleteCorrespondenceArrivalHistory(connection, expected);
                }
                catch (PersistenceService.CampaignRequestReplacedException) { throw; }
                catch (CorrespondenceHistoryMismatchException ex)
                {
                    expected["terminal"] = ex.Message;
                    expected["retry"] = 0L;
                    RelationshipsStore.DeferCorrespondenceArrivalHistory(connection, expected);
                    OperationsService.LogOperational("correspondence.arrival_history_blocked", TestDict("campaignId", campaign,
                        "letterId", letterId, "reason", ex.Message));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    expected["terminal"] = "";
                    expected["retry"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + CorrespondenceArrivalRetrySeconds;
                    RelationshipsStore.DeferCorrespondenceArrivalHistory(connection, expected);
                    OperationsService.LogOperational("correspondence.arrival_history_deferred", TestDict("campaignId", campaign,
                        "letterId", letterId, "error", ex.GetType().Name));
                }
            }
        }

        private static void PublishCorrespondenceArrivalHistory(string campaign, string npcId, string letterId,
            Dictionary<string, object> line)
        {
            string path = CampaignFileService.CharacterFile(campaign, npcId, "history", "dialogue.jsonl");
            CampaignFileService.AppendDurableJsonLineIfAbsent(path, line, existing =>
            {
                if (ReadString(existing, "channel", "") != "correspondence" || ReadString(existing, "letterId", "") != letterId)
                    return false;
                if (CanonicalJson(existing) != CanonicalJson(line))
                    throw new CorrespondenceHistoryMismatchException("existing_history_conflict");
                return true;
            });
        }

        private static bool CorrespondenceDispatchOnTimeline(ReignDbConnection connection, Dictionary<string, object> letter, string timeline)
        {
            if (DialogueService.LetterSourceTimeline(letter) == timeline) return true;
            var restored = DialogueService.RestoredLetterContext(connection, timeline);
            if (restored == null) return false;
            var member = RelationshipsStore.SelectMemoryRestoredLetters(connection, TestDict("id", ReadString(letter, "letter_id", ""),
                "generation", ReadString(restored, "restore_generation", ""))).SingleOrDefault();
            return member != null && ReadString(member, "content_hash", "") == DialogueService.LetterContentHash(letter);
        }

        private static bool CorrespondenceDeliveryFence(ReignDbConnection connection, Dictionary<string, object> job, string token)
        {
            var current = RelationshipsStore.SelectCorrespondenceDeliveryJobs(connection, TestDict("id", ReadString(job, "letter_id", ""))).SingleOrDefault();
            var source = RelationshipsStore.SelectLetters7(connection, TestDict("id", ReadString(job, "letter_id", ""))).SingleOrDefault();
            return current != null && ReadString(current, "status", "") == "running"
                && source != null && DialogueService.LetterContentHash(source) == ReadString(job, "source_hash", "")
                && ReadString(current, "lease_token", "") == token
                && ReadLong(current, "lease_until", 0) > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                && ReadString(current, "restore_generation", "") == ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "")
                && ReadString(current, "restore_generation", "") == ReadString(job, "restore_generation", "")
                && ReadString(current, "timeline_id", "") == WorldSimulationService.ActiveWorldHistoryTimeline(connection);
        }

        private static void RenewCorrespondenceDeliveryLease(string campaign, string id, string token, string generation)
        {
            try
            {
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    RelationshipsStore.UpdateCorrespondenceDeliveryJobs(connection, TestDict("id", id, "token", token, "generation", generation, "until", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120));
            }
            catch (Exception ex) { OperationsService.LogOperational("correspondence.lease_renew_failed", TestDict("letterId", id, "error", DialogueService.LimitText(ex.Message, 300))); }
        }

        private static void ProcessPendingCorrespondence(string campaign, string player, double day, string timeline,
            Func<Dictionary<string, object>, Dictionary<string, object>> composer = null,
            Func<Dictionary<string, object>, Dictionary<string, object>, Dictionary<string, object>> accept = null)
        {
            string token = Guid.NewGuid().ToString("N");
            Dictionary<string, object> job, letter;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                string generation = ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "");
                RelationshipsStore.UpdateCorrespondenceDeliveryJobs2(connection, TestDict("player", player, "timeline", timeline, "generation", generation, "now", now));
                job = RelationshipsStore.UpdateCorrespondenceDeliveryJobs3(connection, TestDict("player", player, "timeline", timeline, "generation", generation, "now", now, "token", token, "until", now + 120)).SingleOrDefault();
                if (job == null) return;
                letter = RelationshipsStore.SelectLetters7(connection, TestDict("id", ReadString(job, "letter_id", ""))).SingleOrDefault();
            }
            string id = ReadString(job, "letter_id", "");
            using (var heartbeat = new Timer(_ => RenewCorrespondenceDeliveryLease(campaign, id, token,
                ReadString(job, "restore_generation", "")), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)))
            {
                try
                {
                    if (letter == null || ReadString(job, "source_hash", "") != DialogueService.LetterContentHash(letter))
                        throw new InvalidOperationException("Delivered letter source changed.");
                    var reply = TryParseJsonObject(ReadString(job, "reply_json", ""));
                    if (reply == null)
                    {
                        reply = ReadString(letter, "sender_id", "") == player
                            ? (composer != null ? composer(letter) : GenerateNpcLetter(campaign, ReadString(letter, "recipient_id", ""), player, letter, day))
                            : TestDict("shouldReply", false, "body", "");
                        if (reply == null || ReadString(reply, "error", "").Length > 0
                            || ReadBool(reply, "shouldReply", false) && string.IsNullOrWhiteSpace(ReadString(reply, "body", "")))
                            throw new InvalidOperationException("Letter composition failed; bounded retry pending.");
                        using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                        {
                            if (!CorrespondenceDeliveryFence(connection, job, token)) return;
                            RelationshipsStore.UpdateCorrespondenceDeliveryJobs4(connection, TestDict("reply", Json.Serialize(reply), "id", id, "token", token));
                        }
                    }
                    using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    {
                        if (!CorrespondenceDeliveryFence(connection, job, token)) return;
                        var payload = TestDict("campaignId", campaign, "playerId", player, "worldDay", day, "timelineId", timeline);
                        // Acceptance uses existing event/parent-letter idempotency.
                        // Cache the composed draft before effects so a retry never
                        // invents a different reply to an already accepted letter.
                        var result = accept != null ? accept(letter, reply) : AcceptDeliveredCorrespondence(campaign, payload, letter, reply);
                        if (!CorrespondenceDeliveryFence(connection, job, token)) return;
                        RelationshipsStore.UpdateCorrespondenceDeliveryJobs5(connection, TestDict("id", id, "token", token, "result", Json.Serialize(result), "now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                    }
                }
                catch (Exception ex)
                {
                    using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                        RelationshipsStore.UpdateCorrespondenceDeliveryJobs6(connection, TestDict("id", id, "token", token, "status", ReadInt(job, "attempt_count", 0) >= 2 ? "incomplete" : "failed",
                                "error", DialogueService.LimitText(ex.Message, 500), "next", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30,
                                "now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                    OperationsService.LogOperational("correspondence.reply_incomplete", TestDict("campaignId", campaign, "letterId", id,
                        "attempt", ReadInt(job, "attempt_count", 0), "error", DialogueService.LimitText(ex.Message, 500)));
                }
            }
        }

        private static Dictionary<string, object> ReadCorrespondenceDeliveryResults(string campaign, string player, string timeline, List<string> acknowledgments)
        {
            var result = EmptyCorrespondenceDeliveryResult();
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                string generation = ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "");
                var args = TestDict("player", player, "timeline", timeline, "generation", generation);
                foreach (string id in (acknowledgments ?? new List<string>()).Distinct().Take(100))
                {
                    args["id"] = id;
                    RelationshipsStore.UpdateCorrespondenceDeliveryJobs7(connection, args);
                }
                var rows = RelationshipsStore.SelectCorrespondenceDeliveryJobs2(connection, args);
                foreach (string field in new[] { "deliveredLetters", "queuedReplies", "nativeRelationChanges", "queuedActions", "structuredActionDeliveries" })
                    result[field] = rows.SelectMany(r => ReadDictionaryList(TryParseJsonObject(ReadString(r, "result_json", "{}")), field)).ToList();
                result["deliveryReceiptIds"] = rows.Select(r => ReadString(r, "letter_id", "")).ToList();
                result["timelineId"] = timeline;
                result["pendingReplyCount"] = ReadInt(RelationshipsStore.CountCorrespondenceDeliveryJobs(connection, args).Single(), "count", 0);
            }
            return result;
        }

        // Called only from the verified snapshot restore path. Ordinary source
        // reads and matching participant names cannot rebind an old branch.
        internal static void RebindRestoredCorrespondenceJobs(ReignDbConnection connection, string timeline, string generation)
        {
            RelationshipsStore.UpdateCorrespondenceDeliveryJobs8(connection, TestDict("timeline", timeline, "generation", generation));
        }
    }
}
