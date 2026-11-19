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
                    Program.OpenCampaignConnection(campaignId))
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
                        Program.ExecuteSql(connection, "BEGIN IMMEDIATE;");
                        try
                        {
                            Program.ExecuteSql(connection, "DELETE FROM identity_roster;");
                            if (route == 0)
                                foreach (Dictionary<string, object> hero in rosterFixture)
                                    CharactersService.UpsertIdentityRosterHero(connection, hero, 424242);
                            else
                                CharactersService.UpsertIdentityRosterHeroesBatch(connection, rosterFixture, 424242);
                            rosterResults[route] = Json.Serialize(Program.QuerySql(connection,
                                "SELECT * FROM identity_roster ORDER BY hero_id;"));
                        }
                        finally { Program.ExecuteSql(connection, "ROLLBACK;"); }
                        rosterRollbackClean &= ReadInt(Program.QuerySql(connection,
                            "SELECT COUNT(*) AS count FROM identity_roster;").FirstOrDefault(), "count", -1) == 0;
                    }
                    add("roster_batch_matches_complete_serial_rows",
                        rosterResults[0] == rosterResults[1] && rosterRollbackClean,
                        "128 diverse roster entries preserve all columns, names, family, role and clamped trait values, timestamps and rollback.");

                    Program.ExecuteSql(connection, @"INSERT INTO character_public_standing(
campaign_id,timeline_id,subject_id,standing_value,sources_json,revision,
calculated_revision,last_changed_day,updated_ts,calculation_status,last_error) VALUES
($campaign,'main','noop_a',-5,'[]',2,2,4,10,'ready',''),
($campaign,'main','noop_b',11,'[]',3,3,4,10,'ready',''),
($campaign,'main','orphan',30,'[]',4,4,4,10,'ready','');",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId
                        });
                    Program.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,
