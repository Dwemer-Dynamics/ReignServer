using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace ReignLiveTest
{
    internal static partial class Program
    {
        private static Dictionary<string, object> QualificationPlanCoverage()
        {
            Dictionary<string, int> required = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["structural_pipeline"] = 40,
                ["identity_evidence"] = 40,
                ["identity_role_authority"] = 40,
                ["short_cross_scene_memory"] = 40,
                ["long_term_memory"] = 40,
                ["dynamic_characteristics"] = 40,
                ["shared_relationship_history"] = 40,
                ["group_awareness"] = 40,
                ["world_local_knowledge"] = 40,
                ["lie_relationship"] = 40,
                ["clan_tier_recognition"] = 30,
                ["manipulation_capabilities"] = 40,
                ["personality_consistency"] = 40,
                ["detailed_factual_accuracy"] = 40,
                ["performance_resilience"] = 40
            };
            Dictionary<string, int> planned = required.Keys.ToDictionary(
                key => key, key => 0, StringComparer.OrdinalIgnoreCase);
            int dynamicRecallCommands = 0;
            Action<Dictionary<string, object>, int> add = (step, minimumReplies) =>
            {
                if (step == null
                    || !String(step, "operation").Equals("send", StringComparison.OrdinalIgnoreCase))
                    return;
                if (step.TryGetValue("readinessCategories", out object rawCategories)
                    && rawCategories is IEnumerable<string> categories)
                {
                    foreach (string category in categories.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (planned.ContainsKey(category))
                            planned[category] += minimumReplies;
                    }
                }
                Dictionary<string, object> assertions = ReadObject(step, "assertions");
                if (assertions.TryGetValue("requiresDynamicCharacteristicRecall", out object rawRecall)
                    && Convert.ToBoolean(rawRecall))
                    dynamicRecallCommands++;
            };

            for (int focalIndex = 0; focalIndex < 5; focalIndex++)
                for (int scene = 0; scene < 12; scene++)
                {
                    for (int turn = 0; turn < 4; turn++)
                        add(IndividualProbe(focalIndex, scene, turn), 1);
                }
            for (int probe = 0; probe < 30; probe++)
                add(ClanTierRecognitionProbe(probe), 1);
            for (int probe = 0; probe < 40; probe++)
                add(ManipulationProbe(probe), 1);

            for (int seedIndex = 0; seedIndex < QualificationSeeds.Length; seedIndex++)
            {
                for (int scene = 0; scene < 7; scene++)
                {
                    int size = scene % 3 == 0 ? 2 : scene % 3 == 1 ? 3 : 5;
                    List<string> group = Enumerable.Range(0, size)
                        .Select(index => "party_" + index).ToList();
                    for (int turn = 0; turn < 2; turn++)
                        add(GroupProbe(seedIndex, scene, turn, group, "party_chat"), size);
                }
                for (int scene = 0; scene < 4; scene++)
                {
                    List<string> group = new List<string> { "social_0", "social_1", "social_2" };
                    for (int turn = 0; turn < 2; turn++)
                        add(GroupProbe(seedIndex, scene, turn, group, "social_event"), 3);
                }
            }
            List<string> relationshipPlanPair =
                new List<string> { "relationship_0", "relationship_1" };
            for (int pair = 0; pair < 5; pair++)
            {
                add(GroupRelationshipHistoryProbe(
                    relationshipPlanPair, "cold", false), 2);
                add(GroupRelationshipHistoryProbe(
                    relationshipPlanPair, "reuse", false), 2);
                add(GroupRelationshipHistoryProbe(
                    relationshipPlanPair, "transition", false), 2);
                add(GroupRelationshipHistoryProbe(
                    relationshipPlanPair, "reuse", false), 2);
            }
            for (int reload = 0; reload < 10; reload++)
                add(GroupRelationshipHistoryProbe(
                    relationshipPlanPair, "reuse", true), 2);
            for (int scene = 0; scene < 20; scene++)
                add(WildernessProbe(scene), 1);

            HashSet<int> socialTargetIndices = new HashSet<int>();
            const int minimumBroadPool = 25;
            for (int seedIndex = 0; seedIndex < QualificationSeeds.Length; seedIndex++)
                for (int scene = 0; scene < 4; scene++)
                    for (int offset = 0; offset < 3; offset++)
                        socialTargetIndices.Add((seedIndex * 7 + scene * 3 + offset) % minimumBroadPool);

            List<Dictionary<string, object>> categoriesResult = required.Select(entry =>
                new Dictionary<string, object>
                {
                    ["category"] = entry.Key,
                    ["plannedMinimumReplies"] = planned[entry.Key],
                    ["requiredReplies"] = entry.Value,
                    ["margin"] = planned[entry.Key] - entry.Value,
                    ["reachable"] = planned[entry.Key] >= entry.Value
                }).ToList();
            int plannedSceneClosuresBeforeTerminalGate =
                180 + 21 + 12 + 20 + 10 + 10;
            int plannedCheckpoints = 3 + 3 + 3 + 1;
            List<string> individualPromptTexts = Enumerable.Range(0, 48)
                .Select(selector => String(IndividualProbe(0, selector / 4, selector % 4), "text"))
                .ToList();
            bool reciprocalPromptContract = individualPromptTexts[0].IndexOf("roads are safe", StringComparison.OrdinalIgnoreCase) >= 0
                && individualPromptTexts[0].IndexOf("share as much as I ask", StringComparison.OrdinalIgnoreCase) >= 0
                && !individualPromptTexts.Any(text => text.IndexOf("more important confidence", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("I will then answer", StringComparison.OrdinalIgnoreCase) >= 0);
            List<Dictionary<string, object>> usageFixture = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["heroId"] = "heavily_used", ["priorDialogueLineCount"] = 40, ["priorMemorySourceCount"] = 0 },
                new Dictionary<string, object> { ["heroId"] = "memory_only", ["priorDialogueLineCount"] = 0, ["priorMemorySourceCount"] = 4 },
                new Dictionary<string, object> { ["heroId"] = "gift_used", ["priorDialogueLineCount"] = 0, ["priorMemorySourceCount"] = 0, ["priorGiftRelationshipEvidence"] = true },
                new Dictionary<string, object> { ["heroId"] = "fresh_b", ["priorDialogueLineCount"] = 0, ["priorMemorySourceCount"] = 0 },
                new Dictionary<string, object> { ["heroId"] = "fresh_a", ["priorDialogueLineCount"] = 0, ["priorMemorySourceCount"] = 0 }
            };
            List<Dictionary<string, object>> orderedUsageFixture = OrderQualificationTargetsByUsage(usageFixture, "fixture");
            bool leastUsedTargetContract = orderedUsageFixture.Count == 5
                && ReadLong(orderedUsageFixture[0], "priorConversationEvidenceCount", -1) == 0
                && ReadLong(orderedUsageFixture[1], "priorConversationEvidenceCount", -1) == 0
                && orderedUsageFixture.Skip(2).All(target =>
                    ReadLong(target, "priorConversationEvidenceCount", -1) > 0);
            bool ok = categoriesResult.All(row => Convert.ToBoolean(row["reachable"]))
                && dynamicRecallCommands >= 40
                && socialTargetIndices.Count >= 25
                && plannedSceneClosuresBeforeTerminalGate >= 100
                && plannedCheckpoints >= 10
                && reciprocalPromptContract
                && leastUsedTargetContract;
            return new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["schemaVersion"] = 1,
                ["categories"] = categoriesResult,
                ["plannedMinimumReplies"] = planned["structural_pipeline"],
                ["plannedDynamicCharacteristicCaptureOpportunities"] = planned["dynamic_characteristics"],
                ["plannedDynamicCharacteristicRecallCommands"] = dynamicRecallCommands,
                ["plannedDistinctBroadTargets"] = socialTargetIndices.Count,
                ["plannedSceneClosuresBeforeTerminalGate"] = plannedSceneClosuresBeforeTerminalGate,
                ["plannedCheckpoints"] = plannedCheckpoints,
                ["plannedReloads"] = plannedCheckpoints,
                ["plannedRollbackChecks"] = 1,
                ["plannedRelationshipHistoryPairs"] = 5,
                ["plannedRelationshipHistoryColdCommands"] = 5,
                ["plannedRelationshipHistoryTransitionReplies"] = 10,
                ["plannedRelationshipHistoryPersistenceReplies"] = 20,
                ["reciprocalIndividualPrompts"] = reciprocalPromptContract,
                ["leastUsedTargetSelection"] = leastUsedTargetContract,
                ["seedCount"] = QualificationSeeds.Length,
                ["modes"] = new[] { "individual_chat", "party_chat", "social_event", "wilderness_event" }
            };
        }

        private static List<Dictionary<string, object>> QualificationTargets(
            string campaignId,
            string mode,
            int limit,
            Dictionary<string, object> filters = null,
            bool includeHistory = true)
        {
            string targetRunId = "target-search-"
                + Guid.NewGuid().ToString("N");
            Dictionary<string, object> started =
                new Dictionary<string, object>();
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                Dictionary<string, object> runtime =
                    QualificationRuntimeWithHeartbeatGrace(campaignId);
                Dictionary<string, object> request =
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["gameInstanceId"] = RuntimeInstance(runtime),
                        ["runId"] = targetRunId,
                        ["mode"] = mode,
                        ["search"] = "",
                        ["limit"] = limit
                    };
                foreach (KeyValuePair<string, object> filter
                    in filters
                        ?? new Dictionary<string, object>())
                {
                    request[filter.Key] = filter.Value;
                }
                try
                {
                    // Target enumeration is game-side and normally completes in
                    // under a second, but a large preceding evidence/report write
                    // can hold the unified server request loop beyond the ordinary
                    // 90-second CLI timeout. The caller supplies a stable run ID,
                    // waits on the long-operation budget, and can therefore
                    // reconcile a late server acceptance without issuing a second
                    // search command.
                    started = PostWithTimeout(
                        "/tests/live/targets/search", request, 300);
                }
                catch (TaskCanceledException)
                {
                    started = new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["runId"] = targetRunId,
                        ["transportAmbiguous"] = true
                    };
                }
                if (IsOk(started)) break;
                string error = String(started, "error");
                if (attempt >= 3
                    || error.IndexOf(
                        "fresh loaded-game heartbeat",
                        StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidOperationException(
                        "Qualification target discovery failed for "
                        + mode + ": " + Json.Serialize(started));
                Thread.Sleep(2000);
            }
            Dictionary<string, object> completed = WaitForRun(
                campaignId,
                FirstNonEmpty(String(started, "runId"), targetRunId),
                "", 300, true);
            if (!IsOk(completed)
                || !String(completed, "status").Equals(
                    "completed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Qualification target discovery did not complete for "
                    + mode + ": " + Json.Serialize(completed));
            Dictionary<string, object> command = ReadObjects(completed, "commands").LastOrDefault()
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> targets = ReadObjects(ReadObject(command, "result"), "targets");
            if (includeHistory
                && string.Equals(
                    mode,
                    "individual_chat",
                    StringComparison.OrdinalIgnoreCase))
            {
                foreach (Dictionary<string, object> target in targets)
                {
                    Dictionary<string, object> history = Post("/dialogue/history", new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["heroStringId"] = String(target, "heroId"),
                        ["limit"] = 500
                    });
                    target["priorDialogueLineCount"] = IsOk(history) ? ReadObjects(history, "lines").Count : 500;
                }
            }
            return targets;
        }

        private static List<Dictionary<string, object>>
            QualificationSocialEvents(string campaignId, int limit)
        {
            Dictionary<string, object> runtime =
                QualificationRuntimeWithHeartbeatGrace(campaignId);
            Dictionary<string, object> started = Post(
                "/tests/live/targets/search",
                new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] =
                        RuntimeInstance(runtime),
                    ["mode"] = "social_event",
                    ["search"] = "",
                    ["limit"] = Math.Max(
                        1, Math.Min(2000, limit))
                });
            if (!IsOk(started))
                throw new InvalidOperationException(
                    "Qualification social-event discovery failed: "
                    + Json.Serialize(started));
            Dictionary<string, object> completed = WaitForRun(
                campaignId, String(started, "runId"), "",
                120, true);
            if (!IsOk(completed)
                || !String(completed, "status").Equals(
                    "completed",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Qualification social-event discovery did not complete: "
                    + Json.Serialize(completed));
            Dictionary<string, object> command =
                ReadObjects(completed, "commands")
                    .LastOrDefault()
                ?? new Dictionary<string, object>();
            return ReadObjects(
                ReadObject(command, "result"), "events");
        }

        private static ulong QualificationStableOrder(string value)
        {
            // FNV-1a is sufficient here: this is recorded scenario ordering, not a
            // security boundary. Avoid string.GetHashCode because it is process-randomized.
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            foreach (char ch in value ?? "")
            {
                hash ^= (byte)(ch & 0xff);
                hash *= prime;
                hash ^= (byte)(ch >> 8);
                hash *= prime;
            }
            return hash;
        }

        private static List<string> ReadRosterIds(Dictionary<string, object> roster, string key)
        {
            if (roster == null || !roster.TryGetValue(key, out object raw) || raw == null)
                return new List<string>();
            IEnumerable<object> values = raw is System.Collections.ArrayList list
                ? list.Cast<object>()
                : raw is object[] array
                    ? array
                    : Enumerable.Empty<object>();
            return values.Select(Convert.ToString)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<List<string>> ReadRosterPairs(
            Dictionary<string, object> roster,
            string key)
        {
            if (roster == null
                || !roster.TryGetValue(key, out object raw)
                || raw == null)
                return new List<List<string>>();
            IEnumerable<object> rows = raw is ArrayList list
                ? list.Cast<object>()
                : raw is object[] array
                    ? array
                    : Enumerable.Empty<object>();
            return rows.Select(row =>
                {
                    IEnumerable<object> values = row is ArrayList nested
                        ? nested.Cast<object>()
                        : row is object[] nestedArray
                            ? nestedArray
                            : Enumerable.Empty<object>();
                    return values.Select(Convert.ToString)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToList();
                })
                .Where(pair => pair.Count == 2)
                .ToList();
        }

        private static List<Dictionary<string, object>> OrderQualificationTargetsByUsage(
            IEnumerable<Dictionary<string, object>> targets, string qualificationId)
        {
            return (targets ?? Enumerable.Empty<Dictionary<string, object>>())
                .Where(target => !string.IsNullOrWhiteSpace(String(target, "heroId")))
                .GroupBy(target => String(target, "heroId"), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    Dictionary<string, object> target = group.First();
                    target["priorConversationEvidenceCount"] =
                        ReadLong(target, "priorDialogueLineCount", 0)
                        + ReadLong(target, "priorMemorySourceCount", 0)
                        + (ReadBoolean(
                                target,
                                "priorGiftRelationshipEvidence")
                            ? 10000
                            : 0);
                    return target;
                })
                // Prefer the least-used eligible heroes so repeated qualification windows
                // do not turn the campaign's oldest test subjects into permanent
                // interrogation targets. Closed group scenes may not appear in the
                // individual dialogue endpoint, so include retrieved memory/summary/event
                // sources in the evidence count. The recorded hash remains the
                // deterministic tie-break.
                .OrderBy(target => ReadLong(target, "priorConversationEvidenceCount", 0))
                .ThenBy(target => QualificationStableOrder((qualificationId ?? "") + "|broad|" + String(target, "heroId")))
                .ToList();
        }

        private static List<Dictionary<string, object>> SelectCourtIntrigueTargets(
            IEnumerable<Dictionary<string, object>> available,
            int limit)
        {
            List<Dictionary<string, object>> all = (available ?? Enumerable.Empty<Dictionary<string, object>>())
                .Where(target => !string.IsNullOrWhiteSpace(String(target, "heroId")))
                .GroupBy(target => String(target, "heroId"), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<Dictionary<string, object>> add = target =>
            {
                if (target == null || result.Count >= limit) return;
                string id = String(target, "heroId");
                if (!string.IsNullOrWhiteSpace(id) && selected.Add(id)) result.Add(target);
            };
            int nobleLimit = Math.Max(16, limit - Math.Max(6, limit / 5));
            Action<Dictionary<string, object>> addNoble = target =>
            {
                if (result.Count < nobleLimit) add(target);
            };

            List<Dictionary<string, object>> nobles = all.Where(target => ReadBoolean(target, "isLord")).ToList();
            foreach (IGrouping<long, Dictionary<string, object>> tier in nobles
                .GroupBy(target => ReadLong(target, "clanTier", 0))
                .OrderBy(group => group.Key))
            {
                foreach (bool female in new[] { true, false })
                {
                    List<Dictionary<string, object>> sex = tier
                        .Where(target => ReadBoolean(target, "isFemale") == female)
                        .OrderBy(target => ReadLong(target, "calculating", 0))
                        .ThenBy(target => String(target, "heroId"), StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    addNoble(sex.FirstOrDefault());
                    addNoble(sex.LastOrDefault());
                }
            }

            foreach (Dictionary<string, object> noble in nobles
                .OrderByDescending(target => Math.Abs(ReadLong(target, "calculating", 0)))
                .ThenBy(target => ReadLong(target, "clanTier", 0))
                .ThenBy(target => String(target, "heroId"), StringComparer.OrdinalIgnoreCase))
                addNoble(noble);

            foreach (Dictionary<string, object> nonNoble in all
                .Where(target => !ReadBoolean(target, "isLord"))
                .OrderByDescending(target => Math.Abs(ReadLong(target, "calculating", 0)))
                .ThenByDescending(target => ReadBoolean(target, "isNotable"))
                .ThenBy(target => String(target, "heroId"), StringComparer.OrdinalIgnoreCase))
                add(nonNoble);

            foreach (Dictionary<string, object> target in all
                .OrderBy(target => String(target, "heroId"), StringComparer.OrdinalIgnoreCase))
                add(target);
            return result;
        }

        private static string IdentityAuthorityTargetKind(
            Dictionary<string, object> target)
        {
            if (ReadBoolean(target, "isRuler"))
                return "ruler";
            if (!string.IsNullOrWhiteSpace(
                    String(
                        target,
                        "governorOfSettlementId")))
                return "governor";
            if (ReadBoolean(target, "isLord"))
                return "noble";
            if (ReadBoolean(target, "isNotable"))
                return "notable";
            if (ReadBoolean(target, "isWanderer"))
                return "wanderer";
            return "commoner";
        }

        private static List<Dictionary<string, object>>
            SelectIdentityAuthorityTargets(
                IEnumerable<Dictionary<string, object>> available,
                string qualificationId,
                int limit)
        {
            List<Dictionary<string, object>> ordered =
                OrderQualificationTargetsByUsage(
                    available,
                    qualificationId)
                .Where(target =>
                    ReadLong(target, "age", 0) >= 18)
                .ToList();
            Dictionary<string,
                Queue<Dictionary<string, object>>> buckets =
                ordered.GroupBy(
                        IdentityAuthorityTargetKind,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => new Queue<
                            Dictionary<string, object>>(
                            group),
                        StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> result =
                new List<Dictionary<string, object>>();
            HashSet<string> selected =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
            string[] roleOrder =
            {
                "ruler",
                "governor",
                "noble",
                "notable",
                "wanderer",
                "commoner"
            };
            while (result.Count < limit
                && buckets.Values.Any(queue =>
                    queue.Count > 0))
            {
                foreach (string role in roleOrder)
                {
                    if (result.Count >= limit)
                        break;
                    if (!buckets.TryGetValue(
                            role,
                            out Queue<
                                Dictionary<string, object>>
                                queue)
                        || queue.Count == 0)
                        continue;
                    Dictionary<string, object> target =
                        queue.Dequeue();
                    string id = String(target, "heroId");
                    if (selected.Add(id))
                    {
                        target["identityAuthorityTargetKind"] =
                            role;
                        result.Add(target);
                    }
                }
            }
            foreach (Dictionary<string, object> target
                in ordered)
            {
                if (result.Count >= limit)
                    break;
                string id = String(target, "heroId");
                if (selected.Add(id))
                {
                    target["identityAuthorityTargetKind"] =
                        IdentityAuthorityTargetKind(target);
                    result.Add(target);
                }
            }
            if (result.Count < Math.Min(limit, 25))
                throw new InvalidOperationException(
                    "Identity/authority qualification requires at least "
                    + Math.Min(limit, 25)
                    + " living adult targets; selected "
                    + result.Count + ".");
            return result;
        }

        private static Dictionary<string, object>
            IdentityAuthorityProbe(
                string text,
                Dictionary<string, object> expected = null,
                bool requireExactIntroduction = false)
        {
            Dictionary<string, object> assertions =
                BaseAssertions(1, true);
            assertions[
                "requiresPoliticalAuthorityEvidence"] = true;
            if (requireExactIntroduction)
            {
                assertions["requiresIdentityRecognition"] =
                    true;
                assertions["expectedIdentityName"] =
                    String(expected, "playerName");
                assertions["expectedIdentityState"] =
                    "verified";
                assertions["expectedIdentitySource"] =
                    "exact_self_introduction";
            }
            if (expected != null)
            {
                foreach (string key in new[]
                {
                    "expectedRealmSovereignKnown",
                    "expectedCurrentSettlementOwnerKnown",
                    "expectedAuthorityRelationship",
                    "expectedRecognizedRoles",
                    "expectedCurrentEnemyKingdomCount"
                })
                {
                    if (expected.ContainsKey(key))
                        assertions[key] = expected[key];
                }
            }
            return SendStep(
                "individual_chat",
                text,
                new List<string>
                {
                    "structural_pipeline",
                    "identity_role_authority",
                    "detailed_factual_accuracy",
                    "performance_resilience"
                },
                assertions);
        }

        private static Dictionary<string, object>
            RunIdentityAuthorityQualification(
                string campaignId,
                string qualificationId,
                Dictionary<string, object> nativeRuntime,
                List<Dictionary<string, object>> targets,
                Dictionary<string, object> plannedCoverage)
        {
            int completedRuns = 0;
            List<Dictionary<string, object>> failures =
                new List<Dictionary<string, object>>();
            string playerId = String(
                nativeRuntime,
                "playerHeroId");
            string playerName = String(
                nativeRuntime,
                "playerName");
            string playerClanId = String(
                nativeRuntime,
                "playerClanId");
            string playerKingdomId = String(
                nativeRuntime,
                "playerKingdomId");
            bool playerIsRuler = ReadBoolean(
                nativeRuntime,
                "playerIsRuler");
            bool playerIsLord = ReadBoolean(
                nativeRuntime,
                "playerIsLord");
            bool playerIsFemale = ReadBoolean(
                nativeRuntime,
                "playerIsFemale");
            string playerGovernorSettlement = String(
                nativeRuntime,
                "playerGovernorOfSettlementId");
            List<string> expectedPublicRoles =
                new List<string>();
            if (playerIsRuler)
                expectedPublicRoles.Add(
                    "realm_sovereign");
            if (playerIsLord)
                expectedPublicRoles.Add(
                    playerIsFemale ? "lady" : "lord");
            if (!string.IsNullOrWhiteSpace(
                    playerGovernorSettlement))
                expectedPublicRoles.Add(
                    "current_settlement_governor");

            for (int index = 0;
                index < targets.Count;
                index++)
            {
                Dictionary<string, object> target =
                    targets[index];
                string targetId = String(
                    target,
                    "heroId");
                Dictionary<string, object> reset = Post(
                    "/identity/reset",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["observerHeroStringId"] =
                            targetId,
                        ["subjectHeroStringId"] =
                            playerId,
                        ["testMode"] = true
                    });
                if (!IsOk(reset))
                    failures.Add(reset);
                bool sameClan =
                    !string.IsNullOrWhiteSpace(
                        playerClanId)
                    && playerClanId.Equals(
                        String(target, "clanId"),
                        StringComparison.OrdinalIgnoreCase);
                bool sameKingdom =
                    !string.IsNullOrWhiteSpace(
                        playerKingdomId)
                    && playerKingdomId.Equals(
                        String(target, "kingdomId"),
                        StringComparison.OrdinalIgnoreCase);
                bool ownSovereign =
                    playerIsRuler && sameKingdom;
                string relationship = ownSovereign
                    ? "subject_is_observer_sovereign"
                    : sameClan
                        ? "same_clan"
                        : sameKingdom
                            ? "same_kingdom_peer_or_subject"
                            : "known_foreign_or_independent_person";
                Dictionary<string, object> expected =
                    new Dictionary<string, object>
                    {
                        ["playerName"] = playerName,
                        ["expectedRealmSovereignKnown"] =
                            playerIsRuler,
                        ["expectedAuthorityRelationship"] =
                            relationship,
                        ["expectedRecognizedRoles"] =
                            expectedPublicRoles.ToArray()
                    };
                List<Dictionary<string, object>> steps =
                    new List<Dictionary<string, object>>
                    {
                        OpenStep(
                            "individual_chat",
                            new[] { targetId }),
                        IdentityAuthorityProbe(
                            "Good day. I have not given you my name yet. Before I do, how would you address me from what you honestly know or can see?"),
                        IdentityAuthorityProbe(
                            "I am " + playerName
                            + ". You may take that as my formal introduction. How would you greet me now, and which of my public offices matter to you here?",
                            expected,
                            true),
                        IdentityAuthorityProbe(
                            "If I asked you to act on my authority in this place, what standing would I truly have over you, and where would my claim end?",
                            expected),
                    };
                // The focused individual matrix contributes 19 replies and
                // the party/social/wilderness parity matrix contributes 21.
                // Together they prove the gate with exactly 40 samples.
                if (index == 0)
                {
                    steps.Add(IdentityAuthorityProbe(
                        "Before I leave, help me avoid an insult born of ignorance: who rules this realm, who holds this settlement, who governs it day to day, and what is your own place in that order?",
                        expected));
                }
                steps.Add(CloseStep("individual_chat"));
                RunQualificationScenario(
                    campaignId,
                    qualificationId,
                    QualificationSeeds[
                        index
                            % QualificationSeeds.Length],
                    "individual_chat",
                    "Identity/role/authority matrix "
                        + index
                        + " observer "
                        + targetId
                        + " role "
                        + String(
                            target,
                            "identityAuthorityTargetKind"),
                    steps,
                    failures);
                completedRuns++;
            }
            Dictionary<string, object> focused = Get(
                "/tests/live/readiness?campaignId="
                + Uri.EscapeDataString(campaignId));
            focused["controllerCompletedRuns"] =
                completedRuns;
            focused["controllerFailureCount"] =
                failures.Count;
            focused["controllerFailures"] = failures;
            focused["qualificationId"] =
                qualificationId;
            focused["qualificationFocus"] =
                "identity_role_authority";
            focused["plannedCoverage"] =
                plannedCoverage;
            focused["focusedReplyPlan"] =
                new Dictionary<string, object>
                {
                    ["targetCount"] = targets.Count,
                    ["replies"] = targets.Count * 3
                        + (targets.Count > 0 ? 1 : 0),
                    ["roleCounts"] =
                        targets.GroupBy(
                                IdentityAuthorityTargetKind,
                                StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(
                                group => group.Key,
                                group => group.Count(),
                                StringComparer.OrdinalIgnoreCase)
                };
            return focused;
        }

        private static Dictionary<string, object>
            RunIdentityAuthorityModeQualification(
                string campaignId,
                string qualificationId,
                Dictionary<string, object> nativeRuntime,
                List<string> partyHeroIds,
                List<Dictionary<string, object>> individualTargets,
                Dictionary<string, object> plannedCoverage)
        {
            int completedRuns = 0;
            List<Dictionary<string, object>> failures =
                new List<Dictionary<string, object>>();
            string playerId = String(nativeRuntime, "playerHeroId");
            string playerName = String(nativeRuntime, "playerName");
            bool playerIsRuler = ReadBoolean(
                nativeRuntime, "playerIsRuler");
            bool playerIsLord = ReadBoolean(
                nativeRuntime, "playerIsLord");
            bool playerIsFemale = ReadBoolean(
                nativeRuntime, "playerIsFemale");
            string playerGovernorSettlement = String(
                nativeRuntime, "playerGovernorOfSettlementId");
            List<string> expectedPublicRoles =
                new List<string>();
            if (playerIsRuler)
                expectedPublicRoles.Add("realm_sovereign");
            if (playerIsLord)
                expectedPublicRoles.Add(
                    playerIsFemale ? "lady" : "lord");
            if (!string.IsNullOrWhiteSpace(
                    playerGovernorSettlement))
                expectedPublicRoles.Add(
                    "current_settlement_governor");
            Dictionary<string, object> expected =
                new Dictionary<string, object>
                {
                    ["playerName"] = playerName,
                    ["expectedRealmSovereignKnown"] =
                        playerIsRuler,
                    ["expectedRecognizedRoles"] =
                        expectedPublicRoles.ToArray()
                };
            if (string.IsNullOrWhiteSpace(
                    String(nativeRuntime, "playerKingdomId")))
                expected["expectedCurrentEnemyKingdomCount"] = 0;

            List<string> partyGroup = partyHeroIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();
            if (partyGroup.Count < 2)
                throw new InvalidOperationException(
                    "Cross-mode identity/authority qualification requires "
                    + "two party-chat-eligible adult heroes.");
            Dictionary<string, object> socialEvent =
                QualificationSocialEvents(campaignId, 100)
                    .Where(row =>
                        !ReadBoolean(row, "wilderness")
                        && ReadBoolean(row, "isOpen")
                        && QualificationReadStrings(
                                row, "attendeeHeroIds")
                            .Count >= 2)
                    .OrderByDescending(row =>
                        QualificationReadStrings(
                                row, "attendeeHeroIds")
                            .Count)
                    .ThenBy(row => String(row, "eventId"),
                        StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            if (socialEvent == null)
                throw new InvalidOperationException(
                    "Cross-mode identity/authority qualification requires "
                    + "an existing currently-open scheduled social event.");
            string socialEventId =
                String(socialEvent, "eventId");
            List<string> socialGroup =
                QualificationReadStrings(
                        socialEvent, "attendeeHeroIds")
                    .Where(id =>
                        !string.IsNullOrWhiteSpace(id))
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToList();
            List<Dictionary<string, object>> socialTargets =
                individualTargets.Where(target =>
                        socialGroup.Contains(
                            String(target, "heroId"),
                            StringComparer.OrdinalIgnoreCase))
                    .ToList();

            ResetIdentityObserversForQualification(
                campaignId, playerId, partyGroup, failures);
            RunQualificationScenario(
                campaignId,
                qualificationId,
                QualificationSeeds[0],
                "party_chat",
                "Identity role authority cross-mode party matrix v3 "
                    + string.Join("+", partyGroup),
                IdentityAuthorityModeSteps(
                    "party_chat",
                    partyGroup,
                    playerName,
                    expected,
                    false,
                    "",
                    partyGroup.Count),
                failures);
            completedRuns++;

            ResetIdentityObserversForQualification(
                campaignId, playerId, socialGroup, failures);
            RunQualificationScenario(
                campaignId,
                qualificationId,
                QualificationSeeds[1],
                "social_event",
                "Identity role authority cross-mode social matrix v3 "
                    + string.Join("+", socialGroup),
                IdentityAuthorityModeSteps(
                    "social_event",
                    socialGroup,
                    playerName,
                    expected,
                    false,
                    socialEventId,
                    socialGroup.Count),
                failures);
            completedRuns++;

            List<string> wildernessGroup =
                partyGroup.Take(2).ToList();
            bool alreadyOnOpenMap = ReadBoolean(
                ReadObject(nativeRuntime, "location"),
                "onOpenMap");
            bool preparedWilderness = false;
            if (!alreadyOnOpenMap)
            {
                RunQualificationScenario(
                    campaignId,
                    qualificationId,
                    QualificationSeeds[2],
                    "wilderness_event",
                    "Identity role authority cross-mode prepare wilderness v3",
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["schemaVersion"] = 2,
                            ["operation"] =
                                "prepare_wilderness",
                            ["mode"] =
                                "wilderness_event",
                            ["targetSearches"] =
                                partyGroup.ToArray(),
                            ["timeoutSeconds"] = 120
                        }
                    },
                    failures);
                completedRuns++;
                preparedWilderness = true;
            }
            try
            {
                RunQualificationScenario(
                    campaignId,
                    qualificationId,
                    QualificationSeeds[2],
                    "wilderness_event",
                    "Identity role authority cross-mode wilderness matrix v3",
                    IdentityAuthorityModeSteps(
                        "wilderness_event",
                        wildernessGroup,
                        playerName,
                        expected,
                        true,
                        "",
                        2),
                    failures);
                completedRuns++;
            }
            finally
            {
                if (preparedWilderness)
                {
                    RunQualificationScenario(
                        campaignId,
                        qualificationId,
                        QualificationSeeds[2],
                        "wilderness_event",
                        "Identity role authority cross-mode restore wilderness v3",
                        new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["schemaVersion"] = 2,
                                ["operation"] =
                                    "restore_settlement",
                                ["mode"] =
                                    "wilderness_event",
                                ["timeoutSeconds"] = 120
                            }
                        },
                        failures);
                    completedRuns++;
                }
            }

            Dictionary<string, object> focused = Get(
                "/tests/live/readiness?campaignId="
                + Uri.EscapeDataString(campaignId));
            focused["controllerCompletedRuns"] = completedRuns;
            focused["controllerFailureCount"] = failures.Count;
            focused["controllerFailures"] = failures;
            focused["qualificationId"] = qualificationId;
            focused["qualificationFocus"] =
                "identity_role_authority_modes";
            focused["plannedCoverage"] = plannedCoverage;
            focused["focusedReplyPlan"] =
                new Dictionary<string, object>
                {
                    ["modes"] = new[]
                    {
                        "party_chat",
                        "social_event",
                        "wilderness_event"
                    },
                    ["partyTargets"] = partyGroup.ToArray(),
                    ["socialTargets"] = socialGroup.ToArray(),
                    ["wildernessTargets"] =
                        wildernessGroup.ToArray(),
                    ["minimumReplies"] =
                        partyGroup.Count * 3
                        + socialGroup.Count * 3
                        + 2 * 3,
                    ["roleCounts"] = socialTargets
                        .GroupBy(
                            IdentityAuthorityTargetKind,
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            group => group.Key,
                            group => group.Count(),
                            StringComparer.OrdinalIgnoreCase)
                };
            return focused;
        }

        private static List<Dictionary<string, object>>
            IdentityAuthorityModeSteps(
                string mode,
                List<string> observers,
                string playerName,
                Dictionary<string, object> expected,
                bool wilderness,
                string existingEventId,
                int minimumReplies)
        {
            return new List<Dictionary<string, object>>
            {
                !string.IsNullOrWhiteSpace(existingEventId)
                    ? OpenExistingSocialEventStep(
                        existingEventId, observers)
                    : OpenStep(
                        mode,
                        observers,
                        wilderness
                            ? Math.Max(2, minimumReplies)
                            : 1),
                IdentityAuthorityModeProbe(
                    mode,
                    observers,
                    "Good day. I have not yet offered my name. Before I do, "
                    + "how would each of you address me from what you "
                    + "honestly know or can see? If another person recognizes "
                    + "me, do not pretend their private reason is your own.",
                    null,
                    false,
                    minimumReplies),
                IdentityAuthorityModeProbe(
                    mode,
                    observers,
                    "I am " + playerName
                    + ". You may take that as my formal introduction to "
                    + "everyone present. How does knowing my name change the "
                    + "way each of you would address me, if at all?",
                    expected,
                    true,
                    minimumReplies),
                IdentityAuthorityModeProbe(
                    mode,
                    observers,
                    wilderness
                        ? "If I gave an order on this road, which of you would "
                            + "owe me obedience, which would merely hear a "
                            + "request, and what nearby danger or allegiance "
                            + "would shape your answer?"
                        : "If I gave an order in this town, which of you would "
                            + "owe me obedience, which would merely hear a "
                            + "request, and how do the ruler, holder, and "
                            + "governor of this place limit the answer?",
                    expected,
                    false,
                    minimumReplies),
                CloseStep(mode)
            };
        }

        private static Dictionary<string, object>
            IdentityAuthorityModeProbe(
                string mode,
                List<string> observers,
                string text,
                Dictionary<string, object> expected,
                bool requireExactIntroduction,
                int minimumReplies)
        {
            Dictionary<string, object> assertions =
                BaseAssertions(
                    Math.Max(1, minimumReplies), true);
            assertions["requiresPoliticalAuthorityEvidence"] = true;
            // This focused gate proves identity and political authority parity
            // across production interaction modes. Group-awareness,
            // personality, and NPC-to-NPC relationship receipts have their own
            // qualification gates and must not discard otherwise valid
            // identity evidence.
            assertions["requiresRelationshipReceipt"] = false;
            assertions.Remove("relationshipPolicy");
            if (requireExactIntroduction)
            {
                assertions["requiresIdentityRecognition"] = true;
                assertions["expectedIdentityName"] =
                    String(expected, "playerName");
                assertions["expectedIdentityState"] = "verified";
                assertions["expectedIdentitySource"] =
                    "exact_self_introduction";
            }
            if (expected != null)
            {
                foreach (string key in new[]
                {
                    "expectedRealmSovereignKnown",
                    "expectedRecognizedRoles",
                    "expectedCurrentEnemyKingdomCount"
                })
                {
                    if (expected.ContainsKey(key))
                        assertions[key] = expected[key];
                }
            }
            return SendStep(
                mode,
                text,
                new List<string>
                {
                    "structural_pipeline",
                    "identity_role_authority",
                    "detailed_factual_accuracy",
                    "performance_resilience"
                },
                assertions);
        }

        private static Dictionary<string, object>
            OpenExistingSocialEventStep(
                string eventId,
                IEnumerable<string> targets)
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "open",
                ["mode"] = "social_event",
                ["eventId"] = eventId,
                ["targetSearches"] =
                    (targets
                        ?? Enumerable.Empty<string>())
                        .ToArray(),
                ["createTestEvent"] = false,
                ["timeoutSeconds"] = 300
            };
        }

        private static void ResetIdentityObserversForQualification(
            string campaignId,
            string playerId,
            IEnumerable<string> observerIds,
            List<Dictionary<string, object>> failures)
        {
            foreach (string observerId in observerIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Dictionary<string, object> reset = Post(
                    "/identity/reset",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["observerHeroStringId"] = observerId,
                        ["subjectHeroStringId"] = playerId,
                        ["testMode"] = true
                    });
                if (!IsOk(reset))
                {
                    failures.Add(reset);
                    throw new InvalidOperationException(
                        "Could not reset identity evidence for observer "
                        + observerId + ": " + Json.Serialize(reset));
                }
            }
        }
    }
}
