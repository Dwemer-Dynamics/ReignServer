using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ReignLiveTest
{
    internal static partial class Program
    {
        private const int SkillExamReplyTarget = 40;
        private const int SkillExamIndividualTargetCount = 10;
        private const int SkillExamGroupTargetCount = 5;

        private static readonly string[] SkillExamSkillOrder =
        {
            "oneHanded", "twoHanded", "polearm", "bow", "crossbow", "throwing",
            "riding", "athletics", "smithing", "scouting", "tactics", "roguery",
            "charm", "leadership", "trade", "steward", "medicine", "engineering"
        };

        private static readonly Dictionary<string, string> SkillExamSkillNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["oneHanded"] = "One Handed", ["twoHanded"] = "Two Handed",
                ["polearm"] = "Polearm", ["bow"] = "Bow", ["crossbow"] = "Crossbow",
                ["throwing"] = "Throwing", ["riding"] = "Riding",
                ["athletics"] = "Athletics", ["smithing"] = "Smithing",
                ["scouting"] = "Scouting", ["tactics"] = "Tactics",
                ["roguery"] = "Roguery", ["charm"] = "Charm",
                ["leadership"] = "Leadership", ["trade"] = "Trade",
                ["steward"] = "Steward", ["medicine"] = "Medicine",
                ["engineering"] = "Engineering"
            };

        private static readonly Dictionary<string, string[]> SkillExamResponseCues =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["oneHanded"] = new[] { "sword", "mace", "shield", "sidearm", "one hand" },
                ["twoHanded"] = new[] { "greatsword", "great axe", "battle axe", "heavy weapon", "long axe" },
                ["polearm"] = new[] { "spear", "lance", "pike", "polearm", "reach" },
                ["bow"] = new[] { "bow", "archer", "arrow", "shoot", "range" },
                ["crossbow"] = new[] { "crossbow", "crossbowman", "bolt", "reload" },
                ["throwing"] = new[] { "javelin", "throwing axe", "throwing knife", "hurl", "thrown" },
                ["riding"] = new[] { "horse", "mount", "saddle", "rider", "cavalry" },
                ["athletics"] = new[] { "on foot", "climb", "stamina", "endurance", "march" },
                ["smithing"] = new[] { "forge", "smith", "iron", "temper", "metal" },
                ["scouting"] = new[] { "track", "trail", "terrain", "sign", "route", "scout" },
                ["tactics"] = new[] { "formation", "flank", "ambush", "position", "maneuver", "reserve" },
                ["roguery"] = new[] { "smuggler", "underworld", "deception", "illicit", "criminal", "scheme" },
                ["charm"] = new[] { "persuade", "rapport", "courtesy", "tact", "negotiate", "impression" },
                ["leadership"] = new[] { "morale", "inspire", "command", "steady them", "delegate", "discipline" },
                ["trade"] = new[] { "price", "profit", "bargain", "market", "merchant", "value" },
                ["steward"] = new[] { "supplies", "stores", "rations", "provisions", "records", "logistics" },
                ["medicine"] = new[] { "wound", "injury", "physician", "treat", "healer", "bleeding" },
                ["engineering"] = new[] { "support", "load", "structure", "mechanism", "repair", "engineer" }
            };

        private static readonly string[] SkillExamBandLabels =
        {
            "Untrained", "Rudimentary", "Novice", "Practiced", "Competent", "Capable",
            "Seasoned", "Skilled", "Highly Skilled", "Expert", "Master", "Renowned Master",
            "Extraordinary", "Legendary"
        };

        private static Dictionary<string, object> SkillExamControl(string[] args)
        {
            string operation = args.Skip(1)
                .FirstOrDefault(value => !value.StartsWith("-", StringComparison.Ordinal))
                ?? "status";
            switch (operation.Trim().ToLowerInvariant())
            {
                case "plan": return SkillExamPlan();
                case "start": return StartSkillExam(args);
                case "status": return SkillExamStatus(args);
                case "pause": return PauseSkillExam(args);
                case "resume": return ResumeSkillExam(args);
                case "report": return SkillExamReport(args);
                default:
                    throw new InvalidOperationException(
                        "Unknown skill-exam operation '" + operation
                        + "'. Use plan, start, status, pause, resume, or report.");
            }
        }

        private static Dictionary<string, object> SkillExamPlan()
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["schema"] = "reign-skill-awareness-exam-v1",
                ["visibleReplyTarget"] = SkillExamReplyTarget,
                ["individualNpcCount"] = SkillExamIndividualTargetCount,
                ["individualReplies"] = 30,
                ["groupNpcCount"] = SkillExamGroupTargetCount,
                ["groupReplies"] = 10,
                ["sessionCount"] = 11,
                ["expectedMinimumProviderCalls"] = 51,
                ["expectedTypicalProviderCalls"] = "51-55 before exceptional retries",
                ["productionModes"] = new[] { "individual_chat", "social_event" },
                ["skillModel"] = "reign_skill_awareness_v1",
                ["skillKeys"] = SkillExamSkillOrder,
                ["sequence"] = new[]
                {
                    "ordinary natural role-play without a skill invitation",
                    "a practical problem with several plausible approaches",
                    "a return to motives, relationships, or consequences",
                    "two five-NPC social-event turns"
                },
                ["automaticRepairPolicy"] =
                    "The forty-reply run is immutable. Only an objectively reproducible production defect may be repaired after its original evidence is preserved."
            };
        }

        private static Dictionary<string, object> StartSkillExam(string[] args)
        {
            Dictionary<string, object> runtime = Runtime(args);
            string campaignId = ResolveCampaign(args, runtime);
            Dictionary<string, object> existing = LoadLatestSkillExamState(campaignId);
            if (existing.Count > 0 && !SkillExamTerminalStatus(String(existing, "status")))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "A skill-awareness exam is already active for this campaign.",
                    ["examId"] = String(existing, "examId"),
                    ["status"] = String(existing, "status")
                };

            long seed = long.TryParse(Value(args, "--seed", "1042"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedSeed)
                ? parsedSeed : 1042L;
            string examId = "skill-exam-"
                + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-" + SkillExamStableOrder(campaignId + "|" + seed)
                    .ToString("x8", CultureInfo.InvariantCulture).Substring(0, 8);
            string derivativeSave = Value(args, "--save", "ConvTest_SkillExam");

            Post("/tests/live/arm", new Dictionary<string, object>
            {
                ["confirmation"] = "arm", ["minutes"] = 720,
                ["requestedBy"] = "ReignLiveTest skill exam"
            });
            if (!Has(args, "--no-save"))
            {
                List<string> saveArgs = new List<string>
                {
                    "game", "save", "--campaign", campaignId,
                    "--save", derivativeSave, "--wait", "300"
                };
                Dictionary<string, object> saved = SaveGame(saveArgs.ToArray());
                if (!IsOk(saved))
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false, ["status"] = "save_failed",
                        ["error"] = "The derivative skill-exam save could not be created.",
                        ["saveResult"] = saved
                    };
            }

            List<Dictionary<string, object>> candidates = QualificationTargets(
                campaignId, "individual_chat", 500, null, false)
                .Where(target => SkillExamBool(target, "isAlive", true)
                    && SkillExamBool(target, "isAdult", true))
                .ToList();
            if (candidates.Count < SkillExamIndividualTargetCount + SkillExamGroupTargetCount)
                throw new InvalidOperationException(
                    "The loaded campaign does not expose fifteen eligible adult NPCs.");

            candidates = SkillExamDeterministicShortlist(candidates, seed, 72);
            foreach (Dictionary<string, object> target in candidates)
                EnrichSkillExamTarget(campaignId, target);
            Dictionary<string, object> roster = BuildSkillExamRoster(candidates, seed);
            List<Dictionary<string, object>> scenarios = BuildSkillExamScenarios(roster, seed);
            Dictionary<string, object> state = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["schema"] = "reign-skill-awareness-exam-state-v1",
                ["examId"] = examId,
                ["campaignId"] = campaignId,
                ["gameInstanceId"] = RuntimeInstance(runtime),
                ["baselineSaveName"] = "ConvTest",
                ["derivativeSaveName"] = derivativeSave,
                ["seed"] = seed,
                ["status"] = "running",
                ["pauseRequested"] = false,
                ["nextScenarioIndex"] = 0,
                ["completedScenarioCount"] = 0,
                ["completedVisibleReplies"] = 0,
                ["visibleReplyTarget"] = SkillExamReplyTarget,
                ["startedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["roster"] = roster,
                ["scenarios"] = scenarios,
                ["scenarioResults"] = new List<object>(),
                ["failures"] = new List<object>(),
                ["artifactDirectory"] = SkillExamArtifactDirectory(campaignId, examId)
            };
            SaveSkillExamState(state);
            return ExecuteSkillExam(state);
        }

        private static Dictionary<string, object> SkillExamStatus(string[] args)
        {
            Dictionary<string, object> state = ResolveSkillExamState(args);
            if (state.Count == 0)
                return new Dictionary<string, object> { ["ok"] = true, ["found"] = false };
            Dictionary<string, object> response = SkillExamStateSummary(state);
            string activeRunId = String(state, "activeRunId");
            if (!string.IsNullOrWhiteSpace(activeRunId))
            {
                try
                {
                    response["activeRun"] = Get("/tests/live/run/status?campaignId="
                        + Uri.EscapeDataString(String(state, "campaignId"))
                        + "&runId=" + Uri.EscapeDataString(activeRunId));
                }
                catch (Exception ex)
                {
                    response["activeRunStatusError"] = ex.Message;
                }
            }
            return response;
        }

        private static Dictionary<string, object> PauseSkillExam(string[] args)
        {
            Dictionary<string, object> state = ResolveSkillExamState(args);
            if (state.Count == 0)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "No skill exam was found." };
            if (SkillExamTerminalStatus(String(state, "status")))
                return SkillExamStateSummary(state);
            state["pauseRequested"] = true;
            state["status"] = string.IsNullOrWhiteSpace(String(state, "activeRunId"))
                ? "paused" : "pausing_after_current_session";
            state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            SaveSkillExamState(state);
            return SkillExamStateSummary(state);
        }

        private static Dictionary<string, object> ResumeSkillExam(string[] args)
        {
            Dictionary<string, object> state = ResolveSkillExamState(args);
            if (state.Count == 0)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "No skill exam was found." };
            if (String(state, "status").Equals("completed", StringComparison.OrdinalIgnoreCase))
                return SkillExamReportFromState(state);
            state["pauseRequested"] = false;
            state["status"] = "running";
            state["resumedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            SaveSkillExamState(state);
            return ExecuteSkillExam(state);
        }

        private static Dictionary<string, object> SkillExamReport(string[] args)
        {
            Dictionary<string, object> state = ResolveSkillExamState(args);
            if (state.Count == 0)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "No skill exam was found." };
            return SkillExamReportFromState(state);
        }

        private static Dictionary<string, object> ExecuteSkillExam(
            Dictionary<string, object> initialState)
        {
            Dictionary<string, object> state = initialState;
            string campaignId = String(state, "campaignId");
            string examId = String(state, "examId");
            List<Dictionary<string, object>> scenarios = ReadObjects(state, "scenarios");
            int next = (int)ReadLong(state, "nextScenarioIndex", 0);
            for (int index = next; index < scenarios.Count; index++)
            {
                state = LoadSkillExamState(campaignId, examId);
                if (SkillExamBool(state, "pauseRequested", false))
                {
                    state["status"] = "paused";
                    state["activeRunId"] = "";
                    state["pausedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                    state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                    SaveSkillExamState(state);
                    return SkillExamStateSummary(state);
                }

                Dictionary<string, object> scenario = scenarios[index];
                state["status"] = "running";
                state["activeScenarioId"] = String(scenario, "scenarioId");
                state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                SaveSkillExamState(state);

                Dictionary<string, object> result;
                try
                {
                    result = RunSkillExamScenario(state, scenario, index);
                }
                catch (Exception ex)
                {
                    result = new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["status"] = "failed",
                        ["scenarioId"] = String(scenario, "scenarioId"),
                        ["error"] = ex.Message
                    };
                }

                state = LoadSkillExamState(campaignId, examId);
                List<Dictionary<string, object>> results = ReadObjects(state, "scenarioResults");
                results.RemoveAll(row => String(row, "scenarioId")
                    .Equals(String(scenario, "scenarioId"), StringComparison.OrdinalIgnoreCase));
                results.Add(result);
                state["scenarioResults"] = results;
                if (!IsOk(result))
                {
                    List<Dictionary<string, object>> failures = ReadObjects(state, "failures");
                    failures.Add(new Dictionary<string, object>
                    {
                        ["scenarioId"] = String(scenario, "scenarioId"),
                        ["error"] = String(result, "error"),
                        ["status"] = String(result, "status"),
                        ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                    });
                    state["failures"] = failures;
                }
                state["nextScenarioIndex"] = index + 1;
                state["completedScenarioCount"] = index + 1;
                state["completedVisibleReplies"] = results.Sum(row =>
                    (int)ReadLong(row, "visibleReplyCount", 0));
                state["activeRunId"] = "";
                state["activeScenarioId"] = "";
                state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
                SaveSkillExamState(state);
            }

            state = LoadSkillExamState(campaignId, examId);
            Dictionary<string, object> report = BuildSkillExamAggregateReport(state);
            state["status"] = "completed";
            state["completedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            state["reportPath"] = String(report, "reportPath");
            state["completedVisibleReplies"] = ReadLong(report, "visibleReplyCount", 0);
            state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            SaveSkillExamState(state);
            return report;
        }

        private static Dictionary<string, object> RunSkillExamScenario(
            Dictionary<string, object> state,
            Dictionary<string, object> scenario,
            int scenarioIndex)
        {
            string campaignId = String(state, "campaignId");
            string examId = String(state, "examId");
            string scenarioId = String(scenario, "scenarioId");
            string mode = String(scenario, "mode");
            string runId = examId + "-" + scenarioId.ToLowerInvariant();
            Post("/tests/live/arm", new Dictionary<string, object>
            {
                ["confirmation"] = "arm", ["minutes"] = 720,
                ["requestedBy"] = "ReignLiveTest skill exam renewal"
            });
            Dictionary<string, object> runtime = QualificationRuntimeWithHeartbeatGrace(campaignId);
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["runId"] = runId,
                ["campaignId"] = campaignId,
                ["gameInstanceId"] = RuntimeInstance(runtime),
                ["mode"] = mode,
                ["presentation"] = "headless",
                ["effects"] = "guarded",
                ["label"] = "Skill awareness exam " + scenarioId,
                ["qualificationId"] = examId,
                ["seed"] = ReadLong(state, "seed", 1042) + scenarioIndex,
                ["autoCompleteWhenIdle"] = true,
                ["steps"] = BuildSkillExamScenarioSteps(scenario)
            };
            Dictionary<string, object> started =
                StartQualificationScenarioRunWithReconciliation(campaignId, payload);
            if (!IsOk(started))
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["status"] = "start_failed",
                    ["scenarioId"] = scenarioId,
                    ["error"] = "Skill-exam scenario could not start: " + Json.Serialize(started)
                };

            state = LoadSkillExamState(campaignId, examId);
            state["activeRunId"] = runId;
            state["activeScenarioId"] = scenarioId;
            state["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            SaveSkillExamState(state);

            Dictionary<string, object> completed = WaitForRun(
                campaignId, runId, "", mode == "social_event" ? 3600 : 1800, true);
            Dictionary<string, object> report;
            try
            {
                report = Get("/tests/live/run/report?campaignId="
                    + Uri.EscapeDataString(campaignId)
                    + "&runId=" + Uri.EscapeDataString(runId));
            }
            catch
            {
                report = completed;
            }
            string artifactDirectory = String(state, "artifactDirectory");
            Directory.CreateDirectory(Path.Combine(artifactDirectory, "scenarios"));
            string reportPath = Path.Combine(artifactDirectory, "scenarios", scenarioId + ".json");
            File.WriteAllText(reportPath, Json.Serialize(report), Encoding.UTF8);
            int replies = CountSkillExamVisibleReplies(report);
            return new Dictionary<string, object>
            {
                ["ok"] = IsOk(completed)
                    && String(completed, "status").Equals("completed", StringComparison.OrdinalIgnoreCase),
                ["status"] = String(completed, "status"),
                ["scenarioId"] = scenarioId,
                ["mode"] = mode,
                ["runId"] = runId,
                ["visibleReplyCount"] = replies,
                ["expectedReplyCount"] = ReadLong(scenario, "expectedReplyCount", 0),
                ["reportPath"] = reportPath,
                ["error"] = String(completed, "error")
            };
        }

        private static List<Dictionary<string, object>> BuildSkillExamScenarioSteps(
            Dictionary<string, object> scenario)
        {
            string mode = String(scenario, "mode");
            List<string> targetIds = SkillExamStrings(scenario, "targetHeroIds");
            List<Dictionary<string, object>> steps = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["operation"] = "open",
                    ["mode"] = mode,
                    ["targetSearches"] = targetIds.ToArray(),
                    ["createTestEvent"] = mode == "social_event",
                    ["presentation"] = "headless",
                    ["effects"] = "guarded",
                    ["timeoutSeconds"] = 180
                }
            };
            int turnIndex = 0;
            foreach (Dictionary<string, object> turn in ReadObjects(scenario, "turns"))
            {
                Dictionary<string, object> assertions = new Dictionary<string, object>
                {
                    ["minReplies"] = mode == "social_event" ? targetIds.Count : 1,
                    ["requiresCorrelationIds"] = true,
                    ["requiresStructuralEvidence"] = true,
                    ["requiresPromptEvidence"] = true,
                    ["requiresGroundedGuardedActionRouting"] = true,
                    ["maxPromptChars"] = 100000,
                    ["critical"] = true,
                    ["skillExam"] = new Dictionary<string, object>
                    {
                        ["scenarioId"] = String(scenario, "scenarioId"),
                        ["pairId"] = String(scenario, "pairId"),
                        ["phase"] = String(turn, "phase"),
                        ["skillRelevant"] = SkillExamBool(turn, "skillRelevant", false),
                        ["expectedContextSkills"] = SkillExamStrings(turn, "expectedContextSkills").ToArray(),
                        ["coverageSkills"] = SkillExamStrings(turn, "coverageSkills").ToArray()
                    }
                };
                steps.Add(new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["operation"] = "send",
                    ["mode"] = mode,
                    ["text"] = String(turn, "text"),
                    ["sceneIndex"] = 0,
                    ["turnIndex"] = turnIndex++,
                    ["readinessCategories"] = new[] { "skill_awareness" },
                    ["assertions"] = assertions,
                    ["timeoutSeconds"] = mode == "social_event" ? 1800 : 900
                });
            }
            steps.Add(new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "close",
                ["mode"] = mode,
                ["timeoutSeconds"] = 900
            });
            return steps;
        }

        private static Dictionary<string, object> BuildSkillExamRoster(
            List<Dictionary<string, object>> candidates,
            long seed)
        {
            List<Dictionary<string, object>> remaining = candidates.ToList();
            List<Dictionary<string, object>> pairRows = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> individual = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> definitions = SkillExamScenarioDefinitions();
            for (int index = 0; index < definitions.Count; index++)
            {
                Dictionary<string, object> definition = definitions[index];
                Tuple<Dictionary<string, object>, Dictionary<string, object>> pair =
                    SelectSkillExamPair(remaining, definition, seed + index);
                if (pair == null)
                    throw new InvalidOperationException(
                        "Could not construct five distinct matched skill-exam pairs.");
                remaining.Remove(pair.Item1);
                remaining.Remove(pair.Item2);
                individual.Add(pair.Item1);
                individual.Add(pair.Item2);
                pairRows.Add(new Dictionary<string, object>
                {
                    ["pairId"] = "PAIR-" + (index + 1).ToString("00", CultureInfo.InvariantCulture),
                    ["scenarioKey"] = String(definition, "key"),
                    ["relevantSkills"] = SkillExamStrings(definition, "relevantSkills").ToArray(),
                    ["heroIds"] = new[] { String(pair.Item1, "heroId"), String(pair.Item2, "heroId") },
                    ["similarityEvidence"] = SkillExamPairEvidence(pair.Item1, pair.Item2),
                    ["topSkillContrast"] = SkillExamTopSkillContrast(pair.Item1, pair.Item2)
                });
            }

            HashSet<string> coveredSkills = new HashSet<string>(
                individual.SelectMany(SkillExamTopSkillKeys),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> coveredCultures = new HashSet<string>(
                individual.Select(row => String(row, "cultureId")),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> coveredRoles = new HashSet<string>(
                individual.Select(SkillExamRole), StringComparer.OrdinalIgnoreCase);
            HashSet<string> coveredCourtCells = new HashSet<string>(
                individual.Select(row => String(row, "courtCharacterCell")),
                StringComparer.OrdinalIgnoreCase);
            bool hasFemale = individual.Any(row => SkillExamBool(row, "isFemale", false));
            bool hasMale = individual.Any(row => !SkillExamBool(row, "isFemale", false));
            List<Dictionary<string, object>> group = new List<Dictionary<string, object>>();
            for (int count = 0; count < SkillExamGroupTargetCount; count++)
            {
                Dictionary<string, object> best = remaining
                    .OrderByDescending(candidate => SkillExamGroupCandidateScore(
                        candidate, coveredSkills, coveredCultures, coveredRoles,
                        coveredCourtCells, hasFemale, hasMale))
                    .ThenBy(candidate => SkillExamStableOrder(
                        seed + "|group|" + String(candidate, "heroId")))
                    .FirstOrDefault();
                if (best == null)
                    throw new InvalidOperationException("Could not select five distinct social-event participants.");
                remaining.Remove(best);
                group.Add(best);
                foreach (string skill in SkillExamTopSkillKeys(best)) coveredSkills.Add(skill);
                coveredCultures.Add(String(best, "cultureId"));
                coveredRoles.Add(SkillExamRole(best));
                coveredCourtCells.Add(String(best, "courtCharacterCell"));
                if (SkillExamBool(best, "isFemale", false)) hasFemale = true; else hasMale = true;
            }

            List<Dictionary<string, object>> all = individual.Concat(group).ToList();
            HashSet<string> topThreeCoverage = new HashSet<string>(
                all.SelectMany(SkillExamTopSkillKeys), StringComparer.OrdinalIgnoreCase);
            List<string> contextCoverage = SkillExamSkillOrder
                .Where(skill => !topThreeCoverage.Contains(skill)).ToList();
            List<string> cultures = all.Select(row => String(row, "cultureId"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> warnings = new List<string>();
            if (cultures.Count < 5)
                warnings.Add("The available matched roster represented only " + cultures.Count + " cultures.");
            if (!hasFemale || !hasMale)
                warnings.Add("The available roster did not include both sexes.");
            if (contextCoverage.Count > 6)
                warnings.Add("More than six skills required contextual rather than top-three coverage.");
            return new Dictionary<string, object>
            {
                ["individualTargets"] = individual,
                ["groupTargets"] = group,
                ["pairs"] = pairRows,
                ["allTargetIds"] = all.Select(row => String(row, "heroId")).ToArray(),
                ["topThreeSkillCoverage"] = topThreeCoverage.OrderBy(value =>
                    Array.IndexOf(SkillExamSkillOrder, value)).ToArray(),
                ["contextCoverageSkills"] = contextCoverage.ToArray(),
                ["cultures"] = cultures.ToArray(),
                ["roles"] = all.Select(SkillExamRole).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ["courtCharacterCells"] = all.Select(row => String(row, "courtCharacterCell"))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ["femaleCount"] = all.Count(row => SkillExamBool(row, "isFemale", false)),
                ["maleCount"] = all.Count(row => !SkillExamBool(row, "isFemale", false)),
                ["selectionWarnings"] = warnings.ToArray()
            };
        }

        private static List<Dictionary<string, object>> SkillExamScenarioDefinitions()
        {
            return new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["key"] = "travel",
                    ["label"] = "The Delayed Family",
                    ["relevantSkills"] = new[] { "scouting", "riding", "athletics", "medicine" },
                    ["ordinary"] = "The streets have finally quieted a little. What sort of company do you find easiest to endure when an evening stretches on?",
                    ["relevant"] = "A family at the market hopes to leave before dusk, but one of them is limping and the road beyond the eastern gate has seen little traffic today. They have asked for honest advice, not heroics. What would you tell them?",
                    ["return"] = "Suppose they ignored your advice, came to grief, and then blamed you for not stopping them. Would that anger you, trouble you, or simply confirm what you already thought of them?"
                },
                new Dictionary<string, object>
                {
                    ["key"] = "supply",
                    ["label"] = "The Missing Stores",
                    ["relevantSkills"] = new[] { "trade", "steward", "roguery", "charm", "leadership" },
                    ["ordinary"] = "People reveal themselves in small choices. What is one courtesy you notice when it is offered, even if you rarely mention it?",
                    ["relevant"] = "Two grain wagons are late, the storehouse tally does not match what the merchants swear they delivered, and the servants are beginning to accuse one another. There is no proof of theft yet. Where would you begin?",
                    ["return"] = "If the person responsible confessed privately and begged you to spare their family the shame, what would matter most in deciding how to answer?"
                },
                new Dictionary<string, object>
                {
                    ["key"] = "craft",
                    ["label"] = "The Groaning Hoist",
                    ["relevantSkills"] = new[] { "engineering", "smithing", "athletics" },
                    ["ordinary"] = "There are corners of every town that feel more honest than the grand rooms. Where would you go here if you wanted a quiet hour away from ceremony?",
                    ["relevant"] = "The miller's lifting frame has begun to groan under load, and the iron pin at its joint grows hot after only a few turns. He wants to keep working until nightfall because several families are waiting. What would you have him do?",
                    ["return"] = "If the miller refused because pride would not let him admit the fault, would you press the matter, leave him to the consequence, or find some less direct way to protect the families waiting?"
                },
                new Dictionary<string, object>
                {
                    ["key"] = "negotiation",
                    ["label"] = "The Two Envoys",
                    ["relevantSkills"] = new[] { "charm", "leadership", "tactics", "roguery", "trade" },
                    ["ordinary"] = "At gatherings like this, do you prefer someone who speaks plainly at once, or someone who takes time to learn the room before saying what they want?",
                    ["relevant"] = "Two clan envoys have each asked for a private assurance that their house will be favored in the same dispute. Refusing both may harden them, but promising either too much could make the quarrel worse. How would you handle the next meeting?",
                    ["return"] = "If peace were possible only by letting one proud person believe the settlement was their own victory, would you grant them that comfort or insist the truth be spoken plainly?"
                },
                new Dictionary<string, object>
                {
                    ["key"] = "security",
                    ["label"] = "The Bent Road",
                    ["relevantSkills"] = new[]
                    {
                        "oneHanded", "twoHanded", "polearm", "bow", "crossbow",
                        "throwing", "riding", "scouting", "tactics", "leadership"
                    },
                    ["ordinary"] = "When people first meet you, what do they most often misunderstand about the way you carry yourself?",
                    ["relevant"] = "A small escort must pass a road that bends beneath a wooded ridge. Fresh marks were found near the ditch, but no enemy has been seen, and turning back may leave villagers without help. How would you approach it?",
                    ["return"] = "When others depend on your judgment in danger, do you feel more bound by their trust, by the result, or by your own sense of what courage requires?"
                }
            };
        }

        private static Tuple<Dictionary<string, object>, Dictionary<string, object>>
            SelectSkillExamPair(
                List<Dictionary<string, object>> candidates,
                Dictionary<string, object> definition,
                long seed)
        {
            List<Dictionary<string, object>> pool = candidates.Take(72).ToList();
            Tuple<Dictionary<string, object>, Dictionary<string, object>> best = null;
            double bestScore = double.MinValue;
            for (int left = 0; left < pool.Count; left++)
            {
                for (int right = left + 1; right < pool.Count; right++)
                {
                    Dictionary<string, object> a = pool[left], b = pool[right];
                    double score = SkillExamPairScore(a, b, definition);
                    score += (SkillExamStableOrder(seed + "|" + String(a, "heroId")
                        + "|" + String(b, "heroId")) % 1000) / 1000000d;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    best = Tuple.Create(a, b);
                }
            }
            return best;
        }

        private static double SkillExamPairScore(
            Dictionary<string, object> a,
            Dictionary<string, object> b,
            Dictionary<string, object> definition)
        {
            double score = 0d;
            if (String(a, "cultureId").Equals(String(b, "cultureId"), StringComparison.OrdinalIgnoreCase)) score += 24d;
            if (SkillExamRole(a).Equals(SkillExamRole(b), StringComparison.OrdinalIgnoreCase)) score += 18d;
            if (SkillExamBool(a, "isLord", false) == SkillExamBool(b, "isLord", false)) score += 8d;
            if (SkillExamBool(a, "isFemale", false) == SkillExamBool(b, "isFemale", false)) score += 2d;
            int tierDifference = Math.Abs((int)ReadLong(a, "clanTier", 0) - (int)ReadLong(b, "clanTier", 0));
            score += Math.Max(0d, 6d - tierDifference * 2d);
            string cellA = String(a, "courtCharacterCell"), cellB = String(b, "courtCharacterCell");
            if (!string.IsNullOrWhiteSpace(cellA) && cellA.Equals(cellB, StringComparison.OrdinalIgnoreCase)) score += 12d;
            else
            {
                if (ReadLong(a, "courtHonorLevel", 99) == ReadLong(b, "courtHonorLevel", -99)) score += 5d;
                if (ReadLong(a, "courtBoldnessLevel", 99) == ReadLong(b, "courtBoldnessLevel", -99)) score += 5d;
            }
            int traitDistance = new[] { "honor", "calculating", "valor", "mercy", "generosity" }
                .Sum(key => Math.Abs((int)ReadLong(a, key, 0) - (int)ReadLong(b, key, 0)));
            score += Math.Max(0d, 8d - traitDistance);

            HashSet<string> aTop = new HashSet<string>(SkillExamTopSkillKeys(a), StringComparer.OrdinalIgnoreCase);
            HashSet<string> bTop = new HashSet<string>(SkillExamTopSkillKeys(b), StringComparer.OrdinalIgnoreCase);
            HashSet<string> relevant = new HashSet<string>(
                SkillExamStrings(definition, "relevantSkills"), StringComparer.OrdinalIgnoreCase);
            int relevantTop = aTop.Count(relevant.Contains) + bTop.Count(relevant.Contains);
            int distinctTop = aTop.Union(bTop, StringComparer.OrdinalIgnoreCase).Count();
            int sharedTop = aTop.Intersect(bTop, StringComparer.OrdinalIgnoreCase).Count();
            score += relevantTop * 10d + distinctTop * 3d - sharedTop * 7d;
            foreach (string skill in relevant)
                score += Math.Min(8d, Math.Abs(SkillExamSkillValue(a, skill)
                    - SkillExamSkillValue(b, skill)) / 30d);
            return score;
        }

        private static Dictionary<string, object> SkillExamPairEvidence(
            Dictionary<string, object> a,
            Dictionary<string, object> b)
        {
            return new Dictionary<string, object>
            {
                ["sameCulture"] = String(a, "cultureId").Equals(String(b, "cultureId"), StringComparison.OrdinalIgnoreCase),
                ["sameRole"] = SkillExamRole(a).Equals(SkillExamRole(b), StringComparison.OrdinalIgnoreCase),
                ["sameCourtCharacterCell"] = !string.IsNullOrWhiteSpace(String(a, "courtCharacterCell"))
                    && String(a, "courtCharacterCell").Equals(String(b, "courtCharacterCell"), StringComparison.OrdinalIgnoreCase),
                ["clanTierDifference"] = Math.Abs((int)ReadLong(a, "clanTier", 0) - (int)ReadLong(b, "clanTier", 0)),
                ["nativeTraitDistance"] = new[] { "honor", "calculating", "valor", "mercy", "generosity" }
                    .Sum(key => Math.Abs((int)ReadLong(a, key, 0) - (int)ReadLong(b, key, 0)))
            };
        }

        private static List<Dictionary<string, object>> SkillExamTopSkillContrast(
            Dictionary<string, object> a,
            Dictionary<string, object> b)
        {
            HashSet<string> keys = new HashSet<string>(SkillExamTopSkillKeys(a), StringComparer.OrdinalIgnoreCase);
            keys.UnionWith(SkillExamTopSkillKeys(b));
            return keys.Select(key => new Dictionary<string, object>
            {
                ["skill"] = key,
                ["firstValue"] = SkillExamSkillValue(a, key),
                ["secondValue"] = SkillExamSkillValue(b, key),
                ["difference"] = Math.Abs(SkillExamSkillValue(a, key) - SkillExamSkillValue(b, key))
            }).OrderByDescending(row => ReadLong(row, "difference", 0)).ToList();
        }

        private static double SkillExamGroupCandidateScore(
            Dictionary<string, object> candidate,
            HashSet<string> coveredSkills,
            HashSet<string> coveredCultures,
            HashSet<string> coveredRoles,
            HashSet<string> coveredCourtCells,
            bool hasFemale,
            bool hasMale)
        {
            double score = SkillExamTopSkillKeys(candidate).Count(skill => !coveredSkills.Contains(skill)) * 18d;
            if (!coveredCultures.Contains(String(candidate, "cultureId"))) score += 28d;
            if (!coveredRoles.Contains(SkillExamRole(candidate))) score += 14d;
            string cell = String(candidate, "courtCharacterCell");
            if (!string.IsNullOrWhiteSpace(cell) && !coveredCourtCells.Contains(cell)) score += 8d;
            if (SkillExamBool(candidate, "isFemale", false) && !hasFemale) score += 35d;
            if (!SkillExamBool(candidate, "isFemale", false) && !hasMale) score += 35d;
            score += SkillExamTopSkills(candidate).Sum(row => Math.Min(5d, ReadLong(row, "value", 0) / 60d));
            return score;
        }

        private static List<Dictionary<string, object>> BuildSkillExamScenarios(
            Dictionary<string, object> roster,
            long seed)
        {
            List<Dictionary<string, object>> definitions = SkillExamScenarioDefinitions();
            List<Dictionary<string, object>> pairs = ReadObjects(roster, "pairs");
            Dictionary<string, Dictionary<string, object>> targets =
                ReadObjects(roster, "individualTargets")
                    .Concat(ReadObjects(roster, "groupTargets"))
                    .ToDictionary(row => String(row, "heroId"), row => row,
                        StringComparer.OrdinalIgnoreCase);
            List<string> contextCoverage = SkillExamStrings(roster, "contextCoverageSkills");
            Dictionary<string, List<string>> coverageByScenario = definitions
                .ToDictionary(row => String(row, "key"), row => new List<string>(),
                    StringComparer.OrdinalIgnoreCase);
            foreach (string skill in contextCoverage)
            {
                Dictionary<string, object> matching = definitions.FirstOrDefault(row =>
                    SkillExamStrings(row, "relevantSkills").Contains(skill, StringComparer.OrdinalIgnoreCase))
                    ?? definitions.Last();
                coverageByScenario[String(matching, "key")].Add(skill);
            }

            List<Dictionary<string, object>> scenarios = new List<Dictionary<string, object>>();
            int ordinal = 0;
            foreach (Dictionary<string, object> pair in pairs)
            {
                Dictionary<string, object> definition = definitions.First(row =>
                    String(row, "key").Equals(String(pair, "scenarioKey"), StringComparison.OrdinalIgnoreCase));
                List<string> coverage = coverageByScenario[String(definition, "key")];
                string relevantText = String(definition, "relevant")
                    + SkillExamCoverageCueText(coverage);
                foreach (string heroId in SkillExamStrings(pair, "heroIds"))
                {
                    ordinal++;
                    scenarios.Add(new Dictionary<string, object>
                    {
                        ["scenarioId"] = "IND-" + ordinal.ToString("00", CultureInfo.InvariantCulture),
                        ["pairId"] = String(pair, "pairId"),
                        ["scenarioKey"] = String(definition, "key"),
                        ["label"] = String(definition, "label"),
                        ["mode"] = "individual_chat",
                        ["targetHeroIds"] = new[] { heroId },
                        ["targetSnapshots"] = new[] { targets[heroId] },
                        ["expectedReplyCount"] = 3,
                        ["turns"] = new object[]
                        {
                            SkillExamTurn("ordinary", String(definition, "ordinary"), false,
                                new string[0], new string[0]),
                            SkillExamTurn("relevant", relevantText, true,
                                SkillExamStrings(definition, "relevantSkills"), coverage),
                            SkillExamTurn("return_to_character", String(definition, "return"), false,
                                new string[0], new string[0])
                        }
                    });
                }
            }

            List<Dictionary<string, object>> groupTargets = ReadObjects(roster, "groupTargets");
            scenarios.Add(new Dictionary<string, object>
            {
                ["scenarioId"] = "GRP-01",
                ["pairId"] = "",
                ["scenarioKey"] = "social_event",
                ["label"] = "The Delayed Caravan",
                ["mode"] = "social_event",
                ["targetHeroIds"] = groupTargets.Select(row => String(row, "heroId")).ToArray(),
                ["targetSnapshots"] = groupTargets,
                ["expectedReplyCount"] = 10,
                ["turns"] = new object[]
                {
                    SkillExamTurn("ordinary",
                        "The evening has been noisy enough. Before the next song begins, what has held each of your attention here tonight?",
                        false, new string[0], new string[0]),
                    SkillExamTurn("relevant",
                        "Word has reached the hall that a caravan due before nightfall has not arrived. One wagon was seen damaged near a fork in the road, an injured driver may still be with it, and tempers are rising among the merchants waiting here. We have only a little daylight. What do each of you think should be done?",
                        true,
                        new[] { "scouting", "riding", "medicine", "engineering", "smithing", "trade", "steward", "charm", "leadership", "tactics" },
                        new string[0])
                }
            });
            return scenarios;
        }

        private static Dictionary<string, object> SkillExamTurn(
            string phase,
            string text,
            bool skillRelevant,
            IEnumerable<string> expectedSkills,
            IEnumerable<string> coverageSkills)
        {
            return new Dictionary<string, object>
            {
                ["phase"] = phase,
                ["text"] = text,
                ["skillRelevant"] = skillRelevant,
                ["expectedContextSkills"] = (expectedSkills ?? Enumerable.Empty<string>()).ToArray(),
                ["coverageSkills"] = (coverageSkills ?? Enumerable.Empty<string>()).ToArray()
            };
        }

        private static string SkillExamCoverageCueText(IEnumerable<string> skills)
        {
            List<string> cues = (skills ?? Enumerable.Empty<string>())
                .Select(SkillExamNaturalCoverageCue)
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            return cues.Count == 0 ? "" : " One further detail may matter: "
                + string.Join("; ", cues) + ".";
        }

        private static string SkillExamNaturalCoverageCue(string skill)
        {
            switch (skill)
            {
                case "oneHanded": return "one guard carries only a sword and shield";
                case "twoHanded": return "another has a heavy two-handed axe";
                case "polearm": return "two spearmen can be spared from the gate";
                case "bow": return "archers have a clear view of the ridge";
                case "crossbow": return "crossbowmen could cover the carts but would reload slowly";
                case "throwing": return "several abandoned javelins were found beside the road";
                case "riding": return "the available horses are already tired";
                case "athletics": return "the shortest approach includes a steep climb on foot";
                case "smithing": return "a warped iron fitting may be part of the failure";
                case "scouting": return "the few tracks point in conflicting directions";
                case "tactics": return "the ground could conceal an ambush";
                case "roguery": return "a smuggler has offered an unverified shortcut";
                case "charm": return "the people waiting will need persuading before they panic";
                case "leadership": return "the frightened guards are beginning to lose heart";
                case "trade": return "prices are already climbing on the rumor";
                case "steward": return "the remaining stores and rations are uncertain";
                case "medicine": return "one traveler may be badly wounded";
                case "engineering": return "a cracked timber support may have trapped the wagon";
                default: return "";
            }
        }

        private static Dictionary<string, object> BuildSkillExamAggregateReport(
            Dictionary<string, object> state)
        {
            string campaignId = String(state, "campaignId");
            string examId = String(state, "examId");
            Dictionary<string, object> roster = ReadObject(state, "roster");
            Dictionary<string, Dictionary<string, object>> targetSnapshots =
                ReadObjects(roster, "individualTargets")
                    .Concat(ReadObjects(roster, "groupTargets"))
                    .ToDictionary(row => String(row, "heroId"), row => row,
                        StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> scenarioPlans =
                ReadObjects(state, "scenarios")
                    .ToDictionary(row => String(row, "scenarioId"), row => row,
                        StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> scenarioSummaries = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> replyReviews = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> hardFailures = new List<Dictionary<string, object>>();
            int providerRetryCount = 0;
            long providerCallCount = 0;
            long promptBuildMs = 0;
            int promptBuildSamples = 0;

            foreach (Dictionary<string, object> scenarioResult in ReadObjects(state, "scenarioResults"))
            {
                string scenarioId = String(scenarioResult, "scenarioId");
                string scenarioReportPath = String(scenarioResult, "reportPath");
                Dictionary<string, object> runReport = File.Exists(scenarioReportPath)
                    ? Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(scenarioReportPath, Encoding.UTF8))
                        ?? new Dictionary<string, object>()
                    : new Dictionary<string, object>();
                Dictionary<string, object> plan = scenarioPlans.TryGetValue(
                    scenarioId, out Dictionary<string, object> foundPlan)
                    ? foundPlan : new Dictionary<string, object>();
                List<Dictionary<string, object>> audit = ReadObjects(runReport, "auditEvidence");
                List<Dictionary<string, object>> sends = ReadObjects(runReport, "commands")
                    .Where(command => String(command, "operation")
                        .Equals("send", StringComparison.OrdinalIgnoreCase)).ToList();
                int scenarioReplyCount = 0;
                for (int sendIndex = 0; sendIndex < sends.Count; sendIndex++)
                {
                    Dictionary<string, object> send = sends[sendIndex];
                    Dictionary<string, object> turnPlan = ReadObjects(plan, "turns")
                        .Skip(sendIndex).FirstOrDefault()
                        ?? new Dictionary<string, object>();
                    foreach (Dictionary<string, object> reply in SkillExamReplies(send))
                    {
                        scenarioReplyCount++;
                        Dictionary<string, object> review = ReviewSkillExamReply(
                            scenarioId, plan, turnPlan, send, reply,
                            targetSnapshots, audit);
                        replyReviews.Add(review);
                        foreach (Dictionary<string, object> failure in ReadObjects(review, "hardFailures"))
                            hardFailures.Add(failure);
                    }
                }
                foreach (Dictionary<string, object> row in audit)
                {
                    string phase = String(row, "phase");
                    Dictionary<string, object> data = ReadObject(row, "data");
                    if (phase.StartsWith("llm.", StringComparison.OrdinalIgnoreCase))
                    {
                        providerCallCount++;
                        if (ReadLong(data, "retryNumber", 0) > 0
                            || phase.IndexOf("retry", StringComparison.OrdinalIgnoreCase) >= 0)
                            providerRetryCount++;
                    }
                    if (phase.Equals("prompt.built", StringComparison.OrdinalIgnoreCase))
                    {
                        promptBuildMs += ReadLong(row, "durationMs", 0);
                        promptBuildSamples++;
                    }
                }
                scenarioSummaries.Add(new Dictionary<string, object>
                {
                    ["scenarioId"] = scenarioId,
                    ["runId"] = String(scenarioResult, "runId"),
                    ["mode"] = String(scenarioResult, "mode"),
                    ["status"] = String(scenarioResult, "status"),
                    ["expectedReplyCount"] = ReadLong(plan, "expectedReplyCount", 0),
                    ["visibleReplyCount"] = scenarioReplyCount,
                    ["reportPath"] = scenarioReportPath
                });
            }

            int visibleReplies = replyReviews.Count;
            int expectedTopThreePasses = replyReviews.Count(row => SkillExamBool(row, "topThreeCorrect", false));
            int promptInjectionPasses = replyReviews.Count(row => SkillExamBool(row, "skillPromptPresent", false));
            int numericLeakCount = replyReviews.Count(row => SkillExamBool(row, "numericSkillLeak", false));
            int unsupportedActionCount = replyReviews.Sum(row => (int)ReadLong(row, "queuedActionCount", 0));
            int wrongOwnerCount = replyReviews.Count(row => !SkillExamBool(row, "ownerCorrect", false));
            int skillRelevantCount = replyReviews.Count(row => SkillExamBool(row, "skillRelevant", false));
            int implicitCueCount = replyReviews.Count(row => SkillExamBool(row, "topSkillCueObserved", false));
            int irrelevantReplyCount = replyReviews.Count(row => !SkillExamBool(row, "skillRelevant", false));
            int restraintPasses = replyReviews.Count(row => !SkillExamBool(row, "skillRelevant", false)
                && !SkillExamBool(row, "gratuitousSkillIdentity", false));
            int dominanceCount = replyReviews.Count(row => SkillExamBool(row, "skillDominanceRisk", false));
            HashSet<string> observedCoverage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> review in replyReviews)
            {
                observedCoverage.UnionWith(SkillExamStrings(review, "observedTopSkills"));
                observedCoverage.UnionWith(SkillExamStrings(review, "observedOnDemandSkills"));
            }
            List<string> missingCoverage = SkillExamSkillOrder
                .Where(skill => !observedCoverage.Contains(skill)).ToList();
            List<Dictionary<string, object>> pairReviews = BuildSkillExamPairReviews(roster, replyReviews);
            Dictionary<string, object> groupReview = BuildSkillExamGroupReview(replyReviews);

            Func<int, int, double> rate = (passed, total) => total <= 0
                ? 0d : Math.Round(passed / (double)total, 4);
            Dictionary<string, object> gates = new Dictionary<string, object>
            {
                ["exactlyFortyVisibleReplies"] = visibleReplies == SkillExamReplyTarget,
                ["topThreeSelection"] = new Dictionary<string, object>
                {
                    ["passed"] = visibleReplies > 0 && expectedTopThreePasses == visibleReplies,
                    ["rate"] = rate(expectedTopThreePasses, visibleReplies),
                    ["required"] = 1d
                },
                ["promptInjection"] = new Dictionary<string, object>
                {
                    ["passed"] = visibleReplies > 0 && promptInjectionPasses == visibleReplies,
                    ["rate"] = rate(promptInjectionPasses, visibleReplies),
                    ["required"] = 1d
                },
                ["numericOrMechanicsExposure"] = new Dictionary<string, object>
                {
                    ["passed"] = numericLeakCount == 0,
                    ["failureCount"] = numericLeakCount,
                    ["requiredMaximum"] = 0
                },
                ["unsupportedActions"] = new Dictionary<string, object>
                {
                    ["passed"] = unsupportedActionCount == 0,
                    ["failureCount"] = unsupportedActionCount,
                    ["requiredMaximum"] = 0
                },
                ["ownership"] = new Dictionary<string, object>
                {
                    ["passed"] = wrongOwnerCount == 0,
                    ["failureCount"] = wrongOwnerCount,
                    ["requiredMaximum"] = 0
                },
                ["allSkillCoverage"] = new Dictionary<string, object>
                {
                    ["passed"] = missingCoverage.Count == 0,
                    ["observedSkills"] = observedCoverage.OrderBy(value =>
                        Array.IndexOf(SkillExamSkillOrder, value)).ToArray(),
                    ["missingSkills"] = missingCoverage.ToArray()
                },
                ["automaticInfluenceIndicator"] = new Dictionary<string, object>
                {
                    ["passed"] = skillRelevantCount > 0
                        && rate(implicitCueCount, skillRelevantCount) >= 0.80d,
                    ["rate"] = rate(implicitCueCount, skillRelevantCount),
                    ["required"] = 0.80d,
                    ["advisory"] = true
                },
                ["automaticRestraintIndicator"] = new Dictionary<string, object>
                {
                    ["passed"] = irrelevantReplyCount > 0
                        && rate(restraintPasses, irrelevantReplyCount) >= 0.95d,
                    ["rate"] = rate(restraintPasses, irrelevantReplyCount),
                    ["required"] = 0.95d,
                    ["advisory"] = true
                },
                ["skillDominanceRisk"] = new Dictionary<string, object>
                {
                    ["passed"] = dominanceCount == 0,
                    ["flaggedReplyCount"] = dominanceCount,
                    ["advisory"] = true
                },
                ["matchedPairContrast"] = new Dictionary<string, object>
                {
                    ["automaticDistinctPairCount"] = pairReviews.Count(row =>
                        SkillExamBool(row, "automaticBehavioralContrast", false)),
                    ["requiredPairCount"] = 4,
                    ["manualReviewRequired"] = true
                },
                ["groupContribution"] = new Dictionary<string, object>
                {
                    ["automaticDistinctContributorCount"] = ReadLong(groupReview, "distinctContributorCount", 0),
                    ["requiredContributorCount"] = 4,
                    ["manualReviewRequired"] = true
                },
                ["capabilityCalibration"] = new Dictionary<string, object>
                {
                    ["required"] = 0.90d,
                    ["manualReviewRequired"] = true
                },
                ["personalityAndHistoryConsistency"] = new Dictionary<string, object>
                {
                    ["required"] = 0.90d,
                    ["manualReviewRequired"] = true
                }
            };
            bool hardPass = visibleReplies == SkillExamReplyTarget
                && expectedTopThreePasses == visibleReplies
                && promptInjectionPasses == visibleReplies
                && numericLeakCount == 0
                && unsupportedActionCount == 0
                && wrongOwnerCount == 0
                && missingCoverage.Count == 0
                && hardFailures.Count == 0;
            Dictionary<string, object> report = new Dictionary<string, object>
            {
                ["ok"] = hardPass,
                ["schema"] = "reign-skill-awareness-exam-report-v1",
                ["examId"] = String(state, "examId"),
                ["campaignId"] = campaignId,
                ["seed"] = ReadLong(state, "seed", 1042),
                ["baselineSaveName"] = String(state, "baselineSaveName"),
                ["derivativeSaveName"] = String(state, "derivativeSaveName"),
                ["visibleReplyCount"] = visibleReplies,
                ["visibleReplyTarget"] = SkillExamReplyTarget,
                ["hardGateStatus"] = hardPass ? "passed" : "failed",
                ["qualitativeGateStatus"] = "pending_codex_review",
                ["repairStatus"] = "no_changes_made_during_original_exam",
                ["roster"] = roster,
                ["scenarioSummaries"] = scenarioSummaries,
                ["replyReviews"] = replyReviews,
                ["pairReviews"] = pairReviews,
                ["groupReview"] = groupReview,
                ["gates"] = gates,
                ["hardFailures"] = hardFailures,
                ["providerEvidence"] = new Dictionary<string, object>
                {
                    ["observedAuditLlmRows"] = providerCallCount,
                    ["observedRetryRows"] = providerRetryCount,
                    ["sessionSummaryAllowance"] = 11,
                    ["averagePromptBuildMs"] = promptBuildSamples == 0
                        ? 0d : Math.Round(promptBuildMs / (double)promptBuildSamples, 2)
                },
                ["completedUtc"] = DateTimeOffset.UtcNow.ToString("o"),
                ["recommendationPolicy"] =
                    "Awkward or subjective wording is reported only. Production changes require reproducible prompt, selection, ownership, enforcement, or state evidence."
            };
            string artifactDirectory = String(state, "artifactDirectory");
            Directory.CreateDirectory(artifactDirectory);
            string path = Path.Combine(artifactDirectory, "skill-exam-report.json");
            report["reportPath"] = path;
            File.WriteAllText(path, Json.Serialize(report), Encoding.UTF8);
            string reviewPath = Path.Combine(artifactDirectory, "skill-exam-review-pack.txt");
            File.WriteAllText(reviewPath, BuildSkillExamReviewPack(report), Encoding.UTF8);
            report["reviewPackPath"] = reviewPath;
            File.WriteAllText(path, Json.Serialize(report), Encoding.UTF8);
            return report;
        }

        private static Dictionary<string, object> ReviewSkillExamReply(
            string scenarioId,
            Dictionary<string, object> scenario,
            Dictionary<string, object> turn,
            Dictionary<string, object> command,
            Dictionary<string, object> reply,
            Dictionary<string, Dictionary<string, object>> targets,
            List<Dictionary<string, object>> audit)
        {
            Dictionary<string, object> raw = ReadObject(reply, "rawResponse");
            string heroId = FirstNonEmpty(String(reply, "heroId"), String(raw, "heroStringId"),
                SkillExamStrings(scenario, "targetHeroIds").FirstOrDefault());
            string textValue = FirstNonEmpty(String(reply, "text"), String(raw, "reply"));
            string correlationId = FirstNonEmpty(String(reply, "correlationId"),
                String(raw, "correlationId"));
            Dictionary<string, object> promptRow = audit.LastOrDefault(row =>
                String(row, "phase").Equals("prompt.built", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(correlationId)
                    || String(row, "correlationId").Equals(correlationId, StringComparison.OrdinalIgnoreCase)))
                ?? new Dictionary<string, object>();
            Dictionary<string, object> promptData = ReadObject(promptRow, "data");
            Dictionary<string, object> envelope = ReadObject(promptData, "promptEnvelope");
            Dictionary<string, object> awareness = ReadObject(envelope, "skillAwareness");
            List<Dictionary<string, object>> observedTop = ReadObjects(awareness, "topSkills");
            List<Dictionary<string, object>> observedOnDemand = ReadObjects(awareness, "onDemandSkills");
            Dictionary<string, object> target = targets.TryGetValue(heroId,
                out Dictionary<string, object> snapshot)
                ? snapshot : new Dictionary<string, object>();
            List<Dictionary<string, object>> expectedTop = SkillExamTopSkills(target);
            List<string> expectedTopKeys = expectedTop.Select(row => String(row, "key")).ToList();
            List<string> observedTopKeys = observedTop.Select(row => String(row, "key")).ToList();
            bool topCorrect = expectedTopKeys.SequenceEqual(observedTopKeys,
                StringComparer.OrdinalIgnoreCase)
                && observedTop.Count == expectedTop.Count
                && observedTop.Zip(expectedTop, (observed, expected) =>
                    ReadLong(observed, "value", -1) == ReadLong(expected, "value", -2)).All(value => value);
            string promptText = string.Join("\n", ReadObjects(promptData, "messages")
                .Select(message => String(message, "content")));
            bool promptPresent = promptText.IndexOf("CORE PRACTICED CAPABILITIES",
                    StringComparison.OrdinalIgnoreCase) >= 0
                && observedTop.All(row => promptText.IndexOf(String(row, "name"),
                    StringComparison.OrdinalIgnoreCase) >= 0);
            bool ownerCorrect = string.IsNullOrWhiteSpace(heroId)
                ? false : String(promptRow, "heroId").Equals(heroId, StringComparison.OrdinalIgnoreCase);
            bool numericLeak = SkillExamContainsNumericSkillLeak(textValue);
            List<string> explicitSkills = SkillExamExplicitSkillNames(textValue);
            List<string> topCues = expectedTopKeys.Where(skill =>
                SkillExamResponseCues[skill].Any(cue => textValue.IndexOf(
                    cue, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            bool relevant = SkillExamBool(turn, "skillRelevant", false);
            bool identityLanguage = Regex.IsMatch(textValue ?? "",
                @"\b(I am|I'm|known as|renowned as|my identity|defines me)\b.{0,55}\b(master|expert|skilled|smith|scout|merchant|engineer|physician|warrior|commander)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool gratuitousIdentity = !relevant && (identityLanguage || explicitSkills.Count >= 2);
            int words = Regex.Matches(textValue ?? "", @"\b[\p{L}\p{N}']+\b").Count;
            int cueHits = SkillExamResponseCues.Values.SelectMany(value => value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(cue => textValue.IndexOf(cue, StringComparison.OrdinalIgnoreCase) >= 0);
            bool dominanceRisk = words > 0 && (explicitSkills.Count >= 3
                || identityLanguage && cueHits >= 4
                || cueHits >= 9 && cueHits / (double)Math.Max(1, words) > 0.10d);
            int queuedActions = (int)Math.Max(
                ReadLong(reply, "queuedActionCount", 0),
                ReadLong(raw, "queuedActionCount", 0));
            List<Dictionary<string, object>> failures = new List<Dictionary<string, object>>();
            Action<string, string> fail = (kind, detail) => failures.Add(new Dictionary<string, object>
            {
                ["scenarioId"] = scenarioId, ["heroId"] = heroId,
                ["correlationId"] = correlationId, ["kind"] = kind, ["detail"] = detail
            });
            if (!topCorrect) fail("top_three_mismatch", "Expected "
                + string.Join(",", expectedTopKeys) + "; observed " + string.Join(",", observedTopKeys) + ".");
            if (!promptPresent) fail("skill_prompt_missing", "The stable top-three capability block was not proven in the final prompt.");
            if (!ownerCorrect) fail("skill_owner_mismatch", "Prompt owner did not match the responding hero.");
            if (numericLeak) fail("numeric_skill_leak", "Visible dialogue exposed a numeric skill level.");
            if (queuedActions > 0) fail("unexpected_action", "Natural skill exam turn queued " + queuedActions + " action(s).");
            return new Dictionary<string, object>
            {
                ["scenarioId"] = scenarioId,
                ["pairId"] = String(scenario, "pairId"),
                ["mode"] = String(scenario, "mode"),
                ["phase"] = String(turn, "phase"),
                ["skillRelevant"] = relevant,
                ["heroId"] = heroId,
                ["heroName"] = String(target, "name"),
                ["cultureId"] = String(target, "cultureId"),
                ["role"] = SkillExamRole(target),
                ["courtCharacterCell"] = String(target, "courtCharacterCell"),
                ["playerText"] = String(command, "text"),
                ["reply"] = textValue,
                ["correlationId"] = correlationId,
                ["expectedTopSkills"] = expectedTop,
                ["observedTopSkills"] = observedTopKeys.ToArray(),
                ["observedOnDemandSkills"] = observedOnDemand.Select(row => String(row, "key")).ToArray(),
                ["expectedContextSkills"] = SkillExamStrings(turn, "expectedContextSkills").ToArray(),
                ["topThreeCorrect"] = topCorrect,
                ["skillPromptPresent"] = promptPresent,
                ["ownerCorrect"] = ownerCorrect,
                ["numericSkillLeak"] = numericLeak,
                ["queuedActionCount"] = queuedActions,
                ["explicitSkillNames"] = explicitSkills.ToArray(),
                ["topSkillCueKeys"] = topCues.ToArray(),
                ["topSkillCueObserved"] = topCues.Count > 0,
                ["gratuitousSkillIdentity"] = gratuitousIdentity,
                ["skillDominanceRisk"] = dominanceRisk,
                ["manualReviewRequired"] = true,
                ["hardFailures"] = failures
            };
        }

        private static List<Dictionary<string, object>> BuildSkillExamPairReviews(
            Dictionary<string, object> roster,
            List<Dictionary<string, object>> replies)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> pair in ReadObjects(roster, "pairs"))
            {
                string pairId = String(pair, "pairId");
                List<Dictionary<string, object>> pairReplies = replies.Where(row =>
                    String(row, "pairId").Equals(pairId, StringComparison.OrdinalIgnoreCase)
                    && String(row, "phase").Equals("relevant", StringComparison.OrdinalIgnoreCase)).ToList();
                List<string> heroIds = SkillExamStrings(pair, "heroIds");
                Dictionary<string, object> first = pairReplies.FirstOrDefault(row =>
                    String(row, "heroId").Equals(heroIds.FirstOrDefault(), StringComparison.OrdinalIgnoreCase));
                Dictionary<string, object> second = pairReplies.FirstOrDefault(row =>
                    String(row, "heroId").Equals(heroIds.Skip(1).FirstOrDefault(), StringComparison.OrdinalIgnoreCase));
                HashSet<string> firstCues = new HashSet<string>(
                    SkillExamStrings(first, "topSkillCueKeys"), StringComparer.OrdinalIgnoreCase);
                HashSet<string> secondCues = new HashSet<string>(
                    SkillExamStrings(second, "topSkillCueKeys"), StringComparer.OrdinalIgnoreCase);
                bool contrast = first != null && second != null
                    && !firstCues.SetEquals(secondCues)
                    && (firstCues.Count > 0 || secondCues.Count > 0);
                result.Add(new Dictionary<string, object>
                {
                    ["pairId"] = pairId,
                    ["scenarioKey"] = String(pair, "scenarioKey"),
                    ["heroIds"] = heroIds.ToArray(),
                    ["similarityEvidence"] = ReadObject(pair, "similarityEvidence"),
                    ["topSkillContrast"] = ReadObjects(pair, "topSkillContrast"),
                    ["firstObservedTopSkillCues"] = firstCues.ToArray(),
                    ["secondObservedTopSkillCues"] = secondCues.ToArray(),
                    ["automaticBehavioralContrast"] = contrast,
                    ["manualBlindedMatchRequired"] = true
                });
            }
            return result;
        }

        private static Dictionary<string, object> BuildSkillExamGroupReview(
            List<Dictionary<string, object>> replies)
        {
            List<Dictionary<string, object>> relevant = replies.Where(row =>
                String(row, "scenarioId").Equals("GRP-01", StringComparison.OrdinalIgnoreCase)
                && String(row, "phase").Equals("relevant", StringComparison.OrdinalIgnoreCase)).ToList();
            List<Dictionary<string, object>> ordinary = replies.Where(row =>
                String(row, "scenarioId").Equals("GRP-01", StringComparison.OrdinalIgnoreCase)
                && String(row, "phase").Equals("ordinary", StringComparison.OrdinalIgnoreCase)).ToList();
            int distinct = relevant.Count(row =>
                SkillExamStrings(row, "topSkillCueKeys").Count > 0
                || SkillExamStrings(row, "explicitSkillNames").Count > 0);
            return new Dictionary<string, object>
            {
                ["ordinaryReplyCount"] = ordinary.Count,
                ["relevantReplyCount"] = relevant.Count,
                ["distinctContributorCount"] = distinct,
                ["ordinaryRestraintCount"] = ordinary.Count(row =>
                    !SkillExamBool(row, "gratuitousSkillIdentity", false)),
                ["contributors"] = relevant.Select(row => new Dictionary<string, object>
                {
                    ["heroId"] = String(row, "heroId"),
                    ["heroName"] = String(row, "heroName"),
                    ["topSkillCues"] = SkillExamStrings(row, "topSkillCueKeys").ToArray(),
                    ["explicitSkillNames"] = SkillExamStrings(row, "explicitSkillNames").ToArray(),
                    ["reply"] = String(row, "reply")
                }).ToList(),
                ["manualSequentialAwarenessReviewRequired"] = true
            };
        }

        private static string BuildSkillExamReviewPack(Dictionary<string, object> report)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("REIGN FORTY-REPLY SUBTLE SKILL-AWARENESS EXAM");
            builder.AppendLine("Exam: " + String(report, "examId"));
            builder.AppendLine("Campaign: " + String(report, "campaignId"));
            builder.AppendLine("Visible replies: " + ReadLong(report, "visibleReplyCount", 0)
                + "/" + ReadLong(report, "visibleReplyTarget", SkillExamReplyTarget));
            builder.AppendLine("Hard gates: " + String(report, "hardGateStatus"));
            builder.AppendLine("Qualitative gates: pending Codex review");
            builder.AppendLine();
            foreach (IGrouping<string, Dictionary<string, object>> scenario in
                ReadObjects(report, "replyReviews").GroupBy(row => String(row, "scenarioId")))
            {
                builder.AppendLine("=== " + scenario.Key + " ===");
                foreach (Dictionary<string, object> reply in scenario)
                {
                    builder.AppendLine("[" + String(reply, "phase") + "] "
                        + String(reply, "heroName") + " (" + String(reply, "heroId") + ")");
                    builder.AppendLine("Player: " + String(reply, "playerText"));
                    builder.AppendLine("NPC: " + String(reply, "reply"));
                    builder.AppendLine("Top three: " + string.Join(", ",
                        ReadObjects(reply, "expectedTopSkills").Select(row =>
                            String(row, "name") + " — " + String(row, "band"))));
                    builder.AppendLine("Detected top-skill cues: "
                        + string.Join(", ", SkillExamStrings(reply, "topSkillCueKeys")));
                    builder.AppendLine("On-demand context: "
                        + string.Join(", ", SkillExamStrings(reply, "observedOnDemandSkills")));
                    builder.AppendLine("Automatic flags: "
                        + (SkillExamBool(reply, "gratuitousSkillIdentity", false) ? "gratuitous-skill-identity " : "")
                        + (SkillExamBool(reply, "skillDominanceRisk", false) ? "skill-dominance-risk " : "")
                        + (SkillExamBool(reply, "numericSkillLeak", false) ? "numeric-leak " : "")
                        + (ReadObjects(reply, "hardFailures").Count == 0 ? "none" : "hard-failure"));
                    builder.AppendLine();
                }
            }
            builder.AppendLine("REVIEW QUESTIONS");
            builder.AppendLine("1. Did expertise affect method, observation, confidence, or limits only where relevant?");
            builder.AppendLine("2. Did ordinary and return-to-character replies remain driven by personality, history, status, and motives?");
            builder.AppendLine("3. Did four of five matched pairs diverge in a way that follows their capability profiles without naming mechanics?");
            builder.AppendLine("4. Did at least four group participants contribute distinct, relevant perspectives without becoming a list of professions?");
            builder.AppendLine("5. Did any response contradict an authoritative proficiency band or turn a skill into the NPC's whole identity?");
            return builder.ToString();
        }

        private static List<Dictionary<string, object>> SkillExamReplies(
            Dictionary<string, object> command)
        {
            Dictionary<string, object> result = ReadObject(command, "result");
            List<Dictionary<string, object>> replies = ReadObjects(result, "replies");
            if (replies.Count > 0) return replies.Where(reply =>
                SkillExamBool(reply, "ok", true)
                && !string.IsNullOrWhiteSpace(String(reply, "text"))).ToList();
            string textValue = FirstNonEmpty(String(result, "text"), String(result, "reply"));
            if (string.IsNullOrWhiteSpace(textValue)) return new List<Dictionary<string, object>>();
            return new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["heroId"] = FirstNonEmpty(
                        String(ReadObject(result, "rawResponse"), "heroStringId"),
                        String(result, "heroId")),
                    ["text"] = textValue,
                    ["correlationId"] = FirstNonEmpty(
                        String(result, "correlationId"),
                        String(ReadObject(result, "rawResponse"), "correlationId")),
                    ["queuedActionCount"] = ReadLong(result, "queuedActionCount", 0),
                    ["rawResponse"] = ReadObject(result, "rawResponse")
                }
            };
        }

        private static int CountSkillExamVisibleReplies(Dictionary<string, object> report)
        {
            return ReadObjects(report, "commands")
                .Where(command => String(command, "operation")
                    .Equals("send", StringComparison.OrdinalIgnoreCase))
                .SelectMany(SkillExamReplies).Count();
        }

        private static bool SkillExamContainsNumericSkillLeak(string textValue)
        {
            if (string.IsNullOrWhiteSpace(textValue)) return false;
            string names = string.Join("|", SkillExamSkillNames.Values
                .Concat(SkillExamSkillOrder).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(Regex.Escape));
            return Regex.IsMatch(textValue,
                @"\b(?:" + names + @")\b\s*(?:skill|level|score|rating)?\s*(?:is|of|:|=)?\s*[-+]?\d{1,3}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(textValue,
                    @"\b(?:skill|level|score|rating)\s*(?:is|of|:|=)?\s*[-+]?\d{1,3}\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static List<string> SkillExamExplicitSkillNames(string textValue)
        {
            if (string.IsNullOrWhiteSpace(textValue)) return new List<string>();
            return SkillExamSkillOrder.Where(skill =>
                Regex.IsMatch(textValue, @"\b" + Regex.Escape(SkillExamSkillNames[skill]) + @"\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(textValue, @"\b" + Regex.Escape(skill) + @"\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .ToList();
        }

        private static List<Dictionary<string, object>> SkillExamDeterministicShortlist(
            List<Dictionary<string, object>> candidates,
            long seed,
            int limit)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, Dictionary<string, object>> group in candidates
                .GroupBy(row => String(row, "cultureId"), StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (Dictionary<string, object> target in group
                    .OrderBy(row => SkillExamStableOrder(seed + "|culture|" + String(row, "heroId")))
                    .Take(4))
                {
                    if (selected.Add(String(target, "heroId"))) result.Add(target);
                }
            }
            foreach (Dictionary<string, object> target in candidates
                .OrderBy(row => SkillExamStableOrder(seed + "|all|" + String(row, "heroId"))))
            {
                if (result.Count >= limit) break;
                if (selected.Add(String(target, "heroId"))) result.Add(target);
            }
            return result.Take(limit).ToList();
        }

        private static void EnrichSkillExamTarget(
            string campaignId,
            Dictionary<string, object> target)
        {
            try
            {
                Dictionary<string, object> loaded = Post("/character-editor/load",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["heroStringId"] = String(target, "heroId")
                    });
                Dictionary<string, object> documents = ReadObject(loaded, "documents");
                Dictionary<string, object> traits = ReadObject(documents, "traits");
                Dictionary<string, object> court = ReadObject(traits, "courtCharacter");
                target["courtCharacterCell"] = FirstNonEmpty(
                    String(court, "cellId"), String(court, "title"));
                target["courtCharacterTitle"] = String(court, "title");
                target["courtHonorLevel"] = ReadLong(court, "honorLevel", 99);
                target["courtBoldnessLevel"] = ReadLong(court, "boldnessLevel", 99);
                target["courtCharacterAvailable"] = SkillExamBool(court, "available", false);
                Dictionary<string, object> profile = ReadObject(documents, "profile");
                if (ReadObject(target, "skills").Count == 0
                    && ReadObject(profile, "skills").Count > 0)
                    target["skills"] = ReadObject(profile, "skills");
                target["characterProfileLoaded"] = IsOk(loaded);
            }
            catch (Exception ex)
            {
                target["characterProfileLoaded"] = false;
                target["characterProfileError"] = ex.Message;
            }
            target["topSkills"] = SkillExamTopSkills(target);
            target["role"] = SkillExamRole(target);
        }

        private static List<Dictionary<string, object>> SkillExamTopSkills(
            Dictionary<string, object> target)
        {
            Dictionary<string, object> skills = ReadObject(target, "skills");
            return SkillExamSkillOrder.Select((key, order) =>
            {
                int value = (int)ReadLong(skills, key, 0);
                int bandIndex = value <= 0 ? 0 : Math.Min(13, value / 20);
                return new Dictionary<string, object>
                {
                    ["key"] = key,
                    ["name"] = SkillExamSkillNames[key],
                    ["value"] = value,
                    ["band"] = SkillExamBandLabels[bandIndex],
                    ["bandIndex"] = bandIndex,
                    ["canonicalOrder"] = order
                };
            }).OrderByDescending(row => ReadLong(row, "value", 0))
                .ThenBy(row => ReadLong(row, "canonicalOrder", 0))
                .Take(3).ToList();
        }

        private static List<string> SkillExamTopSkillKeys(Dictionary<string, object> target)
        {
            List<Dictionary<string, object>> stored = ReadObjects(target, "topSkills");
            if (stored.Count == 0) stored = SkillExamTopSkills(target);
            return stored.Select(row => String(row, "key")).ToList();
        }

        private static int SkillExamSkillValue(Dictionary<string, object> target, string skill)
        {
            return (int)ReadLong(ReadObject(target, "skills"), skill, 0);
        }

        private static string SkillExamRole(Dictionary<string, object> target)
        {
            if (SkillExamBool(target, "isRuler", false)) return "ruler";
            if (!string.IsNullOrWhiteSpace(String(target, "governorOfSettlementId"))) return "governor";
            if (SkillExamBool(target, "isLord", false)) return "noble";
            if (SkillExamBool(target, "isNotable", false)) return "notable";
            if (SkillExamBool(target, "isWanderer", false)) return "wanderer";
            return FirstNonEmpty(String(target, "occupation"), "commoner").ToLowerInvariant();
        }

        private static List<string> SkillExamStrings(
            Dictionary<string, object> value,
            string key)
        {
            if (value == null || !value.TryGetValue(key, out object raw) || raw == null)
                return new List<string>();
            IEnumerable sequence = raw as IEnumerable;
            if (sequence == null || raw is string)
                return string.IsNullOrWhiteSpace(Convert.ToString(raw))
                    ? new List<string>() : new List<string> { Convert.ToString(raw) };
            return sequence.Cast<object>().Select(Convert.ToString)
                .Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
        }

        private static bool SkillExamBool(
            Dictionary<string, object> value,
            string key,
            bool fallback)
        {
            if (value == null || !value.TryGetValue(key, out object raw) || raw == null)
                return fallback;
            try { return Convert.ToBoolean(raw, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static ulong SkillExamStableOrder(string value)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            foreach (char character in value ?? "")
            {
                hash ^= (byte)(character & 0xff);
                hash *= prime;
                hash ^= (byte)(character >> 8);
                hash *= prime;
            }
            return hash;
        }

        private static Dictionary<string, object> ResolveSkillExamState(string[] args)
        {
            string campaignId = Value(args, "--campaign", "");
            if (string.IsNullOrWhiteSpace(campaignId))
            {
                try
                {
                    Dictionary<string, object> runtime = Runtime(args);
                    campaignId = ResolveCampaign(args, runtime);
                }
                catch { }
            }
            string examId = Value(args, "--exam", "");
            if (!string.IsNullOrWhiteSpace(campaignId) && !string.IsNullOrWhiteSpace(examId))
                return LoadSkillExamState(campaignId, examId);
            if (!string.IsNullOrWhiteSpace(campaignId))
                return LoadLatestSkillExamState(campaignId);
            string campaignsRoot = Path.Combine(AppContext.BaseDirectory, "data", "campaigns");
            if (!Directory.Exists(campaignsRoot)) return new Dictionary<string, object>();
            string latest = Directory.GetFiles(campaignsRoot, "latest.json", SearchOption.AllDirectories)
                .Where(path => path.IndexOf(Path.Combine("skill-exam", "latest.json"),
                    StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(latest)) return new Dictionary<string, object>();
            Dictionary<string, object> pointer = ReadSkillExamJson(latest);
            return LoadSkillExamState(String(pointer, "campaignId"), String(pointer, "examId"));
        }

        private static string SkillExamArtifactDirectory(string campaignId, string examId)
        {
            return Path.Combine(AppContext.BaseDirectory, "data", "campaigns",
                SkillExamSafeSegment(campaignId, "campaign"), "audit", "test-data",
                "skill-exam", SkillExamSafeSegment(examId, "exam"));
        }

        private static string SkillExamStatePath(string campaignId, string examId)
        {
            return Path.Combine(SkillExamArtifactDirectory(campaignId, examId), "state.json");
        }

        private static string SkillExamLatestPath(string campaignId)
        {
            return Path.Combine(AppContext.BaseDirectory, "data", "campaigns",
                SkillExamSafeSegment(campaignId, "campaign"), "audit", "test-data",
                "skill-exam", "latest.json");
        }

        private static string SkillExamSafeSegment(string value, string fallback)
        {
            string safe = new string((value ?? "").Select(character =>
                char.IsLetterOrDigit(character) || character == '-' || character == '_'
                    ? character : '_').ToArray()).Trim('_');
            return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
        }

        private static void SaveSkillExamState(Dictionary<string, object> state)
        {
            string campaignId = String(state, "campaignId");
            string examId = String(state, "examId");
            string path = SkillExamStatePath(campaignId, examId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.Serialize(state), Encoding.UTF8);
            string latestPath = SkillExamLatestPath(campaignId);
            Directory.CreateDirectory(Path.GetDirectoryName(latestPath));
            File.WriteAllText(latestPath, Json.Serialize(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["examId"] = examId,
                ["statePath"] = path,
                ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o")
            }), Encoding.UTF8);
        }

        private static Dictionary<string, object> LoadSkillExamState(
            string campaignId,
            string examId)
        {
            return ReadSkillExamJson(SkillExamStatePath(campaignId, examId));
        }

        private static Dictionary<string, object> LoadLatestSkillExamState(string campaignId)
        {
            Dictionary<string, object> pointer = ReadSkillExamJson(SkillExamLatestPath(campaignId));
            return pointer.Count == 0 ? new Dictionary<string, object>()
                : LoadSkillExamState(campaignId, String(pointer, "examId"));
        }

        private static Dictionary<string, object> ReadSkillExamJson(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new Dictionary<string, object>();
            try
            {
                return Json.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(path, Encoding.UTF8))
                    ?? new Dictionary<string, object>();
            }
            catch { return new Dictionary<string, object>(); }
        }

        private static Dictionary<string, object> SkillExamStateSummary(
            Dictionary<string, object> state)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["found"] = state != null && state.Count > 0,
                ["examId"] = String(state, "examId"),
                ["campaignId"] = String(state, "campaignId"),
                ["status"] = String(state, "status"),
                ["completedScenarioCount"] = ReadLong(state, "completedScenarioCount", 0),
                ["scenarioCount"] = ReadObjects(state, "scenarios").Count,
                ["completedVisibleReplies"] = ReadLong(state, "completedVisibleReplies", 0),
                ["visibleReplyTarget"] = ReadLong(state, "visibleReplyTarget", SkillExamReplyTarget),
                ["activeScenarioId"] = String(state, "activeScenarioId"),
                ["activeRunId"] = String(state, "activeRunId"),
                ["pauseRequested"] = SkillExamBool(state, "pauseRequested", false),
                ["derivativeSaveName"] = String(state, "derivativeSaveName"),
                ["artifactDirectory"] = String(state, "artifactDirectory"),
                ["reportPath"] = String(state, "reportPath"),
                ["failures"] = ReadObjects(state, "failures"),
                ["updatedUtc"] = String(state, "updatedUtc")
            };
        }

        private static Dictionary<string, object> SkillExamReportFromState(
            Dictionary<string, object> state)
        {
            string path = String(state, "reportPath");
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return ReadSkillExamJson(path);
            Dictionary<string, object> partial = BuildSkillExamAggregateReport(state);
            partial["partial"] = !String(state, "status")
                .Equals("completed", StringComparison.OrdinalIgnoreCase);
            partial["status"] = String(state, "status");
            return partial;
        }

        private static bool SkillExamTerminalStatus(string status)
        {
            return new[] { "completed", "cancelled" }
                .Contains(status ?? "", StringComparer.OrdinalIgnoreCase);
        }
    }
}
