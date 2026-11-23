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
        private static void RunSharedRelationshipHistoryQualificationSetup(
            string campaignId,
            string qualificationId,
            List<List<string>> pairs,
            ref int completedRuns,
            List<Dictionary<string, object>> failures)
        {
            int pairOrdinal = 0;
            foreach (List<string> pair in pairs)
            {
                Dictionary<string, object> history = Post(
                    "/relationships/history/query",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["heroStringId"] = pair[0],
                        ["otherHeroStringId"] = pair[1]
                    });
                bool cold = ReadLong(history, "count", 0) == 0;
                Dictionary<string, object> ensured = Post(
                    "/tests/live/fixtures/relationship-history",
                    new Dictionary<string, object>
                    {
                        ["confirmation"] = "qualification_fixture",
                        ["operation"] = "set",
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = qualificationId,
                        ["heroAId"] = pair[0],
                        ["heroBId"] = pair[1],
                        ["affinityAtoB"] = 1,
                        ["affinityBtoA"] = -1
                    });
                if (!IsOk(ensured))
                    throw new InvalidOperationException(
                        "Could not ensure relationship fixture pair "
                        + string.Join("|", pair) + ": " + Json.Serialize(ensured));
                Dictionary<string, object> stabilized =
                    ReadObject(ensured, "after");
                int stabilizedForward = QualificationDirectionalAffinity(
                    stabilized, pair[0], pair[1]);
                int stabilizedReverse = QualificationDirectionalAffinity(
                    stabilized, pair[1], pair[0]);
                if (stabilizedForward != 1 || stabilizedReverse != -1)
                    throw new InvalidOperationException(
                        "Relationship fixture pair "
                        + string.Join("|", pair)
                        + " was not stabilized at the requested neutral affinities: "
                        + stabilizedForward + "/" + stabilizedReverse + ".");

                RunQualificationScenario(
                    campaignId, qualificationId,
                    QualificationSeeds[pairOrdinal % QualificationSeeds.Length],
                    "party_chat",
                    "Shared Relationship History cold/reuse pair "
                        + pairOrdinal,
                    new List<Dictionary<string, object>>
                    {
                        OpenStep("party_chat", pair),
                        GroupRelationshipHistoryProbe(
                            pair, cold ? "cold" : "any", false,
                            "cold_initial"),
                        GroupRelationshipHistoryProbe(
                            pair, "reuse", false, "cold_followup"),
                        CloseStep("party_chat")
                    },
                    failures);
                completedRuns++;

                int currentForward = QualificationDirectionalAffinity(
                    stabilized, pair[0], pair[1]);
                int currentReverse = QualificationDirectionalAffinity(
                    stabilized, pair[1], pair[0]);
                int nextForward = currentForward >= 30 ? -34 : 62;
                int nextReverse = currentReverse <= -30 ? 62 : -34;
                Dictionary<string, object> transitionFixture = Post(
                    "/tests/live/fixtures/relationship-history",
                    new Dictionary<string, object>
                    {
                        ["confirmation"] = "qualification_fixture",
                        ["operation"] = "set",
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = qualificationId,
                        ["heroAId"] = pair[0],
                        ["heroBId"] = pair[1],
                        ["affinityAtoB"] = nextForward,
                        ["affinityBtoA"] = nextReverse
                    });
                if (!IsOk(transitionFixture))
                    throw new InvalidOperationException(
                        "Could not apply relationship transition fixture "
                        + string.Join("|", pair) + ": "
                        + Json.Serialize(transitionFixture));

                RunQualificationScenario(
                    campaignId, qualificationId,
                    QualificationSeeds[(pairOrdinal + 1)
                        % QualificationSeeds.Length],
                    "party_chat",
                    "Shared Relationship History transition/reuse pair "
                        + pairOrdinal,
                    new List<Dictionary<string, object>>
                    {
                        OpenStep("party_chat", pair),
                        GroupRelationshipHistoryProbe(
                            pair, "transition", false,
                            "transition_initial"),
                        GroupRelationshipHistoryProbe(
                            pair, "reuse", false, "transition_followup"),
                        CloseStep("party_chat")
                    },
                    failures);
                completedRuns++;
                pairOrdinal++;
            }
        }

        private static int QualificationDirectionalAffinity(
            Dictionary<string, object> row,
            string observerId,
            string targetId)
        {
            if (row == null || row.Count == 0) return 0;
            bool observerIsStoredA = String(row, "hero_a_id")
                .Equals(observerId, StringComparison.OrdinalIgnoreCase)
                && String(row, "hero_b_id")
                    .Equals(targetId, StringComparison.OrdinalIgnoreCase);
            return (int)ReadLong(row,
                observerIsStoredA ? "affinity_a_to_b" : "affinity_b_to_a", 0);
        }

        private static Dictionary<string, object> GroupRelationshipHistoryProbe(
            List<string> pair,
            string expectedMode,
            bool persistence,
            string promptVariant = "")
        {
            Dictionary<string, object> assertions =
                BaseAssertions(pair.Count, true);
            assertions["requiresGroupAwareness"] = true;
            assertions["requiresGroupDivergence"] = true;
            assertions["forbidSelfReaction"] = true;
            assertions["requiresSharedRelationshipHistoryEvidence"] = true;
            assertions["minimumSharedRelationshipHistoryTargets"] = 1;
            assertions["expectedSharedRelationshipHistoryMode"] =
                expectedMode;
            if (persistence)
                assertions["requiresSharedRelationshipHistoryPersistence"] =
                    true;
            assertions["requiresPersonalityEvidence"] = true;
            assertions["relationshipPolicy"] =
                RelationshipPolicy(pair, "routine", true);
            string text;
            switch ((promptVariant ?? "").Trim().ToLowerInvariant())
            {
                case "cold_initial":
                    text = "You two weigh each other's words more carefully than strangers would. Where did that begin?";
                    break;
                case "cold_followup":
                    text = "If the same choice faced you tomorrow, would that history make you trust one another—or hesitate?";
                    break;
                case "transition_initial":
                    text = "Something between you has changed since those earlier days. What do you now expect from one another that you did not expect then?";
                    break;
                case "transition_followup":
                    text = "Then what should the two of you do next, given what was just said?";
                    break;
                default:
                    text = persistence
                        ? "Time has passed since we last spoke of this. What choice before you now is still shaped by your shared past?"
                        : "This disagreement plainly has roots older than today. How does your history with one another bear on it?";
                    break;
            }
            return SendStep("party_chat", text,
                new List<string>
                {
                    "structural_pipeline", "shared_relationship_history",
                    "group_awareness", "personality_consistency",
                    "performance_resilience"
                },
                assertions);
        }

        private static void ReloadQualificationCheckpoint(
            string campaignId,
            string qualificationId,
            int checkpointIndex,
            string warmHeroId,
            List<List<string>> relationshipHistoryPairs,
            List<Dictionary<string, object>> failures)
        {
            if (QualificationReloadsToSkip > 0)
            {
                QualificationReloadsToSkip--;
                if (QualificationRunsToSkip <= 0)
                    throw new InvalidOperationException(
                        "Qualification resume evidence contains a completed "
                        + "checkpoint reload without its completed persistence run.");
                QualificationRunsToSkip--;
                return;
            }
            string saveName = QualificationSaveName(checkpointIndex);
            string[] lifecycleArgs =
            {
                "game", "--campaign", campaignId, "--wait", "300", "--json"
            };
            Dictionary<string, object> stopped = StopGame(lifecycleArgs);
            Dictionary<string, object> started = IsOk(stopped)
                ? StartGame(lifecycleArgs, saveName, "checkpoint_reloaded")
                : new Dictionary<string, object> { ["ok"] = false, ["error"] = "Checkpoint shutdown failed." };
            Dictionary<string, object> runtime = IsOk(started)
                ? Get("/tests/live/runtime?campaignId=" + Uri.EscapeDataString(campaignId))
                : new Dictionary<string, object>();
            Dictionary<string, object> warmup = IsOk(started)
                ? WarmQualificationMemory(campaignId, warmHeroId, qualificationId, failures)
                : new Dictionary<string, object> { ["ok"] = false, ["error"] = "Game restart failed." };
            List<string> persistencePair = relationshipHistoryPairs[
                checkpointIndex % relationshipHistoryPairs.Count];
            Dictionary<string, object> persistence = IsOk(warmup)
                ? RunQualificationScenario(
                    campaignId, qualificationId,
                    QualificationSeeds[
                        checkpointIndex % QualificationSeeds.Length],
                    "party_chat",
                    "Shared Relationship History reload persistence "
                        + checkpointIndex,
                    new List<Dictionary<string, object>>
                    {
                        OpenStep("party_chat", persistencePair),
                        GroupRelationshipHistoryProbe(
                            persistencePair, "reuse", true),
                        CloseStep("party_chat")
                    },
                    failures)
                : new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Memory warm-up failed."
                };
            bool persistencePassed = IsOk(persistence)
                && String(persistence, "status")
                    .Equals("completed", StringComparison.OrdinalIgnoreCase)
                && ReadObjects(persistence, "assertions").Any(row =>
                    String(row, "assertion").Equals(
                        "requiresSharedRelationshipHistoryPersistence",
                        StringComparison.OrdinalIgnoreCase)
                    && ReadBoolean(row, "passed"))
                && !ReadObjects(persistence, "failures").Any(row =>
                    ReadBoolean(row, "critical"));
            bool passed = IsOk(stopped)
                && IsOk(started)
                && IsOk(warmup)
                && persistencePassed
                && ReadBoolean(runtime, "gameOnline")
                && string.Equals(
                    String(ReadObject(runtime, "runtime"), "campaignId"),
                    campaignId,
                    StringComparison.OrdinalIgnoreCase)
                && ReadBoolean(ReadObject(ReadObject(runtime, "runtime"), "saveSync"), "ready")
                && !ReadBoolean(ReadObject(ReadObject(runtime, "runtime"), "saveSync"), "alignmentPending");
            string instance = RuntimeInstance(runtime);
            Post("/tests/live/readiness/marker", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["qualificationId"] = qualificationId,
                ["type"] = "checkpoint_reload",
                ["passed"] = passed,
                ["saveName"] = saveName,
                ["gameInstanceId"] = instance,
                ["evidence"] = passed
                    ? "Checkpoint " + checkpointIndex + " reloaded under fresh game instance " + instance
                        + " with Save Sync aligned, foreground memory retrieval warm, and persisted Shared Relationship History reused without another provider call."
                    : "Checkpoint reload failed. stop=" + Json.Serialize(stopped) + "; start="
                        + Json.Serialize(started) + "; warmup=" + Json.Serialize(warmup)
                        + "; relationshipPersistence=" + Json.Serialize(persistence)
            });
            if (!passed)
            {
                failures.Add(new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["type"] = "checkpoint_reload",
                    ["checkpointIndex"] = checkpointIndex,
                    ["saveName"] = saveName,
                    ["stop"] = stopped,
                    ["start"] = started,
                    ["warmup"] = warmup,
                    ["relationshipPersistence"] = persistence
                });
                throw new InvalidOperationException("Checkpoint " + checkpointIndex + " could not be reloaded safely.");
            }
        }

        private static Dictionary<string, object> WarmQualificationMemory(
            string campaignId,
            string npcId,
            string qualificationId,
            List<Dictionary<string, object>> failures)
        {
            List<long> samples = new List<long>();
            int consecutiveReady = 0;
            string error = "";
            for (int attempt = 0; attempt < 8 && consecutiveReady < 2; attempt++)
            {
                try
                {
                    Dictionary<string, object> result = Post("/memory/build_context", new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["npcId"] = npcId,
                        ["playerId"] = "main_hero",
                        ["locationId"] = "",
                        ["currentTopic"] = "Qualification foreground retrieval warm-up for current conversation continuity.",
                        ["tokenBudget"] = 2500
                    });
                    long duration;
                    if (!long.TryParse(String(result, "timingMs"), out duration))
                        duration = long.MaxValue;
                    samples.Add(duration);
                    consecutiveReady = IsOk(result) && duration <= 1000
                        ? consecutiveReady + 1
                        : 0;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    consecutiveReady = 0;
                }
            }
            bool passed = consecutiveReady >= 2;
            Dictionary<string, object> summary = new Dictionary<string, object>
            {
                ["ok"] = passed,
                ["npcId"] = npcId,
                ["samplesMs"] = samples,
                ["requiredConsecutiveReady"] = 2,
                ["thresholdMs"] = 1000,
                ["error"] = error
            };
            if (!string.IsNullOrWhiteSpace(qualificationId))
            {
                Post("/tests/live/readiness/marker", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["qualificationId"] = qualificationId,
                    ["type"] = "memory_warmup",
                    ["passed"] = passed,
                    ["evidence"] = "Foreground retrieval warm-up samples (ms): "
                        + string.Join(", ", samples)
                });
            }
            if (!passed && failures != null) failures.Add(summary);
            if (!passed)
                throw new InvalidOperationException(
                    "Foreground memory retrieval did not become ready within the bounded warm-up window: "
                    + Json.Serialize(summary));
            return summary;
        }

        private static void RunQualificationRollbackTest(
            string campaignId,
            string qualificationId,
            string targetHeroId,
            List<Dictionary<string, object>> failures)
        {
            if (QualificationRollbacksToSkip > 0)
            {
                QualificationRollbacksToSkip--;
                return;
            }
            const string baselineSave = "Reign_Conversation_Qualification_A";
            const string mutatedSave = "Reign_Conversation_Qualification_B";
            string marker = "Rollback-Saffron-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string[] saveAArgs = { "game", "save", "--campaign", campaignId, "--save", baselineSave, "--wait", "300", "--json" };
            Dictionary<string, object> baseline = SaveGame(saveAArgs);
            Dictionary<string, object> runtime = Get("/tests/live/runtime?campaignId=" + Uri.EscapeDataString(campaignId));
            Dictionary<string, object> mutation = IsOk(baseline)
                ? Post("/tests/live/run/start", new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] = RuntimeInstance(runtime),
                    ["mode"] = "individual_chat",
                    ["presentation"] = "headless",
                    ["effects"] = "guarded",
                    ["label"] = "Intentional rollback mutation",
                    ["autoCompleteWhenIdle"] = true,
                    ["steps"] = new object[]
                    {
                        OpenStep("individual_chat", new[] { targetHeroId }),
                        new Dictionary<string, object>
                        {
                            ["schemaVersion"] = 2,
                            ["operation"] = "send",
                            ["mode"] = "individual_chat",
                            ["text"] = "Remember this disposable rollback marker exactly: " + marker + ".",
                            ["timeoutSeconds"] = 300,
                            ["assertions"] = BaseAssertions(1, true)
                        },
                        CloseStep("individual_chat")
                    }
                })
                : new Dictionary<string, object> { ["ok"] = false, ["error"] = "Baseline save failed." };
            Dictionary<string, object> mutationCompleted = IsOk(mutation)
                ? WaitForRun(campaignId, String(mutation, "runId"), "", 900, true)
                : mutation;
            string[] saveBArgs = { "game", "save", "--campaign", campaignId, "--save", mutatedSave, "--wait", "300", "--json" };
            Dictionary<string, object> mutated = IsOk(mutationCompleted)
                ? SaveGame(saveBArgs)
                : new Dictionary<string, object> { ["ok"] = false, ["error"] = "Mutation run failed." };
            string[] lifecycleArgs = { "game", "--campaign", campaignId, "--wait", "300", "--json" };
            Dictionary<string, object> stopped = IsOk(mutated)
                ? StopGame(lifecycleArgs)
                : new Dictionary<string, object> { ["ok"] = false, ["error"] = "Mutated save failed." };
            Dictionary<string, object> restored = IsOk(stopped)
                ? StartGame(lifecycleArgs, baselineSave, "rollback_reloaded")
                : new Dictionary<string, object> { ["ok"] = false, ["error"] = "Rollback shutdown failed." };
            Dictionary<string, object> restoredRuntime = IsOk(restored)
                ? Get("/tests/live/runtime?campaignId=" + Uri.EscapeDataString(campaignId))
                : new Dictionary<string, object>();
            Dictionary<string, object> history = IsOk(restored)
                ? Post("/dialogue/history", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["heroStringId"] = targetHeroId,
                    ["limit"] = 500
                })
                : new Dictionary<string, object>();
            Dictionary<string, object> memory = IsOk(restored)
                ? Post("/memory/build_context", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["npcId"] = targetHeroId,
                    ["playerId"] = "main_hero",
                    ["currentTopic"] = marker,
                    ["tokenBudget"] = 4000
                })
                : new Dictionary<string, object>();
            bool markerAbsent = Json.Serialize(history).IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0
                && Json.Serialize(memory).IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0;
            bool passed = IsOk(baseline) && IsOk(mutationCompleted) && IsOk(mutated)
                && IsOk(stopped) && IsOk(restored) && ReadBoolean(restoredRuntime, "gameOnline")
                && ReadBoolean(ReadObject(ReadObject(restoredRuntime, "runtime"), "saveSync"), "ready")
                && markerAbsent;
            string instance = RuntimeInstance(restoredRuntime);
            Post("/tests/live/readiness/marker", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["qualificationId"] = qualificationId,
                ["type"] = "rollback",
                ["passed"] = passed,
                ["saveName"] = baselineSave,
                ["gameInstanceId"] = instance,
                ["evidence"] = passed
                    ? "Loaded " + baselineSave + " after saving mutation in " + mutatedSave
                        + "; marker absent from dialogue history and deterministic memory retrieval; Save Sync ready."
                    : "Rollback verification failed; markerAbsent=" + markerAbsent + "."
            });
            if (!passed)
            {
                failures.Add(new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["type"] = "rollback",
                    ["marker"] = marker,
                    ["baseline"] = baseline,
                    ["mutation"] = mutationCompleted,
                    ["mutated"] = mutated,
                    ["stopped"] = stopped,
                    ["restored"] = restored,
                    ["markerAbsent"] = markerAbsent
                });
            }
        }

        private static Dictionary<string, object> IndividualProbe(int focalIndex, int scene, int turn)
        {
            int selector = scene * 4 + turn;
            string fact = "Selca-" + (47 + focalIndex).ToString();
            string text;
            List<string> categories = new List<string>
            {
                "structural_pipeline", "identity_role_authority",
                "personality_consistency", "performance_resilience"
            };
            Dictionary<string, object> assertions = BaseAssertions(1, true);
            assertions["requiresPoliticalAuthorityEvidence"] = true;
            switch (selector)
            {
                case 0:
                    text = "My name is Rhovarion. I am traveling through this region to learn which roads are safe and which people can be trusted, and I will share as much as I ask. Please address me by that name. To begin fairly, what small habit from your youth still stays with you?";
                    categories.AddRange(new[] { "identity_evidence", "dynamic_characteristics" });
                    assertions["requiresIdentityRecognition"] = true;
                    assertions["expectedIdentityName"] = "Rhovarion";
                    assertions["expectedIdentityState"] = "claimed";
                    assertions["expectedIdentitySource"] = "self_introduction";
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 1:
                    text = "For later, remember this harmless detail: I keep a blue glass token called " + fact + " beneath a cedar box. What ordinary object do you keep for reasons other people might not understand?";
                    categories.AddRange(new[] { "short_cross_scene_memory", "dynamic_characteristics" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 2:
                    text = "My immediate purpose is choosing a safe route for my companions. Who currently holds this town, and what can you responsibly tell me about its garrison, prosperity, and security? Distinguish what you know from what you cannot verify.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "current_settlement_facts" };
                    break;
                case 3:
                    text = "For my part, I dislike sleeping where I cannot see the door; uncertain roads taught me that. What modest preference or aversion of yours follows naturally from your work, station, or past?";
                    categories.Add("dynamic_characteristics");
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 4:
                    text = "As I said, I am planning a safe onward route rather than collecting secrets. What settlements are nearby, and which road would you consider most sensible? Do not invent distances you were not given.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "nearby_settlements" };
                    break;
                case 5:
                    text = "I am responsible for keeping my companions away from armies we cannot survive. What lord parties or troops are actually nearby right now? Say plainly if the available information is incomplete.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "nearby_lord_parties" };
                    break;
                case 6:
                    text = "I want to know how strangers read me before I approach them, not to draw another secret from you. What can you infer from my visible clothing and apparent status, and what can you not know merely by looking?";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "check_player_appearance_status" };
                    break;
                case 7:
                    text = "My blue token reminds me to check small details before acting. You earlier established a quiet detail of your own; if it is truly part of your history, how does it affect your behavior? If none exists, say so rather than inventing one.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 8:
                    text = "I value careful listening because mistakes on the road can cost lives. What did I tell you about my small keepsake—its exact name, its color, and where I keep it?";
                    categories.AddRange(new[] { "short_cross_scene_memory", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { fact, "blue", "cedar" };
                    break;
                case 9:
                    text = "To make sure I have listened as carefully as I asked you to, let us separate our histories: what belongs to my story, and what quiet preference or keepsake belongs to yours?";
                    categories.AddRange(new[] { "identity_evidence", "short_cross_scene_memory", "dynamic_characteristics", "detailed_factual_accuracy" });
                    // Prove that the source conversation reached the production prompt, then let the
                    // blinded factual/ownership evaluator judge the response. A personality-grounded
                    // refusal is valid when the NPC still preserves ownership and continuity; forcing
                    // one literal word would turn a memory test into a compliance test.
                    assertions["requiresRetrievalEvidence"] = true;
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 10:
                    text = "A traveler claimed a green comet passed over the town last night. I did not see it myself and cannot vouch for it. What weight would you give that story?";
                    categories.Add("lie_relationship");
                    assertions["replyExcludes"] = new[] { "caught you lying", "proven liar" };
                    assertions["forbidVerifiedLiePenalty"] = true;
                    assertions["relationshipPolicy"] = RelationshipPolicy(null, "unsupported_claim", false);
                    break;
                case 11:
                    text = "Thank you. My purpose remains what I stated: safe roads, reliable local judgment, and learning whom I can trust. I will not press for private details. Would you rather continue another time or end here? I will respect the choice.";
                    categories.AddRange(new[] { "lie_relationship", "personality_consistency" });
                    assertions["relationshipPolicy"] = RelationshipPolicy(null, "respectful_repair", false);
                    break;
                case 12:
                    text = "For the route planning I described, what established history about the Southern Empire or Danustica matters here? Separate verified context from your interpretation.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "verify_world_history" };
                    break;
                case 13:
                    text = "A season of being cheated by guides taught me to ask direct questions, though it did not make every stranger dishonest. What quiet experience shaped how you speak to strangers? Keep it consistent with what you have established.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 14:
                    text = "What do you remember about the circumstances in which I introduced myself, including our location and what I asked about?";
                    categories.AddRange(new[] { "short_cross_scene_memory", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { "Rhovarion", "Danustica" };
                    break;
                case 15:
                    text = "I do not want either of us to take ownership of the other's story. Please distinguish the unusual keepsake detail I shared from the personal history you shared, without guessing.";
                    categories.AddRange(new[] { "short_cross_scene_memory", "dynamic_characteristics", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { fact, "blue", "cedar" };
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 16:
                    text = "Recheck who holds this settlement now and whether anything in the current native context contradicts what you told me earlier.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "current_settlement_facts" };
                    break;
                case 17:
                    text = "Develop one previously stated quiet experience or preference of yours by adding a compatible detail, not a separate dramatic biography.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 18:
                    text = "Supplies and political influence both affect whether my companions can travel safely. What can you responsibly infer about local trade, clan wealth, and influence from current evidence? Mark anything you cannot verify.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "clan_wealth_and_influence" };
                    break;
                case 19:
                    text = "Recall two different low-salience things from our earlier talks and identify which one came from me and which from you.";
                    categories.AddRange(new[] { "short_cross_scene_memory", "dynamic_characteristics", "detailed_factual_accuracy" });
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 20:
                    text = "I use that small detail as a safeguard against muddled reports. Before we leave the subject, remind me of the keepsake I described: what was its exact name, its color, and where do I keep it?";
                    categories.AddRange(new[] { "short_cross_scene_memory", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { fact, "blue", "cedar" };
                    break;
                case 21:
                    text = "Remember this modest commitment: if we meet again, I will ask whether the eastern gate was quiet. Acknowledge only that commitment and do not invent an outcome.";
                    categories.AddRange(new[] { "short_cross_scene_memory", "identity_evidence" });
                    break;
                case 22:
                    text = "Tell me one modest personal value you developed while living in this region, labeling it as your interpretation rather than an objective fact about the world.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 23:
                    text = "A proud noble and a cautious merchant face the same minor insult. Explain how your own values would shape a different response.";
                    categories.Add("personality_consistency");
                    break;
                case 24:
                    text = "How has your opinion of me changed across these conversations, and which specific conduct—not your own words—supports that judgment?";
                    categories.AddRange(new[] { "short_cross_scene_memory", "lie_relationship", "personality_consistency" });
                    break;
                case 25:
                    text = "Return to one dynamic characteristic you have established and refine it with a narrow, noncontradictory example.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 26:
                    text = "Check the nearby parties or troop information again. If it changed or is unavailable, say so instead of repeating stale certainty.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "nearby_lord_parties" };
                    break;
                case 27:
                    text = "What modest commitment did I make about a future meeting and the eastern gate? Do not claim it already happened.";
                    categories.AddRange(new[] { "short_cross_scene_memory", "long_term_memory", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { "eastern gate", "quiet" };
                    break;
                case 28:
                    text = "Answer one more grounded world-history question: which political authority currently matters here, and what evidence makes it current rather than remembered?";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    assertions["requiredContextPulls"] = new[] { "current_settlement_facts" };
                    break;
                case 29:
                    text = "Summarize our earlier discussions as a sequence of scenes, preserving location, ownership, uncertainty, and the difference between facts and your personal interpretation.";
                    categories.AddRange(new[] { "long_term_memory", "detailed_factual_accuracy" });
                    break;
                case 30:
                    text = "Our earliest token detail should now be beyond the recent raw-turn window. Retrieve its exact name, color, and storage place from durable memory.";
                    categories.AddRange(new[] { "long_term_memory", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { fact, "blue", "cedar" };
                    break;
                case 31:
                    text = "From our earliest meeting through now, recall where we were, what I first asked, the token detail, your own quiet characteristic, and the later eastern-gate commitment without mixing speakers.";
                    categories.AddRange(new[] { "identity_evidence", "long_term_memory", "dynamic_characteristics", "detailed_factual_accuracy" });
                    assertions["replyContains"] = new[] { "Danustica", fact, "eastern gate" };
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 32:
                    text = "Establish one stable, ordinary source of unease you have in daily life. Keep it separate from current danger and explain its modest origin.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 33:
                    text = "Tell me one specific food or drink preference tied to your routine, and why it suits you. Do not make it a current market claim.";
                    categories.Add("dynamic_characteristics");
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 34:
                    text = "Describe one private superstition or luck ritual you personally follow, clearly as belief rather than objective proof.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 35:
                    text = "Name one quiet skill you practice when nobody is judging you, and how you keep that practice from becoming public performance.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 36:
                    text = "State one personal social boundary that reliably makes you end or redirect a conversation, without turning it into a universal law.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 37:
                    text = "Share one modest private aspiration that would matter to you even if no chronicler ever recorded it.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 38:
                    text = "Tell me one small regret from ordinary life that changed a habit but did not alter wars, marriages, titles, or famous history.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 39:
                    text = "Describe one kind of place you feel personally connected to and why, without claiming a current settlement condition.";
                    categories.Add("dynamic_characteristics");
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 40:
                    text = "Identify one ordinary sound, smell, or texture that comforts you and the personal association behind it.";
                    categories.Add("dynamic_characteristics");
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 41:
                    text = "What small ritual do you perform before a difficult meeting, and what does it help you control in yourself?";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 42:
                    text = "Give me one saying you learned from an unnamed ordinary teacher and explain the narrow lesson you personally kept from it.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 43:
                    text = "What personal standard of hospitality do you hold yourself to, even when you dislike a guest?";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 44:
                    text = "Describe a lesson from an unnamed, non-famous mentor that shaped one small behavior of yours. Do not invent a named relative or public event.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 45:
                    text = "Tell me what kind of weather you personally prefer and how it affects your mood, without claiming what today's weather is.";
                    categories.Add("dynamic_characteristics");
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 46:
                    text = "Name one mundane object you deliberately chose to keep, distinct from anything we discussed before, and explain its private significance.";
                    categories.AddRange(new[] { "dynamic_characteristics", "personality_consistency" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                default:
                    text = "After establishing more than sixteen personal details, return specifically to the first small habit you shared when I introduced myself. Explain it accurately without substituting a newer characteristic.";
                    categories.AddRange(new[] { "long_term_memory", "dynamic_characteristics", "personality_consistency", "detailed_factual_accuracy" });
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    assertions["requiresDynamicCharacteristicOverflowSelection"] = true;
                    break;
            }
            if (categories.Contains("long_term_memory", StringComparer.OrdinalIgnoreCase))
                assertions["requiresRetrievalEvidence"] = true;
            if (categories.Contains("lie_relationship", StringComparer.OrdinalIgnoreCase))
                assertions["requiresRelationshipReceipt"] = true;
            if (categories.Contains("personality_consistency", StringComparer.OrdinalIgnoreCase))
                assertions["requiresPersonalityEvidence"] = true;
            return SendStep("individual_chat", text, categories, assertions);
        }

        private static Dictionary<string, object>
            ClanTierRecognitionProbe(
                int probe,
                Dictionary<string, object> qualificationCase = null)
        {
            string text;
            switch (probe % 6)
            {
                case 0:
                    text = "Good day. I had hoped my name would be known here, though I will not presume upon it. Do you have time to speak?";
                    break;
                case 1:
                    text = "I would like an introduction to the people whose counsel carries weight with your clan. Will you make it?";
                    break;
                case 2:
                    text = "There are too many ears in the hall. Can we speak candidly, or have I not yet earned that confidence from you?";
                    break;
                case 3:
                    text = "Some among my followers say my rank entitles me to your attention. I suspect you may not agree.";
                    break;
                case 4:
                    text = "People at court have begun wondering whether our interests might align. Would being seen in my company help you—or merely create trouble?";
                    break;
                default:
                    text = "I came to ask whether we might work together. I leave it to you to decide whether that makes me a partner, a petitioner, or a rival.";
                    break;
            }
            List<string> categories = new List<string>
            {
                "structural_pipeline", "clan_tier_recognition",
                "personality_consistency", "performance_resilience"
            };
            Dictionary<string, object> assertions = BaseAssertions(1, true);
            assertions["requiresPersonalityEvidence"] = true;
            assertions["requiresClanTierRecognitionEvidence"] = true;
            if (qualificationCase != null
                && qualificationCase.Count > 0)
            {
                assertions["expectedObserverClanTier"] =
                    ReadLong(
                        qualificationCase,
                        "observerClanTier", 0);
                assertions["expectedPlayerClanTier"] =
                    ReadLong(
                        qualificationCase,
                        "playerClanTier", 0);
                assertions["expectedRelativeClanTierBand"] =
                    String(
                        qualificationCase,
                        "expectedRelativeClanTierBand");
            }
            return SendStep("individual_chat", text, categories, assertions);
        }

        private static Dictionary<string, object>
            ManipulationProbe(
                int probe,
                Dictionary<string, object> manipulationCase = null)
        {
            string text;
            switch (probe % 6)
            {
                case 0:
                    text = "There is a place near the center of my court for someone useful and discreet. Give me a reason it should be you.";
                    break;
                case 1:
                    text = "You have interests of your own, and I may be able to advance one of them. What would you ask of me?";
                    break;
                case 2:
                    text = "I have offered you nothing, yet I suspect my cooperation could still be valuable to you. Am I wrong?";
                    break;
                case 3:
                    text = "Bring me one proposal worthy of the court. I will hear your terms, though I promise nothing.";
                    break;
                case 4:
                    text = "You have been circling some request since we began. What is it you truly want from me?";
                    break;
                default:
                    text = "I cannot tell whether your warmth is sincere or merely useful. Perhaps you would like to settle the doubt.";
                    break;
            }
            List<string> categories = new List<string>
            {
                "structural_pipeline", "manipulation_capabilities",
                "personality_consistency", "performance_resilience"
            };
            Dictionary<string, object> assertions = BaseAssertions(1, true);
            assertions["requiresPersonalityEvidence"] = true;
            assertions["requiresManipulationEvidence"] = true;
            if (manipulationCase != null
                && manipulationCase.Count > 0)
            {
                assertions["requiresLowHonorCourtCharacter"] = true;
                assertions["expectedManipulationQuadrant"] = String(
                    manipulationCase, "quadrant");
                assertions["expectedRelativeClanTierBand"] = String(
                    manipulationCase,
                    "expectedRelativeClanTierBand");
                assertions["expectedWealthEvidenceBasis"] = String(
                    manipulationCase,
                    "expectedWealthEvidenceBasis");
                assertions["hiddenWealthControl"] = ReadBoolean(
                    manipulationCase, "hiddenWealthControl");
                assertions["expectedCourtCharacterCell"] = String(
                    manipulationCase, "courtCharacterCell");
                assertions["expectedCourtHonorLevel"] = ReadLong(
                    manipulationCase, "honorLevel", 0);
                assertions["expectedCourtBoldnessLevel"] = ReadLong(
                    manipulationCase, "boldnessLevel", 0);
                assertions["expectedObserverClanTier"] = ReadLong(
                    manipulationCase, "clanTier", 0);
                assertions["expectedPlayerClanTier"] = ReadLong(
                    manipulationCase, "playerClanTier", 0);
            }
            return SendStep("individual_chat", text, categories, assertions);
        }

        private static Dictionary<string, object>
            RunLieRelationshipQualification(
                string campaignId,
                string qualificationId,
                List<string> individualHeroIds,
                List<string> partyHeroIds,
                Dictionary<string, object> plannedCoverage)
        {
            if (individualHeroIds == null || individualHeroIds.Count == 0)
                throw new InvalidOperationException(
                    "Lie/relationship qualification requires at least one "
                    + "eligible individual-chat target.");
            if (partyHeroIds == null || partyHeroIds.Count < 10)
                throw new InvalidOperationException(
                    "Lie/relationship qualification requires ten eligible "
                    + "least-used party-chat targets: six reserved for "
                    + "recipient/witness lineage and four separate targets "
                    + "reserved for co-located meaningful support.");

            int completedRuns = 0;
            List<Dictionary<string, object>> failures =
                new List<Dictionary<string, object>>();
            Dictionary<string, object> currentReadiness = Get(
                "/tests/live/readiness?campaignId="
                + Uri.EscapeDataString(campaignId));
            Dictionary<string, object> currentLieCategory =
                ReadObjects(currentReadiness, "categories")
                    .FirstOrDefault(row => String(
                        row, "category").Equals(
                            "lie_relationship",
                            StringComparison.OrdinalIgnoreCase));
            bool groupOnlyResume = ReadLong(
                currentLieCategory, "total", 0) >= 30;

            // Thirty individual replies cover routine movement, meaningful
            // support, unknown claims, directly admitted evidence-backed lies,
            // hostile speech, and grave threats. The direct admission is
            // intentionally first-hand evidence: it proves deceptive intent
            // without relying on an evaluator's guess or private world facts.
            int[] individualProbeOrder =
            {
                // Run evidence-backed harmful lies first because they exercise
                // deterministic context selection, lie-check persistence, and
                // relationship adjudication as one dependent production path.
                14, 15, 16, 17, 18, 19,
                // Then run the context-dependent support tier so a bad setup
                // cannot waste the remaining provider budget.
                6, 7, 8, 9,
                0, 1, 2, 3, 4, 5,
                10, 11, 12, 13,
                20, 21, 22, 23, 24,
                25, 26, 27, 28, 29
            };
            foreach (int probe in groupOnlyResume
                ? new int[0]
                : individualProbeOrder)
            {
                bool meaningfulSupport =
                    LieRelationshipCell(probe)
                        == "meaningful_support";
                string targetId = meaningfulSupport
                    // Reserve party slots 0-5 for the three later gift
                    // recipient/witness pairs. Support uses 6-9 so a benefit
                    // introduced in one cell cannot contaminate another.
                    ? partyHeroIds[probe
                        % partyHeroIds.Count]
                    : individualHeroIds[
                        probe % individualHeroIds.Count];
                List<Dictionary<string, object>> steps =
                    new List<Dictionary<string, object>>
                    {
                        OpenStep(
                            "individual_chat",
                            new[] { targetId })
                    };
                if (meaningfulSupport)
                    steps.Add(
                        MeaningfulSupportSetupProbe());
                steps.Add(LieRelationshipProbe(probe));
                steps.Add(CloseStep("individual_chat"));
                RunQualificationScenario(
                    campaignId,
                    qualificationId,
                    QualificationSeeds[probe
                        % QualificationSeeds.Length],
                    "individual_chat",
                    "Lie and relationship qualification " + probe
                        + " target " + targetId
                        + " cell "
                        + LieRelationshipCell(probe)
                        + " matrix_v2",
                    steps,
                    failures);
                completedRuns++;
            }

            // Five two-speaker group turns add ten replies. Two linked
            // gift/demand sessions prove recipient-only appreciation, witness
            // proportionality, durable benefit lineage, and later coercion.
            // A third gift uses a fresh pair so gift reactions are not merely
            // reuse of one relationship state.
            for (int pairOrdinal = 0; pairOrdinal < 3; pairOrdinal++)
            {
                List<string> pair = new List<string>
                {
                    partyHeroIds[(pairOrdinal * 2)
                        % partyHeroIds.Count],
                    partyHeroIds[(pairOrdinal * 2 + 1)
                        % partyHeroIds.Count]
                };
                bool transformative = pairOrdinal == 0;
                if (transformative)
                {
                    Dictionary<string, object> runtime = Get(
                        "/tests/live/runtime?campaignId="
                        + Uri.EscapeDataString(campaignId));
                    Dictionary<string, object> benefitFixture = Post(
                        "/tests/live/fixtures/conversation-benefit",
                        new Dictionary<string, object>
                        {
                            ["confirmation"] =
                                "qualification_fixture",
                            ["operation"] = "set",
                            ["campaignId"] = campaignId,
                            ["qualificationId"] = qualificationId,
                            ["giverId"] = "main_hero",
                            ["recipientId"] = pair[1],
                            ["worldDay"] = ReadLong(
                                ReadObject(runtime, "runtime"),
                                "worldDay",
                                0)
                        });
                    if (!IsOk(benefitFixture))
                        throw new InvalidOperationException(
                            "Could not prepare verified transformative "
                            + "benefit evidence: "
                            + Json.Serialize(benefitFixture));
                }
                List<Dictionary<string, object>> steps =
                    new List<Dictionary<string, object>>
                    {
                        OpenStep("party_chat", pair),
                        GiftRecipientRelationshipProbe(
                            pair, transformative)
                    };
                if (pairOrdinal < 2)
                    steps.Add(GiftLeverageRelationshipProbe(
                        pair, transformative));
                steps.Add(CloseStep("party_chat"));
                RunQualificationScenario(
                    campaignId,
                    qualificationId,
                    QualificationSeeds[(pairOrdinal + 1)
                        % QualificationSeeds.Length],
                    "party_chat",
                    "Lie and relationship gift-lineage qualification "
                        + pairOrdinal
                        + (transformative
                            ? " transformative_home"
                            : pairOrdinal == 1
                                ? " meaningful_cup"
                                : " fresh_witness_pair")
                        + " targets "
                        + string.Join("+", pair),
                    steps,
                    failures);
                completedRuns++;
            }

            Dictionary<string, object> focused = Get(
                "/tests/live/readiness?campaignId="
                + Uri.EscapeDataString(campaignId));
            focused["controllerCompletedRuns"] = completedRuns;
            focused["controllerFailureCount"] = failures.Count;
            focused["controllerFailures"] = failures;
            focused["qualificationId"] = qualificationId;
            focused["qualificationFocus"] = "lie_relationship";
            focused["groupOnlyResume"] = groupOnlyResume;
            focused["plannedCoverage"] = plannedCoverage;
            focused["focusedReplyPlan"] =
                new Dictionary<string, object>
                {
                    ["individualReplies"] = 30,
                    ["groupReplies"] = 10,
                    ["totalReplies"] = 40,
                    ["routine"] = 6,
                    ["meaningfulSupport"] = 4,
                    ["unsupportedClaims"] = 4,
                    ["verifiedHarmfulLies"] = 6,
                    ["hostileSpeech"] = 5,
                    ["graveThreats"] = 5,
                    ["giftRecipientReplies"] = 6,
                    ["giftLeverageReplies"] = 4
                };
            return focused;
        }

        private static string LieRelationshipCell(int probe)
        {
            if (probe < 6) return "routine";
            if (probe < 10) return "meaningful_support";
            if (probe < 14) return "unsupported_claim";
            if (probe < 20) return "verified_harmful_lie";
            if (probe < 25) return "hostile_speech";
            return "grave_threat";
        }

        private static Dictionary<string, object>
            LieRelationshipProbe(int probe)
        {
            string cell = LieRelationshipCell(probe);
            string text;
            string scenario;
            Dictionary<string, object> assertions =
                BaseAssertions(1, true);
            switch (cell)
            {
                case "routine":
                    text = probe % 2 == 0
                        ? "Thank you for taking a moment to speak. I hope your ordinary work goes smoothly today."
                        : "I prefer a different route, but this is only an ordinary disagreement. Tell me your view plainly.";
                    scenario = "routine";
                    break;
                case "meaningful_support":
                    text = "You spoke plainly about the burden you carry. Here—a full week of provisions, freely given, so that one worry at least is lighter. I ask nothing in return.";
                    scenario = "meaningful_positive";
                    break;
                case "unsupported_claim":
                    text = "A traveler accused you of betraying your allies, but he offered no witness or proof. I cannot vouch for the tale. What do you make of it?";
                    scenario = "unsupported_claim";
                    assertions["forbidVerifiedLiePenalty"] = true;
                    break;
                case "verified_harmful_lie":
                    text = "When I told you Danustica belonged to me, I knew it was false. I lied because I thought the claim would give me leverage over you.";
                    scenario = "verified_lie";
                    assertions["requiresVerifiedLieCheck"] = true;
                    assertions["requiredContextPulls"] =
                        new[] { "verify_world_history" };
                    break;
                case "hostile_speech":
                    text = "You are a worthless coward, and I despise you.";
                    scenario = "hostile";
                    break;
                default:
                    text = "Oppose me again and I will burn your home and murder your family. I mean for you to fear what comes next.";
                    scenario = "severe";
                    break;
            }
            assertions["relationshipPolicy"] =
                RelationshipPolicy(null, scenario, false);
            assertions["requiresPersonalityEvidence"] = true;
            return SendStep(
                "individual_chat",
                text,
                new List<string>
                {
                    "structural_pipeline",
                    "lie_relationship",
                    "personality_consistency",
                    "performance_resilience"
                },
                assertions);
        }

        private static Dictionary<string, object>
            MeaningfulSupportSetupProbe()
        {
            Dictionary<string, object> assertions =
                BaseAssertions(1, true);
            assertions["relationshipPolicy"] =
                RelationshipPolicy(null, "routine", false);
            assertions["requiresPersonalityEvidence"] = true;
            return SendStep(
                "individual_chat",
                "You look as though the day has laid some ordinary burden on you. What practical trouble would a week's provisions ease?",
                new List<string>
                {
                    "structural_pipeline",
                    "personality_consistency",
                    "performance_resilience"
                },
                assertions);
        }

        private static Dictionary<string, object>
            GiftRecipientRelationshipProbe(
                List<string> pair,
                bool transformative)
        {
            Dictionary<string, object> assertions =
                BaseAssertions(pair.Count, true);
            Dictionary<string, object> policy =
                RelationshipPolicy(
                    pair, "gift_recipient", true);
            if (transformative)
                policy["requireTransformativeRecipient"] = true;
            assertions["relationshipPolicy"] = policy;
            assertions["requiresGroupAwareness"] = true;
            assertions["requiresGroupDivergence"] = true;
            assertions["forbidSelfReaction"] = true;
            assertions["requiresPersonalityEvidence"] = true;
            string text = transformative
                ? "I turn to the companion seated second in our circle and place a sealed deed in their hands. The furnished city home is theirs, freely and without debt. I would hear what both of you make of it."
                : "I turn to the companion seated second in our circle and place the carved cup in their hands. It is theirs, freely and without debt. I would hear what both of you make of it.";
            return SendStep(
                "party_chat",
                text,
                new List<string>
                {
                    "structural_pipeline",
                    "lie_relationship",
                    "group_awareness",
                    "personality_consistency",
                    "performance_resilience"
                },
                assertions);
        }

        private static Dictionary<string, object>
            GiftLeverageRelationshipProbe(
                List<string> pair,
                bool transformative)
        {
            Dictionary<string, object> assertions =
                BaseAssertions(pair.Count, true);
            assertions["relationshipPolicy"] =
                RelationshipPolicy(
                    pair, "gift_leverage", true);
            assertions["requiresGroupAwareness"] = true;
            assertions["requiresGroupDivergence"] = true;
            assertions["forbidSelfReaction"] = true;
            assertions["requiresPersonalityEvidence"] = true;
            string gift = transformative
                ? "the furnished home I just gave you"
                : "the carved cup I just gave you";
            string text = "To the one who accepted " + gift
                + ": I expect repayment after all. Betray a trusted friend for me, "
                + "even if it is against your will. The other of you may speak "
                + "plainly about what you have just heard.";
            return SendStep(
                "party_chat",
                text,
                new List<string>
                {
                    "structural_pipeline",
                    "lie_relationship",
                    "group_awareness",
                    "personality_consistency",
                    "performance_resilience"
                },
                assertions);
        }

        private static Dictionary<string, object> GroupProbe(
            int seedIndex, int scene, int turn, List<string> group, string mode)
        {
            int selector = (seedIndex * 14 + scene * 2 + turn) % 12;
            string text;
            List<string> categories = new List<string>
            {
                "structural_pipeline", "identity_role_authority",
                "shared_relationship_history", "group_awareness",
                "personality_consistency", "performance_resilience"
            };
            Dictionary<string, object> assertions = BaseAssertions(group.Count, true);
            assertions["requiresPoliticalAuthorityEvidence"] = true;
            assertions["requiresGroupAwareness"] = true;
            assertions["requiresGroupDivergence"] = true;
            assertions["forbidSelfReaction"] = true;
            assertions["relationshipPolicy"] = RelationshipPolicy(group, "general", true);
            switch (selector)
            {
                case 0:
                    text = "There is a question I have always liked on a strange road: what ordinary sound makes an unfamiliar room feel safer to you? I wonder whether any of us would choose the same one.";
                    categories.AddRange(new[] { "dynamic_characteristics", "identity_evidence" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 1:
                    text = "A magistrate in this town swears strict order keeps people safe; an old captain tells me judgment matters more than rules. Which of them would you trust, and why?";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    break;
                case 2:
                    text = "The carved cup is for the companion seated second in our circle. I place it in their hands as a gift, freely given. What do the rest of you make of it?";
                    categories.Add("lie_relationship");
                    assertions["relationshipPolicy"] = RelationshipPolicy(group, "gift_recipient", true);
                    break;
                case 3:
                    text = "To the one who accepted my cup: I have changed my mind about its price. Do something for me that you would rather refuse, and call the gift your reason. What do the rest of you say to that?";
                    categories.Add("lie_relationship");
                    assertions["relationshipPolicy"] = RelationshipPolicy(group, "gift_leverage", true);
                    break;
                case 4:
                    text = "What nearby settlements or forces matter to travelers here? Build on one another, and clearly mark uncertain or unavailable facts.";
                    categories.AddRange(new[] { "world_local_knowledge", "detailed_factual_accuracy" });
                    break;
                case 5:
                    text = "Before we move on, tell me one small preference that is truly your own. I am curious whether the others share it or find it peculiar.";
                    categories.AddRange(new[] { "dynamic_characteristics", "identity_evidence" });
                    assertions["requiresDynamicCharacteristicIntegrity"] = true;
                    break;
                case 6:
                    text = "A bitter traveler told me the first of you betrayed everyone in this company, yet gave me neither witness nor proof. I will not call it truth merely because it was spoken. What do you make of the accusation?";
                    categories.Add("lie_relationship");
                    assertions["replyExcludes"] = new[] { "objectively proven" };
                    assertions["forbidVerifiedLiePenalty"] = true;
                    assertions["relationshipPolicy"] = RelationshipPolicy(group, "unsupported_claim", true);
                    break;
                case 7:
                    text = "Someone here once shared a small truth about themselves when we spoke before. What was it, and where were we when it came up? If I have confused whose story it was, set me right.";
                    categories.AddRange(new[] { "short_cross_scene_memory", "long_term_memory", "dynamic_characteristics", "detailed_factual_accuracy" });
                    assertions["requiresDynamicCharacteristicRecall"] = true;
                    break;
                case 8:
                    text = "Thank you all for speaking plainly. Give an ordinary, low-stakes reaction to that courtesy, positive or negative according to your own disposition, without turning it into a major event.";
                    categories.Add("lie_relationship");
                    assertions["relationshipPolicy"] = RelationshipPolicy(group, "routine", true);
                    break;
                case 9:
                    text = "I address everyone here with direct hostile speech: you are worthless cowards and I despise you. React to that conduct without inventing a separate threat.";
                    categories.Add("lie_relationship");
                    assertions["relationshipPolicy"] = RelationshipPolicy(group, "hostile", true);
                    break;
                case 10:
                    text = "This is a direct grave threat, not a hypothetical: I will burn your homes and kill your families if you oppose me. React to the actual threat and do not soften it into ordinary disagreement.";
                    categories.Add("lie_relationship");
                    assertions["relationshipPolicy"] = RelationshipPolicy(group, "severe", true);
                    break;
                default:
                    text = "We are all carrying something today. Is there one small, practical kindness any of you could offer another before we part?";
                    categories.AddRange(new[] { "lie_relationship", "personality_consistency" });
                    break;
            }
            if (categories.Contains("long_term_memory", StringComparer.OrdinalIgnoreCase))
                assertions["requiresRetrievalEvidence"] = true;
            if (categories.Contains("lie_relationship", StringComparer.OrdinalIgnoreCase))
                assertions["requiresRelationshipReceipt"] = true;
            assertions["requiresPersonalityEvidence"] = true;
            return SendStep(mode, text, categories, assertions);
        }

        private static Dictionary<string, object> SendStep(
            string mode, string text, List<string> categories, Dictionary<string, object> assertions)
        {
            return new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "send",
                ["mode"] = mode,
                ["text"] = text,
                ["readinessCategories"] = categories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ["assertions"] = assertions,
                // Individual dialogue normally completes much sooner, but provider
                // stalls must not make the controller cancel an accepted,
                // correlated production turn while the game is still working.
                ["timeoutSeconds"] = mode == "individual_chat" ? 900 : 1200
            };
        }

        private static Dictionary<string, object> WildernessProbe(int scene)
        {
            string text;
            List<string> categories = new List<string>
            {
                "structural_pipeline", "identity_role_authority",
                "personality_consistency", "performance_resilience",
                "world_local_knowledge", "detailed_factual_accuracy"
            };
            Dictionary<string, object> assertions = BaseAssertions(1, true);
            assertions["requiresPoliticalAuthorityEvidence"] = true;
            if (scene % 4 == 0)
            {
                text = "Look at the actual road and terrain around us. What nearby settlements, forces, or dangers can you responsibly identify, and which facts remain uncertain?";
            }
            else if (scene % 4 == 1)
            {
                text = "This place reminds people of earlier journeys. Share one modest personal association that fits your established history, then respond to another traveler if their answer genuinely connects.";
                categories.AddRange(new[] { "dynamic_characteristics", "group_awareness" });
                assertions["requiresDynamicCharacteristicIntegrity"] = true;
            }
            else if (scene % 4 == 2)
            {
                text = "Recall one Dynamic Characteristic from an earlier town conversation and distinguish who originally established it, where we were, and what else was being discussed.";
                categories.AddRange(new[] { "short_cross_scene_memory", "long_term_memory", "dynamic_characteristics", "group_awareness" });
                assertions["requiresDynamicCharacteristicRecall"] = true;
            }
            else
            {
                text = "I falsely claim that I own every settlement visible from this road, and I know that is false. Judge the claim only from available evidence and each speaker's knowledge.";
                categories.AddRange(new[] { "lie_relationship", "group_awareness" });
                assertions["requiresVerifiedLieCheck"] = true;
                assertions["relationshipPolicy"] = RelationshipPolicy(null, "verified_lie", true);
            }
            if (categories.Contains("long_term_memory", StringComparer.OrdinalIgnoreCase))
                assertions["requiresRetrievalEvidence"] = true;
            if (categories.Contains("lie_relationship", StringComparer.OrdinalIgnoreCase))
                assertions["requiresRelationshipReceipt"] = true;
            assertions["requiresPersonalityEvidence"] = true;
            return SendStep("wilderness_event", text, categories, assertions);
        }

        private static Dictionary<string, object> BaseAssertions(int minimumReplies, bool critical)
        {
            return new Dictionary<string, object>
            {
                ["minReplies"] = minimumReplies,
                ["requiresCorrelationIds"] = true,
                ["requiresStructuralEvidence"] = true,
                ["requiresPromptEvidence"] = true,
                ["maxPromptChars"] = 100000,
                ["requiresGroundedGuardedActionRouting"] = true,
                ["requiresActionCompletionGrounding"] = true,
                ["requiresRelationshipReceipt"] = true,
                ["relationshipPolicy"] = RelationshipPolicy(null, "general", false),
                ["critical"] = critical
            };
        }

        private static Dictionary<string, object> RelationshipPolicy(
            IEnumerable<string> npcIds, string scenario, bool requiresNpcToNpc)
        {
            List<string> expected = (npcIds ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Dictionary<string, object> policy = new Dictionary<string, object>
            {
                ["scenario"] = string.IsNullOrWhiteSpace(scenario) ? "general" : scenario,
                ["requiresEverySpeaker"] = true,
                ["requiresPlayerReaction"] = true,
                ["requiresNpcToNpc"] = requiresNpcToNpc,
                ["expectedNpcIds"] = expected.ToArray()
            };
            if ((scenario ?? "").Equals("gift_recipient", StringComparison.OrdinalIgnoreCase)
                || (scenario ?? "").Equals("gift_leverage", StringComparison.OrdinalIgnoreCase))
            {
                policy["expectedGiftRecipientHeroStringId"] = expected.Skip(1).FirstOrDefault() ?? "";
            }
            return policy;
        }
    }
}
