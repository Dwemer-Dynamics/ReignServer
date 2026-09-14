using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly Dictionary<string, Tuple<int, double, bool>> ConversationReadinessGates =
            new Dictionary<string, Tuple<int, double, bool>>(StringComparer.OrdinalIgnoreCase)
            {
                ["structural_pipeline"] = Tuple.Create(40, 1.00, true),
                ["identity_evidence"] = Tuple.Create(40, 1.00, true),
                ["identity_role_authority"] = Tuple.Create(40, 1.00, true),
                ["sovereign_demeanor"] = Tuple.Create(36, 0.95, false),
                ["short_cross_scene_memory"] = Tuple.Create(40, 0.95, false),
                ["long_term_memory"] = Tuple.Create(40, 0.95, false),
                ["dynamic_characteristics"] = Tuple.Create(40, 0.95, false),
                ["shared_relationship_history"] = Tuple.Create(40, 0.95, false),
                ["group_awareness"] = Tuple.Create(40, 0.95, false),
                ["world_local_knowledge"] = Tuple.Create(40, 0.95, false),
                ["lie_relationship"] = Tuple.Create(40, 0.95, false),
                ["clan_tier_recognition"] = Tuple.Create(30, 0.95, false),
                ["manipulation_capabilities"] = Tuple.Create(40, 0.95, false),
                ["personality_consistency"] = Tuple.Create(40, 0.95, false),
                ["detailed_factual_accuracy"] = Tuple.Create(40, 0.95, false),
                ["performance_resilience"] = Tuple.Create(40, 0.95, false)
            };

        private static string CurrentConversationBuildVersion()
        {
            Version version = typeof(Program).Assembly.GetName().Version;
            string mvid = typeof(Program).Assembly.ManifestModule.ModuleVersionId.ToString("N");
            return (version?.ToString() ?? "0.0.0.0") + "+mvid." + mvid.Substring(0, 16);
        }

        private static Dictionary<string, object> ConversationReadinessResetApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", ResolveLogCampaignId(""));
            if (string.IsNullOrWhiteSpace(campaignId))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "campaignId is required." };
            string qualificationId = FirstNonEmpty(
                ReadString(payload, "qualificationId", ""),
                "qualification-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            Dictionary<string, object> state = new Dictionary<string, object>
            {
                ["schemaVersion"] = 1,
                ["qualificationId"] = qualificationId,
                ["campaignId"] = campaignId,
                ["buildVersion"] = CurrentConversationBuildVersion(),
                ["startedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["status"] = "running",
                ["baselineSaveName"] = ReadString(payload, "baselineSaveName", ""),
                ["stateFingerprint"] =
                    ReadString(payload, "stateFingerprint", ""),
                ["notes"] = ReadString(payload, "notes", "")
            };
            WriteJsonObject(ConversationReadinessStatePath(campaignId), state);
            return ConversationReadinessStatusApi(new Dictionary<string, string>
            {
                ["campaignId"] = campaignId, ["qualificationId"] = qualificationId
            });
        }

        private static Dictionary<string, object> ConversationReadinessMarkerApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", ResolveLogCampaignId(""));
            Dictionary<string, object> state = ReadJsonObject(ConversationReadinessStatePath(campaignId));
            if (state.Count == 0) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "No active qualification exists." };
            List<Dictionary<string, object>> markers = ReadDictionaryList(state, "markers");
            markers.Add(new Dictionary<string, object>
            {
                ["type"] = ReadString(payload, "type", "checkpoint"),
                ["passed"] = ReadBool(payload, "passed", false),
                ["saveName"] = ReadString(payload, "saveName", ""),
                ["gameInstanceId"] = ReadString(payload, "gameInstanceId", ""),
                ["evidence"] = LimitText(ReadString(payload, "evidence", ""), 2000),
                ["utc"] = DateTimeOffset.UtcNow.ToString("o")
            });
            state["markers"] = markers;
            state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            WriteJsonObject(ConversationReadinessStatePath(campaignId), state);
            return new Dictionary<string, object> { ["ok"] = true, ["markerCount"] = markers.Count };
        }

        private static Dictionary<string, object> ConversationReadinessMigrateBuildApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!ReadString(payload, "confirmation", "").Equals(
                    "carry-compatible-categories", StringComparison.Ordinal))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "confirmation must be exactly carry-compatible-categories."
                };
            }
            string campaignId = ReadString(payload, "campaignId", ResolveLogCampaignId(""));
            Dictionary<string, object> state =
                ReadJsonObject(ConversationReadinessStatePath(campaignId));
            if (state.Count == 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["error"] = "No active qualification exists."
                };
            string qualificationId = ReadString(state, "qualificationId", "");
            string requestedQualificationId = ReadString(payload, "qualificationId", "");
            if (!string.IsNullOrWhiteSpace(requestedQualificationId)
                && !requestedQualificationId.Equals(
                    qualificationId, StringComparison.OrdinalIgnoreCase))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] =
                        "The requested qualification does not match the active qualification."
                };
            }
            HashSet<string> supersededCategories = new HashSet<string>(
                ReadStringList(payload, "supersedeCategories")
                    .Where(ConversationReadinessGates.ContainsKey),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> supersededRunIds = new HashSet<string>(
                ReadStringList(state, "supersededRunIds"),
                StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> runs =
                LoadConversationReadinessRuns(campaignId, qualificationId);
            HashSet<string> availableRunIds = new HashSet<string>(
                runs.Select(run => ReadString(run, "runId", ""))
                    .Where(runId =>
                        !string.IsNullOrWhiteSpace(runId)),
                StringComparer.OrdinalIgnoreCase);
            List<string> requestedRunIds = ReadStringList(
                    payload, "supersedeRunIds")
                .Where(availableRunIds.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (supersededCategories.Count == 0
                && requestedRunIds.Count == 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] =
                        "At least one recognized supersedeCategories value or active qualification supersedeRunIds value is required."
                };
            List<string> newlySuperseded = runs
                .Where(run => ConversationReadinessRunTouchesCategories(
                    run, supersededCategories))
                .Select(run => ReadString(run, "runId", ""))
                .Where(runId => !string.IsNullOrWhiteSpace(runId))
                .Concat(requestedRunIds)
                .Where(supersededRunIds.Add)
                .OrderBy(runId => runId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            string previousBuildVersion = ReadString(state, "buildVersion", "");
            string currentBuildVersion = CurrentConversationBuildVersion();
            List<Dictionary<string, object>> transitions =
                ReadDictionaryList(state, "compatibleBuildTransitions");
            transitions.Add(new Dictionary<string, object>
            {
                ["fromBuildVersion"] = previousBuildVersion,
                ["toBuildVersion"] = currentBuildVersion,
                ["preservedCategories"] = ReadStringList(
                    payload, "preserveCategories")
                    .Where(ConversationReadinessGates.ContainsKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ["supersededCategories"] =
                    supersededCategories.OrderBy(value => value).ToList(),
                ["requestedSupersededRunIds"] =
                    requestedRunIds.OrderBy(value => value).ToList(),
                ["supersededRunIds"] = newlySuperseded,
                ["reason"] = LimitText(ReadString(payload, "reason", ""), 2000),
                ["utc"] = DateTimeOffset.UtcNow.ToString("o")
            });
            state["buildVersion"] = currentBuildVersion;
            state["supersededRunIds"] =
                supersededRunIds.OrderBy(runId => runId).ToList();
            state["compatibleBuildTransitions"] = transitions;
            state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            WriteJsonObject(ConversationReadinessStatePath(campaignId), state);
            Dictionary<string, object> status = ConversationReadinessStatusApi(
                new Dictionary<string, string>
                {
                    ["campaignId"] = campaignId,
                    ["qualificationId"] = qualificationId
                });
            status["migration"] = transitions.Last();
            return status;
        }

        private static Dictionary<string, object> ConversationReadinessRosterApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", ResolveLogCampaignId(""));
            Dictionary<string, object> state = ReadJsonObject(ConversationReadinessStatePath(campaignId));
            if (state.Count == 0)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "No active qualification exists." };
            string requestedQualificationId = ReadString(payload, "qualificationId", "");
            string activeQualificationId = ReadString(state, "qualificationId", "");
            if (!string.IsNullOrWhiteSpace(requestedQualificationId)
                && !string.Equals(requestedQualificationId, activeQualificationId, StringComparison.OrdinalIgnoreCase))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "The requested qualification does not match the active qualification."
                };
            }
            Dictionary<string, object> roster = ReadDictionary(payload, "targetRoster");
            if (roster.Count == 0)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "targetRoster is required." };
            state["targetRoster"] = roster;
            state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            WriteJsonObject(ConversationReadinessStatePath(campaignId), state);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["qualificationId"] = activeQualificationId,
                ["targetRoster"] = roster
            };
        }

        private static Dictionary<string, object> ConversationReadinessStatusApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string campaignId = query.TryGetValue("campaignId", out string requestedCampaign)
                ? requestedCampaign : ResolveLogCampaignId("");
            Dictionary<string, object> state = ReadJsonObject(ConversationReadinessStatePath(campaignId));
            string qualificationId = query.TryGetValue("qualificationId", out string requestedId)
                ? requestedId : ReadString(state, "qualificationId", "");
            if (string.IsNullOrWhiteSpace(qualificationId))
                return new Dictionary<string, object> { ["ok"] = true, ["found"] = false, ["campaignId"] = campaignId };

            HashSet<string> supersededRunIds = new HashSet<string>(
                ReadStringList(state, "supersededRunIds"),
                StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> allRuns =
                LoadConversationReadinessRuns(campaignId, qualificationId)
                    .Where(run => !supersededRunIds.Contains(
                        ReadString(run, "runId", "")))
                    .ToList();
            List<Dictionary<string, object>> runs = allRuns
                .Where(ConversationReadinessRunIsCompleted).ToList();
            List<Dictionary<string, object>> terminalFailedRuns = allRuns
                .Where(ConversationReadinessRunIsTerminalFailure).ToList();
            int inProgressRunCount = allRuns.Count(run =>
                !ConversationReadinessRunIsCompleted(run)
                && !ConversationReadinessRunIsTerminalFailure(run));
            List<Dictionary<string, object>> commands = runs.SelectMany(run => ReadDictionaryList(run, "commands")).ToList();
            List<Dictionary<string, object>> sendCommands = commands
                .Where(command => ReadString(command, "operation", "").Equals("send", StringComparison.OrdinalIgnoreCase)).ToList();
            List<Dictionary<string, object>> assertions = runs.SelectMany(run => ReadDictionaryList(run, "assertions")).ToList();
            List<Dictionary<string, object>> failures = allRuns.SelectMany(run => ReadDictionaryList(run, "failures")).ToList();
            string recordedBuildVersion = ReadString(state, "buildVersion", "");
            string currentBuildVersion = CurrentConversationBuildVersion();
            bool sameBuild = !string.IsNullOrWhiteSpace(recordedBuildVersion)
                && recordedBuildVersion.Equals(currentBuildVersion, StringComparison.OrdinalIgnoreCase);
            string recordedStateFingerprint =
                ReadString(state, "stateFingerprint", "");
            string expectedStateFingerprint =
                query.TryGetValue(
                    "stateFingerprint",
                    out string requestedStateFingerprint)
                    ? requestedStateFingerprint
                    : "";
            bool sameStateFingerprint =
                string.IsNullOrWhiteSpace(expectedStateFingerprint)
                    || (!string.IsNullOrWhiteSpace(
                            recordedStateFingerprint)
                        && recordedStateFingerprint.Equals(
                            expectedStateFingerprint,
                            StringComparison.Ordinal));
            sameBuild &= sameStateFingerprint;
            HashSet<string> criticalCommandIds = new HashSet<string>(
                failures.Where(row => ReadBool(row, "critical", false)).Select(row => ReadString(row, "commandId", "")),
                StringComparer.OrdinalIgnoreCase);

            int replyCount = sendCommands.Sum(ConversationReadinessReplyCount);
            HashSet<string> heroIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> command in commands.Where(row => ReadString(row, "operation", "") == "open"))
            {
                foreach (Dictionary<string, object> target in ReadDictionaryList(ReadDictionary(command, "result"), "targets"))
                    heroIds.Add(ReadFirstString(target, "heroId", "stringId"));
            }
            HashSet<string> modes = new HashSet<string>(runs.Select(run => ReadString(run, "mode", "")), StringComparer.OrdinalIgnoreCase);
            HashSet<long> seeds = new HashSet<long>(runs.Select(run => ReadLong(run, "seed", 0)));
            List<Dictionary<string, object>> markers = ReadDictionaryList(state, "markers");
            int checkpointCount = commands.Count(command =>
                ReadString(command, "operation", "") == "save_checkpoint"
                && ReadString(command, "status", "") == "completed")
                + markers.Count(marker => ReadString(marker, "type", "") == "checkpoint" && ReadBool(marker, "passed", false));
            int rollbackCount = markers.Count(marker =>
                ReadString(marker, "type", "") == "rollback" && ReadBool(marker, "passed", false));
            int reloadCount = markers.Count(marker =>
                ReadString(marker, "type", "") == "checkpoint_reload" && ReadBool(marker, "passed", false));
            List<long> promptBuildSamples = sendCommands
                .SelectMany(ConversationReadinessPromptBuildSamples)
                .Where(value => value >= 0)
                .OrderBy(value => value)
                .ToList();
            long promptBuildP95Ms = PercentileNearestRank(promptBuildSamples, 0.95d);
            int dynamicCharacteristicCaptureCount = sendCommands.Sum(ConversationReadinessDynamicCharacteristicCaptureCount);
            int dynamicCharacteristicRecallCount = sendCommands.Count(command =>
            {
                string commandId = ReadString(command, "commandId", "");
                return assertions.Any(row =>
                    string.Equals(ReadString(row, "commandId", ""), commandId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(row, "assertion", "").Equals("requiresDynamicCharacteristicRecall", StringComparison.OrdinalIgnoreCase)
                    && ReadBool(row, "passed", false));
            });
            List<Dictionary<string, object>> sceneMemoryArtifactAssertions = assertions
                .Where(row => ReadString(row, "assertion", "")
                    .Equals("requiresSceneMemoryArtifacts", StringComparison.OrdinalIgnoreCase))
                .ToList();
            int sceneMemoryArtifactCount = sceneMemoryArtifactAssertions.Count;
            int sceneMemoryArtifactFailures = sceneMemoryArtifactAssertions.Count(row => !ReadBool(row, "passed", false));
            List<Dictionary<string, object>> qualificationMemoryCompletionAssertions = assertions
                .Where(row => ReadString(row, "assertion", "")
                    .Equals("requiresQualificationMemoryCompletion", StringComparison.OrdinalIgnoreCase))
                .ToList();
            int qualificationMemoryCompletionCount = qualificationMemoryCompletionAssertions.Count;
            int qualificationMemoryCompletionFailures = qualificationMemoryCompletionAssertions
                .Count(row => !ReadBool(row, "passed", false));
            List<Dictionary<string, object>> relationshipHistoryEvidence =
                sendCommands.Select(command =>
                    ReadDictionary(command, "sharedRelationshipHistoryEvidence"))
                .Where(row => row != null && row.Count > 0).ToList();
            HashSet<string> relationshipHistoryPairs =
                new HashSet<string>(
                    relationshipHistoryEvidence.SelectMany(row =>
                        ReadStringList(row, "pairKeys")),
                    StringComparer.OrdinalIgnoreCase);
            HashSet<string> asymmetricRelationshipHistoryPairs =
                new HashSet<string>(
                    relationshipHistoryEvidence.SelectMany(row =>
                        ReadStringList(row, "asymmetricPairKeys")),
                    StringComparer.OrdinalIgnoreCase);
            int relationshipHistoryColdReplies =
                relationshipHistoryEvidence.Where(row =>
                    ReadString(row, "expectedMode", "")
                        .Equals("cold", StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "providerCallCount", 0) > 0
                    && ReadInt(row, "generatedCount", 0)
                        + ReadInt(row, "fallbackCount", 0) > 0)
                .Sum(row => ReadInt(row, "replyCount", 0));
            int relationshipHistoryReuseReplies =
                relationshipHistoryEvidence.Where(row =>
                    ReadString(row, "expectedMode", "")
                        .Equals("reuse", StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "providerCallCount", 0) == 0
                    && ReadInt(row, "reusedCount", 0) > 0)
                .Sum(row => ReadInt(row, "replyCount", 0));
            int relationshipHistoryTransitionReplies =
                relationshipHistoryEvidence.Where(row =>
                    ReadString(row, "expectedMode", "")
                        .Equals("transition", StringComparison.OrdinalIgnoreCase)
                    && ReadInt(row, "providerCallCount", 0) > 0
                    && ReadInt(row, "generatedCount", 0)
                        + ReadInt(row, "fallbackCount", 0) > 0)
                .Sum(row => ReadInt(row, "replyCount", 0));
            int relationshipHistoryPersistenceReplies =
                relationshipHistoryEvidence.Where(row =>
                    ReadBool(row, "persistenceReuse", false)
                    && ReadBool(row, "valid", false)
                    && ReadInt(row, "providerCallCount", 0) == 0)
                .Sum(row => ReadInt(row, "replyCount", 0));
            int relationshipHistoryPrivacyLeaks =
                relationshipHistoryEvidence.Sum(row =>
                    ReadInt(row, "privacyLeakCount", 0));
            List<long> relationshipHistoryProviderSamples =
                relationshipHistoryEvidence.Where(row =>
                    ReadInt(row, "providerCallCount", 0) > 0)
                .Select(row => ReadLong(row, "providerDurationMs", 0))
                .OrderBy(value => value).ToList();
            long relationshipHistoryProviderP95Ms =
                PercentileNearestRank(relationshipHistoryProviderSamples, 0.95d);
            bool relationshipHistoryCoverageHigh =
                relationshipHistoryPairs.Count >= 20
                && asymmetricRelationshipHistoryPairs.Count >= 5
                && relationshipHistoryColdReplies >= 2
                && relationshipHistoryReuseReplies >= 40
                && relationshipHistoryTransitionReplies >= 10
                && relationshipHistoryPersistenceReplies >= 20
                && relationshipHistoryPrivacyLeaks == 0;
            List<Dictionary<string, object>> manipulationDecisions =
                sendCommands
                    .Where(command => ReadStringList(
                            command, "readinessCategories")
                        .Contains("manipulation_capabilities",
                            StringComparer.OrdinalIgnoreCase))
                    .SelectMany(
                        ConversationReadinessManipulationDecisions)
                    .ToList();
            int manipulationRecommendedReplies =
                manipulationDecisions.Count(decision =>
                    ReadBool(decision, "recommended", false));
            int manipulationRestrainedReplies =
                manipulationDecisions.Count(decision =>
                    !ReadBool(decision, "recommended", false));
            int manipulationDistinctTactics =
                manipulationDecisions
                    .Select(decision => ReadString(
                        decision, "appliedCourtTactic", ""))
                    .Where(value => !string.IsNullOrWhiteSpace(value)
                        && !value.Equals(
                            "none",
                            StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Dictionary<string, int> manipulationQuadrants =
                manipulationDecisions
                    .Select(decision => ReadString(
                        decision, "qualificationQuadrant", ""))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .GroupBy(value => value,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key,
                        group => group.Count(),
                        StringComparer.OrdinalIgnoreCase);
            int manipulationLowHonorReplies =
                manipulationDecisions.Count(decision =>
                    ReadInt(decision, "courtHonorLevel", 0) < 0);
            int manipulationAlignedTacticReplies =
                manipulationDecisions.Count(decision =>
                    ReadBool(decision, "tacticAlignmentValid", false));
            int manipulationHiddenWealthControls =
                manipulationDecisions.Count(decision =>
                    ReadBool(decision, "hiddenWealthControl", false));
            int manipulationHiddenWealthSafe =
                manipulationDecisions.Count(decision =>
                    ReadBool(decision, "hiddenWealthControl", false)
                    && !ReadBool(decision,
                        "economicCapacityKnown", true)
                    && ReadString(decision,
                        "wealthEvidenceBasis", "")
                        .Equals("hidden_wallet_not_observable",
                            StringComparison.OrdinalIgnoreCase));
            int manipulationBoldnessBands =
                manipulationDecisions
                    .Select(decision => ReadInt(
                        decision, "courtBoldnessLevel",
                        int.MinValue))
                    .Where(value => value != int.MinValue)
                    .Distinct().Count();
            int manipulationClanTiers =
                manipulationDecisions
                    .Select(decision => ReadInt(
                        decision, "observerClanTier",
                        int.MinValue))
                    .Where(value => value >= 0 && value <= 6)
                    .Distinct().Count();
            bool manipulationCoverageHigh =
                manipulationRecommendedReplies >= 10
                && manipulationRestrainedReplies >= 10
                && manipulationDistinctTactics >= 4
                && new[]
                {
                    "lower_tier_poor",
                    "lower_tier_wealthy",
                    "equal_tier_poor",
                    "equal_tier_wealthy",
                    "higher_tier_poor",
                    "higher_tier_wealthy"
                }.All(quadrant =>
                    manipulationQuadrants.TryGetValue(
                        quadrant, out int count)
                    && count >= 6)
                && manipulationLowHonorReplies >= 40
                && manipulationAlignedTacticReplies >= 40
                && manipulationHiddenWealthControls >= 4
                && manipulationHiddenWealthSafe
                    == manipulationHiddenWealthControls
                // Low-honor nobles are intentionally rare in a normal
                // campaign. Require multiple authentic risk postures without
                // fabricating traits solely to fill all five matrix bands.
                && manipulationBoldnessBands >= 2
                && manipulationClanTiers >= 6;

            List<Dictionary<string, object>> categories = new List<Dictionary<string, object>>();
            bool allCategoriesHigh = true;
            foreach (KeyValuePair<string, Tuple<int, double, bool>> gate in ConversationReadinessGates)
            {
                List<Dictionary<string, object>> probes = sendCommands.Where(command =>
                    ReadStringList(command, "readinessCategories").Contains(gate.Key, StringComparer.OrdinalIgnoreCase)).ToList();
                int total = probes.Sum(ConversationReadinessReplyCount);
                int passed = probes.Where(command => ConversationReadinessCommandPassed(command, assertions, failures, gate.Key))
                    .Sum(ConversationReadinessReplyCount);
                string requiredRubric = ConversationReadinessRequiredRubric(gate.Key);
                int rubricEvaluated = string.IsNullOrWhiteSpace(requiredRubric)
                    ? 0
                    : probes.Where(command => ConversationReadinessCommandHasAssertion(
                            command, assertions, requiredRubric))
                        .Sum(ConversationReadinessReplyCount);
                double rate = total == 0 ? 0 : (double)passed / total;
                bool critical = probes.Any(command => criticalCommandIds.Contains(ReadString(command, "commandId", "")));
                bool high = total >= gate.Value.Item1
                    && rate + 0.0000001 >= gate.Value.Item2
                    && !critical
                    && sameBuild
                    && (string.IsNullOrWhiteSpace(requiredRubric) || rubricEvaluated == total)
                    && (!gate.Key.Equals("shared_relationship_history",
                            StringComparison.OrdinalIgnoreCase)
                        || relationshipHistoryCoverageHigh)
                    && (!gate.Key.Equals("manipulation_capabilities",
                            StringComparison.OrdinalIgnoreCase)
                        || manipulationCoverageHigh)
                    && (!gate.Key.Equals("performance_resilience", StringComparison.OrdinalIgnoreCase)
                        || (promptBuildSamples.Count >= gate.Value.Item1 && promptBuildP95Ms <= 2000));
                allCategoriesHigh &= high;
                categories.Add(new Dictionary<string, object>
                {
                    ["category"] = gate.Key,
                    ["status"] = high ? "high" : "qualifying",
                    ["passed"] = passed,
                    ["total"] = total,
                    ["required"] = gate.Value.Item1,
                    ["passRate"] = Math.Round(rate, 4),
                    ["requiredPassRate"] = gate.Value.Item2,
                    ["deterministic"] = gate.Value.Item3,
                    ["criticalFailures"] = critical ? 1 : 0,
                    ["requiredBlindedRubric"] = requiredRubric,
                    ["rubricEvaluatedReplies"] = rubricEvaluated,
                    ["rubricCoverageComplete"] = string.IsNullOrWhiteSpace(requiredRubric)
                        || rubricEvaluated == total,
                    ["promptBuildSampleCount"] = gate.Key.Equals("performance_resilience", StringComparison.OrdinalIgnoreCase)
                        ? promptBuildSamples.Count : 0,
                    ["promptBuildP95Ms"] = gate.Key.Equals("performance_resilience", StringComparison.OrdinalIgnoreCase)
                        ? promptBuildP95Ms : 0,
                    ["requiredPromptBuildP95Ms"] = gate.Key.Equals("performance_resilience", StringComparison.OrdinalIgnoreCase)
                        ? 2000 : 0,
                    ["relationshipHistoryCoverageHigh"] =
                        gate.Key.Equals("shared_relationship_history",
                            StringComparison.OrdinalIgnoreCase)
                            && relationshipHistoryCoverageHigh
                    ,
                    ["manipulationCoverageHigh"] =
                        gate.Key.Equals("manipulation_capabilities",
                            StringComparison.OrdinalIgnoreCase)
                            && manipulationCoverageHigh
                });
            }

            bool coverageHigh = replyCount >= 40
                && sameBuild
                && terminalFailedRuns.Count == 0
                && inProgressRunCount == 0
                && heroIds.Count >= 25
                && new[] { "individual_chat", "party_chat", "social_event", "wilderness_event" }.All(modes.Contains)
                && seeds.Count(seed => seed != 0) >= 3
                && checkpointCount >= 10
                && reloadCount >= 10
                && rollbackCount >= 1
                && dynamicCharacteristicCaptureCount >= 40
                && dynamicCharacteristicRecallCount >= 40
                && sceneMemoryArtifactCount >= 40
                && sceneMemoryArtifactFailures == 0
                && qualificationMemoryCompletionCount >= 1
                && qualificationMemoryCompletionFailures == 0;
            bool highOverall = coverageHigh && allCategoriesHigh;
            Dictionary<string, object> scorecard = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["found"] = true,
                ["qualificationId"] = qualificationId,
                ["campaignId"] = campaignId,
                ["buildVersion"] = recordedBuildVersion,
                ["currentBuildVersion"] = currentBuildVersion,
                ["sameBuild"] = sameBuild,
                ["stateFingerprint"] = recordedStateFingerprint,
                ["expectedStateFingerprint"] =
                    expectedStateFingerprint,
                ["sameStateFingerprint"] = sameStateFingerprint,
                ["status"] = highOverall ? "high" : "qualifying",
                ["allHigh"] = highOverall,
                ["replyCount"] = replyCount,
                ["distinctNpcCount"] = heroIds.Count,
                ["modes"] = modes.OrderBy(value => value).ToList(),
                ["seeds"] = seeds.Where(seed => seed != 0).OrderBy(seed => seed).ToList(),
                ["checkpointCount"] = checkpointCount,
                ["reloadCount"] = reloadCount,
                ["rollbackCount"] = rollbackCount,
                ["runCount"] = allRuns.Count,
                ["completedRunCount"] = runs.Count,
                ["inProgressRunCount"] = inProgressRunCount,
                ["terminalFailedRunCount"] = terminalFailedRuns.Count,
                ["supersededRunCount"] = supersededRunIds.Count,
                ["terminalFailedRuns"] = terminalFailedRuns.Select(run => new Dictionary<string, object>
                {
                    ["runId"] = ReadString(run, "runId", ""),
                    ["label"] = ReadString(run, "label", ""),
                    ["status"] = ReadString(run, "status", ""),
                    ["updatedUtc"] = ReadString(run, "updatedUtc", "")
                }).ToList(),
                ["assertionCount"] = assertions.Count,
                ["failureCount"] = failures.Count + terminalFailedRuns.Count,
                ["promptBuildSampleCount"] = promptBuildSamples.Count,
                ["promptBuildP95Ms"] = promptBuildP95Ms,
                ["requiredPromptBuildP95Ms"] = 2000,
                ["dynamicCharacteristicCaptureCount"] = dynamicCharacteristicCaptureCount,
                ["requiredDynamicCharacteristicCaptureCount"] = 40,
                ["dynamicCharacteristicRecallCount"] = dynamicCharacteristicRecallCount,
                ["requiredDynamicCharacteristicRecallCount"] = 40,
                ["sceneMemoryArtifactCount"] = sceneMemoryArtifactCount,
                ["requiredSceneMemoryArtifactCount"] = 40,
                ["sceneMemoryArtifactFailures"] = sceneMemoryArtifactFailures,
                ["qualificationMemoryCompletionCount"] = qualificationMemoryCompletionCount,
                ["requiredQualificationMemoryCompletionCount"] = 1,
                ["qualificationMemoryCompletionFailures"] = qualificationMemoryCompletionFailures,
                ["relationshipHistoryDistinctPairCount"] =
                    relationshipHistoryPairs.Count,
                ["requiredRelationshipHistoryDistinctPairCount"] = 20,
                ["relationshipHistoryAsymmetricPairCount"] =
                    asymmetricRelationshipHistoryPairs.Count,
                ["requiredRelationshipHistoryAsymmetricPairCount"] = 5,
                ["relationshipHistoryColdReplies"] =
                    relationshipHistoryColdReplies,
                ["requiredRelationshipHistoryColdReplies"] = 2,
                ["relationshipHistoryReuseReplies"] =
                    relationshipHistoryReuseReplies,
                ["requiredRelationshipHistoryReuseReplies"] = 40,
                ["relationshipHistoryTransitionReplies"] =
                    relationshipHistoryTransitionReplies,
                ["requiredRelationshipHistoryTransitionReplies"] = 10,
                ["relationshipHistoryPersistenceReplies"] =
                    relationshipHistoryPersistenceReplies,
                ["requiredRelationshipHistoryPersistenceReplies"] = 20,
                ["relationshipHistoryPrivacyLeaks"] =
                    relationshipHistoryPrivacyLeaks,
                ["relationshipHistoryProviderSampleCount"] =
                    relationshipHistoryProviderSamples.Count,
                ["relationshipHistoryProviderP95Ms"] =
                    relationshipHistoryProviderP95Ms,
                ["manipulationRecommendedReplies"] =
                    manipulationRecommendedReplies,
                ["requiredManipulationRecommendedReplies"] = 10,
                ["manipulationRestrainedReplies"] =
                    manipulationRestrainedReplies,
                ["requiredManipulationRestrainedReplies"] = 10,
                ["manipulationDistinctTactics"] =
                    manipulationDistinctTactics,
                ["requiredManipulationDistinctTactics"] = 4,
                ["manipulationQuadrants"] =
                    manipulationQuadrants,
                ["requiredManipulationRepliesPerQuadrant"] = 6,
                ["manipulationLowHonorReplies"] =
                    manipulationLowHonorReplies,
                ["requiredManipulationLowHonorReplies"] = 40,
                ["manipulationAlignedTacticReplies"] =
                    manipulationAlignedTacticReplies,
                ["requiredManipulationAlignedTacticReplies"] = 40,
                ["manipulationHiddenWealthControls"] =
                    manipulationHiddenWealthControls,
                ["manipulationHiddenWealthSafe"] =
                    manipulationHiddenWealthSafe,
                ["requiredManipulationHiddenWealthControls"] = 4,
                ["manipulationBoldnessBands"] =
                    manipulationBoldnessBands,
                ["requiredManipulationBoldnessBands"] = 2,
                ["manipulationClanTiers"] =
                    manipulationClanTiers,
                ["requiredManipulationClanTiers"] = 6,
                ["targetRoster"] = ReadDictionary(state, "targetRoster"),
                ["categories"] = categories,
                ["coverageHigh"] = coverageHigh,
                ["reportPath"] = ConversationReadinessReportPath(campaignId, qualificationId),
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o")
            };
            WriteJsonObject(ConversationReadinessReportPath(campaignId, qualificationId), scorecard);
            if (highOverall)
            {
                state["status"] = "high";
                state["completedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                WriteJsonObject(ConversationReadinessStatePath(campaignId), state);
            }
            return scorecard;
        }

        private static bool ConversationReadinessRunIsCompleted(Dictionary<string, object> run)
        {
            return ReadString(run, "status", "")
                .Equals("completed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ConversationReadinessRunIsTerminalFailure(Dictionary<string, object> run)
        {
            return new[] { "failed", "cancelled", "interrupted" }
                .Contains(ReadString(run, "status", ""), StringComparer.OrdinalIgnoreCase);
        }

        private static List<Dictionary<string, object>> LoadConversationReadinessRuns(string campaignId, string qualificationId)
        {
            string root = LiveTestRunsRoot(campaignId);
            if (!Directory.Exists(root)) return new List<Dictionary<string, object>>();
            return Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                .Select(ReadJsonObject)
                .Where(run => string.Equals(ReadString(run, "qualificationId", ""), qualificationId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(run => ReadString(run, "startedUtc", ""))
                .ToList();
        }

        private static bool ConversationReadinessRunTouchesCategories(
            Dictionary<string, object> run,
            HashSet<string> categories)
        {
            if (run == null || categories == null || categories.Count == 0)
                return false;
            return ReadDictionaryList(run, "commands")
                .Where(command => ReadString(command, "operation", "")
                    .Equals("send", StringComparison.OrdinalIgnoreCase))
                .SelectMany(command => ReadStringList(
                    command, "readinessCategories"))
                .Any(categories.Contains);
        }

        private static int ConversationReadinessReplyCount(Dictionary<string, object> command)
        {
            Dictionary<string, object> result = ReadDictionary(command, "result") ?? new Dictionary<string, object>();
            int replies = ReadDictionaryList(result, "replies").Count;
            return replies > 0
                ? replies
                : string.IsNullOrWhiteSpace(FirstNonEmpty(ReadString(result, "reply", ""), ReadString(result, "text", ""))) ? 0 : 1;
        }

        private static bool ConversationReadinessCommandPassed(
            Dictionary<string, object> command,
            List<Dictionary<string, object>> assertions,
            List<Dictionary<string, object>> failures,
            string category)
        {
            string commandId = ReadString(command, "commandId", "");
            if (!ReadString(command, "status", "").Equals("completed", StringComparison.OrdinalIgnoreCase)) return false;
            if (ConversationReadinessReplyCount(command) == 0) return false;
            if (failures.Any(row =>
                    string.Equals(ReadString(row, "commandId", ""), commandId, StringComparison.OrdinalIgnoreCase)
                    && ReadBool(row, "critical", false)))
                return false;
            List<Dictionary<string, object>> rows = assertions
                .Where(row => string.Equals(ReadString(row, "commandId", ""), commandId, StringComparison.OrdinalIgnoreCase)).ToList();
            Dictionary<string, HashSet<string>> categoryAssertions =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["structural_pipeline"] = new HashSet<string>(new[]
                {
                    "minReplies", "requiresCorrelationIds", "requiresStructuralEvidence", "forbidSelfReaction"
                }, StringComparer.OrdinalIgnoreCase),
                ["identity_evidence"] = new HashSet<string>(new[]
                {
                    "requiresIdentityRecognition", "replyContains", "replyExcludes"
                }, StringComparer.OrdinalIgnoreCase),
                ["identity_role_authority"] = new HashSet<string>(new[]
                {
                    "requiresIdentityRecognition",
                    "requiresPoliticalAuthorityEvidence",
                    "requiresPoliticalConductEvidence",
                    "replyContains",
                    "replyExcludes",
                    "factualAccuracyRubric"
                }, StringComparer.OrdinalIgnoreCase),
                ["sovereign_demeanor"] = new HashSet<string>(new[]
                {
                    "requiresPoliticalAuthorityEvidence",
                    "requiresPoliticalConductEvidence",
                    "requiresGroupAwareness", "requiresGroupDivergence",
                    "replyContains", "replyExcludes",
                    "personalityRubric"
                }, StringComparer.OrdinalIgnoreCase),
                ["short_cross_scene_memory"] = new HashSet<string>(new[]
                {
                    "requiresRetrievalEvidence", "requiredRetrievedPhrases",
                    "forbiddenRetrievedPhrases", "replyContains", "replyExcludes"
                }, StringComparer.OrdinalIgnoreCase),
                ["long_term_memory"] = new HashSet<string>(new[]
                {
                    "requiresRetrievalEvidence", "requiredRetrievedPhrases",
                    "forbiddenRetrievedPhrases", "replyContains", "replyExcludes"
                }, StringComparer.OrdinalIgnoreCase),
                ["dynamic_characteristics"] = new HashSet<string>(new[]
                {
                    "requiresDynamicCharacteristicWrite", "requiresDynamicCharacteristicIntegrity",
                    "requiresDynamicCharacteristicRecall", "requiresDynamicCharacteristicOverflowSelection"
                }, StringComparer.OrdinalIgnoreCase),
                ["shared_relationship_history"] = new HashSet<string>(new[]
                {
                    "requiresSharedRelationshipHistoryEvidence",
                    "requiresSharedRelationshipHistoryPersistence",
                    "relationshipHistoryRubric"
                }, StringComparer.OrdinalIgnoreCase),
                ["group_awareness"] = new HashSet<string>(new[]
                {
                    "requiresGroupAwareness", "requiresGroupDivergence",
                    "requiresActionCompletionGrounding", "forbidSelfReaction",
                    "groupAwarenessRubric"
                }, StringComparer.OrdinalIgnoreCase),
                ["world_local_knowledge"] = new HashSet<string>(new[]
                {
                    "requiredContextPulls", "replyContains", "replyExcludes", "factualAccuracyRubric"
                }, StringComparer.OrdinalIgnoreCase),
                ["lie_relationship"] = new HashSet<string>(new[]
                {
                    "forbidVerifiedLiePenalty", "requiresVerifiedLieCheck", "requiresRelationshipReceipt", "forbidSelfReaction"
                }, StringComparer.OrdinalIgnoreCase),
                ["clan_tier_recognition"] = new HashSet<string>(new[]
                {
                    "requiresClanTierRecognitionEvidence", "clanTierRecognitionRubric"
                }, StringComparer.OrdinalIgnoreCase),
                ["manipulation_capabilities"] = new HashSet<string>(new[]
                {
                    "requiresManipulationEvidence", "manipulationRubric"
                }, StringComparer.OrdinalIgnoreCase),
                ["personality_consistency"] = new HashSet<string>(new[]
                {
                    "requiresPersonalityEvidence", "personalityRubric"
                }, StringComparer.OrdinalIgnoreCase),
                ["detailed_factual_accuracy"] = new HashSet<string>(new[]
                {
                    "requiredContextPulls", "requiresRetrievalEvidence",
                    "requiredRetrievedPhrases", "forbiddenRetrievedPhrases",
                    "replyContains", "replyExcludes",
                    "factualAccuracyRubric"
                }, StringComparer.OrdinalIgnoreCase),
                ["performance_resilience"] = new HashSet<string>(new[]
                {
                    "minReplies", "requiresCorrelationIds", "requiresStructuralEvidence",
                    "maxPromptChars", "requiresGroundedGuardedActionRouting",
                    "requiresActionCompletionGrounding"
                }, StringComparer.OrdinalIgnoreCase)
            };
            if (categoryAssertions.TryGetValue(category, out HashSet<string> relevant))
            {
                rows = rows.Where(row => relevant.Contains(ReadString(row, "assertion", ""))).ToList();
            }
            string requiredRubric = ConversationReadinessRequiredRubric(category);
            if (!string.IsNullOrWhiteSpace(requiredRubric)
                && !rows.Any(row => ReadString(row, "assertion", "")
                    .Equals(requiredRubric, StringComparison.OrdinalIgnoreCase)))
                return false;
            return rows.Count == 0 || rows.All(row => ReadBool(row, "passed", false));
        }

        private static string ConversationReadinessRequiredRubric(string category)
        {
            if (string.Equals(category, "personality_consistency", StringComparison.OrdinalIgnoreCase))
                return "personalityRubric";
            if (string.Equals(category, "sovereign_demeanor", StringComparison.OrdinalIgnoreCase))
                return "personalityRubric";
            if (string.Equals(category, "detailed_factual_accuracy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, "world_local_knowledge", StringComparison.OrdinalIgnoreCase)
                || string.Equals(category, "identity_role_authority", StringComparison.OrdinalIgnoreCase))
                return "factualAccuracyRubric";
            if (string.Equals(category, "group_awareness", StringComparison.OrdinalIgnoreCase))
                return "groupAwarenessRubric";
            if (string.Equals(category, "shared_relationship_history", StringComparison.OrdinalIgnoreCase))
                return "relationshipHistoryRubric";
            if (string.Equals(category, "clan_tier_recognition", StringComparison.OrdinalIgnoreCase))
                return "clanTierRecognitionRubric";
            if (string.Equals(category, "manipulation_capabilities", StringComparison.OrdinalIgnoreCase))
                return "manipulationRubric";
            return "";
        }

        private static bool ConversationReadinessCommandHasAssertion(
            Dictionary<string, object> command,
            List<Dictionary<string, object>> assertions,
            string assertion)
        {
            string commandId = ReadString(command, "commandId", "");
            return !string.IsNullOrWhiteSpace(commandId)
                && (assertions ?? new List<Dictionary<string, object>>()).Any(row =>
                    ReadString(row, "commandId", "").Equals(commandId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(row, "assertion", "").Equals(assertion, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<long> ConversationReadinessPromptBuildSamples(Dictionary<string, object> command)
        {
            Dictionary<string, object> result = ReadDictionary(command, "result") ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> replies = ReadDictionaryList(result, "replies");
            if (replies.Count > 0)
            {
                foreach (Dictionary<string, object> reply in replies)
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                    Dictionary<string, object> timing = ReadDictionary(raw, "timing") ?? ReadDictionary(reply, "timing");
                    if (timing != null) yield return ReadLong(timing, "promptBuildMs", -1);
                }
                yield break;
            }
            Dictionary<string, object> individualRaw = ReadDictionary(result, "rawResponse") ?? result;
            Dictionary<string, object> individualTiming = ReadDictionary(individualRaw, "timing") ?? ReadDictionary(result, "timing");
            if (individualTiming != null) yield return ReadLong(individualTiming, "promptBuildMs", -1);
        }

        private static int ConversationReadinessDynamicCharacteristicCaptureCount(Dictionary<string, object> command)
        {
            Dictionary<string, object> result = ReadDictionary(command, "result") ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> replies = ReadDictionaryList(result, "replies");
            if (replies.Count > 0)
            {
                return replies.Sum(reply =>
                {
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                    Dictionary<string, object> store = ReadDictionary(raw, "dynamicCharacteristicsStore")
                        ?? ReadDictionary(reply, "dynamicCharacteristicsStore")
                        ?? new Dictionary<string, object>();
                    return Math.Max(0, ReadInt(store, "storedCount", 0));
                });
            }
            Dictionary<string, object> individualRaw = ReadDictionary(result, "rawResponse") ?? result;
            Dictionary<string, object> individualStore = ReadDictionary(individualRaw, "dynamicCharacteristicsStore")
                ?? ReadDictionary(result, "dynamicCharacteristicsStore")
                ?? new Dictionary<string, object>();
            return Math.Max(0, ReadInt(individualStore, "storedCount", 0));
        }

        private static IEnumerable<Dictionary<string, object>>
            ConversationReadinessManipulationDecisions(
                Dictionary<string, object> command)
        {
            Dictionary<string, object> result =
                ReadDictionary(command, "result")
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> replies =
                ReadDictionaryList(result, "replies");
            if (replies.Count == 0) replies.Add(result);
            foreach (Dictionary<string, object> reply in replies)
            {
                Dictionary<string, object> raw =
                    ReadDictionary(reply, "rawResponse") ?? reply;
                Dictionary<string, object> motive =
                    ReadDictionary(raw, "motiveDecision")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> manipulation =
                    ReadDictionary(motive, "manipulation");
                if (manipulation != null && manipulation.Count > 0)
                {
                    Dictionary<string, object> decision =
                        new Dictionary<string, object>(
                            manipulation,
                            StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, object> brief =
                        ReadDictionary(raw, "decisionBrief")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> court =
                        ReadDictionary(motive, "courtCharacter")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> opportunity =
                        ReadDictionary(motive, "opportunity")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> requested =
                        ReadDictionary(command, "assertions")
                        ?? new Dictionary<string, object>();
                    string applied = ReadString(
                        brief, "courtTactic", "none");
                    List<string> allowed = ReadStringList(
                        manipulation, "allowedCourtTactics");
                    bool recommended = ReadBool(
                        manipulation, "recommended", false);
                    decision["appliedCourtTactic"] = applied;
                    decision["qualificationQuadrant"] =
                        ReadString(requested,
                            "expectedManipulationQuadrant", "");
                    decision["courtHonorLevel"] = ReadInt(
                        court, "honorLevel", int.MinValue);
                    decision["courtBoldnessLevel"] = ReadInt(
                        court, "boldnessLevel", int.MinValue);
                    decision["courtCharacterCell"] = ReadString(
                        court, "cellId", "");
                    decision["observerClanTier"] = ReadInt(
                        requested,
                        "expectedObserverClanTier",
                        int.MinValue);
                    decision["playerClanTier"] = ReadInt(
                        requested,
                        "expectedPlayerClanTier",
                        int.MinValue);
                    decision["wealthEvidenceBasis"] = ReadString(
                        opportunity, "wealthEvidenceBasis", "");
                    decision["economicCapacityKnown"] = ReadBool(
                        opportunity, "economicCapacityKnown", false);
                    decision["hiddenWealthControl"] = ReadBool(
                        requested, "hiddenWealthControl", false);
                    decision["tacticAlignmentValid"] = recommended
                        ? applied != "none"
                            && allowed.Contains(applied,
                                StringComparer.OrdinalIgnoreCase)
                        : applied == "none"
                            || string.IsNullOrWhiteSpace(applied);
                    yield return decision;
                }
            }
        }

        private static long PercentileNearestRank(List<long> sortedValues, double percentile)
        {
            if (sortedValues == null || sortedValues.Count == 0) return 0;
            int rank = (int)Math.Ceiling(Math.Max(0d, Math.Min(1d, percentile)) * sortedValues.Count);
            return sortedValues[Math.Max(0, Math.Min(sortedValues.Count - 1, rank - 1))];
        }

        private static string ConversationReadinessRoot(string campaignId)
        {
            string path = CampaignFile(campaignId, "audit", "test-data", "conversation-readiness");
            Directory.CreateDirectory(path);
            return path;
        }
        private static string ConversationReadinessStatePath(string campaignId)
        {
            return Path.Combine(ConversationReadinessRoot(campaignId), "active.json");
        }
        private static string ConversationReadinessReportPath(string campaignId, string qualificationId)
        {
            return Path.Combine(ConversationReadinessRoot(campaignId), SafePathSegment(qualificationId, "qualification") + ".json");
        }
    }
}
