using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void RunWhoremongerSelfTests(ReignDbConnection connection, string campaignId,
            Action<string, bool, string, object> add)
        {
            add("whoremonger_probability_boundaries",
                Enumerable.Range(0, 4).All(n => WhoremongerRules.PlayerExposureChance(n) == 0d)
                && WhoremongerRules.PlayerExposureChance(4) == .1d
                && WhoremongerRules.PlayerExposureChance(5) == .2d
                && WhoremongerRules.PlayerExposureChance(13) == 1d
                && WhoremongerRules.PlayerExposureChance(int.MaxValue) == 1d
                && WhoremongerRules.NpcExposureChance(40, 40) == .01d
                && WhoremongerRules.NpcExposureChance(20, 20) == .09d
                && WhoremongerRules.NpcExposureChance(0, 0) == .25d
                && WhoremongerRules.NpcExposureChance(41, 0) == 0d
                && WhoremongerRules.NpcExposureChance(0, 41) == 0d
                && WhoremongerRules.NpcExposureChance(null, 0) == 0d
                && WhoremongerRules.NpcExposureChance(double.NaN, 0) == 0d
                && !WhoremongerRules.InactivityElapsed(2.5d, 32.499d)
                && WhoremongerRules.InactivityElapsed(2.5d, 32.5d),
                "Player ramp, NPC trait boundaries, missing traits, cap and exact inactivity boundary are provider-free.", null);
            var catalog = DefaultSocialCatalog();
            EnsureExpandedSocialCatalog(catalog);
            EnsureExpandedSocialCatalog(catalog);
            var archetype = ReadDictionaryList(catalog, "archetypes").Single(x => ReadString(x, "id", "") == "whoremonger");
            add("whoremonger_catalog_preserves_thirty_days",
                ReadDouble(archetype, "durationDays", 0) == 30d
                && ReadDouble(archetype, "promotionWindowDays", 0) == 30d,
                "Repeated built-in catalog migrations preserve the feature's explicit 30-day duration.", archetype);

            foreach (string id in new[] { "wm_player", "wm_refresh", "wm_late", "wm_spouse", "wm_npc", "wm_missing" })
                UpsertIdentityRosterHero(connection, new Dictionary<string, object>
                {
                    ["heroStringId"] = id, ["name"] = id, ["isAlive"] = true, ["isAdult"] = true,
                    ["isPlayer"] = id == "wm_player" || id == "wm_refresh" || id == "wm_late", ["clanTier"] = 1,
                    ["currentCharm"] = id == "wm_player" ? 100 : 0
                }, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            WriteJsonObject(CharacterFile(campaignId, "wm_player", "profile.json"),
                new Dictionary<string, object> { ["spouseId"] = "wm_spouse" });
            WriteJsonObject(CharacterFile(campaignId, "wm_spouse", "profile.json"),
                new Dictionary<string, object> { ["spouseId"] = "wm_player" });
            string timeline = "whoremonger_test";
            for (int n = 1; n <= 3; n++)
                RegisterWhoremongerVisit(connection, campaignId, timeline, "wm_player", "grace_" + n,
                    n % 2 == 0 ? "town_b" : "town_a", n - 1 + .5d);
            string fourthId = WhoremongerTestReceipt(campaignId, timeline, "wm_player", "visit:", .1d, true, null);
            var fourth = RegisterWhoremongerVisit(connection, campaignId, timeline, "wm_player", fourthId, "town_b", 3.5d);
            var replay = RegisterWhoremongerVisit(connection, campaignId, timeline, "wm_player", fourthId, "town_b", 3.5d);
            bool conflict = false;
            try { RegisterWhoremongerVisit(connection, campaignId, timeline, "wm_player", fourthId, "town_a", 3.5d); }
            catch (InvalidDataException) { conflict = true; }
            var standing = ReadPublicStandingRow(connection, campaignId, timeline, "wm_player");
            var spouse = ResolveObserverPublicStanding(connection, campaignId, timeline, "wm_spouse", "wm_player", standing);
            add("whoremonger_cross_town_receipt_and_spouse_rumor",
                ReadInt(fourth, "visitCount", 0) == 4 && ReadBool(fourth, "exposed", false)
                && ReadDouble(fourth, "chance", 0) == .1d && Json.Serialize(fourth) == Json.Serialize(replay)
                && conflict && ReadInt(standing, "standing_value", 0) == -4
                && ReadInt(spouse, "value", 0) == -16,
                "Cross-town visits share one streak; immutable receipt replay retains its roll; Charm 100 yields rumor -4 or spouse -16.", fourth);

            string fifthId = WhoremongerTestReceipt(campaignId, timeline, "wm_player", "visit:", .2d, true, true);
            var fifth = RegisterWhoremongerVisit(connection, campaignId, timeline, "wm_player", fifthId, "town_a", 4.5d);
            standing = ReadPublicStandingRow(connection, campaignId, timeline, "wm_player");
            spouse = ResolveObserverPublicStanding(connection, campaignId, timeline, "wm_spouse", "wm_player", standing);
            add("whoremonger_promotion_replaces_penalty",
                ReadInt(fifth, "streakCount", 0) == 2 && ReadBool(fifth, "promoted", false)
                && ReadDouble(fifth, "promotionChance", 0) == .3d
                && ReadInt(standing, "standing_value", 0) == -8
                && ReadInt(spouse, "value", 0) == -16
                && ParsePublicStandingSources(ReadString(standing, "sources_json", "[]"))
                    .Count(IsWhoremongerStandingSource) == 1,
                "Second successful exposure can promote at 30%; reputation replaces rumor and spouse base remains -20.", fifth);

            WriteJsonObject(CharacterFile(campaignId, "wm_player", "profile.json"), new Dictionary<string, object>());
            WriteJsonObject(CharacterFile(campaignId, "wm_spouse", "profile.json"), new Dictionary<string, object>());
            add("whoremonger_current_spouse_recalculation",
                ReadInt(ResolveObserverPublicStanding(connection, campaignId, timeline, "wm_spouse", "wm_player", standing), "value", 0) == -8,
                "Ending the marriage immediately removes the spouse substitution without mutating the personal relation.", null);

            Dictionary<string, object> latest = fifth;
            for (int n = 6; n <= 13; n++)
                latest = RegisterWhoremongerVisit(connection, campaignId, timeline, "wm_player", "continued_" + n,
                    "town_a", 4.5d + (n - 5) * 20d);
            double lastDay = 164.5d;
            add("whoremonger_inactivity_not_rolling_window",
                ReadInt(latest, "visitCount", 0) == 13 && ReadDouble(latest, "chance", 0) == 1d
                && !ReadBool(latest, "inactivityReset", true),
                "Visits less than 30 days apart retain all stacks beyond 30 total elapsed days and reach a 100% roll.", latest);
            ExpireWhoremongerActivity(connection, campaignId, timeline, lastDay + 29.999d);
            var before = QuerySql(connection, "SELECT visit_count FROM whoremonger_activity WHERE subject_id='wm_player' AND timeline_id='whoremonger_test';").Single();
            ExpireWhoremongerActivity(connection, campaignId, timeline, lastDay + 30d);
            var resetState = QuerySql(connection, "SELECT visit_count,last_activity_day FROM whoremonger_activity WHERE subject_id='wm_player' AND timeline_id='whoremonger_test';").Single();
            var restarted = RegisterWhoremongerVisit(connection, campaignId, timeline, "wm_player", "after_break", "town_b", lastDay + 30d);
            add("whoremonger_exact_reset_preserves_reputation",
                ReadInt(before, "visit_count", 0) == 13 && ReadInt(resetState, "visit_count", -1) == 0
                && ReadInt(restarted, "visitCount", 0) == 1 && ReadDouble(restarted, "chance", -1) == 0d
                && ReadInt(ReadPublicStandingRow(connection, campaignId, timeline, "wm_player"), "standing_value", 0) == -8,
                "At exactly 30 inactive days all visit and promotion stacks reset while established reputation remains.", restarted);

            for (int n = 1; n <= 3; n++)
                RegisterWhoremongerVisit(connection, campaignId, timeline, "wm_refresh", "refresh_grace_" + n, "town_a", n);
            string firstRefresh = WhoremongerTestReceipt(campaignId, timeline, "wm_refresh", "visit:", .1d, true, null);
            RegisterWhoremongerVisit(connection, campaignId, timeline, "wm_refresh", firstRefresh, "town_a", 4.25d);
            string failedRefresh = WhoremongerTestReceipt(campaignId, timeline, "wm_refresh", "visit:", .2d, false, null);
            var refresh = RegisterWhoremongerVisit(connection, campaignId, timeline, "wm_refresh", failedRefresh, "town_b", 33.25d);
            ExpireWhoremongerActivity(connection, campaignId, timeline, 34.25d);
            var activeRefresh = ReadPublicStandingRow(connection, campaignId, timeline, "wm_refresh");
            string nextRefresh = WhoremongerTestReceipt(campaignId, timeline, "wm_refresh", "visit:", .3d, true, false);
            var continuedPromotion = RegisterWhoremongerVisit(connection, campaignId, timeline, "wm_refresh", nextRefresh, "town_a", 55.25d);
            ExpireWhoremongerActivity(connection, campaignId, timeline, 85.25d);
            add("whoremonger_failed_player_exposure_refreshes_rumor",
                !ReadBool(refresh, "exposed", true) && ReadDouble(refresh, "inactivityDeadline", 0) == 63.25d
                && ReadInt(activeRefresh, "standing_value", 0) == -5
                && ReadInt(continuedPromotion, "streakCount", 0) == 2
                && !ReadBool(continuedPromotion, "promoted", true)
                && ReadInt(ReadPublicStandingRow(connection, campaignId, timeline, "wm_refresh"), "standing_value", -1) == 0,
                "A failed exposure refreshes the rumor and retains promotion progress even when successful exposures are more than 30 days apart.", refresh);

            WriteJsonObject(CharacterFile(campaignId, "wm_npc", "traits.json"), new Dictionary<string, object>
            { ["courtVirtues"] = new Dictionary<string, object> { ["honor"] = 0d, ["judgment"] = 0d } });
            var npcPayload = new Dictionary<string, object> { ["genuineTownEntry"] = true, ["isTown"] = true,
                ["isPlayer"] = false, ["isPrisoner"] = false, ["isAlive"] = true, ["isAdult"] = true,
                ["townId"] = "town_a", ["entryDay"] = 1.25d };
            string npcFirst = WhoremongerTestReceipt(campaignId, timeline, "wm_npc", "entry:", .25d, true, null);
            var npc = RegisterWhoremongerNpcEntry(connection, campaignId, timeline, npcFirst, "wm_npc", 1.25d, npcPayload, false);
            string npcFail = WhoremongerTestReceipt(campaignId, timeline, "wm_npc", "entry:", .25d, false, null);
            npcPayload["entryDay"] = 30.25d;
            var npcFailed = RegisterWhoremongerNpcEntry(connection, campaignId, timeline, npcFail, "wm_npc", 30.25d, npcPayload, false);
            ExpireWhoremongerActivity(connection, campaignId, timeline, 31.25d);
            add("whoremonger_npc_success_deadline",
                ReadBool(npc, "exposed", false) && ReadDouble(npc, "chance", 0) == .25d
                && !ReadBool(npcFailed, "exposed", true)
                && ReadDouble(npcFailed, "inactivityDeadline", 0) == 31.25d
                && ReadInt(ReadPublicStandingRow(connection, campaignId, timeline, "wm_npc"), "standing_value", -1) == 0,
                "NPCs roll immediately without visit ramp; failed town entries never refresh their successful-exposure deadline.", npcFailed);
            npcPayload["isTown"] = false;
            var nonTown = RegisterWhoremongerNpcEntry(connection, campaignId, timeline, "village_entry", "wm_npc", 40, npcPayload, false);
            npcPayload["isTown"] = true;
            npcPayload["isPrisoner"] = true;
            var captive = RegisterWhoremongerNpcEntry(connection, campaignId, timeline, "captive_entry", "wm_npc", 40, npcPayload, false);
            npcPayload["isPrisoner"] = false;
            npcPayload["entryDay"] = 40d;
            var missing = RegisterWhoremongerNpcEntry(connection, campaignId, timeline, "missing_entry", "wm_missing", 40, npcPayload, false);
            add("whoremonger_npc_exclusions_and_missing_traits",
                ReadBool(nonTown, "skipped", false) && ReadBool(captive, "skipped", false)
                && ReadDouble(missing, "chance", -1) == 0 && !ReadBool(missing, "exposed", true),
                "Non-towns and prisoners are excluded; missing court virtues fail closed without inventing low scores.", null);

            npcPayload["isTown"] = true;
            npcPayload["isPrisoner"] = false;
            npcPayload["entryDay"] = 41d;
            npcPayload["townId"] = "town_a";
            var unavailableNpc = RegisterWhoremongerNpcEntry(connection, campaignId, timeline,
                "unavailable_npc_entry", "wm_departed", 41d, npcPayload, false);
            var unavailableReceipt = QuerySql(connection, @"SELECT subject_id,world_day,result_json
FROM whoremonger_event_receipts WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_id='entry:unavailable_npc_entry';", new Dictionary<string, object>
            {
                ["campaign"] = campaignId, ["timeline"] = timeline
            }).Single();
            bool unavailablePlayerRejected = false;
            try
            {
                RegisterWhoremongerActivity(connection, campaignId, timeline, "wm_departed",
                    "visit:unavailable_player", "town_a", 41d, true, null, null, false);
            }
            catch (InvalidDataException error)
            {
                unavailablePlayerRejected = error.Message == WhoremongerTemporalIdentityError;
            }
            add("whoremonger_departed_npc_entry_is_obsolete",
                ReadBool(unavailableNpc, "skipped", false)
                && ReadString(unavailableNpc, "reason", "") == "npc_identity_no_longer_eligible"
                && ReadString(unavailableReceipt, "subject_id", "") == "wm_departed"
                && ReadDouble(unavailableReceipt, "world_day", -1d) == 41d
                && ReadString(TryParseJsonObject(ReadString(unavailableReceipt, "result_json", "{}")),
                    "reason", "") == "npc_identity_no_longer_eligible"
                && unavailablePlayerRejected,
                "An NPC entry emitted while eligible becomes an immutable obsolete receipt if the NPC is gone before processing; an unavailable player visit remains a strict error.",
                unavailableNpc);

            const string lateTimeline = "whoremonger_late_npc_entry";
            var latePayload = new Dictionary<string, object>(npcPayload)
            {
                ["isTown"] = true, ["isPrisoner"] = false,
                ["entryDay"] = 20d, ["townId"] = "town_b"
            };
            RegisterWhoremongerNpcEntry(connection, campaignId, lateTimeline,
                "late_npc_newer", "wm_npc", 20d, latePayload, false);
            latePayload["entryDay"] = 10d;
            latePayload["townId"] = "town_a";
            var lateNpc = RegisterWhoremongerNpcEntry(connection, campaignId,
                lateTimeline, "late_npc_older", "wm_npc", 10d,
                latePayload, false);
            var lateState = QuerySql(connection, @"SELECT last_event_day FROM whoremonger_activity
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id='wm_npc';",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = lateTimeline
                }).Single();
            ExecuteSql(connection, @"INSERT INTO social_outcome_receipts(
event_id,campaign_id,timeline_id,subject_id,archetype_id,counter_applied,
completed,result_json,attempt_count,last_error,updated_ts)
VALUES('late_npc_recovery',$campaign,$timeline,'','',0,-1,'{}',3,$error,1);",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = lateTimeline,
                    ["error"] = WhoremongerOutOfOrderError
                });
            ExecuteSql(connection, @"INSERT INTO social_outcome_receipts(
event_id,campaign_id,timeline_id,subject_id,archetype_id,counter_applied,
completed,result_json,attempt_count,last_error,updated_ts)
VALUES('departed_npc_recovery',$campaign,$timeline,'','',0,-1,'{}',3,$error,1);",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = lateTimeline,
                    ["error"] = WhoremongerTemporalIdentityError
                });
            ExecuteSql(connection, @"INSERT INTO social_outcome_receipts(
event_id,campaign_id,timeline_id,subject_id,archetype_id,counter_applied,
completed,result_json,attempt_count,last_error,updated_ts)
VALUES('stalled_npc_recovery',$campaign,$timeline,'','',0,0,'{}',0,'',1);",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = lateTimeline
                });
            EnsureWorldHistorySchema(connection);
            EnsureSocialSubjectWorkSchema(connection);
            ExecuteSql(connection, @"INSERT INTO world_history_events(
event_id,campaign_id,timeline_id,sequence,world_day,event_type,subject_id,created_utc)
VALUES('late_npc_recovery',$campaign,$timeline,900001,10,'social_outcome','wm_npc',$utc),
('departed_npc_recovery',$campaign,$timeline,900002,11,'social_outcome','wm_npc',$utc),
('stalled_npc_recovery',$campaign,$timeline,900003,12,'social_outcome','wm_npc',$utc);
UPDATE world_history_events SET correlation_id='whoremonger_town_entry|wm_npc|3|town_a'
WHERE event_id='stalled_npc_recovery';
INSERT INTO social_subject_work_queue(campaign_id,timeline_id,subject_id,status,
first_sequence,pending_count,claim_owner,claim_expires_ts,updated_ts,last_error)
VALUES($campaign,$timeline,'wm_npc','completed',900001,0,'',0,1,'')
ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
status='completed',first_sequence=900001,pending_count=0,claim_owner='',
claim_expires_ts=0,updated_ts=1,last_error='';", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = lateTimeline,
                    ["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                });
            EnsureWhoremongerSchema(connection);
            var recovered = QuerySql(connection, @"SELECT event_id,completed,attempt_count,last_error
FROM social_outcome_receipts WHERE event_id IN ('late_npc_recovery','departed_npc_recovery','stalled_npc_recovery')
ORDER BY event_id;");
            var recoveredQueue = QuerySql(connection, @"SELECT status,first_sequence,pending_count,last_error
FROM social_subject_work_queue WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id='wm_npc';", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = lateTimeline
                }).Single();
            add("whoremonger_late_npc_entries_are_obsolete",
                ReadBool(lateNpc, "skipped", false)
                && ReadString(lateNpc, "reason", "") == "late_npc_entry_obsolete"
                && ReadDouble(lateState, "last_event_day", -1d) == 20d
                && recovered.Count == 3
                && recovered.All(row => ReadInt(row, "completed", -1) == 0
                    && ReadInt(row, "attempt_count", -1) == 0
                    && string.IsNullOrWhiteSpace(ReadString(row, "last_error", "missing")))
                && ReadString(recoveredQueue, "status", "") == "pending"
                && ReadLong(recoveredQueue, "first_sequence", 0) == 900001
                && ReadInt(recoveredQueue, "pending_count", 0) == 3
                && string.IsNullOrWhiteSpace(ReadString(recoveredQueue, "last_error", "missing"))
                && HasPendingSocialWorldHistoryOutcomes(campaignId, lateTimeline),
                "Late or temporally ineligible NPC town-entry outcomes are retained as obsolete receipts without rolling back newer activity; exhausted and already-cleared stalled receipts make their exact subject queue claimable again.", recoveredQueue);

            using (var reloaded = OpenCampaignConnection(campaignId))
            {
                var persistedReplay = RegisterWhoremongerVisit(reloaded, campaignId, timeline, "wm_player", fourthId, "town_b", 3.5d);
                add("whoremonger_persisted_receipt_replay",
                    Json.Serialize(persistedReplay) == Json.Serialize(fourth),
                    "Reopening campaign storage replays the original historical receipt without a new visit, roll or penalty.", persistedReplay);
            }
            var independent = RegisterWhoremongerVisit(connection, campaignId, "whoremonger_other_timeline", "wm_player", fourthId, "town_b", 3.5d);
            add("whoremonger_timeline_isolation", ReadInt(independent, "visitCount", 0) == 1,
                "The same visit identity on an independent timeline cannot inherit another timeline's streak or roll.", independent);
            RunWhoremongerDelayedConfirmationSelfTests(connection, campaignId, add);
        }

        private static void RunWhoremongerDelayedConfirmationSelfTests(ReignDbConnection connection, string campaignId,
            Action<string, bool, string, object> add)
        {
            const string timeline = "whoremonger_delayed_confirmation", subject = "wm_late";
            // A's payment day is 10, but no visit is counted until native/server
            // confirmation succeeds. B confirms first on 12, then A on 13.
            var b = RegisterWhoremongerVisit(connection, campaignId, timeline, subject, "late_b", "town_b", 12d);
            var a = RegisterWhoremongerVisit(connection, campaignId, timeline, subject, "late_a_paid10", "town_a", 13d);
            Dictionary<string, object> aRetry;
            using (var reopened = OpenCampaignConnection(campaignId))
                aRetry = RegisterWhoremongerVisit(reopened, campaignId, timeline, subject, "late_a_paid10", "town_a", 14d);
            var bReplay = RegisterWhoremongerVisit(connection, campaignId, timeline, subject, "late_b", "town_b", 12d);
            var args = new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timeline, ["subject"] = subject };
            var activity = QuerySql(connection, @"SELECT visit_count,last_event_day,last_activity_day FROM whoremonger_activity
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", args).Single();
            var receipts = QuerySql(connection, @"SELECT event_id,world_day FROM whoremonger_event_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", args);
            bool oldEventRejected = false, changedTownRejected = false, changedSubjectRejected = false;
            try { RegisterWhoremongerVisit(connection, campaignId, timeline, subject, "new_backdated", "town_c", 11d); }
            catch (InvalidDataException) { oldEventRejected = true; }
            try { RegisterWhoremongerVisit(connection, campaignId, timeline, subject, "late_a_paid10", "town_c", 14d); }
            catch (InvalidDataException) { changedTownRejected = true; }
            try { RegisterWhoremongerVisit(connection, campaignId, timeline, "wm_refresh", "late_a_paid10", "town_a", 14d); }
            catch (InvalidDataException) { changedSubjectRejected = true; }
            add("whoremonger_delayed_paid_confirmation_replays_first_exposure",
                ReadInt(b, "visitCount", 0) == 1 && ReadInt(a, "visitCount", 0) == 2
                && ReadDouble(a, "exposureDay", -1d) == 13d && ReadDouble(a, "inactivityDeadline", -1d) == 43d
                && Json.Serialize(a) == Json.Serialize(aRetry) && Json.Serialize(b) == Json.Serialize(bReplay)
                && ReadInt(activity, "visit_count", 0) == 2 && ReadDouble(activity, "last_event_day", -1d) == 13d
                && ReadDouble(activity, "last_activity_day", -1d) == 13d && receipts.Count == 2
                && receipts.Count(x => ReadString(x, "event_id", "") == "visit:late_a_paid10" && ReadDouble(x, "world_day", -1d) == 13d) == 1
                && oldEventRejected && changedTownRejected && changedSubjectRejected,
                "B confirms on day 12; A paid on day 10 confirms on day 13 and retries on 14 after storage reopen. Exactly two visits remain; the original exposure day/deadline win. New backdated events and changed scopes still reject.", aRetry);

            const string exposedTimeline = "whoremonger_delayed_exposed";
            for (int i = 0; i < 3; i++)
                RegisterWhoremongerVisit(connection, campaignId, exposedTimeline, subject, "exposed_grace_" + i, "town_b", 10d + i);
            string exposedId = WhoremongerTestReceipt(campaignId, exposedTimeline, subject, "visit:", .1d, true, null);
            var exposed = RegisterWhoremongerVisit(connection, campaignId, exposedTimeline, subject, exposedId, "town_a", 13d);
            var exposedRetry = RegisterWhoremongerVisit(connection, campaignId, exposedTimeline, subject, exposedId, "town_a", 14d);
            args["timeline"] = exposedTimeline;
            var streak = QuerySql(connection, @"SELECT streak_count,last_exposure_day FROM rumor_exposure_streaks
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='whoremonger'
AND thread_key=$subject||'|whoremonger';", args).Single();
            activity = QuerySql(connection, @"SELECT visit_count,last_activity_day FROM whoremonger_activity
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", args).Single();
            add("whoremonger_later_day_retry_cannot_stack_or_refresh",
                ReadBool(exposed, "exposed", false) && Json.Serialize(exposed) == Json.Serialize(exposedRetry)
                && ReadInt(streak, "streak_count", 0) == 1 && ReadDouble(streak, "last_exposure_day", -1d) == 13d
                && ReadInt(activity, "visit_count", 0) == 4 && ReadDouble(activity, "last_activity_day", -1d) == 13d,
                "A successful exposure replays on a later day without another visit, promotion stack, roll or inactivity refresh.", exposedRetry);
        }

        private static string WhoremongerTestReceipt(string campaignId, string timelineId,
            string subjectId, string prefix, double chance, bool exposed, bool? promotes)
        {
            for (int index = 0; index < 100000; index++)
            {
                string id = "fixture_" + subjectId + "_" + (exposed ? "pass" : "fail") + "_"
                    + (promotes.HasValue ? promotes.Value.ToString() : "any") + "_" + index.ToString(CultureInfo.InvariantCulture);
                string seed = string.Join("|", campaignId, timelineId, "whoremonger",
                    subjectId + "|whoremonger", prefix + id, "exposure");
                if ((DeterministicSocialRoll(seed) < chance) != exposed) continue;
                if (promotes.HasValue && (DeterministicSocialRoll(seed + "|promotion") < .3d) != promotes.Value) continue;
                return id;
            }
            throw new InvalidOperationException("Unable to select a deterministic Whoremonger test receipt.");
        }
    }
}
