using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class ReputationService
    {
#if !REIGN_EXCLUDE_VERIFICATION
        internal static List<Dictionary<string, object>> RunRumorSubsystemSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            results.AddRange(RunRulerFavorContactBatchSelfTests());
            Action<string, bool, string, object> add = (id, passed, summary, data) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "character_social_reputation",
                ["caseId"] = id, ["name"] = id, ["summary"] = summary,
                ["data"] = data ?? new Dictionary<string, object>(), ["durationMs"] = 0
            });
            using (var started = new System.Threading.ManualResetEventSlim())
            {
                System.Threading.Tasks.Task<Dictionary<string, object>> catalogRead;
                bool completedWithoutFileLock;
                lock (Program.FileLock)
                {
                    catalogRead = System.Threading.Tasks.Task.Run(() =>
                    {
                        started.Set();
                        return ReadGlobalSocialCatalog();
                    });
                    completedWithoutFileLock = started.Wait(2000) && catalogRead.Wait(2000);
                }
                catalogRead.Wait(5000);
                add("social_catalog_read_does_not_take_global_file_lock",
                    completedWithoutFileLock && catalogRead.IsCompletedSuccessfully
                        && catalogRead.Result.Count > 0,
                    "Schema-time social catalog reads remain independent of the action queue's global file lock.", null);
            }
            add("social_fixture_missing_enrollment_rejected",
                !ValidateSocialBalanceLiveEnrollment(new Dictionary<string, object>(), "__missing_enrollment", out string missingEnrollmentError)
                && !string.IsNullOrWhiteSpace(missingEnrollmentError),
                "A missing run enrollment fails closed with a normal error before opening any campaign database.", null);
            var favorPreparations = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["event_id"] = "old_first", ["sequence"] = 1L, ["correlation_id"] = "first" },
                new Dictionary<string, object> { ["event_id"] = "old_second", ["sequence"] = 2L, ["correlation_id"] = "second" },
                new Dictionary<string, object> { ["event_id"] = "current_first", ["sequence"] = 3L, ["correlation_id"] = "first" },
                new Dictionary<string, object> { ["event_id"] = "current_second", ["sequence"] = 4L, ["correlation_id"] = "second" }
            };
            var currentFavorPreparation = SocialBalanceCurrentFavorEvents(favorPreparations, "first", 3, "second", 4);
            add("favor_projection_current_preparation_only",
                currentFavorPreparation.Count == 2
                && currentFavorPreparation.All(row => ReadString(row, "event_id", "").StartsWith("current_", StringComparison.Ordinal))
                && SocialBalanceHasTwoFavorSources(currentFavorPreparation, "first", "second")
                && SocialBalanceCurrentFavorEvents(favorPreparations, "first", 4, "second", 3).Count == 0
                && SocialBalanceCurrentFavorEvents(favorPreparations, "first", 0, "second", 4).Count == 0,
                "Exact native sequences identify this preparation and cannot borrow an older preparation's source events or mismatched keys.", null);
            var favorDeliveries = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["correlation_id"] = "first" },
                new Dictionary<string, object> { ["correlation_id"] = "first" },
                new Dictionary<string, object> { ["correlation_id"] = "second" }
            };
            add("favor_projection_retry_source_identity",
                SocialBalanceHasTwoFavorSources(favorDeliveries, "first", "second")
                && !SocialBalanceHasTwoFavorSources(favorDeliveries, "first", "missing")
                && !SocialBalanceHasTwoFavorSources(favorDeliveries, "first", "first"),
                "Repeated deliveries retain two canonical favor sources; missing or identical pair keys fail.", null);
            favorDeliveries.Add(new Dictionary<string, object> { ["correlation_id"] = "unexpected" });
            add("favor_projection_rejects_unexpected_source",
                !SocialBalanceHasTwoFavorSources(favorDeliveries, "first", "second"),
                "An unrelated third source must not be accepted as a delivery retry.", null);
            var favorRepresentative = new Dictionary<string, object>
            {
                ["tagId"] = "ruler_favoring:first", ["baseTagId"] = "ruler_favoring",
                ["collapsedFamily"] = "ruler_favoring", ["collapsedRepresentative"] = true,
                ["effectiveContribution"] = -5.08d
            };
            var favorSecondary = new Dictionary<string, object>
            {
                ["tagId"] = "ruler_favoring:second", ["baseTagId"] = "ruler_favoring",
                ["collapsedFamily"] = "ruler_favoring", ["effectiveContribution"] = 0d
            };
            var mixedStanding = new Dictionary<string, object>
            {
                ["standingValue"] = -3,
                ["sources"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["tagId"] = "dynasty_secure", ["effectiveContribution"] = 2d },
                    favorRepresentative, favorSecondary
                }
            };
            add("favor_projection_accepts_unrelated_standing",
                SocialBalanceFavoringCostCollapsed(mixedStanding, 246),
                "The native projection verifier isolates the favor cost while retaining the ruler's dynasty bonus and rounded total.", null);
            favorSecondary["effectiveContribution"] = -5.08d;
            mixedStanding["standingValue"] = -8;
            add("favor_projection_rejects_double_charge",
                !SocialBalanceFavoringCostCollapsed(mixedStanding, 246),
                "Two favorites must not charge the standing penalty twice even when the total matches the incorrect sources.", null);
            favorSecondary["effectiveContribution"] = 0d;
            mixedStanding["standingValue"] = -5;
            add("favor_projection_rejects_incorrect_total",
                !SocialBalanceFavoringCostCollapsed(mixedStanding, 246),
                "Isolating favor must not hide a wrong total standing value.", null);
            mixedStanding["standingValue"] = -3;
            favorRepresentative["effectiveContribution"] = -5d;
            add("favor_projection_rejects_premature_rounding",
                !SocialBalanceFavoringCostCollapsed(mixedStanding, 246),
                "The source contribution retains fractional Charm mitigation before final rounding.", null);
            add("charm_mitigation_exact",
                Math.Abs(SocialCharmMitigation(0) - 0d) < 0.000001d
                && Math.Abs(SocialCharmMitigation(100) - 0.20d) < 0.000001d
                && Math.Abs(SocialCharmMitigation(200) - 0.40d) < 0.000001d
                && Math.Abs(SocialCharmMitigation(300) - 0.60d) < 0.000001d
                && Math.Abs(SocialCharmMitigation(600) - 0.60d) < 0.000001d,
                "Current Charm reduces only negative social effects by the exact zero-to-sixty-percent curve.", null);
            add("modifier_ordering_and_rounding",
                CalculateSocialModifier(new[] { 5, -15 }, 100, true) == -7
                && CalculateSocialModifier(new[] { 5, -15 }, 100, false) == -4
                && CalculateSocialModifier(new[] { 10 }, 300, true) == 10,
                "Positive and negative values are separated, negative Charm mitigation precedes kingdom scaling, and the result rounds away from zero.", null);
            add("deterministic_roll_replay",
                DeterministicSocialRoll("same-seed") == DeterministicSocialRoll("same-seed")
                && DeterministicSocialRoll("same-seed") >= 0d && DeterministicSocialRoll("same-seed") < 1d,
                "Exposure and promotion rolls replay deterministically.", null);
            Dictionary<string, object> expandedCatalog = ReadGlobalSocialCatalog();
            HashSet<string> expandedArchetypes = new HashSet<string>(
                ReadDictionaryList(expandedCatalog, "archetypes").Select(x => ReadString(x, "id", "")),
                StringComparer.OrdinalIgnoreCase);
            add("expanded_catalog_complete",
                new[]
                {
                    "strong_captain", "weak_captain", "tactician", "failed_tactician",
                    "siege_commander", "inept_besieger", "siege_breaker", "champion_of_the_pit",
                    "duelist", "fallen_challenger", "coward", "war_crowned", "defeated_crown",
                    "realm_builder", "realm_in_decline", "provider_of_the_realm", "starving_crown",
                    "unifier_of_the_realm", "fractured_crown", "fractured_crown_rebellion",
                    "keeper_of_order", "lawless_crown", "guardian_of_the_commons",
                    "lord_of_empty_fields", "consolidator", "overextended_crown",
                    "fair_hand_of_the_crown", "hoarder_of_titles", "gatherer_of_banners",
                    "scatterer_of_banners", "ruler_favoring_presence", "ruler_favoring_dialogue", "attentive_lord", "absent_lord",
                    "infertile", "flirt", "promiscuous_flirtation", "the_unchaste"
                }.All(expandedArchetypes.Contains),
                "The revisioned catalog contains every combat, ruler-governance, and court-personality producer.", null);

            string campaignId = "__social_reputation_test_" + Guid.NewGuid().ToString("N");
            string campaignPath = CampaignFileService.CampaignDirectory(campaignId);
            try
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(connection);
                    WorldSimulationService.EnsureWorldTestTelemetrySchema(connection);
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    foreach (Dictionary<string, object> hero in new[]
                    {
                        new Dictionary<string, object> { ["heroStringId"]="married",["name"]="Married",["isAlive"]=true,["isAdult"]=true,["isPlayer"]=false,["isFemale"]=false,["clanTier"]=6,["currentCharm"]=0 },
                        new Dictionary<string, object> { ["heroStringId"]="lover",["name"]="Lover",["isAlive"]=true,["isAdult"]=true,["isPlayer"]=false,["isFemale"]=true,["clanTier"]=1,["currentCharm"]=0 },
                        new Dictionary<string, object> { ["heroStringId"]="player_subject",["name"]="Player",["isAlive"]=true,["isAdult"]=true,["isPlayer"]=true,["isFemale"]=false,["clanTier"]=6,["currentCharm"]=100 }
                    }) CharactersService.UpsertIdentityRosterHero(connection, hero, ts);
                    EnsurePublicStandingForRoster(connection, campaignId, "main", 1d);
                    List<Dictionary<string, object>> initialPublicStanding = ReputationStore.SelectCharacterPublicStanding4(connection, new Dictionary<string, object> { ["campaign"] = campaignId });
                    add("public_standing_roster_initialization",
                        initialPublicStanding.Count == 3
                        && initialPublicStanding.All(row => ReadInt(row, "standing_value", int.MinValue) == 0)
                        && initialPublicStanding.All(row => ReadInt(row, "revision", 0) == 1)
                        && initialPublicStanding.All(row => Math.Abs(ReadDouble(row, "last_changed_day", 0d) - 1d) < 0.000001d)
                        && initialPublicStanding.All(row => ReadString(row, "sources_json", "") == "[]"),
                        "Every synchronized character receives a zero-valued, revisioned Public Standing trait before acquiring social sources.",
                        initialPublicStanding);
                    Dictionary<string, object> playerSubject =
                        new Dictionary<string, object>
                        {
                            ["heroStringId"] = "player_subject",
                            ["name"] = "Player",
                            ["isAlive"] = true,
                            ["isAdult"] = true,
                            ["isPlayer"] = true,
                            ["clanTier"] = 6,
                            ["currentCharm"] = 100
                        };
                    foreach (string observerId in new[] { "married", "lover" })
                    {
                        CharactersService.UpsertVerifiedIdentity(
                            connection,
                            new Dictionary<string, object>
                            {
                                ["heroStringId"] = observerId,
                                ["name"] = observerId
                            },
                            playerSubject,
                            "self_test",
                            "player_subject",
                            1d,
                            "social_scope_test");
                        string pairKey = RelationshipsService.AmbientPairKey(observerId, "player_subject");
                        bool observerIsA = pairKey.StartsWith(observerId + "|",
                            StringComparison.OrdinalIgnoreCase);
                        ReputationStore.InsertOrIgnoreRelationshipPairChemistry2(connection, new Dictionary<string, object>
                            {
                                ["pair"] = pairKey,
                                ["a"] = observerIsA ? observerId : "player_subject",
                                ["b"] = observerIsA ? "player_subject" : observerId,
                                ["ts"] = ts
                            });
                    }
                    ReputationStore.DeleteDirectionalSocialProjections(
                        connection, new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId
                            });
                    List<Dictionary<string, object>> pairRowsBefore =
                        ReputationStore.SelectRelationshipPairChemistry4(
                            connection, new Dictionary<string, object>
                            {
                                ["marriedPair"] =
                                    RelationshipsService.AmbientPairKey("married", "player_subject"),
                                ["loverPair"] =
                                    RelationshipsService.AmbientPairKey("lover", "player_subject")
                            });
                    ReconcileSocialRelationshipsForSubjects(
                        connection,
                        campaignId,
                        "main",
                        new[] { "player_subject" },
                        1d);
                    Dictionary<string, object> globalStanding =
                        RelationshipsService.ReadPublicStandingRow(
                            connection,
                            campaignId,
                            "main",
                            "player_subject");
                    Dictionary<string, object> globalMarriedAttitude =
                        RelationshipsService.ResolveEffectiveAttitude(
                            connection,
                            campaignId,
                            "main",
                            "married",
                            "player_subject",
                            "social_reputation_self_test");
                    Dictionary<string, object> globalLoverAttitude =
                        RelationshipsService.ResolveEffectiveAttitude(
                            connection,
                            campaignId,
                            "main",
                            "lover",
                            "player_subject",
                            "social_reputation_self_test");
                    ReconcileSocialRelationshipForObserverSubject(
                        connection,
                        campaignId,
                        "main",
                        "married",
                        "player_subject",
                        1d);
                    Dictionary<string, object> targetedStanding =
                        RelationshipsService.ReadPublicStandingRow(
                            connection,
                            campaignId,
                            "main",
                            "player_subject");
                    Dictionary<string, object> targetedMarriedAttitude =
                        RelationshipsService.ResolveEffectiveAttitude(
                            connection,
                            campaignId,
                            "main",
                            "married",
                            "player_subject",
                            "social_reputation_self_test");
                    List<Dictionary<string, object>> pairRowsAfter =
                        ReputationStore.SelectRelationshipPairChemistry4(
                            connection, new Dictionary<string, object>
                            {
                                ["marriedPair"] =
                                    RelationshipsService.AmbientPairKey("married", "player_subject"),
                                ["loverPair"] =
                                    RelationshipsService.AmbientPairKey("lover", "player_subject")
                            });
                    int copiedProjectionCount = ReadInt(
                        ReputationStore.CountDirectionalSocialProjections(
                            connection, new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId
                            }).FirstOrDefault(),
                        "count",
                        -1);
                    bool pairRowsUnchanged =
                        pairRowsBefore.Count == pairRowsAfter.Count
                        && pairRowsBefore.Zip(
                            pairRowsAfter,
                            (before, after) =>
                                ReadString(before, "pair_key", "") ==
                                    ReadString(after, "pair_key", "")
                                && ReadInt(before, "affinity_a_to_b", int.MinValue) ==
                                    ReadInt(after, "affinity_a_to_b", int.MaxValue)
                                && ReadInt(before, "affinity_b_to_a", int.MinValue) ==
                                    ReadInt(after, "affinity_b_to_a", int.MaxValue)
                                && Math.Abs(
                                    ReadDouble(before, "updated_ts", -1d) -
                                    ReadDouble(after, "updated_ts", -2d)) <
                                    0.000001d)
                            .All(value => value);
                    int standingValue =
                        ReadInt(globalStanding, "standing_value", int.MinValue);
                    add(
                        "foreground_public_standing_scope_and_parity",
                        globalStanding != null
                        && targetedStanding != null
                        && ReadInt(globalStanding, "revision", -1) ==
                            ReadInt(targetedStanding, "revision", -2)
                        && standingValue ==
                            ReadInt(targetedStanding, "standing_value", int.MaxValue)
                        && Math.Abs(
                            ReadDouble(globalStanding, "updated_ts", -1d) -
                            ReadDouble(targetedStanding, "updated_ts", -2d)) <
                            0.000001d
                        && ReadBool(globalMarriedAttitude, "hasPair", false)
                        && ReadBool(globalLoverAttitude, "hasPair", false)
                        && ReadBool(targetedMarriedAttitude, "hasPair", false)
                        && ReadInt(
                            globalMarriedAttitude,
                            "targetPublicStanding",
                            int.MaxValue) == standingValue
                        && ReadInt(
                            globalLoverAttitude,
                            "targetPublicStanding",
                            int.MaxValue) == standingValue
                        && ReadInt(
                            targetedMarriedAttitude,
                            "effectiveAttitude",
                            int.MaxValue) ==
                            ReadInt(
                                globalMarriedAttitude,
                                "effectiveAttitude",
                                int.MinValue)
                        && copiedProjectionCount == 0
                        && pairRowsUnchanged,
                        "Foreground identity reconciliation reuses one revision-stable subject Public Standing value for existing observers without copying observer projections or rewriting relationship rows.",
                        new Dictionary<string, object>
                        {
                            ["standingValue"] = standingValue,
                            ["globalRevision"] =
                                ReadInt(globalStanding, "revision", -1),
                            ["targetedRevision"] =
                                ReadInt(targetedStanding, "revision", -1),
                            ["copiedProjectionCount"] = copiedProjectionCount,
                            ["pairRowsUnchanged"] = pairRowsUnchanged,
                            ["marriedEffectiveAttitude"] =
                                ReadInt(
                                    targetedMarriedAttitude,
                                    "effectiveAttitude",
                                    int.MinValue),
                            ["loverEffectiveAttitude"] =
                                ReadInt(
                                    globalLoverAttitude,
                                    "effectiveAttitude",
                                    int.MinValue)
                        });
                    RelationshipsService.AmbientPairContext pair = new RelationshipsService.AmbientPairContext { PairKey = RelationshipsService.AmbientPairKey("married", "lover"), HeroAId = "married", HeroBId = "lover" };
                    Dictionary<string, object> first = RegisterSocialOccurrence(connection, campaignId,
                        BuildRelationshipSocialOccurrence(pair, 10, "affair",
                            new Dictionary<string, object> { ["heroStringId"]="married",["spouseId"]="spouse",["clanTier"]=6,["isFemale"]=false },
                            new Dictionary<string, object> { ["heroStringId"]="lover",["spouseId"]="",["clanTier"]=1,["isFemale"]=true },
                            "spouse", "", true, false, "An affair was exposed."));
                    Dictionary<string, object> replay = RegisterSocialOccurrence(connection, campaignId,
                        BuildRelationshipSocialOccurrence(pair, 10, "affair",
                            new Dictionary<string, object> { ["heroStringId"]="married",["spouseId"]="spouse",["clanTier"]=6,["isFemale"]=false },
                            new Dictionary<string, object> { ["heroStringId"]="lover",["spouseId"]="",["clanTier"]=1,["isFemale"]=true },
                            "spouse", "", true, false, "An affair was exposed."));
                    Dictionary<string, object> second = RegisterSocialOccurrence(connection, campaignId,
                        BuildRelationshipSocialOccurrence(pair, 11, "affair",
                            new Dictionary<string, object> { ["heroStringId"]="married",["spouseId"]="spouse",["clanTier"]=6,["isFemale"]=false },
                            new Dictionary<string, object> { ["heroStringId"]="lover",["spouseId"]="",["clanTier"]=1,["isFemale"]=true },
                            "spouse", "", true, true, "The affair was exposed again."));
                    Dictionary<string, object> failedExposurePayload =
                        BuildRelationshipSocialOccurrence(pair, 12, "affair",
                            new Dictionary<string, object> { ["heroStringId"]="married",["spouseId"]="spouse",["clanTier"]=6,["isFemale"]=false },
                            new Dictionary<string, object> { ["heroStringId"]="lover",["spouseId"]="",["clanTier"]=1,["isFemale"]=true },
                            "spouse", "", false, false, "An affair report failed to circulate.");
                    string failedSource = "failed_exposure_0";
                    for (int seedIndex = 0; seedIndex < 100; seedIndex++)
                    {
                        string candidate = "failed_exposure_" + seedIndex.ToString(CultureInfo.InvariantCulture);
                        string seed = string.Join("|", campaignId, "main", "affair", pair.PairKey,
                            candidate, "exposure");
                        if (DeterministicSocialRoll(seed) >= 0.15d)
                        {
                            failedSource = candidate;
                            break;
                        }
                    }
                    failedExposurePayload["sourceEventId"] = failedSource;
                    Dictionary<string, object> failedExposure =
                        RegisterSocialOccurrence(connection, campaignId, failedExposurePayload);
                    add("world_test_rumor_funnel_counts",
                        WorldSimulationService.WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "eligibleHooks") == 2
                        && WorldSimulationService.WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "exposureAttempts") == 2
                        && WorldSimulationService.WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "exposurePassed") == 2
                        && WorldSimulationService.WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "duplicateSourcesSuppressed") == 1
                        && WorldSimulationService.WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "occurrencesCreated") == 1
                        && WorldSimulationService.WorldTestCounterValue(connection, campaignId, "main", 11, "rumors", "promotionPassed") == 1
                        && WorldSimulationService.WorldTestCounterValue(connection, campaignId, "main", 11, "rumors", "durableReputationsCreated") == 3
                        && WorldSimulationService.WorldTestCounterValue(connection, campaignId, "main", 12, "rumors", "exposureFailed") == 1
                        && !ReadBool(failedExposure, "exposed", true),
                        "World Test counts eligible hooks, attempts, passed and failed exposure, duplicate suppression, occurrences, promotion, and durable reputations.",
                        failedExposure);
                    add("clan_tier_multiplier_and_cap",
                        Math.Abs(ReadDouble(first, "exposureChance", 0d) - 0.15d) < 0.000001d,
                        "Tier six applies the highest participant multiplier of three to the five-percent base chance.", first);
                    add("one_daily_roll_per_thread",
                        ReadBool(replay, "duplicate", false),
                        "A relationship thread and archetype can produce only one deterministic occurrence per day.", replay);
                    Dictionary<string, object> firstOccurrence = ReputationStore.SelectRumorOccurrences9(connection, new Dictionary<string, object> { ["id"] = ReadString(first, "occurrenceId", "") }).FirstOrDefault();
                    add("rumor_duration_45_days",
                        Math.Abs(ReadDouble(firstOccurrence, "expires_day", 0d)
                            - ReadDouble(firstOccurrence, "world_day", 0d) - 45d) < 0.000001d,
                        "Built-in Rumors expire exactly forty-five campaign days after occurrence.", firstOccurrence);
                    add("promotion_streak_and_complete_bundle",
                        ReadInt(first, "streakCount", 0) == 1 && !ReadBool(first, "promoted", true)
                        && ReadInt(second, "streakCount", 0) == 2
                        && Math.Abs(ReadDouble(second, "promotionChance", 0d) - 0.30d) < 0.000001d
                        && ReputationStore.SelectCharacterReputations18(connection).Count == 2
                        && ReputationStore.SelectCharacterReputations19(connection).Count == 1,
                        "The first exposure cannot promote; one later incident roll promotes the married and unmarried role bundles atomically.", second);
                    add("promotion_suppresses_matching_rumors",
                        ReputationStore.SelectRumorSubjectTags10(connection).Count == 0,
                        "An established reputation suppresses every matching active rumor without deleting occurrence history.", null);
                    Dictionary<string, object> marriedStanding = ReadPublicStandingTrait(
                        connection, campaignId, "main", "married", 11d);
                    List<Dictionary<string, object>> marriedStandingSources =
                        ReadDictionaryList(marriedStanding, "sources");
                    int stableRevision = ReadInt(marriedStanding, "revision", 0);
                    Dictionary<string, object> stableStanding = ReadPublicStandingTrait(
                        connection, campaignId, "main", "married", 11d);
                    add("public_standing_active_source_summary",
                        ReadString(marriedStanding, "subjectId", "") == "married"
                        && ReadInt(marriedStanding, "standingValue", 0) == -30
                        && marriedStandingSources.Count == 2
                        && marriedStandingSources.All(source =>
                            ReadString(source, "sourceType", "") == "reputation")
                        && marriedStandingSources.Sum(source =>
                            ReadInt(source, "effectiveContribution", 0)) == -30
                        && ReadInt(stableStanding, "revision", 0) == stableRevision
                        && Math.Abs(ReadDouble(stableStanding, "lastChangedCampaignDay", 0d)
                            - ReadDouble(marriedStanding, "lastChangedCampaignDay", 0d)) < 0.000001d,
                        "Public Standing combines active sources after Charm mitigation and remains revision-stable when the projection is unchanged.",
                        marriedStanding);
                    ReputationStore.UpdateIdentityRoster(connection);
                    ReconcileSocialRelationshipsForSubjects(connection, campaignId, "main",
                        new[] { "married" }, 12d);
                    Dictionary<string, object> mitigatedStanding = ReadPublicStandingTrait(
                        connection, campaignId, "main", "married", 12d);
                    add("public_standing_charm_revision",
                        ReadInt(mitigatedStanding, "standingValue", 0) == -24
                        && ReadInt(mitigatedStanding, "revision", 0) == stableRevision + 1
                        && Math.Abs(ReadDouble(mitigatedStanding,
                            "lastChangedCampaignDay", 0d) - 12d) < 0.000001d,
                        "A subject Charm change recalculates negative Public Standing, advances its revision, and records the campaign day.",
                        mitigatedStanding);

                    RelationshipsService.AmbientPairContext divorcePair = new RelationshipsService.AmbientPairContext { PairKey = RelationshipsService.AmbientPairKey("married", "lover"), HeroAId = "married", HeroBId = "lover" };
                    RegisterSocialOccurrence(connection, campaignId, BuildRelationshipSocialOccurrence(divorcePair, 20, "divorce",
                        new Dictionary<string, object> { ["heroStringId"]="married",["isFemale"]=false },
                        new Dictionary<string, object> { ["heroStringId"]="lover",["isFemale"]=true }, "lover", "married", true, true, "They divorced."));
                    int maleDivorce = ReadInt(ReputationStore.SelectCharacterReputations20(connection).FirstOrDefault(), "reputation_value", 0);
                    int femaleDivorce = ReadInt(ReputationStore.SelectCharacterReputations21(connection).FirstOrDefault(), "reputation_value", 0);
                    add("gendered_divorce_reputation", maleDivorce == -5 && femaleDivorce == -20,
                        "Divorce directly establishes the snapshotted male and female reputation values for both former spouses.",
                        new Dictionary<string, object> { ["male"] = maleDivorce, ["female"] = femaleDivorce });

                    string occurrenceId = ReadString(first, "occurrenceId", "");
                    CorrectSocialOccurrenceApi(new Dictionary<string, object> { ["campaignId"]=campaignId,["timelineId"]="main",["occurrenceId"]=occurrenceId });
                    add("correction_preserves_reputation_history",
                        ReputationStore.SelectRumorOccurrences10(connection, new Dictionary<string, object> { ["id"] = occurrenceId }).Any()
                        && ReputationStore.SelectCharacterReputations22(connection).Any(),
                        "Correction deactivates the occurrence and resets its active streak while retaining already-established reputations.", null);
                    add("world_test_rumor_correction_counts",
                        WorldSimulationService.WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "corrected") == 1
                        && WorldSimulationService.WorldTestCounterValue(connection, campaignId, "main", 10, "rumors", "disproven") == 1,
                        "Correction records bounded funnel counters without changing retained reputation history.", null);

                    Func<string, string, string, string, double, bool, Dictionary<string, object>> social =
                        (archetype, subject, role, source, day, forcePromotion) =>
                            RegisterSocialOccurrence(connection, campaignId, new Dictionary<string, object>
                            {
                                ["timelineId"] = "main", ["archetypeId"] = archetype,
                                ["threadKey"] = subject + "|" + archetype, ["sourceEventId"] = source,
                                ["worldDay"] = day, ["forceExposure"] = true, ["forcePromotion"] = forcePromotion,
                                ["provenanceSummary"] = "Expanded social self-test.",
                                ["participants"] = new List<object>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["subjectId"] = subject, ["role"] = role, ["clanTier"] = 6,
                                        ["isAlive"] = true, ["isAdult"] = true, ["isPlayer"] = subject == "player_subject"
                                    }
                                }
                            });

                    Dictionary<string, object> courtSubject = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "court_subject", ["name"] = "Court Subject",
                        ["isAlive"] = true, ["isAdult"] = true, ["isPlayer"] = false,
                        ["isLord"] = true, ["isRuler"] = true, ["occupation"] = "Lord", ["kingdomId"] = "kingdom_court",
                        ["clanId"] = "court_subject_clan", ["clanTier"] = 3, ["currentCharm"] = 0
                    };
                    CharactersService.UpsertIdentityRosterHero(connection, courtSubject, ts);
                    for (int index = 0; index < 50; index++)
                    {
                        string observerId = "court_observer_" + index.ToString("00", CultureInfo.InvariantCulture);
                        CharactersService.UpsertIdentityRosterHero(connection, new Dictionary<string, object>
                        {
                            ["heroStringId"] = observerId, ["name"] = "Court Observer " + index,
                            ["isAlive"] = true, ["isAdult"] = true, ["isPlayer"] = false,
                            ["isLord"] = true, ["occupation"] = "Lord", ["kingdomId"] = "kingdom_court",
                            ["clanId"] = "court_observer_clan_" + index, ["clanTier"] = 2,
                            ["isMercenaryClan"] = false, ["currentCharm"] = 0
                        }, ts);
                        string pairKey = RelationshipsService.AmbientPairKey(observerId, "court_subject");
                        bool observerIsA = pairKey.StartsWith(observerId + "|", StringComparison.OrdinalIgnoreCase);
                        int incoming = index < 30 ? 35 : -35;
                        ReputationStore.UpsertRelationshipPairChemistry(connection, new Dictionary<string, object>
                            {
                                ["pair"] = pairKey,
                                ["a"] = observerIsA ? observerId : "court_subject",
                                ["b"] = observerIsA ? "court_subject" : observerId,
                                ["ab"] = observerIsA ? incoming : 0,
                                ["ba"] = observerIsA ? 0 : incoming,
                                ["ts"] = ts
                            });
                    }
                    bool popularityChanged = RecomputeCourtPopularityReputations(connection,
                        campaignId, "main", "court_subject", 20d, "court_popularity_self_test");
                    Dictionary<string, object> popularityMetric = ReputationStore.SelectCourtDynamicReputationMetrics2(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    List<Dictionary<string, object>> activePopularity = ReputationStore.SelectCharacterReputations23(connection, new Dictionary<string, object> { ["campaign"] = campaignId });
                    add("court_popularity_underlying_affinity_thresholds",
                        popularityChanged
                        && ReadInt(popularityMetric, "positive_count", 0) == 30
                        && ReadInt(popularityMetric, "negative_count", 0) == 20
                        && activePopularity.Any(x => ReadString(x, "tag_id", "") == "beloved_of_the_court"
                            && ReadInt(x, "reputation_value", 0) == 15)
                        && activePopularity.Any(x => ReadString(x, "tag_id", "") == "shunned_at_court"
                            && ReadInt(x, "reputation_value", 0) == -10)
                        && activePopularity.Count == 2,
                        "Only the highest dynamic tier per polarity is active, and positive and negative court standing may coexist.",
                        new Dictionary<string, object>
                        {
                            ["metric"] = popularityMetric, ["active"] = activePopularity
                        });
                    ReputationStore.UpdateRelationshipPairChemistry2(connection);
                    bool feedbackChanged = RecomputeCourtPopularityReputations(connection,
                        campaignId, "main", "court_subject", 21d, "court_popularity_no_feedback");
                    Dictionary<string, object> feedbackMetric = ReputationStore.SelectCourtDynamicReputationMetrics2(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    add("court_popularity_ignores_social_feedback",
                        !feedbackChanged
                        && ReadInt(feedbackMetric, "positive_count", 0) == 30
                        && ReadInt(feedbackMetric, "negative_count", 0) == 20,
                        "Popularity derives from directional underlying affinity and never feeds its own effective social modifiers back into the count.",
                        feedbackMetric);

                    foreach (Dictionary<string, object> hero in new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["heroStringId"]="court_male",["name"]="Court Male",["isAlive"]=true,
                            ["isAdult"]=true,["isLord"]=true,["occupation"]="Lord",["isFemale"]=false,
                            ["kingdomId"]="kingdom_court",["clanTier"]=3
                        },
                        new Dictionary<string, object>
                        {
                            ["heroStringId"]="court_female",["name"]="Court Female",["isAlive"]=true,
                            ["isAdult"]=true,["isLord"]=true,["occupation"]="Lord",["isFemale"]=true,
                            ["kingdomId"]="kingdom_court",["clanTier"]=3
                        }
                    }) CharactersService.UpsertIdentityRosterHero(connection, hero, ts);

                    Func<string, string, string, double, bool, Dictionary<string, object>> favored =
                        (subjectId, linkedId, linkedName, day, promote) =>
                            RegisterSocialOccurrence(connection, campaignId, new Dictionary<string, object>
                            {
                                ["timelineId"] = "main", ["archetypeId"] = "ruler_favoring_presence",
                                ["threadKey"] = subjectId + "|ruler_favoring|" + linkedId,
                                ["sourceEventId"] = subjectId + "|" + linkedId + "|" + day.ToString(CultureInfo.InvariantCulture),
                                ["worldDay"] = day, ["forceExposure"] = true, ["forcePromotion"] = promote,
                                ["provenanceSummary"] = linkedName + " showed recorded favor toward the subject.",
                                ["participants"] = new List<object>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["subjectId"] = subjectId, ["role"] = "ruler", ["clanTier"] = 3,
                                        ["isAlive"] = true, ["isAdult"] = true,
                                        ["linkedHeroId"] = linkedId, ["linkedHeroName"] = linkedName
                                    }
                                }
                            });
                    favored("court_subject", "court_male", "Court Male", 22d, false);
                    favored("court_subject", "court_male", "Court Male", 23d, true);
                    favored("court_subject", "court_female", "Court Female", 22d, false);
                    favored("court_subject", "court_female", "Court Female", 23d, true);
                    List<Dictionary<string, object>> favoredReputations = ReputationStore.SelectCharacterReputations24(connection, new Dictionary<string, object> { ["campaign"] = campaignId });
                    add("court_favored_by_parameterized",
                        favoredReputations.Count == 2
                        && favoredReputations.All(x => ReadInt(x, "reputation_value", 0) == -10)
                        && favoredReputations.Any(x => ReadString(x, "snapshot_json", "").Contains("Court Male"))
                        && favoredReputations.Any(x => ReadString(x, "snapshot_json", "").Contains("Court Female")),
                        "Ruler Favoring promotion is independently streaked and stored for each favorite while Public Standing collapses its global cost.",
                        favoredReputations);

                    SetCourtDynamicReputation(connection, campaignId, "main", "court_subject",
                        "dynasty_secure", true, 3, 24d, "dynasty_self_test", "",
                        "Three publicly acknowledged living children secure the subject's dynasty.",
                        new Dictionary<string, object> { ["livingAcknowledgedChildCount"] = 3 });
                    Dictionary<string, object> dynastyRow = ReputationStore.SelectCharacterReputations25(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    SetCourtDynamicReputation(connection, campaignId, "main", "court_subject",
                        "dynasty_secure", false, 0, 25d, "dynasty_self_test_lost", "",
                        "No publicly acknowledged living child currently secures the subject's dynasty.",
                        new Dictionary<string, object> { ["livingAcknowledgedChildCount"] = 0 });
                    Dictionary<string, object> dynastyLost = ReputationStore.SelectCharacterReputations25(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    add("court_dynasty_dynamic_value",
                        ReadInt(dynastyRow, "reputation_value", 0) == 3
                        && ReadString(dynastyRow, "status", "") == "active"
                        && ReadString(dynastyLost, "status", "") == "derived_requirement_lost",
                        "Dynasty Secure is one dynamic relationship point per living acknowledged child and disappears immediately when the requirement is lost.",
                        new Dictionary<string, object> { ["active"] = dynastyRow, ["lost"] = dynastyLost });

                    Dictionary<string, object> judgmentCatalog = ReadActiveSocialCatalog(connection);
                    List<object> judgmentResults = new List<object>();
                    int judgmentIndex = 0;
                    foreach (string judgmentTagId in NobleJudgmentDirectTagIds)
                    {
                        string judgmentEventId = "noble_judgment_event_"
                            + (judgmentIndex++).ToString(CultureInfo.InvariantCulture);
                        bool handled = ApplyCourtDynamicSocialOutcome(connection,
                            campaignId, "main", "court_subject",
                            new Dictionary<string, object>
                            {
                                ["dynamicReputationTagId"] = judgmentTagId,
                                ["dynamicActive"] = true,
                                ["dynamicValue"] = 4,
                                ["dynamicDescription"] = "A formal noble judgment established this reputation.",
                                ["directReputationProducer"] = "noble_judgment",
                                ["provenanceSummary"] = "The ruler entered a formal judgment.",
                                ["dynamicEvidence"] = new Dictionary<string, object>
                                {
                                    ["matterId"] = "noble_judgment_self_test",
                                    ["sourceCorrelationId"] = "noble_judgment_self_test|judgment|court_subject|"
                                        + judgmentTagId
                                }
                            }, judgmentEventId, 25.5d,
                            out Dictionary<string, object> judgmentResult,
                            judgmentCatalog);
                        judgmentResults.Add(new Dictionary<string, object>
                        {
                            ["tagId"] = judgmentTagId,
                            ["handled"] = handled,
                            ["result"] = judgmentResult
                        });
                    }
                    bool reaffirmed = ApplyCourtDynamicSocialOutcome(connection,
                        campaignId, "main", "court_subject",
                        new Dictionary<string, object>
                        {
                            ["dynamicReputationTagId"] = "incompetent",
                            ["dynamicActive"] = true,
                            ["dynamicValue"] = 4,
                            ["dynamicDescription"] = "A formal noble judgment established this reputation.",
                            ["directReputationProducer"] = "noble_judgment",
                            ["provenanceSummary"] = "The ruler reaffirmed the formal judgment.",
                            ["dynamicEvidence"] = new Dictionary<string, object>
                            {
                                ["matterId"] = "noble_judgment_self_test_reaffirmed",
                                ["sourceCorrelationId"] = "noble_judgment_self_test_reaffirmed|judgment|court_subject|incompetent"
                            }
                        }, "noble_judgment_event_reaffirmed", 25.6d,
                        out Dictionary<string, object> reaffirmedResult,
                        judgmentCatalog);
                    List<Dictionary<string, object>> judgmentRows = ReputationStore.SelectCharacterReputations26(connection, new Dictionary<string, object> { ["campaign"] = campaignId });
                    Dictionary<string, object> reaffirmedActivation = ReputationStore.SelectCharacterReputations27(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    Dictionary<string, object> reaffirmedEvidence = TryParseJsonObject(
                        ReadString(reaffirmedActivation, "evidence_json", "{}"))
                        ?? new Dictionary<string, object>();
                    string reaffirmedCorrelation = ReadString(CharactersService.DictionaryOrDefault(
                        reaffirmedEvidence, "dynamicEvidence", new Dictionary<string, object>()),
                        "sourceCorrelationId", "");
                    bool rejectedUnauthorizedProducer = ApplyCourtDynamicSocialOutcome(connection,
                        campaignId, "main", "unauthorized_judgment_subject",
                        new Dictionary<string, object>
                        {
                            ["dynamicReputationTagId"] = "flirt",
                            ["dynamicActive"] = true,
                            ["dynamicValue"] = -5,
                            ["dynamicDescription"] = "This must not be created by a noble judgment.",
                            ["directReputationProducer"] = "noble_judgment",
                            ["provenanceSummary"] = "Unauthorized direct producer guard self-test."
                        }, "noble_judgment_event_unauthorized", 25.7d,
                        out Dictionary<string, object> unauthorizedResult,
                        judgmentCatalog)
                        && !ReadBool(unauthorizedResult, "changed", false);
                    Dictionary<string, object> unauthorizedRow = ReputationStore.SelectCharacterReputations28(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    add("court_noble_judgment_direct_reputation_tags",
                        judgmentResults.OfType<Dictionary<string, object>>().All(x =>
                            ReadBool(x, "handled", false)
                            && ReadBool(CharactersService.DictionaryOrDefault(x, "result",
                                new Dictionary<string, object>()), "changed", false))
                        && judgmentRows.Count == NobleJudgmentDirectTagIds.Length
                        && judgmentRows.All(x => ReadString(x, "status", "") == "active")
                        && reaffirmed
                        && ReadBool(reaffirmedResult, "changed", false)
                        && ReadString(reaffirmedActivation, "source_event_id", "")
                            == "noble_judgment_event_reaffirmed"
                        && reaffirmedCorrelation
                            == "noble_judgment_self_test_reaffirmed|judgment|court_subject|incompetent"
                        && rejectedUnauthorizedProducer
                        && unauthorizedRow == null,
                        "Every docket-applicable reputation accepts only the explicit noble-judgment producer, and a repeated judgment refreshes exact activation provenance.",
                        new Dictionary<string, object>
                        {
                            ["initial"] = judgmentResults,
                            ["rows"] = judgmentRows,
                            ["reaffirmed"] = reaffirmedResult,
                            ["activation"] = reaffirmedActivation,
                            ["activationSourceCorrelationId"] = reaffirmedCorrelation,
                            ["unauthorizedResult"] = unauthorizedResult,
                            ["unauthorizedRow"] = unauthorizedRow
                        });

                    const string ingestJudgmentEventId =
                        "noble_judgment_world_history_ingest_self_test";
                    const string ingestJudgmentCorrelation =
                        "noble_judgment_ingest_self_test|judgment|court_ingest_subject|incompetent";
                    Dictionary<string, object> ingestJudgment =
                        WorldSimulationService.WorldHistoryIngestBatchApi(new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["timelineId"] = "main",
                            ["clientId"] = "rumor-self-test",
                            ["historyCompleteFromWorldDay"] = 0d,
                            ["events"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["eventId"] = ingestJudgmentEventId,
                                    ["sequence"] = 26001L,
                                    ["worldDay"] = 25.8d,
                                    ["eventType"] = "social_outcome",
                                    ["phase"] = "completed",
                                    ["category"] = "social",
                                    ["correlationId"] = ingestJudgmentCorrelation,
                                    ["summary"] = "The ruler entered a formal judgment through World History.",
                                    ["source"] = "reign_social",
                                    ["isComplete"] = true,
                                    ["entities"] = new List<object>(),
                                    ["payload"] = new Dictionary<string, object>
                                    {
                                        ["subjectId"] = "court_ingest_subject",
                                        ["role"] = "derived",
                                        ["dynamicReputationTagId"] = "incompetent",
                                        ["dynamicActive"] = true,
                                        ["dynamicValue"] = 3,
                                        ["dynamicDescription"] = "A formal noble judgment established this reputation.",
                                        ["directReputationProducer"] = "noble_judgment",
                                        ["sourceEventId"] = ingestJudgmentCorrelation,
                                        ["provenanceSummary"] = "The ruler entered a formal judgment through World History.",
                                        ["dynamicEvidence"] = new Dictionary<string, object>
                                        {
                                            ["matterId"] = "noble_judgment_ingest_self_test",
                                            ["sourceCorrelationId"] = ingestJudgmentCorrelation
                                        }
                                    }
                                }
                            }
                        });
                    Dictionary<string, object> ingestJudgmentProfile =
                        SocialCharacterStatusApi(new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["timelineId"] = "main",
                            ["subjectId"] = "court_ingest_subject",
                            ["worldDay"] = 25.8d
                        });
                    Dictionary<string, object> ingestJudgmentReputation =
                        ReadDictionaryList(ingestJudgmentProfile, "reputations")
                            .FirstOrDefault(row => string.Equals(ReadString(row,
                                    "tag_id", ""), "incompetent",
                                StringComparison.OrdinalIgnoreCase));
                    add("court_direct_judgment_acknowledged_with_world_history_ingest",
                        ReadBool(ingestJudgment, "ok", false)
                        && ReadInt(ingestJudgment,
                            "directSocialOutcomesAcknowledged", 0) == 1
                        && ingestJudgmentReputation != null
                        && ReadString(ingestJudgmentReputation,
                            "activation_source_event_id", "")
                            == ingestJudgmentEventId
                        && ReadString(ingestJudgmentReputation,
                            "activation_source_correlation_id", "")
                            == ingestJudgmentCorrelation,
                        "World History acknowledges a direct noble-judgment outcome only after its exact authoritative reputation activation is queryable.",
                        new Dictionary<string, object>
                        {
                            ["ingest"] = ingestJudgment,
                            ["reputation"] = ingestJudgmentReputation
                        });

                    social("the_unchaste", "court_male", "parent", "unchaste_male", 26d, true);
                    social("the_unchaste", "court_female", "parent", "unchaste_female", 26d, true);
                    int maleUnchaste = ReadInt(ReputationStore.SelectCharacterReputations29(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault(),
                        "reputation_value", 0);
                    int femaleUnchaste = ReadInt(ReputationStore.SelectCharacterReputations30(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault(),
                        "reputation_value", 0);
                    add("court_unchaste_gender_values",
                        maleUnchaste == -5 && femaleUnchaste == -50,
                        "Public illegitimacy snapshots the configured male and female court penalties.",
                        new Dictionary<string, object> { ["male"] = maleUnchaste, ["female"] = femaleUnchaste });

                    social("affair", "court_male", "unmarried", "shared_promiscuous_affair", 26.5d, false);
                    Dictionary<string, object> sharedFlirtResult = social("promiscuous_flirtation",
                        "court_male", "speaker", "shared_promiscuous_flirt", 27.5d, false);
                    Dictionary<string, object> sharedPromiscuousStreak = ReputationStore.SelectSharedTagExposureStreaks(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    add("court_promiscuous_shared_cross_producer_streak",
                        ReadInt(sharedPromiscuousStreak, "streak_count", 0) == 2
                        && ReadDictionaryList(sharedFlirtResult, "sharedPromotionResults").Any(x =>
                            ReadString(x, "tagId", "") == "promiscuous"
                            && ReadInt(x, "streakCount", 0) == 2
                            && Math.Abs(ReadDouble(x, "promotionChance", 0d) - 0.30d) < 0.000001d),
                        "Exposed affairs and validated multi-target flirtation advance one shared character/Promiscuous promotion streak.",
                        new Dictionary<string, object>
                        {
                            ["streak"] = sharedPromiscuousStreak,
                            ["flirtResult"] = sharedFlirtResult
                        });

                    Dictionary<string, object> signalSession = new Dictionary<string, object>
                    {
                        ["session_id"] = "court_signal_unvalidated", ["channel"] = "in_person",
                        ["npc_id"] = "court_female", ["player_id"] = "player_subject",
                        ["payload_json"] = "{\"timelineId\":\"main\"}"
                    };
                    List<Dictionary<string, object>> signalTurns = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["turn_id"]="signal_player",["exchange_id"]="signal_exchange",["role"]="player",
                            ["payload_json"]="{\"socialSignals\":[{\"signalId\":\"flirt_test\",\"type\":\"flirtation\",\"validated\":true,\"confidence\":\"explicit\",\"validationSource\":\"untrusted_text_parser\",\"speakerHeroId\":\"player_subject\",\"targetHeroId\":\"court_female\"}]}"
                        },
                        new Dictionary<string, object>
                        {
                            ["turn_id"]="signal_npc",["exchange_id"]="signal_exchange",["role"]="npc",
                            ["payload_json"]="{}"
                        }
                    };
                    ProcessCompletedConversationCourtStanding(campaignId, signalSession, signalTurns, 27d, false);
                    int rejectedSignals = ReadInt(ReputationStore.CountCourtConversationSignalReceipts(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault(), "count", -1);
                    signalSession["session_id"] = "court_signal_validated";
                    signalTurns[0]["turn_id"] = "signal_player_validated";
                    signalTurns[0]["payload_json"] = "{\"socialSignals\":[{\"signalId\":\"flirt_test_validated\",\"type\":\"flirtation\",\"validated\":true,\"confidence\":\"explicit\",\"validationSource\":\"reign_conversation_engine\",\"speakerHeroId\":\"player_subject\",\"targetHeroId\":\"court_female\"}]}";
                    ProcessCompletedConversationCourtStanding(campaignId, signalSession, signalTurns, 28d, false);
                    int acceptedSignals = ReadInt(ReputationStore.CountCourtConversationSignalReceipts2(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault(), "count", -1);
                    add("court_flirt_requires_validated_signal",
                        rejectedSignals == 0 && acceptedSignals == 1,
                        "Raw text or untrusted parser output cannot create Flirt evidence; only the explicit structured Reign conversation-engine contract is accepted.",
                        new Dictionary<string, object>
                        {
                            ["rejectedReceiptCount"] = rejectedSignals,
                            ["acceptedReceiptCount"] = acceptedSignals
                        });

                    var singleSpeakerTurns = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["exchange_id"] = "family_one", ["role"] = "player", ["text"] = "Tell me what you found." },
                        new Dictionary<string, object> { ["exchange_id"] = "family_one", ["role"] = "npc", ["speaker_id"] = "lover", ["text"] = "I will." }
                    };
                    var groupSpeakerTurns = new List<Dictionary<string, object>>(singleSpeakerTurns)
                    {
                        new Dictionary<string, object> { ["exchange_id"] = "family_one", ["role"] = "npc", ["speaker_id"] = "married", ["text"] = "I heard it too." }
                    };
                    add("court_family_chambers_party_chat_social_boundary",
                        IsCourtStandingConversationChannel("in_person")
                        && IsCourtStandingConversationChannel("party_chat")
                        && !IsCourtStandingConversationChannel("correspondence")
                        && SingleQualifyingFavorNpcId(singleSpeakerTurns, new[] { "family_one" }) == "lover"
                        && string.IsNullOrWhiteSpace(SingleQualifyingFavorNpcId(groupSpeakerTurns, new[] { "family_one" })),
                        "Family Chambers party chat participates in court-social processing, while only a one-NPC conversation can advance that NPC's ruler-favor counter.",
                        new Dictionary<string, object>
                        {
                            ["singleSpeaker"] = SingleQualifyingFavorNpcId(singleSpeakerTurns, new[] { "family_one" }),
                            ["groupSpeaker"] = SingleQualifyingFavorNpcId(groupSpeakerTurns, new[] { "family_one" })
                        });

                    foreach (Dictionary<string, object> hero in new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["heroStringId"]="group_intro_alpha",["name"]="Lady Alpha",["isAlive"]=true,
                            ["isAdult"]=true,["isLord"]=true,["occupation"]="Lord",["isFemale"]=true,
                            ["kingdomId"]="kingdom_court",["clanTier"]=3
                        },
                        new Dictionary<string, object>
                        {
                            ["heroStringId"]="group_intro_beta",["name"]="Lord Beta",["isAlive"]=true,
                            ["isAdult"]=true,["isLord"]=true,["occupation"]="Lord",["isFemale"]=false,
                            ["kingdomId"]="kingdom_court",["clanTier"]=3
                        }
                    }) CharactersService.UpsertIdentityRosterHero(connection, hero, ts);
                    ReputationStore.DeleteAcquaintances(connection);
                    var explicitIntroductionTurns = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["exchange_id"]="family_intro",["role"]="player",["speaker_id"]="player_subject",["text"]="Lady Alpha, meet Lord Beta." },
                        new Dictionary<string, object> { ["exchange_id"]="family_intro",["role"]="npc",["speaker_id"]="group_intro_alpha",["text"]="I receive the introduction." },
                        new Dictionary<string, object> { ["exchange_id"]="family_intro",["role"]="npc",["speaker_id"]="group_intro_beta",["text"]="And I return the courtesy." }
                    };
                    List<Dictionary<string, object>> explicitIntroductions =
                        ProcessExplicitGroupConversationIntroductions(connection, "party_chat",
                            "family_group_intro", explicitIntroductionTurns, 29d);
                    Dictionary<string, object> alphaKnowsBeta =
                        CharactersService.ReadStoredAcquaintance(connection, "group_intro_alpha", "group_intro_beta");
                    Dictionary<string, object> betaKnowsAlpha =
                        CharactersService.ReadStoredAcquaintance(connection, "group_intro_beta", "group_intro_alpha");
                    add("court_family_chambers_explicit_group_introduction",
                        explicitIntroductions.Count == 2
                        && CharactersService.IdentityStateVerified(alphaKnowsBeta)
                        && CharactersService.IdentityStateVerified(betaKnowsAlpha)
                        && ReadString(alphaKnowsBeta, "verification_source", "") == "explicit_group_introduction"
                        && ReadString(betaKnowsAlpha, "verification_source", "") == "explicit_group_introduction"
                        && ProcessExplicitGroupConversationIntroductions(connection, "in_person",
                            "ordinary_audience", explicitIntroductionTurns, 29d).Count == 0,
                        "An explicit, named Family Chambers introduction verifies both adult participants, while ordinary one-to-one audiences do not use the group rule.",
                        new Dictionary<string, object>
                        {
                            ["writes"] = explicitIntroductions,
                            ["alphaKnowsBeta"] = alphaKnowsBeta,
                            ["betaKnowsAlpha"] = betaKnowsAlpha
                        });

                    Dictionary<string, object> sameDayA = social("strong_captain", "player_subject", "commander", "same_day_a", 30d, true);
                    Dictionary<string, object> sameDayB = social("strong_captain", "player_subject", "commander", "same_day_b", 30d, false);
                    add("player_subject_and_distinct_same_day_outcomes",
                        ReadBool(sameDayA, "exposed", false) && ReadBool(sameDayB, "exposed", false)
                        && ReputationStore.SelectRumorOccurrences11(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).Count == 2,
                        "Living adult players are eligible, and independently sourced combat outcomes on one day remain distinct.",
                        new Dictionary<string, object> { ["first"] = sameDayA, ["second"] = sameDayB });

                    // Counterevidence only applies to an established active
                    // Reputation.  The same-day outcomes above deliberately
                    // test rumor-source deduplication and do not promise that
                    // the promotion roll succeeds, so establish the fixture's
                    // precondition explicitly instead of depending on a
                    // coincidental earlier catalog/test state.
                    ReputationStore.UpsertCharacterReputations4(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["revision"] = BuiltInSocialCatalogRevision,
                            ["ts"] = ts
                        });
                    Dictionary<string, object> counterFixtureState = ReputationStore.SelectCharacterReputations31(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();

                    for (int count = 1; count <= 3; count++)
                    {
                        double requiredRoll = count * 0.05d;
                        string counterEvent = Enumerable.Range(0, 10000)
                            .Select(index => "counter_" + count + "_" + index)
                            .First(candidate => DeterministicSocialRoll(string.Join("|", campaignId, "main",
                                "player_subject", "strong_captain", candidate, "counter")) >= requiredRoll);
                        ApplySocialCounterevidence(connection, campaignId, "main", "player_subject",
                            "weak_captain", new List<string>(), counterEvent, 30d + count);
                    }
                    Dictionary<string, object> counterState = ReputationStore.SelectReputationCounterevidence2(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    ApplySocialCounterevidence(connection, campaignId, "main", "player_subject",
                        "strong_captain", new List<string>(), "matching_reset", 34d);
                    Dictionary<string, object> resetState = ReputationStore.SelectReputationCounterevidence3(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).FirstOrDefault();
                    add("counterevidence_stack_and_reset",
                        ReadInt(counterState, "consecutive_count", 0) == 3
                        && Math.Abs(ReadDouble(counterState, "last_chance", 0d) - 0.15d) < 0.000001d
                        && ReadInt(resetState, "consecutive_count", -1) == 0,
                        "Opposite outcomes advance removal chances by five percentage points and matching outcomes reset the stack. "
                        + "Fixture=" + Json.Serialize(counterFixtureState) + ". "
                        + "Observed before=" + Json.Serialize(counterState)
                        + ", after=" + Json.Serialize(resetState) + ".",
                        new Dictionary<string, object> { ["beforeReset"] = counterState, ["afterReset"] = resetState });

                    social("tactician", "player_subject", "commander", "derived_tactician", 35d, true);
                    Dictionary<string, object> directReputationOccurrence =
                        social("war_crowned", "player_subject", "ruler", "derived_war", 36d, true);
                    string directReputationOccurrenceId = ReadString(
                        directReputationOccurrence, "occurrenceId", "");
                    Dictionary<string, object> finalizedDirectOccurrence =
                        ReputationStore.SelectRumorOccurrences12(connection, new Dictionary<string, object>
                        {
                            ["id"] = directReputationOccurrenceId
                        }).FirstOrDefault();
                    add("zero_duration_direct_reputation_finalizes_occurrence",
                        ReadString(directReputationOccurrence, "occurrenceStatus", "") == "expired"
                        && ReadString(finalizedDirectOccurrence, "status", "") == "expired"
                        && ReputationStore.SelectCharacterReputations32(connection, new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId
                            }).Any(),
                        "Zero-duration direct-reputation outcomes retain their durable reputation but do not remain as phantom active rumors.",
                        new Dictionary<string, object>
                        {
                            ["result"] = directReputationOccurrence,
                            ["occurrence"] = finalizedDirectOccurrence
                        });
                    social("realm_builder", "player_subject", "ruler", "derived_realm", 37d, true);
                    social("unifier_of_the_realm", "player_subject", "ruler", "derived_unifier", 38d, true);
                    social("fair_hand_of_the_crown", "player_subject", "ruler", "derived_fair", 39d, true);
                    RecomputeDerivedSocialReputations(connection, campaignId, "main", "player_subject", 39d, "derived_check");
                    add("derived_combat_and_governance_reputations",
                        ReputationStore.SelectCharacterReputations33(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).Any()
                        && ReputationStore.SelectCharacterReputations34(connection, new Dictionary<string, object> { ["campaign"] = campaignId }).Any(),
                        "Three base combat Reputations derive Battlemaster, while three governance pillars derive Steward of the Realm.", null);

                    Dictionary<string, object> manualRumor = CharacterEditorSocialRumorAddApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["timelineId"] = "main", ["heroStringId"] = "player_subject",
                        ["archetypeId"] = "weak_captain", ["role"] = "commander",
                        ["incidentDescription"] = "Witnesses blamed the commander for a poorly handled defeat.",
                        ["idempotencyKey"] = "manual_rumor_fixture"
                    });
                    Dictionary<string, object> manualRumorReplay = CharacterEditorSocialRumorAddApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["timelineId"] = "main", ["heroStringId"] = "player_subject",
                        ["archetypeId"] = "weak_captain", ["role"] = "commander",
                        ["incidentDescription"] = "Witnesses blamed the commander for a poorly handled defeat.",
                        ["idempotencyKey"] = "manual_rumor_fixture"
                    });
                    Dictionary<string, object> manualReputation = CharacterEditorSocialReputationAddApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["timelineId"] = "main", ["heroStringId"] = "player_subject",
                        ["tagId"] = "fallen_challenger",
                        ["incidentDescription"] = "The challenger was decisively defeated before assembled witnesses.",
                        ["idempotencyKey"] = "manual_reputation_fixture"
                    });
                    Dictionary<string, object> socialProjection = CharacterSocialStandingProjection(connection, campaignId, "player_subject");
                    List<Dictionary<string, object>> projectedRumors = ReadDictionaryList(socialProjection, "activeRumors");
                    List<Dictionary<string, object>> projectedReputations = ReadDictionaryList(socialProjection, "activeReputations");
                    add("character_profile_social_projection",
                        ReadBool(manualRumor, "ok", false) && ReadBool(manualRumorReplay, "duplicate", false)
                        && ReadBool(manualReputation, "ok", false)
                        && projectedRumors.Any(row => ReadString(row, "label", "") == "Weak Captain"
                            && ReadString(row, "incidentDescription", "").Contains("poorly handled defeat"))
                        && projectedReputations.Any(row => ReadString(row, "label", "") == "The Fallen Challenger"
                            && ReadString(row, "reason_status", "") == "recorded"),
                        "Character profiles list every active social occurrence, manual Rumors are idempotent, and direct Reputations retain their factual establishing event.",
                        new Dictionary<string, object> { ["rumors"] = projectedRumors.Count, ["reputations"] = projectedReputations.Count });
                    add("reputation_incident_is_authoritative",
                        !ReputationStore.SelectReputationReasonJobs(connection).Any()
                        && ReputationStore.SelectCharacterReputations35(connection).Any(),
                        "A newly established Reputation stores its triggering event verbatim and creates no background LLM reason job.", null);
                    Dictionary<string, object> choices = ReadDictionary(socialProjection, "catalogChoices") ?? new Dictionary<string, object>();
                    add("manual_catalog_excludes_derived",
                        !ReadDictionaryList(choices, "reputations").Any(row =>
                            new[] { "Battlemaster", "The Frail", "Steward of the Realm", "The Ruinous Crown" }
                                .Contains(ReadString(row, "label", ""), StringComparer.OrdinalIgnoreCase)),
                        "The profile add dialog offers enabled base Reputations but never derived Reputations.", null);

                    ReputationStore.Begin(connection);
                    ReputationStore.InsertWorldHistoryEvents(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["created"] = DateTime.UtcNow.ToString("o")
                        });
                    for (int index = 0; index < 205; index++)
                    {
                        string eventId = "social_batch_" + index.ToString(CultureInfo.InvariantCulture);
                        ReputationStore.InsertWorldHistoryEvents2(connection, new Dictionary<string, object>
                            {
                                ["event"] = eventId, ["campaign"] = campaignId,
                                ["sequence"] = 9001 + index,
                                ["payload"] = "{\"subjectId\":\"player_subject\",\"archetypeId\":\"\",\"counterOnlyTagIds\":[]}",
                                ["created"] = DateTime.UtcNow.ToString("o")
                            });
                    }
                    ReputationStore.Commit(connection);
                    ProcessPendingSocialWorldHistoryOutcomeBatch(campaignId, "main", campaignId + "|main|batch_1");
                    ProcessPendingSocialWorldHistoryOutcomeBatch(campaignId, "main", campaignId + "|main|batch_2");
                    ProcessPendingSocialWorldHistoryOutcomeBatch(campaignId, "main", campaignId + "|main|batch_3");
                    int completedBatchOutcomes = ReadInt(ReputationStore.CountSocialOutcomeReceipts(connection).FirstOrDefault(), "count", 0);
                    Dictionary<string, object> poisonReceipt = ReputationStore.SelectSocialOutcomeReceipts5(connection).FirstOrDefault();
                    add("social_outcome_batched_drain_and_poison_isolation",
                        completedBatchOutcomes == 205
                        && ReadInt(poisonReceipt, "completed", 0) == -1
                        && ReadInt(poisonReceipt, "attempt_count", 0) == 3
                        && !HasPendingSocialWorldHistoryOutcomes(campaignId, "main"),
                        "Social outcomes drain in bounded batches, a malformed event cannot block later hooks, and exhausted failures terminate visibly after three attempts.",
                        new Dictionary<string, object>
                        {
                            ["completed"] = completedBatchOutcomes,
                            ["poisonAttempts"] = ReadInt(poisonReceipt, "attempt_count", 0)
                        });

                    ReputationStore.Begin(connection);
                    for (int index = 0; index < 1000; index++)
                    {
                        string bulkId = "bulk_" + index.ToString(CultureInfo.InvariantCulture);
                        ReputationStore.InsertRumorOccurrences(connection, new Dictionary<string, object> { ["id"] = bulkId, ["campaign"] = campaignId, ["ts"] = ts });
                        ReputationStore.InsertRumorSubjectTags(connection, new Dictionary<string, object> { ["id"] = bulkId, ["tag"] = "bulk_tag_" + index.ToString(CultureInfo.InvariantCulture), ["ts"] = ts });
                    }
                    ReputationStore.Commit(connection);
                    Dictionary<string, object> bulkObserver = new Dictionary<string, object>
                        { ["hero_id"]="bulk_observer",["kingdom_id"]="kingdom_a" };
                    Dictionary<string, object> bulkSubject = new Dictionary<string, object>
                        { ["hero_id"]="bulk_subject",["kingdom_id"]="kingdom_a",["current_charm"]=0 };
                    System.Diagnostics.Stopwatch bulkTimer = System.Diagnostics.Stopwatch.StartNew();
                    int bulkModifier = CalculateObserverSocialModifier(connection, campaignId, "main",
                        bulkObserver, bulkSubject, 25d, out int bulkRumorTotal, out int bulkReputationTotal);
                    bulkTimer.Stop();
                    add("thousand_rumor_indexed_projection",
                        bulkRumorTotal == -1000 && bulkReputationTotal == 0 && bulkModifier == -1000
                        && bulkTimer.ElapsedMilliseconds < 5000,
                        "One indexed active-Rumor query evaluates 1,000 distinct character-owned entries without LLM work; the social sum remains unclamped until effective affinity.",
                        new Dictionary<string, object> { ["rumorCount"]=1000,["rumorTotal"]=bulkRumorTotal,["modifier"]=bulkModifier,["elapsedMs"]=bulkTimer.ElapsedMilliseconds });

                    HashSet<string> legacy = new HashSet<string>(ReputationStore.SelectSqliteMaster4(connection).Select(x => ReadString(x, "name", "")), StringComparer.OrdinalIgnoreCase);
                    add("legacy_propagation_storage_absent",
                        new[] { "rumor_chains", "rumor_receipts", "rumor_delivery_runs", "rumor_source_rolls", "reputation_profiles", "reputation_axes" }.All(x => !legacy.Contains(x)),
                        "Fresh schema creates no propagation, receipt, acquisition-run, or five-axis reputation tables.", null);
                    RunWhoremongerSelfTests(connection, campaignId, add);
                }
            }
            finally
            {
                try { if (System.IO.Directory.Exists(campaignPath)) System.IO.Directory.Delete(campaignPath, true); } catch { }
            }
            return results;
        }
#endif
    }
}
