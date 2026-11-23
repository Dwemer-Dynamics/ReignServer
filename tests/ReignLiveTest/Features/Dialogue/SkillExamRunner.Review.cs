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
