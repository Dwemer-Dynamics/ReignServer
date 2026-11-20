using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        internal static Dictionary<string, object> DialogueHistory(Dictionary<string, object> payload)
        {
            if (ReadBool(payload, "wandererContactOnly", false)) return CharactersService.WandererContactHistory(payload);
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string heroId = ReadFirstString(payload, "heroStringId", "heroId", "characterId");
            int limit = Math.Max(1, ReadInt(payload, "limit", 60));
            if (string.IsNullOrWhiteSpace(heroId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "heroStringId is required." };
            }

            string path = CampaignFileService.CharacterFile(campaignId, heroId, "history", "dialogue.jsonl");
            List<Dictionary<string, object>> all = Program.ReadJsonLinesFromPath(path);
            bool officialMode = string.Equals(ReadString(payload, "conversationMode", ""), "ambassador_official", StringComparison.OrdinalIgnoreCase);
            if (officialMode)
            {
                string postingId = ReadString(payload, "postingId", "");
                all = all.Where(x => string.Equals(ReadString(x, "channel", ""), "ambassador_official", StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(postingId) || string.Equals(ReadString(x, "postingId", ""), postingId, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            all = DialogueService.ReadOriginalConversationHistory(campaignId, payload, heroId, "", limit, all);
            if (!officialMode)
            {
                // Attention observations are internal prompt/jealousy evidence,
                // including existing saved observations, never spoken chat turns.
                all = all.Where(line => !string.Equals(ReadString(line, "channel", ""),
                    "witnessed_player_attention", StringComparison.OrdinalIgnoreCase)).ToList();
                all = all.OrderBy(line => ReadLong(line, "ts", ReadLong(line, "timestamp", 0))).ToList();
            }
            int start = Math.Max(0, all.Count - limit);
            List<Dictionary<string, object>> lines = all.Skip(start).ToList();
            foreach (Dictionary<string, object> line in lines)
            {
                if (string.Equals(ReadString(line, "role", ""), "npc", StringComparison.OrdinalIgnoreCase))
                {
                    line["text"] = ConversationResponseService.SanitizeVisibleReply(ReadString(line, "text", ""));
                }
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["lines"] = lines
            };
        }

        internal static Dictionary<string, object> DialogueRespond(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            DialogueService.BeginSinglePassConversation(payload);
            return DialogueService.RunPlayerActionTurn(payload, () => LlmProviderService.RunCodexConversationScope(payload, () => DialogueRespondCore(payload)));
        }

        internal static Dictionary<string, object> DialogueRespondCore(Dictionary<string, object> payload)
        {
            payload = payload ?? TestDict();
            return DialogueService.RunDialogueWithRetryGate(payload, () => DialogueRespondCoreOnce(payload));
        }

        private static Dictionary<string, object> DialogueRespondCoreOnce(Dictionary<string, object> payload)
        {
            Stopwatch totalTimer = Stopwatch.StartNew();
            Stopwatch phaseTimer = new Stopwatch();
            long upsertMs = 0;
            long contextLoadMs = 0;
            long promptBuildMs = 0;
            long llmMs = 0;
            long parseMs = 0;
            long storageMs = 0;
            long constructionMs = 0;
            long npcRelationshipContextMs = 0;
            Dictionary<string, object> npcRelationshipPrompt =
                new Dictionary<string, object>();
            Dictionary<string, object> construction = new Dictionary<string, object>();
            payload = payload ?? new Dictionary<string, object>();
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", ReadString(hero, "campaignId", "default"));
            bool officialAmbassador = string.Equals(ReadString(payload, "conversationMode", ""), "ambassador_official", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadString(payload, "channel", ""), "ambassador_official", StringComparison.OrdinalIgnoreCase);
            bool rulerPetitionAudience = string.Equals(
                ReadString(payload, "conversationMode", ""),
                "ruler_petition", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadString(payload, "channel", ""),
                    "ruler_petition", StringComparison.OrdinalIgnoreCase);
            bool nobleDocketAudience = string.Equals(
                ReadString(payload, "conversationMode", ""),
                "noble_docket", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadString(payload, "channel", ""),
                    "noble_docket", StringComparison.OrdinalIgnoreCase);
            bool courtLifeAudience = string.Equals(ReadString(payload, "conversationMode", ""), "court_life", StringComparison.OrdinalIgnoreCase);
            bool privateGovernmentChannel = string.Equals(ReadString(payload, "channel", ""), "in_person", StringComparison.OrdinalIgnoreCase)
                && !officialAmbassador && !rulerPetitionAudience && !nobleDocketAudience && !courtLifeAudience;
            if (privateGovernmentChannel)
                payload["sceneContext"] = ReadString(payload, "sceneContext", "") + "\n" + GovernmentService.BuildGovernmentPrivateConversationPrompt(payload);
            string dialogueAuditRunId = ReadString(payload, "dialogueAuditRunId", "");
            if (!string.IsNullOrWhiteSpace(dialogueAuditRunId))
            {
                Dictionary<string, object> replay = DialogueService.ReadNpcDialogueAuditResponse(campaignId, correlationId);
                if (replay != null)
                {
                    replay["idempotentReplay"] = true;
                    return replay;
                }
            }
            string heroId = ReadFirstString(payload, "heroStringId", "heroId", "characterId");
            if (string.IsNullOrWhiteSpace(heroId))
            {
                heroId = CampaignFileService.CharacterIdFrom(hero);
            }

            string playerText = ReadFirstString(payload, "playerText", "text", "message");
            Dictionary<string, object> visitorPromptContext = ReadDictionary(payload, "nobleVisitorContext");
            if (visitorPromptContext != null)
                payload["sceneContext"] = ReadString(payload, "sceneContext", "") + "\n" + CourtService.BuildNobleVisitorPrompt(campaignId, heroId, visitorPromptContext);
            string playerPromptLabel = RelationshipsService.NormalizePregnancyChoiceToken(ReadFirstString(payload, "playerPromptLabel", "player_prompt_label"));
            if (string.IsNullOrWhiteSpace(heroId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "heroStringId is required." };
            }

            bool npcOpening = CharactersService.IsNpcLifeApproach(payload) || RelationshipsService.PrepareIndividualNpcOpening(campaignId, heroId, payload);
            if (string.IsNullOrWhiteSpace(playerText) && !npcOpening)
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "playerText is required." };
            }
            DialogueService.ApplyPlayerInputAudienceToPayload(payload, heroId);
            playerText = ReadString(payload, "playerText", "");
            if (officialAmbassador)
            {
                Dictionary<string, object> officialContextError = CourtService.ValidateOfficialAmbassadorContext(campaignId, payload);
                if (officialContextError != null) return officialContextError;
                payload["officialMemoryFirewall"] = true;
                payload["suppressConceptionGate"] = true;
            }
            if (!string.IsNullOrWhiteSpace(playerPromptLabel) && playerPromptLabel != "pull_out")
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Unknown playerPromptLabel." };
            }

            Dictionary<string, object> guardedPromptOverrideEvidence;
            string testDirectiveText;
            bool isTestDirective = ActionPipelineService.TryAuthorizeGuardedPromptOverride(
                campaignId, payload, out testDirectiveText,
                out guardedPromptOverrideEvidence);
            bool governmentCertificationMemoryIsolation = isTestDirective
                && string.Equals(ReadString(guardedPromptOverrideEvidence, "profile", ""),
                    "government_certification_memory_isolation",
                    StringComparison.OrdinalIgnoreCase);
            bool actionAcceptanceCooperation = isTestDirective
                && string.Equals(ReadString(guardedPromptOverrideEvidence, "profile", ""),
                    "action_acceptance_cooperation",
                    StringComparison.OrdinalIgnoreCase);
            VerificationService.SaveTestSnapshotFromPayload(campaignId, correlationId, "dialogue", "dialogue.request", payload);
            OperationsService.WriteAudit(campaignId, correlationId, "game_client", "dialogue", "dialogue.request", heroId, "", "", "started", 0, "Dialogue request received.", new Dictionary<string, object>
            {
                ["hero"] = hero,
                ["playerText"] = playerText,
                ["sceneContext"] = ReadString(payload, "sceneContext", ""),
                ["selectedContextPulls"] = ReadDictionaryList(payload, "selectedContextPulls"),
                ["contextBundles"] = ReadDictionaryList(payload, "contextBundles"),
                ["actionResolutionIndex"] = ReadDictionary(payload, "actionResolutionIndex"),
                ["guardedPromptOverride"] = guardedPromptOverrideEvidence
            });

            string promptPlayerText = CharactersService.IsNpcLifeApproach(payload) ? CharactersService.NpcLifeArrivalCue : npcOpening ? RelationshipsService.IndividualNpcOpeningCue : playerPromptLabel == "pull_out"
                ? PromptService.LoadPromptTemplate("pull_out.txt")
                : playerText;
            payload["playerPromptLabel"] = playerPromptLabel;

            phaseTimer.Restart();
            if (hero.Count > 0)
            {
                if (!hero.ContainsKey("heroStringId"))
                {
                    hero["heroStringId"] = heroId;
                }

                CharactersService.UpsertCharacterProfile(campaignId, hero);
            }
            upsertMs = phaseTimer.ElapsedMilliseconds;

            phaseTimer.Restart();
            construction = CharactersService.EnsureCharacterConstructed(campaignId, heroId, hero, "dialogue");
            constructionMs = phaseTimer.ElapsedMilliseconds;
            if (!CharactersService.IsCharacterConstructionReady(construction))
            {
                totalTimer.Stop();
                Dictionary<string, object> failedTiming = DialogueTiming(totalTimer.ElapsedMilliseconds, upsertMs, constructionMs, contextLoadMs, promptBuildMs, llmMs, 0, parseMs, storageMs);
                Dictionary<string, object> failed = CharactersService.CharacterConstructionFailureResponse("dialogue", campaignId, heroId, ReadString(hero, "name", heroId), construction, failedTiming, null);
                OperationsService.LogOperational("dialogue.character_construction_failed", failed);
                OperationsService.WriteAudit(campaignId, correlationId, "server", "dialogue", "character.construction", heroId, "", "", "failed", constructionMs, "Character construction failed before dialogue response.", new Dictionary<string, object>
                {
                    ["construction"] = construction,
                    ["timing"] = failedTiming
                });
                return failed;
            }

            phaseTimer.Restart();
            string heroName = ReadString(hero, "name", heroId);
            string playerName = ReadString(payload, "playerName", "Player");
            string sceneContext = ReadString(payload, "sceneContext", "");
            if (governmentCertificationMemoryIsolation)
            {
                sceneContext = ActionPipelineService.AppendGovernmentCertificationMemoryIsolationContext(
                    sceneContext, testDirectiveText);
            }
            else if (actionAcceptanceCooperation)
            {
                sceneContext = ActionPipelineService.AppendActionAcceptanceCooperationContext(sceneContext);
            }
            else if (isTestDirective)
            {
                sceneContext = ActionPipelineService.AppendTestDirectiveContext(sceneContext, testDirectiveText);
            }

            Dictionary<string, object> settings = SettingsService.LoadSettings();
            string requestedConversationSessionId = ReadFirstString(payload, "conversationSessionId", "sessionId", "conversationId");
            int liveHistoryLimit = Math.Max(2, Math.Min(100, ReadInt(payload, "historyLimit", ReadInt(settings, "recentRawConversationTurns", 30))));
            List<Dictionary<string, object>> priorLines = CanonicalizePromptTranscript(
                DialogueService.ReadCurrentSessionDialogueLines(campaignId, requestedConversationSessionId, liveHistoryLimit), liveHistoryLimit);
            if (governmentCertificationMemoryIsolation)
            {
                priorLines = ActionPipelineService.IsolateGovernmentCertificationTranscript(priorLines,
                    ReadInt(payload, "dialogueAuditTurnIndex", -1));
            }
            if (officialAmbassador)
            {
                List<Dictionary<string, object>> officialHistory = CourtService.ReadOfficialAmbassadorArchiveLines(campaignId, payload, Math.Max(8, liveHistoryLimit));
                priorLines = CanonicalizePromptTranscript(officialHistory.Concat(priorLines).ToList(), liveHistoryLimit);
            }
            else if (priorLines.Count == 0 && string.IsNullOrWhiteSpace(requestedConversationSessionId))
            {
                priorLines = ReadDialogueLines(campaignId, heroId, Math.Min(8, ReadInt(payload, "historyLimit", 24)));
            }
            Dictionary<string, object> identityView = CharactersService.ResolvePromptIdentity(payload, heroId, "dialogue",
                ReadFirstString(payload, "conversationSessionId", "sessionId", "conversationId"), priorLines);
            string promptPlayerName = ReadString(identityView, "usableName", "the stranger");
            payload["identityView"] = identityView;
            DialogueService.BindPromptTranscriptSources(campaignId, heroId, payload, priorLines);
            Dictionary<string, object> profile = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "profile.json"));
            Dictionary<string, object> characteristics = CharactersService.LoadCharacterStack(campaignId, heroId);
            Dictionary<string, object> state = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "state.json"));
            Dictionary<string, object> relationships = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "memory", "relationships.json"));
            Dictionary<string, object> summary = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "memory", "summary.json"));
            List<Dictionary<string, object>> memories = Program.ReadJsonLinesFromPath(CampaignFileService.CharacterFile(campaignId, heroId, "memory", "memories.jsonl"))
                .Where(CampaignFileService.DurableDialogueMemoryRowIsUsable)
                .OrderByDescending(x => ReadLong(x, "ts", 0))
                .Take(ReadInt(settings, "maxContextMemories", 12))
                .Reverse()
                .ToList();
            List<Dictionary<string, object>> selectedContextPulls = NormalizeSelectedContextPulls(ReadDictionaryList(payload, "selectedContextPulls"), Program.ContextPullIds.ToList());
            List<Dictionary<string, object>> contextBundles = ReadDictionaryList(payload, "contextBundles");
            if (officialAmbassador)
            {
                relationships = new Dictionary<string, object>();
                summary = new Dictionary<string, object>();
                memories.Clear();
                selectedContextPulls = selectedContextPulls.Where(x => !ContainsAny(ReadFirstString(x, "id", "pullId"), "relevant_memory", "relationship_history")).ToList();
                contextBundles = contextBundles.Where(x => !ContainsAny(ReadFirstString(x, "id", "pullId", "type"), "relevant_memory", "relationship_history", "private_conversation")).ToList();
            }
            else if (governmentCertificationMemoryIsolation)
            {
                // The requested conversation session remains intact so a natural
                // follow-up can respond to the immediately preceding turn. Only
                // recalled out-of-session certification material is excluded.
                state = new Dictionary<string, object>();
                relationships = new Dictionary<string, object>();
                summary = new Dictionary<string, object>();
                memories.Clear();
                selectedContextPulls = selectedContextPulls.Where(x =>
                    !ContainsAny(ReadFirstString(x, "id", "pullId"),
                        "relevant_memory", "relationship_history",
                        "private_conversation", "conversation_scene")).ToList();
                contextBundles = contextBundles.Where(x =>
                    !ContainsAny(ReadFirstString(x, "id", "pullId", "type"),
                        "relevant_memory", "relationship_history",
                        "private_conversation", "conversation_scene")).ToList();
            }
            else
            {
                SpymasterService.AppendSpymasterRelevantMemoryBundle(campaignId,
                    ReadString(payload, "timelineId", "main"), heroId,
                    selectedContextPulls, contextBundles);
            }
            Dictionary<string, object> catalog = ActionPipelineService.ActionCatalog();
            List<Dictionary<string, object>> compactActionCatalog = DialogueService.UsesStructuredActions(payload) ? new List<Dictionary<string, object>>() : ActionPipelineService.CompactActionCatalog(catalog);
            contextLoadMs = phaseTimer.ElapsedMilliseconds;
            OperationsService.WriteAudit(campaignId, correlationId, "server", "dialogue", "context.loaded", heroId, "", "", "completed", contextLoadMs, "Loaded character stack, memory, context bundles, and action catalog.", new Dictionary<string, object>
            {
                ["profile"] = profile,
                ["characteristics"] = characteristics,
                ["state"] = state,
                ["relationships"] = relationships,
                ["memorySummary"] = summary,
                ["memories"] = memories,
                ["priorLines"] = priorLines,
                ["selectedContextPulls"] = selectedContextPulls,
                ["contextBundles"] = contextBundles,
                ["actionCatalog"] = compactActionCatalog,
                ["identityView"] = identityView
            });

            phaseTimer.Restart();
            using (OperationsService.ReignTraceScope relationshipTrace = OperationsService.BeginReignSpan(
                "relationship.history.resolve",
                new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["correlationId"] = correlationId,
                    ["mode"] = "dialogue",
                    ["heroId"] = heroId
                }))
            {
                npcRelationshipPrompt = officialAmbassador
                    || governmentCertificationMemoryIsolation
                    ? new Dictionary<string, object>{{"block",""},{"targets",new List<Dictionary<string,object>>()}}
                    : RelationshipsService.BuildNpcRelationshipPromptContext(campaignId, heroId, payload);
                relationshipTrace.Set("targetCount",
                    ReadDictionaryList(npcRelationshipPrompt, "targets").Count);
            }
            npcRelationshipContextMs = phaseTimer.ElapsedMilliseconds;
            payload["precomputedNpcRelationshipPrompt"] = npcRelationshipPrompt;
            OperationsService.WriteAudit(campaignId, correlationId, "server", "dialogue",
                "relationship_history.loaded", heroId, "", "", "completed",
                npcRelationshipContextMs,
                "Resolved reusable NPC relationship history before local prompt assembly.",
                new Dictionary<string, object>
                {
                    ["npcRelationshipPrompt"] = npcRelationshipPrompt,
                    ["timing"] = NpcRelationshipTiming(
                        npcRelationshipContextMs, npcRelationshipPrompt)
                });

            phaseTimer.Restart();
            Dictionary<string, object> conversationSceneState = DialogueService.PrepareConversationSceneState(
                campaignId, payload, heroId, promptPlayerText, priorLines, ReadString(payload, "mode", "dialogue"));
            DialogueService.PromptEnvelope promptEnvelope;
            using (OperationsService.ReignTraceScope promptTrace = OperationsService.BeginReignSpan("prompt.build", new Dictionary<string, object>
            { ["campaignId"] = campaignId, ["correlationId"] = correlationId, ["mode"] = "dialogue", ["heroId"] = heroId }))
            {
                if (LlmProviderService.ActiveCodexConversationScope.Value != null) payload["codexParallelPreparation"] = new Dictionary<string, object>
                { ["consistentSnapshot"] = true, ["characterInitialized"] = true, ["requiredStateUpdatesComplete"] = true };
                promptEnvelope = DialogueService.BuildDialoguePromptEnvelope(campaignId, heroId, heroName, promptPlayerName, playerName, promptPlayerText, sceneContext, profile, characteristics, state, relationships, summary, priorLines, selectedContextPulls, contextBundles, compactActionCatalog, identityView, payload);
                promptTrace.Set("promptChars", promptEnvelope.TotalChars);
            }
            Dictionary<string, object> motiveDecision = ReadDictionary(promptEnvelope.Diagnostics, "motiveDecision") ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> messages = promptEnvelope.Messages;
            promptBuildMs = phaseTimer.ElapsedMilliseconds;
            OperationsService.WriteAudit(campaignId, correlationId, "server", "dialogue", "prompt.built", heroId, "", "", "completed", promptBuildMs, "Dialogue prompt assembled.", new Dictionary<string, object>
            {
                ["messages"] = messages,
                ["promptChars"] = promptEnvelope.TotalChars,
                ["promptEnvelope"] = promptEnvelope.Diagnostics,
                ["promptEvidence"] = DialogueService.BuildNpcDialoguePromptAuditEvidence(campaignId, payload, messages, selectedContextPulls),
                ["contextBundleCount"] = contextBundles.Count,
                ["selectedContextPullCount"] = selectedContextPulls.Count
            });

            phaseTimer.Restart();
            Dictionary<string, object> dialogueLlmRequest = BuildConversationGenerationRequest(
                settings, promptEnvelope, campaignId, correlationId, heroId, playerText, false);
            LlmProviderService.AttachCodexAuthoritativeConversationMetadata(dialogueLlmRequest, payload, campaignId, heroId, "dialogue", requestedConversationSessionId,
                identityView, profile, state, relationships, summary, conversationSceneState, memories, contextBundles, priorLines, promptPlayerText);
            DialogueService.AttachStructuredActionResponseContract(dialogueLlmRequest, payload);
            CharactersService.ConfigureNpcApproachGeneration(dialogueLlmRequest, payload);
            Dictionary<string, object> llm = DialogueService.GenerateSinglePassConversation(dialogueLlmRequest, payload, "dialogue", out bool delimitedSpeechOnly);
            if (ReadBool(ReadDictionary(llm, "conversationAgency"), "replayed", false)) return DialogueService.ConversationAgencyReplayResponse(llm, heroId);
            DialogueService.PreserveConversationVerification(payload, llm);
            LlmProviderService.RecordCodexFinalRepairEvidence(llm);
            llmMs = phaseTimer.ElapsedMilliseconds;
            if (!ReadBool(llm, "ok", false))
            {
                llm["inputRecovery"] = DialogueService.RetainUnansweredPlayerContribution(campaignId, payload, heroId,
                    FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "playerId", "mainHeroStringId"), ReadFirstString(profile, "mainHeroStringId")),
                    playerName, heroName, playerText);
                totalTimer.Stop();
                Dictionary<string, object> failedTiming = WithNpcRelationshipTiming(
                    DialogueTiming(totalTimer.ElapsedMilliseconds, upsertMs,
                        constructionMs, contextLoadMs, promptBuildMs, llmMs,
                        ReadLong(llm, "durationMs", 0), parseMs, storageMs),
                    npcRelationshipContextMs, npcRelationshipPrompt);
                OperationsService.LogOperational("dialogue.respond_failed", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["heroStringId"] = heroId,
                    ["stage"] = "llm",
                    ["timing"] = failedTiming,
                    ["llm"] = llm
                });
                OperationsService.WriteAudit(campaignId, correlationId, "server", "dialogue", "dialogue.response", heroId, "", "", "failed", totalTimer.ElapsedMilliseconds, "Dialogue response failed during LLM call.", new Dictionary<string, object>
                {
                    ["stage"] = "llm",
                    ["timing"] = failedTiming,
                    ["llm"] = llm
                });
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["stage"] = "llm",
                    ["error"] = LlmFailureForDisplay(llm),
                    ["errorCode"] = ReadInt(ReadDictionary(llm, "codexFailure"), "httpStatusCode", 0) == 429 ? "codex_rate_limited" : "conversation_generation_failed",
                    ["correlationId"] = correlationId,
                    ["conversationRecovery"] = ReadDictionary(llm, "conversationRecovery"), ["inputRecovery"] = ReadDictionary(llm, "inputRecovery"),
                    ["llm"] = llm,
                    ["timing"] = failedTiming
                };
            }

            phaseTimer.Restart();
            string content = ReadString(llm, "content", "");
            Dictionary<string, object> parsed = TryParseJsonObject(content);
            bool agencyVisibleFailure = ReadBool(ReadDictionary(llm, "conversationAgency"), "visibleFailure", false)
                || ReadBool(llm, "visibleRepairFailure", false) || ReadBool(llm, "conversationEffectsUnavailable", false);
            string reply = ReadFirstString(parsed, "reply", "response", "text", "content");
            if (string.IsNullOrWhiteSpace(reply))
            {
                reply = ConversationResponseService.ExtractVisibleReply(content);
            }

            if (string.IsNullOrWhiteSpace(reply))
            {
                reply = "[Bannerlord Reign: the model returned malformed JSON, so the visible reply was hidden. Please try again.]";
            }
            if (!DialogueService.IsSinglePassConversation(payload) && courtLifeAudience && (reply.StartsWith("[Bannerlord Reign:", StringComparison.Ordinal)
                || ReignRulerDocketRules.ContainsForbiddenNobleDocketStatistics(reply)))
                return new Dictionary<string, object> { ["ok"] = false,
                    ["error"] = "The court guest returned an invalid response. The audience can be retried.",
                    ["courtLifeInvalidReply"] = true };
            reply = ConversationResponseService.SanitizeVisibleReply(reply);
            reply = CharactersService.RemoveSpokenNumericSkillLevels(reply, out bool skillNumericRepairApplied);
            bool nobleDocketMechanicalRepairApplied = false;
            if (!DialogueService.IsSinglePassConversation(payload) && string.Equals(ReadString(payload, "conversationMode", ""),
                    "noble_docket", StringComparison.OrdinalIgnoreCase)
                && ReignRulerDocketRules.ContainsForbiddenNobleDocketStatistics(reply))
            {
                reply = "I will speak only to the merits of this dispute and to what honor requires.";
                nobleDocketMechanicalRepairApplied = true;
            }
            reply = CharactersService.SanitizePromptForIdentity(reply, "", playerName, identityView);
            reply = DialogueService.NormalizeUnknownIdentityVisibleAddress(reply, identityView);
            List<Dictionary<string, object>> recallConsistencyRepairs;
            reply = DialogueService.RepairVisibleExactRecallIdentifiers(playerText, reply, parsed, messages, out recallConsistencyRepairs);
            // NPC Lives owns the eventual generation/session-checked publication.
            // Return only grounded prose; no player turn or ordinary side effects.
            if (CharactersService.IsNpcLifeApproach(payload))
                return CharactersService.BuildNpcApproachDraft(payload, llm, parsed, reply, motiveDecision, identityView, priorLines, heroId, heroName, playerName);
            // A bounded leave reply replaces its whole draft. Do this before
            // deriving memory/state/action caches, not only at final grounding.
            if (!governmentCertificationMemoryIsolation && !agencyVisibleFailure)
                reply = DialogueService.ApplyConversationDrinking(campaignId, heroId, payload, profile, characteristics, parsed, reply, persist: false);
            Dictionary<string, object> conversationSceneResolution = TestDict("status", "pending_acceptance");

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string decisionId = DialogueService.AcceptedConversationEventId(campaignId, heroId, payload);
            string emotion = parsed == null ? "" : ReadString(parsed, "emotion", "");
            string intent = parsed == null ? "" : ReadString(parsed, "intent", "");
            string relationshipSignal = parsed == null ? "" : ReadFirstString(parsed, "relationshipSignal", "relationship_signal");
            List<Dictionary<string, object>> relationshipAssessments = RelationshipsService.NormalizeConversationRelationshipAssessments(
                parsed, payload, heroId, relationshipSignal,
                FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "playerId", "mainHeroStringId"), ReadFirstString(profile, "mainHeroStringId", "playerHeroStringId")),
                ReadString(payload, "sceneTurnId", ""));
            if (agencyVisibleFailure) relationshipAssessments.Clear();
            Dictionary<string, object> actionGate = npcOpening
                ? new Dictionary<string, object> { ["allowed"] = false, ["reason"] = "npc_approach_opening" }
                : ActionPipelineService.NormalizeActionGate(parsed == null ? null : ReadDictionary(parsed, "actionGate") ?? ReadDictionary(parsed, "action_gate"), playerText, reply);
            bool actionAcceptanceGateRepaired = ActionPipelineService.EnforceActionAcceptanceCooperationGate(
                actionAcceptanceCooperation && !delimitedSpeechOnly, actionGate, playerText, ref reply,
                DialogueService.UsesStructuredActions(payload));
            Dictionary<string, object> chancellorDecision = CourtService.NormalizeChancellorDecision(
                parsed, delimitedSpeechOnly ? null : payload, playerText, reply, correlationId);
            Dictionary<string, object> nobleDocketTurn = CourtService.NormalizeNobleDocketTurn(
                parsed, payload, reply);
            Dictionary<string, object> conceptionGate = ActionPipelineService.NormalizeConceptionGate(
                parsed == null ? null : ReadDictionary(parsed, "conceptionGate") ?? ReadDictionary(parsed, "conception_gate"),
                delimitedSpeechOnly ? "" : reply,
                playerPromptLabel == "pull_out" || ReadBool(payload, "suppressConceptionGate", false));
            string socialPlayerId = FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "playerId", "mainHeroStringId"),
                ReadFirstString(profile, "mainHeroStringId", "playerHeroStringId"));
            double socialWorldDay = ReadDouble(payload, "worldDay", 0d);
            Dictionary<string, object> socialSignalValidation =
                ActionPipelineService.ShouldPersistGovernmentCertificationConversationSideEffects(
                    governmentCertificationMemoryIsolation)
                    && !actionAcceptanceCooperation && !delimitedSpeechOnly
                    ? ReputationService.NormalizeAndPersistConversationSocialSignals(
                        campaignId, parsed, payload, heroId, socialPlayerId,
                        playerText, reply, conceptionGate, decisionId,
                        socialWorldDay, persist: false)
                    : new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["timelineId"] = ReadString(payload, "timelineId", "main"),
                        ["signals"] = new List<Dictionary<string, object>>(),
                        ["rejected"] = new List<Dictionary<string, object>>(),
                        ["suppressed"] = true,
                        ["reason"] = delimitedSpeechOnly ? "speech_only" : actionAcceptanceCooperation
                            ? "action_acceptance_cooperation"
                            : "government_certification_memory_isolation"
                    };
            Dictionary<string, object> decisionBrief = DialogueService.AugmentDecisionBriefWithMotiveEvidence(DialogueService.NormalizeDecisionBrief(parsed, intent, actionGate), motiveDecision);
            DialogueService.RemovePrivateReasoningFields(parsed);
            List<Dictionary<string, object>> memoryWrites = parsed == null ? new List<Dictionary<string, object>>() : ActionPipelineService.NormalizeMemoryWrites(parsed.ContainsKey("memoryWrites") ? parsed["memoryWrites"] : null, campaignId, heroId, ts);
            List<Dictionary<string, object>> beliefWrites = parsed == null ? new List<Dictionary<string, object>>() : ReadDictionaryList(parsed, "beliefWrites").Concat(ReadDictionaryList(parsed, "belief_writes")).ToList();
            List<Dictionary<string, object>> relationshipUpdates = parsed == null ? new List<Dictionary<string, object>>() : ReadDictionaryList(parsed, "relationshipUpdates").Concat(ReadDictionaryList(parsed, "relationship_updates")).ToList();
            List<Dictionary<string, object>> obligationWrites = parsed == null ? new List<Dictionary<string, object>>() : ReadDictionaryList(parsed, "obligationWrites").Concat(ReadDictionaryList(parsed, "obligation_writes")).ToList();
            List<Dictionary<string, object>> comprehensionWrites = parsed == null ? new List<Dictionary<string, object>>() : ReadDictionaryList(parsed, "comprehensionWrites").Concat(ReadDictionaryList(parsed, "comprehension_writes")).ToList();
            List<Dictionary<string, object>> dynamicCharacteristicWrites = CharactersService.NormalizeDynamicCharacteristicWrites(parsed, heroId, profile, payload, reply);
            if (agencyVisibleFailure || npcOpening) dynamicCharacteristicWrites.Clear();
            List<Dictionary<string, object>> roleWriteRejections = new List<Dictionary<string, object>>();
            memoryWrites = ActionPipelineService.FilterConversationRoleMisattributions(memoryWrites, payload, playerText, "memory", roleWriteRejections);
            beliefWrites = ActionPipelineService.FilterConversationRoleMisattributions(beliefWrites, payload, playerText, "belief", roleWriteRejections);
            obligationWrites = ActionPipelineService.FilterConversationRoleMisattributions(obligationWrites, payload, playerText, "obligation", roleWriteRejections);
            comprehensionWrites = ActionPipelineService.FilterConversationRoleMisattributions(comprehensionWrites, payload, playerText, "comprehension", roleWriteRejections);
            memoryWrites = DialogueService.FilterRoleplayFeedbackWrites(memoryWrites, "memory", roleWriteRejections, payload, heroId, campaignId);
            beliefWrites = DialogueService.FilterRoleplayFeedbackWrites(beliefWrites, "belief", roleWriteRejections, payload, heroId, campaignId);
            obligationWrites = DialogueService.FilterRoleplayFeedbackWrites(obligationWrites, "obligation", roleWriteRejections, payload, heroId, campaignId);
            comprehensionWrites = DialogueService.FilterRoleplayFeedbackWrites(comprehensionWrites, "comprehension", roleWriteRejections, payload, heroId, campaignId);
            Dictionary<string, object> stateUpdates = parsed == null
                ? new Dictionary<string, object>()
                : DialogueService.NormalizeSafeConversationStateUpdates(
                    ReadDictionary(parsed, "stateUpdates") ?? ReadDictionary(parsed, "state_updates"),
                    roleWriteRejections, payload, heroId, campaignId);
            Dictionary<string, object> politicalRiskPosture =
                ReadDictionary(motiveDecision, "politicalRiskPosture")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> pendingAddressConvention =
                ReadDictionary(politicalRiskPosture,
                    "pendingAddressConvention");
            if (!delimitedSpeechOnly && pendingAddressConvention != null
                && pendingAddressConvention.Count > 0)
                stateUpdates["politicalAddressConvention"] =
                    CloneDictionary(pendingAddressConvention);
            Dictionary<string, object> politicalConduct =
                ReadDictionary(parsed, "politicalConduct")
                ?? new Dictionary<string, object>();
            bool recallOnlyTurn = DialogueService.IsPureConversationRecallRequest(playerText);
            if (recallOnlyTurn)
            {
                // A recall answer is a reconstruction, not a new source of world facts. Keep the
                // verbatim turn, but do not let a mistaken reconstruction recursively become a
                // durable memory, belief, rumor, obligation, or comprehension record.
                memoryWrites.Clear();
                beliefWrites.Clear();
                obligationWrites.Clear();
                comprehensionWrites.Clear();
                dynamicCharacteristicWrites.Clear();
            }
            if (!ActionPipelineService.ShouldPersistGovernmentCertificationConversationSideEffects(
                    governmentCertificationMemoryIsolation)
                || actionAcceptanceCooperation)
            {
                // Certification conversations must behave like ordinary dialogue
                // for visible prose and accepted native actions, but one case must
                // never teach a later case that this NPC already promised, refused,
                // bargained, or adopted a new personal plan.
                memoryWrites.Clear();
                beliefWrites.Clear();
                relationshipUpdates.Clear();
                relationshipAssessments.Clear();
                obligationWrites.Clear();
                comprehensionWrites.Clear();
                dynamicCharacteristicWrites.Clear();
                stateUpdates.Clear();
            }
            List<Dictionary<string, object>> suggestedActions = npcOpening || parsed == null ? new List<Dictionary<string, object>>() : ActionPipelineService.NormalizeSuggestedActions(parsed.ContainsKey("suggestedActions") ? parsed["suggestedActions"] : null, compactActionCatalog);
            DialogueService.CaptureStructuredActionDecision(payload, parsed);
            List<Dictionary<string, object>> identityIntroductionResults = new List<Dictionary<string, object>>();
            OperationsService.WriteAudit(campaignId, correlationId, "server", "dialogue", "model.parsed", heroId, "", "", parsed == null ? "warning" : "completed", phaseTimer.ElapsedMilliseconds, parsed == null ? "Model response was not valid JSON; visible reply fallback was used." : "Dialogue model JSON parsed.", new Dictionary<string, object>
            {
                ["rawContent"] = content,
                ["parsed"] = parsed ?? new Dictionary<string, object>(),
                ["reply"] = reply,
                ["skillNumericRepairApplied"] = skillNumericRepairApplied,
                ["decisionBrief"] = decisionBrief,
                ["emotion"] = emotion,
                ["intent"] = intent,
                ["relationshipSignal"] = relationshipSignal,
                ["relationshipAssessments"] = relationshipAssessments,
                ["actionGate"] = actionGate,
                ["actionAcceptanceGateRepaired"] = actionAcceptanceGateRepaired,
                ["chancellorDecision"] = chancellorDecision,
                ["nobleDocketTurn"] = nobleDocketTurn,
                ["nobleDocketMechanicalRepairApplied"] = nobleDocketMechanicalRepairApplied,
                ["conceptionGate"] = conceptionGate,
                ["socialSignalValidation"] = socialSignalValidation,
                ["identityView"] = identityView,
                ["memoryWrites"] = memoryWrites,
                ["beliefWrites"] = beliefWrites,
                ["relationshipUpdates"] = relationshipUpdates,
                ["obligationWrites"] = obligationWrites,
                ["comprehensionWrites"] = comprehensionWrites,
                ["dynamicCharacteristicWrites"] = dynamicCharacteristicWrites,
                ["roleWriteRejections"] = roleWriteRejections,
                ["recallWriteSuppressed"] = recallOnlyTurn,
                ["recallConsistencyRepairs"] = recallConsistencyRepairs,
                ["stateUpdates"] = stateUpdates,
                ["politicalRiskPosture"] = politicalRiskPosture,
                ["politicalConduct"] = politicalConduct,
                ["politicalConductEnforcement"] = ReadDictionary(
                    llm, "politicalConductEnforcement")
                    ?? new Dictionary<string, object>(),
                ["suggestedActions"] = suggestedActions,
                ["identityIntroductionResults"] = identityIntroductionResults
                ,["conversationSceneState"] = conversationSceneState
                ,["conversationSceneResolution"] = conversationSceneResolution
            });
            List<string> testDirectiveErrors = new List<string>();
            List<Dictionary<string, object>> queuedTestDirectiveActions = new List<Dictionary<string, object>>();
            List<string> dialogueActionErrors = new List<string>();
            List<Dictionary<string, object>> queuedDialogueActions = new List<Dictionary<string, object>>();
            string actionShadowPreview = "";
            if (!delimitedSpeechOnly)
                DialogueService.ApplyAgencyRelationshipAssessment(relationshipAssessments, payload, heroId, socialPlayerId);
            if (!delimitedSpeechOnly && ActionPipelineService.ShouldRouteGuardedPromptOverrideActions(isTestDirective,
                    governmentCertificationMemoryIsolation,
                    actionAcceptanceCooperation))
            {
                memoryWrites.Clear();
                beliefWrites.Clear();
                relationshipUpdates.Clear();
                relationshipAssessments.Clear();
                obligationWrites.Clear();
                comprehensionWrites.Clear();
                dynamicCharacteristicWrites.Clear();
                stateUpdates.Clear();
                queuedTestDirectiveActions = ActionPipelineService.QueueTestDirectiveActions(campaignId, payload, hero, testDirectiveText, suggestedActions, out testDirectiveErrors);
                string statusLine = queuedTestDirectiveActions.Count > 0
                    ? "[Bannerlord Reign prompt override queued: " + string.Join(", ", queuedTestDirectiveActions.Select(x => ReadString(x, "command", "")).Where(x => !string.IsNullOrWhiteSpace(x))) + ".]"
                    : "[Bannerlord Reign prompt override did not queue an action: " + string.Join("; ", testDirectiveErrors.DefaultIfEmpty("No matching action command was found.")) + "]";
                reply = string.IsNullOrWhiteSpace(reply) || reply == "..." ? statusLine : reply + "\n\n" + statusLine;
            }
            else if (!npcOpening && !rulerPetitionAudience && !nobleDocketAudience && !courtLifeAudience)
            {
                if (!agencyVisibleFailure)
                {
                    using (GovernmentService.ReserveGovernmentPaymentOwnership(payload, parsed, playerText, reply))
                    {
                        queuedDialogueActions = ActionPipelineService.QueueAcceptedDialogueActions(campaignId, payload, hero, playerText, reply, actionGate, suggestedActions, out dialogueActionErrors);
                    }
                    if (ActionPipelineService.IsTemporaryPartyGuestLifecycleConversation(actionGate, playerText,
                            queuedDialogueActions))
                    {
                        // Temporary guest negotiations are deliberately relationship-neutral.
                        // This covers the conditional consent turns that precede the final
                        // accepted action as well as the action-queuing turn itself.
                        relationshipAssessments.Clear();
                        relationshipUpdates.Clear();
                        DialogueService.ApplyAgencyRelationshipAssessment(relationshipAssessments, payload, heroId, socialPlayerId);
                    }
                    string campaignCommandDraftReply = ReadString(payload, "campaignCommandDraftReply", "");
                    if (!string.IsNullOrWhiteSpace(campaignCommandDraftReply))
                        reply = campaignCommandDraftReply;
                    if (queuedDialogueActions.Count > 0)
                    {
                        string statusLine = ConversationResponseService.DialogueActionQueuedNoticePrefix + " " + string.Join(", ", queuedDialogueActions.Select(x => ReadString(x, "command", "")).Where(x => !string.IsNullOrWhiteSpace(x))) + ".]";
                        reply = string.IsNullOrWhiteSpace(reply) || reply == "..." ? statusLine : reply + "\n\n" + statusLine;
                    }
                }
            }

            if (WorldSimulationService.HasUnconfirmedMarriageClaim(reply, payload, hero))
            {
                memoryWrites.Clear();
                beliefWrites.Clear();
                comprehensionWrites.Clear();
                relationshipAssessments.Clear();
                relationshipUpdates.Clear();
                obligationWrites.Clear();
                dynamicCharacteristicWrites.Clear();
                stateUpdates.Clear();
            }
            reply = WorldSimulationService.FinalizeDialogueActionOutcome(reply, payload, hero, queuedDialogueActions, dialogueActionErrors);
            reply = DialogueService.RenderGroundedSceneHeading(payload, heroId, parsed, reply);
            if (!DialogueService.FinalizeConversationGrounding(payload, heroId, parsed, ref reply))
            {
                agencyVisibleFailure = true;
                memoryWrites.Clear(); beliefWrites.Clear(); comprehensionWrites.Clear(); relationshipAssessments.Clear();
                relationshipUpdates.Clear(); obligationWrites.Clear(); dynamicCharacteristicWrites.Clear(); stateUpdates.Clear();
                queuedDialogueActions.Clear(); queuedTestDirectiveActions.Clear();
            }
            // Only the final accepted prose can ground social evidence; previewing
            // a draft must never make an intimacy event durable.
            conceptionGate = ActionPipelineService.NormalizeConceptionGate(parsed == null ? null : ReadDictionary(parsed, "conceptionGate") ?? ReadDictionary(parsed, "conception_gate"),
                delimitedSpeechOnly ? "" : reply, playerPromptLabel == "pull_out" || ReadBool(payload, "suppressConceptionGate", false));
            bool publishContextEffects = !agencyVisibleFailure && !governmentCertificationMemoryIsolation && !actionAcceptanceCooperation;
            if (publishContextEffects)
                socialSignalValidation = ReputationService.NormalizeAndPersistConversationSocialSignals(campaignId, parsed, payload,
                    heroId, socialPlayerId, playerText, reply, conceptionGate, decisionId, socialWorldDay, persist: false);
            actionShadowPreview = ActionPipelineService.BuildActionShadowPreview(queuedTestDirectiveActions, queuedDialogueActions, testDirectiveErrors, dialogueActionErrors);
            Dictionary<string, object> playerLine = npcOpening ? null : DialogueLine(ts, "player", playerName, playerText);
            Dictionary<string, object> npcLine = DialogueLine(ts, "npc", heroName, reply);
            if (officialAmbassador)
            {
                Dictionary<string, object> officialContext = ReadDictionary(payload, "ambassadorContext") ?? new Dictionary<string, object>();
                foreach (Dictionary<string, object> line in new[] { playerLine, npcLine }.Where(line => line != null))
                {
                    line["channel"] = "ambassador_official";
                    line["postingId"] = ReadString(officialContext, "postingId", "");
                    line["representedKingdomId"] = ReadString(officialContext, "representedKingdomId", "");
                    line["representedRulerId"] = ReadString(officialContext, "representedRulerId", "");
                    line["officialRelay"] = true;
                }
            }
            parseMs = phaseTimer.ElapsedMilliseconds;

            phaseTimer.Restart();
            string dialoguePlayerId = socialPlayerId;
            Dictionary<string, object> socialExchangePayload = new Dictionary<string, object>(payload, StringComparer.OrdinalIgnoreCase)
            {
                ["socialSignals"] = ReadDictionaryList(socialSignalValidation, "signals")
            };
            DialogueService.CopyPlayerInputSource(payload, socialExchangePayload);
            var acceptedUnit = new DialogueService.AcceptedConversationUnit {
                Campaign = campaignId, Hero = heroId, Player = dialoguePlayerId,
                PlayerName = playerName, HeroName = heroName,
                Location = FirstNonEmpty(ReadFirstString(payload, "locationId", "currentSettlementId", "settlementId"), ReadFirstString(profile, "currentSettlementId", "settlementId")),
                EventId = decisionId, EventType = "dialogue_turn", PlayerText = playerText, Reply = reply, Timestamp = ts,
                Generation = DialogueService._playerActionGeneration, Payload = payload, Motive = motiveDecision, Profile = profile,
                Characteristics = characteristics, Parsed = parsed, Memories = memoryWrites, Beliefs = beliefWrites,
                Relationships = relationshipUpdates, Obligations = obligationWrites, Comprehensions = comprehensionWrites,
                Continuity = actionAcceptanceCooperation ? null : ReadDictionaryList(parsed, "continuityWrites"),
                RetrievalEligible = !recallOnlyTurn && ActionPipelineService.ShouldPersistGovernmentCertificationConversationSideEffects(governmentCertificationMemoryIsolation),
                DelimitedSpeechOnly = delimitedSpeechOnly, CommitEffects = publishContextEffects, PublishScene = publishContextEffects,
                PublishSocial = publishContextEffects && !recallOnlyTurn, PublishIntroductions = publishContextEffects,
                ViewInsertionRequested = !officialAmbassador && DialogueService.IndividualViewInsertionRequested(payload),
                SceneMode = ReadString(payload, "mode", "dialogue"), ConceptionGate = conceptionGate };
            Dictionary<string, object> conversationExchange = DialogueService.PublishAcceptedConversationUnit(acceptedUnit,
                accept => DialogueService.StoreConversationExchange(campaignId, socialExchangePayload, heroId, dialoguePlayerId,
                    playerName, heroName, playerText, reply, decisionId, ts, acceptance: accept));
            if (CharactersService.ApplyNpcLivesRelationshipOwnership(conversationExchange, relationshipAssessments, dialoguePlayerId))
                relationshipSignal = "unchanged";
            llm["conversationEffectAcceptance"] = ReadDictionary(ReadDictionary(conversationExchange, "acceptedUnit"), "effects");
            conversationSceneResolution = ReadDictionary(ReadDictionary(conversationExchange, "acceptedUnit"), "scene");
            socialSignalValidation = ReadDictionary(ReadDictionary(conversationExchange, "acceptedUnit"), "social");
            identityIntroductionResults = ReadDictionaryList(ReadDictionary(conversationExchange, "acceptedUnit"), "introductions");
            if (!delimitedSpeechOnly && !ReadBool(payload, "conversationGroundingFailed", false) && !recallOnlyTurn && ActionPipelineService.ShouldPersistGovernmentCertificationConversationSideEffects(governmentCertificationMemoryIsolation))
                DialogueService.AcceptPersonalGossip(campaignId, payload, parsed, conversationExchange, heroId, dialoguePlayerId, playerText, reply);
            string conversationSessionId = ReadString(conversationExchange, "sessionId", "");
            double dialogueWorldDay = ReadDouble(payload, "worldDay", 0d);
            Dictionary<string, object> immediateSocialIncidents = ReputationService.ProcessImmediatePlayerIntimacySignals(
                campaignId, ReadString(socialSignalValidation, "timelineId", "main"),
                conversationSessionId, ReadDictionaryList(socialSignalValidation, "signals"), dialogueWorldDay);
            string dialogueLocationId = FirstNonEmpty(ReadFirstString(payload, "locationId", "currentSettlementId", "settlementId"), ReadFirstString(profile, "currentSettlementId", "settlementId"));
            if (playerLine != null)
            {
                playerLine["sessionId"] = conversationSessionId;
                playerLine["exchangeId"] = decisionId;
                playerLine["worldDay"] = dialogueWorldDay;
                playerLine["locationId"] = dialogueLocationId;
            }
            npcLine["sessionId"] = conversationSessionId;
            npcLine["exchangeId"] = decisionId;
            npcLine["worldDay"] = dialogueWorldDay;
            npcLine["locationId"] = dialogueLocationId;
            Dictionary<string, object> ambassadorDecision = officialAmbassador && !delimitedSpeechOnly
                ? CourtService.PersistOfficialAmbassadorDecision(campaignId, payload, conversationExchange, ts)
                : new Dictionary<string, object>();
            if (officialAmbassador)
                CourtService.RecordOfficialAmbassadorExchange(campaignId, payload, heroId, dialoguePlayerId, playerLine, npcLine, conversationExchange, ts);
            if (playerLine != null) CampaignFileService.AppendJsonLineToPath(CampaignFileService.CharacterFile(campaignId, heroId, "history", "dialogue.jsonl"), playerLine);
            CampaignFileService.AppendJsonLineToPath(CampaignFileService.CharacterFile(campaignId, heroId, "history", "dialogue.jsonl"), npcLine);
            foreach (Dictionary<string, object> memoryWrite in memoryWrites)
            {
                CampaignFileService.AppendJsonLineToPath(CampaignFileService.CharacterFile(campaignId, heroId, "memory", "memories.jsonl"), memoryWrite);
            }

            Dictionary<string, object> dynamicCharacteristicsStore = CharactersService.StoreDynamicCharacteristicWrites(
                campaignId, heroId, dynamicCharacteristicWrites, payload, conversationExchange,
                decisionId, "dialogue", dialogueLocationId, ts, new[] { heroId, dialoguePlayerId });

            Dictionary<string, object> memoryLayerStore = ReadDictionary(ReadDictionary(conversationExchange, "acceptedUnit"), "memory");
            if (ActionPipelineService.ShouldPersistGovernmentCertificationConversationSideEffects(
                    governmentCertificationMemoryIsolation))
                DialogueService.UpdateSharedGroupConversationState(campaignId, payload, heroId,
                    heroName, reply, conversationSessionId, ts, decisionId);
            Dictionary<string, object> motiveOutcome =
                !delimitedSpeechOnly && ActionPipelineService.ShouldPersistGovernmentCertificationConversationSideEffects(
                    governmentCertificationMemoryIsolation)
                    ? DialogueService.ApplyConversationMotiveOutcome(campaignId, motiveDecision,
                        payload, reply, intent, decisionId, decisionBrief)
                    : new Dictionary<string, object>
                    {
                        ["suppressed"] = true,
                        ["reason"] = delimitedSpeechOnly ? "speech_only" : "government_certification_memory_isolation"
                    };

            if (stateUpdates.Count > 0)
            {
                ActionPipelineService.MergeCharacterState(campaignId, heroId, stateUpdates);
            }

            CampaignFileService.AppendJsonLineToPath(CampaignFileService.CharacterFile(campaignId, heroId, "history", "decisions.jsonl"), new Dictionary<string, object>
            {
                ["id"] = decisionId,
                ["ts"] = ts,
                ["campaignId"] = campaignId,
                ["heroStringId"] = heroId,
                ["playerName"] = playerName,
                ["heroName"] = heroName,
                ["playerText"] = playerText,
                ["reply"] = reply,
                ["decisionBrief"] = decisionBrief,
                ["motiveDecision"] = motiveDecision,
                ["motiveOutcome"] = motiveOutcome,
                ["emotion"] = emotion,
                ["intent"] = intent,
                ["relationshipSignal"] = relationshipSignal,
                ["relationshipAssessments"] = relationshipAssessments,
                ["actionGate"] = actionGate,
                ["chancellorDecision"] = chancellorDecision,
                ["nobleDocketTurn"] = nobleDocketTurn,
                ["nobleDocketMechanicalRepairApplied"] = nobleDocketMechanicalRepairApplied,
                ["conceptionGate"] = conceptionGate,
                ["identityView"] = identityView,
                ["memoryWrites"] = memoryWrites,
                ["beliefWrites"] = beliefWrites,
                ["relationshipUpdates"] = relationshipUpdates,
                ["obligationWrites"] = obligationWrites,
                ["comprehensionWrites"] = comprehensionWrites,
                ["dynamicCharacteristicWrites"] = dynamicCharacteristicWrites,
                ["dynamicCharacteristicsStore"] = dynamicCharacteristicsStore,
                ["recallConsistencyRepairs"] = recallConsistencyRepairs,
                ["stateUpdates"] = stateUpdates,
                ["suggestedActions"] = suggestedActions,
                ["memoryLayerStore"] = memoryLayerStore,
                ["conversationExchange"] = conversationExchange,
                ["socialSignals"] = ReadDictionaryList(socialSignalValidation, "signals"),
                ["rejectedSocialSignals"] = ReadDictionaryList(socialSignalValidation, "rejected"),
                ["immediateSocialIncidents"] = immediateSocialIncidents,
                ["ambassadorDecision"] = ambassadorDecision,
                ["testDirective"] = isTestDirective,
                ["testDirectiveText"] = isTestDirective ? testDirectiveText : "",
                ["guardedPromptOverride"] = guardedPromptOverrideEvidence,
                ["actionAcceptanceGateRepaired"] = actionAcceptanceGateRepaired,
                ["queuedTestDirectiveActions"] = queuedTestDirectiveActions,
                ["queuedDialogueActions"] = queuedDialogueActions,
                ["testDirectiveErrors"] = testDirectiveErrors,
                ["dialogueActionErrors"] = dialogueActionErrors,
                ["identityIntroductionResults"] = identityIntroductionResults
                ,["conversationSceneState"] = conversationSceneState
                ,["conversationSceneResolution"] = conversationSceneResolution
            });
            storageMs = phaseTimer.ElapsedMilliseconds;
            totalTimer.Stop();
            Dictionary<string, object> timing = WithNpcRelationshipTiming(
                DialogueTiming(totalTimer.ElapsedMilliseconds, upsertMs,
                    constructionMs, contextLoadMs, promptBuildMs, llmMs,
                    ReadLong(llm, "durationMs", 0), parseMs, storageMs),
                npcRelationshipContextMs, npcRelationshipPrompt);
            timing["promptChars"] = promptEnvelope.TotalChars;
            timing["contextBundleCount"] = contextBundles.Count;
            timing["contextBundleChars"] = Json.Serialize(contextBundles).Length;
            timing["selectedContextPullCount"] = selectedContextPulls.Count;
            string encyclopediaText = CharactersService.BuildEncyclopediaBackstoryText(campaignId, heroId);

            List<Dictionary<string, object>> lines = new List<Dictionary<string, object>>(priorLines);
            lines.Add(playerLine);
            lines.Add(npcLine);
            Dictionary<string, object> response = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["heroStringId"] = heroId,
                ["reply"] = reply,
                ["identityView"] = identityView,
                ["emotion"] = emotion,
                ["intent"] = intent,
                ["relationshipSignal"] = relationshipSignal,
                ["relationshipAssessments"] = relationshipAssessments,
                ["decisionBrief"] = decisionBrief,
                ["motiveDecision"] = motiveDecision,
                ["motiveOutcome"] = motiveOutcome,
                ["actionGate"] = actionGate,
                ["chancellorDecision"] = chancellorDecision,
                ["nobleDocketTurn"] = nobleDocketTurn,
                ["conceptionGate"] = conceptionGate,
                ["memoryWrites"] = memoryWrites,
                ["beliefWrites"] = beliefWrites,
                ["relationshipUpdates"] = relationshipUpdates,
                ["obligationWrites"] = obligationWrites,
                ["comprehensionWrites"] = comprehensionWrites,
                ["dynamicCharacteristicWrites"] = dynamicCharacteristicWrites,
                ["dynamicCharacteristicsStore"] = dynamicCharacteristicsStore,
                ["recallConsistencyRepairs"] = recallConsistencyRepairs,
                ["stateUpdates"] = stateUpdates,
                ["conversationSceneState"] = conversationSceneState,
                ["conversationSceneResolution"] = conversationSceneResolution,
                ["suggestedActions"] = suggestedActions,
                ["testDirective"] = isTestDirective,
                ["testDirectiveText"] = isTestDirective ? testDirectiveText : "",
                ["guardedPromptOverride"] = guardedPromptOverrideEvidence,
                ["queuedTestDirectiveActions"] = queuedTestDirectiveActions,
                ["queuedDialogueActions"] = queuedDialogueActions,
                ["testDirectiveErrors"] = testDirectiveErrors,
                ["dialogueActionErrors"] = dialogueActionErrors,
                ["identityIntroductionResults"] = identityIntroductionResults,
                ["actionShadowPreview"] = actionShadowPreview,
                ["selectedContextPulls"] = selectedContextPulls,
                ["contextBundles"] = contextBundles,
                ["conversationExchange"] = conversationExchange,
                ["socialSignals"] = ReadDictionaryList(socialSignalValidation, "signals"),
                ["rejectedSocialSignals"] = ReadDictionaryList(socialSignalValidation, "rejected"),
                ["immediateSocialIncidents"] = immediateSocialIncidents,
                ["ambassadorDecision"] = ambassadorDecision,
                ["memoryLayerStore"] = memoryLayerStore,
                ["lines"] = lines,
                ["construction"] = construction,
                ["encyclopediaText"] = encyclopediaText,
                ["timing"] = timing
            };
            if (privateGovernmentChannel && ReadBool(ReadDictionary(payload, "governmentPrivateContext"), "enabled", false)
                && ReadDictionary(parsed, "governmentCommitment") != null)
                response["governmentCommitment"] = CloneDictionary(ReadDictionary(parsed, "governmentCommitment"));
            OperationsService.LogOperational("dialogue.respond", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["heroStringId"] = heroId,
                ["decisionId"] = decisionId,
                ["playerChars"] = playerText.Length,
                ["replyChars"] = reply.Length,
                ["decisionBrief"] = decisionBrief,
                ["emotion"] = emotion,
                ["intent"] = intent,
                ["relationshipSignal"] = relationshipSignal,
                ["actionGate"] = actionGate,
                ["conceptionGate"] = conceptionGate,
                ["memoryWriteCount"] = memoryWrites.Count,
                ["dynamicCharacteristicWriteCount"] = dynamicCharacteristicWrites.Count,
                ["dynamicCharacteristicStoredCount"] = ReadInt(dynamicCharacteristicsStore, "storedCount", 0),
                ["recallConsistencyRepairCount"] = recallConsistencyRepairs.Count,
                ["stateUpdateCount"] = stateUpdates.Count,
                ["suggestedActionCount"] = suggestedActions.Count,
                ["testDirective"] = isTestDirective,
                ["guardedPromptOverride"] = guardedPromptOverrideEvidence,
                ["testDirectiveQueuedActionCount"] = queuedTestDirectiveActions.Count,
                ["testDirectiveErrors"] = testDirectiveErrors,
                ["dialogueQueuedActionCount"] = queuedDialogueActions.Count,
                ["dialogueActionErrors"] = dialogueActionErrors,
                ["actionShadowPreview"] = actionShadowPreview,
                ["selectedContextPulls"] = selectedContextPulls.Select(x => ReadString(x, "id", "")).ToList(),
                ["contextBundleCount"] = contextBundles.Count,
                ["contextBundleChars"] = Json.Serialize(contextBundles).Length,
                ["timing"] = timing
            });
            OperationsService.WriteAudit(campaignId, correlationId, "server", "dialogue", "dialogue.response", heroId, "", "", "completed", totalTimer.ElapsedMilliseconds, "Dialogue response completed.", new Dictionary<string, object>
            {
                ["decisionId"] = decisionId,
                ["playerLine"] = playerLine,
                ["npcLine"] = npcLine,
                ["reply"] = reply,
                ["identityView"] = identityView,
                ["decisionBrief"] = decisionBrief,
                ["actionGate"] = actionGate,
                ["conceptionGate"] = conceptionGate,
                ["memoryWrites"] = memoryWrites,
                ["beliefWrites"] = beliefWrites,
                ["relationshipUpdates"] = relationshipUpdates,
                ["obligationWrites"] = obligationWrites,
                ["comprehensionWrites"] = comprehensionWrites,
                ["dynamicCharacteristicWrites"] = dynamicCharacteristicWrites,
                ["dynamicCharacteristicsStore"] = dynamicCharacteristicsStore,
                ["recallConsistencyRepairs"] = recallConsistencyRepairs,
                ["stateUpdates"] = stateUpdates,
                ["suggestedActions"] = suggestedActions,
                ["guardedPromptOverride"] = guardedPromptOverrideEvidence,
                ["queuedTestDirectiveActions"] = queuedTestDirectiveActions,
                ["queuedDialogueActions"] = queuedDialogueActions,
                ["testDirectiveErrors"] = testDirectiveErrors,
                ["dialogueActionErrors"] = dialogueActionErrors,
                ["actionShadowPreview"] = actionShadowPreview,
                ["timing"] = timing
            });
            DialogueService.ScheduleReplayCorpusCapture(campaignId, correlationId);
            if (!string.IsNullOrWhiteSpace(dialogueAuditRunId))
            {
                DialogueService.SaveNpcDialogueAuditResponse(campaignId, correlationId, response);
            }
            LlmProviderService.RecordCodexFinalizedContract(parsed, response);
            return response;
        }

        private static Dictionary<string, object> DialogueTiming(long totalMs, long upsertMs, long constructionMs, long contextLoadMs, long promptBuildMs, long llmMs, long llmProviderMs, long parseMs, long storageMs)
        {
            return new Dictionary<string, object>
            {
                ["totalMs"] = totalMs,
                ["characterUpsertMs"] = upsertMs,
                ["characterConstructionMs"] = constructionMs,
                ["contextLoadMs"] = contextLoadMs,
                ["promptBuildMs"] = promptBuildMs,
                ["llmCallMs"] = llmMs,
                ["llmProviderMs"] = llmProviderMs,
                ["parseMs"] = parseMs,
                ["storageMs"] = storageMs
            };
        }

        private static Dictionary<string, object> WithNpcRelationshipTiming(
            Dictionary<string, object> timing,
            long totalMs,
            Dictionary<string, object> npcRelationshipPrompt)
        {
            timing = timing ?? new Dictionary<string, object>();
            Dictionary<string, object> relationshipTiming =
                NpcRelationshipTiming(totalMs, npcRelationshipPrompt);
            timing["npcRelationshipContextMs"] = totalMs;
            timing["npcRelationshipProviderMs"] =
                ReadLong(relationshipTiming, "providerMs", 0);
            timing["npcRelationshipProviderCallCount"] =
                ReadInt(relationshipTiming, "providerCallCount", 0);
            timing["npcRelationshipGeneratedCount"] =
                ReadInt(relationshipTiming, "generatedCount", 0);
            timing["npcRelationshipReusedCount"] =
                ReadInt(relationshipTiming, "reusedCount", 0);
            return timing;
        }

        private static Dictionary<string, object> NpcRelationshipTiming(
            long totalMs,
            Dictionary<string, object> npcRelationshipPrompt)
        {
            Dictionary<string, object> generation =
                ReadDictionary(npcRelationshipPrompt, "historyGeneration")
                ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["totalMs"] = totalMs,
                ["providerMs"] = ReadLong(generation, "providerDurationMs", 0),
                ["providerCallCount"] = ReadInt(generation, "providerCallCount", 0),
                ["generatedCount"] = ReadInt(generation, "generatedCount", 0),
                ["reusedCount"] = ReadInt(generation, "reusedCount", 0),
                ["fallbackCount"] = ReadInt(generation, "fallbackCount", 0)
            };
        }
    }
}