base_chance_b_to_a,chance_a_to_b,chance_b_to_a,affinity_a_to_b,
affinity_b_to_a,tag_a_to_b,tag_b_to_a,first_day,last_day,
projected_native_relation,native_action_pending,native_action_id,
compatibility_version,updated_ts)
VALUES('noop_a|noop_b','noop_a','noop_b','INTJ','ENFP',80,80,70,70,
7,-3,'neutral','neutral',1,4,5,1,'native_target:noop_a|noop_b',
$version,101);",
                        new Dictionary<string, object>
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
                        Program.ExecuteSql(connection, @"INSERT INTO identity_roster(hero_id,canonical_name,kingdom_id,current_charm,is_player,updated_ts)
VALUES($id,$id,$kingdom,150,$player,1);", new Dictionary<string, object>
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
                    Program.ExecuteSql(connection, "UPDATE character_public_standing SET standing_value=1,sources_json=$sources WHERE subject_id='noop_b';",
                        new Dictionary<string, object> { ["sources"] = favoringSources });
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
                        Program.ExecuteSql(connection, "UPDATE character_public_standing SET standing_value=17,sources_json='[]' WHERE subject_id='noop_b';");
                        bool mutationVisible = ReadInt(ResolveObserverPublicStanding(connection,
                            campaignId, "main", "favorite", "noop_b"), "value", 0) == 17;
                        foreach (string id in observers.Concat(new[] { "noop_a", "noop_b", "missing_hero" }))
                            context.ReadStanding(id);
                        mutationVisible &= ReadInt(context.ReadStanding("noop_a"), "standing_value", 0) == -5
                            && context.ReadStanding("missing_hero") == null;
                        Program.ExecuteSql(connection, "UPDATE identity_roster SET current_charm=210,is_player=0 WHERE hero_id='player_observer';");
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
                            Program.ExecuteSql(connection, @"INSERT INTO character_public_standing(
campaign_id,timeline_id,subject_id,standing_value,sources_json,revision,
calculated_revision,last_changed_day,updated_ts,calculation_status,last_error)
VALUES($campaign,'main','missing_hero',23,'[]',1,1,4,10,'ready','');",
                                new Dictionary<string, object> { ["campaign"] = campaignId });
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
                    Program.ExecuteSql(connection, "DELETE FROM character_public_standing WHERE subject_id='missing_hero';");
                    Program.ExecuteSql(connection, "UPDATE identity_roster SET current_charm=150,is_player=1 WHERE hero_id='player_observer';");
                    Program.ExecuteSql(connection, "UPDATE character_public_standing SET standing_value=11,sources_json='[]' WHERE subject_id='noop_b';");

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

                    int pairsBeforeOrphanRead = ReadInt(Program.QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry;")
                        .FirstOrDefault(), "count", -1);
                    Dictionary<string, object> orphan =
                        ResolveEffectiveAttitude(connection, campaignId, "main",
                            "noop_a", "orphan", "self_test");
                    int pairsAfterOrphanRead = ReadInt(Program.QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry;")
                        .FirstOrDefault(), "count", -1);
                    add("standing_read_never_creates_social_pair",
                        !ReadBool(orphan, "hasPair", true)
                        && ReadInt(orphan, "effectiveAttitude", -1) == 0
                        && pairsAfterOrphanRead == pairsBeforeOrphanRead,
                        "Reading a subject's Public Standing without an established relationship remains a pure read.");

                    Program.ExecuteSql(connection, @"UPDATE character_public_standing
SET standing_value=0 WHERE subject_id IN ('noop_a','noop_b');");
                    Program.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET affinity_a_to_b=4,affinity_b_to_a=6,updated_ts=101
WHERE pair_key='noop_a|noop_b';");
                    Program.ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,
world_day,last_sync_day,attempt_count,claimed_ts,last_error,updated_ts,revision)
VALUES('noop_a|noop_b','noop_a','noop_b',5,0,'pending',4,-1000,0,0,'',202,7);");
                    Dictionary<string, object> noOpPair = Program.QuerySql(connection,
                        "SELECT * FROM relationship_pair_chemistry WHERE pair_key='noop_a|noop_b';")
                        .First();
                    bool targetChanged = RefreshEffectivePairProjection(connection,
                        campaignId, "main", noOpPair, 5,
                        "unchanged_projection_self_test");
                    Dictionary<string, object> unchangedPair = Program.QuerySql(connection,
                        "SELECT updated_ts FROM relationship_pair_chemistry WHERE pair_key='noop_a|noop_b';")
                        .FirstOrDefault();
                    Dictionary<string, object> unchangedTarget = Program.QuerySql(connection,
                        "SELECT updated_ts,revision FROM relationship_native_targets WHERE pair_key='noop_a|noop_b';")
                        .FirstOrDefault();
                    add("unchanged_projection_does_not_rewrite_pair_or_target",
                        !targetChanged
                        && ReadLong(unchangedPair, "updated_ts", -1) == 101
                        && ReadLong(unchangedTarget, "updated_ts", -1) == 202
                        && ReadInt(unchangedTarget, "revision", -1) == 7,
                        "An unchanged effective/native result preserves both row timestamps and the native target revision.");

                    Program.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET affinity_a_to_b=3,affinity_b_to_a=7,updated_ts=303
WHERE pair_key='noop_a|noop_b';");
                    Dictionary<string, object> sameAveragePair = Program.QuerySql(connection,
                        "SELECT * FROM relationship_pair_chemistry WHERE pair_key='noop_a|noop_b';")
                        .First();
                    bool sameAverageTargetChanged =
                        RefreshEffectivePairProjection(connection, campaignId,
                            "main", sameAveragePair, 6,
                            "same_rounded_projection_self_test");
                    Dictionary<string, object> sameAverageAfter = Program.QuerySql(connection,
                        "SELECT updated_ts FROM relationship_pair_chemistry WHERE pair_key='noop_a|noop_b';")
                        .FirstOrDefault();
                    Dictionary<string, object> sameAverageTarget = Program.QuerySql(connection,
                        "SELECT updated_ts,revision FROM relationship_native_targets WHERE pair_key='noop_a|noop_b';")
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
                    Program.ExecuteSql(connection, @"INSERT INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,last_processed_day,version,updated_ts)
VALUES('family_a|family_b','family_a','family_b',1,$version,515);",
                        new Dictionary<string, object>
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
                        Program.QuerySql(connection, @"SELECT last_processed_day,updated_ts
FROM relationship_pair_lifecycle WHERE pair_key='family_a|family_b';")
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
                        Program.ExecuteSql(connection, @"INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,
template_version,traits_json,created_ts,updated_ts)
VALUES($hero,'INTJ','Architect','Self-test personality',
'pregenerated_profile',0,1,'{}',1,1);",
                            new Dictionary<string, object> { ["hero"] = hero });
                    }
                    bool firstConcurrentCreate = false;
                    bool secondConcurrentCreate = false;
                    System.Threading.Tasks.Task.WaitAll(
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            using (ReignDbConnection concurrent =
                                Program.OpenCampaignConnection(campaignId))
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
                                Program.OpenCampaignConnection(campaignId))
                            {
                                secondConcurrentCreate = EnsurePoliticalRelationshipPair(
                                    concurrent, campaignId, "main", "race_pair_a|race_pair_b",
                                    "race_pair_a", "race_pair_b", 7,
                                    true, true);
                            }
                        }));
                    int concurrentPairRows = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_chemistry
