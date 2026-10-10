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
                }
            }
            catch (Exception ex) { Check("suite_exception", false, LimitText(ex.ToString(), 2000)); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
