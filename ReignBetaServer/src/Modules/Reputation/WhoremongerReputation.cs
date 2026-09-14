using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string WhoremongerOutOfOrderError =
            "Whoremonger events must be processed in campaign order within their timeline.";
        private const string WhoremongerTemporalIdentityError =
            "The Whoremonger subject must be a synchronized living adult of the expected player/NPC role.";

        private static void EnsureWhoremongerSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS whoremonger_activity (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,
is_player INTEGER NOT NULL,visit_count INTEGER NOT NULL DEFAULT 0,
last_activity_day REAL NOT NULL DEFAULT -1,last_event_day REAL NOT NULL DEFAULT -1,
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS whoremonger_event_receipts (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,event_id TEXT NOT NULL,
subject_id TEXT NOT NULL,town_id TEXT NOT NULL,world_day REAL NOT NULL,
input_json TEXT NOT NULL,result_json TEXT NOT NULL,created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,event_id));");
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_whoremonger_inactivity
ON whoremonger_activity(campaign_id,timeline_id,last_activity_day);");
            if (TableExists(connection, "social_outcome_receipts"))
            {
                List<Dictionary<string, object>> recoveryScopes =
                    TableExists(connection, "world_history_events")
                    && TableExists(connection, "social_subject_work_queue")
                        ? QuerySql(connection, @"SELECT DISTINCT r.campaign_id,r.timeline_id
FROM social_outcome_receipts r
JOIN world_history_events e ON e.event_id=r.event_id
WHERE ((r.completed=-1 AND r.last_error IN ($orderingError,$identityError))
 OR (r.completed=0 AND r.attempt_count=0 AND r.last_error=''
  AND r.subject_id='' AND r.archetype_id='' AND r.result_json='{}'
  AND e.correlation_id LIKE 'whoremonger_town_entry|%'))
AND e.subject_id<>'';", new Dictionary<string, object>
                        {
                            ["orderingError"] = WhoremongerOutOfOrderError,
                            ["identityError"] = WhoremongerTemporalIdentityError
                        })
                        : new List<Dictionary<string, object>>();
                ExecuteSql(connection, @"UPDATE social_outcome_receipts SET
completed=0,attempt_count=0,last_error='',result_json='{}',updated_ts=$ts
WHERE completed=-1 AND last_error IN ($orderingError,$identityError);", new Dictionary<string, object>
                {
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["orderingError"] = WhoremongerOutOfOrderError,
                    ["identityError"] = WhoremongerTemporalIdentityError
                });
                foreach (Dictionary<string, object> scope in recoveryScopes)
                {
                    var queueArgs = new Dictionary<string, object>
                    {
                        ["campaign"] = ReadString(scope, "campaign_id", ""),
                        ["timeline"] = ReadString(scope, "timeline_id", ""),
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    ExecuteSql(connection, @"INSERT INTO social_subject_work_queue(
campaign_id,timeline_id,subject_id,status,first_sequence,pending_count,
claim_owner,claim_expires_ts,updated_ts,last_error)
SELECT $campaign,$timeline,e.subject_id,'pending',MIN(e.sequence),COUNT(*),'',0,$ts,''
FROM world_history_events e
WHERE e.campaign_id=$campaign AND e.timeline_id=$timeline
AND e.event_type='social_outcome' AND e.subject_id<>''
AND NOT EXISTS (SELECT 1 FROM social_outcome_receipts r
 WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3))
GROUP BY e.subject_id
ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
first_sequence=excluded.first_sequence,pending_count=excluded.pending_count,
status=CASE WHEN social_subject_work_queue.status='processing'
 AND social_subject_work_queue.claim_expires_ts>$ts THEN 'processing'
 ELSE 'pending' END,claim_owner=CASE WHEN social_subject_work_queue.status='processing'
 AND social_subject_work_queue.claim_expires_ts>$ts THEN social_subject_work_queue.claim_owner
 ELSE '' END,claim_expires_ts=CASE WHEN social_subject_work_queue.status='processing'
 AND social_subject_work_queue.claim_expires_ts>$ts THEN social_subject_work_queue.claim_expires_ts
 ELSE 0 END,updated_ts=$ts,last_error='';", queueArgs);
                    SchedulePendingSocialWorldHistoryOutcomes(
                        ReadString(scope, "campaign_id", ""),
                        ReadString(scope, "timeline_id", ""));
                }
            }
        }

        // Called inside the authoritative visit-confirmation transaction. The caller must
        // first validate the payment receipt and initialize the social schema.
        private static Dictionary<string, object> RegisterWhoremongerVisit(
            ReignDbConnection connection, string campaignId, string timelineId,
            string subjectId, string visitReceiptId, string townId, double worldDay,
            bool useExistingTransaction = false)
        {
            return RegisterWhoremongerActivity(connection, campaignId, timelineId, subjectId,
                "visit:" + visitReceiptId, townId, worldDay, true, null, null,
                useExistingTransaction);
        }

        private static Dictionary<string, object> RegisterWhoremongerNpcEntry(
            ReignDbConnection connection, string campaignId, string timelineId,
            string eventId, string subjectId, double worldDay,
            Dictionary<string, object> payload, bool useExistingTransaction,
            Dictionary<string, object> socialCatalog = null)
        {
            if (!ReadBool(payload, "genuineTownEntry", false)
                || !ReadBool(payload, "isTown", false)
                || ReadBool(payload, "isPlayer", true)
                || ReadBool(payload, "isPrisoner", true)
                || !ReadBool(payload, "isAlive", false)
                || !ReadBool(payload, "isAdult", false))
                return new Dictionary<string, object> { ["ok"] = true, ["skipped"] = true,
                    ["reason"] = "ineligible_town_entry" };

            // Traits are read from the authoritative character document, never inferred
            // from native trait levels or treated as low when the document is missing.
            Dictionary<string, object> virtues = ReadDictionary(
                ReadJsonObject(CharacterFile(campaignId, subjectId, "traits.json")), "courtVirtues")
                ?? new Dictionary<string, object>();
            double? honor = virtues.ContainsKey("honor") ? ReadDouble(virtues, "honor", double.NaN) : (double?)null;
            double? judgment = virtues.ContainsKey("judgment") ? ReadDouble(virtues, "judgment", double.NaN) : (double?)null;
            return RegisterWhoremongerActivity(connection, campaignId, timelineId, subjectId,
                "entry:" + FirstNonEmpty(ReadString(payload, "sourceEventId", ""), eventId),
                ReadString(payload, "townId", ""), ReadDouble(payload, "entryDay", worldDay),
                false, honor, judgment, useExistingTransaction, socialCatalog);
        }

        private static Dictionary<string, object> RegisterWhoremongerActivity(
            ReignDbConnection connection, string campaignId, string timelineId,
            string subjectId, string eventId, string townId, double worldDay,
            bool isPlayer, double? honor, double? judgment, bool useExistingTransaction,
            Dictionary<string, object> socialCatalog = null)
        {
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(timelineId)
                || string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(townId)
                || string.IsNullOrWhiteSpace(eventId) || eventId.EndsWith(":", StringComparison.Ordinal)
                || double.IsNaN(worldDay) || double.IsInfinity(worldDay) || worldDay < 0d)
                throw new InvalidDataException("A Whoremonger event requires a campaign, timeline, subject, town, immutable receipt and finite campaign day.");
            EnsureWhoremongerSchema(connection);
            var args = new Dictionary<string, object> { ["campaign"] = campaignId,
                ["timeline"] = timelineId, ["subject"] = subjectId, ["event"] = eventId,
                ["town"] = townId, ["day"] = worldDay,
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            // Trait scores are intentionally not part of the receipt identity. A replay
            // after personality changes must retain its original probability and roll.
            string inputJson = Json.Serialize(new Dictionary<string, object>
            { ["subjectId"] = subjectId, ["townId"] = townId, ["worldDay"] = worldDay, ["isPlayer"] = isPlayer });
            if (!useExistingTransaction) ExecuteSql(connection, "BEGIN IMMEDIATE;");
            try
            {
                // A row update is also the PostgreSQL serialization point for two
                // concurrent confirmations concerning the same subject.
                args["player"] = isPlayer ? 1 : 0;
                ExecuteSql(connection, @"INSERT INTO whoremonger_activity(
campaign_id,timeline_id,subject_id,is_player,updated_ts)
VALUES($campaign,$timeline,$subject,$player,$ts) ON CONFLICT(campaign_id,timeline_id,subject_id) DO NOTHING;
UPDATE whoremonger_activity SET updated_ts=updated_ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", args);
                var prior = QuerySql(connection, @"SELECT input_json,result_json FROM whoremonger_event_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND event_id=$event LIMIT 1;", args).FirstOrDefault();
                if (prior != null)
                {
                    string originalInput = ReadString(prior, "input_json", "");
                    var original = TryParseJsonObject(originalInput);
                    // A paid visit may be confirmed again after the venue checkpoint
                    // failed. Its native current day advances; the first accepted
                    // exposure day, roll, counters and deadline remain authoritative.
                    // NPC entry timestamps and all actual scope fields stay immutable.
                    bool laterPlayerConfirmation = isPlayer && ReadBool(original, "isPlayer", false)
                        && ReadString(original, "subjectId", "") == subjectId
                        && ReadString(original, "townId", "") == townId
                        && worldDay >= ReadDouble(original, "worldDay", double.PositiveInfinity);
                    if (!string.Equals(originalInput, inputJson, StringComparison.Ordinal) && !laterPlayerConfirmation)
                        throw new InvalidDataException("The Whoremonger receipt was reused with different visit or entry details.");
                    var replay = TryParseJsonObject(ReadString(prior, "result_json", "{}"));
                    if (!useExistingTransaction) ExecuteSql(connection, "COMMIT;");
                    return replay;
                }
                var roster = QuerySql(connection, "SELECT is_player,is_alive,is_adult FROM identity_roster WHERE hero_id=$subject LIMIT 1;", args).FirstOrDefault();
                bool roleMismatch = roster != null
                    && (ReadInt(roster, "is_player", 0) != 0) != isPlayer;
                bool noLongerEligible = roster == null || ReadInt(roster, "is_alive", 0) == 0
                    || ReadInt(roster, "is_adult", 0) == 0;
                if (roleMismatch || (isPlayer && noLongerEligible))
                    throw new InvalidDataException(WhoremongerTemporalIdentityError);
                if (noLongerEligible)
                {
                    var obsolete = new Dictionary<string, object>
                    {
                        ["ok"] = true, ["skipped"] = true,
                        ["reason"] = "npc_identity_no_longer_eligible",
                        ["eventId"] = eventId, ["subjectId"] = subjectId,
                        ["exposureDay"] = worldDay
                    };
                    args["input"] = inputJson;
                    args["result"] = Json.Serialize(obsolete);
                    ExecuteSql(connection, @"INSERT INTO whoremonger_event_receipts(campaign_id,timeline_id,event_id,
subject_id,town_id,world_day,input_json,result_json,created_ts)
VALUES($campaign,$timeline,$event,$subject,$town,$day,$input,$result,$ts);", args);
                    if (!useExistingTransaction) ExecuteSql(connection, "COMMIT;");
                    return obsolete;
                }

                var state = QuerySql(connection, @"SELECT * FROM whoremonger_activity
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject LIMIT 1;", args).FirstOrDefault();
                if (worldDay < ReadDouble(state, "last_event_day", -1d))
                {
                    if (isPlayer) throw new InvalidDataException(WhoremongerOutOfOrderError);
                    var obsolete = new Dictionary<string, object>
                    {
                        ["ok"] = true, ["skipped"] = true,
                        ["reason"] = "late_npc_entry_obsolete",
                        ["eventId"] = eventId, ["subjectId"] = subjectId,
                        ["exposureDay"] = worldDay,
                        ["lastEventDay"] = ReadDouble(state, "last_event_day", -1d)
                    };
                    args["input"] = inputJson;
                    args["result"] = Json.Serialize(obsolete);
                    ExecuteSql(connection, @"INSERT INTO whoremonger_event_receipts(campaign_id,timeline_id,event_id,
subject_id,town_id,world_day,input_json,result_json,created_ts)
VALUES($campaign,$timeline,$event,$subject,$town,$day,$input,$result,$ts);", args);
                    if (!useExistingTransaction) ExecuteSql(connection, "COMMIT;");
                    return obsolete;
                }
                bool reset = WhoremongerRules.InactivityElapsed(ReadDouble(state, "last_activity_day", -1d), worldDay);
                if (reset) ClearWhoremongerStreak(connection, campaignId, timelineId, subjectId, worldDay);
                int count = isPlayer ? Math.Min(int.MaxValue - 1, reset ? 0 : ReadInt(state, "visit_count", 0)) + 1 : 0;
                double chance = isPlayer ? WhoremongerRules.PlayerExposureChance(count)
                    : WhoremongerRules.NpcExposureChance(honor, judgment);
                var occurrence = new Dictionary<string, object>
                {
                    ["timelineId"] = timelineId, ["archetypeId"] = WhoremongerRules.TagId,
                    ["threadKey"] = subjectId + "|" + WhoremongerRules.TagId,
                    ["sourceEventId"] = eventId, ["worldDay"] = worldDay,
                    ["exposureChanceOverride"] = chance,
                    ["provenanceSummary"] = isPlayer ? "Repeated visits to the madam's house became known."
                        : "A visit to the madam's house during a town stay became known.",
                    ["participants"] = new List<object> { new Dictionary<string, object>
                    { ["subjectId"] = subjectId, ["role"] = "visitor", ["isPlayer"] = isPlayer,
                        ["isAlive"] = true, ["isAdult"] = true } }
                };
                var result = RegisterSocialOccurrence(connection, campaignId, occurrence,
                    socialCatalog, false, true, true);
                bool exposed = ReadBool(result, "exposed", false);
                double activityDay = isPlayer || exposed ? worldDay
                    : reset ? -1d : ReadDouble(state, "last_activity_day", -1d);
                args["player"] = isPlayer ? 1 : 0;
                args["count"] = count;
                args["activity"] = activityDay;
                ExecuteSql(connection, @"INSERT INTO whoremonger_activity(campaign_id,timeline_id,subject_id,
is_player,visit_count,last_activity_day,last_event_day,updated_ts)
VALUES($campaign,$timeline,$subject,$player,$count,$activity,$day,$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
is_player=$player,visit_count=$count,last_activity_day=$activity,last_event_day=$day,updated_ts=$ts;", args);

                if (isPlayer || exposed)
                {
                    args["expires"] = worldDay + WhoremongerRules.InactivityDays;
                    ExecuteSql(connection, @"UPDATE rumor_occurrences SET expires_day=$expires,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='whoremonger'
AND thread_key=$subject||'|whoremonger' AND status='active';", args);
                }
                // Historical exposures remain available, but exactly one rumor can be
                // active; an established reputation always replaces that rumor.
                if (exposed)
                {
                    args["occurrence"] = ReadString(result, "occurrenceId", "");
                    ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='superseded',updated_ts=$ts
WHERE subject_id=$subject AND tag_id='whoremonger' AND status='active'
AND occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='whoremonger')
AND (occurrence_id<>$occurrence OR EXISTS (SELECT 1 FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
AND tag_id='whoremonger' AND status='active'));", args);
                }
                result["eventId"] = eventId;
                result["subjectId"] = subjectId;
                result["exposureDay"] = worldDay;
                result["visitCount"] = count;
                result["inactivityReset"] = reset;
                result["lastActivityDay"] = activityDay;
                result["inactivityDeadline"] = activityDay < 0d ? -1d : activityDay + WhoremongerRules.InactivityDays;
                result["honor"] = honor.HasValue && !double.IsNaN(honor.Value) && !double.IsInfinity(honor.Value) ? (object)honor.Value : null;
                result["judgment"] = judgment.HasValue && !double.IsNaN(judgment.Value) && !double.IsInfinity(judgment.Value) ? (object)judgment.Value : null;
                args["input"] = inputJson;
                args["result"] = Json.Serialize(result);
                ExecuteSql(connection, @"INSERT INTO whoremonger_event_receipts(campaign_id,timeline_id,event_id,
subject_id,town_id,world_day,input_json,result_json,created_ts)
VALUES($campaign,$timeline,$event,$subject,$town,$day,$input,$result,$ts);", args);
                RecomputePublicStandingForSubjects(connection, campaignId, timelineId, new[] { subjectId }, worldDay);
                if (!useExistingTransaction) ExecuteSql(connection, "COMMIT;");
                return result;
            }
            catch
            {
                if (!useExistingTransaction) { try { ExecuteSql(connection, "ROLLBACK;"); } catch { } }
                throw;
            }
        }

        private static void ClearWhoremongerStreak(ReignDbConnection connection,
            string campaignId, string timelineId, string subjectId, double worldDay)
        {
            var args = new Dictionary<string, object> { ["campaign"] = campaignId,
                ["timeline"] = timelineId, ["subject"] = subjectId,
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='expired',updated_ts=$ts
WHERE subject_id=$subject AND tag_id='whoremonger' AND status='active'
AND occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='whoremonger');
UPDATE rumor_occurrences SET status='expired',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='whoremonger'
AND thread_key=$subject||'|whoremonger' AND status='active';
DELETE FROM rumor_exposure_streaks WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='whoremonger' AND thread_key=$subject||'|whoremonger';
DELETE FROM shared_tag_exposure_streaks WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND tag_id='whoremonger';
UPDATE whoremonger_activity SET visit_count=0,last_activity_day=-1,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", args);
        }

        private static int ExpireWhoremongerActivity(ReignDbConnection connection,
            string campaignId, string timelineId, double worldDay)
        {
            var expired = QuerySql(connection, @"SELECT subject_id FROM whoremonger_activity
WHERE campaign_id=$campaign AND timeline_id=$timeline AND last_activity_day>=0
AND last_activity_day+30<=$day;", new Dictionary<string, object>
            { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay });
            var ids = expired.Select(row => ReadString(row, "subject_id", "")).ToList();
            foreach (string id in ids) ClearWhoremongerStreak(connection, campaignId, timelineId, id, worldDay);
            if (ids.Count > 0) RecomputePublicStandingForSubjects(connection, campaignId, timelineId, ids, worldDay);
            return ids.Count;
        }

        private static bool IsWhoremongerStandingSource(Dictionary<string, object> source)
        {
            return ReadString(source, "tagId", "").Equals(WhoremongerRules.TagId, StringComparison.OrdinalIgnoreCase);
        }

        private static void CollapseWhoremongerPublicStanding(List<Dictionary<string, object>> sources)
        {
            var entries = sources.Where(IsWhoremongerStandingSource).ToList();
            var retained = entries.OrderByDescending(source => ReadString(source, "sourceType", "") == "reputation")
                .ThenByDescending(source => ReadDouble(source, "acquiredDay", 0d))
                .ThenBy(source => ReadString(source, "sourceId", ""), StringComparer.Ordinal).FirstOrDefault();
            sources.RemoveAll(source => IsWhoremongerStandingSource(source) && !ReferenceEquals(source, retained));
        }

        private static double WhoremongerSpouseStandingAdjustment(string campaignId,
            string observerId, string subjectId, List<Dictionary<string, object>> sources)
        {
            var entry = sources.FirstOrDefault(IsWhoremongerStandingSource);
            if (entry == null || string.IsNullOrWhiteSpace(observerId) || observerId == subjectId) return 0d;
            var profile = ReadJsonObject(CharacterFile(campaignId, subjectId, "profile.json"));
            var observerProfile = ReadJsonObject(CharacterFile(campaignId, observerId, "profile.json"));
            bool spouses = ReadString(profile, "spouseId", "").Equals(observerId, StringComparison.OrdinalIgnoreCase)
                || ReadString(observerProfile, "spouseId", "").Equals(subjectId, StringComparison.OrdinalIgnoreCase);
            if (!spouses) return 0d;
            int raw = ReadInt(entry, "rawValue", 0);
            double existing = ReadDouble(entry, "effectiveContribution", 0d);
            // The source already carries current Charm mitigation. Substitute the
            // spouse base before the surrounding observer sum is rounded.
            return raw < 0 ? WhoremongerRules.BaseContribution(false, true) * (existing / raw) - existing : 0d;
        }
    }
}
