using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private const int PromptLayoutVersion = 14;
        private static readonly object PromptCacheStateLock = new object();
        private static readonly Dictionary<string, DateTime> PromptCacheCooldowns = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, PromptCacheAggregate> PromptCacheAggregates = new Dictionary<string, PromptCacheAggregate>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> ReasoningFallbackCooldowns = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        internal static readonly HashSet<string> SelectiveReasoningRequestTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dialogue",
            "dialogue_answer_support",
            "social_event",
            "party_chat",
            "correspondence",
            "relationship",
            "diplomacy",
            "strategy",
            "action",
            "actions",
            "action_planner",
            "character_construction",
            "generated_wilderness_event",
            "memory"
        };
        private static readonly Dictionary<string, HashSet<string>> VersionTwoReasoningPromptHashes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dialogue_system.txt"] = new HashSet<string>(new[]
            {
                "C31510EEA7377E3FB6FFA3B40CDB230BDF727672033F94922B211F96E0C3B4B6",
                "0AFBF9F928BC0027D1328777483DB90EDA6A52D2EE24CB00E7AC2715D75667E5",
                // Stock template immediately before exact current-conduct evidence was added.
                "33DCF1D88AA5F99DF1087B3049B8F16486AFF610BE7C0F9D43D19B3EF4C2ED67",
                "241AC0A54DBB53DE8942A2F3A8F469529E8E461659AB8EF7DFAD1027FE4F521F",
                "3816B5DE6402F54A5D86217CA04000E87A4D3E2BC366D59295CA8EB2496800DF" // Stock before separate continuity-write allowance.
            }, StringComparer.OrdinalIgnoreCase),
            ["dialogue_internal_posture.txt"] = new HashSet<string>(new[] { "F2D0FC3425273FBFE3397BEFA7464F9C1B14049A811984DFD7DAAC31C49BFE26", "DD570337581C94AC0F60692B862AAA11BA5ED71A0119895A8C45C28F05BB6752" }, StringComparer.OrdinalIgnoreCase),
            ["dialogue_output_schema.json"] = new HashSet<string>(new[]
            {
                "749EBE78DF0440017E60ECAE42B1FC3B3355539633311899E8D4357E1CB8CBB1",
                "AE0198B3F74F5007D8AB526FC8521460CD6B00A58D462F03025A3BC25DEBD880",
                "8170C07A4414E1B7047BECA1D546EF12EDBFF93AD1CC2D2A3BEE4851000F3276",
                "2666B10B6435DE2A17533A1EE86F58D05EC6BAD734ECED74D018F1DF965ACB08"
            }, StringComparer.OrdinalIgnoreCase),
            ["event_system.txt"] = new HashSet<string>(new[]
            {
                "3D67FC3F615D311630D117F1430BA286FB279BBB2C59BE89F2DA88F587D3C462",
                "75F8932989F5502A0E32C6DA234BDE780271D7DA44CBCA18F82B9B0C59B003A9",
                "9CB99E0F46106FC94991CDEC8A748702DA6CD83C2BE02087446CADD7ACF20B73"
            }, StringComparer.OrdinalIgnoreCase),
            ["event_output_schema.json"] = new HashSet<string>(new[]
            {
                "E7A3D2F75940894F562E5B6C982B2E6C1AAA42B402C6216A1BAB1B8B2BEA0B9B",
                "CDE49E0DA270937A85ACE13AD0FEBBF6FFB7A36EB17A525A258D5896739D429F",
                "F61F6592BA7D02C0E65C8E2A0D693DBB6DC590D5B03622C5CD8A55F9CBB33A84",
                "4F83DF36711F60687DE29DE0A02AD66DF6646328702ECB6260D9FD8D3DFE6C89",
                "73E98CB554427F085594C1F3BEAD30DA77B611955CDD709F5493DD4A02C33C3A"
            }, StringComparer.OrdinalIgnoreCase),
            ["correspondence_system.txt"] = new HashSet<string>(new[]
            {
                // Stock correspondence schema immediately before secret-preparation actionGate support.
                "BE1CE77EFDC712E0B848115B7260A5200929FCE16260A430F43E2F9E93B94E65"
            }, StringComparer.OrdinalIgnoreCase)
        };

        internal sealed class PromptEnvelope
        {
            public List<Dictionary<string, object>> Messages { get; set; } = new List<Dictionary<string, object>>();
            public Dictionary<string, object> Diagnostics { get; set; } = new Dictionary<string, object>();
            public int TotalChars { get; set; }
        }

        internal sealed class PromptLiveTurnBudgetResult
        {
            public string LiveTurn = "";
            public ConversationTemplateSlots Structure;
            public string ContextPullText = "";
            public string CanonicalTranscript = "";
            public string NpcRelationshipBlock = "";
            public Dictionary<string, object> Diagnostics = new Dictionary<string, object>();
        }

        private sealed class PromptCacheAggregate
        {
            public string RequestType;
            public string Model;
            public long Calls;
            public long EligibleCalls;
            public long RoutedCalls;
            public long FallbackCalls;
            public long UsageReportedCalls;
            public long HitCalls;
            public long PromptTokens;
            public long CompletionTokens;
            public long CachedTokens;
            public long CacheReadTokens;
            public long CacheWriteTokens;
            public long DurationMs;
            public string LastError = "";
            public string LastSuccessUtc = "";
            public Dictionary<string, object> LastPrefix = new Dictionary<string, object>();
        }

        internal static PromptEnvelope CreatePromptEnvelope(string requestType, string variant, string globalPrefix, string characterPrefix, string liveTurn)
        {
            globalPrefix = NormalizePromptSegment(globalPrefix);
            characterPrefix = NormalizePromptSegment(characterPrefix);
            liveTurn = NormalizePromptSegment(liveTurn);
            List<Dictionary<string, object>> messages = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["role"] = "system", ["content"] = globalPrefix }
            };
            if (!string.IsNullOrWhiteSpace(characterPrefix))
            {
                messages.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = characterPrefix });
            }
            messages.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = liveTurn });

            string globalHash = PromptHash(globalPrefix);
            string characterHash = PromptHash(characterPrefix);
            Dictionary<string, object> diagnostics = new Dictionary<string, object>
            {
                ["layoutVersion"] = PromptLayoutVersion,
                ["requestType"] = requestType ?? "",
                ["variant"] = variant ?? "default",
                ["cacheEligible"] = IsPromptCacheEligibleRequestType(requestType),
                ["globalPrefixVariant"] = (requestType ?? "") + ":" + (variant ?? "default"),
                ["characterPrefixVariant"] = string.IsNullOrWhiteSpace(characterPrefix) ? "none" : (variant ?? "default"),
                ["characterRevision"] = characterHash,
                ["globalPrefixHash"] = globalHash,
                ["globalPrefixChars"] = globalPrefix.Length,
                ["characterPrefixHash"] = characterHash,
                ["characterPrefixChars"] = characterPrefix.Length,
                ["liveTurnHash"] = PromptHash(liveTurn),
                ["liveTurnChars"] = liveTurn.Length,
                ["messageCount"] = messages.Count
            };
            return new PromptEnvelope
            {
                Messages = messages,
                Diagnostics = diagnostics,
                TotalChars = messages.Sum(x => ReadString(x, "content", "").Length)
            };
        }

        private static string AssembleConversationLiveTurn(
            string templateName,
            Dictionary<string, string> values,
            string conversationScenePrompt,
            string roleAttribution,
            string npcRelationshipBlock,
            string templateOverride = null)
        {
            string liveTurn = RenderConversationTemplate(templateOverride ?? Program.LoadPromptTemplate(templateName), values);
            if (!string.IsNullOrWhiteSpace(conversationScenePrompt))
                liveTurn = conversationScenePrompt.Trim() + "\n\n" + liveTurn;
            if (!string.IsNullOrWhiteSpace(roleAttribution))
                liveTurn = roleAttribution + "\n\n" + liveTurn;
            if (!string.IsNullOrWhiteSpace(npcRelationshipBlock))
                liveTurn = liveTurn.TrimEnd() + "\n\n" + npcRelationshipBlock;
            return liveTurn;
        }

        internal static PromptLiveTurnBudgetResult BuildBudgetedConversationLiveTurn(
            string templateName, string transcriptValueKey, Dictionary<string, string> values,
            string conversationScenePrompt, string roleAttribution, string npcRelationshipBlock,
            string globalPrefix, string characterPrefix, int targetOverride = 0, Dictionary<string, object> capacity = null)
        {
            values = values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string context = values.TryGetValue("contextPullText", out string c) ? c ?? "" : "";
            string transcript = values.TryGetValue(transcriptValueKey, out string t) ? t ?? "" : "";
            string relationship = npcRelationshipBlock ?? "";
            string initialContext = context, initialTranscript = transcript;
            var settings = Program.LoadSettings();
            // Warnings are observability settings, never semantic truncation limits.
            // One allocator accounts for every fixed section, including protected
            // relationship and agenda context. Final serialized preflight follows.
            const int finalRequestReserve = 2048;
            int inputAllowance = ReadInt(capacity, "inputAllowance", ConversationUnverifiedInputLimit);
            int target = Math.Min(targetOverride > 0 ? targetOverride : 32000 * 3, inputAllowance * 3);
            Func<string> assemble = () => AssembleConversationLiveTurn(templateName, values,
                conversationScenePrompt, roleAttribution, relationship);
            Func<string, int> total = live => (globalPrefix ?? "").Length + (characterPrefix ?? "").Length + live.Length;
            Func<string, int> serializedEstimate = live => EstimateContinuityTokens(Json.Serialize(new[] {
                TestDict("role", "system", "content", globalPrefix ?? ""),
                TestDict("role", "system", "content", characterPrefix ?? ""),
                TestDict("role", "user", "content", live) }));
            string liveTurn = assemble();
            int initialChars = total(liveTurn);
            int initialEstimate = serializedEstimate(liveTurn);
            var actions = new List<object>();
            values["contextPullText"] = "";
            values[transcriptValueKey] = "";
            int mandatory = total(assemble());
            // Expand for the protected floor before selecting optional records.
            // With verified capacity, grow for all supplied context as well;
            // an arbitrary working step must not discard fitting evidence.
            values["contextPullText"] = string.Join("\n\n", ContinuityPromptRecords(context, false).Where(r => r.Required).Select(r => r.Text));
            values[transcriptValueKey] = string.Join("\n\n", ContinuityPromptRecords(transcript, true).Where(r => r.Required).Select(r => r.Text));
            int protectedEstimate = serializedEstimate(assemble());
            values["contextPullText"] = "";
            values[transcriptValueKey] = "";
            if (targetOverride <= 0)
            {
                // Select the working step using the same metadata reserve as the
                // serialized allocator. Otherwise a 30-32k prompt fits this check
                // but loses selected evidence immediately afterward at 29,952.
                int desired = initialChars > target || initialEstimate + finalRequestReserve > 32000 ? 48000 : 32000;
                if (ReadBool(capacity, "capacityVerified", false))
                    desired = Math.Max(desired, Math.Max((initialChars + 2) / 3,
                        SelectConversationInputStep(initialEstimate + finalRequestReserve)));
                desired = Math.Max(desired, SelectConversationInputStep(protectedEstimate + finalRequestReserve));
                target = Math.Min(inputAllowance, desired) * 3;
            }
            int available = Math.Max(0, target - mandatory);
            // Required whole exchanges and exact topical evidence survive even when
            // the ordinary allowance is full. The final preflight expands or refuses
            // before a provider call; it never silently drops a required source.
            var selected = SelectSharedPromptRecords(context, transcript, available, actions);
            context = selected.Context;
            transcript = selected.Transcript;
            values["contextPullText"] = context;
            values[transcriptValueKey] = transcript;
            liveTurn = assemble();
            // Character allocation alone misses Unicode and JSON escaping. Trim
            // optional whole records against the serialized messages as well,
            // leaving room for final overlays and provider request metadata. The
            // exact final request is still checked after every adapter runs.
            int tokenTarget = Math.Min(inputAllowance, target / 3);
            int messageAllowance = Math.Max(0, tokenTarget - finalRequestReserve);
            int finalEstimate = serializedEstimate(liveTurn);
            var contextRecords = ContinuityPromptRecords(context, false);
            var transcriptRecords = ContinuityPromptRecords(transcript, true);
            var optionalRecords = OrderPromptEvidenceRecords(contextRecords.Select(r => (Record: r, Section: "selectedContextAndMemory"))
                .Concat(transcriptRecords.Select(r => (Record: r, Section: "canonicalTranscript"))))
                .Where(item => !item.Record.Required).Reverse().ToList();
            foreach (var item in optionalRecords)
            {
                if (finalEstimate <= messageAllowance) break;
                (item.Section == "canonicalTranscript" ? transcriptRecords : contextRecords).Remove(item.Record);
                foreach (var decision in actions.OfType<Dictionary<string, object>>().Where(d =>
                    ReadString(d, "section", "") == item.Section && ReadString(d, "sourceId", "") == item.Record.SourceId))
                {
                    decision["retained"] = false;
                    decision["reason"] = "optional_whole_record_exceeds_serialized_token_allowance";
                }
                context = string.Join("\n\n", contextRecords.OrderBy(r => r.Order).Select(r => r.Text));
                transcript = string.Join("\n\n", transcriptRecords.OrderBy(r => r.Order).Select(r => r.Text));
                values["contextPullText"] = context;
                values[transcriptValueKey] = transcript;
                liveTurn = assemble();
                finalEstimate = serializedEstimate(liveTurn);
            }
            var structure = CreateConversationTemplateSlots(Program.LoadPromptTemplate(templateName), transcriptValueKey);
            structure.MarkedLiveTurn = AssembleConversationLiveTurn(templateName, values,
                conversationScenePrompt, roleAttribution, relationship, structure.Template);
            if (NormalizePromptSegment(structure.WithoutMarkers(structure.MarkedLiveTurn)) != NormalizePromptSegment(liveTurn))
                throw new InvalidOperationException("Conversation slot projection changed the original template content.");
            return new PromptLiveTurnBudgetResult
            {
                LiveTurn = liveTurn, Structure = structure, ContextPullText = context, CanonicalTranscript = transcript,
                NpcRelationshipBlock = relationship,
                Diagnostics = new Dictionary<string, object>
                {
                    ["allocator"] = "shared_whole_records_v3", ["warningCharacterLimit"] = ReadInt(settings, "promptWarningCharacterLimit", 100000),
                    ["targetCharacterLimit"] = target, ["initialCharacters"] = initialChars,
                    ["finalCharacters"] = total(liveTurn), ["savedCharacters"] = initialChars - total(liveTurn),
                    ["compacted"] = initialContext != context || initialTranscript != transcript,
                    ["allocationPassRan"] = true,
                    ["contentReduced"] = total(liveTurn) < initialChars,
                    ["targetMet"] = total(liveTurn) <= target && finalEstimate <= messageAllowance, ["mandatoryCharacters"] = mandatory,
                    ["inputTokenEstimate"] = finalEstimate, ["targetInputTokens"] = tokenTarget,
                    ["protectedInputTokenEstimate"] = protectedEstimate, ["inputAllowance"] = inputAllowance,
                    ["capacityRouteKey"] = ReadString(capacity, "routeKey", ""),
                    ["capacityVerified"] = ReadBool(capacity, "capacityVerified", false),
                    ["finalRequestTokenReserve"] = finalRequestReserve,
                    ["actions"] = actions, ["finalComponents"] = PromptBudgetComponentSizes(context, transcript,
                        relationship, conversationScenePrompt, roleAttribution)
                }
            };
        }
        private static Dictionary<string, object> PromptBudgetComponentSizes(
            string context, string transcript, string relationship,
            string scene, string role)
        {
            return new Dictionary<string, object>
            {
                ["selectedContextAndMemory"] = (context ?? "").Length,
                ["canonicalTranscript"] = (transcript ?? "").Length,
                ["npcRelationshipContext"] = (relationship ?? "").Length,
                ["sceneOverlay"] = (scene ?? "").Length,
                ["roleAttribution"] = (role ?? "").Length
            };
        }

        private static string CompactPromptEvidenceBlock(
            string text, int maximum, string label, bool preferNewest)
        {
            text = (text ?? "").Trim();
            if (maximum <= 0) return "";
            if (text.Length <= maximum) return text;
            string marker = "\n[Reign compacted older/lower-priority " + label
                + " evidence to preserve the live prompt budget. Retained excerpts keep their original attribution and provenance.]\n";
            if (marker.Length >= maximum)
                return Program.LimitText(marker.Trim(), maximum);

            string normalized = text.Replace("\r\n", "\n");
            var starts = Regex.Matches(normalized, @"(?m)^- \[[^\]\r\n]+\]\s+").Cast<Match>()
                .Select(x => x.Index).ToList();
            if (starts.Count >= 2)
            {
                var records = starts.Select((start, index) => normalized.Substring(start,
                    (index + 1 < starts.Count ? starts[index + 1] : normalized.Length) - start).TrimEnd()).ToList();
                int availableRecords = maximum - marker.Length;
                var kept = new List<string>();
                foreach (string record in (preferNewest ? records.AsEnumerable().Reverse() : records))
                {
                    int cost = record.Length + (kept.Count == 0 ? 0 : 1);
                    if (cost > availableRecords) continue;
                    kept.Add(record);
                    availableRecords -= cost;
                }
                if (preferNewest) kept.Reverse();
                if (kept.Count > 0)
                    return (marker.Trim() + "\n" + string.Join("\n", kept)).Trim();
            }

            if (preferNewest)
            {
                int tailBudget = maximum - marker.Length;
                string tail = text.Substring(Math.Max(0, text.Length - tailBudget));
                int firstLine = tail.IndexOf('\n');
                if (firstLine >= 0 && firstLine < Math.Min(500, tail.Length - 1))
                    tail = tail.Substring(firstLine + 1);
                return Program.LimitText(marker.TrimStart() + tail, maximum);
            }

            int available = maximum - marker.Length;
            int headBudget = (int)Math.Floor(available * 0.55d);
            int tailBudgetBalanced = Math.Max(0, available - headBudget);
            string head = text.Substring(0, Math.Min(headBudget, text.Length));
            int headLine = head.LastIndexOf('\n');
            if (headLine > Math.Max(100, head.Length / 2))
                head = head.Substring(0, headLine);
            string tailBalanced = text.Substring(Math.Max(0, text.Length - tailBudgetBalanced));
            int tailLine = tailBalanced.IndexOf('\n');
            if (tailLine >= 0 && tailLine < Math.Min(500, tailBalanced.Length - 1))
                tailBalanced = tailBalanced.Substring(tailLine + 1);
            return Program.LimitText(head.TrimEnd() + marker + tailBalanced.TrimStart(), maximum);
        }

        private static string BuildCurrentSceneProgressPrompt(List<Dictionary<string, object>> lines)
        {
            var records = new List<string>();
            foreach (var line in (lines ?? new List<Dictionary<string, object>>()).Where(l => ReadString(l, "role", "") == "npc").TakeLast(12))
            {
                string text = ReadString(line, "text", "");
                // Keep complete marked action spans, not a clipped prefix of speech.
                foreach (Match action in Regex.Matches(text, @"\*([^*]+)\*"))
                {
                    string fact = action.Groups[1].Value;
                    if (!Regex.IsMatch(fact, @"\b(?:finished|completed|cooked|roasted|baked|fried|ate|eaten|served|repaired|built|arrived|departed|drank|drained|emptied)\b", RegexOptions.IgnoreCase)
                        || Regex.IsMatch(fact, @"\b(?:not|never|would|could|might|will|shall|if|unless|before|earlier|yesterday|recall\w*|remember\w*|imagin\w*|consider\w*|pretend\w*|almost|nearly)\b|n['’]t\b", RegexOptions.IgnoreCase)) continue;
                    records.Add("- [narrated action; speaker=" + FirstNonEmpty(ReadFirstString(line, "speakerId", "speaker", "speaker_id"), "Unknown")
                        + "; source=" + FirstNonEmpty(ReadFirstString(line, "turnId", "sceneTurnId", "exchangeId"), "unknown")
                        + "; span=" + action.Groups[1].Index + ":" + fact.Length + "] " + fact);
                }
            }
            return records.Count == 0 ? "" : "\n\nCURRENT SCENE PROGRESS EVIDENCE\n"
                + "These complete source spans describe accepted narration, not confirmed native results. Preserve actors and sequence. A later explicit correction supersedes the corresponding claim.\n"
                + string.Join("\n", records.TakeLast(5));
        }

        internal static PromptEnvelope BuildDialoguePromptEnvelope(
            string campaignId,
            string heroId,
            string heroName,
            string playerName,
            string canonicalPlayerName,
            string playerText,
            string sceneContext,
            Dictionary<string, object> profile,
            Dictionary<string, object> characteristics,
            Dictionary<string, object> state,
            Dictionary<string, object> relationships,
            Dictionary<string, object> summary,
            List<Dictionary<string, object>> priorLines,
            List<Dictionary<string, object>> selectedContextPulls,
            List<Dictionary<string, object>> contextBundles,
            List<Dictionary<string, object>> actionCatalog,
            Dictionary<string, object> identityView,
            Dictionary<string, object> turnPayload,
            string precomputedMemoryPacket = null,
            string precomputedGroupConversationPrompt = null)
        {
            state = WithoutRecallAbsenceState(state, campaignId, heroId, turnPayload);
            CharactersService.EnsureObserverParticipantIdentityViews(
                campaignId, turnPayload, heroId);
            priorLines = ProjectConversationLinesForObserver(priorLines, heroId, turnPayload);
            playerText = ProjectCurrentPlayerPromptText(playerText, heroId, turnPayload);
            turnPayload["playerText"] = playerText ?? "";
            turnPayload["currentConversationCorrection"] = ActiveConversationCorrection(playerText, priorLines);
            if (UsesCastleRoomAttireContext(turnPayload))
            {
                SanitizeRoomOutfitInjection(turnPayload);
                profile = WithoutRoomOutfitInjection(profile);
                characteristics = WithoutRoomOutfitInjection(characteristics);
                contextBundles = (contextBundles ?? new List<Dictionary<string, object>>()).Select(WithoutRoomOutfitInjection).ToList();
            }
            bool officialAmbassador = string.Equals(ReadString(turnPayload, "conversationMode", ""), "ambassador_official", StringComparison.OrdinalIgnoreCase)
                || ReadBool(turnPayload, "officialMemoryFirewall", false);
            if (officialAmbassador)
            {
                relationships = new Dictionary<string, object>();
                summary = new Dictionary<string, object>();
                selectedContextPulls = (selectedContextPulls ?? new List<Dictionary<string, object>>())
                    .Where(x => !Program.ContainsAny(ReadFirstString(x, "id", "pullId"), "relevant_memory", "relationship_history")).ToList();
                contextBundles = (contextBundles ?? new List<Dictionary<string, object>>())
                    .Where(x => !Program.ContainsAny(ReadFirstString(x, "id", "pullId", "type"), "relevant_memory", "relationship_history", "private_conversation")).ToList();
            }
            Dictionary<string, object> promptPhaseTiming = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Stopwatch promptPhaseTimer = Stopwatch.StartNew();
            Dictionary<string, object> motiveDecision = officialAmbassador
                ? new Dictionary<string, object>{{"sanitizedState",state??new Dictionary<string,object>()},{"prompt","Treat this as a formal diplomatic audience. Preserve the envoy's personality while obeying the resident-ambassador authority charter and relay duty."}}
                : BuildConversationDecisionContext(campaignId, "dialogue", heroId, profile, characteristics, state, playerText, sceneContext, turnPayload, identityView);
            promptPhaseTiming["decisionContextMs"] = promptPhaseTimer.ElapsedMilliseconds;
            Dictionary<string, object> liveRelationships = new Dictionary<string, object> { ["npcToTarget"] = ReadDictionary(motiveDecision, "relationshipNpcToTarget") ?? new Dictionary<string, object>(), ["npcToSpouse"] = ReadDictionary(motiveDecision, "relationshipNpcToSpouse") ?? new Dictionary<string, object>() };
            Dictionary<string, object> promptState =
                ReadDictionary(motiveDecision, "sanitizedState")
                ?? state;
            promptPhaseTimer.Restart();
            TryGetCodexParallelPreparation(turnPayload, out bool parallelRequested, out bool consistentSnapshot,
                out bool characterInitialized, out bool requiredStateUpdatesComplete);
            CodexPromptPreparationPieces parallelPieces = null;
            if (parallelRequested && consistentSnapshot && characterInitialized && requiredStateUpdatesComplete)
            {
                parallelPieces = PrepareCodexDialoguePromptPieces(
                    playerText, sceneContext, profile, characteristics, liveRelationships, summary,
                    priorLines, selectedContextPulls, contextBundles, turnPayload, heroId, heroName, promptState);
            }
            string contextPullText = BuildPromptContext(campaignId, heroId, profile, playerText, sceneContext, characteristics, liveRelationships, summary, priorLines, selectedContextPulls, contextBundles, false, turnPayload, "dialogue", promptPhaseTiming,
                parallelPieces == null ? null : parallelPieces.SelectedContext,
                precomputedMemoryPacket, precomputedGroupConversationPrompt);
            promptPhaseTiming["contextTotalMs"] = promptPhaseTimer.ElapsedMilliseconds;
            promptPhaseTimer.Restart();
            string characterPrefix = parallelPieces == null
                ? BuildStableCharacterPrompt(heroId, heroName, profile, characteristics)
                : parallelPieces.CharacterPrefix;
            string liveState = (parallelPieces == null
                ? BuildLiveCharacterPrompt(heroName, profile, characteristics, promptState, playerText, sceneContext)
                : parallelPieces.LiveState) + "\n\n" + ReadString(motiveDecision, "prompt", "");
            liveState += CourtService.BuildCourtLifeResolutionContinuityPrompt(campaignId, heroId, turnPayload);
            liveState += BuildCurrentSceneProgressPrompt(priorLines);
            string canonicalTranscript = parallelPieces == null
                ? FormatContinuityTranscript(priorLines, Program.FormatDialogueForPrompt)
                : parallelPieces.Transcript;
            promptPhaseTiming["characterAndLiveStateMs"] = promptPhaseTimer.ElapsedMilliseconds;
            if (parallelPieces != null)
            {
                promptPhaseTiming["parallelPreparation"] = parallelPieces.Result == null
                    ? new Dictionary<string, object>()
                    : parallelPieces.Result.Diagnostics;
            }
            else if (parallelRequested)
            {
                promptPhaseTiming["parallelPreparation"] = CodexConversationContracts.BuildParallelPreparationPlan(
                    true, consistentSnapshot, characterInitialized, requiredStateUpdatesComplete,
                    new[] { "selected_context", "character_foundation", "live_character_state", "canonical_transcript" });
            }
            promptPhaseTimer.Restart();
            Dictionary<string, string> values = new Dictionary<string, string>
            {
                ["campaignId"] = campaignId ?? "default",
                ["heroId"] = heroId ?? "",
                ["heroName"] = heroName ?? heroId ?? "Unknown",
                ["playerName"] = playerName ?? "the stranger",
                ["playerText"] = playerText ?? "",
                ["identityPromptBlock"] = CharactersService.BuildIdentityPromptBlock(identityView),
                ["sceneContext"] = StructuredActionWriterScene(string.IsNullOrWhiteSpace(sceneContext) ? "No scene context was provided." : sceneContext, turnPayload),
                ["characterLiveStateText"] = liveState,
                ["contextPullText"] = contextPullText,
                ["priorDialogueText"] = canonicalTranscript,
                ["worldTone"] = Program.LoadPromptTemplate("world_tone.txt"),
                ["globalResponseOverride"] = Program.LoadPromptTemplate("global_response_override.txt"),
                ["characterFoundationText"] = characterPrefix + "\n\n" + liveState,
                ["noblePromptBlock"] = Program.BuildNoblePromptBlock(profile),
                ["actionCatalogJson"] = CanonicalActionCatalogJson(actionCatalog),
                ["actionSuggestionRules"] = Program.LoadPromptTemplate("action_suggestion_rules.txt"),
                ["memoryWriteRules"] = LoadSourceScopedMemoryWriteRules(),
                ["internalPosture"] = Program.LoadPromptTemplate("dialogue_internal_posture.txt"),
                ["outputSchema"] = ConversationActionOutputSchema(Program.LoadPromptTemplate("dialogue_output_schema.json"), UsesStructuredActions(turnPayload))
            };
            string conversationScenePrompt = ReadString(turnPayload, "conversationScenePrompt", "");
            string roleAttribution = BuildInteractionRoleAttribution(turnPayload, heroId, heroName, playerName);
            roleAttribution += BuildDialogueAcknowledgedCorrectionLedger(turnPayload, priorLines, heroId);
            string npcApproachInstructions = CharactersService.NpcApproachInstructions(turnPayload);
            if (npcApproachInstructions.Length > 0) roleAttribution += "\n\n" + npcApproachInstructions;
            string guestPromptBlock = BuildTemporaryGuestDialoguePromptBlock(turnPayload, heroId);
            if (!string.IsNullOrWhiteSpace(guestPromptBlock))
                roleAttribution += "\n\n" + guestPromptBlock;
            string arrestPromptBlock = WorldSimulationService.BuildArrestDialoguePromptBlock(turnPayload);
            if (!string.IsNullOrWhiteSpace(arrestPromptBlock))
                roleAttribution = roleAttribution + "\n\n" + arrestPromptBlock;
            promptPhaseTiming["templateAssemblyMs"] = promptPhaseTimer.ElapsedMilliseconds;
            promptPhaseTimer.Restart();
            Dictionary<string, object> npcRelationshipPrompt =
                officialAmbassador
                ? new Dictionary<string, object>{{"block",""},{"characterCount",0}}
                : ReadDictionary(turnPayload, "precomputedNpcRelationshipPrompt")
                    ?? RelationshipsService.BuildNpcRelationshipPromptContext(campaignId, heroId, turnPayload);
            promptPhaseTiming["npcRelationshipMs"] = promptPhaseTimer.ElapsedMilliseconds;
            promptPhaseTimer.Restart();
            string npcRelationshipBlock = ReadString(npcRelationshipPrompt, "block", "");
            bool? nobleStatus = NativeNoblePromptApplicability(profile);
            string variant = nobleStatus == true ? "noble" : nobleStatus == false ? "commoner" : "unknown";
            string ambassadorRoleBlock = CourtService.BuildAmbassadorRolePrompt(campaignId, heroId, heroName, turnPayload);
            string chancellorRoleBlock = BuildChancellorRolePrompt(heroId, heroName, turnPayload);
            string specializedRoleBlock = string.Join("\n\n", new[] { ambassadorRoleBlock, chancellorRoleBlock }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            var composition = new Dictionary<string, object>();
            string globalPrefix = BuildDialogueGlobalPrefix(false, NativeNoblePromptApplicability(profile), actionCatalog, specializedRoleBlock, UsesCastleRoomAttireContext(turnPayload), composition, UsesStructuredActions(turnPayload));
            CaptureRequiredPromptEvidence(turnPayload, "canonicalTranscript", canonicalTranscript);
            roleAttribution += "\n\n" + BuildProtectedConversationContinuity(campaignId, heroId, turnPayload, state, contextPullText,
                priorLines, () => CharactersService.SanitizePromptForIdentity(canonicalTranscript, playerText, canonicalPlayerName, identityView), structuredMessages: true);
            var capacity = ResolveConversationPromptCapacity(turnPayload, "dialogue",
                CharactersService.IsNpcLifeApproach(turnPayload) ? Program.BanterProviderSettings(TestDict("requestType", "npc_approach"), Program.LoadSettings()) : null);
            PromptLiveTurnBudgetResult budgeted = BuildBudgetedConversationLiveTurn(
                "dialogue_live_turn_template.txt", "priorDialogueText", values,
                conversationScenePrompt, roleAttribution,
                npcRelationshipBlock, globalPrefix, characterPrefix, capacity: capacity);
            promptPhaseTiming["budgetCompactionMs"] = promptPhaseTimer.ElapsedMilliseconds;
            contextPullText = budgeted.ContextPullText;
            canonicalTranscript = budgeted.CanonicalTranscript;
            npcRelationshipBlock = budgeted.NpcRelationshipBlock;
            string liveTurn = budgeted.Structure.MarkedLiveTurn;
            promptPhaseTimer.Restart();
            liveTurn = CharactersService.SanitizePromptForIdentity(liveTurn, playerText, canonicalPlayerName, identityView);
            ProjectConversationEvidenceForIdentity(turnPayload, playerText, canonicalPlayerName, identityView);
            npcRelationshipBlock = CharactersService.SanitizePromptForIdentity(
                npcRelationshipBlock, playerText, canonicalPlayerName, identityView);
            npcRelationshipPrompt =
                new Dictionary<string, object>(npcRelationshipPrompt)
                {
                    ["block"] = npcRelationshipBlock,
                    ["characterCount"] = npcRelationshipBlock.Length
                };
            // Current move guidance survives history compaction and is shared by serial/parallel preparation.
            var naturalness = BuildConversationNaturalnessContext(playerText, false, priorLines, heroId, heroName, turnPayload,
                text => CharactersService.SanitizePromptForIdentity(text, playerText, canonicalPlayerName, identityView));
            string naturalnessPrompt = ReadString(naturalness, "prompt", "");
            if (!string.IsNullOrWhiteSpace(naturalnessPrompt)) liveTurn += "\n\n" + naturalnessPrompt;
            string culturalVoiceReminder = CulturalVoiceTurnReminder(profile, false);
            if (!string.IsNullOrWhiteSpace(culturalVoiceReminder)) liveTurn += "\n\n" + culturalVoiceReminder;
            budgeted.Structure.MarkedLiveTurn = liveTurn;
            PromptEnvelope envelope = CreatePromptEnvelope("dialogue", variant, globalPrefix, characterPrefix, budgeted.Structure.WithoutMarkers(liveTurn));
            CharactersService.AttachNpcPurposePrompt(envelope, turnPayload, heroId);
            AttachConversationDrinkPrompt(envelope, campaignId, heroId, turnPayload, profile, characteristics);
            AttachPersonalGossipIdentityPrompt(envelope, campaignId, heroId, turnPayload);
            StructureConversationMessages(envelope, budgeted.Structure, canonicalTranscript, priorLines,
                Program.FormatDialogueForPrompt, heroId, playerName, playerText, false,
                text => CharactersService.SanitizePromptForIdentity(text, playerText, canonicalPlayerName, identityView),
                BuildConfiguredObservedGroupTurnStage(campaignId, turnPayload, heroId, playerText));
            FinalizeContinuityPromptBudget(envelope, turnPayload, capacity);
            envelope.Diagnostics["memoryRecallGrounding"] = BindDeliveredMemoryRecallEvidence(turnPayload, heroId, envelope.Messages, heroName, priorLines,
                text => CharactersService.SanitizePromptForIdentity(text, playerText, canonicalPlayerName, identityView), envelope.Diagnostics);
            envelope.Diagnostics["conversationNaturalness"] = naturalness;
            promptPhaseTiming["finalizeEnvelopeMs"] = promptPhaseTimer.ElapsedMilliseconds;
            envelope.Diagnostics["motiveDecision"] = motiveDecision;
            envelope.Diagnostics["skillAwareness"] = CharactersService.BuildSkillAwarenessDiagnostics(profile, characteristics, playerText, sceneContext);
            envelope.Diagnostics["composition"] = composition;
            envelope.Diagnostics["promptBudgetCompaction"] = budgeted.Diagnostics;
            envelope.Diagnostics["npcRelationshipPrompt"] = npcRelationshipPrompt;
            envelope.Diagnostics["npcRelationshipPromptIncluded"] =
                string.IsNullOrWhiteSpace(npcRelationshipBlock)
                || liveTurn.IndexOf(npcRelationshipBlock, StringComparison.Ordinal) >= 0;
            envelope.Diagnostics["npcRelationshipPromptBlockHash"] =
                PromptHash(npcRelationshipBlock);
            envelope.Diagnostics["conversationSceneState"] = ReadDictionary(turnPayload, "conversationSceneState") ?? new Dictionary<string, object>();
            envelope.Diagnostics["promptPhaseTiming"] = promptPhaseTiming;
            AttachPromptSectionDiagnostics(envelope, new Dictionary<string, string>
            {
                ["globalInstructions"] = globalPrefix,
                ["specializedOfficeRole"] = specializedRoleBlock,
                ["characterFoundation"] = characterPrefix,
                ["currentCharacterState"] = liveState,
                ["selectedContextAndMemory"] = contextPullText,
                ["canonicalTranscript"] = canonicalTranscript,
                ["currentScene"] = values["sceneContext"],
                ["sceneOverlay"] = conversationScenePrompt,
                ["npcRelationshipContext"] = npcRelationshipBlock,
                ["roleAttribution"] = roleAttribution,
                ["latestPlayerText"] = playerText
            }, priorLines);
            AttachStructuredActionDiagnostics(envelope, turnPayload);
            return envelope;
        }

        internal static PromptEnvelope BuildEventPromptEnvelope(
            string campaignId,
            string eventId,
            string heroId,
            string heroName,
            string playerName,
            string canonicalPlayerName,
            string playerText,
            string sceneContext,
            Dictionary<string, object> eventPayload,
            Dictionary<string, object> profile,
            Dictionary<string, object> characteristics,
            Dictionary<string, object> state,
            Dictionary<string, object> relationships,
            Dictionary<string, object> summary,
            List<Dictionary<string, object>> eventLines,
            List<Dictionary<string, object>> selectedContextPulls,
            List<Dictionary<string, object>> contextBundles,
            List<Dictionary<string, object>> actionCatalog,
            Dictionary<string, object> identityView,
            string precomputedMemoryPacket = null,
            string precomputedGroupConversationPrompt = null,
            Dictionary<string, object> precomputedMotiveDecision = null,
            PublishedPartyReplayView publishedParty = null)
        {
            state = WithoutRecallAbsenceState(state, campaignId, heroId, eventPayload);
            CharactersService.EnsureObserverParticipantIdentityViews(
                campaignId, eventPayload, heroId);
            eventLines = publishedParty?.WriterTranscript(campaignId, heroId, eventPayload, playerText)
                ?? ProjectConversationLinesForObserver(eventLines, heroId, eventPayload);
            playerText = ProjectCurrentPlayerPromptText(playerText, heroId, eventPayload);
            eventPayload["playerText"] = playerText ?? "";
            eventPayload["currentConversationCorrection"] = ActiveConversationCorrection(playerText, eventLines);
            if (UsesCastleRoomAttireContext(eventPayload))
            {
                SanitizeRoomOutfitInjection(eventPayload);
                profile = WithoutRoomOutfitInjection(profile);
                characteristics = WithoutRoomOutfitInjection(characteristics);
                contextBundles = (contextBundles ?? new List<Dictionary<string, object>>()).Select(WithoutRoomOutfitInjection).ToList();
            }
            Dictionary<string, object> promptPhaseTiming = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Stopwatch promptPhaseTimer = Stopwatch.StartNew();
            Dictionary<string, object> motiveDecision = precomputedMotiveDecision
                ?? BuildConversationDecisionContext(campaignId, ReadString(eventPayload, "mode", "social_event"), heroId, profile, characteristics, state, playerText, sceneContext, eventPayload, identityView);
            eventPayload["conversationAgencyContext"] = ReadDictionary(motiveDecision, "conversationAgency");
            promptPhaseTiming["decisionContextMs"] = promptPhaseTimer.ElapsedMilliseconds;
            Dictionary<string, object> liveRelationships = new Dictionary<string, object> { ["npcToTarget"] = ReadDictionary(motiveDecision, "relationshipNpcToTarget") ?? new Dictionary<string, object>(), ["npcToSpouse"] = ReadDictionary(motiveDecision, "relationshipNpcToSpouse") ?? new Dictionary<string, object>() };
            Dictionary<string, object> promptState =
                ReadDictionary(motiveDecision, "sanitizedState")
                ?? state;
            promptPhaseTimer.Restart();
            TryGetCodexParallelPreparation(eventPayload, out bool parallelRequested, out bool consistentSnapshot,
                out bool characterInitialized, out bool requiredStateUpdatesComplete);
            CodexPromptPreparationPieces parallelPieces = null;
            if (parallelRequested && consistentSnapshot && characterInitialized && requiredStateUpdatesComplete)
            {
                parallelPieces = PrepareCodexEventPromptPieces(
                    playerText, sceneContext, eventPayload, profile, characteristics, liveRelationships, summary,
                    eventLines, selectedContextPulls, contextBundles, heroId, heroName, promptState, identityView);
            }
            string contextPullText = BuildPromptContext(campaignId, heroId, profile, playerText, sceneContext, characteristics, liveRelationships, summary, eventLines, selectedContextPulls, contextBundles, true, eventPayload, ReadString(eventPayload, "mode", "social_event"), promptPhaseTiming,
                parallelPieces == null ? null : parallelPieces.SelectedContext,
                precomputedMemoryPacket, precomputedGroupConversationPrompt);
            promptPhaseTiming["contextTotalMs"] = promptPhaseTimer.ElapsedMilliseconds;
            promptPhaseTimer.Restart();
            string characterPrefix = parallelPieces == null
                ? BuildStableCharacterPrompt(heroId, heroName, profile, characteristics)
                : parallelPieces.CharacterPrefix;
            string liveState = (parallelPieces == null
                ? BuildLiveCharacterPrompt(heroName, profile, characteristics, promptState, playerText, sceneContext)
                : parallelPieces.LiveState) + "\n\n" + ReadString(motiveDecision, "prompt", "");
            liveState += CourtService.BuildCourtLifeResolutionContinuityPrompt(campaignId, heroId, eventPayload);
            liveState += BuildCurrentSceneProgressPrompt(eventLines);
            string canonicalTranscript = parallelPieces == null
                ? FormatContinuityTranscript(eventLines, lines => CharactersService.FormatEventLinesForObserver(lines, eventPayload, heroId, identityView))
                : parallelPieces.Transcript;
            promptPhaseTiming["characterAndLiveStateMs"] = promptPhaseTimer.ElapsedMilliseconds;
            if (parallelPieces != null)
            {
                promptPhaseTiming["parallelPreparation"] = parallelPieces.Result == null
                    ? new Dictionary<string, object>()
                    : parallelPieces.Result.Diagnostics;
            }
            else if (parallelRequested)
            {
                promptPhaseTiming["parallelPreparation"] = CodexConversationContracts.BuildParallelPreparationPlan(
                    true, consistentSnapshot, characterInitialized, requiredStateUpdatesComplete,
                    new[] { "selected_context", "character_foundation", "live_character_state", "canonical_transcript" });
            }
            promptPhaseTimer.Restart();
            Dictionary<string, string> values = new Dictionary<string, string>
            {
                ["campaignId"] = campaignId ?? "default",
                ["eventId"] = eventId ?? "",
                ["heroId"] = heroId ?? "",
                ["heroName"] = heroName ?? heroId ?? "Unknown",
                ["playerName"] = playerName ?? "the stranger",
                ["playerText"] = playerText ?? "",
                ["identityPromptBlock"] = CharactersService.BuildIdentityPromptBlock(identityView),
                ["sceneContext"] = StructuredActionWriterScene(string.IsNullOrWhiteSpace(sceneContext) ? "No scene context was provided." : sceneContext, eventPayload),
                ["eventJson"] = Program.BuildEventJsonForPrompt(eventPayload),
                ["characterLiveStateText"] = liveState,
                ["contextPullText"] = contextPullText,
                ["eventHistoryText"] = canonicalTranscript,
                ["worldTone"] = Program.LoadPromptTemplate("world_tone.txt"),
                ["globalResponseOverride"] = Program.LoadPromptTemplate("global_response_override.txt"),
                ["characterFoundationText"] = characterPrefix + "\n\n" + liveState,
                ["noblePromptBlock"] = Program.BuildNoblePromptBlock(profile),
                ["actionCatalogJson"] = CanonicalActionCatalogJson(actionCatalog),
                ["actionSuggestionRules"] = Program.LoadPromptTemplate("action_suggestion_rules.txt"),
                ["memoryWriteRules"] = LoadSourceScopedMemoryWriteRules(),
                ["outputSchema"] = ConversationActionOutputSchema(Program.LoadPromptTemplate("event_output_schema.json"), UsesStructuredActions(eventPayload))
            };
            string conversationScenePrompt = ReadString(eventPayload, "conversationScenePrompt", "");
            string roleAttribution = BuildInteractionRoleAttribution(eventPayload, heroId, heroName, playerName);
            roleAttribution += BuildDialogueAcknowledgedCorrectionLedger(eventPayload, eventLines, heroId);
            string guestPromptBlock = BuildTemporaryGuestDialoguePromptBlock(eventPayload, heroId);
            if (!string.IsNullOrWhiteSpace(guestPromptBlock))
                roleAttribution += "\n\n" + guestPromptBlock;
            promptPhaseTiming["templateAssemblyMs"] = promptPhaseTimer.ElapsedMilliseconds;
            promptPhaseTimer.Restart();
            Dictionary<string, object> npcRelationshipPrompt =
                ReadDictionary(eventPayload, "precomputedNpcRelationshipPrompt")
                ?? RelationshipsService.BuildNpcRelationshipPromptContext(campaignId, heroId, eventPayload);
            promptPhaseTiming["npcRelationshipMs"] = promptPhaseTimer.ElapsedMilliseconds;
            promptPhaseTimer.Restart();
            string npcRelationshipBlock = ReadString(npcRelationshipPrompt, "block", "");
            bool? nobleStatus = NativeNoblePromptApplicability(profile);
            string variant = nobleStatus == true ? "noble" : nobleStatus == false ? "commoner" : "unknown";
            string ambassadorRoleBlock = CourtService.BuildAmbassadorRolePrompt(campaignId, heroId, heroName, eventPayload);
            var composition = new Dictionary<string, object>();
            string globalPrefix = BuildDialogueGlobalPrefix(true, NativeNoblePromptApplicability(profile), actionCatalog, ambassadorRoleBlock, UsesCastleRoomAttireContext(eventPayload), composition, UsesStructuredActions(eventPayload));
            CaptureRequiredPromptEvidence(eventPayload, "canonicalTranscript", canonicalTranscript);
            roleAttribution += "\n\n" + BuildProtectedConversationContinuity(campaignId, heroId, eventPayload, state, contextPullText,
                eventLines, () => CharactersService.SanitizePromptForIdentity(canonicalTranscript, playerText, canonicalPlayerName, identityView), structuredMessages: true);
            var capacity = ResolveConversationPromptCapacity(eventPayload, "social_event");
            PromptLiveTurnBudgetResult budgeted = BuildBudgetedConversationLiveTurn(
                "event_live_turn_template.txt", "eventHistoryText", values,
                conversationScenePrompt, roleAttribution,
                npcRelationshipBlock, globalPrefix, characterPrefix, capacity: capacity);
            promptPhaseTiming["budgetCompactionMs"] = promptPhaseTimer.ElapsedMilliseconds;
            contextPullText = budgeted.ContextPullText;
            canonicalTranscript = budgeted.CanonicalTranscript;
            npcRelationshipBlock = budgeted.NpcRelationshipBlock;
            string liveTurn = budgeted.Structure.MarkedLiveTurn;
            promptPhaseTimer.Restart();
            liveTurn = CharactersService.SanitizePromptForIdentity(liveTurn, playerText, canonicalPlayerName, identityView);
            ProjectConversationEvidenceForIdentity(eventPayload, playerText, canonicalPlayerName, identityView);
            npcRelationshipBlock = CharactersService.SanitizePromptForIdentity(
                npcRelationshipBlock, playerText, canonicalPlayerName, identityView);
            npcRelationshipPrompt =
                new Dictionary<string, object>(npcRelationshipPrompt)
                {
                    ["block"] = npcRelationshipBlock,
                    ["characterCount"] = npcRelationshipBlock.Length
                };
            // Current move guidance survives history compaction and is shared by serial/parallel preparation.
            var naturalness = BuildConversationNaturalnessContext(playerText, true, eventLines, heroId, heroName, eventPayload,
                text => CharactersService.SanitizePromptForIdentity(text, playerText, canonicalPlayerName, identityView));
            string naturalnessPrompt = ReadString(naturalness, "prompt", "");
            if (!string.IsNullOrWhiteSpace(naturalnessPrompt)) liveTurn += "\n\n" + naturalnessPrompt;
            string culturalVoiceReminder = CulturalVoiceTurnReminder(profile, false);
            if (!string.IsNullOrWhiteSpace(culturalVoiceReminder)) liveTurn += "\n\n" + culturalVoiceReminder;
            budgeted.Structure.MarkedLiveTurn = liveTurn;
            PromptEnvelope envelope = CreatePromptEnvelope("social_event", variant, globalPrefix, characterPrefix, budgeted.Structure.WithoutMarkers(liveTurn));
            CharactersService.AttachNpcPurposePrompt(envelope, eventPayload, heroId);
            AttachConversationDrinkPrompt(envelope, campaignId, heroId, eventPayload, profile, characteristics);
            AttachPersonalGossipIdentityPrompt(envelope, campaignId, heroId, eventPayload);
            StructureConversationMessages(envelope, budgeted.Structure, canonicalTranscript, eventLines,
                lines => CharactersService.FormatEventLinesForObserver(lines, eventPayload, heroId, identityView), heroId,
                playerName, playerText, true,
                text => CharactersService.SanitizePromptForIdentity(text, playerText, canonicalPlayerName, identityView),
                BuildConfiguredObservedGroupTurnStage(campaignId, eventPayload, heroId, playerText));
            FinalizeContinuityPromptBudget(envelope, eventPayload, capacity);
            envelope.Diagnostics["memoryRecallGrounding"] = BindDeliveredMemoryRecallEvidence(eventPayload, heroId, envelope.Messages, heroName, eventLines,
                text => CharactersService.SanitizePromptForIdentity(text, playerText, canonicalPlayerName, identityView), envelope.Diagnostics);
            envelope.Diagnostics["conversationNaturalness"] = naturalness;
            promptPhaseTiming["finalizeEnvelopeMs"] = promptPhaseTimer.ElapsedMilliseconds;
            envelope.Diagnostics["motiveDecision"] = motiveDecision;
            envelope.Diagnostics["skillAwareness"] = CharactersService.BuildSkillAwarenessDiagnostics(profile, characteristics, playerText, sceneContext);
            envelope.Diagnostics["composition"] = composition;
            envelope.Diagnostics["promptBudgetCompaction"] = budgeted.Diagnostics;
            envelope.Diagnostics["npcRelationshipPrompt"] = npcRelationshipPrompt;
            envelope.Diagnostics["npcRelationshipPromptIncluded"] =
                string.IsNullOrWhiteSpace(npcRelationshipBlock)
                || liveTurn.IndexOf(npcRelationshipBlock, StringComparison.Ordinal) >= 0;
            envelope.Diagnostics["npcRelationshipPromptBlockHash"] =
                PromptHash(npcRelationshipBlock);
            envelope.Diagnostics["conversationSceneState"] = ReadDictionary(eventPayload, "conversationSceneState") ?? new Dictionary<string, object>();
            envelope.Diagnostics["promptPhaseTiming"] = promptPhaseTiming;
            AttachPromptSectionDiagnostics(envelope, new Dictionary<string, string>
            {
                ["globalInstructions"] = globalPrefix,
                ["residentAmbassadorRole"] = ambassadorRoleBlock,
                ["characterFoundation"] = characterPrefix,
                ["currentCharacterState"] = liveState,
                ["selectedContextAndMemory"] = contextPullText,
                ["canonicalTranscript"] = canonicalTranscript,
                ["currentScene"] = values["sceneContext"],
                ["sceneOverlay"] = conversationScenePrompt,
                ["npcRelationshipContext"] = npcRelationshipBlock,
                ["roleAttribution"] = roleAttribution,
                ["latestPlayerText"] = playerText
            }, eventLines);
            AttachStructuredActionDiagnostics(envelope, eventPayload);
            return envelope;
        }

        internal static string BuildInteractionRoleAttribution(
            Dictionary<string, object> payload, string speakerId, string speakerName, string usablePlayerName)
        {
            payload = payload ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> profiles = MergedInteractionParticipantProfiles(payload);
            string playerId = FirstNonEmpty(
                ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId"),
                profiles.Select(x => ReadString(x, "role", "").Equals("player", StringComparison.OrdinalIgnoreCase) ? Program.CharacterIdFrom(x) : "")
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                "main_hero");
            Dictionary<string, Dictionary<string, object>> participantViews =
                ReadDictionaryList(
                        payload,
                        "observerParticipantIdentityViews")
                    .Where(row =>
                    {
                        string observer = ReadFirstString(
                            row,
                            "observerHeroStringId",
                            "observerId");
                        return string.IsNullOrWhiteSpace(
                                observer)
                            || observer.Equals(
                                speakerId ?? "",
                                StringComparison.OrdinalIgnoreCase);
                    })
                    .Where(row => !string.IsNullOrWhiteSpace(
                        ReadFirstString(
                            row,
                            "subjectHeroStringId",
                            "subjectId")))
                    .GroupBy(
                        row => ReadFirstString(
                            row,
                            "subjectHeroStringId",
                            "subjectId"),
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => ReadDictionary(
                                group.First(),
                                "identityView")
                            ?? new Dictionary<string, object>(),
                        StringComparer.OrdinalIgnoreCase);
            Func<Dictionary<string, object>, Dictionary<string, object>>
                observerView = participant =>
                {
                    string id = Program.CharacterIdFrom(participant);
                    return participantViews.TryGetValue(
                        id, out Dictionary<string, object> view)
                            ? view
                            : new Dictionary<string, object>();
                };
            Func<Dictionary<string, object>, string> observerSafeName =
                participant =>
                {
                    string id = Program.CharacterIdFrom(participant);
                    if (id.Equals(
                            speakerId ?? "",
                            StringComparison.OrdinalIgnoreCase))
                        return FirstNonEmpty(
                            ReadString(participant, "name", ""),
                            speakerName,
                            id);
                    Dictionary<string, object> view =
                        observerView(participant);
                    return FirstNonEmpty(
                        ReadString(view, "usableName", ""),
                        ReadString(view, "safeLabel", ""),
                        "an unidentified participant");
                };
            StringBuilder block = new StringBuilder();
            block.AppendLine("AUTHORITATIVE CURRENT ROLE ATTRIBUTION");
            block.AppendLine("- PLAYER ROLE: hero id " + playerId + "; usable address is " + FirstNonEmpty(usablePlayerName, "the stranger") + ".");
            block.AppendLine("- CURRENT NPC SPEAKER: hero id " + FirstNonEmpty(speakerId, "unknown") + "; name " + FirstNonEmpty(speakerName, speakerId, "unknown") + ".");
            foreach (Dictionary<string, object> participant in profiles
                .Where(x => !Program.CharacterIdFrom(x).Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    && !Program.CharacterIdFrom(x).Equals(speakerId, StringComparison.OrdinalIgnoreCase)))
            {
                Dictionary<string, object> view =
                    observerView(participant);
                Dictionary<string, object> authority =
                    ReadDictionary(view, "authorityView")
                    ?? new Dictionary<string, object>();
                block.AppendLine(
                    "- OTHER NPC PARTICIPANT: hero id "
                    + Program.CharacterIdFrom(participant)
                    + "; observer-safe identity "
                    + observerSafeName(participant)
                    + "; identity state "
                    + ReadString(
                        view,
                        "identityState",
                        "encountered_unknown")
                    + "; recognized roles "
                    + (ReadStringList(
                            authority,
                            "recognizedRoles").Count == 0
                        ? "none"
                        : string.Join(
                            ", ",
                            ReadStringList(
                                authority,
                                "recognizedRoles")))
                    + ". Mechanical IDs and hidden profile names do not grant identity knowledge.");
            }
            Dictionary<string, string> names = profiles
                .Where(x => !string.IsNullOrWhiteSpace(Program.CharacterIdFrom(x)))
                .GroupBy(Program.CharacterIdFrom, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => observerSafeName(group.First()),
                    StringComparer.OrdinalIgnoreCase);
            Func<string, string> namedId = id => string.IsNullOrWhiteSpace(id)
                ? "none recorded"
                : (names.TryGetValue(id, out string resolvedName) ? resolvedName + " [" + id + "]" : id);
            block.AppendLine("AUTHORITATIVE NATIVE FAMILY LINKS");
            foreach (Dictionary<string, object> participant in profiles.Where(x =>
                !ReadString(x, "role", "npc").Equals("player", StringComparison.OrdinalIgnoreCase)))
            {
                string participantId = Program.CharacterIdFrom(participant);
                Dictionary<string, object> view =
                    observerView(participant);
                if (!participantId.Equals(
                        speakerId ?? "",
                        StringComparison.OrdinalIgnoreCase)
                    && !ReadBool(
                        view,
                        "canonicalNameAllowed", false))
                {
                    block.AppendLine(
                        "- " + observerSafeName(participant)
                        + ": family links withheld until this observer identifies the participant.");
                    continue;
                }
                List<string> children = ReadStringList(participant, "childrenIds");
                Func<string, string> familyNamedId = relativeId =>
                {
                    if (string.IsNullOrWhiteSpace(relativeId)) return "none recorded";
                    string nativeName = NativeFamilyMemberName(participant, relativeId);
                    return !string.IsNullOrWhiteSpace(nativeName)
                        ? nativeName + " [" + relativeId + "]"
                        : namedId(relativeId);
                };
                block.AppendLine("- " + observerSafeName(participant)
                    + ": spouse=" + familyNamedId(ReadString(participant, "spouseId", ""))
                    + "; father=" + familyNamedId(ReadString(participant, "fatherId", ""))
                    + "; mother=" + familyNamedId(ReadString(participant, "motherId", ""))
                    + "; children=" + (children.Count == 0 ? "none recorded" : string.Join(", ", children.Select(familyNamedId))) + ".");
            }
            block.AppendLine("Only the links above establish parent, child, or spouse links BETWEEN NATIVE HEROES. For an encountered commoner, the speaker's saved PERSONAL COMMONER HOUSEHOLD separately establishes unmodeled relatives and marital status. 'None recorded' in native fields never erases that household or proves them single or childless. Shared clan, kingdom, title, age, household role, or a similar name does not establish kinship between Heroes. Correct earlier dialogue only when it contradicts an actual native Hero link; do not 'correct' a saved unmodeled spouse or child out of existence. Another participant's private household is not automatically known.");
            // The current native snapshot may explicitly clear a home. Do not fill
            // its empty fields from older scene participants or remembered plans.
            Dictionary<string, object> homeProfile = ReadDictionary(payload, "hero");
            if (homeProfile == null || !Program.CharacterIdFrom(homeProfile).Equals(speakerId ?? "", StringComparison.OrdinalIgnoreCase))
                homeProfile = ReadDictionaryList(payload, "attendees")
                    .Concat(ReadDictionaryList(payload, "participantProfiles"))
                    .Concat(ReadDictionaryList(payload, "sceneParticipants"))
                    .FirstOrDefault(x => Program.CharacterIdFrom(x).Equals(speakerId ?? "", StringComparison.OrdinalIgnoreCase))
                    ?? profiles.FirstOrDefault(x => Program.CharacterIdFrom(x).Equals(speakerId ?? "", StringComparison.OrdinalIgnoreCase));
            block.AppendLine();
            block.AppendLine("AUTHORITATIVE NATIVE HOME - CURRENT SPEAKER");
            block.AppendLine(Json.Serialize(new Dictionary<string, object>
            {
                ["heroId"] = speakerId ?? "",
                ["homeSettlementId"] = ReadString(homeProfile, "homeSettlementId", ""),
                ["homeSettlementName"] = ReadString(homeProfile, "homeSettlementName", "")
            }));
            block.AppendLine("These are your own current native home facts, which you know without a context lookup. Use the supplied name and ID when discussing your home or planning a return. "
                + "Current native home overrides incompatible home claims in older dialogue, memories and travel plans: acknowledge an earlier mistaken name naturally and align future directions and intentions with this fact. "
                + "Preserve the historical record of what was said; a genuine former home or original commoner household is separate. "
                + "An empty field is unavailable, not permission to invent a name or ID or substitute your current location, a clan's initial seat, capital or another holding. "
                + "Home does not establish current presence, family attendance, ownership, road conditions or travel time. Another participant's home is not supplied by this block.");
            string roleEquivalence = BuildRoleEquivalenceLedger(payload);
            if (!string.IsNullOrWhiteSpace(roleEquivalence))
            {
                block.AppendLine(roleEquivalence);
            }
            block.Append("A participant name used at the start of PLAYER TEXT followed by a comma is a direct address to that NPC; it never renames the player. "
                + "Never label an NPC participant as '(player)', never claim the player introduced themselves with an addressee's name, and preserve these roles in every private write. "
                + "If an older memory or NPC transcript says the player or stranger claimed to be ANY NPC PARTICIPANT listed above, treat that as a legacy role-attribution error unless an exact attributed player line explicitly records such an alias claim. Do not repeat the corrupted name as fact.");
            return block.ToString().Trim();
        }
    }
}
