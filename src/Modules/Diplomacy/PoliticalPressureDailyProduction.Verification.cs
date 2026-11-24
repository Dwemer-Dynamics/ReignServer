#if !REIGN_EXCLUDE_VERIFICATION
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DiplomacyService
    {
        private sealed class PressureFixtureInterrupted : Exception { }

        private static void CheckResumablePressureProduction(string campaign, Action<string, bool, string> add)
        {
            const string timeline = "resumable_pressure", originalGeneration = "fixture_pressure_original";
            using var setup = ReignDatabase.OpenCampaignConnection(campaign);
            EnsurePoliticalPressureSchema(setup);
            EnsureKingdomLeaderDiplomacySchema(setup);
            WorldSimulationService.EnsureWorldTestTelemetrySchema(setup);
#if !REIGN_EXCLUDE_COURT
            CourtService.EnsureInternationalCourtLifeSchema(setup);
#endif
            string now = DateTime.UtcNow.ToString("O");
            WorldSimulationStore.UpsertWorldHistoryTimelines(setup, TestDict("campaign", campaign,
                "timeline", timeline, "complete", 0, "parent", "", "fork", "", "now", now));
            WorldSimulationStore.UpdateWorldHistoryTimelines(setup, TestDict("campaign", campaign, "timeline", timeline));
            ReignDatabase.ExecuteSql(setup, "UPDATE memory_precision_state SET restore_generation=$generation WHERE state_id='current';",
                TestDict("generation", originalGeneration));

            var original = TestDict("sharedActivityGeneration", originalGeneration,
                "kingdoms", Array.Empty<object>(), "clans", Array.Empty<object>(), "settlements",
                new[] { TestDict("settlementId", "original_holdings") });
            var successor = TestDict("sharedActivityGeneration", originalGeneration,
                "kingdoms", Array.Empty<object>(), "clans", Array.Empty<object>(), "settlements",
                new[] { TestDict("settlementId", "new_holdings") });

            Dictionary<string, object> Claim(int day, double fraction, Dictionary<string, object> input, out PressureDayClaim claim)
                => ClaimPressureDay(campaign, timeline, day, day + fraction, 13, input, out claim);

            void Expire(PressureDayClaim claim)
            {
                var changed = ReignDatabase.QuerySql(setup, @"
UPDATE political_pressure_daily_producers SET claim_expires_utc=clock_timestamp()-interval '1 second'
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day AND claim_id=$claim
RETURNING day_key;", claim.Terms());
                if (changed.Count != 1) throw new InvalidOperationException("Fixture no longer owns its exact day.");
            }

            var initial = Claim(20, .25, original, out var first);
            var concurrent = Claim(20, .75, successor, out _);
            if (first == null) throw new InvalidOperationException("Initial fixture did not own its day.");
            Expire(first);
            var successorStatus = Claim(20, .75, successor, out var resumed);
            if (resumed == null) throw new InvalidOperationException("Fixture retry did not claim its original day.");
            var terminalTerms = PressurePlan(resumed, "roll_failed");
            bool rejectsOldProvider = SealPressureDay(first, PressurePlan(first, "roll_failed")) == null;
            add("daily_reclaim_keeps_original_day_and_snapshot", initial == null && successorStatus == null
                && ReadString(concurrent, "status", "") == "processing" && !ReadBool(concurrent, "ok", true)
                && resumed.WorldDay == 20.25 && resumed.Token != first.Token && resumed.InputSha == first.InputSha
                && resumed.InputJson.Contains("original_holdings") && !resumed.InputJson.Contains("new_holdings") && rejectsOldProvider,
                "The day keeps its first accepted terms and fences the late provider from the expired owner.");
            resumed = SealPressureDay(resumed, terminalTerms);
            if (resumed == null) throw new InvalidOperationException("Fixture terms did not seal.");
            Expire(resumed);
            Claim(20, .9, successor, out var sealedRetry);
            if (sealedRetry == null) throw new InvalidOperationException("Fixture prepared day did not re-open.");
            var lost = false;
            try
            {
                ObservePressurePublicationForTests = stage =>
                {
                    if (stage == "committed") throw new PressureFixtureInterrupted();
                };
                PublishPressureDay(sealedRetry, TryParseJsonObject(sealedRetry.PreparedJson));
            }
            catch (PressureFixtureInterrupted) { lost = true; }
            finally { ObservePressurePublicationForTests = null; }
            var replay = Claim(20, .99, successor, out var unexpected);
            add("daily_commit_replays_exact_terminal_response", lost && unexpected == null
                && sealedRetry.PreparedSha == resumed.PreparedSha && ReadBool(replay, "ok", false)
                && ReadString(replay, "status", "") == "roll_failed" && ReadInt(replay, "roll", -1) == 13,
                "A lost response replays the stored terminal result without re-preparing or publishing again.");

#if !REIGN_EXCLUDE_COURT
            Claim(22, .5, original, out var fixture);
            if (fixture == null) throw new InvalidOperationException("Atomic fixture did not own its original day.");
            string incident = "political-pressure:" + timeline + ":22";
            var origin = TestDict("kingdomId", "pressure_fixture_origin", "leaderHeroId", "pressure_fixture_ruler",
                "leaderName", "Amira", "name", "Original Realm");
            var target = TestDict("kingdomId", "pressure_fixture_player", "leaderHeroId", "pressure_fixture_player_ruler",
                "leaderName", "Bora", "isPlayerKingdom", true, "name", "Receiving Realm");
            var clan = TestDict("clanId", "pressure_fixture_clan", "leaderHeroId", "pressure_fixture_petitioner");
            var holding = TestDict("settlementId", "original_holdings", "ownerClanId", "pressure_fixture_clan", "isTown", true);
            var preparedIncident = PressurePlan(fixture, "incident_created");
            foreach (var term in TestDict("passed", true, "incident", incident, "origin", origin, "target", target,
                "originClans", new List<object> { clan }, "targetClans", new List<object>(),
                "originLords", new List<object> { "pressure_fixture_petitioner" }, "targetLords", new List<object>(),
                "settlements", new List<object> { holding }, "archetype", "fixture_pressure",
                "headline", "Fixture headline", "narrative", "An original fixture petition.",
                "polarity", "hostile", "channel", "coercion", "severity", "minor", "severityRank", 1,
                "pressureAmount", 6, "relationshipPenalty", -10, "originStance", "preserve_relations",
                "targetStance", "awaiting_player", "routes", TestDict("fixture", true), "rolls", new List<object>(),
                "selector", "unavailable_fallback", "narration", "unavailable_fallback"))
                preparedIncident[term.Key] = term.Value;
            preparedIncident["court"] = CourtService.BuildInternationalPoliticalIncidentPayload(incident, fixture.WorldDay,
                origin, target, new List<string> { "pressure_fixture_petitioner" }, new List<string>(), 1,
                "Fixture headline", "Original premise", "coercion", "preserve_relations", "awaiting_player", "hostile");
            fixture = SealPressureDay(fixture, preparedIncident);
            if (fixture == null) throw new InvalidOperationException("Cannot seal atomic fixture.");
            bool HasFootprint() => DiplomacyStore.HasLegacyPoliticalPressureFootprint(setup, null,
                TestDict("campaign", campaign, "timeline", timeline, "day", 22, "incident", incident));
            bool directionInvisible = false, courtInvisible = false;
            foreach (string expected in new[] { "direction", "court" })
            {
                try
                {
                    ObservePressurePublicationForTests = stage =>
                    {
                        if (stage != expected) return;
                        bool absentFromIndependentConnection = !HasFootprint();
                        if (stage == "direction") directionInvisible = absentFromIndependentConnection;
                        else courtInvisible = absentFromIndependentConnection;
                        throw new PressureFixtureInterrupted();
                    };
                    PublishPressureDay(fixture, TryParseJsonObject(fixture.PreparedJson));
                }
                catch (PressureFixtureInterrupted) { }
                finally { ObservePressurePublicationForTests = null; }
                if (HasFootprint()) throw new InvalidOperationException("An uncommitted fixture escaped publication.");
            }
            Expire(fixture);
            Claim(22, .99, successor, out var retried);
            if (retried == null) throw new InvalidOperationException("Atomic fixture retry could not claim.");
            var committed = PublishPressureDay(retried, TryParseJsonObject(retried.PreparedJson));
            var effect = ReignDatabase.QuerySql(setup, @"
SELECT target_id FROM political_pressure_native_effects WHERE incident_id=$incident;", TestDict("incident", incident))
                .SingleOrDefault();
            var courtRows = ReignDatabase.QuerySql(setup, @"
SELECT matter_id FROM international_court_queue WHERE matter_id=$incident;", TestDict("incident", incident));
            add("daily_partial_direction_and_court_roll_back_together", directionInvisible && courtInvisible
                && ReadString(committed, "incidentId", "") == incident && HasFootprint()
                && retried.PreparedSha == fixture.PreparedSha && retried.WorldDay == 22.5
                && ReadString(effect, "target_id", "") == "original_holdings" && courtRows.Count == 1,
                "Two separate rollback boundaries hide pressure and Court from an independent connection, and replay keeps the original holdings.");
#endif

            Claim(21, .3, original, out var older);
            if (older == null) throw new InvalidOperationException("Restore fixture setup did not own a day.");
            ReignDatabase.ExecuteSql(setup, "UPDATE memory_precision_state SET restore_generation=$generation WHERE state_id='current';",
                TestDict("generation", "fixture_pressure_restored"));
            var oldRequest = Claim(21, .8, original, out _);
            var newRequest = Claim(21, .8, TestDict("sharedActivityGeneration", "fixture_pressure_restored"), out _);
            add("daily_restore_rejects_exact_older_scope", SealPressureDay(older, PressurePlan(older, "roll_failed")) == null
                && ReadString(oldRequest, "status", "") == "scope_changed"
                && ReadString(newRequest, "status", "") == "recovery_required",
                "A prepared day from a previous restore generation cannot borrow current timeline authority.");
            const string restoredGeneration = "fixture_pressure_restored";
            var restored = TestDict("sharedActivityGeneration", restoredGeneration,
                "kingdoms", Array.Empty<object>(), "clans", Array.Empty<object>(), "settlements", Array.Empty<object>());
            var invalid = TestDict("sharedActivityGeneration", restoredGeneration,
                "kingdoms", new object[] { "not an object" }, "clans", Array.Empty<object>(), "settlements", Array.Empty<object>());
            var malformed = Claim(29, .1, invalid, out var invalidOwner);
            var missingDay = EvaluatePoliticalPressure(TestDict());
            bool noMalformedProducer = DiplomacyStore.FindPoliticalPressureDailyProducer(setup, null,
                TestDict("campaign", campaign, "timeline", timeline, "day", 29)) == null;
            add("daily_malformed_snapshot_cannot_consume_day",
                invalidOwner == null && noMalformedProducer && ReadString(malformed, "status", "") == "invalid_snapshot"
                && ReadString(missingDay, "status", "") == "invalid_day",
                "Malformed or missing snapshots are rejected before they can reserve a day.");

            var laterResult = Claim(30, .4, restored, out var later);
            if (later == null) throw new InvalidOperationException("The prior restore hold did not release future days.");
            var legacyKey = TestDict("campaign", campaign, "timeline", timeline, "day", 29,
                "worldDay", 29.23d, "chance", PoliticalPressureIncidentChance, "roll", 99, "passed", 0,
                "status", "processing", "origin", "", "incident", "", "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600);
            DiplomacyStore.UpsertPoliticalPressureDailyRolls(setup, legacyKey);
            var pending = DiplomacyStore.FindUnfinishedPoliticalPressureDays(setup, null, later.Terms());
            var preparedLater = SealPressureDay(later, PressurePlan(later, "roll_failed"));
            var blockedLater = PublishPressureDay(preparedLater, TryParseJsonObject(preparedLater.PreparedJson));
            var olderResult = Claim(29, .8, restored, out var legacyOwner);
            if (legacyOwner == null) throw new InvalidOperationException("Oldest legacy claim could not resume.");
            var duplicatePending = DiplomacyStore.FindUnfinishedPoliticalPressureDays(setup, null, later.Terms());
            var sealedLegacy = SealPressureDay(legacyOwner, PressurePlan(legacyOwner, "roll_failed"));
            var legacyResult = PublishPressureDay(sealedLegacy, TryParseJsonObject(sealedLegacy.PreparedJson));
            var resumedLater = PublishPressureDay(preparedLater, TryParseJsonObject(preparedLater.PreparedJson));
            add("daily_multiple_unfinished_days_are_ordered_by_real_postgresql_rows",
                laterResult == null && olderResult == null && pending.SequenceEqual(new[] { 29, 30 })
                && duplicatePending.SequenceEqual(new[] { 29, 30 }) && ReadString(blockedLater, "status", "") == "processing"
                && legacyOwner.WorldDay == 29.23d && legacyOwner.Roll == 99
                && ReadBool(legacyResult, "ok", false) && ReadBool(resumedLater, "ok", false),
                "Historical holds preserve their rows while two real unfinished dates drain oldest first, once per date.");
        }
    }
}
#endif