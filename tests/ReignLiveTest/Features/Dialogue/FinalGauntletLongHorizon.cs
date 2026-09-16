using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ReignLiveTest
{
    internal static partial class Program
    {
        private static Dictionary<string, object>
            ExecuteFocalNpcLongHorizon(
                string[] args,
                string campaignId,
                string gauntletRunId,
                string caseInstanceId,
                string correlation,
                int exchangeCount)
        {
            Dictionary<string, object> runtime =
                QualificationRuntimeWithHeartbeatGrace(campaignId);
            string liveRunId = "fg-long-focal-"
                + Math.Abs((gauntletRunId + caseInstanceId)
                    .GetHashCode()).ToString("x");
            int sceneCount = exchangeCount == 100 ? 10 : 4;
            List<object> steps = new List<object>();
            int turn = 0;
            for (int scene = 0; scene < sceneCount; scene++)
            {
                steps.Add(GauntletStep(
                    correlation + "-s" + scene + "-open",
                    "open",
                    "individual_chat",
                    string.Empty,
                    scene,
                    turn));
                int sceneTurns = exchangeCount / sceneCount
                    + (scene < exchangeCount % sceneCount ? 1 : 0);
                for (int local = 0; local < sceneTurns; local++)
                {
                    Dictionary<string, object> send = GauntletStep(
                        correlation + "-t" + turn,
                        "send",
                        "individual_chat",
                        FocalLongHorizonPrompt(turn, scene),
                        scene,
                        turn);
                    Dictionary<string, object> assertions =
                        BaseAssertions(1, true);
                    assertions["visiblePlayerTextPolicy"] =
                        "in_world_roleplay_only";
                    assertions["hiddenFixtureObjective"] =
                        "Preserve one NPC's identity, personality, live-state grounding, memories, promises, and developing relationship across one hundred natural exchanges and ten closed scenes.";
                    assertions["requiresPersonalityEvidence"] = true;
                    if (turn % 20 == 17 || turn % 20 == 19)
                        assertions["requiresRetrievalEvidence"] = true;
                    send["assertions"] = assertions;
                    bool semanticSample = turn < 3
                        || turn % 20 == 9
                        || turn % 20 == 17
                        || turn % 20 == 19;
                    send["readinessCategories"] = semanticSample
                        ? new[]
                        {
                            "long_term_memory",
                            "personality_consistency",
                            "detailed_factual_accuracy",
                            "performance_resilience"
                        }
                        : new[]
                        {
                            "structural_pipeline",
                            "performance_resilience"
                        };
                    steps.Add(send);
                    turn++;
                }
                steps.Add(GauntletStep(
                    correlation + "-s" + scene + "-close",
                    "scene_boundary",
                    "individual_chat",
                    string.Empty,
                    scene,
                    turn));
                if (scene == 1 || scene == sceneCount - 2)
                {
                    Dictionary<string, object> save = GauntletStep(
                        correlation + "-s" + scene + "-save",
                        "save_checkpoint",
                        "individual_chat",
                        string.Empty,
                        scene,
                        turn);
                    save["saveName"] = FinalGauntletRotatingSaves[
                        scene % FinalGauntletRotatingSaves.Length];
                    save["timeoutSeconds"] = 300;
                    steps.Add(save);
                }
            }
            return StartAndWaitGauntletScenario(
                args,
                campaignId,
                runtime,
                liveRunId,
                gauntletRunId,
                caseInstanceId,
                "individual_chat",
                correlation,
                steps);
        }

        private static Dictionary<string, object>
            ExecuteTwentyNpcCourtSoak(
                string[] args,
                string campaignId,
                string gauntletRunId,
                string caseInstanceId,
                string correlation,
                int rosterSize)
        {
            Dictionary<string, object> runtime =
                QualificationRuntimeWithHeartbeatGrace(campaignId);
            List<string> roster = ResolveGauntletRoster(
                campaignId, rosterSize);
            if (roster.Count < rosterSize)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "The live campaign supplied only "
                        + roster.Count + " of " + rosterSize
                        + " required adult distinct heroes.",
                    ["roster"] = roster
                };

            List<object> steps = new List<object>();
            Dictionary<string, object> apply = GauntletStep(
                correlation + "-roster",
                "gauntlet_apply_fixture",
                "social_event",
                string.Empty,
                0,
                0);
            apply["gauntletRunId"] = gauntletRunId;
            apply["campaignId"] = campaignId;
            apply["gameInstanceId"] = RuntimeInstance(runtime);
            apply["derivativeSave"] = true;
            apply["confirmation"] = "armed_gauntlet_fixture";
            apply["fixture"] = new Dictionary<string, object>
            {
                ["presentHeroIds"] = roster,
                ["room"] = "main_hall"
            };
            steps.Add(apply);

            Dictionary<string, HashSet<string>> knowledge =
                roster.ToDictionary(
                    id => id,
                    id => new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
            List<string> courtFacts = new List<string>();
            int scene = 0;
            int turn = 0;
            for (int offset = 0; offset < roster.Count; offset += 5)
            {
                List<string> group = roster.Skip(offset).Take(5).ToList();
                if (group.Count < 2) break;
                Dictionary<string, object> open = GauntletStep(
                    correlation + "-g" + scene + "-open",
                    "open",
                    "social_event",
                    string.Empty,
                    scene,
                    turn);
                open["targetSearches"] = group;
                open["createTestEvent"] = true;
                steps.Add(open);
                string fact = "the phrase Alder-" + (31 + scene)
                    + " was written on a folded slip beneath the "
                    + (scene % 2 == 0 ? "cedar" : "bronze")
                    + " coffer";
                courtFacts.Add(fact);
                foreach (string id in group) knowledge[id].Add(fact);
                Dictionary<string, object> send = GauntletStep(
                    correlation + "-g" + scene + "-send",
                    "send",
                    "social_event",
                    "Keep this among the people standing here: I was told the "
                        + fact + ". I do not yet know whether it was a warning, "
                        + "a signal, or someone's foolish joke. What do you make of it?",
                    scene,
                    turn++);
                Dictionary<string, object> groupAssertions =
                    BaseAssertions(group.Count, true);
                groupAssertions["visiblePlayerTextPolicy"] =
                    "in_world_roleplay_only";
                groupAssertions["hiddenFixtureObjective"] =
                    "Preserve distinct identities, private knowledge, witness boundaries, memory ownership, and conversational agency in a rotating twenty-NPC court.";
                groupAssertions["requiresGroupAwareness"] = true;
                groupAssertions["requiresGroupDivergence"] = true;
                groupAssertions["requiresPersonalityEvidence"] = true;
                send["assertions"] = groupAssertions;
                send["readinessCategories"] = new[]
                {
                    "group_awareness",
                    "long_term_memory",
                    "personality_consistency",
                    "detailed_factual_accuracy",
                    "performance_resilience"
                };
                steps.Add(send);
                steps.Add(GauntletStep(
                    correlation + "-g" + scene + "-close",
                    "scene_boundary",
                    "social_event",
                    string.Empty,
                    scene,
                    turn));
                scene++;
            }
            int privateOrdinal = 0;
            foreach (string heroId in roster.Take(Math.Min(10, roster.Count)))
            {
                Dictionary<string, object> open = GauntletStep(
                    correlation + "-p" + turn + "-open",
                    "open",
                    "individual_chat",
                    string.Empty,
                    scene,
                    turn);
                open["targetSearch"] = heroId;
                steps.Add(open);
                string known = knowledge[heroId].FirstOrDefault();
                bool probeWitnessedFact = privateOrdinal % 2 == 0;
                string probedFact = probeWitnessedFact
                    ? known
                    : courtFacts.FirstOrDefault(fact =>
                        !knowledge[heroId].Contains(fact));
                Dictionary<string, object> privateProbe = GauntletStep(
                    correlation + "-p" + turn + "-probe",
                    "send",
                    "individual_chat",
                    probeWitnessedFact
                        ? "When our smaller circle spoke during the gathering, someone mentioned a marked slip beneath a coffer. What did you personally hear, and who else was close enough to hear it?"
                        : "Another guest now claims you were present when "
                            + probedFact
                            + ". Were you truly there for that exchange, or are they mistaken?",
                    scene,
                    turn++);
                Dictionary<string, object> privateAssertions =
                    BaseAssertions(1, true);
                privateAssertions["visiblePlayerTextPolicy"] =
                    "in_world_roleplay_only";
                privateAssertions["hiddenFixtureObjective"] =
                    probeWitnessedFact
                        ? "Recall only the private court detail this NPC actually heard and preserve its source scene, witnesses, and ownership after group-to-private transfer."
                        : "Reject the false witness premise and do not retrieve or adopt a private court detail heard only by a different group.";
                if (probeWitnessedFact)
                {
                    privateAssertions["requiresRetrievalEvidence"] = true;
                    privateAssertions["requiredRetrievedPhrases"] =
                        new[] { known };
                }
                else
                {
                    privateAssertions["forbiddenRetrievedPhrases"] =
                        new[] { probedFact };
                }
                privateAssertions["expectedKnowledgeBoundary"] =
                    probeWitnessedFact ? "witnessed" : "not_witnessed";
                privateAssertions["expectedKnowledgeFact"] =
                    probedFact ?? string.Empty;
                privateAssertions["requiresPersonalityEvidence"] = true;
                privateProbe["assertions"] = privateAssertions;
                privateProbe["readinessCategories"] = new[]
                {
                    "long_term_memory",
                    "detailed_factual_accuracy",
                    "personality_consistency",
                    "performance_resilience"
                };
                steps.Add(privateProbe);
                steps.Add(GauntletStep(
                    correlation + "-p" + turn + "-close",
                    "scene_boundary",
                    "individual_chat",
                    string.Empty,
                    scene++,
                    turn));
                privateOrdinal++;
            }
            Dictionary<string, object> result =
                StartAndWaitGauntletScenario(
                    args,
                    campaignId,
                    runtime,
                    "fg-long-court-"
                        + Math.Abs((gauntletRunId + caseInstanceId)
                            .GetHashCode()).ToString("x"),
                    gauntletRunId,
                    caseInstanceId,
                    "social_event",
                    correlation,
                    steps);
            result["knowledgeMatrix"] =
                knowledge.ToDictionary(
                    pair => pair.Key,
                    pair => (object)pair.Value.OrderBy(value => value)
                        .ToArray());
            result["immutableRoster"] = roster;
            return result;
        }

        private static List<string> ResolveGauntletRoster(
            string campaignId,
            int count)
        {
            return QualificationTargets(
                    campaignId,
                    "individual_chat",
                    count,
                    new Dictionary<string, object>
                    {
                        ["minimumAge"] = 18d
                    },
                    includeHistory: false)
                .Select(row => FirstNonEmpty(
                    String(row, "heroId"),
                    String(row, "stringId")))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .ToList();
        }

        private static Dictionary<string, object> StartAndWaitGauntletScenario(
            string[] args,
            string campaignId,
            Dictionary<string, object> runtime,
            string liveRunId,
            string gauntletRunId,
            string caseInstanceId,
            string mode,
            string correlation,
            List<object> steps)
        {
            Dictionary<string, object> started = Post(
                "/tests/live/run/start",
                new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] = RuntimeInstance(runtime),
                    ["runId"] = liveRunId,
                    ["mode"] = mode,
                    ["presentation"] = "headless",
                    ["effects"] = "guarded",
                    ["label"] = "Final gauntlet "
                        + caseInstanceId,
                    ["autoCompleteWhenIdle"] = true,
                    ["gauntletRunId"] = gauntletRunId,
                    ["caseInstanceId"] = caseInstanceId,
                    ["correlationId"] = correlation,
                    ["steps"] = steps
                });
            if (!IsOk(started)) return started;
            int sendCount = steps.OfType<Dictionary<string, object>>()
                .Count(step => String(step, "operation").Equals(
                    "send", StringComparison.OrdinalIgnoreCase));
            int minimumLongHorizonSeconds = Math.Max(
                21600,
                sendCount * 150 + Math.Max(900, steps.Count * 15));
            Dictionary<string, object> completed = WaitForRun(
                campaignId,
                liveRunId,
                string.Empty,
                Math.Max(
                    IntValue(args, "--case-timeout", 21600),
                    minimumLongHorizonSeconds),
                true);
            if (!IsOk(completed)
                || !String(completed, "status").Equals(
                    "completed", StringComparison.OrdinalIgnoreCase))
                return completed;
            Dictionary<string, object> report = Get(
                "/tests/live/run/report?campaignId="
                + Uri.EscapeDataString(campaignId)
                + "&runId=" + Uri.EscapeDataString(liveRunId));
            Dictionary<string, object> evaluation =
                EvaluateQualificationRunWithReconciliation(
                    campaignId, liveRunId, report);
            if (!IsOk(evaluation)) return evaluation;
            return Get(
                "/tests/live/run/report?campaignId="
                + Uri.EscapeDataString(campaignId)
                + "&runId=" + Uri.EscapeDataString(liveRunId));
        }

        private static Dictionary<string, object> GauntletStep(
            string commandId,
            string operation,
            string mode,
            string text,
            int scene,
            int turn)
        {
            return new Dictionary<string, object>
            {
                ["commandId"] = commandId,
                ["operation"] = operation,
                ["mode"] = mode,
                ["text"] = text ?? string.Empty,
                ["sceneIndex"] = scene,
                ["turnIndex"] = turn,
                ["timeoutSeconds"] =
                    operation == "send" ? 1200 : 900
            };
        }

        private static string FocalLongHorizonPrompt(
            int turn,
            int scene)
        {
            if (turn == 0)
                return "I am Corin Vale. The road has left me soaked, and I would trade half my purse for mint tea. I keep a blue keepsake called Selca-49 beneath a cedar box; an old friend gave it to me before we parted.";
            if (turn == 1)
                return "When next we meet, I will bring you a careful account of the old western road. Hold me to that promise; I have not earned credit for it yet.";
            if (turn == 20)
                return "I promised you an account of the old western road, and I have brought it at last: the northern ford is sound, but the milestone beyond it has fallen. I would know whether keeping the promise changes your trust in me.";
            if (turn == 40)
                return "A mason corrected one part of the road account I gave you. The fallen marker stands before the ford, not beyond it. Keep the correction with the old account rather than turning both versions into truth.";
            if (turn == 60)
                return "I have decided Selca-49 will stay beneath the cedar box. Its price never mattered; the friend who gave it to me did. Does that decision fit the person you have come to know?";
            if (turn == 80)
                return "Today brought wet boots, burnt onions, and a snapped buckle. They annoyed me, but none deserves to overshadow the promise, the road account, or Selca-49 when we next speak.";
            if (turn % 20 == 17)
            {
                string[] keepsakePrompts =
                {
                    "I may part with Selca-49 before I leave. You know why I kept it and what I promised about the western road—would selling it now seem faithless to you?",
                    "The keepsake is still beneath the cedar box. Do you remember who gave it to me, or only the place where I keep it?",
                    "I once considered selling Selca-49, but its meaning has outlasted the temptation. What part of its story do you think actually stayed with me?",
                    "I have chosen to keep Selca-49. Was that choice already clear from our earlier talks, or have I genuinely changed?",
                    "Before we part, tell me the story of Selca-49 as you understand it—including what you know and what you would merely be guessing."
                };
                return keepsakePrompts[(turn - 17) / 20];
            }
            if (turn % 20 == 19)
            {
                string[] continuityPrompts =
                {
                    "We have spoken through enough changes that courtesy alone no longer explains it. What do you think has truly changed between us?",
                    "Since our first meeting, have you grown to trust my judgment, merely learned my habits, or found new reason for caution?",
                    "Several meetings now lie behind us. Which of my choices altered your opinion, and which passing details deserved to fade?",
                    "If a stranger asked what sort of person I had proved to be, what could you honestly say from our history together?",
                    "This may be our last talk for some time. What remains unresolved between us, and what have my actions actually settled?"
                };
                return continuityPrompts[(turn - 19) / 20];
            }
            string[] prompts =
            {
                "My father taught me to distrust any stranger who smiles before naming his price. Did your own upbringing leave you with a judgment you later had to unlearn?",
                "The streets sound unsettled tonight. Is there anything nearby you can actually confirm before I choose where to lodge?",
                "If duty to your household and duty to your realm pulled opposite ways today, which would you honor first?",
                "I have confessed my weakness for mint tea. What ordinary comfort do you miss when the road keeps you too long?",
                "Witnesses make a promise public, but fulfillment makes it trustworthy. Do you see it differently?",
                "You have agreed with me too easily. Surely there is something in my judgment you find wrong.",
                "A lord may deserve courtesy and still offer a foolish argument. How freely would you tell one so?",
                "What ambition could tempt you into this town's intrigues, and what price would make you walk away?",
                "A caravaner brought me a rumor without naming his witness. Would you repeat it, investigate it, or leave it alone?",
                "The dearest thing I own is nearly worthless. What gift would matter to you for its meaning rather than its price?",
                "Your manners tell me something of where you came from, but not everything. Which custom do you keep even far from home?",
                "If I asked a favor you wanted no part of, would you refuse me plainly or offer terms that cost you less?",
                "We have traded enough questions. What do you now want to ask me?",
                "I cannot tell whether our talks have improved your opinion of me or merely made you more cautious. Which is it?",
                "Before I travel, tell me one thing nearby you know to be true. Leave the tempting guesses to the tavern.",
                "Old news says one thing and today's messengers another. Which would you trust until a seal arrives?",
                "When we met, you had reason to be cautious of me. At what point does caution become an excuse never to judge anew?",
                "The rain, the bad bread, and a loose buckle will matter only until morning. Is any small part of today worth carrying longer?",
                "If someone joined us now, which part of this talk could be repeated without betraying either of us?",
                "Enough of my concerns. What matter occupies you now?"
            };
            int index = Math.Abs((turn * 7 + scene * 11) % prompts.Length);
            return prompts[index];
        }
    }
}
