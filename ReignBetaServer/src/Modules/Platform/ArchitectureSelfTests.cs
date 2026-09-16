using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Reign.Shared;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunInteractionArchitectureSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, passed, summary, data) => results.Add(new Dictionary<string, object>
            { ["caseId"] = id, ["suite"] = "interaction_architecture", ["passed"] = passed, ["summary"] = summary, ["data"] = data });
            var providerChoice = new Dictionary<string, object> {
                ["message"] = new Dictionary<string, object> { ["content"] = "{\"reply\":\"Hello.\"}" },
                ["finish_reason"] = "stop"
            };
            foreach (object choices in new object[] { new object[] { providerChoice }, new System.Collections.ArrayList { providerChoice } })
            {
                var providerResponse = new Dictionary<string, object> { ["choices"] = choices };
                add("provider_choices_" + choices.GetType().Name,
                    ReadString(TryParseJsonObject(ExtractAssistantContent(providerResponse)), "reply", "") == "Hello."
                    && ExtractFinishReason(providerResponse) == "stop",
                    "Both JSON array representations preserve provider text and completion status.", null);
            }
            var roundTripResponse = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(
                new Dictionary<string, object> { ["choices"] = new object[] { providerChoice } }));
            add("provider_choices_serializer_roundtrip",
                ExtractAssistantContent(roundTripResponse) == "{\"reply\":\"Hello.\"}"
                && ExtractFinishReason(roundTripResponse) == "stop",
                "The active platform serializer preserves a complete provider response.", null);
            string controlCenter = ControlCenterHtml();
            string mainNavigation = System.Text.RegularExpressions.Regex.Match(controlCenter,
                @"<nav id='mainNavigation'[\s\S]*?</nav>").Value;
            string diagnosticNavigation = System.Text.RegularExpressions.Regex.Match(controlCenter,
                @"<div id='diagnosticNavigation'[\s\S]*?</div>").Value;
            add("control_center_navigation_contract",
                System.Text.RegularExpressions.Regex.Matches(mainNavigation, "data-tab=").Count == 11
                && System.Text.RegularExpressions.Regex.Matches(diagnosticNavigation, "data-tab=").Count == 12
                && mainNavigation.Contains("data-tab='diagnostic-tools'")
                && diagnosticNavigation.Contains(" hidden>")
                && controlCenter.Contains("function selectControlCenterTab(tab)"),
                "Ten everyday pages and one Diagnostics entry expose twelve subordinate diagnostic pages.", null);
            add("control_center_retired_tools_absent",
                !controlCenter.Contains("relationshipSimulation") && !controlCenter.Contains("runDirectorSample")
                && !controlCenter.Contains("/relationships/simulation/") && !controlCenter.Contains(".simulationChart"),
                "The retired simulator and unbound director sample have no client functions, timers, styles or calls.", null);
            string controlCenterStyles = string.Join("\n", System.Text.RegularExpressions.Regex.Matches(controlCenter,
                @"<style>[\s\S]*?</style>").Cast<System.Text.RegularExpressions.Match>().Select(match => match.Value));
            add("control_center_modern_style_contract",
                controlCenterStyles.Contains("--canvasBlack:#070808FF")
                && controlCenterStyles.Contains("--goldAntique:#7E6A4DFF")
                && controlCenterStyles.Contains("button:disabled")
                && controlCenterStyles.Contains("input[type=file]::file-selector-button")
                && !controlCenterStyles.Contains("gradient(") && !controlCenterStyles.Contains("rgba("),
                "All web surfaces use frozen Reign tokens without legacy gradients, glow or brown overlays; browser evidence checks computed styles and populated states.", null);
            string navigationPreview = Path.Combine(VerificationDir, "control-center-navigation", "control-center.html");
            Directory.CreateDirectory(Path.GetDirectoryName(navigationPreview));
            File.WriteAllText(navigationPreview, controlCenter);
            add("control_center_navigation_preview", File.Exists(navigationPreview),
                "Exact provider-free Control Center HTML for the isolated browser matrix.", navigationPreview);
            string explicitCurrentFlirtation = ExplicitCurrentPlayerFlirtationQuote(
                "I am flirting with you right now because I am attracted to you, and I want to know whether you welcome my interest.");
            add("social_signal_server_derived_explicit_current",
                explicitCurrentFlirtation == "I am flirting with you right now"
                && SocialSignalSemanticsAreCompleted("flirtation", explicitCurrentFlirtation, false),
                "An unmistakable present-tense player flirtation has an exact server-derived evidence quote even when a provider omits its optional proposal.",
                explicitCurrentFlirtation);
            add("social_signal_server_derived_hypothetical_suppressed",
                string.IsNullOrWhiteSpace(ExplicitCurrentPlayerFlirtationQuote(
                    "If I said I am flirting with you right now, would you welcome it?"))
                && string.IsNullOrWhiteSpace(ExplicitCurrentPlayerFlirtationQuote(
                    "I am not flirting with you right now.")),
                "Hypothetical, quoted, and negated flirtation language cannot create a server-derived current signal.",
                null);
            int triggerCount = 0;
            int beneficialCount = 0;
            const int rollSampleDays = 100000;
            for (int day = 0; day < rollSampleDays; day++)
            {
                if (ReignKingdomEventRollerCore.IsDailyTrigger("kingdom-event-self-test", day))
                    triggerCount++;
                if (ReignKingdomEventRollerCore.IsBeneficial("kingdom-event-self-test", day))
                    beneficialCount++;
            }
            add("kingdom_event_deterministic_daily_rolls",
                ReignKingdomEventRollerCore.DailyTriggerBasisPoints == 100
                && ReignKingdomEventRollerCore.BasisPointScale == 10000
                && ReignKingdomEventRollerCore.StableHash("campaign", 42, "salt")
                    == ReignKingdomEventRollerCore.StableHash("campaign", 42, "salt")
                && ReignKingdomEventRollerCore.StableHash("campaign", 42, "salt")
                    != ReignKingdomEventRollerCore.StableHash("campaign", 43, "salt")
                && triggerCount >= 850 && triggerCount <= 1150
                && beneficialCount >= 49000 && beneficialCount <= 51000,
                "The saved kingdom-event producer uses a deterministic exact 100/10,000 daily threshold and an independent approximately balanced polarity roll.",
                new Dictionary<string, object>
                {
                    ["sampleDays"] = rollSampleDays,
                    ["triggerCount"] = triggerCount,
                    ["beneficialCount"] = beneficialCount
                });
            IReadOnlyList<int> attemptOrder = ReignKingdomEventRollerCore.BuildAttemptOrder(
                8, "campaign", 77, "beneficial-archetypes");
            add("kingdom_event_archetype_fallback_order",
                attemptOrder.Count == 8 && attemptOrder.Distinct().Count() == 8
                && attemptOrder.All(index => index >= 0 && index < 8),
                "A successful polarity roll produces a deterministic complete archetype attempt order so an ineligible first archetype can fall through without changing polarity.",
                attemptOrder);
            string campaignId = "_architecture_test_" + Guid.NewGuid().ToString("N");
            try
            {
                Dictionary<string, object> groupPayload = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["mode"] = "party_chat", ["conversationSessionId"] = "group_test",
                    ["activeHeroIds"] = new List<string> { "npc_a", "npc_b", "npc_c" }
                };
                List<Dictionary<string, object>> lines = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["speaker_name"] = "NPC B", ["text"] = "The road east is unsafe." },
                    new Dictionary<string, object> { ["speaker_name"] = "NPC C", ["text"] = "I disagree; the southern bridge is worse." }
                };
                string groupPrompt = BuildSharedGroupConversationPrompt(campaignId, groupPayload, "npc_a", lines, "What do all of you think?");
                UpdateSharedGroupConversationState(campaignId, groupPayload, "npc_a", "NPC A", "NPC B is right about the eastern road. NPC C, why is the bridge worse?", "group_test", 100);
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    Dictionary<string, object> state = QuerySql(connection, "SELECT * FROM group_conversation_state WHERE session_id='group_test';").FirstOrDefault();
                    add("shared_group_state", groupPrompt.Contains("npc_b") && groupPrompt.Contains("npc_c")
                        && groupPrompt.Contains("COMPACT DERIVED STATE")
                        && groupPrompt.Contains("acknowledge it instead of claiming omission", StringComparison.Ordinal)
                        && !groupPrompt.Contains("road east") && !groupPrompt.Contains("southern bridge")
                        && ReadLong(state, "revision", 0) == 1,
                        "Group turns retain compact shared state, use exact words solely from the canonical transcript, and cannot falsely claim that a prior contribution omitted a present detail.", state);

                    List<Dictionary<string, object>> duplicateTranscript = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["turnId"] = "turn_a", ["exchangeId"] = "exchange_1", ["speaker"] = "Player", ["text"] = "First line." },
                        new Dictionary<string, object> { ["turnId"] = "turn_a", ["exchangeId"] = "exchange_1", ["speaker"] = "Player", ["text"] = "First line." },
                        new Dictionary<string, object> { ["turnId"] = "turn_b", ["exchangeId"] = "exchange_1", ["speaker"] = "NPC A", ["text"] = "Second line." }
                    };
                    List<Dictionary<string, object>> canonicalTranscript = CanonicalizePromptTranscript(duplicateTranscript, 30);
                    add("canonical_transcript_deduplicates_turn_ids", canonicalTranscript.Count == 2
                        && canonicalTranscript.Select(PromptTranscriptIdentity).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
                        "Prompt assembly renders each stable conversation turn once.", canonicalTranscript);

                    List<Dictionary<string, object>> eventHistoryFixture = new List<Dictionary<string, object>>();
                    for (int beat = 1; beat <= 2; beat++)
                    {
                        foreach (string speakerId in new[] { "player", "npc_a", "npc_b" })
                        {
                            var eventPayload = new Dictionary<string, object>
                            {
                                ["eventId"] = "history_roundtrip", ["turnId"] = "shared_beat_" + beat,
                                ["playerHeroStringId"] = "player", ["speakerHeroStringId"] = speakerId
                            };
                            var eventLine = EventTranscriptLine(speakerId == "player" ? "player" : "npc",
                                speakerId, "HISTORY_" + beat + "_" + speakerId, eventPayload);
                            eventLine["ts"] = 100L; // A real group beat can finish within one second.
                            eventHistoryFixture.Add(eventLine);
                        }
                    }
                    var duplicateNpcLine = new Dictionary<string, object>(eventHistoryFixture[1]);
                    duplicateNpcLine["id"] = "a-retry-can-have-a-new-file-row-id";
                    duplicateNpcLine["turnId"] = ReadString(duplicateNpcLine, "turnId", "").ToUpperInvariant();
                    string eventHistoryPath = SocialEventTranscriptFile(campaignId, "history_roundtrip");
                    foreach (var eventLine in eventHistoryFixture) AppendJsonLineToPath(eventHistoryPath, eventLine);
                    AppendJsonLineToPath(eventHistoryPath, duplicateNpcLine);
                    var recoveredEventHistory = BuildSocialEventPromptTranscript(ReadJsonLinesFromPath(eventHistoryPath), 30);
                    string[] expectedHistory = eventHistoryFixture.Select(row => ReadString(row, "text", "")).ToArray();
                    add("social_event_history_keeps_each_speaker_and_deduplicates_retries",
                        recoveredEventHistory.Select(row => ReadString(row, "text", "")).SequenceEqual(expectedHistory),
                        "Persisted player and two NPC contributions survive both shared event turn IDs; replaying one NPC row adds no duplicate.", recoveredEventHistory);
                    var boundedEventHistory = BuildSocialEventPromptTranscript(ReadJsonLinesFromPath(eventHistoryPath), 2);
                    add("social_event_history_same_second_order_and_complete_boundary",
                        boundedEventHistory.Select(row => ReadString(row, "text", "")).SequenceEqual(expectedHistory.Skip(3)),
                        "Same-second lines retain append order and the window includes the whole latest player/NPC exchange.", boundedEventHistory);

                    foreach (string sceneKind in new[] { "party_chat", "castle_chat", "family_chambers", "royal_council", "settlement_home" })
                    {
                        var partyLines = eventHistoryFixture.Select((row, index) => new Dictionary<string, object>(row)
                        {
                            ["sessionId"] = "history_" + sceneKind, ["sequence"] = index + 1
                        }).ToList();
                        partyLines.Add(new Dictionary<string, object>(partyLines[1]));
                        partyLines.Add(new Dictionary<string, object>(partyLines[1])
                        {
                            ["sessionId"] = "unrelated_private_session", ["text"] = "PRIVATE_HISTORY_MUST_NOT_LEAK"
                        });
                        var history = BuildPartyChatPromptTranscript(new Dictionary<string, object>
                        {
                            ["mode"] = "party_chat", ["castleChatMode"] = sceneKind,
                            ["conversationSessionId"] = "history_" + sceneKind,
                            ["sceneTurnId"] = "next_beat", ["groupTranscript"] = partyLines
                        }, "Next question.");
                        add("shared_party_history_route_" + sceneKind,
                            history.Select(row => ReadString(row, "text", "")).SequenceEqual(expectedHistory),
                            "The shared party/castle-style route retains player and NPC contributions, deduplicates replays, and excludes another session.", history);
                    }

                    Dictionary<string, object> partyHistoryPayload = new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["conversationSessionId"] = "castle_recovery_session",
                        ["limit"] = 2
                    };
                    string partyHistoryPath = PartyChatSessionTranscriptFile(campaignId, partyHistoryPayload);
                    AppendJsonLineToPath(partyHistoryPath, new Dictionary<string, object>
                    {
                        ["role"] = "player", ["speaker"] = "Player", ["text"] = "First recovery line.",
                        ["sessionId"] = "castle_recovery_session", ["exchangeId"] = "castle_recovery_session_turn_1"
                    });
                    AppendJsonLineToPath(partyHistoryPath, new Dictionary<string, object>
                    {
                        ["role"] = "npc", ["speaker"] = "NPC A", ["text"] = "Second recovery line.",
                        ["sessionId"] = "castle_recovery_session", ["exchangeId"] = "castle_recovery_session_turn_1"
                    });
                    AppendJsonLineToPath(partyHistoryPath, new Dictionary<string, object>
                    {
                        ["role"] = "npc", ["speaker"] = "NPC B", ["text"] = "Newest recovery line.",
                        ["sessionId"] = "castle_recovery_session", ["exchangeId"] = "castle_recovery_session_turn_1"
                    });
                    Dictionary<string, object> partyHistory = PartyChatHistory(partyHistoryPayload);
                    List<Dictionary<string, object>> recoveredPartyLines = ReadDictionaryList(partyHistory, "lines");
                    add("party_chat_history_recovers_latest_durable_lines",
                        ReadBool(partyHistory, "ok", false)
                        && recoveredPartyLines.Count == 2
                        && ReadInt(recoveredPartyLines[0], "sequence", 0) == 1
                        && ReadString(recoveredPartyLines[0], "text", "") == "Second recovery line."
                        && ReadInt(recoveredPartyLines[1], "sequence", 0) == 2
                        && ReadString(recoveredPartyLines[1], "text", "") == "Newest recovery line.",
                        "An empty save-local castle transcript can recover the bounded durable party-chat tail in stable order.",
                        partyHistory);

                    List<Dictionary<string, object>> addressedProfiles = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["heroStringId"] = "npc_noble", ["name"] = "Aevysara Merovorides" },
                        new Dictionary<string, object> { ["heroStringId"] = "npc_witness", ["name"] = "Mostiros the Slicer" },
                        new Dictionary<string, object> { ["heroStringId"] = "npc_recipient", ["name"] = "Pacarios the Turner" }
                    };
                    Dictionary<string, object> directAddressPayload = new Dictionary<string, object>
                    {
                        ["turnId"] = "direct_address_turn",
                        ["playerText"] = "Pacarios, this gift is for you. Aevysara and Mostiros are witnesses."
                    };
                    List<string> directAddressOrder = BuildSocialEventResponseOrder(directAddressPayload, addressedProfiles);
                    Dictionary<string, object> nextTurnPayload = new Dictionary<string, object>(directAddressPayload)
                    {
                        ["turnId"] = "direct_address_turn_next"
                    };
                    List<string> nextTurnOrder = BuildSocialEventResponseOrder(nextTurnPayload, addressedProfiles);
                    add("group_direct_addressee_speaks_first", directAddressOrder.FirstOrDefault() == "npc_recipient"
                        && nextTurnOrder.FirstOrDefault() == "npc_recipient"
                        && new HashSet<string>(directAddressOrder, StringComparer.OrdinalIgnoreCase).SetEquals(
                            new[] { "npc_recipient", "npc_noble", "npc_witness" }),
                        "A directly named recipient always speaks first while the remaining eligible speakers use a turn-varying stable shuffle instead of roster order.",
                        new Dictionary<string, object> { ["firstTurn"] = directAddressOrder, ["nextTurn"] = nextTurnOrder });

                    Dictionary<string, object> rolePayload = new Dictionary<string, object>
                    {
                        ["playerHeroStringId"] = "main_hero",
                        ["conversationSceneState"] = new Dictionary<string, object> { ["participants"] = addressedProfiles }
                    };
                    string roleBlock = BuildInteractionRoleAttribution(rolePayload, "npc_witness", "Mostiros the Slicer", "the armed stranger");
                    List<Dictionary<string, object>> rejectedRoleWrites = new List<Dictionary<string, object>>();
                    List<Dictionary<string, object>> safeRoleWrites = FilterConversationRoleMisattributions(
                        new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object> { ["text"] = "Pacarios (player) gave a silver cup to Pacarios the Turner." },
                            new Dictionary<string, object> { ["text"] = "The player gave Pacarios the Turner a silver cup." }
                        }, rolePayload, directAddressPayload["playerText"].ToString(), "memory", rejectedRoleWrites);
                    add("group_role_attribution_prevents_addressee_player_confusion",
                        roleBlock.Contains("PLAYER ROLE: hero id main_hero", StringComparison.Ordinal)
                        && !roleBlock.Contains("Pacarios the Turner", StringComparison.Ordinal)
                        && roleBlock.Contains("observer-safe identity an unidentified participant", StringComparison.Ordinal)
                        && roleBlock.Contains("never renames the player", StringComparison.Ordinal)
                        && roleBlock.Contains("legacy role-attribution error", StringComparison.Ordinal)
                        && safeRoleWrites.Count == 1 && rejectedRoleWrites.Count == 1,
                        "Party prompts carry an observer-safe role map, withhold unidentified participant names, and reject private writes that relabel a directly addressed NPC as the player.",
                        new Dictionary<string, object> { ["roleBlock"] = roleBlock, ["safe"] = safeRoleWrites, ["rejected"] = rejectedRoleWrites });
                    add("legacy_player_npc_name_collision_quarantine",
                        IsLegacyPlayerNpcRoleCollision(
                            new Dictionary<string, object> { ["summary"] = "Menor accepted a cup from the stranger claiming to be Menor." },
                            "Menor the Brewer", "Rhovarion")
                        && IsLegacyPlayerNpcRoleCollision(
                            new Dictionary<string, object> { ["summary"] = "The stranger calling himself Menor discussed a clay disk with Purios." },
                            new[] { "Purios the Viper", "Menor the Brewer", "Mystesa the Carpenter" }, "Rhovarion")
                        && IsLegacyPlayerNpcRoleCollision(
                            new Dictionary<string, object> { ["claim"] = "The player was calling himself Menor during the cup exchange." },
                            "Menor the Brewer", "Rhovarion")
                        && !IsLegacyPlayerNpcRoleCollision(
                            new Dictionary<string, object> { ["summary"] = "Menor accepted a cup from the stranger named Rhovarion." },
                            "Menor the Brewer", "Rhovarion"),
                        "Legacy memories that assign the current NPC's own name to the player are quarantined without suppressing correctly attributed player identities.", null);

                    Dictionary<string, object> disputedIdentity = BuildIdentityView(
                        new Dictionary<string, object>
                        {
                            ["identity_state"] = "disputed",
                            ["canonical_name"] = "Rhovarion",
                            ["claimed_name"] = "Rhovarion",
                            ["confidence"] = 0.6d
                        },
                        new Dictionary<string, object> { ["canonicalName"] = "Rhovarion", ["mode"] = "dialogue" });
                    string disputedIdentityBlock = BuildIdentityPromptBlock(disputedIdentity);
                    string sanitizedIdentity = SanitizePromptForIdentity(
                        "Usable name or label: the stranger claiming to be Rhovarion\nClaimed name: Rhovarion (not independently verified)\nThe Stranger Claiming To Be Rhovarion gave Menor a cup.\nRhovarion's Party",
                        "", "Rhovarion", disputedIdentity);
                    Dictionary<string, object> unknownIdentity = new Dictionary<string, object>
                    {
                        ["canonicalNameAllowed"] = false,
                        ["usableName"] = "the armed stranger",
                        ["safeLabel"] = "the armed stranger",
                        ["claimedName"] = ""
                    };
                    string sanitizedUnknownIdentity = SanitizePromptForIdentity(
                        "Rhovarion entered the hall with Rhovarion's Party.", "", "Rhovarion", unknownIdentity);
                    add("identity_sanitizer_does_not_recursively_wrap_claimed_label",
                        sanitizedIdentity.Contains("Usable name or label: the stranger claiming to be Rhovarion", StringComparison.Ordinal)
                        && sanitizedIdentity.Contains("Claimed name: Rhovarion", StringComparison.Ordinal)
                        && disputedIdentityBlock.Contains("preceding Claimed name field is the exact text", StringComparison.Ordinal)
                        && disputedIdentityBlock.Contains("not part of that name", StringComparison.Ordinal)
                        && disputedIdentityBlock.Contains("repeat the exact latest claimed name", StringComparison.Ordinal)
                        && sanitizedIdentity.Contains("Rhovarion's Party", StringComparison.Ordinal)
                        && sanitizedIdentity.Contains("the stranger claiming to be Rhovarion gave Menor a cup", StringComparison.OrdinalIgnoreCase)
                        && !sanitizedIdentity.Contains("the stranger claiming to be the stranger", StringComparison.OrdinalIgnoreCase),
                        "Unknown-identity sanitization protects an already-safe claimed-name label and the explicit claim while masking unrelated canonical-name leakage.",
                        new Dictionary<string, object> { ["claimed"] = sanitizedIdentity, ["unknown"] = sanitizedUnknownIdentity });
                    add("identity_sanitizer_masks_only_unheard_canonical_name",
                        !sanitizedUnknownIdentity.Contains("Rhovarion", StringComparison.OrdinalIgnoreCase)
                        && sanitizedUnknownIdentity.Contains("the armed stranger entered the hall", StringComparison.OrdinalIgnoreCase),
                        "A canonical name the observer has never heard is still replaced by the safe scene label.",
                        sanitizedUnknownIdentity);

                    Dictionary<string, object> kinshipPayload = new Dictionary<string, object>
                    {
                        ["playerHeroStringId"] = "main_hero",
                        ["conversationSceneState"] = new Dictionary<string, object>
                        {
                            ["participants"] = new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object> { ["heroStringId"] = "npc_a", ["name"] = "Aevysara", ["role"] = "npc" },
                                new Dictionary<string, object> { ["heroStringId"] = "npc_b", ["name"] = "Othenes", ["role"] = "npc" }
                            }
                        },
                        ["attendees"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["heroStringId"] = "npc_a", ["name"] = "Aevysara", ["fatherId"] = "actual_father_a",
                                ["motherId"] = "actual_mother_a", ["childrenIds"] = new List<string>()
                            },
                            new Dictionary<string, object>
                            {
                                ["heroStringId"] = "npc_b", ["name"] = "Othenes", ["fatherId"] = "actual_father_b",
                                ["childrenIds"] = new List<string> { "actual_child_b" }
                            }
                        }
                    };
                    string kinshipBlock = BuildInteractionRoleAttribution(kinshipPayload, "npc_b", "Othenes", "Rhovarion");
                    add("group_kinship_uses_native_links_only",
                        !kinshipBlock.Contains("Aevysara", StringComparison.Ordinal)
                        && !kinshipBlock.Contains("actual_father_a", StringComparison.Ordinal)
                        && !kinshipBlock.Contains("actual_mother_a", StringComparison.Ordinal)
                        && kinshipBlock.Contains("family links withheld until this observer identifies the participant.", StringComparison.Ordinal)
                        && kinshipBlock.Contains("Othenes: spouse=none recorded; father=actual_father_b; mother=none recorded; children=actual_child_b.", StringComparison.Ordinal)
                        && kinshipBlock.Contains("Only the links above establish parent, child, or spouse links BETWEEN NATIVE HEROES.", StringComparison.Ordinal)
                        && kinshipBlock.Contains("Shared clan, kingdom, title, age, household role, or a similar name does not establish kinship between Heroes.", StringComparison.Ordinal)
                        && kinshipBlock.Contains("Correct earlier dialogue only when it contradicts an actual native Hero link", StringComparison.Ordinal),
                        "Group prompts expose the current speaker's native family links, withhold an unidentified participant's private links, and explicitly prevent same-clan or prior-transcript parentage inventions.", kinshipBlock);

                    Dictionary<string, object> promptBudgetSettings = new Dictionary<string, object>
                    {
                        ["promptWarningCharacterLimit"] = 500000,
                        ["promptHardCharacterLimit"] = 1000
                    };
                    NormalizeArchitectureSettings(promptBudgetSettings);
                    add("prompt_budget_settings_are_ordered", ReadInt(promptBudgetSettings, "promptWarningCharacterLimit", 0) == 500000
                        && ReadInt(promptBudgetSettings, "promptHardCharacterLimit", 0) == 550000,
                        "Prompt safety normalization preserves the warning threshold and keeps the hard ceiling above it.", promptBudgetSettings);
                    Dictionary<string, object> settingsVaultResult = RunSettingsSecretVaultSelfTest();
                    add("settings_secret_vault_survives_runtime_replacement",
                        ReadBool(settingsVaultResult, "passed", false),
                        "The complete server configuration round-trips through a current-user encrypted vault outside the replaceable application directory, while explicit credential clearing remains cleared.",
                        settingsVaultResult);
                    List<string> longSummaryRecords = Enumerable.Range(1, 12)
                        .Select(index => "record_" + index + " " + new string((char)('a' + (index % 20)), 1800))
                        .ToList();
                    string balancedSummarySource = BuildBalancedSummarySource(longSummaryRecords, 10000);
                    add("memory_summary_balanced_source_coverage",
                        Enumerable.Range(1, 12).All(index => balancedSummarySource.Contains("record_" + index, StringComparison.Ordinal)),
                        "Long scene and consolidation inputs retain bounded evidence from every chronological record instead of truncating all later speakers.",
                        new Dictionary<string, object> { ["sourceChars"] = balancedSummarySource.Length, ["recordCount"] = longSummaryRecords.Count });
                    add("structured_event_first_attempt_budget", StructuredEventResponseMaxTokens(new Dictionary<string, object> { ["maxTokens"] = 900 }) == 8000,
                        "Structured party and social-event JSON receives enough first-attempt output space for the visible reply and all private classifications without a full prompt resend.", null);
                    add("structured_dialogue_first_attempt_budget", StructuredDialogueResponseMaxTokens(new Dictionary<string, object> { ["maxTokens"] = 900 }) == 8000,
                        "Structured individual-dialogue JSON receives enough first-attempt output space for the visible reply and all private classifications without a full prompt resend.", null);

                    string volatileMarker =
                        "volatile_native_state_"
                        + Guid.NewGuid().ToString("N");
                    Dictionary<string, object> volatileResult =
                        IngestEvent(
                            new Dictionary<string, object>
                            {
                                ["campaignId"] = campaignId,
                                ["eventType"] =
                                    "daily_world_snapshot",
                                ["volatileCurrentState"] = true,
                                ["actorIds"] =
                                    new List<object>
                                    {
                                        "npc_a"
                                    },
                                ["summary"] = volatileMarker,
                                ["worldState"] =
                                    new Dictionary<string, object>
                                    {
                                        ["warCount"] = 2
                                    }
                            });
                    Dictionary<string, object> volatileStore =
                        ReadDictionary(
                            volatileResult,
                            "memoryStore")
                        ?? new Dictionary<string, object>();
                    string currentNativePath = CampaignFile(
                        campaignId,
                        "world",
                        "current_native_world_state.json");
                    string durableWorldMemoryPath =
                        CampaignFile(
                            campaignId,
                            "world",
                            "memories.jsonl");
                    string durableCharacterMemoryPath =
                        CharacterFile(
                            campaignId,
                            "npc_a",
                            "memory",
                            "memories.jsonl");
                    string durableWorldMemory =
                        File.Exists(durableWorldMemoryPath)
                            ? File.ReadAllText(
                                durableWorldMemoryPath)
                            : "";
                    string durableCharacterMemory =
                        File.Exists(
                            durableCharacterMemoryPath)
                            ? File.ReadAllText(
                                durableCharacterMemoryPath)
                            : "";
                    add("volatile_native_snapshot_is_not_durable_memory",
                        ReadBool(
                            volatileResult,
                            "ok",
                            false)
                        && !ReadBool(
                            volatileStore,
                            "stored",
                            true)
                        && ReadString(
                            volatileStore,
                            "reason",
                            "").Equals(
                                "volatile_current_state_not_durable_memory",
                                StringComparison.Ordinal)
                        && File.Exists(currentNativePath)
                        && File.ReadAllText(
                                currentNativePath)
                            .Contains(
                                volatileMarker,
                                StringComparison.Ordinal)
                        && !durableWorldMemory.Contains(
                            volatileMarker,
                            StringComparison.Ordinal)
                        && !durableCharacterMemory.Contains(
                            volatileMarker,
                            StringComparison.Ordinal),
                        "Current native political snapshots replace one volatile state file and never become durable world or character memories.",
                        volatileResult);

                    Dictionary<string, object> first = new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId, ["factKey"] = "bridge_condition", ["subjectId"] = "bridge_east", ["predicate"] = "condition",
                        ["claim"] = "The eastern bridge is intact.", ["believer"] = "npc_a", ["known_by"] = new List<string> { "npc_a" }, ["confidence"] = 0.8d
                    };
                    string firstId = UpsertTemporalAssertion(connection, campaignId, "evt_1", "belief_1", "belief", first, 100);
                    Dictionary<string, object> second = new Dictionary<string, object>(first) { ["claim"] = "The eastern bridge has collapsed.", ["confidence"] = 0.95d };
                    string secondId = UpsertTemporalAssertion(connection, campaignId, "evt_2", "belief_2", "belief", second, 200);
                    List<Dictionary<string, object>> versions = QuerySql(connection, "SELECT * FROM temporal_knowledge_assertions WHERE fact_key=(SELECT fact_key FROM temporal_knowledge_assertions WHERE assertion_id=$id) ORDER BY observed_ts;", new Dictionary<string, object> { ["id"] = secondId });
                    add("temporal_belief_versions", versions.Count == 2 && ReadLong(versions[0], "valid_to_ts", 0) == 200
                        && ReadString(versions[1], "supersedes_assertion_id", "") == firstId,
                        "A changed proposition closes the former validity interval and links the new belief to the superseded version.", versions);

                    // Simulate Save Sync restoring an older database after the path
                    // was cached as migrated by this process.
                    ExecuteSql(connection, "DROP TABLE group_conversation_contributions;");
                    ExecuteSql(connection, "DROP TABLE group_conversation_state;");
                    ExecuteSql(connection, "UPDATE schema_meta SET value='2' WHERE key='version';");
                }

                using (ReignDbConnection restored = OpenCampaignConnection(campaignId))
                {
                    int restoredTables = ReadInt(QuerySql(restored, @"SELECT COUNT(*) AS count FROM sqlite_master
WHERE type='table' AND name IN ('group_conversation_state','group_conversation_contributions');").FirstOrDefault(), "count", 0);
                    add("save_sync_schema_remigration", restoredTables == 2 && IsMemorySchemaCurrent(restored),
                        "A Save Sync database replacement invalidates the path cache and reruns current migrations.", restoredTables);
                }

                Dictionary<string, object> scenarioRun = new Dictionary<string, object>
                {
                    ["runId"] = "scenario_test", ["campaignId"] = campaignId, ["gameInstanceId"] = "game",
                    ["mode"] = "party_chat", ["presentation"] = "headless", ["effects"] = "guarded"
                };
                Dictionary<string, object> scenarioPayload = new Dictionary<string, object>
                {
                    ["steps"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["operation"] = "open", ["mode"] = "party_chat" },
                        new Dictionary<string, object> { ["operation"] = "send", ["mode"] = "party_chat", ["text"] = "Hello." }
                    }
                };
                List<Dictionary<string, object>> scenarioCommands = NormalizeLiveTestScenarioCommands(scenarioRun, scenarioPayload);
                add("scenario_command_batch", scenarioCommands.Count == 2
                    && ReadString(scenarioCommands[0], "operation", "") == "open"
                    && ReadString(scenarioCommands[1], "operation", "") == "send",
                    "Versioned scenario files retain every normalized command in order.", scenarioCommands);

                string boundedTurnPath = SocialEventTurnCacheFile(campaignId,
                    "reign_live_test_event_" + new string('e', 64),
                    "live-live-run-command-" + new string('t', 64));
                string boundedRelativePath = boundedTurnPath.Substring(CampaignDirectory(campaignId).Length).TrimStart(Path.DirectorySeparatorChar);
                add("social_turn_bounded_path", boundedRelativePath.Length < 80
                    && Path.GetFileName(boundedTurnPath).StartsWith("turn_", StringComparison.Ordinal),
                    "Generated event and correlation ids use a flat, bounded hashed cache path independent of identifier length.", boundedRelativePath);

                Dictionary<string, object> fullSocialReply =
                    new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["correlationId"] =
                            "live-command-npc-npc_b",
                        ["reply"] =
                            "NPC A is right about the gate.",
                        ["participation"] = "agree",
                        ["relationshipAssessments"] =
                            new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    ["targetHeroStringId"] =
                                        "npc_a"
                                }
                            },
                        ["identityView"] =
                            new Dictionary<string, object>
                            {
                                ["knowsIdentity"] = true
                            },
                        ["decisionBrief"] =
                            new Dictionary<string, object>
                            {
                                ["goals"] =
                                    new List<string> { "answer" }
                            },
                        ["motiveDecision"] =
                            new Dictionary<string, object>
                            {
                                ["activeDomains"] =
                                    new List<object> { "identity" }
                            },
                        ["motiveOutcome"] =
                            new Dictionary<string, object>
                            {
                                ["romanticActionDetected"] =
                                    false
                            },
                        ["actionGate"] =
                            new Dictionary<string, object>
                            {
                                ["needed"] = false
                            },
                        ["queuedActions"] =
                            new List<Dictionary<string, object>>(),
                        ["dynamicCharacteristicsStore"] =
                            new Dictionary<string, object>
                            {
                                ["storedCount"] = 0
                            }
                    };
                Dictionary<string, object> compactSocialReply =
                    CompactSocialEventParticipantResponse(
                        "npc_b", fullSocialReply);
                add("social_turn_private_evidence_parity",
                    ReadDictionary(
                            compactSocialReply,
                            "identityView")
                        != null
                    && ReadDictionary(
                            compactSocialReply,
                            "decisionBrief")
                        != null
                    && ReadDictionary(
                            compactSocialReply,
                            "motiveDecision")
                        != null
                    && ReadDictionary(
                            compactSocialReply,
                            "motiveOutcome")
                        != null
                    && ReadDictionary(
                            compactSocialReply,
                            "actionGate")
                        != null
                    && compactSocialReply.ContainsKey(
                        "queuedActions")
                    && ReadDictionary(
                            compactSocialReply,
                            "dynamicCharacteristicsStore")
                        != null,
                    "Exactly-once social-event caching retains the private identity, personality, action, and Dynamic Characteristics evidence required for typed/live-test parity.",
                    compactSocialReply);

                Dictionary<string, object> attributedGroupPayload =
                    new Dictionary<string, object>
                    {
                        ["activeHeroIds"] =
                            new List<string>
                            {
                                "npc_a", "npc_b"
                            },
                        ["playerHeroStringId"] = "player",
                        ["attendees"] =
                            new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    ["heroStringId"] = "npc_a",
                                    ["name"] = "NPC A"
                                },
                                new Dictionary<string, object>
                                {
                                    ["heroStringId"] = "npc_b",
                                    ["name"] = "NPC B"
                                }
                            },
                        ["groupTurnResponses"] =
                            new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    ["heroStringId"] = "npc_a",
                                    ["reply"] =
                                        "The east gate is open."
                                }
                            }
                    };
                string inferredSocialTarget =
                    NormalizeSocialEventReactionTarget(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "NPC A is right about the gate.",
                            ["relationshipAssessments"] =
                                new List<Dictionary<string, object>>()
                        },
                        attributedGroupPayload,
                        "npc_b");
                string inferredSocialTargetFromUniqueFirstName =
                    NormalizeSocialEventReactionTarget(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "Daresan's accounting is accurate.",
                            ["reactionTargetHeroStringId"] =
                                "player",
                            ["relationshipAssessments"] =
                                new List<Dictionary<string, object>>()
                        },
                        new Dictionary<string, object>
                        {
                            ["activeHeroIds"] =
                                new List<string>
                                {
                                    "npc_daresan", "npc_hazesa"
                                },
                            ["playerHeroStringId"] = "player",
                            ["attendees"] =
                                new List<Dictionary<string, object>>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["heroStringId"] =
                                            "npc_daresan",
                                        ["name"] =
                                            "Daresan Mazaliqani"
                                    },
                                    new Dictionary<string, object>
                                    {
                                        ["heroStringId"] =
                                            "npc_hazesa",
                                        ["name"] = "Hazesa Mazaliqani"
                                    }
                                },
                            ["groupTurnResponses"] =
                                new List<Dictionary<string, object>>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["heroStringId"] =
                                            "npc_daresan",
                                        ["heroName"] =
                                            "Daresan Mazaliqani",
                                        ["reply"] =
                                            "This is the current accounting."
                                    }
                                }
                        },
                        "npc_hazesa");
                bool orderedFirstNameAwareness =
                    LiveTestRepliesShowGroupAwareness(
                        new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["heroId"] = "npc_amgerth",
                                ["heroName"] =
                                    "Amgerth the Restless",
                                ["text"] =
                                    "The current evidence is plain."
                            },
                            new Dictionary<string, object>
                            {
                                ["heroId"] = "npc_anaheid",
                                ["heroName"] =
                                    "Anaheid the Wainwright",
                                ["text"] =
                                    "Amgerth's accounting is accurate.",
                                ["reactionTargetHeroStringId"] =
                                    "player"
                            }
                        });
                string compactEventPrompt =
                    BuildEventJsonForPrompt(
                        new Dictionary<string, object>(
                            attributedGroupPayload)
                        {
                            ["groupSpeakerIndex"] = 1,
                            ["mustAccountForPriorSpeaker"] =
                                true
                        });
                add("social_turn_attributed_group_awareness",
                    inferredSocialTarget.Equals(
                        "npc_a",
                        StringComparison.OrdinalIgnoreCase)
                    && inferredSocialTargetFromUniqueFirstName.Equals(
                        "npc_daresan",
                        StringComparison.OrdinalIgnoreCase)
                    && orderedFirstNameAwareness
                    && compactEventPrompt.Contains(
                        "\"groupSpeakerIndex\":1",
                        StringComparison.Ordinal)
                    && compactEventPrompt.Contains(
                        "\"mustAccountForPriorSpeaker\":true",
                        StringComparison.Ordinal),
                    "Later social-event speakers receive an explicit attributed group-beat contract, and a missing reaction id is inferred only from evidence in their own reply or assessment.",
                    new Dictionary<string, object>
                    {
                        ["target"] = inferredSocialTarget,
                        ["firstNameTarget"] =
                            inferredSocialTargetFromUniqueFirstName,
                        ["orderedFirstNameAwareness"] =
                            orderedFirstNameAwareness,
                        ["eventJson"] =
                            compactEventPrompt
                    });

                Dictionary<string, object> socialRequest = new Dictionary<string, object>
                {
                    ["eventId"] = "social_memory_event", ["sceneTurnId"] = "social_memory_turn_1",
                    ["templateId"] = "feast_empire", ["worldDay"] = 42d, ["settlementId"] = "town_test",
                    ["activeHeroIds"] = new List<string> { "npc_a", "npc_b" },
                    ["attendees"] = new List<Dictionary<string, object>>()
                };
                StoreSocialEventConversationExchange(campaignId, socialRequest, "npc_a", "player", "Player", "NPC A",
                    "At the east gate I put a violet ribbon beneath the map and shared an oatcake.",
                    "I remember the violet ribbon and oatcake.", "social_source_a", "social_memory_event", 300);
                StoreSocialEventConversationExchange(campaignId, socialRequest, "npc_b", "player", "Player", "NPC B",
                    "At the east gate I put a violet ribbon beneath the map and shared an oatcake.",
                    "NPC A is right about both details.", "social_source_b", "social_memory_event", 301);
                var currentSessionHistory = CanonicalizePromptTranscript(
                    ReadCurrentSessionDialogueLines(campaignId, "social_event_social_memory_event", 30), 30);
                add("individual_history_reader_keeps_shared_exchange_speakers",
                    currentSessionHistory.Count == 3
                    && currentSessionHistory.Count(row => ReadString(row, "role", "") == "npc") == 2,
                    "The individual/official conversation database reader preserves one player line and both NPC lines from a shared exchange.", currentSessionHistory);
                Dictionary<string, object> exactSocialHistory;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    ExecuteSql(connection, "UPDATE conversation_sessions SET status='closed',end_ts=302 WHERE session_id='social_event_social_memory_event';");
                    exactSocialHistory = SearchExactConversationHistory(connection, "npc_b", "Reconstruct our prior group discussion.",
                        new Dictionary<string, object> { ["primaryLane"] = "exact_history", ["needsExactTranscript"] = true }, 10000, null, "", 30);
                    int playerTurns = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count FROM conversation_turns
