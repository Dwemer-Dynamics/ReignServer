using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        internal static List<Dictionary<string, object>> RunRelationshipThroughputSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["caseId"] = id, ["name"] = id,
                ["suite"] = "relationship_throughput", ["summary"] = summary, ["durationMs"] = 0
            });
            string retentionRoot = System.IO.Path.Combine(Program.TestsDir, "v");
            var retained = Program.VerificationReferencedSandboxNames(retentionRoot, new[]
            {
                new Dictionary<string, object> { ["runId"] = "verify-123-abcdefgh", ["sandboxPath"] = System.IO.Path.Combine(retentionRoot, "r-abcdefgh") },
                new Dictionary<string, object> { ["sandboxPath"] = System.IO.Path.Combine(retentionRoot, "..", "outside") },
                new Dictionary<string, object> { ["sandboxPath"] = retentionRoot },
                new Dictionary<string, object> { ["sandboxPath"] = "" }
            });
            add("verification_retains_short_sandbox_references", retained.SetEquals(new[] { "r-abcdefgh" }),
                "Retained run reports protect their short sandbox names; root and out-of-root references cannot select cleanup targets.");
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var availability = new DatabaseAvailability(() => now);
            long ticket = availability.Enter();
            availability.Complete(ticket, true);
            bool refused = false;
            try { availability.Enter(); } catch (InvalidOperationException) { refused = true; }
            availability.Complete(ticket, false);
            bool staleSuccessIgnored = availability.BackingOff;
            now = now.AddSeconds(1);
            ticket = availability.Enter();
            bool secondProbeRefused = false;
            try { availability.Enter(); } catch (InvalidOperationException) { secondProbeRefused = true; }
            availability.Complete(ticket, false);
            add("database_outage_shared_probe_and_recovery", refused && staleSuccessIgnored && secondProbeRefused && !availability.BackingOff,
                "All schemas share backoff, one recovery probe, and protection against an earlier connection closing a newer failure.");

            var documents = new RelationshipHeroDocumentCache(2, 1000);
            string document = "{\"heroStringId\":\"hero\",\"traits\":{\"Mercy\":1},\"childrenIds\":[\"child\"]}";
            var firstProfile = documents.Read(document, out bool firstHit);
            ReadDictionary(firstProfile, "traits")["Mercy"] = 99;
            ((System.Collections.IList)firstProfile["childrenIds"])[0] = "changed";
            var secondProfile = documents.Read(document, out bool secondHit);
            var changedProfile = documents.Read(document.Replace("Mercy\":1", "Mercy\":2"), out bool changedHit);
            var restoredProfile = documents.Read(document, out bool restoredHit);
            documents.Read("{\"i\":\"other\",\"a\":25}", out _);
            documents.Read(document.Replace("Mercy\":1", "Mercy\":2"), out bool evictedHit);
            var ingestDocument = new Dictionary<string, object> { ["heroStringId"] = "ingested", ["traits"] = new Dictionary<string, object> { ["Mercy"] = 1 } };
            string ingestJson = Json.Serialize(ingestDocument);
            documents.Remember(ingestJson, ingestDocument);
            ReadDictionary(ingestDocument, "traits")["Mercy"] = 77;
            var remembered = documents.Read(ingestJson, out bool rememberedHit);
            add("hero_document_cache_preserves_changes_restore_and_ownership",
                !firstHit && secondHit && !changedHit && restoredHit && !evictedHit
                && rememberedHit && ReadInt(ReadDictionary(remembered, "traits"), "Mercy", 0) == 1
                && ReadInt(ReadDictionary(secondProfile, "traits"), "Mercy", 0) == 1
                && ReadStringList(secondProfile, "childrenIds").Single() == "child"
                && ReadInt(ReadDictionary(changedProfile, "traits"), "Mercy", 0) == 2
                && ReadInt(ReadDictionary(restoredProfile, "traits"), "Mercy", 0) == 1,
                "Exact-document caching sees changed and restored database content immediately, returns independent nested values, and evicts least-recently-used entries.");

            string campaign = "__throughput_" + Guid.NewGuid().ToString("N");
            try
            {
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    CharactersService.EnsureIdentitySchema(connection);
                    EnsureMbtiRelationshipSchema(connection);
                    var heroes = Enumerable.Range(0, 40).Select(i => new Dictionary<string, object>
                    {
                        ["heroStringId"] = "hero_" + i, ["name"] = i == 1 ? "" : "Élan '" + i,
                        ["clanId"] = "clan_" + i % 5, ["kingdomId"] = "realm_" + i % 3,
                        ["isLord"] = i % 2 == 0, ["isRuler"] = i % 7 == 0,
                        ["isAlive"] = true, ["isAdult"] = true,
                        ["fatherId"] = i < 20 ? "hero_" + (i + 20) : "",
                        ["childrenIds"] = new List<object>()
                    }).ToList();
                    CharactersService.UpsertIdentityRosterHeroesBatch(connection, heroes, 123);
                    var previous = RelationshipsSelfTestStore.SelectIdentityRoster(connection);
                    var next = heroes.Select(x => new Dictionary<string, object>(x)).ToList();
                    foreach (var hero in next.Take(7)) { hero["clanId"] = "new_clan"; hero["isRuler"] = false; }
                    next[15]["fatherId"] = "";
                    next.RemoveAt(39); // The serial rule does not invent knowledge for a removed NPC.
                    RelationshipsSelfTestStore.InsertAcquaintances(connection);
                    Func<List<Dictionary<string, object>>, string> canonical = rows => Json.Serialize(rows
                        .OrderBy(x => ReadString(x, "observer_id", ""), StringComparer.Ordinal)
                        .ThenBy(x => ReadString(x, "subject_id", ""), StringComparer.Ordinal)
                        .Select(x => x.Where(v => v.Key != "rowid").OrderBy(v => v.Key, StringComparer.Ordinal)
                            .ToDictionary(v => v.Key, v => v.Value)).ToList());
                    string before = canonical(RelationshipsSelfTestStore.SelectAcquaintances(connection));
                    RelationshipsSelfTestStore.Begin2(connection);
                    CharactersService.PreserveDepartingImplicitKnowledge(connection, next, 5, 456);
                    string serial = canonical(RelationshipsSelfTestStore.SelectAcquaintances(connection));
                    RelationshipsSelfTestStore.Rollback(connection);
                    var batch = CharactersService.PrepareHistoricalIdentityBatch(previous, next);
                    RelationshipsSelfTestStore.Begin2(connection);
                    CharactersService.WriteHistoricalIdentityBatch(connection, batch, 5, 456);
                    string actual = canonical(RelationshipsSelfTestStore.SelectAcquaintances(connection));
                    RelationshipsSelfTestStore.Rollback(connection);
                    add("historical_identity_batch_matches_serial_and_rollback", serial == actual
                        && canonical(RelationshipsSelfTestStore.SelectAcquaintances(connection)) == before && batch.Count > 0,
                        "All persisted knowledge columns match the original transfer/family rules, existing verification and first-met dates; rollback restores the original rows.");

                    RelationshipsSelfTestStore.InsertRelationshipPairLifecycle(connection);
                    Func<string[], Dictionary<string, object>> group = ids => new Dictionary<string, object> { ["heroIds"] = ids.Cast<object>().ToList() };
                    var groups = new Dictionary<int, List<Dictionary<string, object>>>
                    {
                        [4] = new List<Dictionary<string, object>> { group(new[] { "hero_0", "hero_1" }), group(new[] { "hero_4", "hero_6", "hero_7" }) },
                        [5] = new List<Dictionary<string, object>> { group(new[] { "hero_5" }) }
                    };
                    var full = RelationshipsSelfTestStore.SelectRelationshipPairLifecycle(connection);
                    var selected = LoadCoPresentLifecycleRows(connection, 5, groups);
                    var oracle = full.Where(row => groups.Values.Any(dayGroups => dayGroups.Any(g =>
                        ReadStringList(g, "heroIds").Contains(ReadString(row, "hero_a_id", ""), StringComparer.OrdinalIgnoreCase)
                        && ReadStringList(g, "heroIds").Contains(ReadString(row, "hero_b_id", ""), StringComparer.OrdinalIgnoreCase))))
                        .Select(row => ReadString(row, "pair_key", "")).OrderBy(x => x).ToList();
                    add("lifecycle_workset_matches_copresence_and_keeps_exclusions",
                        Json.Serialize(oracle) == Json.Serialize(selected.Select(x => ReadString(x, "pair_key", "")).OrderBy(x => x).ToList())
                        && selected.Count == 1 && LoadHistoricalCourtshipExclusions(connection, 5).Contains("hero_2|hero_3"),
                        "Separate locations/days and already processed pairs are excluded while quiet co-present pairs and historical failed-flirt exclusions survive.");
                    groups[5].Add(group(new[] { "hero_2", "hero_3" }));
                    add("historical_lifecycle_reactivates_on_new_copresence",
                        LoadCoPresentLifecycleRows(connection, 5, groups).Count == 2,
                        "Reappearance immediately selects historical lifecycle state without resetting its failed attempt.");
                    var workset = LoadRelationshipLifecycleWorkset(connection, new[] { "hero_0|hero_1", "hero_2|hero_3", "new_pair" });
                    add("second_lifecycle_read_keeps_only_scheduled_pairs", workset.Count == 2
                        && workset.Any(row => ReadString(row, "pair_key", "") == "hero_2|hero_3" && ReadInt(row, "flirt_attempted", 0) == 1)
                        && WorldSimulationService.TableCount(connection, "relationship_pair_lifecycle") == 4,
                        "The transaction reloads complete state for matched and independent co-present pairs without reading or deleting unrelated historical rows.");
                    RelationshipsSelfTestStore.UpdateRelationshipPairLifecycle(connection, new Dictionary<string, object> { ["stage"] = "\t\r\n" });
                    add("historical_exclusion_preserves_dotnet_whitespace", LoadHistoricalCourtshipExclusions(connection, 5).Contains("hero_2|hero_3"),
                        "The compact selector preserves IsNullOrWhiteSpace semantics for legacy romance stages.");
                    RelationshipsSelfTestStore.InsertRelationshipPairChemistry2(connection);
                    var pulled = RelationshipNativeTargetPullApi(new Dictionary<string, object> { ["campaignId"] = campaign, ["timelineId"] = "main" });
                    var target = ReadDictionaryList(pulled, "targets").Single();
                    bool observationRequested = ReadInt(pulled, "targetCount", 0) == 1
                        && ReadInt(RelationshipsSelfTestStore.SelectRelationshipNativeTargets(connection).Single(), "requires_observation", 0) == 1;
                    var receipt = new Dictionary<string, object>
                    {
                        ["pairKey"] = "hero_0|hero_1", ["revision"] = ReadInt(target, "revision", 0),
                        ["status"] = "already_aligned", ["observedRelation"] = 42
                    };
                    RelationshipNativeTargetReceiptsApi(new Dictionary<string, object>
                    { ["campaignId"] = campaign, ["timelineId"] = "main", ["worldDay"] = 5d, ["receipts"] = new List<object> { receipt } });
                    add("orphan_native_flag_requires_confirmed_observation", observationRequested
                        && ReadInt(RelationshipsSelfTestStore.SelectRelationshipPairChemistry2(connection).Single(), "native_action_pending", 1) == 0
                        && WorldSimulationService.TableCount(connection, "relationship_native_targets") == 0,
                        "An orphan flag becomes observation work and is cleared only by a matching native receipt.");
                    RelationshipsSelfTestStore.UpdateRelationshipPairChemistry(connection);
                    RelationshipNativeTargetReceiptsApi(new Dictionary<string, object>
                    { ["campaignId"] = campaign, ["timelineId"] = "main", ["worldDay"] = 6d, ["receipts"] = new List<object> { receipt } });
                    add("late_receipt_cannot_clear_new_projection", CountUnrepresentedNativeFlags(connection, "") == 1,
                        "An older aligned receipt cannot erase new chemistry work that arrived after its target was retired.");

                    ReputationService.EnsureSocialReputationSchema(connection);
                    var beforePopularity = RelationshipsSelfTestStore.SelectIdentityRoster(connection);
                    RelationshipsSelfTestStore.UpdateIdentityRoster(connection);
                    add("identity_charm_does_not_recompute_popularity", CharactersService.SelectIdentityPopularitySubjects(connection, beforePopularity,
                        RelationshipsSelfTestStore.SelectIdentityRoster(connection)).Count == 0,
                        "Popularity dependency selection ignores attributes that the authoritative formula does not read.");
                    RelationshipsSelfTestStore.UpdateIdentityRoster2(connection);
                    var scope = CharactersService.SelectIdentityPopularitySubjects(connection, beforePopularity, RelationshipsSelfTestStore.SelectIdentityRoster(connection));
                    add("identity_transfer_invalidates_incoming_neighbors", scope.Contains("hero_0"),
                        "A transferred observer invalidates the subject on the other end of its chemistry pair.");

                    RelationshipsSelfTestStore.UpdateIdentityRoster3(connection);
                    RelationshipsSelfTestStore.InsertOrIgnoreRelationshipPairChemistry(connection);
                    ReputationService.RecomputeAllCourtPopularityReputations(connection, campaign, "main", 5, "scope_baseline");
                    var popularityBaseline = RelationshipsSelfTestStore.SelectIdentityRoster(connection);
                    Func<string> popularityState = () => Json.Serialize(new object[]
                    {
                        RelationshipsSelfTestStore.SelectCharacterReputations2(connection),
                        RelationshipsSelfTestStore.SelectCharacterPublicStanding(connection)
                    });
                    string fullPopularity = "", scopedPopularity = "";
                    for (int route = 0; route < 2; route++)
                    {
                        RelationshipsSelfTestStore.Begin2(connection);
                        try
                        {
                            RelationshipsSelfTestStore.UpdateIdentityRoster4(connection);
                            RelationshipsSelfTestStore.UpdateIdentityRoster5(connection);
                            RelationshipsSelfTestStore.UpdateIdentityRoster6(connection);
                            RelationshipsSelfTestStore.UpdateIdentityRoster7(connection);
                            if (route == 0) ReputationService.RecomputeAllCourtPopularityReputations(connection, campaign, "main", 6, "scope_compare");
                            else
                            {
                                var affected = CharactersService.SelectIdentityPopularitySubjects(connection, popularityBaseline, RelationshipsSelfTestStore.SelectIdentityRoster(connection));
                                var changed = ReputationService.RecomputeCourtPopularityReputationsBatch(connection, campaign, "main", affected, 6, "scope_compare");
                                ReputationService.ReconcileSocialRelationshipsForSubjects(connection, campaign, "main", changed, 6);
                            }
                            if (route == 0) fullPopularity = popularityState(); else scopedPopularity = popularityState();
                        }
                        finally { RelationshipsSelfTestStore.Rollback(connection); InvalidateRelationshipPairStateCache(campaign); }
                    }
                    add("identity_scoped_popularity_matches_full_recompute", fullPopularity == scopedPopularity,
                        "Player eligibility, kingdom transfer, death and notable status changes produce the same reputation tags, values, revisions and standing as the original full sweep.");

                    var profileRow = new Dictionary<string, object> { ["mbti_type"] = "ENFP", ["template_version"] = 3,
                        ["foundation_json"] = "{\"curiosity\":91}", ["percentages_json"] = "{\"sociability\":73}",
                        ["native_traits_json"] = "{\"Mercy\":1}", ["first_observed_day"] = 2 };
                    var compact = CompactNotableRelationshipProfile(profileRow);
                    ResolvePermanentRelationshipMbti(connection, campaign, 6, new Dictionary<string, object> { ["heroStringId"] = "hero_0" },
                        new Dictionary<string, Dictionary<string, object>> { ["hero_0"] = compact });
                    var assignment = RelationshipsSelfTestStore.SelectRelationshipPersonalities(connection).Single();
                    add("compact_personality_preserves_authoritative_assignment", ReadString(assignment, "traits_json", "").Contains("91")
                        && ReadString(assignment, "traits_json", "").Contains("73") && ReadInt(assignment, "assignment_day", -1) == 2,
                        "Lazy expansion keeps the complete original trait document and assignment day when a permanent personality is written.");

                    RelationshipsSelfTestStore.InsertRelationshipDailyInputs(connection, new Dictionary<string, object>
                    { ["campaign"] = campaign, ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                    RecoverInterruptedRelationshipDays(connection, campaign);
                    add("startup_recovers_recent_interrupted_day", ReadString(RelationshipsSelfTestStore.SelectRelationshipDailyInputs(connection).Single(), "status", "") == "pending",
                        "Startup does not wait five minutes to retry a day left processing by the previous worker; its cursor stays intact.");
                    // Readiness is now strictly read-only. The fixture must model
                    // normal writer initialization before observing its counters.
                    WorldSimulationService.EnsureWorldTestSchema(connection);
                    ReputationService.EnsureRumorSchema(connection);
                    EnsureClanConflictSchema(connection);
                    var readinessA = WorldSimulationService.BuildWorldTestReadiness(campaign, "main");
                    var readinessB = WorldSimulationService.BuildWorldTestReadiness(campaign, "main");
                    ResetRelationshipCampaignScheduling(campaign);
                    var restored = WorldSimulationService.BuildWorldTestReadiness(campaign, "main");
                    add("readiness_is_fresh_and_restore_changes_generation",
                        ReadString(readinessA, "observationId", "") != ReadString(readinessB, "observationId", "")
                        && ReadString(readinessA, "campaignGeneration", "") == ReadString(readinessB, "campaignGeneration", "")
                        && ReadString(restored, "campaignGeneration", "") != ReadString(readinessB, "campaignGeneration", "")
                        && ReadInt(ReadDictionary(ReadDictionary(restored, "relationships"), "nativeSync"), "pending", 0) >= 1,
                        "Every read is fresh, restore changes generation, and an orphan native flag continues to block readiness.");
                }
                MarkRelationshipCampaignReady(campaign);
                long observed = RelationshipReadyVersion(campaign);
                MarkRelationshipCampaignReady(campaign);
                ObserveRelationshipCampaignPending(campaign, 0, observed);
                lock (RelationshipReadyGate)
                    add("ready_registry_does_not_erase_concurrent_enqueue", RelationshipReadyCampaigns[campaign] > 0,
                        "A stale empty query cannot erase an enqueue committed while discovery was running.");

                var coverage = ClanConflictRollCoverage(new[] { new Dictionary<string, object>
                { ["day_key"] = 1, ["eligible_kingdom_ids_json"] = "[\"a\",\"b\"]" } },
                    new[] { "a", "c", "c" }.Select(k => new Dictionary<string, object> { ["day_key"] = 1, ["kingdom_id"] = k }));
                add("clan_roll_coverage_separates_missing_extra_duplicate", ReadInt(coverage, "missing", -1) == 1
                    && ReadInt(coverage, "unexpected", -1) == 1 && ReadInt(coverage, "duplicates", -1) == 1,
                    "Excess rows cannot cancel out a missing eligible kingdom/day or produce a zero-missing warning.");
            }
            finally
            {
                lock (RelationshipReadyGate) { RelationshipReadyCampaigns.Remove(campaign); RelationshipReadyVersions.Remove(campaign); }
                ReignPostgreSqlStorage.ClearAllPools();
                try { ReignPostgreSqlStorage.DropCampaign(campaign); } catch { }
                PersistenceService.TryDeleteDirectory(Program.CampaignDirectory(campaign));
            }
            RunRelationshipNativeObservationWriterTests(add);
            RunRelationshipReplaySemanticTests(add);
            RunRelationshipHeroObservationTests(add);
            return results;
        }

        private static void RunRelationshipHeroObservationTests(Action<string, bool, string> add)
        {
            string campaign = "__throughput_history_" + Guid.NewGuid().ToString("N");
            bool previousOriginalPath = RelationshipThroughputOriginalReadPath;
            var trajectories = new List<string>();
            var focuses = new List<int>();
            try
            {
                // Same campaign/seed: normal arrivals, legacy burst, corrected burst.
                for (int route = 0; route < 3; route++)
                {
                    RelationshipThroughputOriginalReadPath = route == 1;
                    var heroes = new[] { "history_a", "history_b" }.Select((id, index) => new Dictionary<string, object>
                    {
                        ["heroStringId"] = id, ["age"] = 25, ["isAlive"] = true, ["isAdult"] = true,
                        ["isLord"] = true, ["isFemale"] = index == 1, ["clanId"] = "clan_" + id,
                        ["nativeCanMarry"] = true, ["nativeMarriageClanSuitable"] = true
                    }).ToList();
                    Func<int, List<Dictionary<string, object>>, bool, Dictionary<string, object>> input = (day, changes, present) =>
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaign, ["timelineId"] = "main", ["worldDay"] = day,
                            ["heroes"] = changes, ["continuousWorker"] = true,
                            ["presenceGroups"] = present ? new List<object> { new Dictionary<string, object>
                            { ["kind"] = "party", ["id"] = "history_party", ["heroIds"] = new[] { "history_a", "history_b" } } } : new List<object>()
                        };
                    using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    {
                        EnsureMbtiRelationshipSchema(connection);
                        CharactersService.EnsureIdentitySchema(connection);
                        CharactersService.UpsertIdentityRosterHeroesBatch(connection, heroes, 1);
                    }
                    PersistAndHydrateRelationshipDailyInput(input(100, heroes, false));
                    MarkRelationshipDailyInputProcessed(campaign, "main", 100);
                    var pending = PersistAndHydrateRelationshipDailyInput(input(101, new List<Dictionary<string, object>>(), true));
                    var futureHero = new Dictionary<string, object>(heroes[1])
                    { ["spouseId"] = "future_spouse", ["isPregnant"] = true, ["nativeCanMarry"] = false };
                    if (route > 0) PersistAndHydrateRelationshipDailyInput(input(102, new List<Dictionary<string, object>> { futureHero }, false));
                    var result = MbtiRelationshipSnapshotApi(pending);
                    focuses.Add(ReadInt(result, "courtshipFocusPairDays", -1));
                    using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                        trajectories.Add(RelationshipReplayRowsHash(RelationshipsSelfTestStore.SelectRelationshipPairChemistry3(connection),
                            new Dictionary<string, string>()));
                    PersistenceService.ClearSaveSyncCaches(campaign);
                    ReignPostgreSqlStorage.DropCampaign(campaign);
                    PersistenceService.TryDeleteDirectory(Program.CampaignDirectory(campaign));
                }
                RelationshipThroughputOriginalReadPath = false;
                add("queued_hero_changes_preserve_historical_day",
                    focuses[0] == 1 && focuses[1] == 0 && focuses[2] == 1 && trajectories[0] == trajectories[2],
                    "Courtship focus counts (normal, legacy burst, corrected burst): " + string.Join(",", focuses)
                    + "; corrected burst retains the normal day's complete chemistry rows. Later marriage/pregnancy cannot rewrite an earlier day.");

                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    var versions = Enumerable.Range(1, 20).Select(day => new Dictionary<string, object>
                    { ["hero"] = "history_a", ["profile"] = "{\"heroStringId\":\"history_a\",\"age\":" + (20 + day) + "}",
                        ["hash"] = day.ToString(), ["day"] = day, ["ts"] = day }).ToList();
                    foreach (var row in versions) WriteRelationshipHeroObservations(connection, "main", new List<Dictionary<string, object>> { row });
                    var alternate = new Dictionary<string, object>(versions[10])
                    { ["profile"] = "{\"heroStringId\":\"history_a\",\"age\":91}" };
                    WriteRelationshipHeroObservations(connection, "branch", new List<Dictionary<string, object>> { alternate });
                    PruneRelationshipHeroObservations(connection, "main", 18);
                    var retained = RelationshipsSelfTestStore.SelectRelationshipHeroObservations(connection);
                    add("hero_history_pruning_retains_anchor_and_future",
                        retained.Count == 10 && ReadInt(retained[0], "observed_day", 0) == 11
                        && ReadInt(retained.Last(), "observed_day", 0) == 20
                        && ReadString(LoadRelationshipHeroObservations(connection, "main", 11, new[] { "history_a" }).Single(), "profile_json", "").Contains("31")
                        && ReadString(LoadRelationshipHeroObservations(connection, "branch", 11, new[] { "history_a" }).Single(), "profile_json", "").Contains("91"),
                        "Cleanup retains the latest predecessor and all future queued facts, and each timeline resolves its own version.");
                    bool refused = false;
                    try { LoadRelationshipHeroObservations(connection, "main", 5, new[] { "history_a" }); }
                    catch (InvalidOperationException) { refused = true; }
                    add("missing_historical_hero_data_fails_explicitly", refused,
                        "An old queue without reconstructable history cannot silently consume a future NPC profile.");
                    var unchangedFuture = new Dictionary<string, object>(versions[19]) { ["day"] = 40, ["ts"] = 40 };
                    WriteRelationshipHeroObservations(connection, "main", new List<Dictionary<string, object>> { unchangedFuture });
                    var olderChange = new Dictionary<string, object>(versions[18]) { ["day"] = 39, ["ts"] = 39 };
                    WriteRelationshipHeroObservations(connection, "main", new List<Dictionary<string, object>> { olderChange });
                    var latest = RelationshipsSelfTestStore.SelectRelationshipObservedHeroes(connection).Single();
                    add("unchanged_later_observation_rejects_older_latest_overwrite",
                        ReadInt(latest, "last_observed_day", -1) == 40 && ReadInt(latest, "updated_ts", -1) == 20
                        && ReadString(latest, "profile_json", "") == ReadString(versions[19], "profile", "")
                        && ReadString(LoadRelationshipHeroObservations(connection, "main", 39, new[] { "history_a" }).Single(), "profile_json", "") == ReadString(olderChange, "profile", ""),
                        "A newer unchanged observation advances freshness without changing updated_ts; an older pending change remains historical and cannot replace the latest facts.");
                    RelationshipsSelfTestStore.InsertRelationshipDailyInputs2(connection, new Dictionary<string, object> { ["campaign"] = campaign });
                    PruneRelationshipHeroObservations(connection, "main", 40);
                    add("earliest_pending_day_protects_hero_anchor",
                        ReadInt(RelationshipsSelfTestStore.SelectRelationshipHeroObservations2(connection).Single(), "day", -1) == 11,
                        "Even an out-of-order pending day retains its needed predecessor when a later day requests cleanup.");
                    RelationshipsSelfTestStore.DeleteRelationshipDailyInputs(connection);
                }
                var replayInput = new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["timelineId"] = "main", ["worldDay"] = 21,
                    ["heroes"] = new List<object> { new Dictionary<string, object> { ["heroStringId"] = "history_a", ["age"] = 41 } }
                };
                PersistAndHydrateRelationshipDailyInput(replayInput);
                MarkRelationshipDailyInputProcessed(campaign, "main", 21);
                replayInput["heroes"] = new List<object> { new Dictionary<string, object> { ["heroStringId"] = "history_a", ["age"] = 99 } };
                PersistAndHydrateRelationshipDailyInput(replayInput);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    add("processed_day_retry_preserves_hero_history",
                        ReadString(LoadRelationshipHeroObservations(connection, "main", 21, new[] { "history_a" }).Single(), "profile_json", "").Contains("41")
                        && ReadString(RelationshipsSelfTestStore.SelectRelationshipDailyInputs2(connection).Single(), "hero_changes_json", "") == "[]",
                        "A duplicate completed input leaves the historical version and cleared durable payload intact.");
                replayInput["worldDay"] = 22;
                replayInput["heroes"] = new List<object> { new Dictionary<string, object> { ["heroStringId"] = "history_a", ["age"] = 42 } };
                PersistAndHydrateRelationshipDailyInput(replayInput);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    RelationshipsSelfTestStore.UpdateRelationshipDailyInputs(connection);
                replayInput["heroes"] = new List<object> { new Dictionary<string, object> { ["heroStringId"] = "history_a", ["age"] = 99 } };
                PersistAndHydrateRelationshipDailyInput(replayInput);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign)) RecoverInterruptedRelationshipDays(connection, campaign);
                PersistAndHydrateRelationshipDailyInput(replayInput);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var partial = RelationshipsSelfTestStore.SelectRelationshipDailyInputs3(connection).Single();
                    add("recovered_partial_day_retry_keeps_original_facts",
                        ReadString(partial, "status", "") == "pending" && ReadInt(partial, "started_ts", 0) == 123
                        && ReadString(partial, "pair_cursor", "") == "history_a|history_b"
                        && ReadString(LoadRelationshipHeroObservations(connection, "main", 22, new[] { "history_a" }).Single(), "profile_json", "").Contains("42"),
                        "Retries during processing and after recovery preserve the partially completed day's original facts and cursor.");
                }
            }
            finally
            {
                RelationshipThroughputOriginalReadPath = previousOriginalPath;
                PersistenceService.ClearSaveSyncCaches(campaign);
                ReignPostgreSqlStorage.DropCampaign(campaign);
                PersistenceService.TryDeleteDirectory(Program.CampaignDirectory(campaign));
            }
        }

        private static void RunRelationshipReplaySemanticTests(Action<string, bool, string> add)
        {
            var captured = new RelationshipReplayConceptionInputs("fixture");
            captured.BeginRoute(true);
            string originalId = captured.Next("pair_day");
            captured.CompleteRoute();
            int originalRoll = StableDie("fixture|" + originalId + "|pregnancy_commitment_a", 100);
            captured.BeginRoute(false);
            string candidateId = captured.Next("pair_day");
            captured.CompleteRoute();
            add("replay_captures_conception_random_inputs", originalId == candidateId
                && originalRoll == StableDie("fixture|" + candidateId + "|pregnancy_commitment_a", 100),
                "All replay routes reuse the original random conception identifier because commitment rolls depend on it; production still generates new GUIDs.");
            bool duplicateRejected = false, extraRejected = false, missingRejected = false;
            try { captured.Next("pair_day"); } catch (InvalidOperationException) { duplicateRejected = true; }
            captured.BeginRoute(false);
            try { captured.Next("unexpected_day"); } catch (InvalidOperationException) { extraRejected = true; }
            captured.BeginRoute(false);
            try { captured.CompleteRoute(); } catch (InvalidOperationException) { missingRejected = true; }
            add("replay_conception_inputs_reject_trajectory_changes", duplicateRejected && extraRejected && missingRejected,
                "Captured random input cannot hide duplicate, extra or omitted conception attempts.");

            var conceptions = new List<Dictionary<string, object>> { new Dictionary<string, object>
            {
                ["conception_id"] = "opaque_conception_a", ["attempt_id"] = "pair_day", ["mother_id"] = "mother",
                ["biological_father_id"] = "father", ["due_day"] = 1037d, ["status"] = "pending_game",
                ["payload_json"] = "{\"chancePercent\":5,\"roll\":2}", ["updated_ts"] = 1
            } };
            var actions = new List<Dictionary<string, object>> { new Dictionary<string, object>
            {
                ["director_action_id"] = "opaque_action_a", ["action_type"] = "start_conception", ["actor_id"] = "mother",
                ["target_id"] = "father", ["status"] = "pending", ["world_day"] = 1001d,
                ["payload_json"] = "{\"conceptionId\":\"opaque_conception_a\",\"dueDay\":1037}", ["created_ts"] = 1
            } };
            var replayConceptions = Json.Deserialize<List<Dictionary<string, object>>>(Json.Serialize(conceptions).Replace("opaque_conception_a", "opaque_conception_b"));
            var replayActions = Json.Deserialize<List<Dictionary<string, object>>>(Json.Serialize(actions).Replace("opaque_conception_a", "opaque_conception_b").Replace("opaque_action_a", "opaque_action_b"));
            replayActions[0]["created_ts"] = 500;
            replayConceptions[0]["payload_json"] = "{\"roll\":2,\"chancePercent\":5}";
            var originalMap = BuildRelationshipReplayIdentityMap(conceptions, actions);
            var replayMap = BuildRelationshipReplayIdentityMap(replayConceptions, replayActions);
            string expected = RelationshipReplayRowsHash(actions, originalMap);
            add("replay_opaque_ids_preserve_full_semantics",
                expected == RelationshipReplayRowsHash(replayActions, replayMap)
                && RelationshipReplayRowsHash(conceptions, originalMap) == RelationshipReplayRowsHash(replayConceptions, replayMap)
                && originalMap["opaque_action_a"] == replayMap["opaque_action_b"],
                "Random storage IDs, JSON key ordering and wall-clock audit time do not change the compared action/conception semantics.");

            replayActions[0]["payload_json"] = "{\"conceptionId\":\"opaque_conception_b\",\"dueDay\":1038}";
            var changedPayloadMap = BuildRelationshipReplayIdentityMap(replayConceptions, replayActions);
            add("replay_retains_action_payload_changes", expected != RelationshipReplayRowsHash(replayActions, changedPayloadMap),
                "An altered native action payload remains a functional mismatch after opaque ID normalization.");
            replayActions[0]["payload_json"] = "{\"conceptionId\":\"opaque_conception_b\",\"dueDay\":1037}";
            replayConceptions[0]["biological_father_id"] = "different_father";
            var changedParentMap = BuildRelationshipReplayIdentityMap(replayConceptions, replayActions);
            add("replay_retains_referenced_conception_changes", expected != RelationshipReplayRowsHash(replayActions, changedParentMap),
                "Changing the referenced conception changes its native action's semantic identity; parentage is never masked.");

            var duplicate = new Dictionary<string, object>(actions[0]);
            duplicate["director_action_id"] = "opaque_action_duplicate";
            bool rejected = false;
            try { BuildRelationshipReplayIdentityMap(conceptions, new List<Dictionary<string, object>> { actions[0], duplicate }); }
            catch (InvalidOperationException) { rejected = true; }
            add("replay_rejects_ambiguous_action_identity", rejected,
                "Duplicate indistinguishable actions fail comparison instead of hiding exactly-once errors by sorting random IDs.");
            var commitments = new List<Dictionary<string, object>> { new Dictionary<string, object>
                { ["conception_id"] = "opaque_conception_a", ["roll_a"] = 12, ["passed_a"] = 1, ["status"] = "awaiting_conception" } };
            string commitmentHash = RelationshipReplayRowsHash(commitments, originalMap);
            commitments[0]["roll_a"] = 13;
            add("replay_retains_commitment_roll_changes", commitmentHash != RelationshipReplayRowsHash(commitments, originalMap),
                "Commitment roll differences remain failures even when opaque identifiers have matching semantic identities.");
        }

        private static void RunRelationshipNativeObservationWriterTests(Action<string, bool, string> add)
        {
            string campaign = "__throughput_native_" + Guid.NewGuid().ToString("N");
            const string key = "native_a|native_b";
            try
            {
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    CharactersService.EnsureIdentitySchema(connection);
                    var heroes = new[] { "native_a", "native_b" }.Select(id => new Dictionary<string, object>
                    {
                        ["heroStringId"] = id, ["age"] = 25, ["isAdult"] = true,
                        ["isAlive"] = true, ["isLord"] = true, ["isFemale"] = false
                    }).ToList();
                    CharactersService.UpsertIdentityRosterHeroesBatch(connection, heroes, 1);
                    RelationshipsSelfTestStore.InsertRelationshipPairChemistry3(connection, new Dictionary<string, object> { ["version"] = MbtiChemistryVersion });
                    Func<Dictionary<string, object>> pair = () => RelationshipsSelfTestStore.SelectRelationshipPairChemistry3(connection).Single();
                    Func<bool> waiting = () => ReadInt(pair(), "native_action_pending", 0) == 1
                        && RelationshipsSelfTestStore.SelectRelationshipNativeTargets2(connection).Count == 1;
                    var context = new AmbientPairContext { PairKey = key, HeroAId = "native_a", HeroBId = "native_b", ContextKind = "party" };
                    int day = Enumerable.Range(10, 1000).First(candidate =>
                    {
                        var dice = BuildDailyPairDice(campaign, context, candidate);
                        return dice.RollAtoB <= AdjustedMbtiCompatibility(100) && dice.RollBtoA <= AdjustedMbtiCompatibility(100);
                    });
                    foreach (string route in new[] { "single_projection", "bulk_projection", "commitment", "daily_snapshot" })
                    {
                        RelationshipsSelfTestStore.DeleteRelationshipNativeTargets(connection);
                        RelationshipsSelfTestStore.InsertRelationshipNativeTargets(connection);
                        RelationshipsSelfTestStore.UpdateRelationshipPairChemistry2(connection);
                        if (route == "single_projection") RefreshEffectivePairProjection(connection, campaign, "main", pair(), 2, "observation_test");
                        else if (route == "bulk_projection") RefreshEffectivePairProjectionsBulk(connection, campaign, "main", new List<Dictionary<string, object>> { pair() }, 2, "observation_test");
                        else if (route == "commitment") ApplyRelationshipCommitmentAffinityDelta(connection, campaign, key, 0, 2);
                        else
                        {
                            MbtiRelationshipSnapshotApi(new Dictionary<string, object>
                            {
                                ["campaignId"] = campaign, ["timelineId"] = "main", ["worldDay"] = day,
                                ["continuousWorker"] = true, ["heroes"] = heroes,
                                ["presenceGroups"] = new List<object> { new Dictionary<string, object>
                                { ["kind"] = "party", ["id"] = "native_test", ["heroIds"] = new List<object> { "native_a", "native_b" } } }
                            });
                        }
                        add("native_observation_survives_" + route, waiting(),
                            "Recalculation preserves an observation-required target even when its unconfirmed stored values match.");
                    }
                    RelationshipsSelfTestStore.DeleteRelationshipNativeTargets(connection);
                    RelationshipsSelfTestStore.UpdateRelationshipPairChemistry3(connection);
                    RefreshEffectivePairProjectionsBulk(connection, campaign, "main", new List<Dictionary<string, object>> { pair() }, day + 1, "zero_orphan");
                    add("zero_projection_orphan_still_requires_observation", waiting(),
                        "An orphan flag with a zero projection becomes explicit observation work instead of disappearing during recalculation.");
                    var pulled = RelationshipNativeTargetPullApi(new Dictionary<string, object> { ["campaignId"] = campaign, ["timelineId"] = "main" });
                    var target = ReadDictionaryList(pulled, "targets").Single();
                    RelationshipNativeTargetReceiptsApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaign, ["timelineId"] = "main", ["worldDay"] = day + 1,
                        ["receipts"] = new List<object> { new Dictionary<string, object>
                        { ["pairKey"] = key, ["revision"] = ReadInt(target, "revision", 0), ["status"] = "already_aligned", ["observedRelation"] = 0 } }
                    });
                    add("recalculated_native_observation_clears_on_matching_receipt",
                        ReadInt(pair(), "native_action_pending", 1) == 0 && WorldSimulationService.TableCount(connection, "relationship_native_targets") == 0,
                        "The current native receipt completes the repaired target and clears its chemistry flag.");
                }
            }
            finally
            {
                lock (RelationshipReadyGate)
                {
                    RelationshipReadyCampaigns.Remove(campaign); RelationshipReadyVersions.Remove(campaign);
                    RelationshipRecoveredCampaigns.Remove(campaign); RelationshipCampaignGenerations.Remove(campaign);
                }
                ReignPostgreSqlStorage.ClearAllPools();
                try { ReignPostgreSqlStorage.DropCampaign(campaign); } catch { }
                PersistenceService.TryDeleteDirectory(Program.CampaignDirectory(campaign));
            }
        }
    }
}