WHERE pair_key='race_pair_a|race_pair_b';")
                        .FirstOrDefault(), "count", -1);
                    add("political_pair_creation_is_concurrency_safe",
                        firstConcurrentCreate != secondConcurrentCreate
                        && concurrentPairRows == 1,
                        "Concurrent topology workers create one chemistry row and only its owner reports a new pair.");
                    Program.ExecuteSql(connection, @"INSERT INTO identity_roster(
hero_id,canonical_name,clan_id,kingdom_id,is_lord,is_ruler,
family_ids_json,updated_ts,is_alive,is_adult,is_player,is_clan_leader)
VALUES
('political_ruler','Ruler','clan_r','kingdom_test',1,1,'[]',1,1,1,0,1),
('political_leader_a','Leader A','clan_a','kingdom_test',1,0,'[]',1,1,1,0,1),
('political_leader_b','Leader B','clan_b','kingdom_test',1,0,'[]',1,1,1,0,1);");
                    Dictionary<string, object> firstPolitical =
                        ReconcilePoliticalRelationshipNetwork(connection,
                            campaignId, "main", 7, "self_test_first");
                    Program.ExecuteSql(connection, @"UPDATE relationship_pair_provenance
SET updated_ts=404 WHERE source='kingdom_leadership' AND active=1;");
                    Program.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET updated_ts=405 WHERE pair_key LIKE 'political_%';");
                    Dictionary<string, object> secondPolitical =
                        ReconcilePoliticalRelationshipNetwork(connection,
                            campaignId, "main", 7, "self_test_repeat");
                    int unchangedProvenance = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_provenance
WHERE source='kingdom_leadership' AND active=1 AND updated_ts=404;")
                        .FirstOrDefault(), "count", -1);
                    int unchangedPoliticalPairs = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_chemistry
WHERE pair_key LIKE 'political_%' AND updated_ts=405;")
                        .FirstOrDefault(), "count", -1);
                    add("political_reconciliation_is_write_idempotent",
                        ReadInt(firstPolitical, "requiredPairs", -1) == 3
                        && ReadInt(firstPolitical, "createdPairs", -1) == 3
                        && ReadInt(secondPolitical, "createdPairs", -1) == 0
                        && unchangedProvenance == 3
                        && unchangedPoliticalPairs == 3,
                        "Repeating an unchanged political topology performs no pair or provenance rewrites.");

                    Program.ExecuteSql(connection, @"UPDATE identity_roster
SET is_clan_leader=0 WHERE hero_id='political_leader_b';");
                    ReconcilePoliticalRelationshipNetwork(connection, campaignId,
                        "main", 8, "self_test_leader_removed");
                    int retainedFormerPairs = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_chemistry
WHERE pair_key IN (
'political_leader_a|political_leader_b',
'political_leader_b|political_ruler');")
                        .FirstOrDefault(), "count", -1);
                    int inactiveFormerRequirements = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_provenance
WHERE source='kingdom_leadership' AND active=0
AND pair_key IN (
'political_leader_a|political_leader_b',
'political_leader_b|political_ruler');")
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
                PersistenceService.TryDeleteDirectory(Program.CampaignDirectory(campaignId));
            }
            return results;
        }
