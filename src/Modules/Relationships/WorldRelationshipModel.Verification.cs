using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
#if !REIGN_EXCLUDE_VERIFICATION
        internal static List<Dictionary<string, object>>
            RunWorldRelationshipModelSelfTests()
        {
            List<Dictionary<string, object>> results =
                RunRelationshipThroughputSelfTests();
            Action<string, bool, string> add = (id, passed, summary) =>
                results.Add(new Dictionary<string, object>
                {
                    ["ok"] = true, ["passed"] = passed,
                    ["suite"] = "world_relationship_model",
                    ["caseId"] = id, ["name"] = id,
                    ["summary"] = summary, ["durationMs"] = 0
                });
            string campaignId = "world_model_"
                + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                using (ReignDbConnection connection =
                    ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    CharactersService.EnsureIdentitySchema(connection);
                    EnsureMbtiRelationshipSchema(connection);
                    EnsureWorldRelationshipSchema(connection);

                    // A full native roster replacement must preserve every field
                    // and its transaction boundary when routed through the batch writer.
                    List<Dictionary<string, object>> rosterFixture = Enumerable.Range(0, 128)
                        .Select(index => new Dictionary<string, object>
                        {
                            ["heroStringId"] = "batch_" + index,
                            ["name"] = "NPC '" + index + " — Élan",
                            ["clanId"] = "clan_" + index % 7,
                            ["kingdomId"] = "kingdom_" + index % 3,
                            ["isLord"] = index % 2 == 0, ["isRuler"] = index % 11 == 0,
                            ["fatherId"] = "father_" + index % 4,
                            ["motherId"] = "mother_" + index % 5,
                            ["spouseId"] = index % 2 == 0 ? "spouse_" + index : "",
                            ["childrenIds"] = new List<object> { "child_" + index, "child_" + index },
                            ["isAlive"] = index % 13 != 0, ["isChild"] = index % 17 == 0,
                            ["isPlayer"] = index == 0, ["isFemale"] = index % 2 == 1,
                            ["clanTier"] = index % 9 - 1, ["currentCharm"] = index - 10,
                            ["occupation"] = index % 2 == 0 ? "Lord" : "Wanderer",
                            ["isNotable"] = index % 3 == 0, ["isWanderer"] = index % 5 == 0,
                            ["isClanLeader"] = index % 7 == 0, ["isMercenaryClan"] = index % 11 == 0,
                            ["governorOfSettlementId"] = "town_" + index % 6,
                            ["governorOfSettlementName"] = "Town '" + index % 6
                        }).ToList();
                    string[] rosterResults = new string[2];
                    bool rosterRollbackClean = true;
                    for (int route = 0; route < rosterResults.Length; route++)
                    {
                        RelationshipsStore.Begin(connection);
                        try
                        {
                            RelationshipsStore.DeleteIdentityRoster(connection);
                            if (route == 0)
                                foreach (Dictionary<string, object> hero in rosterFixture)
                                    CharactersService.UpsertIdentityRosterHero(connection, hero, 424242);
                            else
                                CharactersService.UpsertIdentityRosterHeroesBatch(connection, rosterFixture, 424242);
                            rosterResults[route] = Json.Serialize(RelationshipsStore.SelectIdentityRoster2(connection));
                        }
                        finally { RelationshipsStore.Rollback(connection); }
                        rosterRollbackClean &= ReadInt(RelationshipsStore.CountIdentityRoster(connection).FirstOrDefault(), "count", -1) == 0;
                    }
                    add("roster_batch_matches_complete_serial_rows",
                        rosterResults[0] == rosterResults[1] && rosterRollbackClean,
                        "128 diverse roster entries preserve all columns, names, family, role and clamped trait values, timestamps and rollback.");

                    RelationshipsStore.InsertCharacterPublicStanding(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId
                        });
                    RelationshipsStore.InsertRelationshipPairChemistry6(connection, new Dictionary<string, object>
                        {
                            ["version"] = MbtiChemistryVersion
                        });
                    Dictionary<string, object> resolved =
                        ResolveEffectiveAttitude(connection, campaignId, "main",
                            "noop_a", "noop_b", "self_test");
                    Dictionary<string, object> reverse =
                        ResolveEffectiveAttitude(connection, campaignId, "main",
                            "noop_b", "noop_a", "self_test");
                    add("standing_changes_effective_view_not_personal_affinity",
                        ReadBool(resolved, "hasPair", false)
                        && ReadInt(resolved, "personalAffinity", 0) == 7
                        && ReadInt(resolved, "targetPublicStanding", 0) == 11
                        && ReadInt(resolved, "effectiveAttitude", 0) == 18
                        && ReadInt(reverse, "personalAffinity", 0) == -3
                        && ReadInt(reverse, "targetPublicStanding", 0) == -5
                        && ReadInt(reverse, "effectiveAttitude", 0) == -8,
                        "Public Standing is added dynamically and directionally without rewriting personal affinity.");

                    string[] observers = { "favorite", "local_observer", "foreign_observer", "player_observer" };
                    foreach (string id in observers.Concat(new[] { "noop_b" }))
                        RelationshipsStore.InsertIdentityRoster(connection, new Dictionary<string, object>
                        {
                            ["id"] = id, ["kingdom"] = id == "foreign_observer" ? "foreign" : "local",
                            ["player"] = id == "player_observer" ? 1 : 0
                        });
                    string favoringSources = Json.Serialize(new[]
                    {
                        new Dictionary<string, object> { ["baseTagId"] = "fixture", ["effectiveContribution"] = 11d },
                        new Dictionary<string, object> { ["baseTagId"] = "ruler_favoring", ["sourceType"] = "reputation",
                            ["linkedHeroId"] = "favorite", ["effectiveContribution"] = -10d }
                    });
                    RelationshipsStore.UpdateCharacterPublicStanding(connection, new Dictionary<string, object> { ["sources"] = favoringSources });
                    Dictionary<string, string> expectedStanding = observers.ToDictionary(id => id,
                        id => Json.Serialize(ResolveObserverPublicStanding(connection, campaignId, "main", id, "noop_b")));
                    using (RelationshipStandingContext context = new RelationshipStandingContext(connection, campaignId, "main"))
                    {
                        context.Prefetch(observers.Concat(new[] { "noop_a", "noop_b", "missing_hero" }));
                        bool equivalent = true;
                        for (int iteration = 0; iteration < 128; iteration++)
                        {
                            foreach (string id in observers)
                                equivalent &= Json.Serialize(ResolveObserverPublicStanding(connection,
                                    campaignId, "main", id, "noop_b")) == expectedStanding[id];
                            equivalent &= ProjectNativeRelation(connection, "noop_b", "player_observer", 37, -81, 5, 6) == 37;
                        }
                        equivalent &= ReadInt(ReadPublicStandingRow(connection, campaignId, "main", "missing_hero"), "standing_value", 0) == 0;
                        add("standing_batch_preserves_observer_rules_and_bounded_reads",
                            equivalent && context.StandingReads == 1 && context.RosterReads == 1 && context.PlayerReads == 1,
                            "512 observer-specific resolutions and 128 native projections reuse three batch reads, preserving Favoring, foreign/player views and missing rows.");
                        RelationshipsStore.UpdateCharacterPublicStanding2(connection);
                        bool mutationVisible = ReadInt(ResolveObserverPublicStanding(connection,
                            campaignId, "main", "favorite", "noop_b"), "value", 0) == 17;
                        foreach (string id in observers.Concat(new[] { "noop_a", "noop_b", "missing_hero" }))
                            context.ReadStanding(id);
                        mutationVisible &= ReadInt(context.ReadStanding("noop_a"), "standing_value", 0) == -5
                            && context.ReadStanding("missing_hero") == null;
                        RelationshipsStore.UpdateIdentityRoster(connection);
                        mutationVisible &= ReadInt(context.ReadRoster("player_observer"), "current_charm", 0) == 210;
                        foreach (string id in observers.Concat(new[] { "noop_b", "missing_hero" }))
                            context.ReadRoster(id);
                        mutationVisible &= context.ReadRoster("missing_hero") == null
                            && !context.PlayerIds().Contains("player_observer");
                        // A nested projection must invalidate the outer day as well,
                        // including previously absent subjects inserted by the write.
                        using (RelationshipStandingContext nested = new RelationshipStandingContext(connection, campaignId, "main"))
                        {
                            nested.Prefetch(new[] { "noop_b", "missing_hero" });
                            RelationshipsStore.InsertCharacterPublicStanding2(connection, new Dictionary<string, object> { ["campaign"] = campaignId });
                            mutationVisible &= ReadInt(nested.ReadStanding("missing_hero"), "standing_value", 0) == 23;
                            nested.ReadStanding("noop_b");
                            mutationVisible &= nested.StandingReads == 2;
                        }
                        foreach (string id in observers.Concat(new[] { "noop_a", "noop_b", "missing_hero" }))
                            context.ReadStanding(id);
                        mutationVisible &= ReadInt(context.ReadStanding("missing_hero"), "standing_value", 0) == 23;
                        add("standing_batch_observes_transaction_mutations",
                            mutationVisible && context.StandingReads == 3
                                && context.RosterReads == 2 && context.PlayerReads == 2,
                            "Standing, roster and player mutations refresh immediately in batches, preserving absent/inserted rows and invalidating nested and outer scopes.");
                    }
                    add("standing_batch_scope_does_not_escape_day", RelationshipStandingContext.For(connection) == null,
                        "The batch context is disposed before another day, timeline or restored state can use it.");
                    RelationshipsStore.DeleteCharacterPublicStanding(connection);
                    RelationshipsStore.UpdateIdentityRoster2(connection);
                    RelationshipsStore.UpdateCharacterPublicStanding3(connection);

                    List<Dictionary<string, object>> presenceFixture = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["heroIds"] = new[] { "a", "B", "a" } },
                        new Dictionary<string, object> { ["heroIds"] = new[] { "b", "c" } },
                        new Dictionary<string, object> { ["heroIds"] = new string[0] }
                    };
                    Dictionary<string, HashSet<int>> membership = BuildRelationshipCoPresenceIndex(presenceFixture);
                    string[] presenceIds = { "a", "b", "B", "c", "absent", "", null };
                    add("copresence_index_matches_group_scan",
                        presenceIds.All(a => presenceIds.All(b => RelationshipHeroesShareGroup(membership, a, b)
                            == AreRelationshipHeroesCoPresent(presenceFixture, a, b))),
                        "Indexed membership preserves overlaps, case-insensitivity, duplicates, self-membership and missing-ID behavior.");

                    add("relationship_scheduler_active_campaign_and_fairness",
                        RelationshipCampaignWorkPriority("active", "current", "active", "current", false)
                            < RelationshipCampaignWorkPriority("other", "main", "active", "current", false)
                        && RelationshipCampaignWorkPriority("active", "current", "active", "current", false)
                            < RelationshipCampaignWorkPriority("active", "older", "active", "current", false)
                        && RelationshipCampaignWorkPriority("other", "main", "active", "current", true)
                            < RelationshipCampaignWorkPriority("active", "current", "active", "current", true)
                        && RelationshipCampaignWorkPriority("other", "main", "", "", false) == 0,
                        "Fresh native campaign/timeline wins normally; the periodic maintenance slot serves another campaign and an absent heartbeat preserves chronological selection.");

                    int pairsBeforeOrphanRead = ReadInt(RelationshipsStore.CountRelationshipPairChemistry(connection)
                        .FirstOrDefault(), "count", -1);
                    Dictionary<string, object> orphan =
                        ResolveEffectiveAttitude(connection, campaignId, "main",
                            "noop_a", "orphan", "self_test");
                    int pairsAfterOrphanRead = ReadInt(RelationshipsStore.CountRelationshipPairChemistry(connection)
                        .FirstOrDefault(), "count", -1);
                    add("standing_read_never_creates_social_pair",
                        !ReadBool(orphan, "hasPair", true)
                        && ReadInt(orphan, "effectiveAttitude", -1) == 0
                        && pairsAfterOrphanRead == pairsBeforeOrphanRead,
                        "Reading a subject's Public Standing without an established relationship remains a pure read.");

                    RelationshipsStore.UpdateCharacterPublicStanding4(connection);
                    RelationshipsStore.UpdateRelationshipPairChemistry27(connection);
                    RelationshipsStore.InsertRelationshipNativeTargets2(connection);
                    Dictionary<string, object> noOpPair = RelationshipsStore.SelectRelationshipPairChemistry21(connection)
                        .First();
                    bool targetChanged = RefreshEffectivePairProjection(connection,
                        campaignId, "main", noOpPair, 5,
                        "unchanged_projection_self_test");
                    Dictionary<string, object> unchangedPair = RelationshipsStore.SelectRelationshipPairChemistry22(connection)
                        .FirstOrDefault();
                    Dictionary<string, object> unchangedTarget = RelationshipsStore.SelectRelationshipNativeTargets11(connection)
                        .FirstOrDefault();
                    add("unchanged_projection_does_not_rewrite_pair_or_target",
                        !targetChanged
                        && ReadLong(unchangedPair, "updated_ts", -1) == 101
                        && ReadLong(unchangedTarget, "updated_ts", -1) == 202
                        && ReadInt(unchangedTarget, "revision", -1) == 7,
                        "An unchanged effective/native result preserves both row timestamps and the native target revision.");

                    RelationshipsStore.UpdateRelationshipPairChemistry28(connection);
                    Dictionary<string, object> sameAveragePair = RelationshipsStore.SelectRelationshipPairChemistry21(connection)
                        .First();
                    bool sameAverageTargetChanged =
                        RefreshEffectivePairProjection(connection, campaignId,
                            "main", sameAveragePair, 6,
                            "same_rounded_projection_self_test");
                    Dictionary<string, object> sameAverageAfter = RelationshipsStore.SelectRelationshipPairChemistry22(connection)
                        .FirstOrDefault();
                    Dictionary<string, object> sameAverageTarget = RelationshipsStore.SelectRelationshipNativeTargets11(connection)
                        .FirstOrDefault();
                    add("same_rounded_native_result_does_not_rewrite_projection",
                        !sameAverageTargetChanged
                        && ReadLong(sameAverageAfter, "updated_ts", -1) == 303
                        && ReadLong(sameAverageTarget, "updated_ts", -1) == 202
                        && ReadInt(sameAverageTarget, "revision", -1) == 7,
                        "Directional affinity changes whose authoritative average is unchanged do not churn the native target.");

                    AmbientPairContext lifecyclePair = new AmbientPairContext
                    {
                        PairKey = "family_a|family_b",
                        HeroAId = "family_a", HeroBId = "family_b"
                    };
                    RelationshipsStore.InsertRelationshipPairLifecycle3(connection, new Dictionary<string, object>
                        {
                            ["version"] = RelationshipLifecycleVersion
                        });
                    Dictionary<string, object> familyA =
                        new Dictionary<string, object>
                        {
                            ["heroStringId"] = "family_a", ["age"] = 30,
                            ["fatherId"] = "shared_father",
                            ["isFemale"] = false
                        };
                    Dictionary<string, object> familyB =
                        new Dictionary<string, object>
                        {
                            ["heroStringId"] = "family_b", ["age"] = 28,
                            ["fatherId"] = "shared_father",
                            ["isFemale"] = true
                        };
                    ProcessRelationshipLifecycle(connection, campaignId,
                        lifecyclePair, 9, familyA, familyB, 0, 0,
                        null, false, "main");
                    Dictionary<string, object> unchangedLifecycle =
                        RelationshipsStore.SelectRelationshipPairLifecycle16(connection)
                        .FirstOrDefault();
                    add("lifecycle_no_event_does_not_write_processed_date",
                        ReadInt(unchangedLifecycle, "last_processed_day", -1) == 1
                        && ReadLong(unchangedLifecycle, "updated_ts", -1) == 515,
                        "A lifecycle inspection with no state transition does not rewrite the row merely to advance a processed date.");

                    foreach (string hero in new[]
                    {
                        "political_ruler", "political_leader_a",
                        "political_leader_b", "race_pair_a",
                        "race_pair_b"
                    })
                    {
                        RelationshipsStore.InsertRelationshipPersonalities4(connection, new Dictionary<string, object> { ["hero"] = hero });
                    }
                    bool firstConcurrentCreate = false;
                    bool secondConcurrentCreate = false;
                    System.Threading.Tasks.Task.WaitAll(
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            using (ReignDbConnection concurrent =
                                ReignDatabase.OpenCampaignConnection(campaignId))
                            {
                                firstConcurrentCreate = EnsurePoliticalRelationshipPair(
                                    concurrent, campaignId, "main", "race_pair_a|race_pair_b",
                                    "race_pair_a", "race_pair_b", 7,
                                    true, true);
                            }
                        }),
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            using (ReignDbConnection concurrent =
                                ReignDatabase.OpenCampaignConnection(campaignId))
                            {
                                secondConcurrentCreate = EnsurePoliticalRelationshipPair(
                                    concurrent, campaignId, "main", "race_pair_a|race_pair_b",
                                    "race_pair_a", "race_pair_b", 7,
                                    true, true);
                            }
                        }));
                    int concurrentPairRows = ReadInt(RelationshipsStore.CountRelationshipPairChemistry3(connection)
                        .FirstOrDefault(), "count", -1);
                    add("political_pair_creation_is_concurrency_safe",
                        firstConcurrentCreate != secondConcurrentCreate
                        && concurrentPairRows == 1,
                        "Concurrent topology workers create one chemistry row and only its owner reports a new pair.");
                    RelationshipsStore.InsertIdentityRoster2(connection);
                    Dictionary<string, object> firstPolitical =
                        ReconcilePoliticalRelationshipNetwork(connection,
                            campaignId, "main", 7, "self_test_first");
                    Dictionary<string, object> Leader(string hero, string kingdom, bool player, bool ruler) =>
                        new Dictionary<string, object> { ["heroStringId"] = hero, ["kingdomId"] = kingdom,
                            ["isClanLeader"] = true, ["isPlayer"] = player, ["isRuler"] = ruler };
                    foreach (bool playerRules in new[] { false, true })
                    {
                        var topology = BuildRequiredPoliticalPairDefinitions(new[] {
                            Leader("player", "home", true, playerRules), Leader("npc", "home", false, !playerRules),
                            Leader("vassal", "home", false, false), Leader("foreign", "away", false, true) });
                        add(playerRules ? "player_ruler_has_domestic_political_pairs" : "player_vassal_keeps_existing_network_scope",
                            topology.ContainsKey(AmbientPairKey("vassal", "npc"))
                            && topology.ContainsKey(AmbientPairKey("player", "vassal")) == playerRules
                            && !topology.ContainsKey(AmbientPairKey("player", "foreign"))
                            && !topology.ContainsKey(AmbientPairKey("vassal", "foreign")),
                            "Domestic political relationships cover the player as ruler, with ordinary vassals and foreign rulers in their existing scopes.");
                    }
                    Dictionary<string, object> Direction(string observer, string target) =>
                        new Dictionary<string, object> { ["observerId"] = observer, ["targetId"] = target };
                    var requested = Enumerable.Range(0, 4997).Select(i => Direction("absent_" + i, "political_ruler")).ToList();
                    requested.Add(Direction("noop_a", "noop_b"));
                    requested.Add(Direction("noop_b", "noop_a"));
                    requested.Add(Direction("political_leader_a", "political_ruler"));
                    var payload = new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["timelineId"] = "main", ["pairs"] = requested
                    };
                    var batch = EffectiveAttitudeBatchApi(payload);
                    var rows = ReadDictionaryList(batch, "attitudes");
                    add("effective_batch_5000_preserves_every_directed_identity",
                        ReadBool(batch, "ok", false) && rows.Count == requested.Count
                        && rows.Zip(requested, (row, pair) => ReadString(row, "observerId", "") == ReadString(pair, "observerId", "")
                            && ReadString(row, "targetId", "") == ReadString(pair, "targetId", "")).All(match => match)
                        && !ReadBool(rows[0], "hasPair", true) && ReadInt(rows[0], "effectiveAttitude", -1) == 0
                        && ReadBool(rows[4999], "hasPair", false) && ReadInt(rows[4999], "effectiveAttitude", -1) == 0
                        && new[] { 4997, 4998, 4999 }.All(i => Json.Serialize(rows[i]) == Json.Serialize(
                            ResolveEffectiveAttitude(connection, campaignId, "main", ReadString(requested[i], "observerId", ""),
                                ReadString(requested[i], "targetId", ""), "batch"))),
                        "The final player-ruler pair is present and neutral. Missing chemistry is explicit and both directions match the scalar API.");
                    requested.Add(Direction("overflow", "political_ruler"));
                    var oversized = EffectiveAttitudeBatchApi(payload);
                    add("effective_batch_5001_refused_without_partial_results",
                        !ReadBool(oversized, "ok", true) && !oversized.ContainsKey("attitudes"),
                        "An oversized request returns an error without a successful prefix.");
                    payload["pairs"] = new object[] { requested[0], "invalid" };
                    add("effective_batch_malformed_pair_is_not_dropped",
                        !ReadBool(EffectiveAttitudeBatchApi(payload), "ok", true),
                        "A malformed element cannot disappear from a successful result.");
                    RelationshipsStore.UpdateRelationshipPairProvenance(connection);
                    RelationshipsStore.UpdateRelationshipPairChemistry29(connection);
                    Dictionary<string, object> secondPolitical =
                        ReconcilePoliticalRelationshipNetwork(connection,
                            campaignId, "main", 7, "self_test_repeat");
                    int unchangedProvenance = ReadInt(RelationshipsStore.CountRelationshipPairProvenance(connection)
                        .FirstOrDefault(), "count", -1);
                    int unchangedPoliticalPairs = ReadInt(RelationshipsStore.CountRelationshipPairChemistry4(connection)
                        .FirstOrDefault(), "count", -1);
                    add("political_reconciliation_is_write_idempotent",
                        ReadInt(firstPolitical, "requiredPairs", -1) == 3
                        && ReadInt(firstPolitical, "createdPairs", -1) == 3
                        && ReadInt(secondPolitical, "createdPairs", -1) == 0
                        && unchangedProvenance == 3
                        && unchangedPoliticalPairs == 3,
                        "Repeating an unchanged political topology performs no pair or provenance rewrites.");

                    RelationshipsStore.UpdateIdentityRoster3(connection);
                    ReconcilePoliticalRelationshipNetwork(connection, campaignId,
                        "main", 8, "self_test_leader_removed");
                    int retainedFormerPairs = ReadInt(RelationshipsStore.CountRelationshipPairChemistry5(connection)
                        .FirstOrDefault(), "count", -1);
                    int inactiveFormerRequirements = ReadInt(RelationshipsStore.CountRelationshipPairProvenance2(connection)
                        .FirstOrDefault(), "count", -1);
                    add("former_political_pairs_retain_personal_history",
                        retainedFormerPairs == 2
                        && inactiveFormerRequirements == 2,
                        "Leadership changes remove only political provenance while retaining the established personal pairs.");
                }
            }
            finally
            {
                ReignPostgreSqlStorage.ClearAllPools();
                try { ReignPostgreSqlStorage.DropCampaign(campaignId); } catch { }
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaignId));
            }
            return results;
        }