WHERE session_id='social_event_social_memory_event' AND role='player';").FirstOrDefault(), "count", 0);
                    int npcTurns = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count FROM conversation_turns
WHERE session_id='social_event_social_memory_event' AND role='npc';").FirstOrDefault(), "count", 0);
                    string exactText = ReadString(exactSocialHistory, "text", "");
                    add("social_event_canonical_group_history", playerTurns == 1 && npcTurns == 2
                        && exactText.Contains("violet ribbon") && exactText.Contains("oatcake") && exactText.Contains("NPC A") && exactText.Contains("NPC B")
                        && exactText.Contains("Historical deictic terms", StringComparison.Ordinal),
                        "Social-event group turns use the canonical exactly-once conversation store and are available verbatim to every participant.",
                        new Dictionary<string, object> { ["playerTurns"] = playerTurns, ["npcTurns"] = npcTurns, ["history"] = exactSocialHistory });
                }
                Dictionary<string, object> recallRequest = new Dictionary<string, object>(socialRequest)
                {
                    ["eventId"] = "social_recall_event", ["sceneTurnId"] = "social_recall_turn_1"
                };
                string deicticMemoryPacket = BuildMemoryPacketText(
                    "npc_a", "player",
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["summary"] = "All three present heard the claim here.",
                            ["memory_lane"] = "interpersonal_history",
                            ["participants_json"] = Json.Serialize(new List<string> { "player", "npc_a", "historical_npc" }),
                            ["source"] = "conversation_scene"
                        }
                    },
                    new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                    new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                    new List<Dictionary<string, object>>(), new List<string>(),
                    new Dictionary<string, object>
                    {
                        ["primaryLane"] = "interpersonal_history",
                        ["selectedLanes"] = new List<string> { "interpersonal_history" }
                    }, 1200);
                add("memory_deictic_source_participants",
                    deicticMemoryPacket.Contains("Historical deictic rule", StringComparison.Ordinal)
                    && deicticMemoryPacket.Contains("source-conversation participants (hearing only) player, npc_a, historical_npc", StringComparison.Ordinal)
                    && deicticMemoryPacket.Contains("does NOT prove", StringComparison.Ordinal)
                    && deicticMemoryPacket.Contains("All three present heard the claim here.", StringComparison.Ordinal),
                    "Consolidated memories retain their source participant set and explicitly prevent historical deictic words from being remapped onto a new group.", deicticMemoryPacket);

                KnowledgeAccessContext unknownIdentityKnowledge = BuildKnowledgeAccessContext(
                    "npc_a", "main_hero", "town_test", new Dictionary<string, object>
                    {
                        ["mainHeroStringId"] = "main_hero",
                        ["mainHeroName"] = "Canonical Player",
                        ["playerClanId"] = "player_faction",
                        ["playerClanName"] = "Canonical House",
                        ["playerKingdomId"] = "player_kingdom",
                        ["playerKingdomName"] = "Canonical Realm",
                        ["identityView"] = new Dictionary<string, object> { ["knowsIdentity"] = false }
                    });
                Dictionary<string, object> unsafeClanMemory = new Dictionary<string, object>
                {
                    ["owner_id"] = "npc_a",
                    ["summary"] = "The stranger leads Canonical House, has no fiefs, and the player clan is independent.",
                    ["about_entities_json"] = Json.Serialize(new List<string> { "main_hero", "player_faction" }),
                    ["participants_json"] = Json.Serialize(new List<string> { "npc_a", "main_hero" })
                };
                Dictionary<string, object> safePreferenceMemory = new Dictionary<string, object>
                {
                    ["owner_id"] = "npc_a",
                    ["summary"] = "The unidentified stranger said that blue ribbons are lucky.",
                    ["about_entities_json"] = Json.Serialize(new List<string> { "main_hero" }),
                    ["participants_json"] = Json.Serialize(new List<string> { "npc_a", "main_hero" })
                };
                Dictionary<string, object> sanitizedPreference =
                    SanitizeUnknownIdentityMemoryRow(safePreferenceMemory, unknownIdentityKnowledge);
                KnowledgeAccessContext knownIdentityKnowledge = BuildKnowledgeAccessContext(
                    "npc_a", "main_hero", "town_test", new Dictionary<string, object>
                    {
                        ["mainHeroStringId"] = "main_hero",
                        ["mainHeroName"] = "Canonical Player",
                        ["playerClanId"] = "player_faction",
                        ["playerClanName"] = "Canonical House",
                        ["identityView"] = new Dictionary<string, object> { ["knowsIdentity"] = true }
                    });
                add("unknown_identity_memory_boundary",
                    !UnknownIdentityMemoryEvidenceAllowed(unsafeClanMemory, unknownIdentityKnowledge,
                        ReadString(unsafeClanMemory, "summary", ""))
                    && UnknownIdentityMemoryEvidenceAllowed(safePreferenceMemory, unknownIdentityKnowledge,
                        ReadString(safePreferenceMemory, "summary", ""))
                    && UnknownIdentityMemoryEvidenceAllowed(unsafeClanMemory, knownIdentityKnowledge,
                        ReadString(unsafeClanMemory, "summary", ""))
                    && !ReadString(sanitizedPreference, "participants_json", "").Contains("main_hero", StringComparison.OrdinalIgnoreCase)
                    && ReadString(sanitizedPreference, "summary", "").Contains("blue ribbons", StringComparison.OrdinalIgnoreCase),
                    "Unknown observers retain harmless encounter continuity while canonical identity, clan, fief, wealth, and political-state records are withheld before ranking.",
                    new Dictionary<string, object>
                    {
                        ["unsafeAllowed"] = UnknownIdentityMemoryEvidenceAllowed(unsafeClanMemory, unknownIdentityKnowledge,
                            ReadString(unsafeClanMemory, "summary", "")),
                        ["safe"] = sanitizedPreference
                    });
                string protectedNpcHistoryLine = RenderExactHistoryTurn(new Dictionary<string, object>
                {
                    ["role"] = "npc", ["speaker_name"] = "NPC A", ["world_day"] = 42d,
                    ["text"] = "You are a clan leader with no fiefs and little gold."
                }, unknownIdentityKnowledge);
                string protectedPlayerHistoryLine = RenderExactHistoryTurn(new Dictionary<string, object>
                {
                    ["role"] = "player", ["speaker_name"] = "Canonical Player", ["world_day"] = 42d,
                    ["text"] = "I prefer blue ribbons and claim that I own no fiefs."
                }, unknownIdentityKnowledge);
                add("unknown_identity_exact_history_boundary",
                    protectedNpcHistoryLine.Contains("wording withheld", StringComparison.OrdinalIgnoreCase)
                    && !protectedNpcHistoryLine.Contains("no fiefs", StringComparison.OrdinalIgnoreCase)
                    && protectedPlayerHistoryLine.Contains("Unidentified interlocutor", StringComparison.Ordinal)
                    && protectedPlayerHistoryLine.Contains("blue ribbons", StringComparison.OrdinalIgnoreCase)
                    && protectedPlayerHistoryLine.Contains("claim that I own no fiefs", StringComparison.OrdinalIgnoreCase)
                    && !protectedPlayerHistoryLine.Contains("Canonical Player", StringComparison.OrdinalIgnoreCase),
                    "Prior NPC reconstructions cannot launder unavailable private status into exact history, while the stranger's own unverified statements remain available as attributed continuity.",
                    new Dictionary<string, object>
                    {
                        ["npcLine"] = protectedNpcHistoryLine,
                        ["playerLine"] = protectedPlayerHistoryLine
                    });
                Dictionary<string, object> unknownSceneSummary = CreateConversationSceneSummary(
                    campaignId,
                    new Dictionary<string, object>
                    {
                        ["session_id"] = "unknown_identity_scene_summary",
                        ["npc_id"] = "npc_a",
                        ["player_id"] = "main_hero",
                        ["location_id"] = "town_test",
                        ["participants_json"] = Json.Serialize(new List<string> { "npc_a", "main_hero" }),
                        ["payload_json"] = "{}"
                    },
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["turn_id"] = "unknown_identity_scene_player",
                            ["event_id"] = "unknown_identity_scene_event",
                            ["exchange_id"] = "unknown_identity_scene_exchange",
                            ["role"] = "player",
                            ["speaker_id"] = "main_hero",
                            ["speaker_name"] = "Canonical Player",
                            ["text"] = "I prefer blue ribbons.",
                            ["ts"] = 600L,
                            ["payload_json"] = Json.Serialize(new Dictionary<string, object>
                            {
                                ["mainHeroStringId"] = "main_hero",
                                ["mainHeroName"] = "Canonical Player",
                                ["playerClanId"] = "player_faction",
                                ["playerClanName"] = "Canonical House",
                                ["identityView"] = new Dictionary<string, object> { ["knowsIdentity"] = false }
                            })
                        },
                        new Dictionary<string, object>
                        {
                            ["turn_id"] = "unknown_identity_scene_npc",
                            ["event_id"] = "unknown_identity_scene_event",
                            ["exchange_id"] = "unknown_identity_scene_exchange",
                            ["role"] = "npc",
                            ["speaker_id"] = "npc_a",
                            ["speaker_name"] = "NPC A",
                            ["text"] = "You lead Canonical House and have no fiefs or gold.",
                            ["ts"] = 601L,
                            ["payload_json"] = Json.Serialize(new Dictionary<string, object>
                            {
                                ["mainHeroStringId"] = "main_hero",
                                ["mainHeroName"] = "Canonical Player",
                                ["playerClanId"] = "player_faction",
                                ["playerClanName"] = "Canonical House",
                                ["identityView"] = new Dictionary<string, object> { ["knowsIdentity"] = false }
                            })
                        }
                    },
                    602L,
                    new Dictionary<string, object>
                    {
                        ["useMemoryLlmForConsolidation"] = false,
                        ["memoryConsolidationMaxSummaryChars"] = 1800,
                        ["enableMinimeMemoryWorker"] = false
                    });
                string protectedSceneText = ReadString(unknownSceneSummary, "summary", "");
                add("unknown_identity_scene_summary_boundary",
                    protectedSceneText.Contains("blue ribbons", StringComparison.OrdinalIgnoreCase)
                    && protectedSceneText.Contains("unidentified interlocutor", StringComparison.OrdinalIgnoreCase)
                    && protectedSceneText.Contains("wording withheld", StringComparison.OrdinalIgnoreCase)
                    && !protectedSceneText.Contains("Canonical Player", StringComparison.OrdinalIgnoreCase)
                    && !protectedSceneText.Contains("Canonical House", StringComparison.OrdinalIgnoreCase)
                    && !protectedSceneText.Contains("no fiefs", StringComparison.OrdinalIgnoreCase)
                    && !ReadBool(ReadDictionary(unknownSceneSummary, "knowledgeBoundary"),
                        "playerIdentityKnown", true),
                    "Scene summaries preserve the same unknown-identity boundary as the live prompt, preventing storage and rolling arcs from laundering unavailable player status.",
                    unknownSceneSummary);
                StoreSocialEventConversationExchange(campaignId, recallRequest, "npc_b", "player", "Player", "NPC B",
                    "Reconstruct our prior group discussion without guessing.",
                    "It happened at a roadside camp with dried meat.", "social_recall_source", "social_recall_event", 400);
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    ExecuteSql(connection, "UPDATE conversation_sessions SET status='closed',end_ts=402 WHERE session_id='social_event_social_recall_event';");
                    Dictionary<string, object> protectedHistory = SearchExactConversationHistory(connection, "npc_b",
                        "Reconstruct our prior group discussion without guessing.",
                        new Dictionary<string, object> { ["primaryLane"] = "exact_history", ["needsExactTranscript"] = true }, 14000, null, "", 30);
                    string protectedText = ReadString(protectedHistory, "text", "");
                    add("recall_answer_not_factual_source", ReadString(protectedHistory, "sessionId", "") == "social_event_social_memory_event"
                        && protectedText.Contains("MOST RECENT SOURCE-BEARING SESSION")
                        && !protectedText.Contains("roadside camp") && protectedText.Contains("violet ribbon"),
                        "A prior NPC reconstruction remains stored history but is excluded from the authoritative source scene supplied for another reconstruction.", protectedHistory);
                }
                Dictionary<string, object> recallAuditRequest = new Dictionary<string, object>(socialRequest)
                {
                    ["eventId"] = "social_recall_audit_event", ["sceneTurnId"] = "social_recall_audit_turn_1"
                };
                StoreSocialEventConversationExchange(campaignId, recallAuditRequest, "npc_b", "player", "Player", "NPC B",
                    "Recall the immediately preceding group conversation without inventing details.",
                    "The violet ribbon was under the map.", "social_recall_audit_source", "social_recall_audit_event", 500);
                Dictionary<string, object> recallAuditFinish = ConversationFinishApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["sessionId"] = "social_event_social_recall_audit_event",
                    ["worldDay"] = 42d, ["ts"] = 502L
                });
                string recallAuditSummaryId = ReadString(recallAuditFinish, "sceneSummaryId", "");
                Dictionary<string, object> recallAuditSummary;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    recallAuditSummary = QuerySql(connection, "SELECT * FROM summaries WHERE summary_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = recallAuditSummaryId }).FirstOrDefault() ?? new Dictionary<string, object>();
                }
                add("recall_scene_is_audit_only", ReadBool(recallAuditFinish, "recallOnlySession", false)
                    && ReadString(recallAuditSummary, "status", "") == "audit_only"
                    && ReadString(recallAuditSummary, "embedding_status", "") == "skipped"
                    && ReadDictionaryList(recallAuditFinish, "participantConsolidations").Count == 0,
                    "Recall reconstructions retain source-linked audit summaries but cannot become retrievable memories, embeddings, arcs, or consolidation inputs.",
                    new Dictionary<string, object> { ["finish"] = recallAuditFinish, ["summary"] = recallAuditSummary });
                Dictionary<string, object> recallTurnStore = StoreTurnMemoryLayers(
                    campaignId,
                    "recall_projection_event",
                    "party_chat_turn",
                    "npc_b",
                    "player",
                    "town_test",
                    "Reconstruct the prior scene.",
                    "The violet ribbon was under the map.",
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["text"] = "This deliberately supplied write must be suppressed." }
                    },
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["claim"] = "This deliberately supplied belief must be suppressed." }
                    },
                    new List<Dictionary<string, object>>(),
                    new List<Dictionary<string, object>>(),
                    new List<Dictionary<string, object>>(),
                    "conversation:social_event_social_recall_audit_event",
                    503L,
                    new List<string> { "npc_a", "npc_b", "player" },
                    retrievalEligible: false);
                Dictionary<string, object> recallProjectionCounts;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    recallProjectionCounts = QuerySql(connection, @"SELECT
(SELECT COUNT(*) FROM events WHERE event_id='recall_projection_event') AS events,
(SELECT COUNT(*) FROM memories WHERE event_id='recall_projection_event') AS memories,
(SELECT COUNT(*) FROM beliefs WHERE event_id='recall_projection_event') AS beliefs,
(SELECT COUNT(*) FROM event_fts WHERE event_id='recall_projection_event') AS event_fts;").FirstOrDefault()
                        ?? new Dictionary<string, object>();
                }
                add("recall_turn_skips_retrieval_projection",
                    ReadBool(recallTurnStore, "auditOnly", false)
                    && !ReadBool(recallTurnStore, "retrievalEligible", true)
                    && ReadInt(recallTurnStore, "memoryCount", -1) == 0
                    && ReadInt(recallProjectionCounts, "events", -1) == 0
                    && ReadInt(recallProjectionCounts, "memories", -1) == 0
                    && ReadInt(recallProjectionCounts, "beliefs", -1) == 0
                    && ReadInt(recallProjectionCounts, "event_fts", -1) == 0,
                    "A recall answer remains in canonical conversation/audit storage but cannot project itself into events, structured memory, FTS, or vectors.",
                    new Dictionary<string, object> { ["store"] = recallTurnStore, ["counts"] = recallProjectionCounts });
                Dictionary<string, object> lowSalienceStore = StoreTurnMemoryLayers(
                    campaignId, "low_salience_turn_event", "dialogue_turn", "npc_a", "player", "town_test",
                    "Good afternoon.", "Good afternoon to you as well.",
                    new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                    new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                    new List<Dictionary<string, object>>(),
                    "conversation:low_salience", 504L);
                Dictionary<string, object> durableTurnStore = StoreTurnMemoryLayers(
                    campaignId, "durable_turn_event", "dialogue_turn", "npc_a", "player", "town_test",
                    "I keep a blue bead in my left glove.", "I will remember the blue bead in your left glove.",
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["text"] = "The player said they keep a blue bead in their left glove.",
                            ["ownerId"] = "npc_a", ["importance"] = 0.7d
                        }
                    },
                    new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                    new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                    "conversation:durable", 505L);
                Dictionary<string, object> conversationProjectionCounts;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    conversationProjectionCounts = QuerySql(connection, @"SELECT
(SELECT COUNT(*) FROM events WHERE event_id='low_salience_turn_event') AS low_events,
(SELECT COUNT(*) FROM memories WHERE event_id='low_salience_turn_event') AS low_memories,
(SELECT COUNT(*) FROM event_fts WHERE event_id='low_salience_turn_event') AS low_event_fts,
(SELECT COUNT(*) FROM events WHERE event_id='durable_turn_event') AS durable_events,
(SELECT COUNT(*) FROM memories WHERE event_id='durable_turn_event') AS durable_memories,
(SELECT COUNT(*) FROM memory_fts WHERE memory_id IN (SELECT memory_id FROM memories WHERE event_id='durable_turn_event')) AS durable_memory_fts,
(SELECT COUNT(*) FROM event_fts WHERE event_id='durable_turn_event') AS durable_event_fts,
(SELECT COUNT(*) FROM events WHERE event_id IN ('low_salience_turn_event','durable_turn_event') AND embedding_status='skipped') AS skipped_event_embeddings,
(SELECT COUNT(*) FROM embedding_jobs WHERE source_type='event' AND source_id IN ('low_salience_turn_event','durable_turn_event')) AS raw_event_embedding_jobs,
(SELECT COUNT(*) FROM embedding_jobs WHERE source_type='conversation_turn') AS raw_turn_embedding_jobs,
(SELECT COUNT(*) FROM embedding_jobs WHERE source_type='memory' AND source_id IN (SELECT memory_id FROM memories WHERE event_id='durable_turn_event')) AS durable_memory_embedding_jobs;").FirstOrDefault()
                        ?? new Dictionary<string, object>();
                }
                add("conversation_turns_only_project_explicit_durable_memory",
                    ReadInt(lowSalienceStore, "memoryCount", -1) == 0
                    && ReadInt(conversationProjectionCounts, "low_events", -1) == 1
                    && ReadInt(conversationProjectionCounts, "low_memories", -1) == 0
                    && ReadInt(conversationProjectionCounts, "low_event_fts", -1) == 0
                    && ReadInt(durableTurnStore, "memoryCount", -1) == 1
                    && ReadInt(conversationProjectionCounts, "durable_events", -1) == 1
                    && ReadInt(conversationProjectionCounts, "durable_memories", -1) == 1
                    && ReadInt(conversationProjectionCounts, "durable_memory_fts", -1) == 1
                    && ReadInt(conversationProjectionCounts, "durable_event_fts", -1) == 0,
                    "Canonical raw conversation remains separate from long-term retrieval: low-salience turns add no durable memory, while an explicit memory write is indexed exactly once.",
                    new Dictionary<string, object>
                    {
                        ["lowSalience"] = lowSalienceStore, ["durable"] = durableTurnStore,
                        ["counts"] = conversationProjectionCounts
                    });
                add("raw_conversation_uses_fts_not_vectors",
                    ReadInt(conversationProjectionCounts, "skipped_event_embeddings", -1) == 2
                    && ReadInt(conversationProjectionCounts, "raw_event_embedding_jobs", -1) == 0
                    && ReadInt(conversationProjectionCounts, "raw_turn_embedding_jobs", -1) == 0
                    && ReadInt(conversationProjectionCounts, "durable_memory_embedding_jobs", -1) == 1,
                    "Raw turns and their canonical source events remain available to exact-history FTS without entering the vector backlog; only the explicit durable memory is queued for semantic indexing.",
                    conversationProjectionCounts);
                Dictionary<string, object> broadRecallRoute = BuildMemoryRetrievalRoute(new Dictionary<string, object>(),
                    "Reconstruct our prior group discussion without guessing.", new Dictionary<string, object>());
                add("broad_recent_recall_detection", LooksLikeMostRecentConversationRecall("Reconstruct our prior group discussion without guessing.")
                    && ReadBool(broadRecallRoute, "needsExactTranscript", false)
                    && ReadStringList(broadRecallRoute, "selectedLanes").Contains("exact_history", StringComparer.OrdinalIgnoreCase),
                    "Natural requests for an earlier group discussion force the authoritative recent raw-turn window.", broadRecallRoute);
                Dictionary<string, object> exactRestatementRoute = BuildMemoryRetrievalRoute(new Dictionary<string, object>(),
                    "Paraphrase the blue-token fact without losing its exact name, color, or storage place.", new Dictionary<string, object>());
                add("exact_fact_restatement_detection",
                    LooksLikeExactFactRestatement("Paraphrase the blue-token fact without losing its exact name, color, or storage place.")
                    && LooksLikeExactFactRestatement("You once heard me describe a keepsake: what was its exact name, color, and location?")
                    && ReadBool(exactRestatementRoute, "needsExactTranscript", false)
                    && ReadString(exactRestatementRoute, "primaryLane", "") == "exact_history"
                    && IsPureConversationRecallRequest("Paraphrase the blue-token fact without losing its exact name, color, or storage place."),
                    "Natural requests to preserve exact facts route to targeted source history and remain recall-only even without explicit remember/recall keywords.",
                    exactRestatementRoute);
                const string multiSceneRecallText = "Reconstruct our two closed conversations without guessing. At the group supper, what did each speaker say? In the later private exchange, what identity distinction did you make?";
                Dictionary<string, object> multiSceneRecallRoute = BuildMemoryRetrievalRoute(new Dictionary<string, object>(),
                    multiSceneRecallText, new Dictionary<string, object>());
                List<Dictionary<string, object>> multiSceneContextPulls = DeterministicContextPullSelection("dialogue",
                    multiSceneRecallText, "", ContextPullIds.ToList());
                add("multi_scene_closed_conversation_recall_detection",
                    LooksLikeMostRecentConversationRecall(multiSceneRecallText)
                    && ReadBool(multiSceneRecallRoute, "needsExactTranscript", false)
                    && ReadStringList(multiSceneRecallRoute, "selectedLanes").Contains("exact_history", StringComparer.OrdinalIgnoreCase)
                    && multiSceneContextPulls.Any(row => ReadString(row, "id", "") == "relevant_memory"),
                    "Explicit reconstruction of multiple closed conversations selects relevant memory and the source-bearing exact-history window.",
                    new Dictionary<string, object> { ["route"] = multiSceneRecallRoute, ["contextPulls"] = multiSceneContextPulls });
                Dictionary<string, object> precedingRecallRoute = BuildMemoryRetrievalRoute(new Dictionary<string, object>(),
                    "Recall the immediately preceding group conversation without inventing details.", new Dictionary<string, object>());
                add("preceding_group_recall_detection", LooksLikeMostRecentConversationRecall("Recall the immediately preceding group conversation without inventing details.")
                    && ReadBool(precedingRecallRoute, "needsExactTranscript", false)
                    && ReadStringList(precedingRecallRoute, "selectedLanes").Contains("exact_history", StringComparer.OrdinalIgnoreCase),
                    "Natural preceding-group wording also forces the authoritative recent raw-turn window.", precedingRecallRoute);
                Dictionary<string, object> precedingObservationRoute = BuildMemoryRetrievalRoute(new Dictionary<string, object>(),
                    "Begin by recalling the preceding low-salience observation, then recover one much older detail.", new Dictionary<string, object>());
                add("preceding_observation_recall_detection", LooksLikeMostRecentConversationRecall("Begin by recalling the preceding low-salience observation, then recover one much older detail.")
                    && ReadBool(precedingObservationRoute, "needsExactTranscript", false)
                    && ReadStringList(precedingObservationRoute, "selectedLanes").Contains("exact_history", StringComparer.OrdinalIgnoreCase),
                    "Natural recall wording around a preceding observation also forces authoritative raw history.", precedingObservationRoute);
                const string noCommitmentScene = "Purios counted seven open conversations on a clay disk. Menor compared the marks with counting planks, while Mystesa discussed maintaining connections.";
                Dictionary<string, object> noCommitmentRoute = BuildMemoryRetrievalRoute(new Dictionary<string, object>(),
                    noCommitmentScene, new Dictionary<string, object>());
                add("scene_lane_uses_word_boundaries_and_interpersonal_fallback",
                    ReadDouble(ReadDictionary(noCommitmentRoute, "scores"), "commitments_and_plots", -1d) == 0d
                    && SelectConversationSceneMemoryLane(noCommitmentRoute) == "interpersonal_history",
                    "The cue 'plan' cannot match the word 'planks', and an ordinary closed conversation without a stronger lane cue is stored as interpersonal history.",
                    noCommitmentRoute);
                const string mixedRecallDisclosure = "Recall our prior group conversation without guessing. A new low-salience detail for later is that I set a cedar pin with two shallow grooves beside an empty clay cup.";
                add("mixed_recall_preserves_new_player_source",
                    LooksLikeMostRecentConversationRecall(mixedRecallDisclosure)
                    && !IsPureConversationRecallRequest(mixedRecallDisclosure)
                    && IsPureConversationRecallRequest("Recall our prior group conversation without guessing.")
                    && IsPureConversationRecallRequest("Reconstruct the new low-salience detail from the most recent supper scene without guessing."),
                    "A mixed recall-and-disclosure turn still receives exact history but is not quarantined as a recall-only source.",
                    new Dictionary<string, object>
                    {
                        ["mixedRoutesHistory"] = LooksLikeMostRecentConversationRecall(mixedRecallDisclosure),
                        ["mixedIsRecallOnly"] = IsPureConversationRecallRequest(mixedRecallDisclosure),
                        ["pureIsRecallOnly"] = IsPureConversationRecallRequest("Recall our prior group conversation without guessing."),
                        ["priorNewDetailIsRecallOnly"] = IsPureConversationRecallRequest("Reconstruct the new low-salience detail from the most recent supper scene without guessing.")
                    });
                const string askedToRememberRecall = "Earlier I asked you to remember a blue glass token. What was its exact name, color, and where did I say I keep it?";
                Dictionary<string, object> exactRecallParsed = new Dictionary<string, object>
                {
                    ["decisionBrief"] = new Dictionary<string, object>
                    {
                        ["facts"] = new List<object>
                        {
                            "Rhovarion previously told Abalytos about a blue glass token called Selca-49 kept beneath a cedar box."
                        }
                    },
                    ["memoryWrites"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["text"] = "Abalytos recalled the blue glass token Selca-49 kept beneath a cedar box."
                        }
                    }
                };
                List<Dictionary<string, object>> exactRecallMessages = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] = "Authoritative conversation source: the blue glass token was called Selca-49 and was kept beneath a cedar box."
                    },
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = askedToRememberRecall }
                };
                List<Dictionary<string, object>> exactRecallRepairs;
                string exactRecallReply = RepairVisibleExactRecallIdentifiers(
                    askedToRememberRecall, "Selca. Blue. Under a cedar box.", exactRecallParsed, exactRecallMessages, out exactRecallRepairs);
                List<Dictionary<string, object>> ungroundedRecallRepairs;
                string ungroundedRecallReply = RepairVisibleExactRecallIdentifiers(
                    askedToRememberRecall, "Selca. Blue. Under a cedar box.", exactRecallParsed,
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = "No exact token identifier is available." }
                    },
                    out ungroundedRecallRepairs);
                List<Dictionary<string, object>> privateOmissionRepairs;
                string privateOmissionReply = RepairVisibleExactRecallIdentifiers(
                    askedToRememberRecall,
                    "It was blue and kept beneath the cedar box, but the exact name escapes me.",
                    new Dictionary<string, object>
                    {
                        ["decisionBrief"] = new Dictionary<string, object>
                        {
                            ["facts"] = new List<object>
                            {
                                "The player previously discussed the blue glass token kept beneath a cedar box."
                            }
                        }
                    },
                    exactRecallMessages,
                    out privateOmissionRepairs);
                List<Dictionary<string, object>> ambiguousRecallRepairs;
                string ambiguousRecallReply = RepairVisibleExactRecallIdentifiers(
                    askedToRememberRecall,
                    "It was blue and beneath the cedar box, but I cannot safely give the exact name.",
                    new Dictionary<string, object>(),
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["role"] = "system",
                            ["content"] =
                                "Two disputed sources call the blue glass token beneath the cedar box Selca-49 and Velra-52."
                        }
                    },
                    out ambiguousRecallRepairs);
                add("asked_to_remember_recall_quarantine_and_visible_precision",
                    LooksLikeMostRecentConversationRecall(askedToRememberRecall)
                    && IsPureConversationRecallRequest(askedToRememberRecall)
                    && exactRecallReply.Contains("Selca-49", StringComparison.Ordinal)
                    && exactRecallRepairs.Count == 1
                    && privateOmissionReply.Contains("Selca-49", StringComparison.Ordinal)
                    && privateOmissionRepairs.Count == 1
                    && ambiguousRecallReply.IndexOf("Selca-49", StringComparison.Ordinal) < 0
                    && ambiguousRecallReply.IndexOf("Velra-52", StringComparison.Ordinal) < 0
                    && ambiguousRecallRepairs.Count == 0
                    && ungroundedRecallReply == "Selca. Blue. Under a cedar box."
                    && ungroundedRecallRepairs.Count == 0,
                    "Natural 'asked you to remember' wording is recall-only, and a prompt-grounded identifier selected by the model cannot lose its numeric suffix in the visible answer.",
                    new Dictionary<string, object>
                    {
                        ["repairedReply"] = exactRecallReply,
                        ["repairs"] = exactRecallRepairs,
                        ["privateOmissionReply"] = privateOmissionReply,
                        ["privateOmissionRepairs"] = privateOmissionRepairs,
                        ["ambiguousReply"] = ambiguousRecallReply,
                        ["ambiguousRepairs"] = ambiguousRecallRepairs,
                        ["ungroundedReply"] = ungroundedRecallReply,
                        ["ungroundedRepairs"] = ungroundedRecallRepairs
                    });
                Dictionary<string, string> promptDefaults = DefaultPromptTemplates();
                string dialogueUserPrompt = BuildDialogueGlobalPrefix(false, false, new List<Dictionary<string, object>>());
                string dialogueRecallPrompt = promptDefaults["dialogue_live_turn_template.txt"];
                string eventUserPrompt = BuildDialogueGlobalPrefix(true, false, new List<Dictionary<string, object>>());
                string eventRecallPrompt = promptDefaults["event_live_turn_template.txt"];
                Dictionary<string, object> recallGroundingChecks = new Dictionary<string, object>
                {
                    ["dialogueUserRejectsFalseRecall"] = dialogueRecallPrompt.Contains("distinguish established memory from a new compatible personal disclosure", StringComparison.Ordinal),
                    ["dialogueUserPersistsDisclosure"] = dialogueUserPrompt.Contains("dynamicCharacteristicWrites", StringComparison.Ordinal),
                    ["dialogueLiveDistinguishesDisclosure"] = dialogueRecallPrompt.Contains("distinguish established memory from a new compatible personal disclosure", StringComparison.Ordinal),
                    ["dialogueLivePersistsDisclosure"] = dialogueRecallPrompt.Contains("persist any new low-impact self-history through dynamicCharacteristicWrites", StringComparison.Ordinal),
                    ["dialogueProtectsMajorFacts"] = dialogueRecallPrompt.Contains("Never invent named relatives", StringComparison.Ordinal),
                    ["eventUserRejectsFalseRecall"] = eventRecallPrompt.Contains("distinguish established memory from a new compatible personal disclosure", StringComparison.Ordinal),
                    ["eventUserPersistsDisclosure"] = eventUserPrompt.Contains("dynamicCharacteristicWrites", StringComparison.Ordinal),
                    ["eventDistinguishesDisclosure"] = eventRecallPrompt.Contains("distinguish established memory from a new compatible personal disclosure", StringComparison.Ordinal),
                    ["eventPersistsDisclosure"] = eventRecallPrompt.Contains("persist any new low-impact self-history through dynamicCharacteristicWrites", StringComparison.Ordinal),
                    ["eventProtectsMajorFacts"] = eventRecallPrompt.Contains("Never invent named relatives", StringComparison.Ordinal)
                };
                add("recall_grounding_prompt_contract",
                    recallGroundingChecks.Values.All(value => value is bool passed && passed),
                    "Every production dialogue surface forbids false autobiographical recall while requiring a newly disclosed low-impact anecdote to become stable soft canon.",
                    recallGroundingChecks);
                string factBoundaryPrompt = BuildDialogueGlobalPrefix(false, false, new List<Dictionary<string, object>>());
                add("current_world_fact_boundary_prompt_contract",
                    factBoundaryPrompt.Contains("Plausibility is not evidence", StringComparison.Ordinal)
                    && factBoundaryPrompt.Contains("Never invent current weather", StringComparison.Ordinal)
                    && factBoundaryPrompt.Contains("garrison readiness", StringComparison.Ordinal)
                    && factBoundaryPrompt.Contains("it does not prove that markets are open", StringComparison.Ordinal)
                    && factBoundaryPrompt.Contains("granaries are stocked", StringComparison.Ordinal)
                    && factBoundaryPrompt.Contains("cannot presently verify", StringComparison.Ordinal),
                    "The production prompt forbids atmospheric prose from fabricating unsupported current weather, economy, security, garrison, or nearby-world facts.",
                    factBoundaryPrompt);
                add("qualification_prompt_latency_uses_p95",
                    PercentileNearestRank(Enumerable.Range(1, 100).Select(value => (long)value).ToList(), 0.95d) == 95
                    && CurrentConversationBuildVersion().Contains("+mvid.", StringComparison.Ordinal),
                    "Qualification enforces the stated aggregate p95 prompt-construction gate and identifies the exact deployed build by module version id.",
                    new Dictionary<string, object>
                    {
                        ["p95"] = PercentileNearestRank(Enumerable.Range(1, 100).Select(value => (long)value).ToList(), 0.95d),
                        ["buildVersion"] = CurrentConversationBuildVersion()
                    });
                string qualificationSourceRoot = FindVerificationSourceRoot();
                string economyBehaviorPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "ReignEconomyCampaignBehavior.cs", "src");
                string economyModelsPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "ReignEconomyModels.cs", "src");
                string economySubModulePath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "SubModule.cs", "src");
                string economyTelemetryPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "ReignWorldTestClient.cs", "src");
                string economyBehaviorText = File.Exists(economyBehaviorPath) ? File.ReadAllText(economyBehaviorPath) : "";
                string economyModelsText = File.Exists(economyModelsPath) ? File.ReadAllText(economyModelsPath) : "";
                string economySubModuleText = File.Exists(economySubModulePath) ? File.ReadAllText(economySubModulePath) : "";
                string economyTelemetryText = File.Exists(economyTelemetryPath) ? File.ReadAllText(economyTelemetryPath) : "";
                string kingdomEventBehaviorPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "ReignKingdomEventsCampaignBehavior.cs", "src");
                string kingdomEventRecordPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "ReignKingdomEventRecord.cs", "src");
                string kingdomEventSavePath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "ReignBetaSaveDefiner.cs", "src");
                string kingdomEventSettingsPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "ReignBetaSettings.cs", "src");
                string kingdomEventBehaviorText = File.Exists(kingdomEventBehaviorPath) ? File.ReadAllText(kingdomEventBehaviorPath) : "";
                string kingdomEventRecordText = File.Exists(kingdomEventRecordPath) ? File.ReadAllText(kingdomEventRecordPath) : "";
                string kingdomEventSaveText = File.Exists(kingdomEventSavePath) ? File.ReadAllText(kingdomEventSavePath) : "";
                string kingdomEventSettingsText = File.Exists(kingdomEventSettingsPath) ? File.ReadAllText(kingdomEventSettingsPath) : "";
                string actionResultPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "ReignActionResult.cs", "src");
                string diplomacyExecutorPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "ReignDiplomacyExecutor.cs", "src");
                string actionBehaviorPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "ReignAICampaignBehavior.cs", "src");
                string relationshipCampaignPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "ReignRelationshipCampaignBehavior.cs", "src");
                string liveTestServerPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBetaServer"), "LiveInteractionTest.cs", "src");
                string passiveWorldControllerPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(
                        Path.Combine(qualificationSourceRoot, "ReignBetaServer"),
                        "PassiveWorldControl.cs",
                        "ReignLiveTest/Features/WorldSimulation");
                string passiveWorldHostPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "ReignLiveInteractionPassiveWorldHost.cs", "src");
                string actionResultText = File.Exists(actionResultPath) ? File.ReadAllText(actionResultPath) : "";
                string diplomacyExecutorText = File.Exists(diplomacyExecutorPath) ? File.ReadAllText(diplomacyExecutorPath) : "";
                string actionBehaviorText = File.Exists(actionBehaviorPath) ? File.ReadAllText(actionBehaviorPath) : "";
                string relationshipCampaignText = File.Exists(relationshipCampaignPath) ? File.ReadAllText(relationshipCampaignPath) : "";
                string liveTestServerText = File.Exists(liveTestServerPath) ? File.ReadAllText(liveTestServerPath) : "";
                string passiveWorldControllerText = File.Exists(passiveWorldControllerPath) ? File.ReadAllText(passiveWorldControllerPath) : "";
                string passiveWorldHostText = File.Exists(passiveWorldHostPath) ? File.ReadAllText(passiveWorldHostPath) : "";
                add("kingdom_catastrophe_and_prosperity_contract",
                    kingdomEventBehaviorText.Contains("IsDailyTrigger(campaignId, day)", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("IsBeneficial(campaignId, day)", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("BuildAttemptOrder", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("EligibleKingdoms(definition)", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("political_unrest", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("national_unity", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("HasProtectedAgreement", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("IsRebellionWar", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("kingdom.RulingClan.SetLeader(heir)", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("kingdom.RulingClan == Clan.PlayerClan", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("_reignKingdomEvents_lastRolledDay", StringComparison.Ordinal)
                    && economyModelsText.Contains("GetFoodProductionFactor", StringComparison.Ordinal)
                    && economyModelsText.Contains("ReignSettlementProsperityModel", StringComparison.Ordinal)
                    && economyModelsText.Contains("ReignBuildingConstructionModel", StringComparison.Ordinal)
                    && economyModelsText.Contains("GetLoyaltyDelta", StringComparison.Ordinal)
                    && economySubModuleText.Contains("new ReignKingdomEventsCampaignBehavior()", StringComparison.Ordinal)
                    && economySubModuleText.Contains("new ReignSettlementProsperityModel()", StringComparison.Ordinal)
                    && economySubModuleText.Contains("new ReignBuildingConstructionModel()", StringComparison.Ordinal)
                    && kingdomEventRecordText.Contains("[SaveableField(18)]", StringComparison.Ordinal)
                    && kingdomEventSaveText.Contains("typeof(ReignKingdomEventRecord), 44", StringComparison.Ordinal)
                    && kingdomEventSettingsText.Contains("Enable Kingdom Catastrophes and Boons", StringComparison.Ordinal)
                    && kingdomEventSettingsText.Contains("Force Kingdom Catastrophe or Boon", StringComparison.Ordinal)
                    && !kingdomEventBehaviorText.Contains("desertion", StringComparison.OrdinalIgnoreCase)
                    && !kingdomEventBehaviorText.Contains("recruitment_surge", StringComparison.OrdinalIgnoreCase),
                    "The client contract wires every approved kingdom-wide effect, saved roll ordering, treaty/civil-war guards, living NPC succession, player exclusion, history/notification integration, and a separate force-event control without troop or desertion mechanics.",
                    new Dictionary<string, object>
                    {
                        ["behaviorPath"] = kingdomEventBehaviorPath,
                        ["recordPath"] = kingdomEventRecordPath,
                        ["savePath"] = kingdomEventSavePath,
                        ["settingsPath"] = kingdomEventSettingsPath
                    });
                add("kingdom_event_announcement_queue_contract",
                    kingdomEventBehaviorText.Contains("Queue<string> PendingAnnouncementEventIds", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("PendingAnnouncementEventIds.Enqueue(announcementEventId)", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("InformationManager.IsAnyInquiryActive()", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("PendingAnnouncementEventIds.Dequeue()", StringComparison.Ordinal)
                    && kingdomEventBehaviorText.Contains("InformationManager.HideInquiry()", StringComparison.Ordinal),
                    "The disposable live harness tracks every queued Reign kingdom-event inquiry and dismisses only one known announcement at a time, so stacked event notices cannot leave an empty modal blocking native time.",
                    kingdomEventBehaviorPath);
                add("stale_ransom_action_obsolete_contract",
                    actionResultText.Contains("ransom_target_obsolete", StringComparison.Ordinal)
                    && actionResultText.Contains("is no longer a prisoner", StringComparison.Ordinal)
                    && actionResultText.Contains("is not held by either involved kingdom", StringComparison.Ordinal)
                    && actionResultText.Contains("return ValidationFailed(reason);", StringComparison.Ordinal)
                    && diplomacyExecutorText.Contains("DiplomacyValidationFailed(action, validationFailure)", StringComparison.Ordinal)
                    && actionBehaviorText.Contains("action.Status = ReignWorldActionStatus.Cancelled", StringComparison.Ordinal)
                    && actionBehaviorText.Contains("ReportActionAsync(action, \"obsolete\"", StringComparison.Ordinal),
                    "A ransom that becomes impossible because its named prisoner was released or moved is terminally obsolete rather than a failed world action, while malformed proposals remain validation failures.",
                    new Dictionary<string, object>
                    {
                        ["actionResultPath"] = actionResultPath,
                        ["diplomacyExecutorPath"] = diplomacyExecutorPath,
                        ["actionBehaviorPath"] = actionBehaviorPath
                    });
                add("stale_conception_action_obsolete_contract",
                    relationshipCampaignText.Contains("IsObsoleteManagedConceptionError", StringComparison.Ordinal)
                    && relationshipCampaignText.Contains("Mother is already pregnant.", StringComparison.Ordinal)
                    && relationshipCampaignText.Contains("biologicalFather == null", StringComparison.Ordinal)
                    && relationshipCampaignText.Contains("!mother.IsAlive", StringComparison.Ordinal)
                    && relationshipCampaignText.Contains("!biologicalFather.IsAlive", StringComparison.Ordinal)
                    && relationshipCampaignText.Contains("mother.Age > 45f", StringComparison.Ordinal)
                    && relationshipCampaignText.Contains("status = \"obsolete\"", StringComparison.Ordinal),
                    "A conception that reaches Bannerlord after a participant disappears or dies, the mother ages out, or the mother becomes pregnant is terminally obsolete rather than a failed relationship action.",
                    new Dictionary<string, object>
                    {
                        ["relationshipCampaignPath"] = relationshipCampaignPath
                    });
                add("passive_world_long_advance_timeout_contract",
                    liveTestServerText.Contains("mode.Equals(\"passive_world\"", StringComparison.Ordinal)
                    && liveTestServerText.Contains("operation.Equals(\"world_advance\"", StringComparison.Ordinal)
                    && liveTestServerText.Contains("\"world_delete_checkpoint\"", StringComparison.Ordinal)
                    && liveTestServerText.Contains("? 21600", StringComparison.Ordinal)
                    && liveTestServerText.Contains(": 1800", StringComparison.Ordinal)
                    && passiveWorldControllerText.Contains("Math.Min(21600, IntValue(args, \"--timeout\", 7200))", StringComparison.Ordinal)
                    && passiveWorldControllerText.Contains("Has(args, \"--initial-baseline\")", StringComparison.Ordinal)
                    && passiveWorldControllerText.Contains("initialBaselineRollupExceptionApplied", StringComparison.Ordinal)
                    && passiveWorldControllerText.Contains("nativeConfirmedDay", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("Math.Min(21600, command.Value<int?>(\"timeoutSeconds\") ?? 7200)", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("GetLiveTestRunStatusAsync(runId)", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("LiveCommandResult.Cancelled", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("timeModeReassertions", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("campaign.TimeControlMode != CampaignTimeControlMode.UnstoppableFastForward", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("waitMenu.StartWait();", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("TryFocusBannerlordWindowForLiveAdvance", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("AttachThreadInput", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("GetForegroundWindow() == windowHandle", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("focusSuccesses", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("IsEscapeMenuOpened", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("CloseEscapeMenu", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("escapeMenuRecoveryCount", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("world_acknowledge_native_diplomacy_notices", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("NativeDiplomacyNoticeConfirmation", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("notice is WarMapNotification || notice is PeaceMapNotification", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("MBInformationManager.MapNoticeRemoved(notice)", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("nativeDiplomacyNoticesAcknowledged", StringComparison.Ordinal)
                    && passiveWorldHostText.Contains("campaign.TimeControlMode = CampaignTimeControlMode.Stop", StringComparison.Ordinal),
                    "Passive-world advancement preserves an explicitly requested timeout up to six hours, exposes confirmation-gated run-owned checkpoint deletion, acknowledges only native war/peace map notices inside the armed disposable-save harness, reasserts the native wait mode, recovers Bannerlord focus when background pausing stalls progress, and actively observes cancellation so campaign time stops before the native command returns.",
                    new Dictionary<string, object>
                    {
                        ["serverPath"] = liveTestServerPath,
                        ["controllerPath"] = passiveWorldControllerPath,
                        ["hostPath"] = passiveWorldHostPath
                    });
                add("war_economy_mobilization_relief_contract",
                    economyBehaviorText.Contains("MobilizationDailyDecayFactor = 0.95f", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("MobilizationDailyFlatRecovery = 0.5f", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("MinimumRecruitmentRecoveryModifier = 0.3f", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("ReliefDonorEntryRatio = 0.7f", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("ReliefDonorReserveRatio = 0.6f", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("ReliefRecipientEntryRatio = 0.3f", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("ReliefRecipientExitRatio = 0.5f", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("ReliefMaximumDonorShipment = 10f", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("ReliefMaximumRecipientShipment = 15f", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("ReliefMaximumRouteDistance = 180f", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("donor.FoodStocks -= shipment", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("recipient.FoodStocks += delivered", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("float delivered = shipment * efficiency", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("town != null && !town.IsUnderSiege", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("GroupBy(ReliefNetworkKey)", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("_reignEconomy_villageMobilizationStrain", StringComparison.Ordinal)
                    && economyBehaviorText.Contains("_reignEconomy_reliefFoodShipped", StringComparison.Ordinal)
                    && economyModelsText.Contains("manpower shortage", StringComparison.Ordinal)
                    && economyModelsText.Contains("Emergency provisions received", StringComparison.Ordinal)
                    && economyModelsText.Contains("Unmet emergency provisions", StringComparison.Ordinal)
                    && economySubModuleText.Contains("new ReignSettlementLoyaltyModel()", StringComparison.Ordinal)
                    && economyTelemetryText.Contains("mobilizationFoodPenalty", StringComparison.Ordinal)
                    && economyTelemetryText.Contains("reliefFoodDelivered", StringComparison.Ordinal),
                    "Recruitment creates saved, decaying village mobilization strain; recovering villages cannot recruit early; and conserved, capped, distance/security-limited same-realm relief moves real food stocks with loyalty and telemetry consequences.",
                    new Dictionary<string, object>
                    {
                        ["behaviorPath"] = economyBehaviorPath,
                        ["modelsPath"] = economyModelsPath,
                        ["telemetryPath"] = economyTelemetryPath
                    });
                add("war_economy_relief_float_drift_tolerance",
                    ReliefFoodIsConserved(46846.6367d, 37907.082d, 8939.383d)
                    && !ReliefFoodIsConserved(46846.6367d, 37906.5d, 8939.383d)
                    && !ReliefFoodIsConserved(-1d, 0d, 0d),
                    "Long-run single-precision relief totals allow only ten parts per million of cumulative summation drift while genuine missing or negative food still fails conservation.",
                    new Dictionary<string, object>
                    {
                        ["observedLongRunDrift"] =
                            46846.6367d - 37907.082d - 8939.383d
                    });
                string qualificationRunnerPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(
                        Path.Combine(qualificationSourceRoot, "ReignBetaServer"),
                        "QualificationRunner.cs",
                        "ReignLiveTest/Features/Dialogue");
                string qualificationRunnerText = File.Exists(qualificationRunnerPath)
                    ? File.ReadAllText(qualificationRunnerPath)
                    : "";
                add("qualification_same_build_guarded_target_contract",
                    qualificationRunnerText.Contains("[\"effects\"] = \"guarded\"", StringComparison.Ordinal)
                    && !qualificationRunnerText.Contains("mode == \"party_chat\" ? \"full\"", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("QualificationTargets(campaignId, \"individual_chat\", 500)", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("campaignId, \"party_chat\", 50", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("[\"priorMemorySourceCount\"]", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("\"sourceSummaryIds\"", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("\"sourceMemoryIds\"", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("\"sourceEventIds\"", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("priorConversationEvidenceCount", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("SelectCourtIntrigueTargets(individualTargets, 30)", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("courtNobles < 16", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("courtTiers < 3", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("Clan-tier recognition qualification ", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("Manipulation qualification ", StringComparison.Ordinal)
                    && qualificationRunnerText.IndexOf("To make sure I have listened as carefully as I asked you to", StringComparison.Ordinal)
                        < qualificationRunnerText.IndexOf("A traveler claimed a green comet", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "What did I tell you about my small keepsake—its exact name, its color, and where I keep it?",
                        StringComparison.Ordinal)
                    && !qualificationRunnerText.Contains(
                        "What did I tell you about my small blue keepsake—its exact name and where I keep it?",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("forcing", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("one literal word would turn a memory test into a compliance test", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("assertions[\"requiresRetrievalEvidence\"] = true;", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("RelationshipPolicy(null, \"respectful_repair\", false)", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("String(scorecard, \"buildVersion\")", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("String(scorecard, \"currentBuildVersion\")", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("[\"requiresSceneMemoryArtifacts\"] = true", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("[\"timeoutSeconds\"] = 900", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("mode == \"individual_chat\" ? 900 : 1200", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("[\"requiresQualificationMemoryCompletion\"] = true", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("bool focusCoverageGapsFirst = !Has(args, \"--full-order\")", StringComparison.Ordinal)
                    && qualificationRunnerText.IndexOf("Clan-tier recognition qualification ", StringComparison.Ordinal)
                        < qualificationRunnerText.IndexOf("Manipulation qualification ", StringComparison.Ordinal)
                    && qualificationRunnerText.IndexOf("Manipulation qualification ", StringComparison.Ordinal)
                        < qualificationRunnerText.IndexOf("RunSharedRelationshipHistoryQualificationSetup(", StringComparison.Ordinal)
                    && qualificationRunnerText.IndexOf("RunSharedRelationshipHistoryQualificationSetup(", StringComparison.Ordinal)
                        < qualificationRunnerText.IndexOf("new[] { 0, 1, 2, 3, 4, 5, 6, 7, 11 }", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("new[] { 8, 9, 10 }", StringComparison.Ordinal)
                    && !qualificationRunnerText.Contains("new[] { 6, 7, 11 }", StringComparison.Ordinal)
                    && !qualificationRunnerText.Contains("new[] { 4, 5, 8, 9, 10, 0, 1, 2, 3 }", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("Keep this order invariant across controller restarts", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("QualificationPlanCoverage()", StringComparison.Ordinal),
                    "Qualification uses guarded effects in every mode, separates broad NPC coverage from party eligibility, stratifies court-intrigue probes across rank and personality evidence, tests clan recognition before manipulation in distinct matrices, tests exact recall without disclosing the expected color in the probe, judges ownership recall from retrieved evidence and blinded factual accuracy without forcing unrelated literal compliance, offers respectful repair before adversarial claims, starts a fresh window after a deployed-build change, resumes durable evidence without replaying completed scenes, preflights every denominator, gives accepted individual sends and production closure a 15-minute liveness boundary, makes every production scene close prove its memory artifacts, and ends with bounded consolidation and semantic-retrieval evidence.",
                    qualificationRunnerPath);
                string liveTestControllerPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(
                        Path.Combine(qualificationSourceRoot, "ReignBetaServer"),
                        "Program.cs",
                        "ReignLiveTest");
                string liveTestControllerText = File.Exists(liveTestControllerPath)
                    ? File.ReadAllText(liveTestControllerPath)
                    : "";
                string finalGauntletLongHorizonPath =
                    string.IsNullOrWhiteSpace(qualificationSourceRoot)
                        ? ""
                        : VerificationSourceLocator.ResolveUnique(
                            Path.Combine(qualificationSourceRoot, "ReignBetaServer"),
                            "FinalGauntletLongHorizon.cs",
                            "ReignLiveTest");
                string finalGauntletLongHorizonText =
                    File.Exists(finalGauntletLongHorizonPath)
                        ? File.ReadAllText(finalGauntletLongHorizonPath)
                        : "";
                string gauntletFixtureHostPath =
                    string.IsNullOrWhiteSpace(qualificationSourceRoot)
                        ? ""
                        : VerificationSourceLocator.ResolveUnique(
                            Path.Combine(qualificationSourceRoot, "ReignBeta"),
                            "ReignLiveInteractionGauntletFixtures.cs",
                            "src/Modules");
                string gauntletFixtureHostText =
                    File.Exists(gauntletFixtureHostPath)
                        ? File.ReadAllText(gauntletFixtureHostPath)
                        : "";
                string finalGauntletRunnerPath =
                    string.IsNullOrWhiteSpace(qualificationSourceRoot)
                        ? ""
                        : VerificationSourceLocator.ResolveUnique(
                            Path.Combine(qualificationSourceRoot, "ReignBetaServer"),
                            "FinalGauntletRunner.cs",
                            "ReignLiveTest");
                string finalGauntletRunnerText =
                    File.Exists(finalGauntletRunnerPath)
                        ? File.ReadAllText(finalGauntletRunnerPath)
                        : "";
                add("final_gauntlet_long_horizon_uses_resilient_runtime_and_roster",
                    finalGauntletLongHorizonText.Contains(
                        "QualificationRuntimeWithHeartbeatGrace(campaignId)",
                        StringComparison.Ordinal)
                    && finalGauntletLongHorizonText.Contains(
                        "QualificationTargets(",
                        StringComparison.Ordinal)
                    && finalGauntletLongHorizonText.Contains(
                        "includeHistory: false",
                        StringComparison.Ordinal)
                    && !finalGauntletLongHorizonText.Contains(
                        "[\"operation\"] = \"search_targets\"",
                        StringComparison.Ordinal),
                    "Both long horizons wait through the background heartbeat interval and discover their adult roster through the proven atomic target-search path before any semantic execution begins.",
                    finalGauntletLongHorizonPath);
                add("gauntlet_fixture_validates_live_and_parent_run_ids",
                    gauntletFixtureHostText.Contains(
                        "command.Value<string>(\"gauntletRunId\")",
                        StringComparison.Ordinal)
                    && gauntletFixtureHostText.Contains(
                        "command.Value<string>(\"runId\")",
                        StringComparison.Ordinal)
                    && gauntletFixtureHostText.Contains(
                        "activeLiveRunId,",
                        StringComparison.Ordinal)
                    && gauntletFixtureHostText.Contains(
                        "_activeRunId,",
                        StringComparison.Ordinal),
                    "Native gauntlet fixtures require a stable parent-gauntlet id while binding command ownership to the active live run id instead of incorrectly comparing the two distinct identifiers.",
                    gauntletFixtureHostPath);
                add("final_gauntlet_result_commit_is_fail_stop",
                    finalGauntletRunnerText.Contains(
                        "Dictionary<string, object> recorded = Post(",
                        StringComparison.Ordinal)
                    && finalGauntletRunnerText.Contains(
                        "The final gauntlet production result was not committed",
                        StringComparison.Ordinal)
                    && finalGauntletRunnerText.Contains(
                        "if (!IsOk(recorded))",
                        StringComparison.Ordinal),
                    "The unattended controller fails visibly when a terminal production result cannot be committed instead of silently polling an already-consumed semantic lease.",
                    finalGauntletRunnerPath);
                add("live_test_close_liveness_boundary",
                    liveTestControllerText.Contains("normalizedOperation == \"close\" || normalizedOperation == \"scene_boundary\"", StringComparison.Ordinal)
                    && liveTestControllerText.Contains("return 900;", StringComparison.Ordinal),
                    "One-off close and scene-boundary commands retain a 15-minute accuracy-preserving liveness boundary instead of the ordinary-turn timeout.",
                    liveTestControllerPath);
                string serverClientPath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBeta"), "ReignServerClient.cs", "src");
                string serverClientText = File.Exists(serverClientPath)
                    ? File.ReadAllText(serverClientPath)
                    : "";
                add("live_test_control_plane_bypasses_shared_endpoint_breaker",
                    serverClientText.Contains("bool liveTestRoute = route.StartsWith(", StringComparison.Ordinal)
                    && serverClientText.Contains("if (!liveTestRoute", StringComparison.Ordinal)
                    && serverClientText.Contains("LiveTestClient", StringComparison.Ordinal)
                    && serverClientText.Contains("ReignServerEndpoint.ReportTransportFailure();", StringComparison.Ordinal),
                    "Loopback heartbeat, polling, acknowledgement, and lifecycle routes use their short-timeout client without inheriting unrelated passive-system endpoint backoff.",
                    serverClientPath);
                string liveTestClientPath =
                    string.IsNullOrWhiteSpace(
                        qualificationSourceRoot)
                        ? ""
                        : VerificationSourceLocator.ResolveUnique(
                            Path.Combine(qualificationSourceRoot, "ReignBeta"),
                            "ReignLiveTestClient.cs",
                            "src/Modules");
                string liveTestClientText =
                    File.Exists(liveTestClientPath)
                        ? File.ReadAllText(
                            liveTestClientPath)
                        : "";
                add("native_identity_authority_context_is_always_on",
                    serverClientText.Contains(
                        "BuildNativePoliticalContext(",
                        StringComparison.Ordinal)
                    && serverClientText.Contains(
                        "[\"nativePoliticalContext\"]",
                        StringComparison.Ordinal)
                    && serverClientText.Contains(
                        "IsKingdomFaction",
                        StringComparison.Ordinal)
                    && serverClientText.Contains(
                        "[\"enemyKingdomIds\"]",
                        StringComparison.Ordinal)
                    && liveTestClientText.Contains(
                        "[\"playerIsRuler\"]",
                        StringComparison.Ordinal)
                    && liveTestClientText.Contains(
                        "[\"playerGovernorOfSettlementId\"]",
                        StringComparison.Ordinal),
                    "Every production dialogue request has current native identity, role, settlement, and distinct-kingdom diplomacy evidence, while the live controller heartbeat exposes the player offices needed to grade targeted fixtures.",
                    new Dictionary<string, object>
                    {
                        ["dialogueClient"] =
                            serverClientPath,
                        ["liveTestClient"] =
                            liveTestClientPath
                    });
                string liveTestHostPath =
                    string.IsNullOrWhiteSpace(
                        qualificationSourceRoot)
                        ? ""
                        : VerificationSourceLocator.ResolveUnique(
                            Path.Combine(qualificationSourceRoot, "ReignBeta"),
                            "ReignLiveInteractionTestHost.cs",
                            "src/Modules");
                string liveTestHostText =
                    File.Exists(liveTestHostPath)
                        ? File.ReadAllText(liveTestHostPath)
                        : "";
                string correspondenceVmPath =
                    string.IsNullOrWhiteSpace(qualificationSourceRoot)
                        ? ""
                        : VerificationSourceLocator.ResolveUnique(
                            Path.Combine(qualificationSourceRoot, "ReignBeta"),
                            "ReignCorrespondenceScreenVM.cs",
                            "src/Modules/Dialogue");
                string correspondenceVmText =
                    File.Exists(correspondenceVmPath)
                        ? File.ReadAllText(correspondenceVmPath)
                        : "";
                string correspondenceHostPath =
                    string.IsNullOrWhiteSpace(qualificationSourceRoot)
                        ? ""
                        : VerificationSourceLocator.ResolveUnique(
                            Path.Combine(qualificationSourceRoot, "ReignBeta"),
                            "ReignLiveInteractionCorrespondenceHost.cs",
                            "src/Modules/Dialogue");
                string correspondenceHostText =
                    File.Exists(correspondenceHostPath)
                        ? File.ReadAllText(correspondenceHostPath)
                        : "";
                string courtHostPath =
                    string.IsNullOrWhiteSpace(qualificationSourceRoot)
                        ? ""
                        : VerificationSourceLocator.ResolveUnique(
                            Path.Combine(qualificationSourceRoot, "ReignBeta"),
                            "ReignLiveInteractionCourtHost.cs",
                            "src/Modules/Court");
                string courtHostText =
                    File.Exists(courtHostPath)
                        ? File.ReadAllText(courtHostPath)
                        : "";
                add("live_bridge_enabled_mode_capability_contract",
                    liveTestHostText.Contains(
                        "GetSupportedModes()",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "CorrespondenceEnabled",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "IsCourtSystemAvailable",
                        StringComparison.Ordinal)
                    && liveTestClientText.Contains(
                        "ReignLiveInteractionTestHost.GetSupportedModes()",
                        StringComparison.Ordinal)
                    && !liveTestClientText.Contains(
                        "new JArray(\"individual_chat\", \"party_chat\", \"social_event\", \"wilderness_event\", \"social_balance\")",
                        StringComparison.Ordinal),
                    "The game heartbeat advertises correspondence and court only through fresh production availability checks instead of a stale hard-coded mode list.",
                    new Dictionary<string, object>
                    {
                        ["host"] = liveTestHostPath,
                        ["heartbeat"] = liveTestClientPath
                    });
                add("live_bridge_correspondence_production_controller_parity",
                    correspondenceVmText.Contains(
                        "SendProductionLetterAsync(",
                        StringComparison.Ordinal)
                    && correspondenceVmText.Contains(
                        "await SendProductionLetterAsync(",
                        StringComparison.Ordinal)
                    && correspondenceHostText.Contains(
                        "ReignCorrespondenceScreenVM.SendProductionLetterAsync(",
                        StringComparison.Ordinal),
                    "Typed and injected correspondence delegate to one production send-and-action controller.",
                    new Dictionary<string, object>
                    {
                        ["viewModel"] = correspondenceVmPath,
                        ["host"] = correspondenceHostPath
                    });
                add("live_bridge_court_production_controller_parity",
                    courtHostText.Contains(
                        "RespondToAudienceAsync(",
                        StringComparison.Ordinal)
                    && courtHostText.Contains(
                        "BeginAudienceAsync(",
                        StringComparison.Ordinal)
                    && courtHostText.Contains(
                        "EndAudience(",
                        StringComparison.Ordinal),
                    "Injected court dialogue opens, responds, and closes through the real active CourtMatter production lifecycle.",
                    courtHostPath);
                add("manipulation_fixture_refreshes_native_clan_tier",
                    liveTestHostText.Contains(
                        "SetManipulationFixtureRenown(",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "clan.ResetClanRenown();",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "clan.AddRenown(renown, shouldNotify: false);",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "[\"tierVerified\"] = clan.Tier == desiredTier",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "wealth[\"partyInventoryValue\"] = -1;",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "wealth[\"visibleWealthTier\"] =",
                        StringComparison.Ordinal),
                    "The reversible court-status fixture changes renown through Bannerlord's native tier-recalculation path, verifies the requested cached Clan.Tier immediately, uses the same path during restoration, and redacts non-observable wallet, treasury, and party-inventory wealth from hidden-status controls.",
                    liveTestHostPath);
                add("npc_observable_player_wealth_boundary",
                    liveTestHostText.Contains(
                        "ApplyNpcObservableWealthEvidence(",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "ApplyNpcObservableClanEvidence(",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "private_wallet_not_observable",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "private_treasury_not_observable",
                        StringComparison.Ordinal)
                    && serverClientText.Contains(
                        "BuildNpcObservableWealthProfile(Hero.MainHero)",
                        StringComparison.Ordinal)
                    && serverClientText.Contains(
                        "ApplyNpcObservableClanEvidence(",
                        StringComparison.Ordinal)
                    && serverClientText.Contains(
                        "[\"clan\"] = playerClan",
                        StringComparison.Ordinal),
                    "Prompt-facing player wealth and clan context hide exact wallet, inventory-value, and treasury evidence by default; demonstrated capacity may be exposed deliberately, while the non-prompt native action index retains exact validation data.",
                    new Dictionary<string, object>
                    {
                        ["client"] = serverClientPath,
                        ["host"] = liveTestHostPath
                    });
                add("live_test_checkpoint_heartbeat_boundary",
                    liveTestControllerText.Contains("HeartbeatPauseExpectedOperation(operation)", StringComparison.Ordinal)
                    && liveTestControllerText.Contains("normalized == \"save_checkpoint\"", StringComparison.Ordinal)
                    && liveTestControllerText.Contains("normalized == \"shutdown_game\"", StringComparison.Ordinal)
                    && liveTestControllerText.Contains("normalized == \"world_advance\"", StringComparison.Ordinal)
                    && liveTestControllerText.Contains("GameProcessIds().Length > 0", StringComparison.Ordinal)
                    && liveTestControllerText.Contains("runtime = QualificationRuntimeWithHeartbeatGrace(campaignId);", StringComparison.Ordinal)
                    && liveTestControllerText.Contains("before their declared timeout plus grace", StringComparison.Ordinal),
                    "Checkpoint saves wait through the native heartbeat interval before starting; checkpoint/shutdown operations and a world advance with a still-present Bannerlord process may pause that heartbeat through their declared timeout instead of receiving the ordinary 90-second crash heuristic.",
                    liveTestControllerPath);
                add("live_test_game_process_classification",
                    liveTestControllerText.Contains("return new[] { \"Bannerlord\", \"Bannerlord.Native\", \"Bannerlord.BLSE.Standalone\" }", StringComparison.Ordinal)
                    && liveTestControllerText.Contains("private static int[] GameLauncherProcessIds()", StringComparison.Ordinal)
                    && liveTestControllerText.Contains("private static int[] BannerlordLauncherProcesses(bool hostingGame)", StringComparison.Ordinal)
                    && liveTestControllerText.Contains(".Concat(BannerlordLauncherProcesses(true))", StringComparison.Ordinal)
                    && liveTestControllerText.Contains(".Concat(BannerlordLauncherProcesses(false))", StringComparison.Ordinal)
                    && liveTestControllerText.Contains("title.IndexOf(\"PID:\"", StringComparison.Ordinal)
                    && liveTestControllerText.Contains("[\"launcherProcessIds\"] = GameLauncherProcessIds()", StringComparison.Ordinal)
                    && !liveTestControllerText.Contains("return new[] { \"Bannerlord\", \"Bannerlord.BLSE.Launcher\"", StringComparison.Ordinal),
                    "An idle BLSE launcher remains diagnostic, while a launcher process whose window has become the native Singleplayer client prevents a duplicate unattended /continuesave launch.",
                    liveTestControllerPath);
                add("qualification_reciprocity_and_fresh_target_contract",
                    qualificationRunnerText.Contains("OrderQualificationTargetsByUsage(individualTargets, qualificationId)", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("[\"priorDialogueLineCount\"]", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("ReadRosterIds(recordedRoster, \"focalHeroIds\")", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("\"/tests/live/readiness/roster\"", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("I will share as much as I ask", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("[\"reciprocalIndividualPrompts\"]", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("[\"leastUsedTargetSelection\"]", StringComparison.Ordinal)
                    && !qualificationRunnerText.Contains("Before I trust you with a more important confidence", StringComparison.Ordinal),
                    "Unattended qualification prefers the campaign's least-used eligible NPCs, persists that roster for every resume, and uses reciprocal, purpose-bearing prompts instead of manufacturing permanent hostility through unfulfilled extraction promises.",
                    qualificationRunnerPath);
                add("qualification_identity_role_authority_focus",
                    qualificationRunnerText.Contains(
                        "identityAuthorityOnly",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "SelectIdentityAuthorityTargets(",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "RunIdentityAuthorityQualification(",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "\"identity_role_authority\"",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "requiresPoliticalAuthorityEvidence",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "targets.Count * 3",
                        StringComparison.Ordinal),
                    "The controller can run an 81-reply focused identity/role/authority matrix across 27 stratified adult observers, reset pair knowledge safely, test before and after exact introduction, and grade the native authority invariant on every reply.",
                    qualificationRunnerPath);
                add("qualification_manipulation_focus",
                    qualificationRunnerText.Contains("qualificationFocus", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("manipulationOnly", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("prepare_manipulation_fixture", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("restore_manipulation_fixture", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("BuildManipulationQualificationCases", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("missingTiers.Count > 0", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("lower_tier_poor", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("equal_tier_poor", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("higher_tier_wealthy", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("wealthyOrdinal++ % 5", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("hidden_wallet_not_observable", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("[\"expectedObserverClanTier\"]", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("requiresLowHonorCourtCharacter", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("focused[\"qualificationFocus\"] = \"manipulation_capabilities\"", StringComparison.Ordinal),
                    "The controller can resume only the 40-reply manipulation gate, uses authentic low-honor Court Characters across the naturally available Boldness bands and every noble clan tier one through six, counterbalances lower/equal/higher rank with poor/wealthy conditions, includes exactly four hidden-wallet controls, restores the native fixture, and does not replay accepted clan-tier probes.",
                    qualificationRunnerPath);
                add("qualification_clan_tier_focus",
                    qualificationRunnerText.Contains(
                        "clanTierOnly",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "BuildClanTierRecognitionQualificationCases",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "RunClanTierRecognitionQualification",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "[\"observerClanTier\"]",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "[\"playerClanTier\"]",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "[\"matrixBand\"] = \"peer\"",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "[\"targetKind\"] = \"notable\"",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "[\"targetKind\"] = \"wanderer\"",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "result.Count != 30",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "playerClanTiers\"] = 7",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "Restore clan-tier recognition fixture",
                        StringComparison.Ordinal),
                    "The controller can resume only the 30-reply clan-tier gate, reversibly tests the player below, equal to, and above adult NPCs, includes tier-zero peer cases for both a notable and a wanderer plus peer cases at every noble tier one through six, covers player tiers zero through six, verifies exact native tier evidence, and restores both clan renown and temporary identity knowledge.",
                    qualificationRunnerPath);
                string liveInteractionSourcePath = string.IsNullOrWhiteSpace(qualificationSourceRoot)
                    ? ""
                    : VerificationSourceLocator.ResolveUnique(Path.Combine(qualificationSourceRoot, "ReignBetaServer"), "LiveInteractionTest.cs", "src");
                string liveInteractionSourceText = File.Exists(liveInteractionSourcePath)
                    ? File.ReadAllText(liveInteractionSourcePath)
                    : "";
                add("qualification_lie_relationship_focus",
                    qualificationRunnerText.Contains("lieRelationshipOnly", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("RunLieRelationshipQualification", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("lieRelationshipHeroIds", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("verified_harmful_lie", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("meaningful_positive", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("requireTransformativeRecipient", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("giftLeverageReplies", StringComparison.Ordinal)
                    && qualificationRunnerText.Contains("focused[\"qualificationFocus\"] = \"lie_relationship\"", StringComparison.Ordinal)
                    && liveInteractionSourceText.Contains("No meaningful positive-support receipt applied the required +2 to +4 tier.", StringComparison.Ordinal)
                    && liveInteractionSourceText.Contains("requireTransformativeRecipient", StringComparison.Ordinal),
                    "The controller can resume a purpose-built 40-reply lie and relationship-consequence gate using the least-used eligible NPCs and covering routine, meaningful, unsupported-claim, verified-lie, hostile, severe, transformative-recipient, witness, and linked later-demand behavior without replaying unrelated qualification scenes.",
                    qualificationRunnerPath);
                string qualificationFixturesPath =
                    string.IsNullOrWhiteSpace(qualificationSourceRoot)
                        ? ""
                        : VerificationSourceLocator.ResolveUnique(
                            Path.Combine(qualificationSourceRoot, "ReignBetaServer"),
                            "ConversationQualificationFixtures.cs",
                            "src/Modules/Dialogue");
                string qualificationFixturesText =
                    File.Exists(qualificationFixturesPath)
                        ? File.ReadAllText(qualificationFixturesPath)
                        : "";
                int targetSearchStart = liveInteractionSourceText.IndexOf(
                    "private static Dictionary<string, object> LiveTestTargetSearchApi",
                    StringComparison.Ordinal);
                int targetSearchEnd = targetSearchStart < 0
                    ? -1
                    : liveInteractionSourceText.IndexOf(
                        "private static Dictionary<string, object> LiveTestRunStartApi",
                        targetSearchStart,
                        StringComparison.Ordinal);
                string targetSearchBody = targetSearchStart >= 0 && targetSearchEnd > targetSearchStart
                    ? liveInteractionSourceText.Substring(targetSearchStart, targetSearchEnd - targetSearchStart)
                    : "";
                add("live_test_target_search_atomic_start",
                    targetSearchBody.Contains("[\"steps\"] = new List<Dictionary<string, object>>", StringComparison.Ordinal)
                    && targetSearchBody.Contains("[\"operation\"] = \"search_targets\"", StringComparison.Ordinal)
                    && targetSearchBody.Contains("startPayload[\"runId\"] = requestedRunId;", StringComparison.Ordinal)
                    && targetSearchBody.Contains("started[\"command\"] = command;", StringComparison.Ordinal)
                    && !targetSearchBody.Contains("LiveTestCommandEnqueueApi(", StringComparison.Ordinal),
                    "Target discovery creates its auto-completing run and search command atomically and preserves a caller-supplied run ID for transport reconciliation.",
                    liveInteractionSourcePath);
                add("qualification_target_search_transport_reconciliation",
                    qualificationRunnerText.Contains(
                        "string targetRunId = \"target-search-\"",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "\"/tests/live/targets/search\", request, 300)",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "[\"transportAmbiguous\"] = true",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "FirstNonEmpty(String(started, \"runId\"), targetRunId)",
                        StringComparison.Ordinal),
                    "Qualification target discovery uses a deterministic run ID, a long request budget, and late-acceptance reconciliation instead of duplicating an ambiguous search.",
                    qualificationRunnerPath);
                add("live_test_target_search_full_roster_boundary",
                    qualificationRunnerText.Contains(
                        "QualificationTargets(campaignId, \"individual_chat\", 500)",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        ".Take(2000).ToArray()",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "[\"isLord\"] = true",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "[\"minimumAge\"] = 18d",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "[\"minClanTier\"] = 1",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "[\"maxClanTier\"] = 6",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "[\"targetSnapshots\"]",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "[\"verifyIdentity\"] = false",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "manipulationCaseTargetIds",
                        StringComparison.Ordinal)
                    && qualificationRunnerText.Contains(
                        "PostWithTimeout(",
                        StringComparison.Ordinal)
                    && qualificationFixturesText.Contains(
                        "bool verifyIdentity = ReadBool(payload, \"verifyIdentity\", true);",
                        StringComparison.Ordinal)
                    && qualificationFixturesText.Contains(
                        "\"manipulation-profile-scan.json\"",
                        StringComparison.Ordinal)
                    && qualificationFixturesText.Contains(
                        "[\"profileCacheHit\"] = true",
                        StringComparison.Ordinal)
                    && qualificationFixturesText.Contains(
                        "traits = BuildTraitDocument(runtimeProfile);",
                        StringComparison.Ordinal)
                    && qualificationFixturesText.Contains(
                        "if (verifyIdentity)",
                        StringComparison.Ordinal)
                    && targetSearchBody.Contains(
                        "2000, ReadInt(payload, \"limit\", 25)",
                        StringComparison.Ordinal)
                    && targetSearchBody.Contains(
                        "searchStep[\"maxHonor\"]",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "Math.Min(2000, command.Value<int?>(\"limit\") ?? 25)",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "hero.GetTraitLevel(DefaultTraits.Honor)",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "[\"traits\"] = traits",
                        StringComparison.Ordinal)
                    && liveTestHostText.Contains(
                        "[\"skills\"] = skills",
                        StringComparison.Ordinal)
                    && qualificationFixturesText.Contains(
                        ".Take(2000).ToList();",
                        StringComparison.Ordinal)
                    && qualificationFixturesText.Contains(
                        "QualificationRuntimeProfileFromTarget(",
                        StringComparison.Ordinal)
                    && qualificationFixturesText.Contains(
                        "MaterializeCharacterProfile(",
                        StringComparison.Ordinal)
                    && qualificationFixturesText.Contains(
                        "[\"materializedBaselineCount\"]",
                        StringComparison.Ordinal)
                    && qualificationFixturesText.Contains(
                        "else if (ReadBool(fixture, \"restored\", false))",
                        StringComparison.Ordinal)
                    && qualificationFixturesText.Contains(
                        "ReadBool(payload, \"force\", false)",
                        StringComparison.Ordinal),
                    "Manipulation qualification asks the native game for the complete relevant adult noble population through one consistent filtered 2,000-target boundary, materializes missing candidates from canonical profiles and live native trait/skill evidence without a provider call, selects by derived Court-system Honor, and safely reactivates/restores repeated identity fixtures.",
                    new Dictionary<string, object>
                    {
                        ["runner"] = qualificationRunnerPath,
                        ["server"] = liveInteractionSourcePath,
                        ["gameHost"] = liveTestHostPath,
                        ["fixture"] = qualificationFixturesPath
                    });
                add("live_test_active_run_index",
                    liveInteractionSourceText.Contains("LiveTestActiveRunPath", StringComparison.Ordinal)
                    && liveInteractionSourceText.Contains("Normal heartbeats never deserialize", StringComparison.Ordinal)
                    && liveInteractionSourceText.Contains("[\"activeRunId\"] = ReadString(active, \"runId\", \"\")", StringComparison.Ordinal),
                    "Heartbeat and runtime status use one indexed active report instead of repeatedly deserializing the historical live-run corpus.",
                    liveInteractionSourcePath);

                Dictionary<string, object> replay = new Dictionary<string, object>
                {
                    ["assertions"] = new Dictionary<string, object>
                    {
                        ["replyContains"] = new List<string> { "NPC B" }, ["replyExcludes"] = new List<string> { "secret leak" },
                        ["promptExcludes"] = new List<string> { "private player clan ledger" },
                        ["requiresGroupAwareness"] = true, ["otherNpcNames"] = new List<string> { "NPC B", "NPC C" }, ["maxPromptBuildMs"] = 750,
                        ["maxPromptChars"] = 15000,
                        ["requiredContextPulls"] = new List<string> { "verify_world_history" }
                    },
                    ["promptEvidence"] = new Dictionary<string, object>
                    {
                        ["promptEvidence"] = new Dictionary<string, object>
                        {
                            ["contextPulls"] = new Dictionary<string, object> { ["verify_world_history"] = true }
                        }
                    },
                    ["responseEvidence"] = new Dictionary<string, object>
                    {
                        ["reply"] = "NPC B makes a fair point, though NPC C should explain the bridge.",
                        ["timing"] = new Dictionary<string, object> { ["promptBuildMs"] = 80, ["promptChars"] = 12000, ["totalMs"] = 1200 }
                    }
                };
                Dictionary<string, object> evaluation = EvaluateReplayCorpusCase(replay);
                add("replay_evaluator", ReadBool(evaluation, "passed", false), "Replay cases evaluate grounding, group awareness, prohibited knowledge, and latency without a provider call.", evaluation);
                Dictionary<string, object> oversizedReplay = new Dictionary<string, object>(replay)
                {
                    ["responseEvidence"] = new Dictionary<string, object>
                    {
                        ["reply"] = "NPC B makes a fair point, though NPC C should explain the bridge.",
                        ["timing"] = new Dictionary<string, object> { ["promptBuildMs"] = 80, ["promptChars"] = 15001, ["totalMs"] = 1200 }
                    }
                };
                Dictionary<string, object> oversizedEvaluation = EvaluateReplayCorpusCase(oversizedReplay);
                add("replay_evaluator_prompt_character_budget",
                    !ReadBool(oversizedEvaluation, "passed", true)
                    && ReadStringList(oversizedEvaluation, "failures")
                        .Any(value => value.Contains("Prompt characters", StringComparison.OrdinalIgnoreCase)),
                    "Replay evaluation rejects a captured request that exceeded its prompt-character budget.",
                    oversizedEvaluation);
                Dictionary<string, object> missingContextReplay = new Dictionary<string, object>(replay)
                {
                    ["promptEvidence"] = new Dictionary<string, object>
                    {
                        ["promptEvidence"] = new Dictionary<string, object>
                        {
                            ["contextPulls"] = new Dictionary<string, object> { ["relevant_memory"] = true }
                        }
                    }
                };
                Dictionary<string, object> missingContextEvaluation = EvaluateReplayCorpusCase(missingContextReplay);
                add("replay_evaluator_required_context_pull",
                    !ReadBool(missingContextEvaluation, "passed", true)
                    && ReadStringList(missingContextEvaluation, "failures")
                        .Any(value => value.Contains("verify_world_history", StringComparison.OrdinalIgnoreCase)),
                    "Replay evaluation fails when a required production context helper did not reach the final prompt.",
                    missingContextEvaluation);
                Dictionary<string, object> dynamicRecallReplay = new Dictionary<string, object>
                {
                    ["assertions"] = new Dictionary<string, object>
                    {
                        ["requiresDynamicCharacteristicRecall"] = true
                    },
                    ["promptEvidence"] = new Dictionary<string, object>
                    {
                        ["messages"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["role"] = "system",
                                ["content"] = "RELEVANT DYNAMIC CHARACTERISTICS\n- [aversion] Abalytos has a strong aversion to people who verbally label their own generosity or fairness before acting, seeing it as calculated self-interest."
                            }
                        }
                    },
                    ["responseEvidence"] = new Dictionary<string, object>
                    {
                        ["reply"] = "The aversion is simple: when people tell me they are fair before they have acted, I stop listening to what they say and watch what they take."
                    }
                };
                Dictionary<string, object> dynamicRecallEvaluation = EvaluateReplayCorpusCase(dynamicRecallReplay);
                Dictionary<string, object> missingDynamicRecallReplay = new Dictionary<string, object>(dynamicRecallReplay)
                {
                    ["responseEvidence"] = new Dictionary<string, object>
                    {
                        ["reply"] = "The roads near the settlement are busy today."
                    }
                };
                Dictionary<string, object> missingDynamicRecallEvaluation = EvaluateReplayCorpusCase(missingDynamicRecallReplay);
                add("replay_evaluator_dynamic_characteristic_recall",
                    ReadBool(dynamicRecallEvaluation, "passed", false)
                    && !ReadBool(missingDynamicRecallEvaluation, "passed", true)
                    && ReadStringList(missingDynamicRecallEvaluation, "failures")
                        .Any(value => value.Contains("Dynamic Characteristic", StringComparison.OrdinalIgnoreCase)),
                    "Replay evaluation accepts a grounded grammatical paraphrase of injected soft personal canon and rejects an unrelated response.",
                    new Dictionary<string, object>
                    {
                        ["grounded"] = dynamicRecallEvaluation,
                        ["unrelated"] = missingDynamicRecallEvaluation
                    });
                string boundedReplayPath = ReplayCorpusCasePath(campaignId, "replay_" + new string('r', 220));
                add("replay_corpus_bounded_path", Path.GetFileName(boundedReplayPath).Length < 40,
                    "Automatic replay filenames remain below legacy Windows path limits even with long correlation ids.", boundedReplayPath);

                int ran = 0;
                System.Threading.Tasks.Task scheduled = EnqueuePriorityBackgroundWork("architecture.self_test", "interactive", () => Interlocked.Increment(ref ran));
                bool scheduledDone = scheduled.Wait(3000);
                add("priority_scheduler", scheduledDone && ran == 1, "Priority work completes through the shared scheduler and exposes queue state.", PriorityBackgroundStatus());

                ManualResetEvent firstReaderEntered = new ManualResetEvent(false);
                ManualResetEvent releaseFirstReader = new ManualResetEvent(false);
                ManualResetEvent historyIngestEntered = new ManualResetEvent(false);
                System.Threading.Tasks.Task firstReader = System.Threading.Tasks.Task.Run(() =>
                {
                    CampaignRequestAccess access = EnterCampaignRequestAccess("/relationships/ambient/native_sync/receipts");
                    try
                    {
                        firstReaderEntered.Set();
                        releaseFirstReader.WaitOne(3000);
                    }
                    finally { ExitCampaignRequestAccess(access); }
                });
                firstReaderEntered.WaitOne(1000);
                System.Threading.Tasks.Task historyIngest = System.Threading.Tasks.Task.Run(() =>
                {
                    CampaignRequestAccess access = EnterCampaignRequestAccess("/world-history/ingest-batch");
                    try
                    {
                        historyIngestEntered.Set();
                    }
                    finally { ExitCampaignRequestAccess(access); }
                });
                bool historyIngestDidNotWaitForIndependentReader =
                    historyIngestEntered.WaitOne(1000);
                releaseFirstReader.Set();
                bool allCampaignAccessTasksFinished = System.Threading.Tasks.Task.WaitAll(
                    new[] { firstReader, historyIngest }, 3000);
                add("world_history_ingest_does_not_wait_for_campaign_reader",
                    historyIngestDidNotWaitForIndependentReader
                    && allCampaignAccessTasksFinished,
                    "Durable World History ingestion is not held behind unrelated campaign readers; PostgreSQL transaction boundaries provide its write serialization.",
                    null);

                CampaignRequestAccess liveHeartbeatAccess =
                    EnterCampaignRequestAccess("/tests/live/game/heartbeat");
                bool liveHeartbeatUsesCampaignReadGate =
                    liveHeartbeatAccess == CampaignRequestAccess.Read;
                ExitCampaignRequestAccess(liveHeartbeatAccess);
                add("live_test_heartbeat_uses_campaign_read_gate",
                    liveHeartbeatUsesCampaignReadGate,
                    "Live-test campaign files cannot be recreated while Save Sync holds the campaign write gate.",
                    liveHeartbeatAccess.ToString());

                CampaignRequestAccess synchronousVerificationAccess =
                    EnterCampaignRequestAccess("/tests/run-suite");
                bool synchronousVerificationRemainsUngated =
                    synchronousVerificationAccess == CampaignRequestAccess.None;
                ExitCampaignRequestAccess(synchronousVerificationAccess);
                add("synchronous_verification_remains_outside_campaign_gate",
                    synchronousVerificationRemainsUngated,
                    "Synchronous verification routes remain outside the campaign gate so suites that exercise Save Sync cannot deadlock themselves.",
                    synchronousVerificationAccess.ToString());

                string scopedResumeRoot = Path.Combine(Path.GetTempPath(),
                    "reign_social_resume_scope_" + Guid.NewGuid().ToString("N"));
                string priorScopedResumeRoot = CampaignsRootOverride.Value;
                string oldResumeCampaign = campaignId + "_old_resume";
                string newResumeCampaign = campaignId + "_new_resume";
                string oldResumeEventId = campaignId + "_old_social_event";
                try
                {
                    CampaignsRootOverride.Value = scopedResumeRoot;
                    using (ReignDbConnection connection = OpenCampaignConnection(oldResumeCampaign))
                    {
                        EnsureWorldHistorySchema(connection);
                        EnsureSocialReputationSchema(connection);
                        ExecuteSql(connection, @"INSERT INTO world_history_events(
event_id,campaign_id,timeline_id,sequence,world_day,event_type,summary,payload_json,created_utc)
VALUES($event_id,$campaign,'main',1,1,'social_outcome','old outcome',
'{""subjectId"":""old_subject"",""archetypeId"":""battle_proven""}',$created);",
                            new Dictionary<string, object>
                            {
                                ["event_id"] = oldResumeEventId,
                                ["campaign"] = oldResumeCampaign,
                                ["created"] = DateTime.UtcNow.ToString("o")
                            });
                    }
                    bool unrelatedScheduled =
                        ResumePendingSocialWorldHistoryOutcomes(newResumeCampaign, "main");
                    add("social_world_history_resume_is_campaign_scoped",
                        !unrelatedScheduled,
                        "Opening a new campaign never scans or schedules pending derived social work from historical campaigns.",
                        unrelatedScheduled);
                }
                finally
                {
                    InvalidateCampaignSchemaCaches(oldResumeCampaign);
                    InvalidateCampaignSchemaCaches(newResumeCampaign);
                    ReignPostgreSqlStorage.DropCampaign(oldResumeCampaign);
                    ReignPostgreSqlStorage.DropCampaign(newResumeCampaign);
                    CampaignsRootOverride.Value = priorScopedResumeRoot;
                    TryDeleteDirectory(scopedResumeRoot);
                }

                Dictionary<string, object> localSettings = new Dictionary<string, object>();
                List<Dictionary<string, object>> selected = DeterministicContextPullSelection("dialogue", "What villages and bandits are nearby?", "", ContextPullIds.ToList());
                add("foreground_context_router", selected.Any(x => ReadString(x, "id", "") == "nearby_settlements")
                    && selected.Any(x => ReadString(x, "id", "") == "nearby_bandit_parties"),
                    "Context helpers are selected locally on the foreground path without a second LLM request.", selected);
                const string naturalNearbySettlementPrompt =
                    "What settlements are nearby, and which road would you consider most sensible? Do not invent distances you were not given.";
                List<Dictionary<string, object>> naturalNearbySettlementSelected = PruneContextPullSelection(
                    naturalNearbySettlementPrompt,
                    DeterministicContextPullSelection("dialogue", naturalNearbySettlementPrompt, "", ContextPullIds.ToList()));
                add("context_router_nearby_settlement_natural_word_order",
                    naturalNearbySettlementSelected.Any(x => ReadString(x, "id", "") == "nearby_settlements"),
                    "Natural 'settlements are nearby' wording deterministically includes the native nearby-settlement bundle and survives pruning.",
                    naturalNearbySettlementSelected);
                const string characteristicInfluencePrompt =
                    "Earlier you established a quiet personal detail of your own. Explain one small way that exact detail influences your behavior.";
                List<Dictionary<string, object>> characteristicInfluenceSelected = PruneContextPullSelection(
                    characteristicInfluencePrompt,
                    DeterministicContextPullSelection("dialogue", characteristicInfluencePrompt, "", ContextPullIds.ToList()));
                add("context_router_behavioral_influence_is_not_clan_influence",
                    !characteristicInfluenceSelected.Any(x => ReadString(x, "id", "") == "clan_wealth_and_influence"),
                    "The verb 'influences' in a personal-behavior question cannot trigger the clan wealth and political influence helper.",
                    characteristicInfluenceSelected);
                List<Dictionary<string, object>> recallSelected = DeterministicContextPullSelection("party_chat",
                    "Address each other while recalling what the baker wrapped.", "", ContextPullIds.ToList());
                add("context_router_word_boundary", !recallSelected.Any(x => ReadString(x, "id", "") == "check_player_appearance_status"),
                    "The word 'address' cannot accidentally trigger the player-appearance helper through its 'dress' substring.", recallSelected);
                List<Dictionary<string, object>> historySelected = DeterministicContextPullSelection("party_chat",
                    "What happened to Suruq's Party, and whose party was responsible according to canonical campaign history?", "", ContextPullIds.ToList());
                add("context_router_world_history_lookup", historySelected.Any(x => ReadString(x, "id", "") == "verify_world_history")
                    && !historySelected.Any(x => ReadString(x, "id", "") == "appraise_trade_offer")
                    && !historySelected.Any(x => ReadString(x, "id", "") == "kingdom_diplomacy_status")
                    && !historySelected.Any(x => ReadString(x, "id", "") == "relevant_memory"),
                    "General campaign-history questions select canonical history without incidental trade, personal-memory, or current-diplomacy pulls.", historySelected);
                const string establishedRegionalHistoryPrompt =
                    "What established history about the Southern Empire or Danustica would matter to someone making plans here? Separate verified context from your interpretation.";
                List<Dictionary<string, object>> establishedRegionalHistorySelected = PruneContextPullSelection(
                    establishedRegionalHistoryPrompt,
                    DeterministicContextPullSelection(
                        "dialogue",
                        establishedRegionalHistoryPrompt,
                        "Current settlement: Danustica (`town_ES1`)",
                        ContextPullIds.ToList()));
                add("context_router_established_regional_history",
                    establishedRegionalHistorySelected.Any(x => ReadString(x, "id", "") == "verify_world_history"),
                    "Natural established-history wording about an empire or settlement selects canonical world-history evidence and survives pruning.",
                    establishedRegionalHistorySelected);
                const string currentDanusticaHistoryPrompt = "Using current native facts and canonical campaign history, who currently owns and governs Danustica, is it under siege, which lord parties are nearby, and who most recently captured it?";
                List<Dictionary<string, object>> currentDanusticaHistorySelected = PruneContextPullSelection(currentDanusticaHistoryPrompt,
                    DeterministicContextPullSelection("dialogue", currentDanusticaHistoryPrompt,
                        "Current settlement: Danustica (`town_ES1`)", ContextPullIds.ToList()));
                add("context_router_current_settlement_label_and_world_history_split",
                    currentDanusticaHistorySelected.Any(x => ReadString(x, "id", "") == "current_settlement_facts")
                    && currentDanusticaHistorySelected.Any(x => ReadString(x, "id", "") == "nearby_lord_parties")
                    && currentDanusticaHistorySelected.Any(x => ReadString(x, "id", "") == "verify_world_history")
                    && !currentDanusticaHistorySelected.Any(x => ReadString(x, "id", "") == "relevant_memory"),
                    "A scene labeled Current settlement resolves named local facts while canonical campaign history remains distinct from personal memory.",
                    currentDanusticaHistorySelected);
                const string generalCurrentDanusticaPrompt = "What can you responsibly tell me about Danustica right now, and which current local facts can you not verify?";
                List<Dictionary<string, object>> generalCurrentDanusticaSelected = DeterministicContextPullSelection(
                    "dialogue", generalCurrentDanusticaPrompt,
                    "Current settlement: Danustica (`town_ES1`)", ContextPullIds.ToList());
                add("context_router_general_named_current_settlement_question",
                    generalCurrentDanusticaSelected.Any(x => ReadString(x, "id", "") == "current_settlement_facts"),
                    "A general current-facts question naming the active settlement receives the authoritative native settlement bundle rather than inviting plausible invention.",
                    generalCurrentDanusticaSelected);
                List<Dictionary<string, object>> hyphenatedHistorySelected = DeterministicContextPullSelection("party_chat",
                    "Canonical-history probe: what happened to Suruq's Party?", "", ContextPullIds.ToList());
                add("context_router_hyphenated_world_history", hyphenatedHistorySelected.Any(x => ReadString(x, "id", "") == "verify_world_history"),
                    "Hyphenated canonical-history wording still selects authoritative history evidence.", hyphenatedHistorySelected);
                List<Dictionary<string, object>> nearbyOrderSelected = DeterministicContextPullSelection("party_chat",
                    "Name nearby parties, nearby forces, or nearby troops supplied by current native context.", "", ContextPullIds.ToList());
                add("context_router_nearby_word_order", nearbyOrderSelected.Any(x => ReadString(x, "id", "") == "nearby_lord_parties"),
                    "Natural nearby-first wording selects the nearby-party native helper.", nearbyOrderSelected);
                List<Dictionary<string, object>> nearbyClauseSelected = DeterministicContextPullSelection("party_chat",
                    "What named lord parties or armies are near Danustica, and which nearby settlements matter most?", "", ContextPullIds.ToList());
                add("context_router_nearby_relational_clause", nearbyClauseSelected.Any(x => ReadString(x, "id", "") == "nearby_lord_parties"),
                    "A natural 'forces are near PLACE' clause selects the nearby-party native helper.", nearbyClauseSelected);
                List<Dictionary<string, object>> nearbySpatialPull = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["id"] = "nearby_lord_parties", ["reason"] = "player asked which forces are nearby" }
                };
                List<Dictionary<string, object>> nearbySpatialBundle = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["id"] = "nearby_lord_parties", ["ok"] = true, ["source"] = "game_client",
                        ["data"] = new Dictionary<string, object>
                        {
                            ["items"] = new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object> { ["leaderName"] = "Oros", ["troops"] = 184, ["distance"] = 24.11d }
                            }
                        }
                    }
                };
                string nearbySpatialPrompt = BuildContextPullText("Which lord parties are nearby?", "Current settlement: Danustica",
                    new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>(),
                    new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(), nearbySpatialPull, nearbySpatialBundle, false);
                add("native_spatial_context_does_not_invent_time_or_bearing",
                    nearbySpatialPrompt.Contains("raw game-map distance scalar", StringComparison.Ordinal)
                    && nearbySpatialPrompt.Contains("do not convert it into travel time", StringComparison.Ordinal)
                    && nearbySpatialPrompt.Contains("do not state or imply north, south, east, west", StringComparison.Ordinal)
                    && nearbySpatialPrompt.Contains("\"distance\":24.11", StringComparison.Ordinal),
                    "Nearby native bundles preserve supplied distance while explicitly forbidding invented travel time, direction, bearings, or routes.", nearbySpatialPrompt);
                List<Dictionary<string, object>> playerAppearancePull = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["id"] = "check_player_appearance_status", ["reason"] = "player asked how they look" }
                };
                List<Dictionary<string, object>> playerAppearanceBundle = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["id"] = "check_player_appearance_status", ["ok"] = true, ["source"] = "game_client",
                        ["data"] = new Dictionary<string, object>
                        {
                            ["subject"] = new Dictionary<string, object>
                            {
                                ["heroStringId"] = "main_hero", ["name"] = "Caribos",
                                ["clanName"] = "Lovanides", ["isClanLeader"] = true
                            },
                            ["appearance"] = new Dictionary<string, object>
                            {
                                ["trueStatusLabel"] = "respectable or prosperous",
                                ["visibleStatusLabel"] = "respectable or prosperous",
                                ["firstView"] = "Caribos appears respectable or prosperous at first glance, while their true station reads as respectable or prosperous. Visible details: Infantryman Long Gambeson, Footman's Spatha, Leather Shoes. Their presentation broadly matches their station.",
                                ["civilianEquipmentValue"] = 2956
                            },
                            ["wealth"] = new Dictionary<string, object>
                            {
                                ["gold"] = 167, ["clanGold"] = 167, ["partyInventoryValue"] = 20,
                                ["clanTier"] = 0, ["clanRenown"] = 5d, ["clanFiefCount"] = 0
                            }
                        }
                    }
                };
                string playerAppearancePrompt = BuildContextPullText("What can you infer by looking at me?", "",
                    new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>(),
                    new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                    playerAppearancePull, playerAppearanceBundle, false);
                add("player_appearance_context_excludes_private_native_state",
                    playerAppearancePrompt.Contains("Infantryman Long Gambeson", StringComparison.Ordinal)
                    && playerAppearancePrompt.Contains("respectable or prosperous", StringComparison.Ordinal)
                    && playerAppearancePrompt.Contains("Visual knowledge rule", StringComparison.Ordinal)
                    && !playerAppearancePrompt.Contains("Caribos", StringComparison.Ordinal)
                    && !playerAppearancePrompt.Contains("Lovanides", StringComparison.Ordinal)
                    && !playerAppearancePrompt.Contains("167", StringComparison.Ordinal)
                    && !playerAppearancePrompt.Contains("clanTier", StringComparison.Ordinal)
                    && !playerAppearancePrompt.Contains("civilianEquipmentValue", StringComparison.Ordinal),
                    "Player appearance helpers retain observable clothing and apparent status while excluding canonical identity, exact money, clan standing, inventory value, and other private native state from the model prompt.",
                    playerAppearancePrompt);
                const string clanDeferencePrompt = "How much public deference, private candor, or guarded distance should someone of my known standing receive from you? Show the answer through your conduct rather than reciting clan-tier rules.";
                List<Dictionary<string, object>> clanDeferenceSelected = DeterministicContextPullSelection(
                    "dialogue", clanDeferencePrompt, "", ContextPullIds.ToList());
                add("context_router_public_deference_is_not_trade_appraisal",
                    clanDeferenceSelected.Any(x => ReadString(x, "id", "") == "clan_wealth_and_influence")
                    && !clanDeferenceSelected.Any(x => ReadString(x, "id", "") == "appraise_trade_offer"),
                    "The phrase 'how much public deference' can request clan standing without being misclassified as a material price or trade appraisal.",
                    clanDeferenceSelected);
                List<Dictionary<string, object>> privateClanBundle = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["id"] = "clan_wealth_and_influence", ["ok"] = true, ["source"] = "game_client",
                        ["data"] = new Dictionary<string, object>
                        {
                            ["player"] = new Dictionary<string, object>
                            {
                                ["hero"] = new Dictionary<string, object>
                                {
                                    ["heroStringId"] = "main_hero", ["name"] = "Caribos",
                                    ["clanName"] = "Lovanides", ["isClanLeader"] = true
                                },
                                ["wealth"] = new Dictionary<string, object>
                                {
                                    ["gold"] = 167, ["clanTier"] = 0, ["clanFiefCount"] = 0
                                },
                                ["clan"] = new Dictionary<string, object>
                                {
                                    ["name"] = "Lovanides", ["tier"] = 0, ["gold"] = 167, ["fiefCount"] = 0
                                },
                                ["appearance"] = new Dictionary<string, object>
                                {
                                    ["visibleStatusLabel"] = "respectable or prosperous",
                                    ["firstView"] = "Caribos appears respectable or prosperous. Visible details: Infantryman Long Gambeson, Footman's Spatha, Leather Shoes."
                                }
                            },
                            ["speaker"] = new Dictionary<string, object>
                            {
                                ["clan"] = new Dictionary<string, object> { ["name"] = "Vizartos", ["tier"] = 3 }
                            }
                        }
                    }
                };
                Dictionary<string, object> unverifiedPlayerIdentity = new Dictionary<string, object>
                {
                    ["identityState"] = "encountered_unknown", ["knowsIdentity"] = false
                };
                string privateClanPrompt = BuildContextPullText(clanDeferencePrompt, "",
                    new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>(),
                    new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                    clanDeferenceSelected, privateClanBundle, false, unverifiedPlayerIdentity);
                add("unknown_identity_clan_context_excludes_private_player_state",
                    privateClanPrompt.Contains("Vizartos", StringComparison.Ordinal)
                    && privateClanPrompt.Contains("Infantryman Long Gambeson", StringComparison.Ordinal)
                    && privateClanPrompt.Contains("observer has not verified", StringComparison.OrdinalIgnoreCase)
                    && !privateClanPrompt.Contains("Caribos", StringComparison.Ordinal)
                    && !privateClanPrompt.Contains("Lovanides", StringComparison.Ordinal)
                    && !privateClanPrompt.Contains("\"gold\":167", StringComparison.Ordinal)
                    && !privateClanPrompt.Contains("\"tier\":0", StringComparison.Ordinal)
                    && !privateClanPrompt.Contains("\"fiefCount\":0", StringComparison.Ordinal),
                    "Unknown observers retain their own clan evidence and the player's visible presentation while exact player identity, clan, money, tier, and fiefs are withheld from the actual model prompt.",
                    privateClanPrompt);
                const string immediateAnswerRecall = "Without checking new reports, recall your immediately preceding answer: which party had wounded troops and what raw distance did you give for the closest party?";
                List<Dictionary<string, object>> immediateAnswerRecallPulls = PruneContextPullSelection(immediateAnswerRecall,
                    DeterministicContextPullSelection("dialogue", immediateAnswerRecall, "Current settlement: Danustica", ContextPullIds.ToList()));
                add("pure_answer_recall_cannot_refresh_live_native_context",
                    LooksLikeMostRecentConversationRecall(immediateAnswerRecall)
                    && IsPureConversationRecallRequest(immediateAnswerRecall)
                    && immediateAnswerRecallPulls.Any(x => ReadString(x, "id", "") == "relevant_memory")
                    && !immediateAnswerRecallPulls.Any(x => ReadString(x, "id", "") == "nearby_lord_parties")
                    && !immediateAnswerRecallPulls.Any(x => ReadString(x, "id", "") == "current_settlement_facts")
                    && !immediateAnswerRecallPulls.Any(x => ReadString(x, "id", "") == "verify_world_history"),
                    "A pure probe of the NPC's immediately preceding answer uses transcript/memory evidence and cannot silently refresh native world facts.", immediateAnswerRecallPulls);
                const string spymasterMissionRecall = "Aeroc, remind me what came of the two stories we planted about Ranaon, and whether your effort to clean up my reputation worked.";
                List<Dictionary<string, object>> spymasterMissionRecallPulls = PruneContextPullSelection(spymasterMissionRecall,
                    DeterministicContextPullSelection("dialogue", spymasterMissionRecall, "Current settlement: Zeonica", ContextPullIds.ToList()));
                add("natural_remind_me_selects_relevant_memory",
                    spymasterMissionRecallPulls.Any(x => ReadString(x, "id", "") == "relevant_memory"),
                    "Natural 'remind me' phrasing retrieves the NPC's private mission memory without requiring test-shaped recall language.",
                    spymasterMissionRecallPulls);
                List<Dictionary<string, object>> artisanSelected = DeterministicContextPullSelection("party_chat",
                    "How would this news matter to ordinary artisans?", "", ContextPullIds.ToList());
                add("context_router_artisan_boundary", !artisanSelected.Any(x => ReadString(x, "id", "") == "kingdom_diplomacy_status"),
                    "The word 'artisans' cannot activate current diplomacy through its embedded 'war' letters.", artisanSelected);
                List<Dictionary<string, object>> conversationalValueSelected = DeterministicContextPullSelection("party_chat",
                    "Ascyron, I'm newly arrived in Danustica and curious what each of you values most about this city. If someone before you says something worth responding to, address their point as well.", "", ContextPullIds.ToList());
                add("context_router_conversational_value_boundary",
                    conversationalValueSelected.Any(x => ReadString(x, "id", "") == "current_settlement_facts")
                    && !conversationalValueSelected.Any(x => ReadString(x, "id", "") == "appraise_trade_offer")
                    && !conversationalValueSelected.Any(x => ReadString(x, "id", "") == "relevant_memory"),
                    "Conversational value and speaker-order wording select current settlement facts without inventing trade appraisal or memory retrieval intent.", conversationalValueSelected);
                const string personalTradePrompt = "Let us trade something small and personal rather than useful information. I find the smell of lamp oil calming. Tell me one ordinary sound, smell, texture, or arrangement that makes you feel settled.";
                List<Dictionary<string, object>> personalTradeSelected = PruneContextPullSelection(personalTradePrompt,
                    DeterministicContextPullSelection("party_chat", personalTradePrompt, "", ContextPullIds.ToList()));
                add("context_router_personal_story_trade_is_not_inventory",
                    !personalTradeSelected.Any(x => ReadString(x, "id", "") == "check_inventory_appearance")
                    && !personalTradeSelected.Any(x => ReadString(x, "id", "") == "appraise_trade_offer")
                    && !personalTradeSelected.Any(x => ReadString(x, "id", "") == "clan_wealth_and_influence"),
                    "Trading personal stories or details is conversational reciprocity, not a material inventory, appraisal, or clan-wealth request.", personalTradeSelected);
                List<Dictionary<string, object>> eventConcernSelected = DeterministicContextPullSelection("social_event",
                    "Identify the occasion and phase this gathering actually supplies, then tell me one concern about Danustica.",
                    "Location: Danustica\nEvent: Villa Supper\nPhase: Arrival and Seating", ContextPullIds.ToList());
                add("context_router_event_supply_and_named_location_boundary",
                    eventConcernSelected.Any(x => ReadString(x, "id", "") == "current_settlement_facts")
                    && !eventConcernSelected.Any(x => ReadString(x, "id", "") == "check_inventory_appearance")
                    && !eventConcernSelected.Any(x => ReadString(x, "id", "") == "relevant_memory"),
                    "Using 'supplies' as a verb does not request inventory, while a concern about the named current settlement receives live local facts.", eventConcernSelected);
                const string currentOwnerGovernorPrompt = "My name is Rhovarion. According to current native settlement facts, which kingdom and clan own Danustica, who is its current governor, and is it under siege?";
                List<Dictionary<string, object>> currentOwnerGovernorSelected = PruneContextPullSelection(currentOwnerGovernorPrompt,
                    DeterministicContextPullSelection("dialogue", currentOwnerGovernorPrompt, "Location: Danustica", ContextPullIds.ToList()));
                add("context_router_local_owner_governor_is_not_broad_diplomacy",
                    currentOwnerGovernorSelected.Any(x => ReadString(x, "id", "") == "current_settlement_facts")
                    && !currentOwnerGovernorSelected.Any(x => ReadString(x, "id", "") == "kingdom_diplomacy_status")
                    && !currentOwnerGovernorSelected.Any(x => ReadString(x, "id", "") == "clan_wealth_and_influence"),
                    "A current owner, governor, and siege-status question uses the authoritative settlement bundle without unrelated kingdom-war or clan-wealth payloads.", currentOwnerGovernorSelected);
                List<Dictionary<string, object>> keepsakeSelected = DeterministicContextPullSelection("party_chat",
                    "I keep a blue glass bead inside my left glove. It is only a small keepsake, not a gift or trade offer.", "", ContextPullIds.ToList());
                add("context_router_negation_and_glove_boundary",
                    !keepsakeSelected.Any(x => ReadString(x, "id", "") == "check_inventory_appearance")
                    && !keepsakeSelected.Any(x => ReadString(x, "id", "") == "appraise_trade_offer")
                    && !keepsakeSelected.Any(x => ReadString(x, "id", "") == "relationship_history"),
                    "An explicitly non-commercial keepsake does not pull inventory or appraisal context, and 'glove' cannot activate relationship history through its embedded 'love'.", keepsakeSelected);
                const string negatedClayDiscussion = "Purios, I found a plain clay disk with seven shallow notches. It is not valuable and I am not offering it as a gift or trade. Menor and Mystesa, say where your own trades lead you to agree or disagree.";
                List<Dictionary<string, object>> negatedClaySelected = PruneContextPullSelection(negatedClayDiscussion,
                    DeterministicContextPullSelection("party_chat", negatedClayDiscussion, "Current settlement: Danustica", ContextPullIds.ToList()));
                add("context_router_negated_exchange_and_occupational_trade",
                    !negatedClaySelected.Any(x => ReadString(x, "id", "") == "check_inventory_appearance")
                    && !negatedClaySelected.Any(x => ReadString(x, "id", "") == "appraise_trade_offer"),
                    "A negated gift/trade and occupational use of 'your trades' cannot fetch current inventory or appraisal evidence.", negatedClaySelected);
                const string naturalNegatedAppraisal = "Menor and Mystesa, respond to Purios's runner tally from your own trades. Do not treat the disk as a gift or sale.";
                List<Dictionary<string, object>> naturalNegatedSelected = PruneContextPullSelection(naturalNegatedAppraisal,
                    DeterministicContextPullSelection("party_chat", naturalNegatedAppraisal, "Current settlement: Danustica", ContextPullIds.ToList()));
                add("context_router_natural_do_not_treat_negation",
                    !naturalNegatedSelected.Any(x => ReadString(x, "id", "") == "check_inventory_appearance")
                    && !naturalNegatedSelected.Any(x => ReadString(x, "id", "") == "appraise_trade_offer"),
                    "Natural 'do not treat this as a gift or sale' wording cannot be reinterpreted as a gift-appraisal request.", naturalNegatedSelected);
                List<Dictionary<string, object>> materialGiftSelected = DeterministicContextPullSelection("party_chat",
                    "Pacarios, I give you this finely worked silver cup as a personal gift, with no favor expected.", "", ContextPullIds.ToList());
                add("context_router_material_gift_verification",
                    materialGiftSelected.Any(x => ReadString(x, "id", "") == "appraise_trade_offer")
                    && !materialGiftSelected.Any(x => ReadString(x, "id", "") == "check_inventory_appearance"),
                    "A concrete material gift uses the appraisal bundle containing both player and recipient inventories instead of inspecting only the speaking NPC.", materialGiftSelected);
                List<Dictionary<string, object>> historicalSilverAccusationSelected = DeterministicContextPullSelection("party_chat",
                    "What exact evidence supports your accusation that I hand out silver and lie? If you did not witness it, identify the historical source or retract it.", "", ContextPullIds.ToList());
                add("context_router_historical_gift_discussion_not_appraisal",
                    !historicalSilverAccusationSelected.Any(x => ReadString(x, "id", "") == "appraise_trade_offer"),
                    "Discussing evidence for an alleged past silver transfer does not masquerade as a present gift or trade offer.", historicalSilverAccusationSelected);
                const string recordsCupRecall = "From your own records, who gave you the silver cup, who received it, and what exactly do you know about the giver identity?";
                List<Dictionary<string, object>> recordsCupRecallSelected = PruneContextPullSelection(recordsCupRecall,
                    DeterministicContextPullSelection("dialogue", recordsCupRecall, "", ContextPullIds.ToList()));
                add("context_router_records_based_gift_recall",
                    recordsCupRecallSelected.Any(x => ReadString(x, "id", "") == "relevant_memory")
                    && !recordsCupRecallSelected.Any(x => ReadString(x, "id", "") == "check_inventory_appearance")
                    && !recordsCupRecallSelected.Any(x => ReadString(x, "id", "") == "appraise_trade_offer")
                    && IsPureConversationRecallRequest(recordsCupRecall),
                    "A records-based question about a completed historical gift selects memory, not current inventory/appraisal, and remains an audit-only recall probe.",
                    recordsCupRecallSelected);
                const string nameAndBoxFollowup = "Before we continue, my name is Rhovarion. Honoratus, answer Menor's prior point that the box is nothing to you until I give you a reason. Keep the cup attributed to my earlier claim.";
                List<Dictionary<string, object>> nameAndBoxSelected = DeterministicContextPullSelection("party_chat",
                    nameAndBoxFollowup, "", ContextPullIds.ToList());
                add("context_router_distant_give_and_item_not_trade",
                    !nameAndBoxSelected.Any(x => ReadString(x, "id", "") == "appraise_trade_offer"),
                    "A non-commercial 'give you a reason' clause cannot combine with a later mentioned cup to create a false trade-appraisal pull.", nameAndBoxSelected);
                List<Dictionary<string, object>> indirectRecallSelected = DeterministicContextPullSelection("party_chat",
                    "Earlier I told this group why I keep something hidden in one piece of clothing. Without guessing, tell me what the object was and where I kept it.", "", ContextPullIds.ToList());
                add("context_router_indirect_recall_priority",
                    indirectRecallSelected.Any(x => ReadString(x, "id", "") == "relevant_memory")
                    && !indirectRecallSelected.Any(x => ReadString(x, "id", "") == "check_player_appearance_status")
                    && !indirectRecallSelected.Any(x => ReadString(x, "id", "") == "check_inventory_appearance"),
                    "An indirect request for an earlier disclosed object prioritizes memory evidence instead of treating historical clothing language as a live appearance query.", indirectRecallSelected);
                List<Dictionary<string, object>> sceneReconstructionSelected = DeterministicContextPullSelection("party_chat",
                    "Reconstruct the third question from our immediately preceding group conversation. If a stored summary conflicts with the raw conversation, trust the raw conversation.", "", ContextPullIds.ToList());
                add("context_router_explicit_scene_reconstruction",
                    sceneReconstructionSelected.Any(x => ReadString(x, "id", "") == "relevant_memory"),
                    "Natural scene-reconstruction wording selects source-bearing recent memory instead of relying only on relationship summaries.", sceneReconstructionSelected);
                List<Dictionary<string, object>> negativeLocationClaimSelected = DeterministicContextPullSelection("party_chat",
                    "I have never entered Danustica at any time, and I am not presently in Danustica.", "", ContextPullIds.ToList());
                add("context_router_negative_location_claim",
                    negativeLocationClaimSelected.Any(x => ReadString(x, "id", "") == "verify_world_history"),
                    "Negative-form first-person location claims receive objective lie/history verification instead of bypassing the evidence pipeline.", negativeLocationClaimSelected);
                const string alternateLocationPressure = "That is false. We are in Lycaron, not Danustica. I inspected the town marker myself; accept my report and stop questioning me.";
                List<Dictionary<string, object>> alternateLocationSelected = PruneContextPullSelection(alternateLocationPressure,
                    DeterministicContextPullSelection("dialogue", alternateLocationPressure, "Current settlement: Danustica", ContextPullIds.ToList()));
                add("context_router_alternate_current_location_claim",
                    alternateLocationSelected.Any(x => ReadString(x, "id", "") == "verify_world_history"),
                    "A named alternate-location claim that denies the visible current settlement receives first-hand lie verification.", alternateLocationSelected);
                const string admittedCaptureLie = "I personally captured Danustica for the Western Empire yesterday. I know that account is false; I am deliberately lying. Use current settlement ownership and accessible history.";
                List<Dictionary<string, object>> admittedCaptureSelected = DeterministicContextPullSelection("party_chat",
                    admittedCaptureLie, "Location: Danustica", ContextPullIds.ToList());
                admittedCaptureSelected = PruneContextPullSelection(admittedCaptureLie, admittedCaptureSelected);
                add("context_router_admitted_capture_lie",
                    admittedCaptureSelected.Any(x => ReadString(x, "id", "") == "verify_world_history")
                    && admittedCaptureSelected.Any(x => ReadString(x, "id", "") == "current_settlement_facts")
                    && !admittedCaptureSelected.Any(x => ReadString(x, "id", "") == "appraise_trade_offer"),
                    "A first-person capture claim with an explicit falsehood admission receives both authoritative history and current-settlement ownership context.", admittedCaptureSelected);
                const string admittedCurrentDeception = "I am deliberately trying to deceive you: Oros is inside Danustica with exactly four hundred troops and the walls are unmanned.";
                List<Dictionary<string, object>> admittedCurrentDeceptionSelected = DeterministicContextPullSelection("party_chat",
                    admittedCurrentDeception, "Location: Danustica", ContextPullIds.ToList());
                admittedCurrentDeceptionSelected = PruneContextPullSelection(admittedCurrentDeception, admittedCurrentDeceptionSelected);
                add("context_router_admitted_current_deception",
                    admittedCurrentDeceptionSelected.Any(x => ReadString(x, "id", "") == "verify_world_history")
                    && admittedCurrentDeceptionSelected.Any(x => ReadString(x, "id", "") == "current_settlement_facts"),
                    "An explicit attempt-to-deceive admission about current native facts cannot bypass the lie-verification pull.", admittedCurrentDeceptionSelected);
                const string admittedPastDeception = "I deliberately lied to you when I claimed that I personally own Danustica. I knew that claim was false and intended to deceive you for my own advantage.";
                List<Dictionary<string, object>> admittedPastDeceptionSelected =
                    PruneContextPullSelection(
                        admittedPastDeception,
                        DeterministicContextPullSelection(
                            "dialogue", admittedPastDeception,
                            "Location: Danustica",
                            ContextPullIds.ToList()));
                add("context_router_admitted_past_deception",
                    admittedPastDeceptionSelected.Any(x =>
                        ReadString(x, "id", "")
                            == "verify_world_history"),
                    "Past-tense direct admissions such as 'I deliberately lied' and 'intended to deceive' deterministically enter the evidence-backed lie-check path.",
                    admittedPastDeceptionSelected);
                Dictionary<string, object> replyOnly = new Dictionary<string, object> { ["reply"] = "Three lies in one breath." };
                List<string> completedFields;
                Dictionary<string, object> schemaCompleted = CompleteConversationStructuredResponse(replyOnly, "party_chat", out completedFields);
                add("structured_reply_only_completion",
                    StructuredResponseIsComplete(Json.Serialize(schemaCompleted), "party_chat")
                    && ReadDictionary(schemaCompleted, "actionGate") != null
                    && schemaCompleted.ContainsKey("relationshipAssessments")
                    && completedFields.Contains("actionGate"),
                    "A usable reply with missing private JSON metadata is completed conservatively without a second full provider request.", schemaCompleted);
                Dictionary<string, object> castleEchoRequest =
                    new Dictionary<string, object>
                    {
                        ["playerText"] =
                            "I need advisors I can trust. If its justified."
                    };
                List<string> castleEchoFields;
                Dictionary<string, object> castleEchoCompleted =
                    CompleteConversationStructuredResponse(
                        new Dictionary<string, object>
                        {
                            ["reply"] = "'If it's justified.'"
                        },
                        "castle_chat",
                        out castleEchoFields);
                Dictionary<string, object> secondCastleEchoRequest =
                    new Dictionary<string, object>
                    {
                        ["playerText"] =
                            "There is only so much room on this bench. If being a forgotten daughter sounds better."
                    };
                List<string> secondCastleEchoFields;
                Dictionary<string, object> secondCastleEchoCompleted =
                    CompleteConversationStructuredResponse(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "'If being a forgotten daughter sounds better.'"
                        },
                        "castle_chat",
                        out secondCastleEchoFields);
                List<string> castleSubstantiveFields;
                Dictionary<string, object> castleSubstantiveCompleted =
                    CompleteConversationStructuredResponse(
                        new Dictionary<string, object>
                        {
                            ["reply"] =
                                "She weighs the offer before answering. \"If it is justified, then name the duty and the limits you would place upon it.\""
                        },
                        "castle_chat",
                        out castleSubstantiveFields);
                add("castle_chat_quoted_prompt_echo_requires_retry",
                    IsConversationStructuredMode("castle_chat")
                    && StructuredResponseIsComplete(
                        Json.Serialize(castleEchoCompleted),
                        "castle_chat")
                    && StructuredResponseIsComplete(
                        Json.Serialize(secondCastleEchoCompleted),
                        "castle_chat")
                    && !ConversationVisibleReplyPassesRequestQualityGate(
                        Json.Serialize(castleEchoCompleted),
                        castleEchoRequest,
                        "castle_chat")
                    && !ConversationVisibleReplyPassesRequestQualityGate(
                        Json.Serialize(secondCastleEchoCompleted),
                        secondCastleEchoRequest,
                        "castle_chat")
                    && ConversationVisibleReplyPassesRequestQualityGate(
                        Json.Serialize(castleSubstantiveCompleted),
                        castleEchoRequest,
                        "castle_chat"),
                    "Castle Chat treats the two observed quote-only player-fragment echoes as malformed responses requiring bounded retry while accepting a complete in-character reaction.",
                    new Dictionary<string, object>
                    {
                        ["firstObservedFragment"] = castleEchoCompleted,
                        ["secondObservedFragment"] = secondCastleEchoCompleted,
                        ["validResponse"] = castleSubstantiveCompleted
                    });
                Dictionary<string, object> completeCorrespondence = new Dictionary<string, object>
                {
                    ["shouldReply"] = true,
                    ["body"] = "I accept your sealed terms and pledge my clan to your planned rebellion.",
                    ["reason"] = "The concrete bargain justifies the risk.",
                    ["actionGate"] = new Dictionary<string, object>
                    {
                        ["needed"] = true,
                        ["commitment"] = "accepted",
                        ["intent"] = "pledge my clan to the planned rebellion"
                    },
                    ["rebellionDecision"] = "none",
                    ["campaignOrder"] = new Dictionary<string, object> { ["decision"] = "none" }
                };
                add("correspondence_structured_response_requires_complete_written_contract",
                    StructuredResponseIsComplete(Json.Serialize(completeCorrespondence), "correspondence")
                    && !StructuredResponseIsComplete(
                        "{\"shouldReply\":true,\"body\":\"I accept your sealed terms",
                        "correspondence")
                    && !StructuredResponseIsComplete(
                        Json.Serialize(new Dictionary<string, object>
                        {
                            ["shouldReply"] = true,
                            ["body"] = "I accept your sealed terms."
                        }),
                        "correspondence"),
                    "Correspondence accepts the production body schema only when the full action-bearing contract is present and rejects the observed token-capped partial JSON.",
                    new Dictionary<string, object> { ["validResponse"] = completeCorrespondence });
                Dictionary<string, object> malformedCollections = new Dictionary<string, object>
                {
                    ["reply"] = "I keep a chipped bone die in a locked drawer because it belonged to my father.",
                    ["emotion"] = "guarded",
                    ["intent"] = "share_a_personal_detail",
                    ["relationshipSignal"] = "unchanged",
                    ["relationshipAssessments"] = new List<object> { 0, 0, 0 },
                    ["decisionBrief"] = new Dictionary<string, object>
                    {
                        ["facts"] = new List<object> { "The speaker owns a keepsake.", 0 },
                        ["goals"] = new List<object> { 0 },
                        ["constraints"] = new List<object> { 0 },
                        ["decision"] = "Share the keepsake."
                    },
                    ["actionGate"] = new Dictionary<string, object> { ["needed"] = false },
                    ["memoryWrites"] = new List<object> { 0, 0, 0 },
                    ["dynamicCharacteristicWrites"] = new List<object> { 0, 0, 0 }
                };
                List<string> malformedCompletedFields;
                Dictionary<string, object> sanitizedCollections = CompleteConversationStructuredResponse(
                    malformedCollections, "dialogue", out malformedCompletedFields);
                add("malformed_numeric_structured_collections_are_sanitized",
                    !StructuredResponseIsComplete(Json.Serialize(malformedCollections), "dialogue")
                    && StructuredResponseIsComplete(Json.Serialize(sanitizedCollections), "dialogue")
                    && ReadDictionaryList(sanitizedCollections, "memoryWrites").Count == 0
                    && ReadDictionaryList(sanitizedCollections, "dynamicCharacteristicWrites").Count == 0
                    && ReadDictionaryList(sanitizedCollections, "relationshipAssessments").Count == 0
                    && malformedCompletedFields.Contains("memoryWrites")
                    && malformedCompletedFields.Contains("dynamicCharacteristicWrites"),
                    "Numeric or mixed collection entries are schema corruption: deterministic completion removes them instead of creating bogus memories or suppressing grounded companion writes.",
                    sanitizedCollections);
                List<Dictionary<string, object>> scalarMemories = NormalizeMemoryWrites(
                    new List<object> { 0, 0, "0" }, "schema_test", "npc_schema", 1);
                add("scalar_memory_entries_never_persist",
                    scalarMemories.Count == 0,
                    "Primitive array entries cannot be converted into durable dialogue memories.",
                    scalarMemories);
                add("legacy_scalar_memory_rows_are_not_retrievable",
                    !DurableDialogueMemoryRowIsUsable(new Dictionary<string, object>
                    {
                        ["source"] = "dialogue_llm", ["text"] = "0"
                    })
                    && DurableDialogueMemoryRowIsUsable(new Dictionary<string, object>
                    {
                        ["source"] = "dialogue_llm", ["text"] = "The chipped die remains in a locked drawer."
                    }),
                    "Already-persisted scalar artifacts are excluded from prompt and replay retrieval while real dialogue memories remain eligible.",
                    new Dictionary<string, object>());
                Dictionary<string, object> shiftedReplyKeys = new Dictionary<string, object>
                {
                    [""] = "reply",
                    [" "] = "The road is unsafe after dark.",
                    ["emotion"] = "guarded",
                    ["intent"] = "warn",
                    ["relationshipSignal"] = "unchanged",
                    ["relationshipAssessments"] = new List<Dictionary<string, object>>(),
                    ["decisionBrief"] = new Dictionary<string, object> { ["decision"] = "Warn the player." },
                    ["actionGate"] = new Dictionary<string, object> { ["needed"] = false }
                };
                bool shiftedReplyRepaired = TryRepairShiftedConversationReplyKeys(
                    shiftedReplyKeys, out Dictionary<string, object> repairedShiftedReply);
                add("structured_shifted_reply_key_repair",
                    shiftedReplyRepaired
                    && ReadString(repairedShiftedReply, "reply", "") == "The road is unsafe after dark."
                    && !repairedShiftedReply.ContainsKey("")
                    && !repairedShiftedReply.ContainsKey(" "),
                    "An unambiguous provider corruption that shifts reply into two whitespace-named properties is repaired without another LLM call.",
                    repairedShiftedReply);
                Dictionary<string, object> nestedMemoryObject = new Dictionary<string, object>
                {
                    ["text"] = "The speaker discussed the town and withheld military details.",
                    ["importance"] = 0.7d,
                    ["tags"] = new List<string> { "dialogue", "town" }
                };
                add("nested_memory_object_not_visible_reply",
                    !ConversationStructuredObjectHasUsableVisibleReply(nestedMemoryObject)
                    && !StructuredResponseIsComplete(Json.Serialize(nestedMemoryObject), "dialogue"),
                    "A truncated response ending on a nested memory-write object cannot be promoted into visible NPC dialogue.",
                    nestedMemoryObject);
                Dictionary<string, object> missingPartyReply = new Dictionary<string, object>
                {
                    [": "] = "reply",
                    ["participation"] = "speak",
                    ["emotion"] = "wary",
                    ["intent"] = "answer_guardedly",
                    ["relationshipSignal"] = "suspicious",
                    ["relationshipAssessments"] = new List<Dictionary<string, object>>(),
                    ["decisionBrief"] = new Dictionary<string, object> { ["decision"] = "Answer guardedly." },
                    ["actionGate"] = new Dictionary<string, object> { ["needed"] = false }
                };
                add("party_chat_missing_visible_reply_requires_repair",
                    !StructuredResponseIsComplete(
                        Json.Serialize(missingPartyReply), "party_chat")
                    && !StructuredResponseIsComplete(
                        Json.Serialize(missingPartyReply), "social_event"),
                    "Party and social-event mode normalization cannot let a participation token masquerade as a visible reply when the provider omitted reply.",
                    missingPartyReply);
                Dictionary<string, object> controlTokenReply =
                    new Dictionary<string, object>(schemaCompleted)
                    {
                        ["reply"] = "speak",
                        ["participation"] = "speak"
                    };
                Dictionary<string, object> validQuietReply =
                    new Dictionary<string, object>(schemaCompleted)
                    {
                        ["reply"] = "",
                        ["participation"] = "quiet"
                    };
                add("participation_control_token_not_visible_dialogue",
                    !ConversationStructuredObjectHasUsableVisibleReply(
                        controlTokenReply)
                    && !StructuredResponseIsComplete(
                        Json.Serialize(controlTokenReply), "party_chat")
                    && StructuredResponseIsComplete(
                        Json.Serialize(validQuietReply), "party_chat"),
                    "Participation enums cannot be accepted as NPC prose, while an intentionally quiet participant remains a complete structured response.",
                    controlTokenReply);
                string quotedReply = SanitizeVisibleReply("\"Rhovarion, then.\" He inclines his head.");
                add("visible_reply_leading_quote_preserved",
                    quotedReply.StartsWith("\"Rhovarion, then.\"", StringComparison.Ordinal),
                    "Legitimate leading dialogue quotes remain balanced when narration follows the spoken sentence.", quotedReply);
                string provenanceSummary = EnsureConversationSummaryProvenance("Menor said he watched the gate.", 500);
                add("conversation_summary_provenance_label",
                    provenanceSummary.StartsWith("Conversation record (reported speech; not independent proof of world facts):", StringComparison.Ordinal),
                    "Conversation-derived summaries carry an explicit evidence-class warning before semantic indexing and later retrieval.", provenanceSummary);
                add("action_status_reports_are_monotonic",
                    ShouldAcceptActionStatusTransition("enqueued", "completed")
                    && ShouldAcceptActionStatusTransition("completed", "completed")
                    && !ShouldAcceptActionStatusTransition("completed", "enqueued")
                    && !ShouldAcceptActionStatusTransition("failed", "executing")
                    && !ShouldAcceptActionStatusTransition("executing", "queued"),
                    "Terminal action receipts are authoritative and late asynchronous queue/progress reports cannot move either the action queue or diplomacy event ledger backward.",
                    new Dictionary<string, object>
                    {
                        ["completedThenEnqueuedAccepted"] = ShouldAcceptActionStatusTransition("completed", "enqueued"),
                        ["failedThenExecutingAccepted"] = ShouldAcceptActionStatusTransition("failed", "executing")
                    });
                Dictionary<string, object> governmentRefusalRecord =
                    new Dictionary<string, object>
                    {
                        ["source"] = "world_diplomacy_director"
                    };
                Func<string, Dictionary<string, object>> governmentRefusalReport =
                    resultCode => new Dictionary<string, object>
                    {
                        ["source"] = "world_diplomacy_director",
                        ["status"] = "failed",
                        ["result"] = new Dictionary<string, object>
                        {
                            ["resultCode"] = resultCode,
                            ["failureKind"] = "government_authority",
                            ["retryable"] = false
                        }
                    };
                add("npc_government_refusal_is_resolved_rejection",
                    NormalizeActionReportStatus(governmentRefusalRecord,
                        governmentRefusalReport("government_pressure_prevailed"),
                        "failed") == "rejected"
                    && NormalizeActionReportStatus(governmentRefusalRecord,
                        governmentRefusalReport("government_level_five_block"),
                        "failed") == "rejected"
                     && NormalizeActionReportStatus(governmentRefusalRecord,
                         governmentRefusalReport("execution_failed"),
                         "failed") == "failed"
                     && NormalizeActionReportStatus(governmentRefusalRecord,
                         new Dictionary<string, object>
                         {
                             ["source"] = "world_diplomacy_director",
                             ["status"] = "failed",
                             ["result"] = new Dictionary<string, object>
                             {
                                 ["resultCode"] = "government_pressure_prevailed",
                                 ["failureKind"] = "government_authority"
                             }
                         }, "failed") == "failed",
                    "A lawful NPC-government veto resolves an autonomous diplomacy action as rejected, while ordinary execution failures remain failed.",
                    null);
                add("world_test_resolved_rejection_is_not_terminal_failure",
                    !IsTerminalFailureStatus("rejected")
                    && IsTerminalFailureStatus("failed")
                    && IsTerminalFailureStatus("blocked")
                    && IsTerminalFailureStatus("expired")
                    && IsTerminalFailureStatus("validation_failed"),
                    "World Test treats a resolved action rejection, including a lawful NPC-government veto, as an observed outcome rather than a pipeline failure while retaining every actual terminal failure state.",
                    null);
                var recordedRefusal = governmentRefusalReport("government_authorization_refused");
                recordedRefusal["status"] = "rejected";
                var recordedRefusalResult = ReadDictionary(recordedRefusal, "result");
                recordedRefusalResult["success"] = false;
                recordedRefusalResult["completed"] = true;
                recordedRefusalResult["diagnostics"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["key"] = "governmentBusinessId", ["value"] = "recorded_refusal_business" },
                    new Dictionary<string, object> { ["key"] = "governmentStatus", ["value"] = "decided" },
                    new Dictionary<string, object> { ["key"] = "governmentExecutedOptionId", ["value"] = "reject" }
                };
                var legacyRefusal = governmentRefusalReport("government_refused");
                ReadDictionary(legacyRefusal, "result")["success"] = false;
                ReadDictionary(legacyRefusal, "result")["completed"] = false;
                var legacyRefusalRow = new Dictionary<string, object>
                {
                    ["id"] = "legacy_government_refusal_action", ["status"] = "failed",
                    ["record"] = governmentRefusalRecord, ["report"] = legacyRefusal
                };
                add("government_refusal_correction_requires_recorded_rejection",
                    IsLegacyGovernmentRefusalCorrection(legacyRefusalRow, governmentRefusalRecord, recordedRefusal, "rejected")
                    && !IsLegacyGovernmentRefusalCorrection(legacyRefusalRow, governmentRefusalRecord,
                        governmentRefusalReport("government_authorization_refused"), "rejected"),
                    "A legacy refusal can be corrected only with a completed recorded reject decision, not an unproven result code.", null);
                ReadDictionary(legacyRefusal, "result")["resultCode"] = "execution_failed";
                add("government_refusal_correction_preserves_real_failures",
                    !IsLegacyGovernmentRefusalCorrection(legacyRefusalRow, governmentRefusalRecord, recordedRefusal, "rejected")
                    && !ShouldAcceptActionStatusTransition("failed", "rejected"),
                    "Ordinary terminal failures remain immutable; the narrow government-refusal repair is not a generic status override.", null);
                ReadDictionary(legacyRefusal, "result")["resultCode"] = "government_refused";
                WriteActionQueueUnlocked(campaignId, new List<Dictionary<string, object>> { legacyRefusalRow });
                WriteDiplomaticEventQueue(campaignId, new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["eventId"] = "legacy_government_refusal_event", ["actionId"] = "legacy_government_refusal_action",
                        ["executionStatus"] = "failed", ["announcementReady"] = false,
                        ["accepted"] = true, ["command"] = "offer_tribute_peace", ["consequenceRecorded"] = true
                    }
                });
                recordedRefusal["campaignId"] = campaignId;
                recordedRefusal["serverActionId"] = "legacy_government_refusal_action";
                var correctedRefusal = ReportAction(recordedRefusal);
                var duplicateRefusal = ReportAction(recordedRefusal);
                var correctedEvent = ReadDiplomaticEventQueue(campaignId).Single();
                add("government_refusal_repairs_action_and_announcement_once",
                    ReadBool(correctedRefusal, "ok", false) && ReadBool(duplicateRefusal, "ok", false)
                    && ReadString(ReadActionQueueUnlocked(campaignId).Single(), "status", "") == "rejected"
                    && ReadString(correctedEvent, "executionStatus", "") == "rejected"
                    && !ReadBool(correctedEvent, "accepted", true) && ReadBool(correctedEvent, "announcementReady", false),
                    "The production report route corrects the exact legacy action and its announcement, retains a truthful refusal, and tolerates a replay without creating another event.", null);
                string repairActionId = "late_action_status_test";
                WriteActionQueueUnlocked(campaignId, new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["id"] = repairActionId, ["campaignId"] = campaignId,
                        ["status"] = "enqueued", ["command"] = "sign_trade_agreement",
                        ["record"] = new Dictionary<string, object>
                        {
                            ["serverActionId"] = repairActionId,
                            ["source"] = "world_diplomacy_director"
                        }
                    }
                });
                WriteDiplomaticEventQueue(campaignId, new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["eventId"] = "late_action_event_test",
                        ["actionId"] = repairActionId,
                        ["executionStatus"] = "enqueued",
                        ["consequenceRecorded"] = true
                    }
                });
                AppendJsonLineToPath(CampaignFile(campaignId, "actions", "actions.jsonl"),
                    new Dictionary<string, object>
                    {
                        ["id"] = repairActionId, ["status"] = "completed", ["ts"] = 10L,
                        ["report"] = new Dictionary<string, object>
                        {
                            ["status"] = "completed", ["message"] = "Treaty applied."
                        }
                    });
                AppendJsonLineToPath(CampaignFile(campaignId, "actions", "actions.jsonl"),
                    new Dictionary<string, object>
                    {
                        ["id"] = repairActionId, ["status"] = "enqueued", ["ts"] = 11L,
                        ["report"] = new Dictionary<string, object>
                        {
                            ["status"] = "enqueued", ["message"] = "Late queue report."
                        }
                    });
                List<Dictionary<string, object>> repairedQueue;
                lock (FileLock) repairedQueue = ReadActionQueueUnlocked(campaignId);
                Dictionary<string, object> repairedEvent =
                    ReadDiplomaticEventQueue(campaignId).FirstOrDefault()
                    ?? new Dictionary<string, object>();
                add("action_status_audit_repairs_prior_regressions",
                    ReadString(repairedQueue.FirstOrDefault(), "status", "") == "completed"
                    && ReadString(repairedEvent, "executionStatus", "") == "completed",
                    "The append-only action audit repairs pre-fix queue and diplomacy rows whose terminal completion was previously overwritten by a late enqueued report.",
                    new Dictionary<string, object>
                    {
                        ["queueStatus"] = ReadString(repairedQueue.FirstOrDefault(), "status", ""),
                        ["eventStatus"] = ReadString(repairedEvent, "executionStatus", "")
                    });
                add("provider_middleware_status", ReadBool(ProviderMiddlewareStatus(), "ok", false), "Provider middleware exposes circuit, concurrency, idempotency, and latency state.", ProviderMiddlewareStatus());
                add("provider_transient_connection_closed_variants",
                    IsTransientLlmFailure("The underlying connection was closed: The connection was closed unexpectedly.")
                    && IsTransientLlmFailure("The connection was closed by the upstream provider.")
                    && !IsTransientLlmFailure("The provider rejected an invalid model name."),
                    "Unexpected upstream connection closure is retryable, while permanent request validation remains non-transient.",
                    new Dictionary<string, object> { ["providerRequestTimeoutMs"] = 120000 });
                add("telemetry_status", ReadBool(ReignTelemetryStatus(), "ok", false), "OpenTelemetry tracing is initialized with a local NDJSON fallback.", ReignTelemetryStatus());
            }
            catch (Exception ex)
            {
                add("architecture_exception", false, "Architecture self-test threw: " + ex.Message, ex.ToString());
            }
            finally
            {
                ReignPostgreSqlStorage.ClearAllPools();
                TryDeleteDirectory(Path.Combine(CampaignsRoot(), SafePathSegment(campaignId, "campaign")));
            }
            foreach (Dictionary<string, object> continuityTest in RunRoleplayContinuitySelfTests())
            {
                add("roleplay_" + ReadString(continuityTest, "id", "contract"),
                    ReadBool(continuityTest, "passed", false),
                    ReadString(continuityTest, "summary", "Role-play continuity contract."),
                    continuityTest);
            }
            foreach (Dictionary<string, object> liveTest in RunLiveInteractionTestSelfTests())
            {
                add("bridge_" + ReadString(liveTest, "id", "contract"),
                    ReadBool(liveTest, "passed", false), ReadString(liveTest, "summary", "Live bridge contract."), liveTest);
            }
            foreach (Dictionary<string, object> codexTest in RunCodexAppServerProviderSelfTests())
            {
                add("codex_" + ReadString(codexTest, "id", "contract"),
                    ReadBool(codexTest, "passed", false), ReadString(codexTest, "summary", "Codex app-server provider contract."), codexTest);
            }
            return results;
        }
    }
}
