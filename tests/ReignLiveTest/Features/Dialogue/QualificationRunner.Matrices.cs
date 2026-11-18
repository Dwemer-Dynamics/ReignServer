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
        private static List<Dictionary<string, object>>
            BuildClanTierRecognitionQualificationCases(
                List<Dictionary<string, object>> nativeTargets,
                string qualificationId)
        {
            List<Dictionary<string, object>> ordered =
                OrderQualificationTargetsByUsage(
                    nativeTargets, qualificationId)
                    .Where(target =>
                        ReadLong(target, "age", 0) >= 18
                        && ReadLong(target, "clanTier", 0) >= 0
                        && ReadLong(target, "clanTier", 0) <= 6)
                    .ToList();
            List<Dictionary<string, object>> tierZeroNotables =
                ordered.Where(target =>
                    !ReadBoolean(target, "isLord")
                    && ReadBoolean(target, "isNotable")
                    && ReadLong(target, "clanTier", -1) == 0)
                    .ToList();
            List<Dictionary<string, object>> tierZeroWanderers =
                ordered.Where(target =>
                    !ReadBoolean(target, "isLord")
                    && ReadBoolean(target, "isWanderer")
                    && ReadLong(target, "clanTier", -1) == 0)
                    .ToList();
            Dictionary<int, List<Dictionary<string, object>>> byTier =
                Enumerable.Range(1, 6).ToDictionary(
                    tier => tier,
                    tier => ordered.Where(target =>
                        ReadBoolean(target, "isLord")
                        && ReadLong(target, "clanTier", -1)
                            == tier).ToList());
            List<int> missingTiers = byTier
                .Where(pair => pair.Value.Count == 0)
                .Select(pair => pair.Key).ToList();
            if (missingTiers.Count > 0)
                throw new InvalidOperationException(
                    "Clan-tier qualification requires at least one living "
                    + "adult noble at every native clan tier one through six. "
                    + "Missing tiers=" + string.Join(",", missingTiers)
                    + "; available adult nobles=" + ordered.Count + ".");
            if (tierZeroNotables.Count == 0
                || tierZeroWanderers.Count == 0)
                throw new InvalidOperationException(
                    "Clan-tier qualification requires at least one living "
                    + "adult tier-zero notable and one living adult "
                    + "tier-zero wanderer. Available tier-zero notables="
                    + tierZeroNotables.Count
                    + "; wanderers=" + tierZeroWanderers.Count + ".");

            List<Dictionary<string, object>> specifications =
                new List<Dictionary<string, object>>();
            specifications.Add(
                new Dictionary<string, object>
                {
                    ["observerTier"] = 0,
                    ["playerTier"] = 0,
                    ["matrixBand"] = "peer",
                    ["targetKind"] = "notable"
                });
            specifications.Add(
                new Dictionary<string, object>
                {
                    ["observerTier"] = 0,
                    ["playerTier"] = 0,
                    ["matrixBand"] = "peer",
                    ["targetKind"] = "wanderer"
                });
            for (int tier = 1; tier <= 6; tier++)
            {
                for (int repeat = 0; repeat < 2; repeat++)
                    specifications.Add(
                        new Dictionary<string, object>
                        {
                            ["observerTier"] = tier,
                            ["playerTier"] = tier,
                            ["matrixBand"] = "peer",
                            ["targetKind"] = "noble"
                        });
            }
            for (int observerTier = 1;
                observerTier <= 5;
                observerTier++)
            {
                specifications.Add(
                    new Dictionary<string, object>
                    {
                        ["observerTier"] = observerTier,
                        ["playerTier"] = observerTier - 1,
                        ["matrixBand"] = "lower"
                    });
                specifications.Add(
                    new Dictionary<string, object>
                    {
                        ["observerTier"] = observerTier,
                        ["playerTier"] = observerTier + 1,
                        ["matrixBand"] = "higher"
                    });
            }
            foreach (int observerTier in new[] { 2, 3, 6 })
                specifications.Add(
                    new Dictionary<string, object>
                    {
                        ["observerTier"] = observerTier,
                        ["playerTier"] = observerTier - 2,
                        ["matrixBand"] = "substantially_lower"
                    });
            foreach (int observerTier in new[] { 1, 3, 4 })
                specifications.Add(
                    new Dictionary<string, object>
                    {
                        ["observerTier"] = observerTier,
                        ["playerTier"] = observerTier + 2,
                        ["matrixBand"] = "substantially_higher"
                    });

            Dictionary<string, int> uses =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> result =
                new List<Dictionary<string, object>>();
            for (int ordinal = 0;
                ordinal < specifications.Count;
                ordinal++)
            {
                Dictionary<string, object> specification =
                    specifications[ordinal];
                int observerTier = (int)ReadLong(
                    specification, "observerTier", 0);
                int playerTier = (int)ReadLong(
                    specification, "playerTier", 0);
                string targetKind = String(
                    specification, "targetKind");
                List<Dictionary<string, object>> candidates =
                    observerTier == 0
                        ? targetKind.Equals(
                                "wanderer",
                                StringComparison.OrdinalIgnoreCase)
                            ? tierZeroWanderers
                            : tierZeroNotables
                        : byTier[observerTier];
                Dictionary<string, object> chosen =
                    candidates
                        .OrderBy(target =>
                            uses.TryGetValue(
                                String(target, "heroId"),
                                out int used)
                                ? used : 0)
                        .ThenBy(target =>
                            QualificationStableOrder(
                                (qualificationId ?? "")
                                + "|clan-tier|"
                                + ordinal + "|"
                                + String(target, "heroId")))
                        .First();
                string chosenId = String(chosen, "heroId");
                uses[chosenId] = uses.TryGetValue(
                    chosenId, out int prior)
                    ? prior + 1 : 1;
                Dictionary<string, object> row =
                    new Dictionary<string, object>(
                        chosen,
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["caseOrdinal"] = ordinal,
                        ["observerClanTier"] = observerTier,
                        ["playerClanTier"] = playerTier,
                        ["expectedRelativeClanTierBand"] =
                            QualificationRelativeClanTierBand(
                                playerTier - observerTier),
                        ["matrixBand"] = String(
                            specification, "matrixBand"),
                        ["targetKind"] = observerTier == 0
                            ? targetKind
                            : "noble",
                        // Keep wallet state private and constant so rank—not
                        // visible wealth—drives this focused recognition gate.
                        ["playerGold"] = 100,
                        ["wealthEvidence"] = "hidden"
                    };
                result.Add(row);
            }
            if (result.Count != 30
                || result.Where(row =>
                        String(row, "matrixBand").Equals(
                            "peer",
                            StringComparison.OrdinalIgnoreCase))
                    .Select(row => ReadLong(
                        row, "observerClanTier", -1))
                    .Distinct().Count() != 7
                || result.Select(row => ReadLong(
                        row, "observerClanTier", -1))
                    .Distinct().Count() != 7
                || result.Select(row => ReadLong(
                        row, "playerClanTier", -1))
                    .Distinct().Count() != 7
                || result.Count(row =>
                        ReadLong(row,
                            "observerClanTier", -1) == 0
                        && String(row, "targetKind").Equals(
                            "notable",
                            StringComparison.OrdinalIgnoreCase))
                    != 1
                || result.Count(row =>
                        ReadLong(row,
                            "observerClanTier", -1) == 0
                        && String(row, "targetKind").Equals(
                            "wanderer",
                            StringComparison.OrdinalIgnoreCase))
                    != 1
                || result.Select(row => String(
                        row, "expectedRelativeClanTierBand"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() != 5)
            {
                throw new InvalidOperationException(
                    "The 30-probe clan-tier matrix must cover equal-rank "
                    + "cases for a tier-zero notable, a tier-zero wanderer, "
                    + "and nobles at every native tier one through six; "
                    + "player tiers zero through six; and all five "
                    + "relative-rank bands.");
            }
            return result;
        }

        private static Dictionary<string, object>
            RunClanTierRecognitionQualification(
                string campaignId,
                string qualificationId,
                List<Dictionary<string, object>> cases,
                Dictionary<string, object> plannedCoverage)
        {
            int completedRuns = 0;
            List<Dictionary<string, object>> failures =
                new List<Dictionary<string, object>>();
            try
            {
                for (int probe = 0; probe < cases.Count; probe++)
                {
                    Dictionary<string, object> qualificationCase =
                        cases[probe];
                    string targetId = String(
                        qualificationCase, "heroId");
                    RunQualificationScenario(
                        campaignId,
                        qualificationId,
                        QualificationSeeds[
                            probe % QualificationSeeds.Length],
                        "individual_chat",
                        "Clan-tier recognition qualification "
                            + probe
                            + " target " + targetId
                            + " observer-tier "
                            + ReadLong(qualificationCase,
                                "observerClanTier", 0)
                            + " player-tier "
                            + ReadLong(qualificationCase,
                                "playerClanTier", 0)
                            + " band "
                            + String(qualificationCase,
                                "expectedRelativeClanTierBand"),
                        new List<Dictionary<string, object>>
                        {
                            ManipulationFixtureStep(
                                qualificationCase),
                            OpenStep(
                                "individual_chat",
                                new[] { targetId }),
                            ClanTierRecognitionProbe(
                                probe,
                                qualificationCase),
                            CloseStep("individual_chat")
                        },
                        failures);
                    completedRuns++;
                }
            }
            finally
            {
                try
                {
                    RunQualificationScenario(
                        campaignId,
                        qualificationId,
                        QualificationSeeds[0],
                        "individual_chat",
                        "Restore clan-tier recognition fixture",
                        new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["schemaVersion"] = 2,
                                ["operation"] =
                                    "restore_manipulation_fixture",
                                ["mode"] = "individual_chat",
                                ["timeoutSeconds"] = 120
                            }
                        },
                        failures);
                    completedRuns++;
                }
                catch (Exception ex)
                {
                    failures.Add(
                        new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["error"] =
                                "Native clan-tier fixture restoration failed: "
                                + ex.Message
                        });
                }
                Dictionary<string, object> identityRestore = Post(
                    "/tests/live/fixtures/manipulation",
                    new Dictionary<string, object>
                    {
                        ["confirmation"] = "qualification_fixture",
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = qualificationId,
                        ["operation"] = "restore"
                    });
                if (!IsOk(identityRestore))
                    failures.Add(identityRestore);
            }
            Dictionary<string, object> focused = Get(
                "/tests/live/readiness?campaignId="
                + Uri.EscapeDataString(campaignId));
            focused["controllerCompletedRuns"] = completedRuns;
            focused["controllerFailureCount"] = failures.Count;
            focused["controllerFailures"] = failures;
            focused["qualificationId"] = qualificationId;
            focused["qualificationFocus"] =
                "clan_tier_recognition";
            focused["plannedCoverage"] = plannedCoverage;
            focused["focusedReplyPlan"] =
                new Dictionary<string, object>
                {
                    ["totalReplies"] = 30,
                    ["peerReplies"] = 14,
                    ["equalTierReplies"] = 14,
                    ["equalTierClanLevels"] =
                        new[] { 0, 1, 2, 3, 4, 5, 6 },
                    ["tierZeroNotableReplies"] = 1,
                    ["tierZeroWandererReplies"] = 1,
                    ["lowerReplies"] = 5,
                    ["higherReplies"] = 5,
                    ["substantiallyLowerReplies"] = 3,
                    ["substantiallyHigherReplies"] = 3,
                    ["observerClanTiers"] = 7,
                    ["playerClanTiers"] = 7
                };
            return focused;
        }

        private static List<Dictionary<string, object>>
            BuildManipulationQualificationCases(
                List<Dictionary<string, object>> profiles)
        {
            List<Dictionary<string, object>> candidates =
                (profiles ?? new List<Dictionary<string, object>>())
                    .Where(row =>
                        ReadBoolean(row, "courtCharacterAvailable")
                        && ReadBoolean(row, "supportsManipulation")
                        && ReadLong(row, "honorLevel", 0) < 0
                        && ReadLong(row, "age", 0) >= 18
                        && ReadLong(row, "clanTier", 0) >= 1
                        && ReadLong(row, "clanTier", 0) <= 6)
                    .ToList();
            Dictionary<int, List<Dictionary<string, object>>>
                candidatesByTier = Enumerable.Range(1, 6)
                    .ToDictionary(
                        tier => tier,
                        tier => candidates.Where(row =>
                            ReadLong(row, "clanTier", -1)
                                == tier).ToList());
            List<int> missingTiers = candidatesByTier
                .Where(pair => pair.Value.Count == 0)
                .Select(pair => pair.Key).ToList();
            if (missingTiers.Count > 0)
                throw new InvalidOperationException(
                    "Manipulation qualification requires at least one "
                    + "living adult low-honor Court Character at every "
                    + "native noble clan tier from one through six. Missing tiers="
                    + string.Join(",", missingTiers)
                    + "; available low-honor Court Characters="
                    + candidates.Count + ".");

            List<Dictionary<string, object>> matrix =
                new List<Dictionary<string, object>>();
            for (int tier = 1; tier <= 6; tier++)
            {
                foreach (bool wealthy in new[] { false, true })
                    matrix.Add(new Dictionary<string, object>
                    {
                        ["relativeTier"] = "equal",
                        ["observerTier"] = tier,
                        ["wealthy"] = wealthy
                    });
            }
            for (int index = 0; index < 7; index++)
            {
                int tier = 1 + index % 6;
                matrix.Add(new Dictionary<string, object>
                {
                    ["relativeTier"] = "lower",
                    ["observerTier"] = tier,
                    ["wealthy"] = false
                });
                matrix.Add(new Dictionary<string, object>
                {
                    ["relativeTier"] = "lower",
                    ["observerTier"] = tier,
                    ["wealthy"] = true
                });
            }
            for (int index = 0; index < 7; index++)
            {
                int tier = 1 + index % 5;
                matrix.Add(new Dictionary<string, object>
                {
                    ["relativeTier"] = "higher",
                    ["observerTier"] = tier,
                    ["wealthy"] = false
                });
                matrix.Add(new Dictionary<string, object>
                {
                    ["relativeTier"] = "higher",
                    ["observerTier"] = tier,
                    ["wealthy"] = true
                });
            }

            Dictionary<string, int> useCounts =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<int, Dictionary<string, object>>
                equalTargetByTier =
                    new Dictionary<int,
                        Dictionary<string, object>>();
            List<Dictionary<string, object>> result =
                new List<Dictionary<string, object>>();
            int wealthyOrdinal = 0;
            for (int caseOrdinal = 0;
                caseOrdinal < matrix.Count;
                caseOrdinal++)
            {
                Dictionary<string, object> specification =
                    matrix[caseOrdinal];
                string relativeTier =
                    String(specification, "relativeTier");
                int observerTier = (int)ReadLong(
                    specification, "observerTier", 0);
                bool wealthy = ReadBoolean(
                    specification, "wealthy");
                int requestedBoldness =
                    -2 + caseOrdinal % 5;
                bool requestedFemale =
                    caseOrdinal % 2 == 0;
                Dictionary<string, object> chosen;
                if (relativeTier == "equal"
                    && equalTargetByTier.TryGetValue(
                        observerTier, out chosen))
                {
                    // Use the same Court Character for poor/wealthy peer
                    // cases at each tier, isolating visible wealth while
                    // keeping personality and relative rank constant.
                }
                else
                {
                    chosen = candidatesByTier[observerTier]
                        .OrderBy(row =>
                            useCounts.TryGetValue(
                                String(row, "heroId"),
                                out int used)
                                ? used : 0)
                        .ThenBy(row => Math.Abs(
                            ReadLong(
                                row, "boldnessLevel", 0)
                            - requestedBoldness))
                        .ThenBy(row =>
                            ReadBoolean(row, "isFemale")
                                == requestedFemale ? 0 : 1)
                        .ThenBy(row => ReadLong(
                            row, "priorDialogueLineCount", 500))
                        .ThenBy(row => ReadLong(
                            row, "honorLevel", 0))
                        .ThenByDescending(row => ReadLong(
                            row, "influencePotential", 0))
                        .ThenBy(row => String(
                            row, "heroId"),
                            StringComparer.OrdinalIgnoreCase)
                        .First();
                    if (relativeTier == "equal")
                        equalTargetByTier[observerTier] =
                            chosen;
                }
                string chosenId = String(chosen, "heroId");
                useCounts[chosenId] =
                    useCounts.TryGetValue(chosenId, out int prior)
                        ? prior + 1 : 1;
                int playerTier = relativeTier == "equal"
                    ? observerTier
                    : relativeTier == "higher"
                        ? Math.Min(6, observerTier + 2)
                        : Math.Max(0, observerTier - 2);
                int delta = playerTier - observerTier;
                string quadrant = relativeTier
                    + "_tier_"
                    + (wealthy ? "wealthy" : "poor");
                bool hiddenWealthControl = wealthy
                    && wealthyOrdinal++ % 5 == 0;
                Dictionary<string, object> row =
                    new Dictionary<string, object>(
                        chosen,
                        StringComparer.OrdinalIgnoreCase)
                {
                    ["quadrant"] = quadrant,
                    ["matrixRound"] =
                        relativeTier == "equal"
                            ? 0
                            : relativeTier == "lower"
                                ? 1 : 2,
                    ["matrixTargetIndex"] = observerTier,
                    ["caseOrdinal"] = caseOrdinal,
                    ["playerClanTier"] = playerTier,
                    ["playerGold"] = wealthy ? 50000000 : 100,
                    ["wealthEvidence"] = hiddenWealthControl
                        ? "hidden" : "demonstrated",
                    ["expectedWealthEvidenceBasis"] =
                        hiddenWealthControl
                            ? "hidden_wallet_not_observable"
                            : "demonstrated_financial_capacity",
                    ["hiddenWealthControl"] =
                        hiddenWealthControl,
                    ["expectedRelativeClanTierBand"] =
                        QualificationRelativeClanTierBand(delta)
                };
                result.Add(row);
            }
            if (result.Count != 40
                || new Dictionary<string, int>
                {
                    ["equal_tier_poor"] = 6,
                    ["equal_tier_wealthy"] = 6,
                    ["lower_tier_poor"] = 7,
                    ["lower_tier_wealthy"] = 7,
                    ["higher_tier_poor"] = 7,
                    ["higher_tier_wealthy"] = 7
                }.Any(required =>
                    result.Count(row => String(
                        row, "quadrant").Equals(
                            required.Key,
                            StringComparison.OrdinalIgnoreCase))
                        != required.Value)
                || result.Where(row => String(
                        row, "quadrant").StartsWith(
                            "equal_", StringComparison.Ordinal))
                    .Select(row => ReadLong(
                        row, "matrixTargetIndex", -1))
                    .Distinct().Count() != 6
                || result.Select(row => String(row, "heroId"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() < 6
                || result.Count(row =>
                    ReadBoolean(row, "hiddenWealthControl")) != 4)
                throw new InvalidOperationException(
                    "The counterbalanced manipulation matrix did not "
                    + "produce all six tier/wealth cells, peer cases at "
                    + "every noble clan tier one through six, four hidden-wallet "
                    + "controls, and at least six authentic low-honor "
                    + "Court Character targets.");
            return result;
        }

        private static string QualificationRelativeClanTierBand(int delta)
        {
            if (delta <= -2) return "substantially_lower";
            if (delta == -1) return "lower";
            if (delta == 0) return "peer";
            if (delta == 1) return "higher";
            return "substantially_higher";
        }

        private static Dictionary<string, object>
            ManipulationFixtureStep(
                Dictionary<string, object> manipulationCase)
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "prepare_manipulation_fixture",
                ["mode"] = "individual_chat",
                ["playerClanTier"] = ReadLong(
                    manipulationCase, "playerClanTier", 0),
                ["playerGold"] = ReadLong(
                    manipulationCase, "playerGold", 0),
                ["wealthEvidence"] = String(
                    manipulationCase, "wealthEvidence"),
                ["timeoutSeconds"] = 120
            };
        }

        private static Dictionary<string, object> RunQualificationScenario(
            string campaignId,
            string qualificationId,
            long seed,
            string mode,
            string label,
            List<Dictionary<string, object>> steps,
            List<Dictionary<string, object>> failures)
        {
            if (QualificationRunsToSkip > 0)
            {
                QualificationRunsToSkip--;
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = "completed",
                    ["resumedSkip"] = true,
                    ["label"] = label,
                    ["remainingCompletedRunsToSkip"] = QualificationRunsToSkip
                };
            }
            Post("/tests/live/arm", new Dictionary<string, object>
            {
                ["confirmation"] = "arm", ["minutes"] = 720, ["requestedBy"] = "ReignLiveTest qualification renewal"
            });
            Dictionary<string, object> runtime =
                QualificationRuntimeWithHeartbeatGrace(campaignId);
            Dictionary<string, object> startPayload = new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["runId"] = QualificationScenarioRunId(
                    qualificationId, mode, label, seed),
                ["campaignId"] = campaignId,
                ["gameInstanceId"] = RuntimeInstance(runtime),
                ["mode"] = mode,
                ["presentation"] = "headless",
                ["effects"] = "guarded",
                ["label"] = label,
                ["seed"] = seed,
                ["qualificationSeed"] = seed,
                ["qualificationId"] = qualificationId,
                ["autoCompleteWhenIdle"] = true,
                ["steps"] = steps
            };
            Dictionary<string, object> started =
                StartQualificationScenarioRunWithReconciliation(
                    campaignId, startPayload);
            if (!IsOk(started))
            {
                failures.Add(started);
                throw new InvalidOperationException(
                    "Qualification scenario did not start after bounded "
                    + "idempotent reconciliation: " + label + "; "
                    + Json.Serialize(started));
            }
            Dictionary<string, object> completed = WaitForRun(
                campaignId, String(started, "runId"), "", mode == "individual_chat" ? 1800 : 3600, true);
            if (!IsOk(completed) || !String(completed, "status").Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(completed);
                return completed;
            }
            string runId = String(started, "runId");
            Dictionary<string, object> report = Get("/tests/live/run/report?campaignId="
                + Uri.EscapeDataString(campaignId)
                + "&runId=" + Uri.EscapeDataString(runId));
            Dictionary<string, object> evaluation =
                EvaluateQualificationRunWithReconciliation(
                    campaignId, runId, report);
            if (!IsOk(evaluation))
                failures.Add(evaluation);
            return Get("/tests/live/run/report?campaignId="
                + Uri.EscapeDataString(campaignId)
                + "&runId=" + Uri.EscapeDataString(runId));
        }

        private static void EnsureQualificationPartyFixture(
            string campaignId,
            string qualificationId,
            List<string> candidateHeroIds)
        {
            Dictionary<string, object> runtime =
                QualificationRuntimeWithHeartbeatGrace(campaignId);
            string gameInstanceId = RuntimeInstance(runtime);
            string normalizedInstance = new string(
                (gameInstanceId ?? string.Empty)
                    .Where(char.IsLetterOrDigit).ToArray());
            string runSuffix = normalizedInstance.Length <= 12
                ? normalizedInstance
                : normalizedInstance.Substring(
                    normalizedInstance.Length - 12);
            Dictionary<string, object> payload =
                new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["runId"] = "live-party-fixture-" + runSuffix,
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] = gameInstanceId,
                    ["mode"] = "party_chat",
                    ["presentation"] = "headless",
                    ["effects"] = "guarded",
                    ["label"] = "Prepare disposable qualification party",
                    ["autoCompleteWhenIdle"] = true,
                    ["steps"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["schemaVersion"] = 2,
                            ["operation"] = "prepare_party_fixture",
                            ["mode"] = "party_chat",
                            ["desiredCount"] = 5,
                            ["targetSearches"] =
                                candidateHeroIds.ToArray(),
                            ["timeoutSeconds"] = 120
                        }
                    }
                };
            Dictionary<string, object> started =
                StartQualificationScenarioRunWithReconciliation(
                    campaignId, payload);
            if (!IsOk(started))
                throw new InvalidOperationException(
                    "Could not start the disposable party fixture: "
                    + Json.Serialize(started));
            Dictionary<string, object> completed = WaitForRun(
                campaignId,
                String(started, "runId"),
                "",
                300,
                true);
            if (!IsOk(completed)
                || !String(completed, "status").Equals(
                    "completed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Could not prepare five native party-chat participants: "
                    + Json.Serialize(completed));
        }

        private static Dictionary<string, object>
            EnsureQualificationAuthorityFixture(
                string campaignId,
                string qualificationId)
        {
            Dictionary<string, object> runtime =
                QualificationRuntimeWithHeartbeatGrace(campaignId);
            string gameInstanceId = RuntimeInstance(runtime);
            string normalizedInstance = new string(
                (gameInstanceId ?? string.Empty)
                    .Where(char.IsLetterOrDigit).ToArray());
            string runSuffix = normalizedInstance.Length <= 12
                ? normalizedInstance
                : normalizedInstance.Substring(
                    normalizedInstance.Length - 12);
            Dictionary<string, object> payload =
                new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["runId"] =
                        "live-authority-fixture-" + runSuffix,
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] = gameInstanceId,
                    ["mode"] = "individual_chat",
                    ["presentation"] = "headless",
                    ["effects"] = "guarded",
                    ["label"] =
                        "Prepare disposable sovereign authority fixture",
                    ["qualificationId"] = qualificationId,
                    ["autoCompleteWhenIdle"] = true,
                    ["steps"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["schemaVersion"] = 2,
                            ["operation"] =
                                "prepare_authority_fixture",
                            ["mode"] = "individual_chat",
                            ["confirmDisposableCampaign"] = true,
                            ["timeoutSeconds"] = 300
                        }
                    }
                };
            Dictionary<string, object> started =
                StartQualificationScenarioRunWithReconciliation(
                    campaignId, payload);
            if (!IsOk(started))
                throw new InvalidOperationException(
                    "Could not start the disposable sovereign authority fixture: "
                    + Json.Serialize(started));
            Dictionary<string, object> completed =
                WaitForRun(
                    campaignId,
                    String(started, "runId"),
                    "",
                    600,
                    true);
            if (!IsOk(completed)
                || !String(completed, "status").Equals(
                    "completed",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Could not prepare the native sovereign authority fixture: "
                    + Json.Serialize(completed));
            Dictionary<string, object> command =
                ReadObjects(completed, "commands")
                    .LastOrDefault()
                ?? new Dictionary<string, object>();
            Dictionary<string, object> result =
                ReadObject(command, "result");
            if (!ReadBoolean(result, "playerIsRuler")
                || !ReadBoolean(
                    ReadObject(result, "nativeInvariant"),
                    "sameClanWitness")
                || !ReadBoolean(
                    ReadObject(result, "nativeInvariant"),
                    "immediateFamilyWitness")
                || ReadLong(
                    ReadObject(result, "nativeInvariant"),
                    "subjectVassalWitnessCount", 0) < 2
                || !ReadBoolean(
                    ReadObject(result, "nativeInvariant"),
                    "playerOwnsSettlement")
                || ReadBoolean(
                    ReadObject(result, "nativeInvariant"),
                    "playerGovernsSettlement"))
                throw new InvalidOperationException(
                    "The authority fixture command completed without proving all required native invariants: "
                    + Json.Serialize(result));
            return result;
        }

        private static string QualificationScenarioRunId(
            string qualificationId,
            string mode,
            string label,
            long seed)
        {
            string identity = string.Join("|", new[]
            {
                qualificationId ?? "",
                QualificationScenarioBuildVersion ?? "",
                mode ?? "",
                label ?? "",
                seed.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            using (System.Security.Cryptography.SHA256 sha =
                System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(
                    System.Text.Encoding.UTF8.GetBytes(identity));
                string hex = BitConverter.ToString(hash)
                    .Replace("-", "").ToLowerInvariant();
                // Correlation ids repeat the run id and command id inside
                // Windows snapshot filenames. Keep this deterministic id no
                // longer than the former timestamp-based ids so the installed
                // module's deep campaign path remains below MAX_PATH.
                return "live-q-" + hex.Substring(0, 20);
            }
        }

        private static Dictionary<string, object>
            StartQualificationScenarioRunWithReconciliation(
                string campaignId,
                Dictionary<string, object> payload)
        {
            string requestedRunId = String(payload, "runId");
            Dictionary<string, object> last = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = "The qualification scenario was not submitted."
            };
            for (int attempt = 1; attempt <= 30; attempt++)
            {
                try
                {
                    Dictionary<string, object> started =
                        Post("/tests/live/run/start", payload);
                    if (IsOk(started))
                    {
                        started["qualificationStartAttempt"] = attempt;
                        return started;
                    }
                    last = started;
                    string error = String(started, "error");
                    bool transient =
                        error.IndexOf("already active",
                            StringComparison.OrdinalIgnoreCase) >= 0
                        || error.IndexOf("fresh loaded-game heartbeat",
                            StringComparison.OrdinalIgnoreCase) >= 0
                        || error.IndexOf("Save Sync alignment",
                            StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!transient) break;
                }
                catch (Exception ex)
                {
                    last = new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["error"] = ex.Message,
                        ["ambiguousTransport"] = true
                    };
                }

                try
                {
                    Dictionary<string, object> durable = Get(
                        "/tests/live/run/report?campaignId="
                        + Uri.EscapeDataString(campaignId)
                        + "&runId=" + Uri.EscapeDataString(requestedRunId));
                    if (IsOk(durable)
                        && ReadBoolean(durable, "found")
                        && String(durable, "runId").Equals(
                            requestedRunId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        durable["qualificationStartReconciled"] = true;
                        durable["qualificationStartAttempt"] = attempt;
                        return durable;
                    }
                }
                catch
                {
                    // The stable requested run id makes another start attempt
                    // safe even when this read is also temporarily unavailable.
                }
                Thread.Sleep(2000);
                Dictionary<string, object> runtime =
                    QualificationRuntimeWithHeartbeatGrace(campaignId);
                payload["gameInstanceId"] = RuntimeInstance(runtime);
            }
            last["requestedRunId"] = requestedRunId;
            last["boundedAttempts"] = 30;
            return last;
        }

        private static Dictionary<string, object>
            EvaluateQualificationRunWithReconciliation(
                string campaignId,
                string runId,
                Dictionary<string, object> completedReport)
        {
            if (QualificationRunEvaluationComplete(completedReport))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["runId"] = runId,
                    ["reconciled"] = true,
                    ["evaluationAlreadyComplete"] = true
                };
            }

            try
            {
                return PostWithTimeout(
                    "/tests/live/readiness/evaluate-run",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["runId"] = runId
                    },
                    ReadinessEvaluationTimeoutSeconds);
            }
            catch (TaskCanceledException)
            {
                // The server may still be completing the idempotent blinded
                // evaluation after the client timeout. Reconcile its durable
                // report; never replay or resubmit the dialogue run.
            }
            catch (System.Net.Http.HttpRequestException)
            {
                // Treat ambiguous transport exactly like a timeout and inspect
                // the correlation-bearing durable report before any retry.
            }

            Dictionary<string, object> latest = completedReport;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                Thread.Sleep(5000);
                latest = Get("/tests/live/run/report?campaignId="
                    + Uri.EscapeDataString(campaignId)
                    + "&runId=" + Uri.EscapeDataString(runId));
                if (QualificationRunEvaluationComplete(latest))
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["runId"] = runId,
                        ["reconciled"] = true,
                        ["evaluationCompletedAfterAmbiguousTransport"] = true
                    };
                }
            }

            throw new TimeoutException(
                "Blinded readiness evaluation did not become durable within "
                + ReadinessEvaluationTimeoutSeconds
                + " seconds plus the bounded reconciliation interval. "
                + "The completed dialogue run was not replayed. Last report: "
                + Json.Serialize(latest));
        }

        private static bool QualificationRunEvaluationComplete(
            Dictionary<string, object> report)
        {
            HashSet<string> rubricCategories = new HashSet<string>(
                new[]
                {
                    "personality_consistency", "detailed_factual_accuracy",
                    "world_local_knowledge", "group_awareness",
                    "shared_relationship_history", "clan_tier_recognition",
                    "manipulation_capabilities"
                },
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> required = new HashSet<string>(
                ReadObjects(report, "commands")
                    .Where(command =>
                        String(command, "operation").Equals(
                            "send", StringComparison.OrdinalIgnoreCase)
                        && String(command, "status").Equals(
                            "completed", StringComparison.OrdinalIgnoreCase)
                        && QualificationReadStrings(
                            command, "readinessCategories")
                            .Any(rubricCategories.Contains))
                    .Select(command => String(command, "commandId"))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> evaluated = new HashSet<string>(
                ReadObjects(report, "readinessEvaluations")
                    .Select(row => String(row, "commandId"))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            return required.All(evaluated.Contains);
        }

        private static List<string> QualificationReadStrings(
            Dictionary<string, object> value,
            string key)
        {
            if (value == null
                || !value.TryGetValue(key, out object raw)
                || raw == null)
                return new List<string>();
            if (raw is ArrayList list)
                return list.Cast<object>().Select(Convert.ToString)
                    .Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
            if (raw is object[] array)
                return array.Select(Convert.ToString)
                    .Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
            if (raw is IEnumerable<string> strings)
                return strings.Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList();
            return new List<string> { Convert.ToString(raw) }
                .Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
        }

        private static Dictionary<string, object>
            QualificationRuntimeWithHeartbeatGrace(string campaignId)
        {
            Dictionary<string, object> runtime =
                new Dictionary<string, object>();
            // A backgrounded Bannerlord host can miss several ordinary
            // heartbeat intervals while a large campaign census or Save Sync
            // boundary is being reconciled. Give the still-running native host
            // a bounded five-minute recovery window, while continuing to reject
            // a heartbeat from the wrong campaign immediately.
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(5);
            int attempts = 0;
            while (DateTimeOffset.UtcNow < deadline)
            {
                attempts++;
                runtime = Get("/tests/live/runtime?campaignId="
                    + Uri.EscapeDataString(campaignId));
                Dictionary<string, object> native =
                    ReadObject(runtime, "runtime");
                string observedCampaignId = String(native, "campaignId");
                if (!string.IsNullOrWhiteSpace(observedCampaignId)
                    && !observedCampaignId.Equals(
                        campaignId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Qualification rejected a stale campaign heartbeat. "
                        + "Expected " + campaignId + " but observed "
                        + observedCampaignId + ".");
                }
                if (ReadBoolean(runtime, "gameOnline")
                    && !string.IsNullOrWhiteSpace(RuntimeInstance(runtime)))
                    return runtime;
                if (DateTimeOffset.UtcNow < deadline)
                    Thread.Sleep(2000);
            }

            throw new InvalidOperationException(
                "The campaign heartbeat remained offline across "
                + attempts
                + " qualification checks before its bounded five-minute deadline. Last runtime evidence: "
                + Json.Serialize(runtime));
        }

        private static Dictionary<string, object> OpenStep(
            string mode,
            IEnumerable<string> targets,
            int minimumParticipants = 1)
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "open",
                ["mode"] = mode,
                ["targetSearches"] = targets.ToArray(),
                ["createTestEvent"] = true,
                ["minimumParticipants"] =
                    Math.Max(1, Math.Min(5, minimumParticipants)),
                ["timeoutSeconds"] = 300
            };
        }

        private static Dictionary<string, object> CloseStep(string mode)
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "close",
                ["mode"] = mode,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["requiresSceneMemoryArtifacts"] = true,
                    ["critical"] = true
                },
                ["timeoutSeconds"] = 900
            };
        }

        private static Dictionary<string, object> SaveStep(int index)
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "save_checkpoint",
                ["mode"] = "individual_chat",
                ["saveName"] = QualificationSaveName(index),
                ["timeoutSeconds"] = 240
            };
        }

        private static Dictionary<string, object> MemoryCompletionStep()
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "wait_for_memory",
                ["mode"] = "individual_chat",
                ["waitMilliseconds"] = 30000,
                ["assertions"] = new Dictionary<string, object>
                {
                    ["requiresQualificationMemoryCompletion"] = true,
                    ["critical"] = true
                },
                ["timeoutSeconds"] = 180
            };
        }

        private static string QualificationSaveName(int index)
        {
            return index % 2 == 0
                ? "Reign_Conversation_Qualification_A"
                : "Reign_Conversation_Qualification_B";
        }

        private static List<List<string>> SelectQualificationRelationshipHistoryPairs(
            string campaignId,
            List<string> partyHeroIds,
            int count)
        {
            List<Dictionary<string, object>> candidates =
                new List<Dictionary<string, object>>();
            List<string> ids = (partyHeroIds ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (int first = 0; first < ids.Count; first++)
            {
                for (int second = first + 1; second < ids.Count; second++)
                {
                    Dictionary<string, object> history = Post(
                        "/relationships/history/query",
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["heroStringId"] = ids[first],
                            ["otherHeroStringId"] = ids[second]
                        });
                    candidates.Add(new Dictionary<string, object>
                    {
                        ["first"] = ids[first],
                        ["second"] = ids[second],
                        ["historyCount"] = ReadLong(history, "count", 0)
                    });
                }
            }
            List<List<string>> selected = candidates
                .OrderBy(row => ReadLong(row, "historyCount", 0))
                .ThenBy(row => String(row, "first"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => String(row, "second"), StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, count))
                .Select(row => new List<string>
                {
                    String(row, "first"), String(row, "second")
                }).ToList();
            if (selected.Count < count)
                throw new InvalidOperationException(
                    "Shared Relationship History qualification requires "
                    + count + " distinct party-eligible pairs; only "
                    + selected.Count + " were available.");
            return selected;
        }
    }
}