#endif

        internal static Dictionary<string, object> BuildWorldTestStandingAndPoliticalMetrics(
            ReignDbConnection connection, string campaignId, string timelineId,
            double latestDay)
        {
            EnsureWorldRelationshipSchema(connection);
            Dictionary<string, object> standing = RelationshipsStore.CountCharacterPublicStanding(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["day"] = latestDay
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            List<int> standingValues = RelationshipsStore.SelectCharacterPublicStanding2(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).Select(row => ReadInt(row, "standing_value", 0)).ToList();
            double standingMedian = standingValues.Count == 0 ? 0d
                : standingValues.Count % 2 == 1
                    ? standingValues[standingValues.Count / 2]
                    : (standingValues[standingValues.Count / 2 - 1]
                        + standingValues[standingValues.Count / 2]) / 2d;
            Dictionary<string, int> sourceCategories =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            long revisionCount = 0;
            foreach (Dictionary<string, object> row in RelationshipsStore.SelectCharacterPublicStanding3(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }))
            {
                revisionCount += Math.Max(0, ReadInt(row, "revision", 1) - 1);
                foreach (Dictionary<string, object> source in ReputationService.ParsePublicStandingSources(
                    ReadString(row, "sources_json", "[]")))
                {
                    string category = FirstNonEmpty(
                        ReadString(source, "sourceType", ""), "other");
                    sourceCategories[category] =
                        (sourceCategories.TryGetValue(category, out int count)
                            ? count : 0) + 1;
                }
            }
            Dictionary<string, object> invalidations = RelationshipsStore.CountPublicStandingInvalidations(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> political = RelationshipsStore.CountRelationshipPairProvenance3(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, List<Dictionary<string, object>>>
                desiredPolitical = BuildRequiredPoliticalPairDefinitions(
                    RelationshipsStore.SelectIdentityRoster3(connection));
            HashSet<string> currentPolitical = new HashSet<string>(RelationshipsStore.SelectRelationshipPairProvenance2(
                    connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).Select(row => ReadString(row, "pair_key", "") + "|"
                    + ReadString(row, "source", "")),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> currentPairKeys = new HashSet<string>(RelationshipsStore.SelectRelationshipPairChemistry23(
                    connection)
                .Select(row => ReadString(row, "pair_key", "")),
                StringComparer.OrdinalIgnoreCase);
            int missingPolitical = desiredPolitical.Sum(pair =>
                pair.Value.Count(provenance =>
                    !currentPolitical.Contains(pair.Key + "|"
                        + ReadString(provenance, "source", ""))
                    || !currentPairKeys.Contains(pair.Key)));
            HashSet<string> desiredPoliticalKeys = new HashSet<string>(
                desiredPolitical.SelectMany(pair => pair.Value.Select(provenance =>
                    pair.Key + "|" + ReadString(provenance, "source", ""))),
                StringComparer.OrdinalIgnoreCase);
            int obsoletePolitical = currentPolitical.Count(key =>
                !desiredPoliticalKeys.Contains(key));
            Dictionary<string, int> effectiveBands = WorldSimulationService.WorldTestRelationshipBands
                .ToDictionary(value => value, value => 0,
                    StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in RelationshipsStore.SelectRelationshipPairChemistry24(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }))
            {
                string bandAB = RelationshipBand(Clamp(ReadInt(row, "affinity_a_to_b", 0)
                    + ReadInt(row, "standing_b", 0), -100, 100));
                string bandBA = RelationshipBand(Clamp(ReadInt(row, "affinity_b_to_a", 0)
                    + ReadInt(row, "standing_a", 0), -100, 100));
                effectiveBands[bandAB]++;
                effectiveBands[bandBA]++;
            }
            return new Dictionary<string, object>
            {
                ["publicStanding"] = new Dictionary<string, object>
                {
                    ["characterCount"] = ReadInt(standing, "character_count", 0),
                    ["positiveCount"] = ReadInt(standing, "positive_count", 0),
                    ["negativeCount"] = ReadInt(standing, "negative_count", 0),
                    ["neutralCount"] = ReadInt(standing, "neutral_count", 0),
                    ["minimum"] = ReadInt(standing, "minimum", 0),
                    ["maximum"] = ReadInt(standing, "maximum", 0),
                    ["average"] = Math.Round(ReadDouble(standing, "average", 0d), 2),
                    ["median"] = standingMedian,
                    ["revisionCount"] = revisionCount,
                    ["sourceCategories"] = sourceCategories,
                    ["errorCount"] = ReadInt(standing, "error_count", 0),
                    ["staleCount"] = ReadInt(standing, "stale_count", 0),
                    ["invalidationsPending"] = ReadInt(invalidations, "pending", 0),
                    ["invalidationFailures"] = ReadInt(invalidations, "failed", 0),
                    ["invalidationPairsRemaining"] = ReadInt(invalidations,
                        "remaining_pairs", 0),
                    ["affectedObserverCount"] = ReadLong(invalidations,
                        "affected_observers", 0),
                    ["oldestPendingInvalidationSeconds"] = ReadLong(invalidations,
                        "oldest_pending_seconds", 0)
                },
                ["politicalNetwork"] = new Dictionary<string, object>
                {
                    ["activeRequiredPairs"] = ReadInt(political, "active_required", 0),
                    ["kingdomLeadershipPairs"] = ReadInt(political, "kingdom_pairs", 0),
                    ["rulerNetworkPairs"] = ReadInt(political, "ruler_pairs", 0),
                    ["missingRequiredPairs"] = missingPolitical,
                    ["obsoleteRequiredPairs"] = obsoletePolitical
                },
                ["effectiveBands"] = effectiveBands
            };
        }
    }
}
