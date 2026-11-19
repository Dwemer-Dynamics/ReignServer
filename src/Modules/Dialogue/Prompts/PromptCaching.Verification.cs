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
#if !REIGN_EXCLUDE_VERIFICATION
        internal static List<Dictionary<string, object>> RunPromptCachingSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, passed, summary, data) => results.Add(new Dictionary<string, object> { ["caseId"] = id, ["passed"] = passed, ["summary"] = summary, ["data"] = data });
            results.AddRange(RunProviderResponseRecoverySelfTests());
            results.AddRange(RunPromptCompositionSelfTests());
            results.AddRange(RunConversationMessageStructureSelfTests());
            results.AddRange(RunConversationNaturalnessSelfTests());
            results.AddRange(RunRoleplayContinuitySelfTests());
            results.AddRange(RunTemporaryGuestDialogueSelfTests());
            results.AddRange(RunConversationContinuitySelfTests());
            results.AddRange(RunCharacterCommitmentAssertions());
            results.AddRange(RunConversationAgencySelfTests());
            results.AddRange(RunConversationEffectAcceptanceSelfTests());
            results.AddRange(RunConversationAcceptedUnitSelfTests());
            results.AddRange(RunConversationReferenceGraphSelfTests());
            results.AddRange(RunPartyBanterSelfTests());
            results.AddRange(RunDialogueAccuracySelfTests());
            results.AddRange(RunMemoryEvidenceReplaySelfTests());
            results.AddRange(RunConversationSourcePersistenceSelfTests());
            results.AddRange(RunSinglePassConversationSelfTests());
            results.AddRange(RunConversationFailureRecoverySelfTests());
            results.AddRange(RunConversationRouteMemorySelfTests());
            results.AddRange(RelationshipsService.RunSharedRelationshipHistoryEvidenceSelfTests());
            results.AddRange(RunConversationEvidenceSelfTests());
            results.AddRange(RunCorrespondenceMemoryAssertions());
            results.AddRange(RunConversationObserverProjectionAssertions());
            results.AddRange(RunConversationInputGroundingAssertions());
            var nativeHomeHero = TestDict("heroStringId", "lord_1_62_1", "name", "Arytha",
                "homeSettlementId", "castle_fixture", "homeSettlementName", "Gersegos Castle",
                "currentSettlementId", "town_fixture", "currentSettlementName", "Marunath");
            var nativeHomePayload = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(TestDict(
                "hero", nativeHomeHero, "playerHeroStringId", "main_hero",
                "sceneParticipants", new List<Dictionary<string, object>>
                {
                    TestDict("heroStringId", "main_hero", "role", "player", "name", "Michael",
                        "homeSettlementId", "private_player_home", "homeSettlementName", "PRIVATE_PLAYER_HOME"),
                    TestDict("heroStringId", "other_npc", "name", "Hidden participant",
                        "homeSettlementId", "private_other_home", "homeSettlementName", "PRIVATE_OTHER_HOME")
                })));
            string nativeHomeRole = BuildInteractionRoleAttribution(nativeHomePayload, "lord_1_62_1", "Arytha", "Michael");
            add("native_home_current_speaker_and_privacy",
                nativeHomeRole.Contains("\"homeSettlementId\":\"castle_fixture\"", StringComparison.Ordinal)
                && nativeHomeRole.Contains("Gersegos Castle", StringComparison.Ordinal)
                && !nativeHomeRole.Contains("Marunath", StringComparison.Ordinal)
                && !nativeHomeRole.Contains("PRIVATE_PLAYER_HOME", StringComparison.Ordinal)
                && !nativeHomeRole.Contains("PRIVATE_OTHER_HOME", StringComparison.Ordinal),
                "A serialized ordinary request supplies the speaker's own home without a context pull or another participant's residence.", nativeHomeRole);
            add("native_home_authority_and_presence_boundaries",
                nativeHomeRole.Contains("overrides incompatible home claims in older dialogue, memories and travel plans", StringComparison.Ordinal)
                && nativeHomeRole.Contains("Preserve the historical record", StringComparison.Ordinal)
                && nativeHomeRole.Contains("Home does not establish current presence, family attendance, ownership, road conditions or travel time", StringComparison.Ordinal),
                "Current native home corrects invented plans while retaining old speech and independent presence/ownership evidence.", null);
            foreach (string homeRoute in new[] { "dialogue", "event" })
            {
                string historyKey = homeRoute == "dialogue" ? "priorDialogueText" : "eventHistoryText";
                string oldHomeClaim = "- Arytha: Our home is Argoran. We will ride there.";
                var homeValues = new Dictionary<string, string>
                {
                    ["campaignId"] = "native_home_fixture", ["heroId"] = "lord_1_62_1", ["heroName"] = "Arytha",
                    ["playerName"] = "Michael", ["identityPromptBlock"] = "IDENTITY", ["sceneContext"] = "Traveling together",
                    ["characterLiveStateText"] = "current plan: Ride to Argoran with Michael",
                    ["contextPullText"] = new string('C', 60000), [historyKey] = oldHomeClaim,
                    ["playerText"] = "Where is your home?"
                };
                var homeBudget = BuildBudgetedConversationLiveTurn(homeRoute + "_live_turn_template.txt", historyKey,
                    homeValues, "SCENE", nativeHomeRole, "", new string('G', 32000), new string('F', 5000), 96000);
                add("native_home_protected_live_prompt_" + homeRoute,
                    ReadBool(homeBudget.Diagnostics, "compacted", false)
                    && ReadBool(homeBudget.Diagnostics, "targetMet", false)
                    && homeBudget.LiveTurn.Contains(NormalizePromptSegment(nativeHomeRole), StringComparison.Ordinal)
                    && homeBudget.CanonicalTranscript == oldHomeClaim
                    && homeBudget.LiveTurn.Contains("current plan: Ride to Argoran with Michael", StringComparison.Ordinal),
                    "Budgeted production templates preserve the complete authoritative home block and the conflicting historical claim separately.", homeBudget.Diagnostics);
            }
            var changedHomeHero = new Dictionary<string, object>(nativeHomeHero)
            {
                ["homeSettlementId"] = "new_castle", ["homeSettlementName"] = "New Native Home"
            };
            string changedHomeRole = BuildInteractionRoleAttribution(TestDict("hero", changedHomeHero), "LORD_1_62_1", "Arytha", "Michael");
            var emptyCharacteristics = new Dictionary<string, object>();
            add("native_home_refreshes_outside_stable_character_cache",
                changedHomeRole.Contains("New Native Home", StringComparison.Ordinal)
                && !changedHomeRole.Contains("Gersegos Castle", StringComparison.Ordinal)
                && BuildStableCharacterPrompt("lord_1_62_1", "Arytha", nativeHomeHero, emptyCharacteristics)
                    == BuildStableCharacterPrompt("lord_1_62_1", "Arytha", changedHomeHero, emptyCharacteristics),
                "A new native home refreshes the live role block without depending on a character-foundation cache invalidation.", null);
            var missingHomeHero = new Dictionary<string, object>(nativeHomeHero)
            {
                ["homeSettlementId"] = "", ["homeSettlementName"] = "",
                ["initialHomeSettlementName"] = "OLD_CLAN_SEAT",
                ["encounteredResident"] = TestDict("homeSettlementName", "ORIGINAL_COMMONER_HOME")
            };
            string missingHomeRole = BuildInteractionRoleAttribution(TestDict("hero", missingHomeHero,
                "conversationSceneState", TestDict("participants", new List<Dictionary<string, object>> { nativeHomeHero })),
                "lord_1_62_1", "Arytha", "Michael");
            add("native_home_empty_current_snapshot_does_not_reuse_stale_home",
                missingHomeRole.Contains("\"homeSettlementId\":\"\"", StringComparison.Ordinal)
                && missingHomeRole.Contains("\"homeSettlementName\":\"\"", StringComparison.Ordinal)
                && !missingHomeRole.Contains("Gersegos Castle", StringComparison.Ordinal)
                && !missingHomeRole.Contains("Marunath", StringComparison.Ordinal)
                && !missingHomeRole.Contains("OLD_CLAN_SEAT", StringComparison.Ordinal)
                && !missingHomeRole.Contains("ORIGINAL_COMMONER_HOME", StringComparison.Ordinal),
                "An explicitly missing native home stays unavailable despite a stale scene snapshot, current town, clan seat or original household.", missingHomeRole);
            string groupHomeRole = BuildInteractionRoleAttribution(TestDict("hero", TestDict("heroStringId", "other_npc"),
                "conversationSceneState", TestDict("participants", new List<Dictionary<string, object>> { nativeHomeHero }),
                "attendees", new List<Dictionary<string, object>> { changedHomeHero }), "lord_1_62_1", "Arytha", "Michael");
            string clearedGroupHomeRole = BuildInteractionRoleAttribution(TestDict("attendees",
                new List<Dictionary<string, object>> { missingHomeHero }, "conversationSceneState",
                TestDict("participants", new List<Dictionary<string, object>> { nativeHomeHero })), "lord_1_62_1", "Arytha", "Michael");
            add("native_home_group_selects_current_speaker",
                groupHomeRole.Contains("New Native Home", StringComparison.Ordinal)
                && !groupHomeRole.Contains("Gersegos Castle", StringComparison.Ordinal)
                && groupHomeRole.Contains("\"heroId\":\"lord_1_62_1\"", StringComparison.Ordinal)
                && clearedGroupHomeRole.Contains("\"homeSettlementName\":\"\"", StringComparison.Ordinal)
                && !clearedGroupHomeRole.Contains("Gersegos Castle", StringComparison.Ordinal),
                "A group speaker receives their own fresh attendee home over a stale scene, even when the top-level hero is someone else.", null);
            string legacyHomeRole = BuildInteractionRoleAttribution(TestDict("hero", TestDict("heroStringId", "legacy_npc")),
                "legacy_npc", "Legacy NPC", "Michael");
            add("native_home_legacy_request_stays_unknown",
                legacyHomeRole.Contains("\"homeSettlementName\":\"\"", StringComparison.Ordinal)
                && legacyHomeRole.Contains("An empty field is unavailable", StringComparison.Ordinal),
                "Requests without native home fields remain compatible and cannot manufacture a settlement.", null);
            PromptEnvelope first = CreatePromptEnvelope("dialogue", "commoner", "STATIC", "CHARACTER", "turn one");
            PromptEnvelope second = CreatePromptEnvelope("dialogue", "commoner", "STATIC", "CHARACTER", "turn two");
            add("prompt_prefix_dynamic_stability", ReadString(first.Diagnostics, "globalPrefixHash", "") == ReadString(second.Diagnostics, "globalPrefixHash", "") && ReadString(first.Diagnostics, "characterPrefixHash", "") == ReadString(second.Diagnostics, "characterPrefixHash", ""), "Changing the live suffix preserves both reusable prefix hashes.", null);
            PromptEnvelope changed = CreatePromptEnvelope("dialogue", "commoner", "STATIC", "CHANGED CHARACTER", "turn two");
            add("prompt_character_invalidation", ReadString(first.Diagnostics, "characterPrefixHash", "") != ReadString(changed.Diagnostics, "characterPrefixHash", ""), "Changing stable character content invalidates the character prefix.", null);
            add("prompt_envelope_metadata",
                ReadBool(first.Diagnostics, "cacheEligible", false)
                && ReadString(first.Diagnostics, "characterRevision", "")
                    == ReadString(first.Diagnostics, "characterPrefixHash", "")
                && ReadString(first.Diagnostics, "liveTurnHash", "")
                    == PromptHash("turn one")
                && ReadString(first.Messages.Last(), "content", "") == "turn one",
                "The envelope records cache eligibility, character revision, and an exact live-turn proof hash while keeping live input last.",
                first.Diagnostics);
            AttachPromptSectionDiagnostics(first, new Dictionary<string, string>
            {
                ["globalInstructions"] = "STATIC", ["canonicalTranscript"] = "turn one"
            }, new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["sourceTurnId"] = "statement_1", ["turnId"] = "shared_beat", ["text"] = "turn one" },
                new Dictionary<string, object> { ["sourceTurnId"] = "statement_1", ["turnId"] = "shared_beat", ["text"] = "turn one" }
            });
            Dictionary<string, object> transcriptDiagnostics = ReadDictionary(first.Diagnostics, "canonicalTranscript") ?? new Dictionary<string, object>();
            add("prompt_section_and_duplicate_diagnostics", ReadDictionary(first.Diagnostics, "sectionSizes") != null
                && ReadInt(transcriptDiagnostics, "lineCount", 0) == 2
                && ReadInt(transcriptDiagnostics, "uniqueLineCount", 0) == 1
                && ReadInt(transcriptDiagnostics, "duplicateLineCount", 0) == 1,
                "Prompt diagnostics expose per-section sizes and duplicate conversation identities before provider submission.", first.Diagnostics);
            var unidentified = new List<Dictionary<string, object>>
            {
                TestDict("turnId", "shared_beat", "text", "Yes.", "role", "player", "speakerHeroStringId", "player"),
                TestDict("turnId", "shared_beat", "text", "Yes.", "role", "player", "speakerHeroStringId", "player")
            };
            AttachPromptSectionDiagnostics(first, new Dictionary<string, string>(), unidentified);
            transcriptDiagnostics = ReadDictionary(first.Diagnostics, "canonicalTranscript");
            add("prompt_unidentified_statements_are_not_duplicate_evidence",
                ReadInt(transcriptDiagnostics, "uniqueLineCount", 0) == 2
                && ReadInt(transcriptDiagnostics, "duplicateLineCount", -1) == 0
                && DialogueService.CanonicalizePromptTranscript(unidentified, 30).Count == 2,
                "Equal words in one group beat are retained unless statement identity proves a duplicate.", first.Diagnostics);
            var mixedIdentities = unidentified.Concat(new[]
            {
                TestDict("sourceTurnId", "known_statement", "text", "Yes.", "role", "player", "speakerHeroStringId", "player"),
                TestDict("sourceTurnId", "known_statement", "text", "Yes.", "role", "player", "speakerHeroStringId", "player")
            }).ToList();
            AttachPromptSectionDiagnostics(first, new Dictionary<string, string>(), mixedIdentities);
            transcriptDiagnostics = ReadDictionary(first.Diagnostics, "canonicalTranscript");
            int mixedCanonicalCount = DialogueService.CanonicalizePromptTranscript(mixedIdentities, 30).Count;
            add("prompt_mixed_identity_diagnostics_match_retained_statements", mixedCanonicalCount == 3
                && ReadInt(transcriptDiagnostics, "uniqueLineCount", 0) == mixedCanonicalCount
                && ReadInt(transcriptDiagnostics, "duplicateLineCount", -1) == mixedIdentities.Count - mixedCanonicalCount,
                "One proven duplicate is counted without treating either unidentified reply as a duplicate.", first.Diagnostics);
            var attributedSequence = new List<Dictionary<string, object>>
            {
                TestDict("turnId", "shared_beat", "sequence", 1, "text", "Agreed.", "role", "npc", "speakerId", "ira"),
                TestDict("turnId", "shared_beat", "sequence", 1, "text", "Agreed.", "role", "npc", "speakerId", "rhagaea")
            };
            add("prompt_native_speaker_id_preserves_distinct_authors", DialogueService.CanonicalizePromptTranscript(attributedSequence, 30).Count == 2,
                "The native speakerId field participates in identity even when equal words and sequence occur in one beat.", null);
            Dictionary<string, string> oversizedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["campaignId"] = "budget_campaign",
                ["heroId"] = "budget_npc",
                ["heroName"] = "Budget NPC",
                ["playerName"] = "Budget Player",
                ["identityPromptBlock"] = "IDENTITY_SENTINEL",
                ["sceneContext"] = "SCENE_SENTINEL",
                ["characterLiveStateText"] = "CURRENT_STATE_SENTINEL",
                ["contextPullText"] = "CONTEXT_HEAD_SENTINEL\n\n" + new string('C', 60000) + "\n\nCONTEXT_TAIL_SENTINEL",
                ["priorDialogueText"] = "OLDEST_TRANSCRIPT_SENTINEL\n\n" + new string('T', 60000) + "\n\nNEWEST_TRANSCRIPT_SENTINEL",
                ["playerText"] = "CURRENT_PLAYER_SENTINEL"
            };
            PromptLiveTurnBudgetResult budgetedPrompt = BuildBudgetedConversationLiveTurn(
                "dialogue_live_turn_template.txt", "priorDialogueText", oversizedValues,
                "SCENE_OVERLAY_SENTINEL\n" + new string('S', 3000),
                "ROLE_ATTRIBUTION_SENTINEL\n" + new string('R', 1500),
                "RELATIONSHIP_HEAD_SENTINEL\n" + new string('N', 9000) + "\nRELATIONSHIP_TAIL_SENTINEL",
                new string('G', 32000),
                new string('F', 5000),
                96000);
            int budgetedPromptChars = 32000 + 5000 + budgetedPrompt.LiveTurn.Length;
            add("priority_prompt_budget_compacts_dynamic_evidence",
                budgetedPromptChars <= 96000
                && ReadBool(budgetedPrompt.Diagnostics, "compacted", false)
                && ReadBool(budgetedPrompt.Diagnostics, "targetMet", false)
                && budgetedPrompt.LiveTurn.Contains("CURRENT_PLAYER_SENTINEL")
                && budgetedPrompt.LiveTurn.Contains("IDENTITY_SENTINEL")
                && budgetedPrompt.LiveTurn.Contains("ROLE_ATTRIBUTION_SENTINEL")
                && budgetedPrompt.LiveTurn.Contains("SCENE_OVERLAY_SENTINEL")
                && budgetedPrompt.ContextPullText.Contains("CONTEXT_HEAD_SENTINEL")
                && budgetedPrompt.ContextPullText.Contains("CONTEXT_TAIL_SENTINEL")
                && budgetedPrompt.CanonicalTranscript.Contains("NEWEST_TRANSCRIPT_SENTINEL")
                && budgetedPrompt.NpcRelationshipBlock.Contains("RELATIONSHIP_HEAD_SENTINEL")
                && budgetedPrompt.NpcRelationshipBlock.Contains("RELATIONSHIP_TAIL_SENTINEL"),
                "Over-budget dialogue preserves current identity, role, scene, player input, newest transcript, and both ends of source-linked context while compacting only dynamic evidence.",
                new Dictionary<string, object>
                {
                    ["promptCharacters"] = budgetedPromptChars,
                    ["diagnostics"] = budgetedPrompt.Diagnostics
                });
            string attributedTranscript = "- [In person] Michael: " + new string('A', 260) + "\n"
                + "continued old player detail\n"
                + "- [In person] Hulara: She cooked and served the lamb shoulder.\n"
                + "The meal is complete and the platter is on the table.";
            string compactAttributed = CompactPromptEvidenceBlock(attributedTranscript, 300,
                "conversation transcript", true);
            add("prompt_compaction_preserves_attributed_records",
                compactAttributed.Contains("- [In person] Hulara:")
                && compactAttributed.Contains("The meal is complete and the platter is on the table.")
                && !compactAttributed.StartsWith("continued old player detail", StringComparison.Ordinal),
                "Transcript compaction retains complete newest speaker records and never exposes an orphaned continuation as a turn.",
                compactAttributed);
            var progressLines = new List<Dictionary<string, object>>
            {
                TestDict("role", "player", "speaker", "Michael", "turnId", "turn_1",
                    "text", "The lamb shoulder still needs cooking."),
                TestDict("role", "npc", "speaker", "Hulara", "turnId", "turn_2",
                    "text", "*Hulara roasted the lamb shoulder and served it.* The meal is ready."),
                TestDict("role", "npc", "speaker", "Hulara", "turnId", "turn_3",
                    "text", "She watches the fire.")
            };
            string progressPrompt = BuildCurrentSceneProgressPrompt(progressLines);
            add("current_scene_progress_preserves_completed_activity",
                progressPrompt.Contains("speaker=Hulara") && progressPrompt.Contains("source=turn_2")
                && progressPrompt.Contains("Hulara roasted the lamb shoulder and served it.")
                && progressPrompt.Contains("not confirmed native results")
                && !progressPrompt.Contains("still needs cooking"),
                "The live prompt carries recent accepted NPC completion evidence with source attribution and a native-effect boundary.",
                progressPrompt);
            Dictionary<string, string> modestValues = new Dictionary<string, string>(
                oversizedValues, StringComparer.OrdinalIgnoreCase)
            {
                ["contextPullText"] = "SMALL_CONTEXT_SENTINEL",
                ["priorDialogueText"] = "SMALL_TRANSCRIPT_SENTINEL"
            };
            PromptLiveTurnBudgetResult modestPrompt = BuildBudgetedConversationLiveTurn(
                "dialogue_live_turn_template.txt", "priorDialogueText", modestValues,
                "SMALL_SCENE", "SMALL_ROLE", "SMALL_RELATIONSHIP",
                "SMALL_GLOBAL", "SMALL_CHARACTER", 96000);
            add("prompt_budget_leaves_in_budget_evidence_unchanged",
                !ReadBool(modestPrompt.Diagnostics, "compacted", true)
                && modestPrompt.ContextPullText == "SMALL_CONTEXT_SENTINEL"
                && modestPrompt.CanonicalTranscript == "SMALL_TRANSCRIPT_SENTINEL"
                && modestPrompt.NpcRelationshipBlock == "SMALL_RELATIONSHIP",
                "In-budget prompts retain their full dynamic evidence byte-for-byte.",
                modestPrompt.Diagnostics);
            // Compare the runtime tone segments, including scoped language and LF normalization.
            // Raw templates may use CRLF in a Windows checkout of the Linux server source.
            string worldTone = NormalizePromptSegment(ScopeConversationTone(Program.LoadPromptTemplate("world_tone.txt")));
            string noblePrompt = NormalizePromptSegment(ScopeConversationTone(Program.LoadPromptTemplate("noble_prompt.txt")));
            string nobleEventPrefix = BuildDialogueGlobalPrefix(true, true, new List<Dictionary<string, object>>());
            string commonerEventPrefix = BuildDialogueGlobalPrefix(true, false, new List<Dictionary<string, object>>());
            bool layeredPromptEnvelope = !string.IsNullOrWhiteSpace(worldTone)
                && !string.IsNullOrWhiteSpace(noblePrompt)
                && nobleEventPrefix.Contains(worldTone)
                && commonerEventPrefix.Contains(worldTone)
                && nobleEventPrefix.Contains(noblePrompt)
                && nobleEventPrefix.Contains("NOBLE PROMPT")
                && !commonerEventPrefix.Contains("NOBLE PROMPT")
                && !commonerEventPrefix.Contains(noblePrompt);
            add("prompt_world_tone_noble_layering", layeredPromptEnvelope, "Social-event prompts always carry World Tone and add Noble Prompt only for noble characters.", null);
            List<Dictionary<string, object>> fullCatalog = Program.CompactActionCatalog(Program.ActionCatalog());
            string routedEventPrefix = BuildDialogueGlobalPrefix(true, true, fullCatalog);
            bool actionCatalogIsOutOfBand = routedEventPrefix.Contains("ACTION ROUTING BOUNDARY")
                && routedEventPrefix.IndexOf("AVAILABLE ACTION COMMANDS", StringComparison.OrdinalIgnoreCase) < 0
                && routedEventPrefix.IndexOf("\"command\":\"declare_war\"", StringComparison.OrdinalIgnoreCase) < 0;
            add("dialogue_action_catalog_is_out_of_band", actionCatalogIsOutOfBand,
                "Dialogue prompts carry the action gate contract but leave the authoritative command catalog to the hidden planner.",
                new Dictionary<string, object> { ["characterCount"] = routedEventPrefix.Length, ["catalogCommandCount"] = fullCatalog.Count });
            Dictionary<string, object> a = new Dictionary<string, object> { ["z"] = 1, ["a"] = 2 };
            Dictionary<string, object> b = new Dictionary<string, object> { ["a"] = 2, ["z"] = 1 };
            add("prompt_canonical_json", CanonicalJson(a) == CanonicalJson(b), "Cacheable JSON is deterministic regardless of dictionary insertion order.", CanonicalJson(a));
            Dictionary<string, object> invalidSettings = new Dictionary<string, object> { ["promptCacheMode"] = "wrong", ["promptCacheFallbackCooldownSeconds"] = 2 };
            NormalizePromptCacheSettings(invalidSettings);
            add("prompt_cache_setting_validation", ReadString(invalidSettings, "promptCacheMode", "") == "implicit" && ReadInt(invalidSettings, "promptCacheFallbackCooldownSeconds", 0) == 30, "Prompt-cache settings migrate to subscription-safe implicit caching and retain bounded diagnostics settings.", invalidSettings);
            Dictionary<string, object> legacyCacheSettings = new Dictionary<string, object> { ["promptCacheMode"] = "prefer" };
            Dictionary<string, object> outgoingCacheBody = new Dictionary<string, object>
            {
                ["model"] = "zai-org/glm-5.2:thinking:cache", ["caching"] = true, ["stickyprovider"] = true, ["stickyProvider"] = true
            };
            Dictionary<string, object> subscriptionSafeRouting = ConfigurePromptCacheRouting(legacyCacheSettings,
                new Dictionary<string, object> { ["promptCacheEligible"] = true }, outgoingCacheBody,
                "https://nano-gpt.com/api/v1/chat/completions", "zai-org/glm-5.2", "party_chat");
            add("nanogpt_subscription_safe_cache_body",
                ReadString(legacyCacheSettings, "promptCacheMode", "") == "implicit"
                && !outgoingCacheBody.ContainsKey("caching")
                && !outgoingCacheBody.ContainsKey("stickyprovider")
                && !outgoingCacheBody.ContainsKey("stickyProvider")
                && ReadString(outgoingCacheBody, "model", "") == "zai-org/glm-5.2:thinking"
                && !ReadBool(subscriptionSafeRouting, "cacheCapableRouteRequested", true)
                && ReadBool(subscriptionSafeRouting, "subscriptionSafeImplicit", false),
                "Legacy explicit-cache settings cannot emit NanoGPT cache-provider routing fields that bypass subscription coverage.",
                new Dictionary<string, object> { ["settings"] = legacyCacheSettings, ["body"] = outgoingCacheBody, ["routing"] = subscriptionSafeRouting });
            Dictionary<string, object> absentUsage = BuildPromptCacheDiagnostics(new Dictionary<string, object>(), new Dictionary<string, object> { ["eligible"] = true }, 1, "self_test", "self_test", "", false);
            add("prompt_cache_missing_usage", !ReadBool(absentUsage, "usageReported", true) && absentUsage.ContainsKey("cachedTokens") && absentUsage["cachedTokens"] == null && absentUsage["cacheHit"] == null, "Missing provider usage is represented as unreported rather than as zero tokens or a cache miss.", absentUsage);
            Dictionary<string, object> reportedUsage = BuildPromptCacheDiagnostics(new Dictionary<string, object>
            {
                ["usage"] = new Dictionary<string, object>
                {
                    ["prompt_tokens"] = 100,
                    ["completion_tokens"] = 10,
                    ["prompt_tokens_details"] = new Dictionary<string, object> { ["cached_tokens"] = 80 }
                }
            }, new Dictionary<string, object> { ["eligible"] = true }, 1, "self_test", "self_test", "", false);
            add("prompt_cache_reported_usage", ReadBool(reportedUsage, "usageReported", false) && ReadBool(reportedUsage, "cacheHit", false) && ReadLong(reportedUsage, "cachedTokens", 0) == 80, "Reported cached-token metrics are normalized into cache diagnostics.", reportedUsage);
            Dictionary<string, object> route = new Dictionary<string, object> { ["cacheCapableRouteRequested"] = true, ["mode"] = "prefer" };
            add("prompt_cache_retry_classification", ShouldRetryWithoutCache(route, "{\"error\":{\"code\":\"no_cache_capable_provider\"}}") && !ShouldRetryWithoutCache(route, "invalid_api_key"), "Only explicit cache-route availability failures qualify for uncached fallback.", null);
            Dictionary<string, object> invalidReasoning = new Dictionary<string, object> { ["reasoningMode"] = "wrong", ["reasoningEffort"] = "maximum", ["reasoningFallbackCooldownSeconds"] = 1 };
            NormalizeReasoningSettings(invalidReasoning);
            add("reasoning_setting_validation", ReadString(invalidReasoning, "reasoningMode", "") == "selective" && ReadString(invalidReasoning, "reasoningEffort", "") == "medium" && ReadInt(invalidReasoning, "reasoningFallbackCooldownSeconds", 0) == 30, "Reasoning settings normalize to supported values and bounds.", invalidReasoning);
            Dictionary<string, object> reasoningSettings = new Dictionary<string, object> { ["reasoningMode"] = "selective", ["reasoningEffort"] = "high", ["reasoningFallbackCooldownSeconds"] = 300 };
            add("reasoning_selective_mapping", EffectiveReasoningEffort(reasoningSettings, new Dictionary<string, object>(), "dialogue") == "high" && EffectiveReasoningEffort(reasoningSettings, new Dictionary<string, object>(), "memory") == "low" && EffectiveReasoningEffort(reasoningSettings, new Dictionary<string, object>(), "action_router") == "none", "Selective reasoning applies to complex calls, caps memory at low effort, and keeps selectors direct.", null);
            Dictionary<string, object> reasoningBody = new Dictionary<string, object>();
            Dictionary<string, object> reasoningRoute = ConfigureReasoningRouting(reasoningSettings, new Dictionary<string, object>(), reasoningBody, "https://nano-gpt.com/api/v1/chat/completions", "zai-org/glm-5.2", "dialogue");
            Dictionary<string, object> reasoningControl = ReadDictionary(reasoningBody, "reasoning") ?? new Dictionary<string, object>();
            add("reasoning_nanogpt_controls", ReadBool(reasoningRoute, "providerControlsApplied", false) && ReadString(reasoningControl, "effort", "") == "high" && ReadBool(reasoningControl, "exclude", false), "NanoGPT receives hidden reasoning controls with reasoning output excluded.", reasoningRoute);
            foreach (string url in new[] { PortraitsService.NanoGptChatCompletionsUrl, "https://nano-gpt.com/api/subscription/v1/chat/completions", "https://openrouter.ai/api/v1/chat/completions" })
            {
                var disabledBody = TestDict("reasoning_effort", "high", "reasoning", TestDict("effort", "high", "exclude", false));
                var disabledRoute = ConfigureReasoningRouting(reasoningSettings, TestDict("reasoningDisabled", true), disabledBody, url, "fixture-disabled-checker", "repair");
                add("reasoning_explicit_disabled_" + PromptHash(url).Substring(0, 8),
                    !ReadBool(disabledRoute, "requested", true) && ReadBool(disabledRoute, "providerControlsApplied", false)
                    && ReadString(ReadDictionary(disabledBody, "reasoning"), "effort", "") == "none"
                    && ReadBool(ReadDictionary(disabledBody, "reasoning"), "exclude", false) && !disabledBody.ContainsKey("reasoning_effort"),
                    "Disabled checking explicitly disables reasoning and cannot be overridden by a stale extra effort field.", disabledRoute);
            }
            foreach (string mode in new[] { "off", "selective" })
            {
                var directBody = TestDict();
                ConfigureReasoningRouting(TestDict("reasoningMode", mode, "reasoningEffort", "high"), TestDict(), directBody,
                    PortraitsService.NanoGptChatCompletionsUrl, "fixture-direct", mode == "off" ? "dialogue" : "action_router");
                add("reasoning_none_is_explicit_" + mode, ReadString(ReadDictionary(directBody, "reasoning"), "effort", "") == "none",
                    "Off mode and direct-only purposes explicitly disable the provider's default thinking.", null);
            }
            var unsupportedBody = TestDict();
            ConfigureReasoningRouting(reasoningSettings, TestDict("reasoningDisabled", true), unsupportedBody,
                "https://example.invalid/v1/chat/completions", "fixture", "repair");
            add("reasoning_unrelated_provider_contract_unchanged", !unsupportedBody.ContainsKey("reasoning"), "Nano/OpenRouter controls are not added to unrelated providers.", null);
            string disabledCooldownKey = ReasoningRouteKey(PortraitsService.NanoGptChatCompletionsUrl, "fixture-disabled-cooldown");
            try
            {
                lock (PromptCacheStateLock) ReasoningFallbackCooldowns[disabledCooldownKey] = DateTime.UtcNow.AddMinutes(1);
                var cooldownBody = TestDict("reasoning_effort", "high", "reasoning", TestDict("effort", "high"));
                var disabledCooldown = ConfigureReasoningRouting(reasoningSettings, TestDict("reasoningDisabled", true), cooldownBody,
                    PortraitsService.NanoGptChatCompletionsUrl, "fixture-disabled-cooldown", "repair");
                add("reasoning_disabled_cooldown_stays_explicitly_unapplied", !ReadBool(disabledCooldown, "providerControlsApplied", true)
                    && ReadBool(disabledCooldown, "cooldownActive", false) && !cooldownBody.ContainsKey("reasoning") && !cooldownBody.ContainsKey("reasoning_effort"),
                    "Unsupported-parameter cooldown remains visible and cannot retain stale enabled controls.", disabledCooldown);
            }
            finally { lock (PromptCacheStateLock) ReasoningFallbackCooldowns.Remove(disabledCooldownKey); }
            Dictionary<string, object> retryRoute = new Dictionary<string, object> { ["providerControlsApplied"] = true };
            add("reasoning_retry_classification", ShouldRetryWithoutReasoning(retryRoute, "unsupported reasoning parameter") && !ShouldRetryWithoutReasoning(retryRoute, "invalid_api_key"), "Only explicit reasoning-parameter rejection qualifies for direct fallback.", null);
            Dictionary<string, object> rawReasoning = new Dictionary<string, object>
            {
                ["reasoning"] = "private provider trace",
                ["choices"] = new ArrayList
                {
                    new Dictionary<string, object>
                    {
                        ["message"] = new Dictionary<string, object>
                        {
                            ["reasoning_content"] = "private nested trace",
                            ["content"] = "{\"reply\":\"Good day.\",\"internalThoughts\":\"private legacy trace\",\"decisionBrief\":{\"facts\":[\"The player greeted me.\"],\"goals\":[],\"constraints\":[],\"decision\":\"Answer politely.\",\"confidence\":0.9}}"
                        }
                    }
                }
            };
            Dictionary<string, object> sanitized = SanitizeLlmResponseForStorage(rawReasoning);
            string sanitizedJson = Json.Serialize(sanitized);
            add("reasoning_storage_sanitization", sanitizedJson.IndexOf("private provider trace", StringComparison.OrdinalIgnoreCase) < 0 && sanitizedJson.IndexOf("private nested trace", StringComparison.OrdinalIgnoreCase) < 0 && sanitizedJson.IndexOf("private legacy trace", StringComparison.OrdinalIgnoreCase) < 0 && sanitizedJson.Contains("decisionBrief") && sanitizedJson.Contains("Good day."), "Provider and legacy thought traces are removed while the visible reply and compact decision brief remain.", sanitized);
            Dictionary<string, object> normalizedBrief = NormalizeDecisionBrief(new Dictionary<string, object>
            {
                ["decisionBrief"] = new Dictionary<string, object>
                {
                    ["facts"] = Enumerable.Range(1, 8).Select(x => "fact " + x).ToList(),
                    ["goals"] = Enumerable.Range(1, 5).Select(x => "goal " + x).ToList(),
                    ["constraints"] = Enumerable.Range(1, 7).Select(x => "constraint " + x).ToList(),
                    ["decision"] = "Give a measured answer.",
                    ["confidence"] = 4d
                }
            }, "", new Dictionary<string, object>());
            add("decision_brief_normalization", ReadStringList(normalizedBrief, "facts").Count == 5 && ReadStringList(normalizedBrief, "goals").Count == 3 && ReadStringList(normalizedBrief, "constraints").Count == 4 && Math.Abs(ReadDouble(normalizedBrief, "confidence", 0d) - 1d) < 0.001d, "Decision briefs enforce compact collection limits and confidence bounds.", normalizedBrief);
            results.AddRange(CharactersService.RunSkillAwarenessSelfTests());
            return results;
        }
#endif
    }
}
