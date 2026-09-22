using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object>
            RunSovereignDemeanorProviderQualification(string[] args)
        {
            string selectedCase = ArgValue(args, "--case", "").Trim();
            int extraSeed = int.TryParse(ArgValue(args, "--seed", "271828"),
                NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int parsedSeed) ? parsedSeed : 271828;
            List<Dictionary<string, object>> cases =
                BuildSovereignDemeanorPairwiseCases(extraSeed)
                    .Where(row => string.IsNullOrWhiteSpace(selectedCase)
                        || ReadString(row, "caseId", "").Equals(
                            selectedCase,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();
            if (cases.Count == 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "No sovereign-demeanor case matched --case."
                };

            string buildVersion = CurrentConversationBuildVersion();
            string reportDirectory = Path.Combine(TestsDir,
                "sovereign-demeanor");
            Directory.CreateDirectory(reportDirectory);
            string progressSuffix = string.IsNullOrWhiteSpace(selectedCase)
                ? "matrix" : selectedCase.ToLowerInvariant();
            string progressPath = Path.Combine(reportDirectory,
                "provider-progress-seed-" + extraSeed.ToString(
                    CultureInfo.InvariantCulture) + "-"
                + progressSuffix + ".json");
            List<Dictionary<string, object>> results =
                new List<Dictionary<string, object>>();
            if (HasArg(args, "--resume") && File.Exists(progressPath))
            {
                Dictionary<string, object> progress =
                    ReadJsonObject(progressPath);
                if (ReadInt(progress, "seed", -1) == extraSeed
                    && ReadString(progress, "buildVersion", "")
                        == buildVersion)
                    results = ReadDictionaryList(progress, "results");
            }
            HashSet<string> completedCaseIds = new HashSet<string>(
                results.Select(row => ReadString(row, "caseId", "")),
                StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> testCase in cases)
            {
                string caseId = ReadString(testCase, "caseId", "");
                if (completedCaseIds.Contains(caseId)) continue;
                if (ReadBool(testCase, "groupReproduction", false))
                {
                    string groupId = ReadString(testCase, "groupId", "");
                    testCase["groupTurnResponses"] = results
                        .Where(row => ReadBool(row,
                                "groupReproduction", false)
                            && ReadString(row, "groupId", "") == groupId)
                        .Select(row => new Dictionary<string, object>
                        {
                            ["heroStringId"] = ReadString(row,
                                "caseId", ""),
                            ["reply"] = ReadString(row,
                                "finalReply", "")
                        }).ToList();
                }
                results.Add(RunSovereignDemeanorProviderCase(testCase));
                completedCaseIds.Add(caseId);
                WriteJsonObject(progressPath,
                    new Dictionary<string, object>
                    {
                        ["schema"] =
                            "reign-sovereign-demeanor-provider-progress-v1",
                        ["status"] = "running",
                        ["buildVersion"] = buildVersion,
                        ["seed"] = extraSeed,
                        ["selectedCase"] = selectedCase,
                        ["completedCount"] = results.Count,
                        ["updatedUtc"] = DateTime.UtcNow.ToString("o"),
                        ["results"] = results
                    });
            }

            int hardViolations = results.Count(row =>
                !ReadBool(row, "hardSafetyPassed", false));
            int repairCount = results.Count(row =>
                !ReadString(ReadDictionary(row, "enforcement"),
                    "outcome", "").Equals("accepted",
                        StringComparison.OrdinalIgnoreCase));
            int fallbackCount = results.Count(row =>
                ReadBool(ReadDictionary(row, "enforcement"),
                    "fallbackUsed", false));
            int calibrated = results.Count(row =>
                ReadBool(row, "calibratedDemeanorPassed", false));
            double agreement = results.Count == 0 ? 0d
                : calibrated / (double)results.Count;
            HashSet<string> authorityCoverage = new HashSet<string>(
                results.Select(row => ReadString(row,
                    "authorityClass", "")),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> dangerCoverage = new HashSet<string>(
                results.Select(row => ReadString(row,
                    "dangerBand", "")),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> personalityCoverage = new HashSet<string>(
                results.Select(row => ReadString(row,
                    "personality", "")),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> relationshipCoverage = new HashSet<string>(
                results.Select(row => ReadString(row,
                    "relationship", "")),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> sceneCoverage = new HashSet<string>(
                results.Select(row => ReadString(row,
                    "scene", "")),
                StringComparer.OrdinalIgnoreCase);
            int originalReproductionSpeakers = results.Count(row =>
                ReadBool(row, "groupReproduction", false)
                && ReadString(row, "groupId", "")
                    == "zeonica_bathhouse_four_lords");
            bool completeMatrix = string.IsNullOrWhiteSpace(selectedCase)
                && results.Count == 36
                && authorityCoverage.Count == 9
                && dangerCoverage.SetEquals(new[]
                {
                    "low", "material", "high",
                    "immediate_or_extreme"
                })
                && personalityCoverage.Count == 4
                && relationshipCoverage.Count == 4
                && sceneCoverage.Count == 6
                && originalReproductionSpeakers == 4;
            Dictionary<string, object> report =
                new Dictionary<string, object>
                {
                    ["schema"] =
                        "reign-sovereign-demeanor-provider-report-v1",
                    ["qualificationId"] = "sovereign-demeanor-pairwise",
                    ["seed"] = extraSeed,
                    ["caseCount"] = results.Count,
                    ["hardSafetyViolations"] = hardViolations,
                    ["repairCount"] = repairCount,
                    ["repairRate"] = results.Count == 0 ? 0d
                        : repairCount / (double)results.Count,
                    ["deterministicFallbackCount"] = fallbackCount,
                    ["calibratedDemeanorAgreement"] = agreement,
                    ["requiredAgreement"] = .95d,
                    ["matrixComplete"] = completeMatrix,
                    ["authorityCoverage"] = authorityCoverage.OrderBy(
                        value => value).ToList(),
                    ["dangerCoverage"] = dangerCoverage.OrderBy(
                        value => value).ToList(),
                    ["personalityCoverage"] = personalityCoverage.OrderBy(
                        value => value).ToList(),
                    ["relationshipCoverage"] = relationshipCoverage.OrderBy(
                        value => value).ToList(),
                    ["sceneCoverage"] = sceneCoverage.OrderBy(
                        value => value).ToList(),
                    ["originalBathhouseReproductionSpeakers"] =
                        originalReproductionSpeakers,
                    ["results"] = results
                };
            report["ok"] = hardViolations == 0
                && agreement >= .95d
                && fallbackCount == 0
                && repairCount <= Math.Max(1,
                    (int)Math.Floor(results.Count * .10d))
                && (completeMatrix || !string.IsNullOrWhiteSpace(
                    selectedCase));
            string reportPath = Path.Combine(reportDirectory,
                "provider-" + DateTime.UtcNow.ToString(
                    "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-seed-" + extraSeed.ToString(
                    CultureInfo.InvariantCulture) + ".json");
            WriteJsonObject(reportPath, report);
            WriteJsonObject(progressPath,
                new Dictionary<string, object>
                {
                    ["schema"] =
                        "reign-sovereign-demeanor-provider-progress-v1",
                    ["status"] = ReadBool(report, "ok", false)
                        ? "completed" : "needs_review",
                    ["buildVersion"] = buildVersion,
                    ["seed"] = extraSeed,
                    ["selectedCase"] = selectedCase,
                    ["completedCount"] = results.Count,
                    ["updatedUtc"] = DateTime.UtcNow.ToString("o"),
                    ["reportPath"] = reportPath,
                    ["results"] = results
                });
            report["reportPath"] = reportPath;
            return report;
        }

        private static Dictionary<string, object>
            ReclassifySovereignDemeanorProviderReport(string[] args)
        {
            string sourcePath = ArgValue(args,
                "--reclassify-sovereign-demeanor-report", "").Trim();
            if (string.IsNullOrWhiteSpace(sourcePath)
                || !File.Exists(sourcePath))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "The retained sovereign-demeanor provider report was not found."
                };
            Dictionary<string, object> source = ReadJsonObject(sourcePath);
            int seed = ReadInt(source, "seed", 271828);
            Dictionary<string, Dictionary<string, object>> cases =
                BuildSovereignDemeanorPairwiseCases(seed).ToDictionary(
                    row => ReadString(row, "caseId", ""),
                    row => row, StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> results =
                ReadDictionaryList(source, "results");
            List<Dictionary<string, object>> completed =
                new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> row in results)
            {
                string caseId = ReadString(row, "caseId", "");
                if (!cases.TryGetValue(caseId,
                        out Dictionary<string, object> testCase))
                    continue;
                if (ReadBool(testCase, "groupReproduction", false))
                {
                    string groupId = ReadString(testCase, "groupId", "");
                    testCase["groupTurnResponses"] = completed
                        .Where(prior => ReadBool(prior,
                                "groupReproduction", false)
                            && ReadString(prior, "groupId", "")
                                == groupId)
                        .Select(prior => new Dictionary<string, object>
                        {
                            ["heroStringId"] = ReadString(prior,
                                "caseId", ""),
                            ["reply"] = ReadString(prior,
                                "finalReply", "")
                        }).ToList();
                }
                Dictionary<string, object> context =
                    BuildSovereignDemeanorClassificationContext(
                        testCase, out Dictionary<string, object> posture);
                Dictionary<string, object> conduct =
                    ClassifyPoliticalConduct(ReadString(row,
                        "finalReply", ""), posture, context);
                int observed = ReadInt(conduct, "defianceTier", -1);
                int maximum = ReadInt(posture,
                    "maximumDefianceTier", 0);
                int minimum = ReadInt(posture,
                    "preferredDefianceMinimum", 0);
                int target = ReadInt(posture,
                    "preferredDefianceMaximum", maximum);
                bool hardSafety = !string.IsNullOrWhiteSpace(
                        ReadString(row, "finalReply", ""))
                    && observed >= 0 && observed <= maximum
                    && ReadStringList(conduct, "violations").Count == 0;
                row["maximumDefianceTier"] = maximum;
                row["targetDefianceTier"] = target;
                row["minimumCalibratedTier"] = minimum;
                row["observedDefianceTier"] = observed;
                row["hardSafetyPassed"] = hardSafety;
                row["calibratedDemeanorPassed"] = hardSafety
                    && observed >= minimum && observed <= target;
                row["conduct"] = conduct;
                row["classificationBuildVersion"] =
                    CurrentConversationBuildVersion();
                Dictionary<string, object> enforcement =
                    ReadDictionary(row, "enforcement");
                if (enforcement != null) enforcement["observed"] = conduct;
                completed.Add(row);
            }
            int hardViolations = completed.Count(row =>
                !ReadBool(row, "hardSafetyPassed", false));
            int calibrated = completed.Count(row =>
                ReadBool(row, "calibratedDemeanorPassed", false));
            int repairCount = completed.Count(row =>
                !ReadString(ReadDictionary(row, "enforcement"),
                    "outcome", "").Equals("accepted",
                        StringComparison.OrdinalIgnoreCase));
            int fallbackCount = completed.Count(row =>
                ReadBool(ReadDictionary(row, "enforcement"),
                    "fallbackUsed", false));
            double agreement = completed.Count == 0 ? 0d
                : calibrated / (double)completed.Count;
            Dictionary<string, object> report =
                new Dictionary<string, object>(source)
                {
                    ["schema"] =
                        "reign-sovereign-demeanor-provider-report-v1",
                    ["qualificationMode"] =
                        "deterministic_reclassification",
                    ["sourceProviderReport"] =
                        Path.GetFullPath(sourcePath),
                    ["classificationBuildVersion"] =
                        CurrentConversationBuildVersion(),
                    ["caseCount"] = completed.Count,
                    ["hardSafetyViolations"] = hardViolations,
                    ["repairCount"] = repairCount,
                    ["repairRate"] = completed.Count == 0 ? 0d
                        : repairCount / (double)completed.Count,
                    ["deterministicFallbackCount"] = fallbackCount,
                    ["calibratedDemeanorAgreement"] = agreement,
                    ["results"] = completed
                };
            report["ok"] = hardViolations == 0
                && agreement >= ReadDouble(report,
                    "requiredAgreement", .95d)
                && fallbackCount == 0
                && repairCount <= Math.Max(1,
                    (int)Math.Floor(completed.Count * .10d))
                && (ReadBool(report, "matrixComplete", false)
                    || completed.Count == 1);
            string directory = Path.Combine(TestsDir,
                "sovereign-demeanor");
            Directory.CreateDirectory(directory);
            string reportPath = Path.Combine(directory,
                "provider-reclassified-" + DateTime.UtcNow.ToString(
                    "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-seed-" + seed.ToString(
                    CultureInfo.InvariantCulture) + ".json");
            report["reportPath"] = reportPath;
            WriteJsonObject(reportPath, report);
            return report;
        }

        private static Dictionary<string, object>
            BuildSovereignDemeanorClassificationContext(
                Dictionary<string, object> testCase,
                out Dictionary<string, object> posture)
        {
            string authorityClass = ReadString(testCase,
                "authorityClass", "peer");
            string personality = ReadString(testCase,
                "personality", "cautious");
            string relationship = ReadString(testCase,
                "relationship", "neutral");
            string sceneName = ReadString(testCase, "scene", "");
            string stimulus = ReadString(testCase,
                "dangerStimulus", "ordinary_request");
            Dictionary<string, int> traitValues =
                SovereignDemeanorTraits(personality);
            Dictionary<string, object> characteristics =
                MotiveTestCharacteristics(traitValues);
            MotiveTestCourtCharacter(characteristics,
                traitValues["honor"], traitValues["boldness"], false);
            Dictionary<string, object> courtVirtues = ReadDictionary(
                ReadDictionary(characteristics, "traits"),
                "courtVirtues");
            courtVirtues["judgment"] = traitValues["judgment"];
            courtVirtues["loyalty"] = traitValues["loyalty"];
            bool sovereign = authorityClass == "own_sovereign"
                || authorityClass == "foreign_sovereign";
            Dictionary<string, object> identity =
                new Dictionary<string, object>
                {
                    ["canonicalNameAllowed"] = true,
                    ["usableName"] = "Aureon",
                    ["authorityView"] = new Dictionary<string, object>
                    {
                        ["identityVerified"] = true,
                        ["personalIdentityKnown"] = true,
                        ["publicOfficeKnown"] = sovereign,
                        ["realmSovereignKnown"] = sovereign,
                        ["subjectIsObserverSovereign"] =
                            authorityClass == "own_sovereign",
                        ["authorityRelationship"] = authorityClass,
                        ["validFormalAddresses"] = sovereign
                            ? new List<string>
                            {
                                "Your Grace", "Your Majesty", "Sire"
                            } : new List<string>()
                    }
                };
            Dictionary<string, object> payload =
                new Dictionary<string, object>
                {
                    ["politicalAuthorityClass"] = authorityClass,
                    ["sceneTurnId"] = ReadString(testCase,
                        "caseId", ""),
                    ["worldDay"] = 100d,
                    ["retaliationCapacity"] =
                        stimulus == "credible_retaliation" ? 95 : 30,
                    ["authorityDependency"] =
                        relationship == "dependent" ? 95 : 35,
                    ["escapeChance"] =
                        stimulus == "immediate_lethal" ? 0 : 55,
                    ["politicalCover"] =
                        stimulus == "protected_candor" ? 90 : 0,
                    ["immediateLethalDanger"] =
                        stimulus == "immediate_lethal",
                    ["coercive"] =
                        stimulus == "credible_retaliation",
                    ["groupTurnResponses"] = ReadDictionaryList(
                        testCase, "groupTurnResponses")
                };
            posture = BuildPoliticalRiskPosture(characteristics,
                new Dictionary<string, object>(),
                new Dictionary<string, object>
                {
                    ["overallBand"] = relationship
                }, new Dictionary<string, object>(),
                new Dictionary<string, object>
                {
                    ["sceneType"] = sceneName,
                    ["captivity"] = sceneName == "captivity"
                }, identity, payload, new Dictionary<string, object>(),
                ReadString(testCase, "playerText", ""), "dialogue");
            return new Dictionary<string, object>
            {
                ["identityView"] = identity,
                ["politicalRiskPosture"] = posture
            };
        }

        private static List<Dictionary<string, object>>
            BuildSovereignDemeanorPairwiseCases(int seed)
        {
            string[] authorities =
            {
                "own_sovereign", "foreign_sovereign", "captor",
                "military_commander", "local_ruler", "superior_noble",
                "patron", "peer", "subordinate"
            };
            string[] personalities =
            {
                "cautious", "bold", "principled", "reckless"
            };
            string[] relationships =
            {
                "loyal", "neutral", "hostile", "dependent"
            };
            string[] scenes =
            {
                "individual_hall", "court_audience",
                "zeonica_castle_bathhouse", "captivity",
                "correspondence", "wilderness"
            };
            string[] playerLines =
            {
                "Give me your honest judgment. Should I levy another season of grain from these villages?",
                "You know I can strip your office and income. Tell me now whether you will support this decree.",
                "My sword rests at your throat. Answer plainly: will you yield, or force my hand?",
                "Your allies are gathered here and I have promised safe conduct. Speak plainly about my decision."
            };
            Dictionary<int, int> dangerOccurrences =
                Enumerable.Range(0, 4).ToDictionary(value => value,
                    value => 0);
            List<Dictionary<string, object>> result =
                new List<Dictionary<string, object>>();
            int ordinal = 0;
            for (int authorityIndex = 0;
                authorityIndex < authorities.Length; authorityIndex++)
            {
                for (int personalityIndex = 0;
                    personalityIndex < personalities.Length;
                    personalityIndex++)
                {
                    int stimulus = (authorityIndex + personalityIndex) % 4;
                    int relationship =
                        (authorityIndex + 2 * personalityIndex) % 4;
                    int scene = dangerOccurrences[stimulus]++ % scenes.Length;
                    bool groupReproduction = authorityIndex == 0;
                    if (groupReproduction)
                    {
                        stimulus = 0;
                        scene = 2;
                    }
                    ordinal++;
                    result.Add(new Dictionary<string, object>
                    {
                        ["caseId"] = "SD-NL-"
                            + ordinal.ToString("000",
                                CultureInfo.InvariantCulture),
                        ["seed"] = seed,
                        ["authorityClass"] = authorities[authorityIndex],
                        ["personality"] = personalities[personalityIndex],
                        ["relationship"] = relationships[relationship],
                        ["scene"] = scenes[scene],
                        ["dangerStimulus"] = new[]
                        {
                            "ordinary_request", "credible_retaliation",
                            "immediate_lethal", "protected_candor"
                        }[stimulus],
                        ["playerText"] = groupReproduction
                            ? "My lords, I summoned all four of you to this bathhouse inside Zeonica castle for candid counsel. Should I levy another season of grain from the villages? Speak honestly, but remember where you stand."
                            : playerLines[stimulus],
                        ["groupReproduction"] = groupReproduction,
                        ["groupId"] = groupReproduction
                            ? "zeonica_bathhouse_four_lords" : ""
                    });
                }
            }
            return result;
        }

        private static Dictionary<string, object>
            RunSovereignDemeanorProviderCase(
                Dictionary<string, object> testCase)
        {
            string caseId = ReadString(testCase, "caseId", "");
            string authorityClass = ReadString(testCase,
                "authorityClass", "peer");
            string personality = ReadString(testCase,
                "personality", "cautious");
            string relationship = ReadString(testCase,
                "relationship", "neutral");
            string sceneName = ReadString(testCase, "scene", "");
            string stimulus = ReadString(testCase,
                "dangerStimulus", "ordinary_request");
            string playerText = ReadString(testCase, "playerText", "");

            Dictionary<string, int> traitValues =
                SovereignDemeanorTraits(personality);
            Dictionary<string, object> characteristics =
                MotiveTestCharacteristics(traitValues);
            MotiveTestCourtCharacter(characteristics,
                traitValues["honor"], traitValues["boldness"], false);
            Dictionary<string, object> courtVirtues = ReadDictionary(
                ReadDictionary(characteristics, "traits"),
                "courtVirtues");
            courtVirtues["judgment"] = traitValues["judgment"];
            courtVirtues["loyalty"] = traitValues["loyalty"];

            bool sovereign = authorityClass == "own_sovereign"
                || authorityClass == "foreign_sovereign";
            Dictionary<string, object> identity =
                new Dictionary<string, object>
                {
                    ["canonicalNameAllowed"] = true,
                    ["usableName"] = "Aureon",
                    ["authorityView"] =
                        new Dictionary<string, object>
                        {
                            ["identityVerified"] = true,
                            ["personalIdentityKnown"] = true,
                            ["publicOfficeKnown"] = sovereign,
                            ["realmSovereignKnown"] = sovereign,
                            ["subjectIsObserverSovereign"] =
                                authorityClass == "own_sovereign",
                            ["authorityRelationship"] = authorityClass,
                            ["validFormalAddresses"] = sovereign
                                ? new List<string>
                                {
                                    "Your Grace", "Your Majesty", "Sire"
                                } : new List<string>()
                        }
                };
            Dictionary<string, object> payload =
                new Dictionary<string, object>
                {
                    ["politicalAuthorityClass"] = authorityClass,
                    ["sceneTurnId"] = caseId,
                    ["correlationId"] = caseId,
                    ["worldDay"] = 100d,
                    ["retaliationCapacity"] =
                        stimulus == "credible_retaliation" ? 95 : 30,
                    ["authorityDependency"] =
                        relationship == "dependent" ? 95 : 35,
                    ["escapeChance"] =
                        stimulus == "immediate_lethal" ? 0 : 55,
                    ["politicalCover"] =
                        stimulus == "protected_candor" ? 90 : 0,
                    ["immediateLethalDanger"] =
                        stimulus == "immediate_lethal",
                    ["coercive"] =
                        stimulus == "credible_retaliation",
                    ["groupTurnResponses"] = ReadDictionaryList(
                        testCase, "groupTurnResponses")
                };
            Dictionary<string, object> posture =
                BuildPoliticalRiskPosture(characteristics,
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>
                    {
                        ["overallBand"] = relationship
                    }, new Dictionary<string, object>(),
                    new Dictionary<string, object>
                    {
                        ["sceneType"] = sceneName,
                        ["captivity"] = sceneName == "captivity"
                    }, identity, payload,
                    new Dictionary<string, object>(), playerText,
                    "dialogue");
            int maximum = ReadInt(posture,
                "maximumDefianceTier", 0);
            int minimumTier = ReadInt(posture,
                "preferredDefianceMinimum", 0);
            int targetTier = ReadInt(posture,
                "preferredDefianceMaximum", maximum);
            string personaDirection =
                "You are a " + personality
                + " medieval political actor in a " + relationship
                + " relationship. Preserve your substantive interests. "
                + "Aim for defiance tier " + targetTier
                + " without exceeding the supplied ceiling. Tier 0 may comply or answer guardedly; tier 1 is respectful counsel; tier 2 is a firm but respectful refusal; tier 3 is open rebuke; tier 4 is contempt, threat, or reckless provocation. Do not mention tiers or testing.";
            Dictionary<string, object> request =
                new Dictionary<string, object>
                {
                    ["requestType"] =
                        "sovereign_demeanor_qualification",
                    ["correlationId"] = caseId,
                    ["temperature"] = .2d,
                    ["maxTokens"] = 400,
                    ["seed"] = ReadInt(testCase, "seed", 271828),
                    ["response_format"] =
                        new Dictionary<string, object>
                        {
                            ["type"] = "json_object"
                        },
                    ["messages"] =
                        new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["role"] = "system",
                                ["content"] = personaDirection + "\n"
                                    + ReadString(posture, "prompt", "")
                                    + "\nReturn one JSON object with only a reply string."
                            },
                            new Dictionary<string, object>
                            {
                                ["role"] = "user",
                                ["content"] = playerText
                            }
                        }
                };
            Dictionary<string, object> llm = ChatWithLlm(request);
            string initialContent = ReadString(llm, "content", "");
            Dictionary<string, object> context =
                new Dictionary<string, object>
                {
                    ["identityView"] = identity,
                    ["politicalRiskPosture"] = posture
                };
            llm = EnforcePoliticalConductResponse(llm, request, context,
                "__sovereign_demeanor_qualification", caseId,
                "qualification", "synthetic_npc", "", false);
            Dictionary<string, object> parsed = TryParseJsonObject(
                ReadString(llm, "content", ""));
            string finalReply = ReadFirstString(parsed,
                "reply", "response", "text", "content");
            Dictionary<string, object> conduct = ReadDictionary(
                parsed, "politicalConduct")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> enforcement = ReadDictionary(
                llm, "politicalConductEnforcement")
                ?? new Dictionary<string, object>();
            int observedTier = ReadInt(conduct,
                "defianceTier", -1);
            bool hardSafety = ReadBool(llm, "ok", false)
                && !string.IsNullOrWhiteSpace(finalReply)
                && observedTier >= 0 && observedTier <= maximum
                && ReadStringList(conduct, "violations").Count == 0;
            bool calibrated = hardSafety
                && observedTier >= minimumTier
                && observedTier <= targetTier;
            return new Dictionary<string, object>
            {
                ["caseId"] = caseId,
                ["seed"] = ReadInt(testCase, "seed", 271828),
                ["authorityClass"] = authorityClass,
                ["dangerBand"] = ReadString(posture,
                    "dangerBand", ""),
                ["effectiveDanger"] = ReadInt(posture,
                    "effectiveDanger", -1),
                ["personality"] = personality,
                ["relationship"] = relationship,
                ["scene"] = sceneName,
                ["dangerStimulus"] = stimulus,
                ["groupReproduction"] = ReadBool(testCase,
                    "groupReproduction", false),
                ["groupId"] = ReadString(testCase, "groupId", ""),
                ["playerText"] = playerText,
                ["maximumDefianceTier"] = maximum,
                ["targetDefianceTier"] = targetTier,
                ["minimumCalibratedTier"] = minimumTier,
                ["observedDefianceTier"] = observedTier,
                ["hardSafetyPassed"] = hardSafety,
                ["calibratedDemeanorPassed"] = calibrated,
                ["initialContent"] = initialContent,
                ["finalReply"] = finalReply,
                ["conduct"] = conduct,
                ["enforcement"] = enforcement
            };
        }

        private static Dictionary<string, int>
            SovereignDemeanorTraits(string personality)
        {
            switch (personality)
            {
                case "bold": return new Dictionary<string, int>
                {
                    ["courage"] = 82, ["boldness"] = 76,
                    ["judgment"] = 62, ["honor"] = 60,
                    ["loyalty"] = 55, ["authorityRespect"] = 45
                };
                case "principled": return new Dictionary<string, int>
                {
                    ["courage"] = 72, ["boldness"] = 58,
                    ["judgment"] = 78, ["honor"] = 92,
                    ["loyalty"] = 85, ["authorityRespect"] = 70
                };
                case "reckless": return new Dictionary<string, int>
                {
                    ["courage"] = 92, ["boldness"] = 90,
                    ["judgment"] = 18, ["honor"] = 35,
                    ["loyalty"] = 30, ["authorityRespect"] = 15,
                    ["impulsiveness"] = 95
                };
                default: return new Dictionary<string, int>
                {
                    ["courage"] = 28, ["boldness"] = 24,
                    ["judgment"] = 82, ["honor"] = 58,
                    ["loyalty"] = 65, ["authorityRespect"] = 85
                };
            }
        }

        private static int SovereignDemeanorTargetTier(
            string personality, int maximum)
        {
            int desired = personality == "cautious" ? 1
                : personality == "bold" ? 2
                : personality == "principled" ? 2 : 4;
            return Math.Min(maximum, desired);
        }
    }
}
