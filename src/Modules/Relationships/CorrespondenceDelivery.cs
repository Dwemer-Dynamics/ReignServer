using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void EnsureCorrespondenceDeliverySchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS correspondence_delivery_jobs (
letter_id TEXT PRIMARY KEY,player_id TEXT NOT NULL,timeline_id TEXT NOT NULL,restore_generation TEXT NOT NULL,
source_hash TEXT NOT NULL,status TEXT NOT NULL,attempt_count INTEGER NOT NULL DEFAULT 0,
lease_token TEXT NOT NULL DEFAULT '',lease_until BIGINT NOT NULL DEFAULT 0,next_attempt_ts BIGINT NOT NULL DEFAULT 0,
reply_json TEXT NOT NULL DEFAULT '',result_json TEXT NOT NULL DEFAULT '{}',acknowledged INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '',created_ts BIGINT NOT NULL,updated_ts BIGINT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_correspondence_delivery_work ON correspondence_delivery_jobs(status,next_attempt_ts,lease_until);");
        }

        private static Dictionary<string, object> EmptyCorrespondenceDeliveryResult() => TestDict(
            "ok", true, "deliveredLetters", new List<Dictionary<string, object>>(),
            "queuedReplies", new List<Dictionary<string, object>>(), "nativeRelationChanges", new List<Dictionary<string, object>>(),
            "queuedActions", new List<Dictionary<string, object>>());

        // This phase has no provider path. Commit every due arrival before any
        // optional reply generation, so one slow writer cannot delay other mail.
        private static void DeliverDueCorrespondence(string campaign, string player, double day, string timeline)
        {
            var arrived = new List<Dictionary<string, object>>();
            using (var connection = OpenCampaignConnection(campaign))
            using (var transaction = connection.BeginTransaction())
            {
                var due = QuerySql(connection, @"SELECT * FROM letters WHERE status='in_transit' AND delivery_day<=$day
ORDER BY delivery_day,letter_id LIMIT 100 FOR UPDATE SKIP LOCKED;", TestDict("day", day));
                string generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var letter in due)
                {
                    if (!CorrespondenceDispatchOnTimeline(connection, letter, timeline)) continue;
                    string id = ReadString(letter, "letter_id", "");
                    bool toPlayer = ReadString(letter, "recipient_id", "") == player;
                    string status = toPlayer ? "delivered" : "read";
                    ExecuteSql(connection, @"UPDATE letters SET status=$status,delivered_day=$day,read_day=$read,
delivery_timeline_id=$timeline,updated_ts=$ts WHERE letter_id=$id AND status='in_transit';",
                        TestDict("id", id, "status", status, "day", day, "read", toPlayer ? 0d : day, "timeline", timeline, "ts", now));
                    letter["status"] = status; letter["delivered_day"] = day;
                    letter["read_day"] = toPlayer ? 0d : day; letter["delivery_timeline_id"] = timeline;
                    arrived.Add(letter);
                    var result = EmptyCorrespondenceDeliveryResult();
                    if (toPlayer) result["deliveredLetters"] = new[] { letter };
                    ExecuteSql(connection, @"INSERT INTO correspondence_delivery_jobs
(letter_id,player_id,timeline_id,restore_generation,source_hash,status,result_json,created_ts,updated_ts)
VALUES($id,$player,$timeline,$generation,$hash,$status,$result,$now,$now) ON CONFLICT(letter_id) DO NOTHING;",
                        TestDict("id", id, "player", player, "timeline", timeline, "generation", generation,
                            "hash", LetterContentHash(letter), "status", toPlayer ? "completed" : "pending",
                            "result", Json.Serialize(result), "now", now));
                }
                transaction.Commit();
            }
            // History/favor work is local and follows the committed arrival.
            // No provider is invoked by this publication phase.
            foreach (var letter in arrived)
            {
                using (var connection = OpenCampaignConnection(campaign))
                    RecordCompletedFavorCorrespondence(connection, campaign, timeline, letter, day);
                AppendDeliveredLetterHistory(campaign, player, letter, day);
            }
        }

        private static bool CorrespondenceDispatchOnTimeline(ReignDbConnection connection, Dictionary<string, object> letter, string timeline)
        {
            if (LetterSourceTimeline(letter) == timeline) return true;
            var restored = RestoredLetterContext(connection, timeline);
            if (restored == null) return false;
            var member = QuerySql(connection, @"SELECT content_hash FROM memory_restored_letters
WHERE letter_id=$id AND restore_generation=$generation;", TestDict("id", ReadString(letter, "letter_id", ""),
                "generation", ReadString(restored, "restore_generation", ""))).SingleOrDefault();
            return member != null && ReadString(member, "content_hash", "") == LetterContentHash(letter);
        }

        private static bool CorrespondenceDeliveryFence(ReignDbConnection connection, Dictionary<string, object> job, string token)
        {
            var current = QuerySql(connection, "SELECT * FROM correspondence_delivery_jobs WHERE letter_id=$id;",
                TestDict("id", ReadString(job, "letter_id", ""))).SingleOrDefault();
            var source = QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;",
                TestDict("id", ReadString(job, "letter_id", ""))).SingleOrDefault();
            return current != null && ReadString(current, "status", "") == "running"
                && source != null && LetterContentHash(source) == ReadString(job, "source_hash", "")
                && ReadString(current, "lease_token", "") == token
                && ReadLong(current, "lease_until", 0) > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                && ReadString(current, "restore_generation", "") == ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "")
                && ReadString(current, "restore_generation", "") == ReadString(job, "restore_generation", "")
                && ReadString(current, "timeline_id", "") == ActiveWorldHistoryTimeline(connection);
        }

        private static void RenewCorrespondenceDeliveryLease(string campaign, string id, string token, string generation)
        {
            try
            {
                using (var connection = OpenCampaignConnection(campaign))
                    ExecuteSql(connection, @"UPDATE correspondence_delivery_jobs SET lease_until=$until
WHERE letter_id=$id AND status='running' AND lease_token=$token AND restore_generation=$generation;",
                        TestDict("id", id, "token", token, "generation", generation, "until", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120));
            }
            catch (Exception ex) { LogOperational("correspondence.lease_renew_failed", TestDict("letterId", id, "error", LimitText(ex.Message, 300))); }
        }

        private static void ProcessPendingCorrespondence(string campaign, string player, double day, string timeline,
            Func<Dictionary<string, object>, Dictionary<string, object>> composer = null,
            Func<Dictionary<string, object>, Dictionary<string, object>, Dictionary<string, object>> accept = null)
        {
            string token = Guid.NewGuid().ToString("N");
            Dictionary<string, object> job, letter;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var connection = OpenCampaignConnection(campaign))
            {
                string generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                ExecuteSql(connection, @"UPDATE correspondence_delivery_jobs SET status='incomplete',lease_token='',lease_until=0,
last_error='Reply attempt limit reached after interrupted work.',updated_ts=$now
WHERE player_id=$player AND timeline_id=$timeline AND restore_generation=$generation
AND attempt_count>=2 AND (status IN ('pending','failed') OR (status='running' AND lease_until<=$now));",
                    TestDict("player", player, "timeline", timeline, "generation", generation, "now", now));
                job = QuerySql(connection, @"UPDATE correspondence_delivery_jobs SET status='running',
attempt_count=attempt_count+1,lease_token=$token,lease_until=$until,updated_ts=$now
WHERE letter_id=(SELECT letter_id FROM correspondence_delivery_jobs WHERE player_id=$player AND timeline_id=$timeline
AND restore_generation=$generation AND attempt_count<2 AND next_attempt_ts<=$now
AND (status IN ('pending','failed') OR (status='running' AND lease_until<=$now))
ORDER BY created_ts,letter_id LIMIT 1 FOR UPDATE SKIP LOCKED) RETURNING *;",
                    TestDict("player", player, "timeline", timeline, "generation", generation, "now", now, "token", token, "until", now + 120)).SingleOrDefault();
                if (job == null) return;
                letter = QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", TestDict("id", ReadString(job, "letter_id", ""))).SingleOrDefault();
            }
            string id = ReadString(job, "letter_id", "");
            using (var heartbeat = new Timer(_ => RenewCorrespondenceDeliveryLease(campaign, id, token,
                ReadString(job, "restore_generation", "")), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)))
            {
                try
                {
                    if (letter == null || ReadString(job, "source_hash", "") != LetterContentHash(letter))
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
                        using (var connection = OpenCampaignConnection(campaign))
                        {
                            if (!CorrespondenceDeliveryFence(connection, job, token)) return;
                            ExecuteSql(connection, "UPDATE correspondence_delivery_jobs SET reply_json=$reply WHERE letter_id=$id AND lease_token=$token;",
                                TestDict("reply", Json.Serialize(reply), "id", id, "token", token));
                        }
                    }
                    using (var connection = OpenCampaignConnection(campaign))
                    {
                        if (!CorrespondenceDeliveryFence(connection, job, token)) return;
                        var payload = TestDict("campaignId", campaign, "playerId", player, "worldDay", day, "timelineId", timeline);
                        // Acceptance uses existing event/parent-letter idempotency.
                        // Cache the composed draft before effects so a retry never
                        // invents a different reply to an already accepted letter.
                        var result = accept != null ? accept(letter, reply) : AcceptDeliveredCorrespondence(campaign, payload, letter, reply);
                        if (!CorrespondenceDeliveryFence(connection, job, token)) return;
                        ExecuteSql(connection, @"UPDATE correspondence_delivery_jobs SET status='completed',result_json=$result,
last_error='',lease_token='',lease_until=0,updated_ts=$now WHERE letter_id=$id AND lease_token=$token;",
                            TestDict("id", id, "token", token, "result", Json.Serialize(result), "now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                    }
                }
                catch (Exception ex)
                {
                    using (var connection = OpenCampaignConnection(campaign))
                        ExecuteSql(connection, @"UPDATE correspondence_delivery_jobs SET status=$status,last_error=$error,
lease_token='',lease_until=0,next_attempt_ts=$next,updated_ts=$now WHERE letter_id=$id AND lease_token=$token;",
                            TestDict("id", id, "token", token, "status", ReadInt(job, "attempt_count", 0) >= 2 ? "incomplete" : "failed",
                                "error", LimitText(ex.Message, 500), "next", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30,
                                "now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                    LogOperational("correspondence.reply_incomplete", TestDict("campaignId", campaign, "letterId", id,
                        "attempt", ReadInt(job, "attempt_count", 0), "error", LimitText(ex.Message, 500)));
                }
            }
        }

        private static Dictionary<string, object> ReadCorrespondenceDeliveryResults(string campaign, string player, string timeline, List<string> acknowledgments)
        {
            var result = EmptyCorrespondenceDeliveryResult();
            using (var connection = OpenCampaignConnection(campaign))
            {
                string generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                var args = TestDict("player", player, "timeline", timeline, "generation", generation);
                foreach (string id in (acknowledgments ?? new List<string>()).Distinct().Take(100))
                {
                    args["id"] = id;
                    ExecuteSql(connection, @"UPDATE correspondence_delivery_jobs SET acknowledged=1 WHERE letter_id=$id
AND player_id=$player AND timeline_id=$timeline AND restore_generation=$generation AND status='completed';", args);
                }
                var rows = QuerySql(connection, @"SELECT * FROM correspondence_delivery_jobs WHERE player_id=$player AND timeline_id=$timeline
AND restore_generation=$generation AND status='completed' AND acknowledged=0 ORDER BY created_ts,letter_id LIMIT 100;", args);
                foreach (string field in new[] { "deliveredLetters", "queuedReplies", "nativeRelationChanges", "queuedActions", "structuredActionDeliveries" })
                    result[field] = rows.SelectMany(r => ReadDictionaryList(TryParseJsonObject(ReadString(r, "result_json", "{}")), field)).ToList();
                result["deliveryReceiptIds"] = rows.Select(r => ReadString(r, "letter_id", "")).ToList();
                result["timelineId"] = timeline;
                result["pendingReplyCount"] = ReadInt(QuerySql(connection, @"SELECT COUNT(*) count FROM correspondence_delivery_jobs
WHERE player_id=$player AND timeline_id=$timeline AND restore_generation=$generation AND status IN ('pending','running','failed');", args).Single(), "count", 0);
            }
            return result;
        }

        // Called only from the verified snapshot restore path. Ordinary source
        // reads and matching participant names cannot rebind an old branch.
        private static void RebindRestoredCorrespondenceJobs(ReignDbConnection connection, string timeline, string generation)
        {
            ExecuteSql(connection, @"UPDATE correspondence_delivery_jobs SET timeline_id=$timeline,restore_generation=$generation,
status=CASE WHEN status='running' AND attempt_count>=2 THEN 'incomplete' WHEN status='running' THEN 'pending' ELSE status END,lease_token='',lease_until=0;",
                TestDict("timeline", timeline, "generation", generation));
        }
    }
}
