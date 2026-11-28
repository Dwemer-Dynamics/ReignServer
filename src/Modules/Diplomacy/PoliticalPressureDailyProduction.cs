using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class DiplomacyService
    {
        private const string PressureDailyPlanFormat = "reign-political-pressure-day-v1";

        private sealed record PressureStorageWitness(string StorageId, string RestoreGeneration);
        private sealed record PressureDayClaim(string Campaign, string Timeline, int Day, double WorldDay, int Roll,
            PressureStorageWitness Witness, string Token, string InputJson, string InputSha,
            string PreparedJson, string PreparedSha)
        {
            internal Dictionary<string, object> Terms() => TestDict("campaign", Campaign, "timeline", Timeline,
                "day", Day, "storage", Witness.StorageId, "generation", Witness.RestoreGeneration, "claim", Token,
                "inputSha", InputSha);
        }

        private static string PressureDailyHash(string json) => Sha256Hex(Encoding.UTF8.GetBytes(json));

        private static PressureStorageWitness ReadPressureStorageWitness(ReignDbConnection connection,
            ReignDbTransaction transaction, string campaign, string timeline)
        {
            var row = DiplomacyStore.ReadPoliticalPressureStorageWitness(connection, transaction,
                TestDict("campaign", campaign, "timeline", timeline));
            if (row == null || !row.TryGetValue("storage_witness", out object storage) || storage is not string id
                || id.Length == 0 || !row.TryGetValue("restore_generation", out object generation)
                || generation is not string exactGeneration) return null;
            return new PressureStorageWitness(id, exactGeneration);
        }

        private static bool PressureWitnessMatchesWire(PressureStorageWitness witness, Dictionary<string, object> request) =>
            witness != null && (!request.TryGetValue("sharedActivityGeneration", out object supplied)
                || supplied is string generation && generation == witness.RestoreGeneration);

        private static bool PressureWitnessMatchesRow(PressureStorageWitness witness, Dictionary<string, object> row) =>
            witness != null && ReadString(row, "storage_witness", "") == witness.StorageId
                && ReadString(row, "restore_generation", null) == witness.RestoreGeneration;

        private static Dictionary<string, object> PressureDayDeferred(string status, int day)
            => TestDict("ok", false, "status", status, "day", day,
                "retryable", status == "processing" || status == "scope_changed");

        private static bool TryEnterPressureMutation(ReignDbConnection connection, ReignDbTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 5;
            command.CommandText = "SELECT pg_try_advisory_xact_lock(hashtextextended(current_schema(), 9137));";
            return command.ExecuteScalar() is true;
        }

        private static Dictionary<string, object> PressureDayHeld(int day, string reason)
            => TestDict("ok", false, "status", "recovery_required", "day", day,
                "retryable", false, "recoveryHeld", true, "reason", reason);

        private static Dictionary<string, object> HoldPressureDay(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> key, string campaign, int day, string reason)
        {
            key["reason"] = reason;
            DiplomacyStore.InsertPoliticalPressureDailyHold(connection, transaction, key);
            PersistenceService.MarkSaveSyncActiveStateDirty(campaign);
            transaction.Commit();
            return PressureDayHeld(day, reason);
        }

        private static bool IsPressureDayFirst(ReignDbConnection connection, ReignDbTransaction transaction,
            Dictionary<string, object> terms, int day)
        {
            var older = DiplomacyStore.FindUnfinishedPoliticalPressureDays(connection, transaction, terms);
            return older.Count == 0 || older[0] == day;
        }

        private static bool IsTerminalLegacyPressureStatus(string status)
            => status is "incident_created" or "roll_failed" or "no_eligible_kingdoms"
                or "no_eligible_target" or "catalog_cooldown" or "no_eligible_petitioners";

        private static bool HasPressureSnapshotArray(Dictionary<string, object> request, string key)
            => request.TryGetValue(key, out object items) && items is IList entries
                && entries.Cast<object>().All(entry => entry is Dictionary<string, object>);

        private static bool HasPressureJson(string json, string digest)
            => !string.IsNullOrEmpty(json) && PressureDailyHash(json) == digest;

        private static Dictionary<string, object> ClaimPressureDay(
            string campaign, string timeline, int day, double requestedWorldDay, int roll,
            Dictionary<string, object> request, out PressureDayClaim claim)
        {
            claim = null;
            using var timelineScope = WorldSimulationService.EnterWorldHistoryTimelineScope(campaign);
            using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                trackCampaignMutations: true, initializeInfrastructure: false);
            if (connection == null) return PressureDayDeferred("scope_changed", day);

            WorldSimulationService.EnsureWorldTestTelemetrySchema(connection);
            using var suppress = new ReignDatabase.SqlStatementRetrySuppression(connection);
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            if (!TryEnterPressureMutation(connection, transaction)) return PressureDayDeferred("processing", day);
            var witness = ReadPressureStorageWitness(connection, transaction, campaign, timeline);
            if (!PressureWitnessMatchesWire(witness, request)) return PressureDayDeferred("scope_changed", day);

            var key = TestDict("campaign", campaign, "timeline", timeline, "day", day,
                "storage", witness.StorageId, "generation", witness.RestoreGeneration);
            var held = DiplomacyStore.ReadPoliticalPressureDailyHold(connection, transaction, key);
            if (held != null) return PressureDayHeld(day, ReadString(held, "reason", ""));
            if (!IsPressureDayFirst(connection, transaction, key, day))
                return PressureDayDeferred("processing", day);

            var existing = DiplomacyStore.FindPoliticalPressureDailyProducer(connection, transaction, key);
            var legacy = DiplomacyStore.SelectPoliticalPressureDailyRolls2(connection, key, transaction).SingleOrDefault();
            string legacyStatus = ReadString(legacy, "status", "");
            if (existing != null)
            {
                string result = ReadString(existing, "result_json", "");
                if (!PressureWitnessMatchesRow(witness, existing))
                {
                    if (result.Length > 0 && IsTerminalLegacyPressureStatus(legacyStatus)
                        && HasPressureJson(result, ReadString(existing, "result_sha256", ""))
                        && TryParseJsonObject(result) is Dictionary<string, object> historical
                        && ReadString(historical, "status", "") == legacyStatus)
                        return historical;
                    return HoldPressureDay(connection, transaction, key, campaign, day, "restore_scope_changed");
                }
                if (result.Length > 0)
                {
                    if (HasPressureJson(result, ReadString(existing, "result_sha256", ""))
                        && TryParseJsonObject(result) is Dictionary<string, object> terminal)
                        return terminal;
                    return HoldPressureDay(connection, transaction, key, campaign, day, "terminal_receipt_invalid");
                }
            }
            else
            {
                if (legacy != null && legacyStatus != "incident_created" && IsTerminalLegacyPressureStatus(legacyStatus))
                    return TestDict("ok", true, "status", "already_processed", "day", day,
                        "incidentId", ReadString(legacy, "incident_id", ""));

                if (legacyStatus == "processing"
                    && ReadLong(legacy, "created_ts", long.MaxValue) > DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 300)
                    return PressureDayDeferred("processing", day);

                string oldIncident = "political-pressure:" + timeline + ":" + day.ToString(CultureInfo.InvariantCulture);
                key["incident"] = oldIncident;
                var published = DiplomacyStore.ReadPublishedLegacyPressureIncident(connection, transaction, key);
                if (published != null)
                    return RecoverPublishedLegacyPressureDay(connection, transaction, key, campaign, day, roll,
                        legacy, legacyStatus, published);
                if ((legacy != null && legacyStatus != "processing")
                    || DiplomacyStore.HasLegacyPoliticalPressureFootprint(connection, transaction,
                        TestDict("campaign", campaign, "timeline", timeline, "incident", oldIncident, "day", day)))
                    return HoldPressureDay(connection, transaction, key, campaign, day, "legacy_publication_unverified");
                if (!HasPressureSnapshotArray(request, "kingdoms") || !HasPressureSnapshotArray(request, "clans")
                    || !HasPressureSnapshotArray(request, "settlements"))
                    return PressureDayDeferred("invalid_snapshot", day);
                if (legacy != null)
                {
                    requestedWorldDay = ReadDouble(legacy, "world_day", double.NaN);
                    roll = ReadInt(legacy, "roll", 0);
                    if (!double.IsFinite(requestedWorldDay) || requestedWorldDay < day || requestedWorldDay >= day + 1d
                        || roll < 1 || roll > 100 || ReadInt(legacy, "chance", 0) != PoliticalPressureIncidentChance)
                        return HoldPressureDay(connection, transaction, key, campaign, day, "legacy_claim_invalid");
                }
            }

            string input;
            if (existing == null)
                input = Json.Serialize(TestDict("kingdoms", ReadDictionaryList(request, "kingdoms"),
                    "clans", ReadDictionaryList(request, "clans"), "settlements", ReadDictionaryList(request, "settlements")));
            else
                input = ReadString(existing, "input_json", "");

            string inputSha = existing == null ? PressureDailyHash(input) : ReadString(existing, "input_sha256", "");
            if (!HasPressureJson(input, inputSha) || (existing != null
                && ReadString(existing, "result_json", "").Length > 0))
                return HoldPressureDay(connection, transaction, key, campaign, day, "input_receipt_invalid");
            string token = Guid.NewGuid().ToString("N");
            key["worldDay"] = requestedWorldDay;
            key["roll"] = roll;
            key["claim"] = token;
            key["input"] = input;
            key["inputSha"] = inputSha;
            if (!DiplomacyStore.TryClaimPoliticalPressureDailyProducer(connection, transaction, key))
                return PressureDayDeferred("processing", day);

            var current = DiplomacyStore.FindPoliticalPressureDailyProducer(connection, transaction, key);
            string prepared = ReadString(current, "prepared_json", "");
            string preparedSha = ReadString(current, "prepared_sha256", "");
            if (prepared.Length > 0 && !HasPressureJson(prepared, preparedSha))
                return HoldPressureDay(connection, transaction, key, campaign, day, "prepared_receipt_invalid");
            claim = new PressureDayClaim(campaign, timeline, day,
                ReadDouble(current, "world_day", requestedWorldDay), ReadInt(current, "daily_roll", roll),
                witness, token, ReadString(current, "input_json", ""), ReadString(current, "input_sha256", ""),
                prepared, preparedSha);
            PersistenceService.MarkSaveSyncActiveStateDirty(campaign);
            transaction.Commit();
            return null;
        }

        private static Dictionary<string, object> RecoverPublishedLegacyPressureDay(ReignDbConnection connection,
            ReignDbTransaction transaction, Dictionary<string, object> key, string campaign, int day, int roll,
            Dictionary<string, object> legacy, string legacyStatus, Dictionary<string, object> published)
        {
            double worldDay = ReadDouble(published, "world_day", double.NaN);
            string incident = ReadString(published, "incident_id", "");
            string origin = ReadString(published, "origin_kingdom_id", "");
            bool completedRoll = legacyStatus == "incident_created";
            if (incident.Length == 0 || origin.Length == 0 || !double.IsFinite(worldDay) || worldDay < day || worldDay >= day + 1d
                || (legacy != null && (legacyStatus != "processing" && !completedRoll
                    || ReadDouble(legacy, "world_day", double.NaN) != worldDay
                    || ReadInt(legacy, "chance", 0) != PoliticalPressureIncidentChance
                    || (completedRoll && (ReadInt(legacy, "passed", 0) != 1 || ReadString(legacy, "incident_id", "") != incident
                        || ReadString(legacy, "origin_kingdom_id", "") != origin)))))
                return HoldPressureDay(connection, transaction, key, campaign, day, "legacy_publication_conflict");

            if (legacy != null) roll = ReadInt(legacy, "roll", 0);
            if (roll < 1 || roll > PoliticalPressureIncidentChance)
                return HoldPressureDay(connection, transaction, key, campaign, day, "legacy_roll_conflict");

            key["worldDay"] = worldDay;
            key["chance"] = PoliticalPressureIncidentChance;
            key["roll"] = roll;
            key["origin"] = origin;
            key["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            key["chunk"] = "daily:" + day.ToString(CultureInfo.InvariantCulture);
            key["counter"] = Json.Serialize(TestDict("dailyRolls", 1, "dailyRollsPassed", 1, "status:incident_created", 1));
            if (!completedRoll && !DiplomacyStore.TryFinalizePublishedLegacyPressureDay(connection, transaction, key))
                throw new InvalidOperationException("Legacy pressure roll changed during metadata recovery.");
            bool insertedCounter = DiplomacyStore.InsertMissingLegacyPressureRollCounter(connection, transaction, key);
            if (!completedRoll || insertedCounter)
            {
                PersistenceService.MarkSaveSyncActiveStateDirty(campaign);
                transaction.Commit();
            }
            return TestDict("ok", true, "status", "already_processed", "day", day, "incidentId", incident);
        }

        private static PressureDayClaim SealPressureDay(PressureDayClaim claim, Dictionary<string, object> ready)
        {
            if (claim.PreparedJson.Length > 0) return claim;
            string prepared = Json.Serialize(ready);
            string preparedSha = PressureDailyHash(prepared);
            for (int attempt = 1; ; attempt++)
            {
                string deferred = TrySealPressurePlan(claim, prepared, preparedSha);
                if (deferred == null)
                {
                    if (attempt > 1) LogPressureSeal(claim, "recovered_advisory_busy", attempt);
                    return claim with { PreparedJson = prepared, PreparedSha = preparedSha };
                }
                if (deferred != "advisory_busy" || attempt == 4)
                {
                    LogPressureSeal(claim, deferred, attempt);
                    return null;
                }

                // Back off after closing the transaction, connection and timeline scope.
                // Keep the same serialized plan and claim token so an expired owner cannot publish.
                ObservePressurePublicationForTests?.Invoke("seal_busy");
                Thread.Sleep(50 << (attempt - 1));
            }
        }

        private static void LogPressureSeal(PressureDayClaim claim, string reason, int attempts)
            => OperationsService.LogOperational("diplomacy.pressure_producer_seal", TestDict(
                "campaignId", claim.Campaign, "timelineId", claim.Timeline, "day", claim.Day,
                "reason", reason, "attempts", attempts));

        private static string TrySealPressurePlan(PressureDayClaim claim, string prepared, string preparedSha)
        {
            using var timelineScope = WorldSimulationService.EnterWorldHistoryTimelineScope(claim.Campaign);
            using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(claim.Campaign,
                trackCampaignMutations: true, initializeInfrastructure: false);
            if (connection == null) return "connection_unavailable";
            using var suppress = new ReignDatabase.SqlStatementRetrySuppression(connection);
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var terms = claim.Terms();
            terms["prepared"] = prepared;
            terms["preparedSha"] = preparedSha;
            if (!TryEnterPressureMutation(connection, transaction)) return "advisory_busy";
            if (ReadPressureStorageWitness(connection, transaction, claim.Campaign, claim.Timeline) != claim.Witness)
                return "scope_changed";
            if (!DiplomacyStore.SealPoliticalPressureDailyProducer(connection, transaction, terms))
                return "claim_write_refused";
            PersistenceService.MarkSaveSyncActiveStateDirty(claim.Campaign);
            transaction.Commit();
            return null;
        }

        [ThreadStatic] private static Action<string> observePressurePublicationForTests;
        private static Action<string> ObservePressurePublicationForTests
        {
            get => observePressurePublicationForTests;
            set => observePressurePublicationForTests = value;
        }

        private static Dictionary<string, object> PublishPressureDay(PressureDayClaim claim, Dictionary<string, object> plan)
        {
            string status = ReadString(plan, "status", "");
            bool createsIncident = status == "incident_created";
            if (!createsIncident && status != "roll_failed" && status != "no_eligible_kingdoms"
                && status != "no_eligible_target" && status != "catalog_cooldown" && status != "no_eligible_petitioners")
                return PressureDayDeferred("recovery_required", claim.Day);
            string incident = "political-pressure:" + claim.Timeline + ":" + claim.Day.ToString(CultureInfo.InvariantCulture);
            if (createsIncident && ReadString(plan, "incident", "") != incident)
                return PressureDayDeferred("recovery_required", claim.Day);

            using var timelineScope = WorldSimulationService.EnterWorldHistoryTimelineScope(claim.Campaign);
            using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(claim.Campaign,
                trackCampaignMutations: true, initializeInfrastructure: false);
            if (connection == null) return PressureDayDeferred("scope_changed", claim.Day);
            WorldSimulationService.EnsureWorldTestTelemetrySchema(connection);

            RelationshipsService.RelationshipFileSnapshot files = null;
            if (createsIncident)
            {
                var origin = ReadDictionary(plan, "origin");
                var target = ReadDictionary(plan, "target");
                if (origin == null || target == null) return PressureDayDeferred("recovery_required", claim.Day);
                RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                RelationshipsService.EnsureWorldRelationshipSchema(connection);
                var people = ReadDictionaryList(plan, "originClans").Concat(ReadDictionaryList(plan, "targetClans"))
                    .SelectMany(PoliticalPressureClanMembers)
                    .Append(ReadString(origin, "leaderHeroId", "")).Append(ReadString(target, "leaderHeroId", ""));
                files = RelationshipsService.RelationshipFileSnapshot.Capture(claim.Campaign, claim.Timeline, people);
            }

            Dictionary<string, object> result;
            using (PersistenceService.EnterCampaignWorkLock(RelationshipsService.CampaignRelationshipWriteLock(claim.Campaign)))
            {
                using var standing = files == null ? null : new RelationshipsService.RelationshipStandingContext(
                    connection, claim.Campaign, claim.Timeline, files, deferPublication: true);
                using var suppress = new ReignDatabase.SqlStatementRetrySuppression(connection);
                using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                var terms = claim.Terms();
                terms["preparedSha"] = claim.PreparedSha;
                if (!TryEnterPressureMutation(connection, transaction))
                    return PressureDayDeferred("processing", claim.Day);
                if (ReadPressureStorageWitness(connection, transaction, claim.Campaign, claim.Timeline) != claim.Witness)
                    return PressureDayDeferred("scope_changed", claim.Day);
                if (!IsPressureDayFirst(connection, transaction, terms, claim.Day))
                    return PressureDayDeferred("processing", claim.Day);
                var locked = DiplomacyStore.LockPoliticalPressureDailyProducer(connection, transaction, terms);
                if (locked == null || ReadString(locked, "prepared_json", "") != claim.PreparedJson)
                    return PressureDayDeferred("processing", claim.Day);

                string originKingdom = ReadString(plan, "originKingdom", "");
                if (createsIncident)
                {
                    WritePressureIncident(connection, transaction, claim, plan, incident);
                    originKingdom = ReadString(ReadDictionary(plan, "origin"), "kingdomId", "");
                }
                WritePressureDailyCompletion(connection, transaction, claim, status, ReadBool(plan, "passed", false),
                    originKingdom, createsIncident ? incident : "");

                result = TestDict("ok", true, "status", status);
                if (createsIncident)
                {
                    result["incidentId"] = incident;
                    result["polarity"] = ReadString(plan, "polarity", "");
                    result["severity"] = ReadString(plan, "severity", "");
                }
                else if (status == "roll_failed" || status == "no_eligible_kingdoms")
                {
                    result["roll"] = claim.Roll;
                    result["chance"] = PoliticalPressureIncidentChance;
                }
                string json = Json.Serialize(result);
                terms["result"] = json;
                terms["resultSha"] = PressureDailyHash(json);
                if (!DiplomacyStore.CompletePoliticalPressureDailyProducer(connection, transaction, terms))
                    throw new InvalidOperationException("Political pressure completion lost its exact owner.");
                PersistenceService.MarkSaveSyncActiveStateDirty(claim.Campaign);
                transaction.Commit();
            }
            ObservePressurePublicationForTests?.Invoke("committed");
            if (createsIncident) RelationshipsService.InvalidateRelationshipPairStateCache(claim.Campaign);
            WorldSimulationService.SignalWorldTestRollupWorker();
            WorldSimulationService.InvalidateWorldTestOverviewCache(claim.Campaign, claim.Timeline);
            return result;
        }

        private static void WritePressureIncident(ReignDbConnection connection, ReignDbTransaction transaction,
            PressureDayClaim claim, Dictionary<string, object> plan, string incident)
        {
            string campaign = claim.Campaign, timeline = claim.Timeline;
            double day = claim.WorldDay;
            var origin = ReadDictionary(plan, "origin");
            var target = ReadDictionary(plan, "target");
            var originClans = ReadDictionaryList(plan, "originClans");
            var targetClans = ReadDictionaryList(plan, "targetClans");
            var settlements = ReadDictionaryList(plan, "settlements");
            string originId = ReadString(origin, "kingdomId", ""), targetId = ReadString(target, "kingdomId", "");
            if (string.IsNullOrEmpty(originId) || string.IsNullOrEmpty(targetId)
                || originId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The prepared political-pressure realms are incomplete.");
            string originStance = ReadString(plan, "originStance", ""), targetStance = ReadString(plan, "targetStance", "");
            string polarity = ReadString(plan, "polarity", ""), channel = ReadString(plan, "channel", "");
            string severity = ReadString(plan, "severity", ""), headline = ReadString(plan, "headline", "");
            string narrative = ReadString(plan, "narrative", ""), selector = ReadString(plan, "selector", "");
            string narration = ReadString(plan, "narration", "");
            int rank = ReadInt(plan, "severityRank", 0), amount = ReadInt(plan, "pressureAmount", 0);
            int penalty = ReadInt(plan, "relationshipPenalty", 0);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            PoliticalPressureMovement Direction(Dictionary<string, object> actor, Dictionary<string, object> other, string stance)
            {
                int delta = stance == "support_lords" ? (polarity == "hostile" ? amount : -amount)
                    : stance == "preserve_relations" && ReadBool(other, "isPlayerKingdom", false) ? -amount : 0;
                if (delta == 0)
                {
                    var pair = TestDict("campaign", campaign, "timeline", timeline,
                        "actor", ReadString(actor, "kingdomId", ""), "target", ReadString(other, "kingdomId", ""));
                    int value = ReadInt(DiplomacyStore.SelectPoliticalPressureState(connection, pair, transaction)
                        .SingleOrDefault(), "pressure_value", 0);
                    return new PoliticalPressureMovement(value, value, false);
                }
                var change = ApplyPoliticalPressureDeltaInTransaction(connection, transaction, campaign, timeline,
                    day, actor, other, delta, channel, incident);
                if (!change.Applied)
                    throw new InvalidOperationException("A political pressure direction already exists outside this prepared publication.");
                ObservePressurePublicationForTests?.Invoke("direction");
                return change;
            }

            // Lock the same pair order even if the opposing side originated the incident.
            bool originFirst = string.CompareOrdinal(originId, targetId) < 0;
            var first = originFirst ? Direction(origin, target, originStance) : Direction(target, origin, targetStance);
            var second = originFirst ? Direction(target, origin, targetStance) : Direction(origin, target, originStance);
            var a = originFirst ? first : second;
            var b = originFirst ? second : first;
            var originLords = ReadStringList(plan, "originLords");
            var targetLords = ReadStringList(plan, "targetLords");
            var rows = TestDict("id", incident, "campaign", campaign, "timeline", timeline, "day", claim.Day, "worldDay", day,
                "archetype", ReadString(plan, "archetype", ""), "polarity", polarity, "channel", channel, "severity", severity,
                "severityRank", rank, "pressureAmount", amount, "relationshipPenalty", penalty,
                "originKingdom", originId, "targetKingdom", targetId, "originKingdomName", ReadString(origin, "name", ""),
                "targetKingdomName", ReadString(target, "name", ""), "originRuler", ReadString(origin, "leaderHeroId", ""),
                "targetRuler", ReadString(target, "leaderHeroId", ""), "originRulerName", ReadString(origin, "leaderName", ""),
                "targetRulerName", ReadString(target, "leaderName", ""), "originWars", ReadInt(plan, "originWars", 0),
                "targetWars", ReadStringList(target, "enemies").Count, "hostileChance", ReadInt(plan, "hostileChance", 0),
                "polarityRoll", ReadInt(plan, "polarityRoll", 0),
                "originClans", Json.Serialize(originClans.Select(x => ReadString(x, "clanId", "")).ToList()),
                "originLords", Json.Serialize(originLords),
                "targetClans", Json.Serialize(targetClans.Select(x => ReadString(x, "clanId", "")).ToList()),
                "targetLords", Json.Serialize(targetLords), "routes", Json.Serialize(ReadDictionary(plan, "routes")),
                "rolls", Json.Serialize(ReadDictionaryList(plan, "rolls")), "originStance", originStance, "targetStance", targetStance,
                "originBefore", a.Before, "originAfter", a.After, "targetBefore", b.Before, "targetAfter", b.After,
                "headline", headline, "narrative", narrative, "correlation", incident,
                "payload", Json.Serialize(TestDict("origin", origin, "target", target,
                    "originClans", originClans, "targetClans", targetClans)), "ts", ts);
            DiplomacyStore.InsertPoliticalPressureIncidents(connection, rows, transaction);
            ApplyPoliticalPressureForeignRulerChoice(connection, transaction, campaign, timeline, day,
                incident, "origin", origin, target, originStance, penalty, incident);
            ApplyPoliticalPressureForeignRulerChoice(connection, transaction, campaign, timeline, day,
                incident, "target", target, origin, targetStance, penalty, incident);
            if (originStance == "preserve_relations")
                ApplyPoliticalPressureDomesticRefusal(connection, transaction, campaign, timeline, day,
                    incident, "origin", origin, originClans, settlements, penalty, incident);
            if (targetStance == "preserve_relations")
                ApplyPoliticalPressureDomesticRefusal(connection, transaction, campaign, timeline, day,
                    incident, "target", target, targetClans, settlements, penalty, incident);
#if !REIGN_EXCLUDE_COURT
            if (originStance == "awaiting_player" || targetStance == "awaiting_player")
            {
                string court = ReadString(plan, "court", "");
                if (court.Length == 0) throw new InvalidOperationException("The prepared incident has no Court queue terms.");
                CourtService.StoreExactInternationalPoliticalIncident(connection, transaction, campaign, timeline, incident, rank, day, court);
                ObservePressurePublicationForTests?.Invoke("court");
            }
#endif
            DiplomacyStore.UpdatePoliticalPressureIncidents(connection, TestDict("narrative", narrative, "selector", selector,
                "narration", narration, "ts", ts, "id", incident), transaction);
            RecordPoliticalPressureActivity(campaign, timeline, day, incident, origin, target,
                "incident_completed", polarity, channel, severity, "completed", a.Before, a.After,
                ReadInt(plan, "polarityRoll", 0), ReadInt(plan, "hostileChance", 0), "", incident, originStance + " / " + targetStance,
                TestDict("originStance", originStance, "targetStance", targetStance, "pressureAmount", amount,
                    "selectorStatus", selector, "narrationStatus", narration), connection, transaction);
        }

        private static void WritePressureDailyCompletion(ReignDbConnection connection, ReignDbTransaction transaction,
            PressureDayClaim claim, string status, bool passed, string origin, string incident)
        {
            DiplomacyStore.UpsertPoliticalPressureDailyRolls(connection, TestDict("campaign", claim.Campaign,
                "timeline", claim.Timeline, "day", claim.Day, "worldDay", claim.WorldDay, "chance", PoliticalPressureIncidentChance,
                "roll", claim.Roll, "passed", passed ? 1 : 0, "status", status, "origin", origin, "incident", incident,
                "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), transaction);
            WorldSimulationService.WriteWorldTestCounterSchemaReady(connection, claim.Campaign, claim.Timeline,
                claim.Day, "political_pressures", "daily:" + claim.Day.ToString(CultureInfo.InvariantCulture),
                TestDict("dailyRolls", 1, passed ? "dailyRollsPassed" : "dailyRollsFailed", 1, "status:" + status, 1), transaction);
        }

        private static Dictionary<string, object> PressurePlan(PressureDayClaim claim, string status)
            => TestDict("schema", PressureDailyPlanFormat, "campaign", claim.Campaign, "timeline", claim.Timeline,
                "day", claim.Day, "worldDay", claim.WorldDay, "roll", claim.Roll, "status", status);

        private static bool IsOwnedPressurePlan(PressureDayClaim claim, Dictionary<string, object> plan)
            => ReadString(plan, "schema", "") == PressureDailyPlanFormat
                && ReadString(plan, "campaign", "") == claim.Campaign && ReadString(plan, "timeline", "") == claim.Timeline
                && ReadInt(plan, "day", -1) == claim.Day && ReadDouble(plan, "worldDay", -1) == claim.WorldDay
                && ReadInt(plan, "roll", -1) == claim.Roll;
    }
}
