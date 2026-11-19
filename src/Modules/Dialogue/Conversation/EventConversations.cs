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
        internal static Dictionary<string, object> SocialEventStart(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            string campaignId = ReadString(payload, "campaignId", "default");
            string eventId = ReadFirstString(payload, "eventId", "socialEventId");
            if (string.IsNullOrWhiteSpace(eventId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "eventId is required." };
            }

            List<Dictionary<string, object>> attendees = ReadDictionaryList(payload, "attendees");
            VerificationService.SaveTestSnapshotFromPayload(campaignId, correlationId, "social_event", "event.start", payload);
            foreach (Dictionary<string, object> attendee in attendees)
            {
                if (attendee != null)
                {
                    CharactersService.UpsertCharacterProfile(campaignId, attendee);
                }
            }

            Dictionary<string, object> row = EventTranscriptLine("system", "Event", "Started " + ReadString(payload, "displayName", "social event") + ".", payload);
            CampaignFileService.AppendJsonLineToPath(SocialEventTranscriptFile(campaignId, eventId), row);
            AppendEventLineForAttendees(campaignId, attendees, row);
            OperationsService.WriteAudit(campaignId, correlationId, "game_client", "social_event", "event.start", "", "", eventId, "completed", 0, "Social event started.", new Dictionary<string, object>
            {
                ["event"] = payload,
                ["attendeeCount"] = attendees.Count,
                ["transcriptLine"] = row
            });
            OperationsService.LogOperational("social_event.start", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["eventId"] = eventId,
                ["templateId"] = ReadString(payload, "templateId", ""),
                ["attendeeCount"] = attendees.Count
            });

            return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaignId, ["correlationId"] = correlationId, ["eventId"] = eventId, ["attendeeCount"] = attendees.Count };
        }

        internal static Dictionary<string, object> SocialEventResolve(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            string campaignId = ReadString(payload, "campaignId", "default");
            string eventId = ReadFirstString(payload, "eventId", "socialEventId");
            if (string.IsNullOrWhiteSpace(eventId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "eventId is required." };
            }

            Dictionary<string, object> row = EventTranscriptLine("system", "Event", "Resolved " + ReadString(payload, "displayName", "social event") + ".", payload);
            VerificationService.SaveTestSnapshotFromPayload(campaignId, correlationId, "social_event", "event.resolve", payload);
            CampaignFileService.AppendJsonLineToPath(SocialEventTranscriptFile(campaignId, eventId), row);
            AppendEventLineForAttendees(campaignId, ReadDictionaryList(payload, "attendees"), row);
            string conversationSessionId = "social_event_" + DialogueService.SafeMemoryKey(eventId);
            Dictionary<string, object> conversationFinish = DialogueService.FinishConversationSession(campaignId, conversationSessionId,
                "social_event_resolved", false, ReadDouble(payload, "worldDay", 0d), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            OperationsService.WriteAudit(campaignId, correlationId, "game_client", "social_event", "event.resolve", "", "", eventId, "completed", 0, "Social event resolved.", new Dictionary<string, object>
            {
                ["event"] = payload,
                ["transcriptLine"] = row
            });
            OperationsService.LogOperational("social_event.resolve", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["eventId"] = eventId,
                ["templateId"] = ReadString(payload, "templateId", "")
            });

            return new Dictionary<string, object>
            {
                ["ok"] = true, ["campaignId"] = campaignId, ["correlationId"] = correlationId, ["eventId"] = eventId,
                ["conversationSessionId"] = conversationSessionId, ["conversationFinish"] = conversationFinish
            };
        }

        internal static Dictionary<string, object> SocialEventHistory(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string eventId = ReadFirstString(payload, "eventId", "socialEventId");
            int limit = Math.Max(1, ReadInt(payload, "limit", 80));
            if (string.IsNullOrWhiteSpace(eventId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "eventId is required." };
            }

            List<Dictionary<string, object>> all = Program.ReadJsonLinesFromPath(SocialEventTranscriptFile(campaignId, eventId));
            all = DialogueService.ReadOriginalConversationHistory(campaignId, payload, "", "social_event_" + DialogueService.SafeMemoryKey(eventId), limit, all);
            int start = Math.Max(0, all.Count - limit);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["eventId"] = eventId,
                ["lines"] = all.Skip(start).ToList()
            };
        }

        internal static Dictionary<string, object> PartyChatHistory(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string sessionId = ReadFirstString(payload, "conversationSessionId", "sessionId");
            int limit = Math.Max(1, Math.Min(100, ReadInt(payload, "limit", 36)));
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "conversationSessionId is required."
                };
            }

            List<Dictionary<string, object>> all = Program.ReadJsonLinesFromPath(
                PartyChatSessionTranscriptFile(campaignId, payload));
            all = DialogueService.ReadOriginalConversationHistory(campaignId, payload, "", sessionId, limit, all);
            int start = Math.Max(0, all.Count - limit);
            List<Dictionary<string, object>> lines = all.Skip(start).ToList();
            for (int index = 0; index < lines.Count; index++)
            {
                Dictionary<string, object> line = lines[index];
                line["sequence"] = index + 1;
                if (string.Equals(ReadString(line, "role", ""), "npc", StringComparison.OrdinalIgnoreCase))
                {
                    line["text"] = ConversationResponseService.SanitizeVisibleReply(ReadString(line, "text", ""));
                }
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["conversationSessionId"] = sessionId,
                ["lines"] = lines
            };
        }

        internal static Dictionary<string, object> SocialEventRespond(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            DialogueService.BeginSinglePassConversation(payload);
            return DialogueService.RunPlayerActionTurn(payload, () => LlmProviderService.RunCodexConversationScope(payload, () => SocialEventRespondCore(payload)));
        }

        private static Dictionary<string, object> SocialEventRespondCore(Dictionary<string, object> payload)
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
            Dictionary<string, object> speaker = ReadDictionary(payload, "speaker") ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", ReadString(speaker, "campaignId", "default"));
            string eventId = ReadFirstString(payload, "eventId", "socialEventId");
            string auditMode = ReadString(payload, "mode", string.Equals(ReadString(payload, "templateId", ""), "party_chat", StringComparison.OrdinalIgnoreCase) ? "party_chat" : "social_event");
            bool partyChatRequest = string.Equals(auditMode, "party_chat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventId, "party_chat", StringComparison.OrdinalIgnoreCase);
            string heroId = ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId", "characterId");
            if (string.IsNullOrWhiteSpace(heroId))
            {
                heroId = CampaignFileService.CharacterIdFrom(speaker);
            }

            DialogueService.ApplyPlayerInputAudienceToPayload(payload, heroId);

            string playerText = ReadFirstString(payload, "playerText", "text", "message");
            bool approachOpening = ReadBool(payload, "approachOpening", false)
                || string.Equals(ReadString(payload, "turnType", ""), "npc_approach_opening", StringComparison.OrdinalIgnoreCase);
            string promptTurnText = approachOpening
                ? "[EVENT CUE: You have just chosen to approach the player during this social-event phase. Speak first in character. Open naturally from your current motive, relationship, knowledge, and the supplied setting. Do not claim that the player said anything and do not invent a completed action.]"
                : playerText;
            if (string.IsNullOrWhiteSpace(eventId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "eventId is required." };
            }

            if (string.IsNullOrWhiteSpace(heroId))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "speakerHeroStringId is required." };
            }

            if (string.IsNullOrWhiteSpace(playerText) && !approachOpening)
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "playerText is required." };
            }

            VerificationService.SaveTestSnapshotFromPayload(campaignId, correlationId, auditMode, auditMode + ".request", payload);
            OperationsService.WriteAudit(campaignId, correlationId, "game_client", auditMode, auditMode + ".request", heroId, "", eventId, "started", 0, "Event response request received.", new Dictionary<string, object>
            {
                ["speaker"] = speaker,
                ["playerText"] = playerText,
                ["turnType"] = approachOpening ? "npc_approach_opening" : "player_reply",
                ["sceneContext"] = ReadString(payload, "sceneContext", ""),
                ["eventId"] = eventId,
                ["templateId"] = ReadString(payload, "templateId", ""),
                ["attendees"] = ReadDictionaryList(payload, "attendees"),
                ["selectedContextPulls"] = ReadDictionaryList(payload, "selectedContextPulls"),
                ["contextBundles"] = ReadDictionaryList(payload, "contextBundles"),
                ["actionResolutionIndex"] = ReadDictionary(payload, "actionResolutionIndex")
            });

            phaseTimer.Restart();
            List<Dictionary<string, object>> attendees = ReadDictionaryList(payload, "attendees");
            foreach (Dictionary<string, object> attendee in attendees)
            {
                if (attendee != null)
                {
                    CharactersService.UpsertCharacterProfile(campaignId, attendee);
                }
            }

            if (speaker.Count > 0)
            {
                if (!speaker.ContainsKey("heroStringId"))
                {
                    speaker["heroStringId"] = heroId;
                }

                CharactersService.UpsertCharacterProfile(campaignId, speaker);
            }
            upsertMs = phaseTimer.ElapsedMilliseconds;

            phaseTimer.Restart();
            construction = CharactersService.EnsureCharacterConstructed(campaignId, heroId, speaker, "social_event");
            constructionMs = phaseTimer.ElapsedMilliseconds;
            if (!CharactersService.IsCharacterConstructionReady(construction))
            {
                totalTimer.Stop();
                Dictionary<string, object> failedTiming = DialogueTiming(totalTimer.ElapsedMilliseconds, upsertMs, constructionMs, contextLoadMs, promptBuildMs, llmMs, 0, parseMs, storageMs);
                Dictionary<string, object> failed = CharactersService.CharacterConstructionFailureResponse("social_event", campaignId, heroId, ReadString(speaker, "name", heroId), construction, failedTiming, new Dictionary<string, object>
                {
                    ["eventId"] = eventId
                });
                OperationsService.LogOperational("social_event.character_construction_failed", failed);
                OperationsService.WriteAudit(campaignId, correlationId, "server", auditMode, "character.construction", heroId, "", eventId, "failed", constructionMs, "Character construction failed before event response.", new Dictionary<string, object>
                {
                    ["construction"] = construction,
                    ["timing"] = failedTiming
                });
                return failed;
            }

            phaseTimer.Restart();
            string heroName = ReadString(speaker, "name", heroId);
            string playerName = ReadString(payload, "playerName", "Player");
            string sceneContext = ReadString(payload, "sceneContext", "");
            Dictionary<string, object> settings = SettingsService.LoadSettings();
            int canonicalTranscriptLimit = Math.Max(2, Math.Min(100, ReadInt(settings, "recentRawConversationTurns", 30)));
            List<Dictionary<string, object>> eventLines = partyChatRequest
                ? BuildPartyChatPromptTranscript(payload, playerText)
                : BuildSocialEventPromptTranscript(
                    Program.ReadJsonLinesFromPath(SocialEventTranscriptFile(campaignId, eventId)),
                    canonicalTranscriptLimit);
            eventLines = CanonicalizePromptTranscript(eventLines, canonicalTranscriptLimit);
            Dictionary<string, object> identityView = CharactersService.ResolvePromptIdentity(payload, heroId, auditMode, eventId, eventLines);
            string promptPlayerName = ReadString(identityView, "usableName", "the stranger");
            payload["identityView"] = identityView;
            DialogueService.BindPromptTranscriptSources(campaignId, heroId, payload, eventLines);
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
            List<Dictionary<string, object>> compactActionCatalog = DialogueService.UsesStructuredActions(payload) ? new List<Dictionary<string, object>>() : ActionPipelineService.CompactActionCatalog(ActionPipelineService.ActionCatalog());
            contextLoadMs = phaseTimer.ElapsedMilliseconds;
            OperationsService.WriteAudit(campaignId, correlationId, "server", auditMode, "context.loaded", heroId, "", eventId, "completed", contextLoadMs, "Loaded event character stack, transcript, context bundles, and action catalog.", new Dictionary<string, object>
            {
                ["profile"] = profile,
                ["characteristics"] = characteristics,
                ["state"] = state,
                ["relationships"] = relationships,
                ["memorySummary"] = summary,
                ["memories"] = memories,
                ["eventLines"] = eventLines,
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
                    ["mode"] = auditMode,
                    ["heroId"] = heroId,
                    ["eventId"] = eventId
                }))
            {
                npcRelationshipPrompt =
                    RelationshipsService.BuildNpcRelationshipPromptContext(campaignId, heroId, payload);
                relationshipTrace.Set("targetCount",
                    ReadDictionaryList(npcRelationshipPrompt, "targets").Count);
            }
            npcRelationshipContextMs = phaseTimer.ElapsedMilliseconds;
            payload["precomputedNpcRelationshipPrompt"] = npcRelationshipPrompt;
            OperationsService.WriteAudit(campaignId, correlationId, "server", auditMode,
                "relationship_history.loaded", heroId, "", eventId, "completed",
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
                campaignId, payload, heroId, promptTurnText, eventLines, ReadString(payload, "mode", "social_event"));
            DialogueService.PromptEnvelope promptEnvelope;
            using (OperationsService.ReignTraceScope promptTrace = OperationsService.BeginReignSpan("prompt.build", new Dictionary<string, object>
            { ["campaignId"] = campaignId, ["correlationId"] = correlationId, ["mode"] = auditMode, ["heroId"] = heroId, ["eventId"] = eventId }))
            {
                if (LlmProviderService.ActiveCodexConversationScope.Value != null) payload["codexParallelPreparation"] = new Dictionary<string, object>
                { ["consistentSnapshot"] = true, ["characterInitialized"] = true, ["requiredStateUpdatesComplete"] = true };
                promptEnvelope = DialogueService.BuildEventPromptEnvelope(campaignId, eventId, heroId, heroName, promptPlayerName, playerName, promptTurnText, sceneContext, payload, profile, characteristics, state, relationships, summary, eventLines, selectedContextPulls, contextBundles, compactActionCatalog, identityView);
                promptTrace.Set("promptChars", promptEnvelope.TotalChars);
            }
            Dictionary<string, object> motiveDecision = ReadDictionary(promptEnvelope.Diagnostics, "motiveDecision") ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> messages = promptEnvelope.Messages;
            promptBuildMs = phaseTimer.ElapsedMilliseconds;
            OperationsService.WriteAudit(campaignId, correlationId, "server", auditMode, "prompt.built", heroId, "", eventId, "completed", promptBuildMs, "Event prompt assembled.", new Dictionary<string, object>
            {
                ["messages"] = messages,
                ["promptChars"] = promptEnvelope.TotalChars,
                ["promptEnvelope"] = promptEnvelope.Diagnostics,
                ["contextBundleCount"] = contextBundles.Count,
                ["selectedContextPullCount"] = selectedContextPulls.Count
            });

            phaseTimer.Restart();
            Dictionary<string, object> eventLlmRequest = BuildConversationGenerationRequest(
                settings, promptEnvelope, campaignId, correlationId, heroId, playerText, true, eventId);
            LlmProviderService.AttachCodexAuthoritativeConversationMetadata(eventLlmRequest, payload, campaignId, heroId, auditMode,
                ReadFirstString(payload, "conversationSessionId", "sessionId", "conversationId"), identityView, profile, state, relationships, summary,
                conversationSceneState, null, contextBundles, eventLines, promptTurnText);
            DialogueService.AttachStructuredActionResponseContract(eventLlmRequest, payload);
            Dictionary<string, object> llm = DialogueService.GenerateSinglePassConversation(eventLlmRequest, payload, auditMode, allowQuiet: true);
            if (ReadBool(ReadDictionary(llm, "conversationAgency"), "replayed", false)) return DialogueService.ConversationAgencyReplayResponse(llm, heroId);
            DialogueService.PreserveConversationVerification(payload, llm);
            LlmProviderService.RecordCodexFinalRepairEvidence(llm);
            llmMs = phaseTimer.ElapsedMilliseconds;
            if (!ReadBool(llm, "ok", false))
            {
                llm["inputRecovery"] = DialogueService.RetainUnansweredPlayerContribution(campaignId, payload, heroId,
                    FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"), ReadFirstString(profile, "mainHeroStringId")),
                    playerName, heroName, playerText, grouped: true,
                    socialEvent: ReadString(payload, "mode", "") == "party_chat" ? "party_chat" : eventId);
                totalTimer.Stop();
                Dictionary<string, object> failedTiming = WithNpcRelationshipTiming(
                    DialogueTiming(totalTimer.ElapsedMilliseconds, upsertMs,
                        constructionMs, contextLoadMs, promptBuildMs, llmMs,
                        ReadLong(llm, "durationMs", 0), parseMs, storageMs),
                    npcRelationshipContextMs, npcRelationshipPrompt);
                OperationsService.LogOperational("social_event.respond_failed", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["eventId"] = eventId,
                    ["heroStringId"] = heroId,
                    ["stage"] = "llm",
                    ["timing"] = failedTiming,
                    ["llm"] = llm
                });
                OperationsService.WriteAudit(campaignId, correlationId, "server", auditMode, auditMode + ".response", heroId, "", eventId, "failed", totalTimer.ElapsedMilliseconds, "Event response failed during LLM call.", new Dictionary<string, object>
                {
                    ["stage"] = "llm",
                    ["timing"] = failedTiming,
                    ["llm"] = llm
                });
                return new Dictionary<string, object> { ["ok"] = false, ["stage"] = "llm", ["error"] = LlmFailureForDisplay(llm),
                    ["conversationRecovery"] = ReadDictionary(llm, "conversationRecovery"), ["inputRecovery"] = ReadDictionary(llm, "inputRecovery"), ["llm"] = llm, ["timing"] = failedTiming };
            }

            phaseTimer.Restart();
            string content = ReadString(llm, "content", "");
            Dictionary<string, object> parsed = TryParseJsonObject(content);
            bool agencyVisibleFailure = ReadBool(ReadDictionary(llm, "conversationAgency"), "visibleFailure", false)
                || ReadBool(llm, "visibleRepairFailure", false) || ReadBool(llm, "conversationEffectsUnavailable", false);
            string participation = WorldSimulationService.NormalizeSocialEventParticipation(parsed);
            string reply = ReadFirstString(parsed, "reply", "response", "text", "content");
            if (string.IsNullOrWhiteSpace(reply) && participation != "quiet")
            {
                reply = ConversationResponseService.ExtractVisibleReply(content);
            }

            if (string.IsNullOrWhiteSpace(reply) && participation != "quiet")
            {
                reply = "[Bannerlord Reign: the model returned malformed JSON, so the visible reply was hidden. Please try again.]";
            }
            if (participation == "quiet")
            {
                reply = string.Empty;
            }
            reply = ConversationResponseService.SanitizeVisibleReply(reply);
            reply = CharactersService.RemoveSpokenNumericSkillLevels(reply, out bool skillNumericRepairApplied);
            reply = CharactersService.SanitizePromptForIdentity(reply, "", playerName, identityView);
            reply = DialogueService.NormalizeUnknownIdentityVisibleAddress(reply, identityView);
            List<Dictionary<string, object>> recallConsistencyRepairs;
            reply = DialogueService.RepairVisibleExactRecallIdentifiers(playerText, reply, parsed, messages, out recallConsistencyRepairs);
            // Keep group/event publication from caching effects of a discarded
            // leave draft before the final prose guard runs.
            if (ReadString(ReadDictionary(payload, "npcLeaveResponseGuard"), "repair", "") == "bounded_local")
                participation = WorldSimulationService.NormalizeSocialEventParticipation(parsed);
            if (!agencyVisibleFailure)
                reply = DialogueService.ApplyConversationDrinking(campaignId, heroId, payload, profile, characteristics, parsed, reply, persist: false);
            Dictionary<string, object> conversationSceneResolution = TestDict("status", "pending_acceptance");

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string decisionId = DialogueService.AcceptedConversationEventId(campaignId, heroId, payload);
            string emotion = parsed == null ? "" : ReadString(parsed, "emotion", "");
            string intent = parsed == null ? "" : ReadString(parsed, "intent", "");
            string relationshipSignal = parsed == null ? "" : ReadFirstString(parsed, "relationshipSignal", "relationship_signal");
            string reactionTargetHeroStringId = WorldSimulationService.NormalizeSocialEventReactionTarget(parsed, payload, heroId);
            List<Dictionary<string, object>> relationshipAssessments = RelationshipsService.NormalizeConversationRelationshipAssessments(
                parsed, payload, heroId, relationshipSignal, reactionTargetHeroStringId, ReadString(payload, "sceneTurnId", ""));
            if (agencyVisibleFailure || participation == "quiet" || string.IsNullOrWhiteSpace(reply))
            {
                relationshipAssessments.Clear();
            }
            else DialogueService.ApplyAgencyRelationshipAssessment(relationshipAssessments, payload, heroId,
                ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"));
            if (!ReadBool(payload, "canInvitePlayerToDance", true) && WorldSimulationService.IsForbiddenDanceInvitation(intent, reply))
            {
                participation = "quiet";
                reactionTargetHeroStringId = "";
                relationshipAssessments.Clear();
                reply = string.Empty;
                intent = "ordinary_social_observation";
            }
            if (participation == "quiet")
            {
                parsed["participation"] = "quiet";
                parsed["reply"] = "";
            }
            Dictionary<string, object> actionGate = approachOpening
                ? ActionPipelineService.NormalizeActionGate(null, "", "")
                : ActionPipelineService.NormalizeActionGate(parsed == null ? null : ReadDictionary(parsed, "actionGate") ?? ReadDictionary(parsed, "action_gate"), playerText, reply);
            Dictionary<string, object> decisionBrief = DialogueService.AugmentDecisionBriefWithMotiveEvidence(DialogueService.NormalizeDecisionBrief(parsed, intent, actionGate), motiveDecision);
            DialogueService.RemovePrivateReasoningFields(parsed);
            List<Dictionary<string, object>> memoryWrites = parsed == null ? new List<Dictionary<string, object>>() : ActionPipelineService.NormalizeMemoryWrites(parsed.ContainsKey("memoryWrites") ? parsed["memoryWrites"] : null, campaignId, heroId, ts);
            List<Dictionary<string, object>> beliefWrites = parsed == null ? new List<Dictionary<string, object>>() : ReadDictionaryList(parsed, "beliefWrites").Concat(ReadDictionaryList(parsed, "belief_writes")).ToList();
            List<Dictionary<string, object>> relationshipUpdates = parsed == null ? new List<Dictionary<string, object>>() : ReadDictionaryList(parsed, "relationshipUpdates").Concat(ReadDictionaryList(parsed, "relationship_updates")).ToList();
            List<Dictionary<string, object>> obligationWrites = parsed == null ? new List<Dictionary<string, object>>() : ReadDictionaryList(parsed, "obligationWrites").Concat(ReadDictionaryList(parsed, "obligation_writes")).ToList();
            List<Dictionary<string, object>> comprehensionWrites = parsed == null ? new List<Dictionary<string, object>>() : ReadDictionaryList(parsed, "comprehensionWrites").Concat(ReadDictionaryList(parsed, "comprehension_writes")).ToList();
            List<Dictionary<string, object>> dynamicCharacteristicWrites = CharactersService.NormalizeDynamicCharacteristicWrites(parsed, heroId, profile, payload, reply);
            if (agencyVisibleFailure || approachOpening || participation == "quiet") dynamicCharacteristicWrites.Clear();
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
            if (pendingAddressConvention != null
                && pendingAddressConvention.Count > 0)
                stateUpdates["politicalAddressConvention"] =
                    CloneDictionary(pendingAddressConvention);
            Dictionary<string, object> politicalConduct =
                ReadDictionary(parsed, "politicalConduct")
                ?? new Dictionary<string, object>();
            bool recallOnlyTurn = DialogueService.IsPureConversationRecallRequest(playerText);
            if (recallOnlyTurn)
            {
                memoryWrites.Clear();
                beliefWrites.Clear();
                obligationWrites.Clear();
                comprehensionWrites.Clear();
                dynamicCharacteristicWrites.Clear();
            }
            List<Dictionary<string, object>> suggestedActions = approachOpening || parsed == null
                ? new List<Dictionary<string, object>>()
                : ActionPipelineService.NormalizeSuggestedActions(parsed.ContainsKey("suggestedActions") ? parsed["suggestedActions"] : null, compactActionCatalog);
            if (participation == "quiet")
            {
                // These also feed file projections and the returned group result,
                // outside the accepted-source transaction. Silence confirms none.
                memoryWrites.Clear(); beliefWrites.Clear(); relationshipUpdates.Clear(); obligationWrites.Clear();
                comprehensionWrites.Clear(); dynamicCharacteristicWrites.Clear(); stateUpdates.Clear(); suggestedActions.Clear();
                relationshipSignal = "unchanged"; reactionTargetHeroStringId = "";
                actionGate = ActionPipelineService.NormalizeActionGate(null, "", "");
            }
            else DialogueService.CaptureStructuredActionDecision(payload, parsed);
            List<Dictionary<string, object>> identityIntroductionResults = new List<Dictionary<string, object>>();
            OperationsService.WriteAudit(campaignId, correlationId, "server", auditMode, "model.parsed", heroId, "", eventId, parsed == null ? "warning" : "completed", phaseTimer.ElapsedMilliseconds, parsed == null ? "Model response was not valid JSON; visible reply fallback was used." : "Event model JSON parsed.", new Dictionary<string, object>
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
            List<string> eventActionErrors = new List<string>();
            List<Dictionary<string, object>> queuedEventActions = approachOpening || agencyVisibleFailure || participation == "quiet"
                ? new List<Dictionary<string, object>>()
                : ActionPipelineService.QueueAcceptedDialogueActions(campaignId, payload, profile, playerText, reply, actionGate, suggestedActions, out eventActionErrors);
            if (queuedEventActions.Count > 0)
            {
                string statusLine = "[Bannerlord Reign event action queued: " + string.Join(", ", queuedEventActions.Select(x => ReadString(x, "command", "")).Where(x => !string.IsNullOrWhiteSpace(x))) + ".]";
                reply = string.IsNullOrWhiteSpace(reply) || reply == "..." ? statusLine : reply + "\n\n" + statusLine;
            }
            if (WorldSimulationService.HasUnconfirmedMarriageClaim(reply, payload, profile))
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
            reply = WorldSimulationService.FinalizeDialogueActionOutcome(reply, payload, profile, queuedEventActions, eventActionErrors);
            reply = DialogueService.RenderGroundedSceneHeading(payload, heroId, parsed, reply);
            if (!DialogueService.FinalizeConversationGrounding(payload, heroId, parsed, ref reply))
            {
                agencyVisibleFailure = true;
                memoryWrites.Clear(); beliefWrites.Clear(); comprehensionWrites.Clear(); relationshipAssessments.Clear();
                relationshipUpdates.Clear(); obligationWrites.Clear(); dynamicCharacteristicWrites.Clear(); stateUpdates.Clear();
                queuedEventActions.Clear();
            }
            bool privatePlayerInputForSpeaker = ReadBool(payload, "privatePlayerInputForSpeaker", false);
            string sharedPlayerText = ReadString(payload, "publicPlayerText", playerText);
            string sharedReply = participation == "quiet" ? "" : privatePlayerInputForSpeaker
                ? "*The speaker replies privately to the player.*" : DialogueService.ObserverConversationText(reply, heroId, "");
            Dictionary<string, object> playerLine = approachOpening || ReadBool(payload, "suppressPlayerTranscript", false)
                || partyChatRequest && ReadInt(payload, "partySpeakerIndex", 0) > 0
                ? null
                : EventTranscriptLine("player", playerName, sharedPlayerText, payload);
            Dictionary<string, object> npcLine = participation == "quiet" || string.IsNullOrWhiteSpace(reply) ? null : EventTranscriptLine("npc", heroName, sharedReply, payload);
            parseMs = phaseTimer.ElapsedMilliseconds;

            phaseTimer.Restart();
            bool partyChatTurn = string.Equals(ReadString(payload, "mode", ""), "party_chat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventId, "party_chat", StringComparison.OrdinalIgnoreCase);
            string eventTranscriptPath = partyChatTurn
                ? PartyChatSessionTranscriptFile(campaignId, payload)
                : SocialEventTranscriptFile(campaignId, eventId);
            string socialPlayerId = FirstNonEmpty(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"),
                ReadFirstString(profile, "mainHeroStringId"));
            string socialLocationId = FirstNonEmpty(
                ReadFirstString(payload, "locationId", "currentSettlementId", "settlementId"),
                ReadFirstString(profile, "currentSettlementId", "settlementId"));
            List<string> sharedParticipants = partyChatTurn
                ? MergeStringLists(
                    MergeStringLists(ReadStringList(payload, "participants"), ReadStringList(payload, "activeHeroIds")),
                    new[] { heroId, socialPlayerId })
                : new List<string>();
            var acceptedUnit = new DialogueService.AcceptedConversationUnit {
                Campaign = campaignId, Hero = heroId, Player = socialPlayerId, Location = socialLocationId,
                PlayerName = playerName, HeroName = heroName, EventId = decisionId,
                EventType = partyChatTurn ? "party_chat_turn" : "social_event_turn", PlayerText = playerText, Reply = reply,
                Timestamp = ts, Generation = DialogueService._playerActionGeneration, Payload = payload, Motive = motiveDecision,
                Profile = profile, Characteristics = characteristics, Parsed = parsed, Memories = memoryWrites,
                Beliefs = beliefWrites, Relationships = relationshipUpdates, Obligations = obligationWrites,
                Comprehensions = comprehensionWrites, Continuity = ReadDictionaryList(parsed, "continuityWrites"),
                Participants = privatePlayerInputForSpeaker ? new List<string> { heroId, socialPlayerId } : partyChatTurn ? sharedParticipants : null,
                RetrievalEligible = !recallOnlyTurn, CommitEffects = !agencyVisibleFailure && participation != "quiet",
                PublishScene = !agencyVisibleFailure && participation != "quiet",
                PublishIntroductions = !agencyVisibleFailure && participation != "quiet",
                SceneMode = ReadString(payload, "mode", "social_event"),
                GroupConversation = partyChatTurn, PrivateWhisper = privatePlayerInputForSpeaker };
            Dictionary<string, object> conversationExchange = DialogueService.PublishAcceptedConversationUnit(acceptedUnit,
                accept => partyChatTurn
                    ? DialogueService.StorePartyConversationExchange(campaignId, payload, heroId, socialPlayerId, playerName, heroName,
                        sharedPlayerText, privatePlayerInputForSpeaker ? sharedReply : reply, decisionId, ts, accept)
                    : DialogueService.StoreSocialEventConversationExchange(campaignId, payload, heroId, socialPlayerId, playerName, heroName,
                        sharedPlayerText, privatePlayerInputForSpeaker ? sharedReply : reply, decisionId, eventId, ts, accept));
            if (CharactersService.ApplyNpcLivesRelationshipOwnership(conversationExchange, relationshipAssessments, socialPlayerId))
                relationshipSignal = "unchanged";
            llm["conversationEffectAcceptance"] = ReadDictionary(ReadDictionary(conversationExchange, "acceptedUnit"), "effects");
            conversationSceneResolution = ReadDictionary(ReadDictionary(conversationExchange, "acceptedUnit"), "scene");
            identityIntroductionResults = ReadDictionaryList(ReadDictionary(conversationExchange, "acceptedUnit"), "introductions");
            Dictionary<string, object> gossipExchange = privatePlayerInputForSpeaker
                ? ReadDictionary(ReadDictionary(conversationExchange, "acceptedUnit"), "privateExchange") : conversationExchange;
            if (!ReadBool(payload, "conversationGroundingFailed", false) && participation != "quiet" && !recallOnlyTurn)
                DialogueService.AcceptPersonalGossip(campaignId, payload, parsed, gossipExchange, heroId, socialPlayerId, playerText, reply, conversationExchange);
            if (partyChatTurn || !string.IsNullOrWhiteSpace(ReadString(conversationExchange, "sessionId", "")))
            {
                string storedSessionId = ReadString(conversationExchange, "sessionId", "");
                string storedExchangeId = ReadString(conversationExchange, "exchangeId", "");
                if (playerLine != null)
                {
                    playerLine["sessionId"] = storedSessionId;
                    playerLine["exchangeId"] = storedExchangeId;
                }
                if (npcLine != null)
                {
                    npcLine["sessionId"] = storedSessionId;
                    npcLine["exchangeId"] = storedExchangeId;
                }
            }
            AttachAcceptedTranscriptSourceIds(playerLine, npcLine, conversationExchange);
            if (playerLine != null)
            {
                CampaignFileService.AppendJsonLineToPath(eventTranscriptPath, playerLine);
                CampaignFileService.AppendJsonLineToPath(CampaignFileService.CharacterFile(campaignId, heroId, "history", "events.jsonl"), playerLine);
            }
            if (npcLine != null)
            {
                CampaignFileService.AppendJsonLineToPath(eventTranscriptPath, npcLine);
                CampaignFileService.AppendJsonLineToPath(CampaignFileService.CharacterFile(campaignId, heroId, "history", "events.jsonl"), npcLine);
            }
            foreach (Dictionary<string, object> memoryWrite in memoryWrites)
            {
                CampaignFileService.AppendJsonLineToPath(CampaignFileService.CharacterFile(campaignId, heroId, "memory", "memories.jsonl"), memoryWrite);
            }

            List<string> dynamicCharacteristicAudience = partyChatTurn
                ? privatePlayerInputForSpeaker ? new List<string> { heroId, socialPlayerId } : sharedParticipants
                : privatePlayerInputForSpeaker ? new List<string> { heroId, socialPlayerId } : MergeStringLists(
                    attendees.Select(CampaignFileService.CharacterIdFrom).Where(x => !string.IsNullOrWhiteSpace(x)),
                    new[] { heroId, socialPlayerId });
            Dictionary<string, object> dynamicCharacteristicsStore = CharactersService.StoreDynamicCharacteristicWrites(
                campaignId,
                heroId,
                dynamicCharacteristicWrites,
                payload,
                conversationExchange,
                decisionId,
                partyChatTurn ? "party_chat" : ReadString(payload, "mode", "social_event"),
                socialLocationId,
                ts,
                dynamicCharacteristicAudience);

            string partySessionId = ReadString(conversationExchange, "sessionId", "");
            Dictionary<string, object> memoryLayerStore = ReadDictionary(ReadDictionary(conversationExchange, "acceptedUnit"), "memory");
            Dictionary<string, object> sharedConversationPayload = new Dictionary<string, object>(payload,
                StringComparer.OrdinalIgnoreCase) { ["playerText"] = sharedPlayerText };
            DialogueService.UpdateSharedGroupConversationState(campaignId, sharedConversationPayload, heroId, heroName, sharedReply,
                FirstNonEmpty(partySessionId, DialogueService.GroupConversationSessionId(payload), eventId), ts, decisionId);
            Dictionary<string, object> motiveOutcome = participation == "quiet"
                ? TestDict("status", "intentional_silence_excluded")
                : DialogueService.ApplyConversationMotiveOutcome(campaignId, motiveDecision, payload, reply, intent, decisionId, decisionBrief);
            if (stateUpdates.Count > 0)
            {
                ActionPipelineService.MergeCharacterState(campaignId, heroId, stateUpdates);
            }

            CampaignFileService.AppendJsonLineToPath(CampaignFileService.CharacterFile(campaignId, heroId, "history", "decisions.jsonl"), new Dictionary<string, object>
            {
                ["id"] = decisionId,
                ["ts"] = ts,
                ["campaignId"] = campaignId,
                ["eventId"] = eventId,
                ["templateId"] = ReadString(payload, "templateId", ""),
                ["phaseId"] = ReadString(payload, "phaseId", ""),
                ["heroStringId"] = heroId,
                ["playerName"] = playerName,
                ["heroName"] = heroName,
                ["playerText"] = playerText,
                ["turnType"] = approachOpening ? "npc_approach_opening" : "player_reply",
                ["turnId"] = ReadString(payload, "turnId", ""),
                ["participation"] = participation,
                ["reactionTargetHeroStringId"] = reactionTargetHeroStringId,
                ["reply"] = reply,
                ["privateAudienceHeroStringId"] = privatePlayerInputForSpeaker ? heroId : string.Empty,
                ["decisionBrief"] = decisionBrief,
                ["motiveDecision"] = motiveDecision,
                ["motiveOutcome"] = motiveOutcome,
                ["emotion"] = emotion,
                ["intent"] = intent,
                ["relationshipSignal"] = relationshipSignal,
                ["relationshipAssessments"] = relationshipAssessments,
                ["actionGate"] = actionGate,
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
                ["queuedActions"] = queuedEventActions,
                ["actionErrors"] = eventActionErrors,
                ["identityIntroductionResults"] = identityIntroductionResults,
                ["conversationExchange"] = conversationExchange
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

            Dictionary<string, object> response = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["eventId"] = eventId,
                ["heroStringId"] = heroId,
                ["reply"] = reply,
                ["privateAudienceHeroStringId"] = privatePlayerInputForSpeaker ? heroId : string.Empty,
                ["participation"] = participation,
                ["reactionTargetHeroStringId"] = reactionTargetHeroStringId,
                ["identityView"] = identityView,
                ["emotion"] = emotion,
                ["intent"] = intent,
                ["relationshipSignal"] = relationshipSignal,
                ["relationshipAssessments"] = relationshipAssessments,
                ["decisionBrief"] = decisionBrief,
                ["motiveDecision"] = motiveDecision,
                ["motiveOutcome"] = motiveOutcome,
                ["actionGate"] = actionGate,
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
                ["queuedActions"] = queuedEventActions,
                ["actionErrors"] = eventActionErrors,
                ["identityIntroductionResults"] = identityIntroductionResults,
                ["conversationExchange"] = conversationExchange,
                ["selectedContextPulls"] = selectedContextPulls,
                ["contextBundles"] = contextBundles,
                ["construction"] = construction,
                ["encyclopediaText"] = encyclopediaText,
                ["timing"] = timing
            };
            OperationsService.LogOperational("social_event.respond", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["eventId"] = eventId,
                ["heroStringId"] = heroId,
                ["decisionId"] = decisionId,
                ["playerChars"] = playerText.Length,
                ["replyChars"] = reply.Length,
                ["decisionBrief"] = decisionBrief,
                ["emotion"] = emotion,
                ["intent"] = intent,
                ["relationshipSignal"] = relationshipSignal,
                ["actionGate"] = actionGate,
                ["memoryWriteCount"] = memoryWrites.Count,
                ["dynamicCharacteristicWriteCount"] = dynamicCharacteristicWrites.Count,
                ["dynamicCharacteristicStoredCount"] = ReadInt(dynamicCharacteristicsStore, "storedCount", 0),
                ["recallConsistencyRepairCount"] = recallConsistencyRepairs.Count,
                ["stateUpdateCount"] = stateUpdates.Count,
                ["suggestedActionCount"] = suggestedActions.Count,
                ["queuedActionCount"] = queuedEventActions.Count,
                ["actionErrors"] = eventActionErrors,
                ["selectedContextPulls"] = selectedContextPulls.Select(x => ReadString(x, "id", "")).ToList(),
                ["contextBundleCount"] = contextBundles.Count,
                ["contextBundleChars"] = Json.Serialize(contextBundles).Length,
                ["timing"] = timing
            });
            OperationsService.WriteAudit(campaignId, correlationId, "server", auditMode, auditMode + ".response", heroId, "", eventId, "completed", totalTimer.ElapsedMilliseconds, "Event response completed.", new Dictionary<string, object>
            {
                ["decisionId"] = decisionId,
                ["playerLine"] = playerLine,
                ["npcLine"] = npcLine,
                ["reply"] = reply,
                ["decisionBrief"] = decisionBrief,
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
                ["conversationExchange"] = conversationExchange,
                ["timing"] = timing
            });
            DialogueService.ScheduleReplayCorpusCapture(campaignId, correlationId);

            LlmProviderService.RecordCodexFinalizedContract(parsed, response);
            return response;
        }

        // Live dialogue, event/party turns and isolated replay must use the same
        // sampling, output budget and envelope metadata. Route-specific prompt
        // preparation and the live repair pipeline remain at their call sites.
        internal static Dictionary<string, object> BuildConversationGenerationRequest(
            Dictionary<string, object> settings, DialogueService.PromptEnvelope envelope, string campaignId,
            string correlationId, string heroId, string playerText, bool socialEvent, string eventId = "")
        {
            var request = new Dictionary<string, object>
            {
                ["requestType"] = socialEvent ? "social_event" : "dialogue",
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["heroStringId"] = heroId,
                ["playerText"] = playerText,
                ["messages"] = envelope.Messages,
                ["promptEnvelope"] = envelope.Diagnostics,
                ["promptCacheEligible"] = true,
                ["privateDrinkActionsRequired"] = ReadDictionary(envelope.Diagnostics, "drinkPromptBudget") != null,
                // Structured replies carry visible prose and private metadata;
                // a small first-call budget causes avoidable full-prompt repairs.
                ["maxTokens"] = socialEvent ? StructuredEventResponseMaxTokens(settings) : StructuredDialogueResponseMaxTokens(settings),
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            };
            if (socialEvent) request["eventId"] = eventId;
            else request["temperature"] = Math.Min(0.5d, ReadDouble(settings, "temperature", 0.7d));
            return request;
        }

        internal static int StructuredEventResponseMaxTokens(Dictionary<string, object> settings)
        {
            return Math.Max(16000, ReadInt(settings, "maxTokens", 900));
        }

        internal static int StructuredDialogueResponseMaxTokens(Dictionary<string, object> settings)
        {
            return Math.Max(16000, ReadInt(settings, "maxTokens", 900));
        }

        internal static Dictionary<string, object> PartyChatRespond(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            var floorResult = DialogueService.ApplyGroupConversationFloorToSpeaker(payload);
            if (floorResult != null) return floorResult;
            bool familyChambers = ReadBool(payload, "familyChambers", false);
            if (ReadBool(ReadDictionary(payload, "speaker"), "isChild", false))
            {
                return CourtService.FamilyChambersChildRespond(payload);
            }
            if (familyChambers)
            {
                List<Dictionary<string, object>> familyAttendees = ReadDictionaryList(payload, "attendees");
                List<Dictionary<string, object>> children = familyAttendees.Where(item => ReadBool(item, "isChild", false)).ToList();
                List<Dictionary<string, object>> adults = familyAttendees.Where(item => !ReadBool(item, "isChild", false)).ToList();
                HashSet<string> childIds = new HashSet<string>(
                    children.Select(CampaignFileService.CharacterIdFrom).Where(id => !string.IsNullOrWhiteSpace(id)),
                    StringComparer.OrdinalIgnoreCase);
                payload["attendees"] = adults;
                payload["activeHeroIds"] = adults.Select(CampaignFileService.CharacterIdFrom).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
                payload["participants"] = adults.Select(CampaignFileService.CharacterIdFrom).Where(id => !string.IsNullOrWhiteSpace(id))
                    .Concat(new[] { ReadString(payload, "playerHeroStringId", "") }).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                payload["sceneParticipants"] = ReadDictionaryList(payload, "sceneParticipants")
                    .Where(item => !ReadBool(item, "isChild", false) && !childIds.Contains(CampaignFileService.CharacterIdFrom(item))).ToList();
                payload["actionResolutionIndex"] = new Dictionary<string, object>();
                payload["sceneContext"] = ReadString(payload, "sceneContext", "")
                    + "\nFAMILY CHILD WITNESSES (prompt context only; never upsert, construct, assess, target, or mutate through adult systems): " + Json.Serialize(children)
                    + "\nAdult dialogue may acknowledge the listed children naturally, but must not emit relationship assessments, reputation, traits, romance, politics, punishment, combat, or actions for a child.";
            }
            payload["mode"] = "party_chat";
            if (string.IsNullOrWhiteSpace(ReadFirstString(payload, "eventId", "socialEventId")))
            {
                payload["eventId"] = "party_chat";
            }

            if (string.IsNullOrWhiteSpace(ReadString(payload, "templateId", "")))
            {
                payload["templateId"] = "party_chat";
            }

            if (string.IsNullOrWhiteSpace(ReadString(payload, "displayName", "")))
            {
                payload["displayName"] = "Party Chat";
            }

            bool castleOpening = ReadBool(payload, "castleOpening", false);
            bool npcOpening = castleOpening || ReadBool(payload, "approachOpening", false)
                || string.Equals(ReadString(payload, "turnType", ""), "npc_approach_opening", StringComparison.OrdinalIgnoreCase);
            string sceneContext = ReadString(payload, "sceneContext", "");
            if (string.IsNullOrWhiteSpace(sceneContext))
            {
                sceneContext = "Conversation mode: party chat. Multiple selected NPCs may be present and may hear the reply.";
            }

            payload["sceneContext"] = sceneContext
                + "\nServer mode: party_chat. The speaker must only speak as themselves, while staying aware this is a group conversation."
                + (npcOpening
                    ? "\nCURRENT-BEAT PRIORITY: The player has entered but has not spoken. Initiate naturally; do not answer, acknowledge, quote, or infer a player message. Every prior labeled line belongs to that NPC, never to the player."
                    : "\nCURRENT-BEAT PRIORITY: Consider the latest player contribution and current scene before extending an earlier topic. If it asks you a question, answer naturally when warranted; an observable action may instead invite your initiative. Prior NPC lines are context to engage, not a replacement question. When the player asks for current or canonical facts, use only the selected live bundles; if a requested list or fact is absent, say it is unavailable instead of improvising local lore.")
                + "\nRECALL GROUNDING: A plausible story is not evidence of an already established memory. Never claim a past event was previously discussed, externally verified, or part of supplied recall evidence unless it is present in the attributed transcript, selected memory/history evidence, or character foundation. The NPC may originate a new, low-impact autobiographical detail that is compatible with native facts and their traits; present it as a new disclosure and emit the required dynamicCharacteristicWrites entry so it becomes stable soft character canon. Never invent named relatives, major offices, ownership, wars, crimes, deaths, campaign outcomes, or facts about another person. Preserve evidence granularity and clearly label hypothetical examples as hypothetical."
                + (ReadInt(payload, "partySpeakerIndex", 0) > 0
                    ? " Earlier response opportunities may have been quiet. Only actual attributed contributions establish prior speech. Engage a specific prior contribution when relevant and set reactionTargetHeroStringId to that NPC's supplied id; otherwise make your own warranted contribution or remain quiet."
                    : npcOpening
                        ? " This is an opportunity to initiate the room encounter naturally if warranted, without implying that the player spoke. Quiet is also valid."
                        : " You have the first speaking opportunity this group beat. Answer naturally if appropriate; a brief response or quiet participation is valid.");

            string castlePrompt = ReadString(payload, "castleDialoguePrompt", "");
            Dictionary<string, object> partyVisitorContext = ReadDictionary(payload, "nobleVisitorContext");
            if (partyVisitorContext != null)
                payload["sceneContext"] = ReadString(payload, "sceneContext", "") + "\n" + CourtService.BuildNobleVisitorPrompt(
                    ReadString(payload, "campaignId", "default"), ReadString(payload, "speakerHeroStringId", ""), partyVisitorContext);
            ApplyCastleChatMode(payload, castlePrompt);

            Dictionary<string, object> response = SocialEventRespond(payload);
            response["mode"] = string.IsNullOrWhiteSpace(castlePrompt) ? "party_chat" : "castle_chat";
            if (familyChambers && ReadBool(response, "ok", false)) CourtService.ProcessFamilyReconciliationReply(payload, response);
            return response;
        }

        internal static bool ApplyCastleChatMode(
            Dictionary<string, object> payload,
            string castlePrompt)
        {
            if (payload == null || string.IsNullOrWhiteSpace(castlePrompt))
            {
                return false;
            }

            bool castleOpening = ReadBool(payload, "castleOpening", false);
            payload["mode"] = "castle_chat";
            payload["displayName"] = ReadString(payload, "castleRoomName", "Castle Chat");
            if (castleOpening)
            {
                // SocialEventRespond already has a first-speaker contract that
                // permits an empty playerText. Bridge Castle Chat onto that
                // established path instead of fabricating a player utterance.
                payload["approachOpening"] = true;
                payload["turnType"] = "npc_approach_opening";
            }
            payload["sceneContext"] = ReadString(payload, "sceneContext", "")
                + "\nCASTLE ROOM PROMPT SNAPSHOT (authoritative for this persisted room session):\n" + castlePrompt
                + "\nCASTLE OPENING CONTRACT: " + (castleOpening
                    ? "This is an NPC opening turn. There is no preceding player statement. Speak naturally on noticing the ruler enter; do not fabricate, answer, acknowledge, quote, or infer a player line. If another NPC has already spoken, their labeled words remain that NPC's contribution and are not a petition or statement from the ruler."
                    : "This is a continued Castle Chat turn using the normal party-chat transcript and action/memory rules.");
            return true;
        }

        internal static Dictionary<string, object> GeneratedWildernessPlan(Dictionary<string, object> payload)
        {
            Stopwatch totalTimer = Stopwatch.StartNew();
            Stopwatch phaseTimer = Stopwatch.StartNew();
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            List<Dictionary<string, object>> participants = ReadDictionaryList(payload, "participants")
                .Where(participant => !string.IsNullOrWhiteSpace(CampaignFileService.CharacterIdFrom(participant)) && CampaignFileService.CharacterIdFrom(participant).Length <= 160)
                .GroupBy(CampaignFileService.CharacterIdFrom, StringComparer.Ordinal).Select(group => group.First())
                .Take(WorldSimulationService.WildernessParticipantLimit).ToList();
            if (participants.Count == 0)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "No eligible wilderness participants." };
            string[] direction = WorldSimulationService.SelectWildernessDirection(payload);
            string initiatorId = CampaignFileService.CharacterIdFrom(participants[RandomNumberGenerator.GetInt32(participants.Count)]);
            payload["wildernessInitiatorHeroStringId"] = initiatorId;
            // Passive planning cannot materialize or construct a dynamic character.
            // Their first interactive response performs construction synchronously.
            long constructionMs = 0;

            phaseTimer.Restart();
            string prompt = BuildGeneratedWildernessPrompt(campaignId, payload, participants, direction);
            DialogueService.PromptEnvelope promptEnvelope = DialogueService.BuildSimplePromptEnvelope("generated_wilderness_event", "opening", "You create grounded Mount & Blade II: Bannerlord social role-play event openings. Output only a valid JSON object.", prompt);
            List<Dictionary<string, object>> messages = promptEnvelope.Messages;
            long promptBuildMs = phaseTimer.ElapsedMilliseconds;

            phaseTimer.Restart();
            Dictionary<string, object> wildernessLlmRequest = new Dictionary<string, object>
            {
                ["requestType"] = "generated_wilderness_event",
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["messages"] = messages,
                ["promptEnvelope"] = promptEnvelope.Diagnostics,
                ["promptCacheEligible"] = true,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            };
            Dictionary<string, object> llm = LlmProviderService.ChatWithLlm(wildernessLlmRequest);
            llm = ConversationResponseService.WithDiagnosticTransformation("Answer structure and format repair", llm, () => ConversationResponseService.RetryMalformedStructuredResponse(llm, wildernessLlmRequest, campaignId, correlationId, "generated_wilderness_event", "", ""));
            long llmMs = phaseTimer.ElapsedMilliseconds;
            if (!ReadBool(llm, "ok", false))
            {
                totalTimer.Stop();
                Dictionary<string, object> failedTiming = new Dictionary<string, object>
                {
                    ["totalMs"] = totalTimer.ElapsedMilliseconds,
                    ["characterConstructionMs"] = constructionMs,
                    ["promptBuildMs"] = promptBuildMs,
                    ["llmCallMs"] = llmMs,
                    ["llmProviderMs"] = ReadLong(llm, "durationMs", 0)
                };
                OperationsService.LogOperational("generated_wilderness.plan_failed", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["correlationId"] = correlationId,
                    ["varietyKey"] = direction[0],
                    ["stage"] = "llm",
                    ["timing"] = failedTiming,
                    ["llm"] = llm
                });
                return new Dictionary<string, object> { ["ok"] = false, ["stage"] = "llm", ["error"] = ReadString(llm, "error", "LLM call failed."), ["timing"] = failedTiming };
            }

            phaseTimer.Restart();
            string content = ReadString(llm, "content", "");
            Dictionary<string, object> parsed = TryParseJsonObject(content) ?? new Dictionary<string, object>();
            string title = FirstNonEmpty(ReadFirstString(parsed, "title", "event_title"), "Something Ahead");
            string approach = ReadFirstString(parsed, "approachDescription", "approach_description", "approach");
            string opening = ReadFirstString(parsed, "openingText", "opening_text", "event_opening_text", "opening");
            string playerHook = ReadFirstString(parsed, "playerHook", "player_hook", "hook");
            string externalHeroId = ReadFirstString(parsed, "externalHeroId", "external_hero_id", "outside_hero_id", "outside_npc_id");
            string externalContext = ReadFirstString(parsed, "externalContext", "external_context", "outside_context");
            string terrainKey = ReadString(payload, "terrainKey", "plain");
            string emotionalPressure = ReadFirstString(parsed, "emotionalPressure", "emotional_pressure", "pressure");
            string surfaceClues = FirstNonEmpty(StringListText(ReadStringList(parsed, "surfaceClues")), StringListText(ReadStringList(parsed, "surface_clues")), ReadFirstString(parsed, "surfaceClues", "surface_clues"));
            string hiddenContext = ReadFirstString(parsed, "hiddenContext", "hidden_context", "private_context", "hidden_depth");
            string discoveryRoutes = FirstNonEmpty(StringListText(ReadStringList(parsed, "discoveryRoutes")), StringListText(ReadStringList(parsed, "discovery_routes")), ReadFirstString(parsed, "discoveryRoutes", "discovery_routes", "how_to_discover"));
            List<string> participantIds = ReadStringList(parsed, "participantHeroIds");
            if (participantIds.Count == 0)
            {
                participantIds = ReadStringList(parsed, "participant_ids");
            }

            if (participantIds.Count == 0)
            {
                participantIds = participants.Select(CampaignFileService.CharacterIdFrom).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            }
            participantIds = participantIds.Where(id => participants.Any(participant => CampaignFileService.CharacterIdFrom(participant) == id)).Distinct().ToList();
            // The prompt's selected initiator is authoritative, not a model guess.
            if (!participantIds.Contains(initiatorId)) participantIds.Insert(0, initiatorId);
            // The client supplies authority for any outside participant; a model
            // must not add a new person to the encounter.
            externalHeroId = ReadString(payload, "externalHeroId", "");
            externalContext = ReadString(payload, "externalContext", "");

            long parseMs = phaseTimer.ElapsedMilliseconds;
            totalTimer.Stop();
            Dictionary<string, object> timing = new Dictionary<string, object>
            {
                ["totalMs"] = totalTimer.ElapsedMilliseconds,
                ["characterConstructionMs"] = constructionMs,
                ["promptBuildMs"] = promptBuildMs,
                ["llmCallMs"] = llmMs,
                ["llmProviderMs"] = ReadLong(llm, "durationMs", 0),
                ["parseMs"] = parseMs
            };

            Dictionary<string, object> response = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["varietyKey"] = direction[0],
                ["title"] = LimitText(title, 120),
                ["approachDescription"] = LimitText(approach, 1200),
                ["openingText"] = LimitText(opening, 1600),
                ["playerHook"] = LimitText(playerHook, 700),
                ["participantHeroIds"] = participantIds,
                ["initiatorHeroStringId"] = initiatorId,
                ["externalHeroId"] = LimitText(externalHeroId, 120),
                ["externalContext"] = LimitText(externalContext, 700),
                ["terrainKey"] = LimitText(terrainKey, 80),
                ["emotionalPressure"] = LimitText(emotionalPressure, 200),
                ["surfaceClues"] = LimitText(surfaceClues, 1200),
                ["hiddenContext"] = LimitText(hiddenContext, 1600),
                ["discoveryRoutes"] = LimitText(discoveryRoutes, 1200),
                ["timing"] = timing
            };
            string rejection = WorldSimulationService.WildernessOpeningRejection(response, payload, participants);
            if (!string.IsNullOrEmpty(rejection))
            {
                OperationsService.LogOperational("generated_wilderness.plan_rejected", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["correlationId"] = correlationId,
                    ["varietyKey"] = direction[0], ["reason"] = rejection, ["timing"] = timing
                });
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["error"] = "Wilderness opening rejected: " + rejection,
                    ["errorCode"] = rejection, ["timing"] = timing
                };
            }
            OperationsService.LogOperational("generated_wilderness.plan", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["varietyKey"] = direction[0],
                ["promptCharacters"] = prompt.Length,
                ["recentEventCount"] = WorldSimulationService.WildernessRecentEvents(payload).Count,
                ["participantCount"] = participants.Count,
                ["title"] = response["title"],
                ["timing"] = timing
            });
            return response;
        }

        internal static string BuildGeneratedWildernessPrompt(string campaignId, Dictionary<string, object> payload,
            List<Dictionary<string, object>> participants, string[] direction = null,
            Func<string, Dictionary<string, object>, Dictionary<string, object>> loadPassiveStack = null)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Create one Bannerlord wilderness role-play event opening for the player's traveling party.");
            builder.AppendLine("Output only valid JSON. This is a social/emotional opening, not a quest.");
            builder.AppendLine("No combat, ambushes, loot, injuries, arrests, forced romance, marriage obligations, settlement effects, party movement, or campaign consequences.");
            builder.AppendLine("Do not write dialogue or feelings for the player. Do not invent extra named NPCs. Use only listed participants and the player character.");
            builder.AppendLine();
            builder.AppendLine("Player display name: " + LimitText(ReadString(payload, "playerName", "Player"), 120));
            builder.AppendLine("Terrain key: " + LimitText(ReadString(payload, "terrainKey", "plain"), 80));
            builder.AppendLine("Location text: " + LimitText(ReadString(payload, "locationText", "on the road"), 400));
            builder.AppendLine("Time of day: " + LimitText(ReadString(payload, "timeOfDayText", "unknown time of day"), 120));
            builder.AppendLine("External hero id: " + LimitText(ReadString(payload, "externalHeroId", ""), 160));
            builder.AppendLine("External context: " + LimitText(ReadString(payload, "externalContext", ""), 700));
            builder.AppendLine();
            WorldSimulationService.AppendWildernessVarietyPrompt(builder, payload, direction ?? WorldSimulationService.SelectWildernessDirection(payload));
            var openingParticipants = (participants ?? new List<Dictionary<string, object>>()).Take(WorldSimulationService.WildernessParticipantLimit).ToList();
            if (openingParticipants.Count > 0)
                builder.AppendLine("Initiating participant id: " + LimitText(
                    openingParticipants.Any(p => CampaignFileService.CharacterIdFrom(p) == ReadString(payload, "wildernessInitiatorHeroStringId", ""))
                        ? ReadString(payload, "wildernessInitiatorHeroStringId", "") : CampaignFileService.CharacterIdFrom(openingParticipants[0]), 160)
                    + ". Give this person the initiating action or question; other listed people may react. Adapt the direction to their established character rather than forcing a new trait.");
            builder.AppendLine("Participants:");
            foreach (Dictionary<string, object> participant in openingParticipants)
            {
                string heroId = CampaignFileService.CharacterIdFrom(participant);
                Dictionary<string, object> stack = loadPassiveStack != null
                    ? loadPassiveStack(heroId, participant)
                    : CharactersService.LoadPassiveCharacterStack(campaignId, heroId, participant);
                builder.AppendLine("- " + Json.Serialize(WorldSimulationService.WildernessCharacterBrief(stack, participant)));
            }

            builder.AppendLine();
            builder.AppendLine("Rules:");
            builder.AppendLine("- Native gender and pronouns in each participant brief are authoritative. Use she/her for female participants, he/him for male participants, or they/them when unknown. Names, occupation Lord, personality and earlier event wording never override native identity.");
            builder.AppendLine("- Time of day must affect lighting, visibility, fatigue, privacy, or travel routine.");
            builder.AppendLine("- If terrain_key is water, frame the scene around travel by water.");
            builder.AppendLine("- approach_description shows the concrete incident and what makes it worth engaging with.");
            builder.AppendLine("- opening_text continues that incident and creates a specific opportunity to join in; do not assume the player has moved, spoken or acted.");
            builder.AppendLine("- player_hook gives the player room to respond without forcing their words or thoughts.");
            builder.AppendLine("- hidden_context is private GM-only depth. Do not reveal it directly in approach_description or opening_text.");
            builder.AppendLine("- Keep stakes personal and social.");
            builder.AppendLine();
            builder.AppendLine("JSON schema:");
            builder.AppendLine("{");
            builder.AppendLine("  \"title\": \"short event title\",");
            builder.AppendLine("  \"approach_description\": \"2-4 sentences showing a concrete incident\",");
            builder.AppendLine("  \"opening_text\": \"2-5 sentences continuing the distinct activity and social situation\",");
            builder.AppendLine("  \"player_hook\": \"1-2 sentences giving the player room to respond\",");
            builder.AppendLine("  \"participant_ids\": [\"hero_id_from_list\"],");
            builder.AppendLine("  \"external_hero_id\": \"outside hero id or empty string\",");
            builder.AppendLine("  \"external_context\": \"why the outside NPC is plausibly nearby, or empty string\",");
            builder.AppendLine("  \"terrain_key\": " + Json.Serialize(LimitText(ReadString(payload, "terrainKey", "plain"), 80)) + ",");
            builder.AppendLine("  \"emotional_pressure\": \"one concise pressure\",");
            builder.AppendLine("  \"surface_clues\": [\"visible clue, oddity, emotional tell, or contradiction\"],");
            builder.AppendLine("  \"hidden_context\": \"private depth explaining what is really going on\",");
            builder.AppendLine("  \"discovery_routes\": [\"how the player can uncover that depth through conversation\"]");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static string StringListText(List<string> values)
        {
            return values == null || values.Count == 0 ? string.Empty : string.Join("; ", values);
        }

        internal static string LimitText(string text, int max)
        {
            text = (text ?? string.Empty).Trim();
            if (max <= 0 || text.Length <= max)
            {
                return text;
            }

            return text.Substring(0, max).TrimEnd();
        }
    }
}
