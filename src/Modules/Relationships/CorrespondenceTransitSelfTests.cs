using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Shared;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunCorrespondenceTransitAssertions()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => results.Add(TestDict("caseId", "courier_" + id,
                "passed", pass, "summary", id, "data", data));
            bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-8d;
            Check("quarter_observed_gavene_route", Near(CorrespondenceTransitPolicy.NewTransitDays(5.3893225703d), 1.347330642575d));
            Check("quarter_floor_and_default", Near(CorrespondenceTransitPolicy.NewTransitDays(0.25d), 0.0625d)
                && Near(CorrespondenceTransitPolicy.NewTransitDays(3d), 0.75d));
            Check("urgent_stays_immediate", CorrespondenceTransitPolicy.NewTransitDays(3d, true) == 0d);
            Check("travel_control_range_and_relative_speed", Near(CorrespondenceTransitPolicy.NewTransitDays(3d, false, .25d), .1875d)
                && Near(CorrespondenceTransitPolicy.NewTransitDays(3d, false, 4d), 3d)
                && CorrespondenceTransitPolicy.NewTransitDays(3d, true, 4d) == 0d);
            Check("version_one_parent_retains_unscaled_base", Near(CorrespondenceTransitPolicy.ReplyBaseDays(1, 3d, .75d), 3d));
            Check("invalid_estimate_has_finite_fallback", Near(CorrespondenceTransitPolicy.NewTransitDays(double.NaN), 0.75d)
                && Near(CorrespondenceTransitPolicy.NewTransitDays(double.PositiveInfinity), 0.75d));
            double hour = 91133d * 24d;
            Check("hourly_at_large_native_dates", Enumerable.Range(1, 24).All(i => CorrespondenceTransitPolicy.ShouldPollAtHour(hour + i, hour + i - 1)));
            Check("same_hour_dedup_and_rewind", !CorrespondenceTransitPolicy.ShouldPollAtHour(hour + 0.6d, hour + 0.1d)
                && CorrespondenceTransitPolicy.ShouldPollAtHour(hour - 1d, hour)
                && CorrespondenceTransitPolicy.ShouldPollAtHour(hour, double.NegativeInfinity));

            string campaign = "courier_test_" + Guid.NewGuid().ToString("N");
            try
            {
                string originalId, replyId;
                using (var connection = OpenCampaignConnection(campaign))
                {
                    EnsureRelationshipAndCorrespondenceSchema(connection);
                    var letter = InsertLetter(connection, "sender", "Sender", "player", "Player", "Come through the western gate.",
                        "npc_initiated", "fixture", "", "a", "b", 100d, 105.3893225703d,
                        TestDict("transitPolicyVersion", 99, "baseTransitDays", 0.01d));
                    originalId = ReadString(letter, "letterId", "");
                    var persisted = QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", TestDict("id", originalId)).Single();
                    Check("dispatch_persists_authoritative_unscaled_base", Near(ReadDouble(persisted, "base_transit_days", 0d), 5.3893225703d)
                        && ReadInt(persisted, "transit_policy_version", 0) == CorrespondenceTransitPolicy.Version
                        && Near(ReadDouble(persisted, "delivery_day", 0d), 101.347330642575d));
                    double replyBase = CorrespondenceTransitPolicy.ReplyBaseDays(ReadInt(persisted, "transit_policy_version", 0),
                        ReadDouble(persisted, "base_transit_days", 0d), ReadDouble(persisted, "delivery_day", 0d) - ReadDouble(persisted, "dispatch_day", 0d));
                    var reply = InsertLetter(connection, "player", "Player", "sender", "Sender", "I will return through that gate.",
                        "npc_reply", "fixture", originalId, "b", "a", 102d, 102d + replyBase, TestDict());
                    replyId = ReadString(reply, "letterId", "");
                    Check("reply_does_not_compound_quartering", Near(ReadDouble(reply, "deliveryDay", 0d) - 102d, 1.347330642575d));
                    var retry = InsertLetter(connection, "player", "Player", "sender", "Sender", "A retried draft.",
                        "npc_reply", "fixture", originalId, "b", "a", 103d, 110d, TestDict());
                    Check("retry_keeps_same_letter_and_eta", ReadBool(retry, "replayed", false) && ReadString(retry, "letterId", "") == replyId
                        && Near(ReadDouble(retry, "deliveryDay", 0d), ReadDouble(reply, "deliveryDay", 0d)));
                    // Fixture representing a pre-upgrade row: schema extension
                    // must not retime it, including after another connection.
                    ExecuteSql(connection, "UPDATE letters SET transit_policy_version=0,base_transit_days=0,delivery_day=105.3893225703 WHERE letter_id=$id;",
                        TestDict("id", originalId));
                    EnsureRelationshipAndCorrespondenceSchema(connection);
                    persisted = QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", TestDict("id", originalId)).Single();
                    Check("legacy_inflight_eta_unchanged", Near(ReadDouble(persisted, "delivery_day", 0d), 105.3893225703d));
                    double legacyReplyBase = CorrespondenceTransitPolicy.ReplyBaseDays(0, 0d, 5.3893225703d);
                    Check("reply_to_legacy_parent_uses_new_speed_once", Near(CorrespondenceTransitPolicy.NewTransitDays(legacyReplyBase), 1.347330642575d));
                    foreach (string source in new[] { "player", "npc_initiated", "npc_introduction", "relationship_development", "court_economic_transfer" })
                    {
                        var item = InsertLetter(connection, "writer_" + source, source, "player", "Player", "A complete accepted letter.", source, "route", "", "a", "b", 200d, 203d, TestDict());
                        Check("central_policy_" + source, Near(ReadDouble(item, "deliveryDay", 0d), 200.75d));
                    }
                    var urgent = InsertLetter(connection, "commander", "Commander", "player", "Player", "Urgent report.",
                        "campaign_command_urgent_report", "urgent", "", "a", "b", 200d, 203d, TestDict());
                    Check("central_policy_urgent", ReadDouble(urgent, "deliveryDay", -1d) == 200d);
                    var scaled = InsertLetter(connection, "scaled", "Scaled", "player", "Player", "Slower courier.",
                        "npc_initiated", "scaled", "", "a", "b", 300d, 303d, TestDict(), 2d);
                    var persistedScaled = QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;",
                        TestDict("id", ReadString(scaled, "letterId", ""))).Single();
                    double scaledBase = CorrespondenceTransitPolicy.ReplyBaseDays(ReadInt(persistedScaled, "transit_policy_version", 0),
                        ReadDouble(persistedScaled, "base_transit_days", 0), 1.5d);
                    var fasterReply = InsertLetter(connection, "player", "Player", "scaled", "Scaled", "Faster return.",
                        "npc_reply", "scaled", ReadString(scaled, "letterId", ""), "b", "a", 302d, 302d + scaledBase, TestDict(), .5d);
                    var scaledReplay = InsertLetter(connection, "player", "Player", "scaled", "Scaled", "Retry.",
                        "npc_reply", "scaled", ReadString(scaled, "letterId", ""), "b", "a", 303d, 306d, TestDict(), 4d);
                    Check("new_multiplier_persists_without_compounding_or_retiming", Near(ReadDouble(scaled, "deliveryDay", 0), 301.5d)
                        && Near(ReadDouble(fasterReply, "deliveryDay", 0), 302.375d)
                        && Near(ReadDouble(scaledReplay, "deliveryDay", 0), 302.375d)
                        && ReadBool(scaledReplay, "replayed", false));
                }
                var reportPayload = TestDict("campaignId", campaign, "senderId", "commander",
                    "senderName", "Lucon", "recipientId", "player", "recipientName", "Nal",
                    "source", "campaign_command_report", "reason", "scout_report_receipt",
                    "body", "I observed no hostile parties nearby. The order is complete.",
                    "orderId", "scout_order", "reportId", "scout_report_receipt",
                    "dispatchDay", 204d, "deliveryDay", 210d);
                var deliveredReport = CorrespondenceSendApi(reportPayload);
                var replayedReport = CorrespondenceSendApi(reportPayload);
                string deliveredId = ReadString(deliveredReport, "letterId", "");
                Check("completion_report_delivered_without_courier_delay", ReadBool(deliveredReport, "ok", false)
                    && ReadString(deliveredReport, "status", "") == "delivered"
                    && ReadDouble(deliveredReport, "deliveryDay", -1d) == 204d && deliveredId.Length > 0);
                Check("completion_report_retry_deduplicates_exact_identity", ReadBool(replayedReport, "deduplicated", false)
                    && ReadString(replayedReport, "letterId", "") == deliveredId
                    && ReadString(replayedReport, "status", "") == "delivered");
                var inbox = CorrespondenceThreadsApi(TestDict("campaignId", campaign, "playerId", "player",
                    "contactIds", new[] { "unrelated_contact" }));
                Check("completion_report_visible_outside_contact_filter", ReadDictionaryList(inbox, "letters")
                    .Any(x => ReadString(x, "letter_id", "") == deliveredId));
                using (var reports = OpenCampaignConnection(campaign))
                {
                    Check("completion_report_retry_preserves_one_body", QuerySql(reports,
                        "SELECT * FROM letters WHERE source='campaign_command_report' AND reason='scout_report_receipt';",
                        TestDict()).Count == 1);
                    ExecuteSql(reports, "UPDATE letters SET status='read' WHERE letter_id=$id;", TestDict("id", deliveredId));
                    var guidance = QueueImmediateCampaignOrderGuidanceAction(reports, campaign,
                        TestDict("campaignId", campaign), "player", "commander", "continue");
                    Check("completion_report_cannot_become_guidance_order", guidance.Count == 0);
                }
                var readReplay = CorrespondenceSendApi(reportPayload);
                Check("completion_report_lost_ack_after_read_still_deduplicates",
                    ReadBool(readReplay, "deduplicated", false) && ReadString(readReplay, "letterId", "") == deliveredId
                    && ReadString(readReplay, "status", "") == "read");
                using (var reopened = OpenCampaignConnection(campaign))
                {
                    var original = QuerySql(reopened, "SELECT * FROM letters WHERE letter_id=$id;", TestDict("id", originalId)).Single();
                    var reply = QuerySql(reopened, "SELECT * FROM letters WHERE letter_id=$id;", TestDict("id", replyId)).Single();
                    Check("reopened_ledger_preserves_both_policy_generations", ReadInt(original, "transit_policy_version", -1) == 0
                        && Near(ReadDouble(original, "delivery_day", 0d), 105.3893225703d)
                        && ReadInt(reply, "transit_policy_version", 0) == CorrespondenceTransitPolicy.Version
                        && Near(ReadDouble(reply, "delivery_day", 0d), 103.347330642575d));
                    ExecuteSql(reopened, @"INSERT INTO letters(letter_id,thread_id,sender_id,recipient_id,body,status,source,dispatch_day,created_ts,updated_ts)
SELECT 'bulk_'||i,'bulk_thread','player','bulk','Fixture read body','read','player',400+i,i,i FROM generate_series(1,1001) AS i;
INSERT INTO letters(letter_id,thread_id,sender_id,recipient_id,body,status,source,dispatch_day,created_ts,updated_ts) VALUES
('old_unread','old_thread','old_contact','player','Old unread body','delivered','npc_initiated',1,1,1),
('hidden_mail','hidden_thread','hidden_contact','player','Unseen body','in_transit','npc_introduction',5000,5000,5000),
('foreign_mail','foreign_thread','other_npc','other_player','Private body','delivered','npc_initiated',6000,6000,6000);");
                }
                var archive = CorrespondenceThreadsApi(TestDict("campaignId", campaign, "playerId", "player",
                    "contactIds", new[] { "bulk", "old_contact" }));
                var summaries = ReadDictionaryList(archive, "contactStates");
                Check("full_history_summary_survives_body_limit", ReadDictionaryList(archive, "letters").Count == 1000
                    && ReadDictionaryList(archive, "letters").All(row => ReadString(row, "sender_id", "") == "player")
                    && summaries.Any(row => ReadString(row, "contactId", "") == "old_contact" && ReadInt(row, "unreadCount", 0) == 1)
                    && !summaries.Any(row => ReadString(row, "contactId", "") == "hidden_contact")
                    && !summaries.Any(row => ReadString(row, "contactId", "") == "other_npc"));
                var oldThread = CorrespondenceThreadsApi(TestDict("campaignId", campaign, "playerId", "player",
                    "contactIds", new[] { "bulk", "old_contact" }, "correspondentId", "old_contact"));
                Check("selected_old_contact_loads_its_own_letters", ReadDictionaryList(oldThread, "letters").Count == 1
                    && ReadString(ReadDictionaryList(oldThread, "letters")[0], "letter_id", "") == "old_unread");
                using (var connection = OpenCampaignConnection(campaign))
                    ExecuteSql(connection, "UPDATE letters SET payload_json=$payload WHERE letter_id IN ('old_unread','bulk_1001');",
                        TestDict("payload", System.Text.Json.JsonSerializer.Serialize(TestDict("privatePrompt", new string('x', 1000000)))));
                var summariesOnly = CorrespondenceThreadsApi(TestDict("campaignId", campaign, "playerId", "player",
                    "contactIds", new[] { "bulk", "old_contact" }, "summaryOnly", true));
                Check("opening_mailbox_returns_summaries_without_bodies", ReadDictionaryList(summariesOnly, "letters").Count == 0
                    && ReadInt(summariesOnly, "unreadCount", 0) == 1
                    && ReadDictionaryList(summariesOnly, "contactStates").Count == summaries.Count
                    && System.Text.Json.JsonSerializer.Serialize(summariesOnly).Length < 10000);
                var slimThread = CorrespondenceThreadsApi(TestDict("campaignId", campaign, "playerId", "player",
                    "contactIds", new[] { "bulk", "old_contact" }, "correspondentId", "old_contact"));
                var slimLetter = ReadDictionaryList(slimThread, "letters").Single();
                Check("display_projection_excludes_large_private_payload", !slimLetter.ContainsKey("payload_json")
                    && ReadString(slimLetter, "body", "") == "Old unread body"
                    && slimLetter.ContainsKey("read_day") && slimLetter.ContainsKey("delivered_day")
                    && System.Text.Json.JsonSerializer.Serialize(slimThread).Length < 10000);
                var batchRead = CorrespondenceReadApi(TestDict("campaignId", campaign, "playerId", "player", "worldDay", 7000d,
                    "letterIds", new[] { "old_unread", "old_unread", "hidden_mail", "foreign_mail", "missing", "bulk_1001" }));
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var statuses = QuerySql(connection, "SELECT letter_id,status,read_day,payload_json FROM letters WHERE letter_id IN ('old_unread','hidden_mail','foreign_mail','bulk_1001');");
                    Check("batch_read_is_idempotent_and_recipient_scoped", ReadBool(batchRead, "ok", false)
                        && statuses.Any(row => ReadString(row, "letter_id", "") == "old_unread" && ReadString(row, "status", "") == "read" && ReadDouble(row, "read_day", 0) == 7000d)
                        && statuses.Any(row => ReadString(row, "letter_id", "") == "hidden_mail" && ReadString(row, "status", "") == "in_transit")
                        && statuses.Any(row => ReadString(row, "letter_id", "") == "foreign_mail" && ReadString(row, "status", "") == "delivered")
                        && statuses.Any(row => ReadString(row, "letter_id", "") == "old_unread" && ReadString(row, "payload_json", "").Length > 1000000));
                }
                CorrespondenceReadApi(TestDict("campaignId", campaign, "playerId", "player", "worldDay", 7001d, "letterIds", new[] { "old_unread" }));
                using (var connection = OpenCampaignConnection(campaign))
                    Check("batch_read_retry_preserves_original_read_day", ReadDouble(QuerySql(connection,
                        "SELECT read_day FROM letters WHERE letter_id='old_unread';").Single(), "read_day", 0) == 7000d);
            }
            catch (Exception ex) { Check("suite_exception", false, LimitText(ex.ToString(), 2000)); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
