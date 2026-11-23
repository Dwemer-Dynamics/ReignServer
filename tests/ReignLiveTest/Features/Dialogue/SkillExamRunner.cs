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
    }
}