#endif

        internal static Dictionary<string, object> BuildWorldTestStandingAndPoliticalMetrics(
            ReignDbConnection connection, string campaignId, string timelineId,
            double latestDay)
        {
            EnsureWorldRelationshipSchema(connection);
            Dictionary<string, object> standing = Program.QuerySql(connection, @"
SELECT COUNT(*) AS character_count,
SUM(CASE WHEN standing_value>0 THEN 1 ELSE 0 END) AS positive_count,
SUM(CASE WHEN standing_value<0 THEN 1 ELSE 0 END) AS negative_count,
SUM(CASE WHEN standing_value=0 THEN 1 ELSE 0 END) AS neutral_count,
COALESCE(MIN(standing_value),0) AS minimum,
COALESCE(MAX(standing_value),0) AS maximum,
COALESCE(AVG(standing_value),0) AS average,
SUM(CASE WHEN calculation_status<>'ready' THEN 1 ELSE 0 END) AS error_count,
SUM(CASE WHEN calculated_revision<>revision THEN 1 ELSE 0 END) AS stale_count
FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["day"] = latestDay
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            List<int> standingValues = Program.QuerySql(connection, @"
SELECT standing_value FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY standing_value;",
                new Dictionary<string, object>
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
            foreach (Dictionary<string, object> row in Program.QuerySql(connection, @"
SELECT sources_json,revision FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
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
            Dictionary<string, object> invalidations = Program.QuerySql(connection, @"
SELECT COUNT(*) AS total,
SUM(CASE WHEN status='pending' THEN 1 ELSE 0 END) AS pending,
SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END) AS failed,
COALESCE(SUM(CASE WHEN status='pending' THEN affected_pairs-processed_pairs ELSE 0 END),0)
AS remaining_pairs,
COALESCE(MAX(CASE WHEN status='pending' THEN $now-created_ts ELSE 0 END),0)
AS oldest_pending_seconds,
COALESCE(SUM(affected_pairs),0) AS affected_observers
FROM public_standing_invalidations
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> political = Program.QuerySql(connection, @"
SELECT COUNT(*) AS active_required,
SUM(CASE WHEN source='kingdom_leadership' THEN 1 ELSE 0 END) AS kingdom_pairs,
SUM(CASE WHEN source='ruler_network' THEN 1 ELSE 0 END) AS ruler_pairs
FROM relationship_pair_provenance
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND source IN ('kingdom_leadership','ruler_network')
AND active=1 AND required=1;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, List<Dictionary<string, object>>>
                desiredPolitical = BuildRequiredPoliticalPairDefinitions(
                    Program.QuerySql(connection, @"
SELECT * FROM identity_roster
WHERE is_alive=1 AND is_adult=1 AND is_player=0
AND (is_clan_leader=1 OR is_ruler=1) AND hero_id<>'';"));
            HashSet<string> currentPolitical = new HashSet<string>(Program.QuerySql(
                    connection, @"
SELECT pair_key,source FROM relationship_pair_provenance
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND source IN ('kingdom_leadership','ruler_network')
AND active=1 AND required=1;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).Select(row => ReadString(row, "pair_key", "") + "|"
                    + ReadString(row, "source", "")),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> currentPairKeys = new HashSet<string>(Program.QuerySql(
                    connection,
                    "SELECT pair_key FROM relationship_pair_chemistry;")
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
            foreach (Dictionary<string, object> row in Program.QuerySql(connection, @"
SELECT p.affinity_a_to_b,p.affinity_b_to_a,
COALESCE(sa.standing_value,0) AS standing_a,
COALESCE(sb.standing_value,0) AS standing_b
FROM relationship_pair_chemistry p
LEFT JOIN character_public_standing sa ON sa.campaign_id=$campaign
AND sa.timeline_id=$timeline AND sa.subject_id=p.hero_a_id
LEFT JOIN character_public_standing sb ON sb.campaign_id=$campaign
AND sb.timeline_id=$timeline AND sb.subject_id=p.hero_b_id; ",
                new Dictionary<string, object>
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
