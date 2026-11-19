using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class ReputationService
    {
        private const string WhoremongerOutOfOrderError =
            "Whoremonger events must be processed in campaign order within their timeline.";
        private const string WhoremongerTemporalIdentityError =
            "The Whoremonger subject must be a synchronized living adult of the expected player/NPC role.";

        private static void EnsureWhoremongerSchema(ReignDbConnection connection)
        {
            ReputationSchema.CreateWhoremongerActivity(connection);
            ReputationSchema.CreateWhoremongerEventReceipts(connection);
            ReputationSchema.CreateIndexWhoremongerInactivity(connection);
            if (WorldSimulationService.TableExists(connection, "social_outcome_receipts"))
            {
                List<Dictionary<string, object>> recoveryScopes =
                    WorldSimulationService.TableExists(connection, "world_history_events")
                    && WorldSimulationService.TableExists(connection, "social_subject_work_queue")
                        ? ReputationStore.SelectSocialOutcomeReceipts6(connection, new Dictionary<string, object>
                        {
                            ["orderingError"] = WhoremongerOutOfOrderError,
                            ["identityError"] = WhoremongerTemporalIdentityError
                        })
                        : new List<Dictionary<string, object>>();
                ReputationStore.UpdateSocialOutcomeReceipts4(connection, new Dictionary<string, object>
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
                    ReputationStore.UpsertSocialSubjectWorkQueue2(connection, queueArgs);
                    SchedulePendingSocialWorldHistoryOutcomes(
                        ReadString(scope, "campaign_id", ""),
                        ReadString(scope, "timeline_id", ""));
                }
            }
        }

        // Called inside the authoritative visit-confirmation transaction. The caller must
        // first validate the payment receipt and initialize the social schema.
        internal static Dictionary<string, object> RegisterWhoremongerVisit(
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
                Program.ReadJsonObject(Program.CharacterFile(campaignId, subjectId, "traits.json")), "courtVirtues")
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
            if (!useExistingTransaction) ReputationStore.Begin(connection);
            try
            {
                // A row update is also the PostgreSQL serialization point for two
                // concurrent confirmations concerning the same subject.
                args["player"] = isPlayer ? 1 : 0;
                ReputationStore.InsertOrIgnoreWhoremongerActivity(connection, args);
                var prior = ReputationStore.SelectWhoremongerEventReceipts(connection, args).FirstOrDefault();
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
                    if (!useExistingTransaction) ReputationStore.Commit(connection);
                    return replay;
                }
                var roster = ReputationStore.SelectIdentityRoster14(connection, args).FirstOrDefault();
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
                    ReputationStore.InsertWhoremongerEventReceipts(connection, args);
                    if (!useExistingTransaction) ReputationStore.Commit(connection);
                    return obsolete;
                }

                var state = ReputationStore.SelectWhoremongerActivity(connection, args).FirstOrDefault();
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
                    ReputationStore.InsertWhoremongerEventReceipts(connection, args);
                    if (!useExistingTransaction) ReputationStore.Commit(connection);
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
                ReputationStore.UpsertWhoremongerActivity(connection, args);

                if (isPlayer || exposed)
                {
                    args["expires"] = worldDay + WhoremongerRules.InactivityDays;
                    ReputationStore.UpdateRumorOccurrences6(connection, args);
                }
                // Historical exposures remain available, but exactly one rumor can be
                // active; an established reputation always replaces that rumor.
                if (exposed)
                {
                    args["occurrence"] = ReadString(result, "occurrenceId", "");
                    ReputationStore.UpdateRumorSubjectTags7(connection, args);
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
                ReputationStore.InsertWhoremongerEventReceipts(connection, args);
                RecomputePublicStandingForSubjects(connection, campaignId, timelineId, new[] { subjectId }, worldDay);
                if (!useExistingTransaction) ReputationStore.Commit(connection);
                return result;
            }
            catch
            {
                if (!useExistingTransaction) { try { ReputationStore.Rollback(connection); } catch { } }
                throw;
            }
        }

        private static void ClearWhoremongerStreak(ReignDbConnection connection,
            string campaignId, string timelineId, string subjectId, double worldDay)
        {
            var args = new Dictionary<string, object> { ["campaign"] = campaignId,
                ["timeline"] = timelineId, ["subject"] = subjectId,
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            ReputationStore.UpdateRumorSubjectTags8(connection, args);
        }

        private static int ExpireWhoremongerActivity(ReignDbConnection connection,
            string campaignId, string timelineId, double worldDay)
        {
            var expired = ReputationStore.SelectWhoremongerActivity2(connection, new Dictionary<string, object>
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

        // With a file snapshot (terminal acceptance under row locks) both profiles come
        // only from it; a missing entry is refused rather than read under FileLock.
        internal static double WhoremongerSpouseStandingAdjustment(string campaignId,
            string observerId, string subjectId, List<Dictionary<string, object>> sources,
            RelationshipsService.RelationshipFileSnapshot files = null)
        {
            var entry = sources.FirstOrDefault(IsWhoremongerStandingSource);
            if (entry == null || string.IsNullOrWhiteSpace(observerId) || observerId == subjectId) return 0d;
            var profile = files != null ? files.Document(subjectId, "profile.json")
                : Program.ReadJsonObject(Program.CharacterFile(campaignId, subjectId, "profile.json"));
            var observerProfile = files != null ? files.Document(observerId, "profile.json")
                : Program.ReadJsonObject(Program.CharacterFile(campaignId, observerId, "profile.json"));
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
