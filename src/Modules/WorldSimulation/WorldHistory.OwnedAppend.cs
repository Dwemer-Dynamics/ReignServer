using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Reign.Core.Contracts.Actions;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        internal const string OwnedJusticeHistoryAppendRoute = "/world-history/owned-justice/append";
        internal const int OwnedJusticeHistoryAppendMaxBytes = 16 * 1024;
        private const string OwnedDiscoveryPrefix = "arrest_discovery_";
        private const string OwnedSettlementPrefix = "arrest_settlement_";

        private static bool IsReservedOwnedJusticeEventId(string id) =>
            id.StartsWith(OwnedDiscoveryPrefix, StringComparison.Ordinal) || id.StartsWith(OwnedSettlementPrefix, StringComparison.Ordinal);

        internal static Dictionary<string, object> WorldHistoryOwnedJusticeAppendApi(Dictionary<string, object> payload)
        {
            if (!TryPrepareOwnedJusticeAppend(payload, out var append))
                return OwnedJusticeHistoryAppendFailure("invalid_request");

            using var scope = EnterWorldHistoryTimelineScope(append.Campaign);
            using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(append.Campaign,
                trackCampaignMutations: true, initializeInfrastructure: false);
            if (connection == null) return OwnedJusticeHistoryAppendFailure("owner_mismatch");

            using var transaction = connection.BeginTransaction();
            AcquirePostgreSqlTransactionMutationLock(connection, transaction);
            if (!TableExists(connection, "memory_precision_state") || !TableExists(connection, "world_history_timelines")
                || !TableExists(connection, "world_history_events") || !TableExists(connection, "world_history_entities")
                || !TableExists(connection, "world_history_ingest_state") || !TableExists(connection, "world_history_knowledge_rules")
                || !TableExists(connection, "world_history_fts")
                || !PersistenceService.IsCampaignMigrationReady(connection, PersistenceService.ArrestLateEvidenceReceiptMigrationVersion))
                return OwnedJusticeHistoryAppendFailure("unavailable");

            var generation = DialogueStore.SelectMemoryPrecisionState(connection, transaction: transaction).SingleOrDefault();
            if (generation == null || ReadString(generation, "restore_generation", "") != append.Generation
                || ReadOwnedHistoryLoadSession(connection, transaction, append.Generation) != append.LoadSession)
                return OwnedJusticeHistoryAppendFailure("owner_mismatch");

            var key = TestDict("campaign", append.Campaign, "timeline", append.Timeline,
                "generation", append.Generation, "id", append.Event.EventId,
                "previousSequence", append.PreviousSequence, "previousEventId", append.PreviousEventId,
                "settlesDiscovery", append.Settlement?.DiscoveryId ?? "");
            var head = WorldSimulationStore.SelectOwnedWorldHistoryHead(connection, key, transaction, lockForAppend: true);
            if (head == null) return OwnedJusticeHistoryAppendFailure("owner_mismatch");

            if (append.Settlement != null && !OwnedJusticeSettlementMatchesReceipt(connection, transaction, append))
                return OwnedJusticeHistoryAppendFailure("effect_receipt_mismatch");

            var existing = WorldSimulationStore.SelectWorldHistoryEvents7(connection,
                TestDict("id", append.Event.EventId), transaction).SingleOrDefault();
            if (existing != null)
            {
                var retained = WorldSimulationStore.SelectOwnedWorldHistoryEventProof(connection, key, transaction);
                var admitted = WorldSimulationStore.SelectOwnedJusticeAdmission(connection, key, transaction);
                if (ReadLong(admitted, "previous_sequence", -1) != append.PreviousSequence
                    || ReadString(admitted, "previous_event_id", "") != append.PreviousEventId
                    || !OwnedJusticeHistoryMatches(append, retained))
                    return OwnedJusticeHistoryAppendFailure("id_conflict");
                transaction.Commit();
                return OwnedJusticeHistoryAppendResult(append, retained, "replayed");
            }

            if (ReadLong(head, "head_sequence", -1) != append.PreviousSequence
                || ReadString(head, "head_event_id", "") != append.PreviousEventId)
                return OwnedJusticeHistoryAppendFailure("predecessor_mismatch");

            if (append.Settlement != null && WorldSimulationStore.HasOwnedJusticeSettlement(connection, key, transaction))
                return OwnedJusticeHistoryAppendFailure("id_conflict");

            var cursor = TestDict("timeline", append.Timeline, "client", "reign_native_campaign", "now", append.Event.CreatedUtc,
                "sequence", append.Event.Sequence, "event", append.Event.EventId);
            var previousIngest = WorldSimulationStore.SelectWorldHistoryIngestCursor(connection, cursor, transaction);
            long previousIngestSequence = ReadLong(previousIngest, "last_sequence", -1);
            string previousIngestId = ReadString(previousIngest, "last_event_id", "");
            if (previousIngestSequence > append.PreviousSequence
                || (previousIngestSequence == append.PreviousSequence && previousIngestId.Length != 0
                    && previousIngestId != append.PreviousEventId))
                return OwnedJusticeHistoryAppendFailure("predecessor_mismatch");

            if (PersistenceService.InsertWorldHistoryPostgreSqlBulk(connection, transaction, append.Campaign,
                    append.Timeline, new List<PersistenceService.PreparedWorldHistoryEvent> { append.Event }) != 1)
                return OwnedJusticeHistoryAppendFailure("id_conflict");

            var written = WorldSimulationStore.SelectOwnedWorldHistoryEventProof(connection, key, transaction);
            if (!OwnedJusticeHistoryMatches(append, written))
                throw new InvalidOperationException("The owned justice event differs from its stored projection.");

            WorldSimulationStore.InsertOwnedJusticeAdmission(connection, key, transaction);
            WorldSimulationStore.UpdateWorldHistoryTimelines2(connection, cursor, transaction);
            WorldSimulationStore.UpsertWorldHistoryIngestState(connection, cursor, transaction);
            var advanced = WorldSimulationStore.SelectOwnedWorldHistoryHead(connection, key, transaction);
            var ingested = WorldSimulationStore.SelectWorldHistoryIngestCursor(connection, cursor, transaction);
            if (ReadLong(advanced, "head_sequence", -1) != append.Event.Sequence
                || ReadString(advanced, "head_event_id", "") != append.Event.EventId
                || ReadLong(ingested, "last_sequence", -1) != append.Event.Sequence
                || ReadString(ingested, "last_event_id", "") != append.Event.EventId)
                throw new InvalidOperationException("The owned justice append did not advance its paired cursor.");

            PersistenceService.MarkSaveSyncActiveStateDirty(append.Campaign);
            transaction.Commit();
            return OwnedJusticeHistoryAppendResult(append, written, "inserted");
        }

        // Every production timeline-head writer takes the campaign read gate. Save Sync owns
        // its write gate through the snapshot, so taking the timeline monitor here is unsafe:
        // a contending reader may already have acquired it while its read lease is suspended.
        internal static bool MatchesOwnedJusticeSaveBoundary(string campaign, string timeline, string generation,
            string loadSession, long sequence, string eventId)
        {
            if (!Program.CampaignDataGate.IsWriteLockHeld) return false;
            using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                trackCampaignMutations: false, initializeInfrastructure: false);
            if (connection == null || !TableExists(connection, "memory_precision_state")
                || !TableExists(connection, "world_history_timelines") || !TableExists(connection, "world_history_ingest_state"))
                return false;
            using var snapshot = connection.BeginTransaction();
            var head = WorldSimulationStore.SelectOwnedWorldHistoryHead(connection,
                TestDict("campaign", campaign, "timeline", timeline, "generation", generation), snapshot);
            var cursor = WorldSimulationStore.SelectWorldHistoryIngestCursor(connection,
                TestDict("timeline", timeline, "client", "reign_native_campaign"), snapshot);
            bool matches = head != null && ReadLong(head, "head_sequence", -1) == sequence
                && ReadString(head, "head_event_id", "") == eventId
                && ReadLong(cursor, "last_sequence", cursor == null ? 0L : -1L) == sequence
                && ReadString(cursor, "last_event_id", "") == eventId
                && ReadOwnedHistoryLoadSession(connection, snapshot, generation) == loadSession;
            snapshot.Commit();
            return matches;
        }

        private sealed record OwnedJusticeSettlement(string DiscoveryId, string EffectIntentId, string Tag, string Player, string Accused,
            long AppliedDelta, long Movement, long Reputation);

        private sealed record OwnedJusticeAppend(string Campaign, string Timeline, string Generation, string LoadSession,
            long PreviousSequence, string PreviousEventId, PersistenceService.PreparedWorldHistoryEvent Event, OwnedJusticeSettlement Settlement);

        private static bool TryPrepareOwnedJusticeAppend(Dictionary<string, object> payload, out OwnedJusticeAppend append)
        {
            append = null;
            if (payload == null || payload.Count != 8 || OwnedHistoryInteger(payload, "historyAppendVersion") != 1
                || !TryReadOwnedHistoryScope(payload, "campaignId", out string campaign)
                || !TryReadOwnedHistoryScope(payload, "timelineId", out string timeline)
                || !OwnedHistoryText(payload, "sharedActivityGeneration", 160, out string generation)
                || !OwnedHistoryText(payload, "nativeLoadSessionId", 160, out string loadSession)
                || (loadSession.Length != 0 && !TerminalActionReportProtocol.IsTrustedIdentifier(loadSession))
                || !OwnedHistoryText(payload, "previousEventId", 160, out string previousId)
                || !payload.TryGetValue("event", out object rawEvent) || rawEvent is not Dictionary<string, object> fields)
                return false;

            long previous = OwnedHistoryInteger(payload, "previousSequence");
            long sequence = OwnedHistoryInteger(fields, "sequence");
            if (previous < 0 || previous == long.MaxValue || sequence != previous + 1
                || (previous == 0 ? previousId.Length != 0 : !TerminalActionReportProtocol.IsTrustedIdentifier(previousId))
                || !OwnedHistoryId(fields, "eventId", out string eventId)
                || !OwnedHistoryId(fields, "caseId", out string caseId) || !caseId.StartsWith("arrest_", StringComparison.Ordinal)
                || !OwnedHistoryId(fields, "playerHeroId", out string player)
                || !OwnedHistoryId(fields, "accusedHeroId", out string accused) || player == accused
                || !OwnedHistoryText(fields, "locationId", 160, out string location) || (location.Length != 0 && !OwnedHistoryId(location))
                || !OwnedHistoryText(fields, "locationName", 120, out string locationName)
                || !fields.TryGetValue("worldDay", out object rawDay))
                return false;

            double day = rawDay switch { decimal d => (double)d, double d => d, long l => l, int i => i, _ => -1 };
            if (!double.IsFinite(day) || day < 0) return false;
            var material = TestDict("caseId", caseId, "accusedHeroId", accused, "playerHeroId", player);
            OwnedJusticeSettlement settlement = null;
            if (fields.ContainsKey("effectIntentId"))
            {
                if (fields.Count != 14 || !OwnedHistoryId(fields, "discoveryEventId", out string discoveryId)
                    || !OwnedHistoryId(fields, "effectIntentId", out string intentId)
                    || !OwnedHistoryId(fields, "reputationTagId", out string tag)
                    || !OwnedHistoryAmount(fields, "appliedRelationshipDelta", 100, out long applied)
                    || !OwnedHistoryAmount(fields, "measuredRelationshipChange", 200, out long movement)
                    || !OwnedHistoryAmount(fields, "reputationValue", 100, out long reputation))
                    return false;
                settlement = new OwnedJusticeSettlement(discoveryId, intentId, tag, player, accused, applied, movement, reputation);
                material["discoveryEventId"] = discoveryId;
                material["effectIntentId"] = intentId;
                material["appliedRelationshipDelta"] = applied;
                material["measuredRelationshipChange"] = movement;
                material["reputationValue"] = reputation;
                material["effectStatus"] = "measured";
            }
            else
            {
                if (fields.Count != 9 || !fields.TryGetValue("evidenceIds", out object rawEvidence)
                    || rawEvidence is not object[] values || values.Length < 1 || values.Length > 16) return false;
                string[] evidence = values.OfType<string>().ToArray();
                if (evidence.Length != values.Length || evidence.Any(id => !OwnedHistoryId(id))
                    || evidence.Distinct(StringComparer.Ordinal).Count() != evidence.Length) return false;
                material["evidenceIds"] = evidence;
                material["effectStatus"] = "pending";
            }
            bool measured = settlement != null;
            string eventPrefix = measured ? OwnedSettlementPrefix : OwnedDiscoveryPrefix;
            if (!eventId.StartsWith(eventPrefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(eventId.Substring(eventPrefix.Length), "N", out Guid eventNonce)
                || eventNonce == Guid.Empty || eventId != eventPrefix + eventNonce.ToString("N"))
                return false;
            var normalized = TestDict("eventId", eventId,
                "eventType", measured ? "arrest_reclassification_effect_settled" : "arrest_evidence_reclassified",
                "phase", measured ? "effect_settled" : "evidence_established", "category", "justice", "correlationId", caseId,
                "sequence", sequence, "worldDay", day, "locationId", location, "locationName", locationName,
                "source", "reign_system", "disseminationClass", "ordinary", "isComplete", true,
                "summary", measured ? "The relationship and reputation effects of reclassification were measured."
                    : "New evidence established cause in an active arrest.", "payload", material,
                "entities", new List<Dictionary<string, object>>
                {
                    TestDict("entityId", player, "entityType", "hero", "role", "political actor", "isPlayer", true),
                    TestDict("entityId", accused, "entityType", "hero", "role", "political participant")
                });
            var prepared = PersistenceService.PrepareWorldHistoryEvent(normalized, DateTimeOffset.UtcNow.ToString("O"));
            if (prepared.Retention == "ephemeral" || prepared.Entities.Count != 2) return false;
            append = new OwnedJusticeAppend(campaign, timeline, generation, loadSession, previous, previousId, prepared, settlement);
            return true;
        }

        private static bool OwnedJusticeSettlementMatchesReceipt(ReignDbConnection connection,
            ReignDbTransaction transaction, OwnedJusticeAppend append)
        {
            var settlement = append.Settlement;
            var key = TestDict("campaign", append.Campaign, "timeline", append.Timeline, "case", append.Event.CorrelationId,
                "subject", settlement.Accused, "player", settlement.Player, "tag", settlement.Tag,
                "generation", append.Generation, "id", settlement.DiscoveryId);
            var source = WorldSimulationStore.SelectOwnedWorldHistoryEventProof(connection, key, transaction);
            if (ReadString(source, "event_type", "") != "arrest_evidence_reclassified"
                || ReadString(source, "correlation_id", "") != append.Event.CorrelationId
                || ReadLong(source, "sequence", long.MaxValue) >= append.Event.Sequence
                || WorldSimulationStore.SelectOwnedJusticeAdmission(connection, key, transaction) == null) return false;
            var retained = WorldSimulationStore.SelectArrestLateEvidenceReceipt(connection, key, transaction);
            if (ReadString(retained, "late_discovery_id", "") != settlement.DiscoveryId
                || ReadString(retained, "late_effect_intent_id", "") != settlement.EffectIntentId) return false;
            var receipt = TryParseStrictJsonObject(ReadString(retained, "late_effect_receipt_json", "{}"));
            return ReadBool(receipt, "ok", false) && ReadString(receipt, "status", "") == "committed"
                && ReadLong(receipt, "appliedRelationshipDelta", long.MinValue) == settlement.AppliedDelta
                && ReadLong(receipt, "measuredRelationshipChange", long.MinValue) == settlement.Movement
                && ReadLong(receipt, "reputationValue", long.MinValue) == settlement.Reputation
                && ReadDouble(receipt, "worldDay", -1) == append.Event.WorldDay;
        }

        private static string ReadOwnedHistoryLoadSession(ReignDbConnection connection, ReignDbTransaction transaction, string generation)
        {
            if (!TableExists(connection, "memory_restored_source_context"))
                return null;
            var row = DialogueStore.SelectMemoryRestoredSourceContext2(connection, transaction: transaction).SingleOrDefault();
            if (row == null) return string.Empty;
            var context = TryParseJsonObject(ReadString(row, "context", ""));
            return ReadString(context, "restore_generation", "") == generation
                && context != null && context.TryGetValue("load_session_id", out object session) && session is string id ? id : null;
        }

        private static long OwnedHistoryInteger(Dictionary<string, object> fields, string key)
            => fields.TryGetValue(key, out object value) ? value switch { int i => i, long l => l, _ => -1 } : -1;

        private static bool OwnedHistoryAmount(Dictionary<string, object> fields, string key, long bound, out long amount)
        {
            amount = 0;
            if (!fields.TryGetValue(key, out object value) || value is not (int or long)) return false;
            amount = Convert.ToInt64(value);
            return amount >= -bound && amount <= bound;
        }

        private static bool OwnedHistoryText(Dictionary<string, object> fields, string key, int limit, out string text)
        {
            text = fields.TryGetValue(key, out object value) ? value as string : null;
            return text != null && text.Length <= limit && !text.Any(char.IsControl);
        }

        private static bool TryReadOwnedHistoryScope(Dictionary<string, object> fields, string key, out string id)
            => OwnedHistoryText(fields, key, 180, out id)
                && Regex.IsMatch(id, @"\A[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant);

        private static bool OwnedHistoryId(Dictionary<string, object> fields, string key, out string id)
            => OwnedHistoryText(fields, key, 160, out id) && OwnedHistoryId(id);

        private static bool OwnedHistoryId(string id)
            => id != null && Regex.IsMatch(id, @"\A[A-Za-z0-9_.:-]{1,160}\z", RegexOptions.CultureInvariant);

        private static bool OwnedJusticeHistoryMatches(OwnedJusticeAppend append, Dictionary<string, object> saved)
        {
            var expected = append.Event;
            if (saved == null || ReadString(saved, "event_id", "") != expected.EventId
                || ReadString(saved, "campaign_id", "") != append.Campaign || ReadString(saved, "timeline_id", "") != append.Timeline
                || ReadLong(saved, "sequence", -1) != expected.Sequence || ReadDouble(saved, "world_day", -1) != expected.WorldDay
                || ReadString(saved, "event_type", "") != expected.EventType || ReadString(saved, "category", "") != expected.Category
                || ReadString(saved, "phase", "") != expected.Phase || ReadString(saved, "correlation_id", "") != expected.CorrelationId
                || ReadString(saved, "location_id", "") != expected.LocationId || ReadString(saved, "location_name", "") != expected.LocationName
                || ReadString(saved, "dissemination_class", "") != expected.Dissemination || ReadString(saved, "summary", "") != expected.Summary
                || ReadString(saved, "source", "") != expected.Source || ReadInt(saved, "is_complete", 0) != 1
                || !OwnedHistoryJsonEquals(ReadString(saved, "payload_json", ""), expected.PayloadJson)) return false;

            var children = ReadDictionaryList(TestDict("entities",
                Json.DeserializeObject(ReadString(saved, "entities_json", "[]"))), "entities");
            return children.Count == expected.Entities.Count && children.Select((child, index) =>
                ReadLong(child, "ordinal", -1) == index && ReadString(child, "event_id", "") == expected.EventId
                && OwnedHistoryJsonEquals(ReadString(child, "payload_json", ""), Json.Serialize(expected.Entities[index]))).All(match => match);
        }

        private static bool OwnedHistoryJsonEquals(string actual, string expected)
        {
            try
            {
                using var left = JsonDocument.Parse(actual);
                using var right = JsonDocument.Parse(expected);
                return JsonElement.DeepEquals(left.RootElement, right.RootElement);
            }
            catch (JsonException) { return false; }
        }

        private static Dictionary<string, object> OwnedJusticeHistoryAppendFailure(string result)
            => TestDict("ok", false, "historyAppendVersion", 1, "result", result);

        private static Dictionary<string, object> OwnedJusticeHistoryAppendResult(OwnedJusticeAppend append,
            Dictionary<string, object> row, string result)
        {
            row["entities"] = Json.DeserializeObject(ReadString(row, "entities_json", "[]"));
            row.Remove("entities_json");
            var owner = TestDict("campaignId", append.Campaign, "timelineId", append.Timeline,
                "sharedActivityGeneration", append.Generation, "nativeLoadSessionId", append.LoadSession);
            return TestDict("ok", true, "historyAppendVersion", 1, "result", result, "ownership", owner, "event", row);
        }
    }
}
