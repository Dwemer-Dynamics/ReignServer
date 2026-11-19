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
    internal static partial class ActionPipelineService
    {
        private static Dictionary<string, object> PlanActionsFromGate(string campaignId, Dictionary<string, object> payload, Dictionary<string, object> hero, string playerText, string visibleReply, Dictionary<string, object> actionGate)
        {
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["candidates"] = new List<Dictionary<string, object>>(),
                ["errors"] = new List<string>()
            };
            Dictionary<string, object> settings = SettingsService.LoadSettings();
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            if (!ReadBool(settings, "enableHiddenActionPlanner", true))
            {
                result["errors"] = new List<string> { "Hidden action planner is disabled in settings." };
                return result;
            }

            if (!LlmProviderService.LlmProviderConfigured(settings))
            {
                result["errors"] = new List<string> { "Hidden action planner skipped because the LLM API URL or key is missing." };
                return result;
            }

            Stopwatch timer = Stopwatch.StartNew();
            int maxCandidates = Math.Max(3, ReadInt(settings, "actionRouterCandidateCount", 10));
            string intent = ReadString(actionGate, "intent", "");
            string plannerText = BuildRouterResolutionText(playerText + "\n" + intent, visibleReply);
            List<Dictionary<string, object>> allowedActions = BuildAllowedActionPlannerChoices(settings, payload, hero, actionGate, playerText, visibleReply, maxCandidates);
            result["allowedActionCount"] = allowedActions.Count;
            result["allowedActions"] = allowedActions.Select(x => ReadString(x, "command", "")).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (allowedActions.Count == 0)
            {
                result["errors"] = new List<string> { "No allowed action schemas matched the action gate intent." };
                return result;
            }

            Dictionary<string, string> values = new Dictionary<string, string>
            {
                ["campaignId"] = campaignId,
                ["mode"] = ReadFirstString(payload, "mode", "requestType", "sceneMode"),
                ["playerText"] = playerText ?? "",
                ["npcReply"] = visibleReply ?? "",
                ["actionGateJson"] = Json.Serialize(actionGate ?? new Dictionary<string, object>()),
                ["sceneContextJson"] = Json.Serialize(ReadDictionary(payload, "sceneContext") ?? new Dictionary<string, object>()),
                ["nativeSceneMovementJson"] = Json.Serialize(ReadDictionary(payload, "nativeSceneMovement") ?? new Dictionary<string, object>()),
                ["campaignCalendarJson"] = Json.Serialize(ReadDictionary(payload, "campaignCalendar") ?? new Dictionary<string, object>()),
                ["temporaryGuestJson"] = Json.Serialize(DialogueService.TemporaryGuestDialogueContext(payload, ReadFirstString(payload, "speakerHeroStringId", "heroStringId")) ?? new Dictionary<string, object>()),
                ["allowedActionsJson"] = Json.Serialize(allowedActions),
                ["resolverHintsJson"] = Json.Serialize(BuildPlannerResolverHints(payload, plannerText)),
                ["characterMbti"] = RelationshipsService.BuildCharacterMbtiPromptBlock(
                    campaignId,
                    ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId"),
                    hero,
                    CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId"), "traits.json")))
            };
            DialogueService.PromptEnvelope promptEnvelope = DialogueService.BuildActionPlannerPromptEnvelope(values, allowedActions);
            OperationsService.WriteAudit(campaignId, correlationId, "server", "action_planner", "planner.prompt", ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId"), "", ReadFirstString(payload, "eventId", "socialEventId"), "completed", 0, "Hidden action planner prompt assembled.", new Dictionary<string, object>
            {
                ["actionGate"] = actionGate,
                ["allowedActions"] = allowedActions,
                ["promptChars"] = promptEnvelope.TotalChars,
                ["promptEnvelope"] = promptEnvelope.Diagnostics
            });

            Dictionary<string, object> plannerLlmRequest = new Dictionary<string, object>
            {
                ["requestType"] = "action_planner",
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["heroStringId"] = ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId"),
                ["eventId"] = ReadFirstString(payload, "eventId", "socialEventId"),
                ["messages"] = promptEnvelope.Messages,
                ["promptEnvelope"] = promptEnvelope.Diagnostics,
                ["promptCacheEligible"] = true,
                ["temperature"] = 0.05d,
                ["maxTokens"] = ReadInt(settings, "actionPlannerMaxTokens", 900),
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            };
            Dictionary<string, object> llm = LlmProviderService.ChatWithLlm(plannerLlmRequest);
            llm = ConversationResponseService.WithDiagnosticTransformation("Answer structure and format repair", llm, () => ConversationResponseService.RetryMalformedStructuredResponse(llm, plannerLlmRequest, campaignId, correlationId, "action_planner",
                ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId"),
                ReadFirstString(payload, "eventId", "socialEventId")));
            result["llm"] = llm;
            if (!ReadBool(llm, "ok", false))
            {
                result["errors"] = new List<string> { "Hidden action planner LLM failed: " + ReadString(llm, "error", "") };
                return result;
            }

            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", "")) ?? new Dictionary<string, object>();
            HashSet<string> allowed = new HashSet<string>(allowedActions.Select(x => CanonicalCommand(ReadString(x, "command", ""))), StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> candidates = NormalizeRouterActions(parsed, allowed, payload, hero, plannerText, "hidden_action_planner", "Hidden action gate");
            string acceptedGuestCommand = TemporaryPartyGuestCandidateToPreserve(
                actionGate, playerText, payload, hero);
            bool plannerReturnedAcceptedGuestCommand = !string.IsNullOrWhiteSpace(
                    acceptedGuestCommand)
                && candidates.Any(candidate => string.Equals(
                    CanonicalCommand(ReadString(candidate, "command", "")),
                    acceptedGuestCommand, StringComparison.OrdinalIgnoreCase));
            if (candidates.Count == 0
                || (!string.IsNullOrWhiteSpace(acceptedGuestCommand)
                    && !plannerReturnedAcceptedGuestCommand))
            {
                Dictionary<string, object> acceptedGuestFallback =
                    BuildAcceptedTemporaryPartyGuestFallbackAction(
                        actionGate, payload, hero, playerText, visibleReply, allowed);
                if (acceptedGuestFallback != null)
                {
                    Dictionary<string, object> fallbackEnvelope = new Dictionary<string, object>
                    {
                        ["actions"] = new ArrayList { acceptedGuestFallback }
                    };
                    candidates = NormalizeRouterActions(fallbackEnvelope, allowed, payload, hero,
                        plannerText, "accepted_temporary_guest_fallback",
                        "Accepted temporary guest lifecycle fallback");
                    result["deterministicFallback"] = "accepted_temporary_guest_lifecycle";
                    result["replacedMismatchedPlannerAction"] =
                        !plannerReturnedAcceptedGuestCommand && parsed.Count > 0;
                }
            }
            string acceptedPlayerServiceCommand = AcceptedPlayerServiceCandidateToPreserve(
                actionGate, playerText);
            bool removedSupersededPlayerServiceCandidate = RemoveSupersededPlayerServiceCandidates(
                candidates, acceptedPlayerServiceCommand);
            bool plannerReturnedAcceptedPlayerServiceCommand = !string.IsNullOrWhiteSpace(
                    acceptedPlayerServiceCommand)
                && candidates.Any(candidate => string.Equals(
                    CanonicalCommand(ReadString(candidate, "command", "")),
                    acceptedPlayerServiceCommand, StringComparison.OrdinalIgnoreCase));
            if (candidates.Count == 0
                || (!string.IsNullOrWhiteSpace(acceptedPlayerServiceCommand)
                    && !plannerReturnedAcceptedPlayerServiceCommand))
            {
                Dictionary<string, object> acceptedPlayerServiceFallback =
                    BuildAcceptedPlayerServiceFallbackAction(
                        actionGate, payload, hero, playerText, allowed);
                if (acceptedPlayerServiceFallback != null)
                {
                    Dictionary<string, object> fallbackEnvelope = new Dictionary<string, object>
                    {
                        ["actions"] = new ArrayList { acceptedPlayerServiceFallback }
                    };
                    candidates = NormalizeRouterActions(fallbackEnvelope, allowed, payload, hero,
                        plannerText, "accepted_player_service_fallback",
                        "Accepted player service lifecycle fallback");
                    result["deterministicFallback"] = "accepted_player_service_lifecycle";
                    result["replacedMismatchedPlannerAction"] =
                        (!plannerReturnedAcceptedPlayerServiceCommand
                            || removedSupersededPlayerServiceCandidate)
                        && parsed.Count > 0;
                }
            }
            string acceptedKingdomCommand = AcceptedKingdomMembershipCandidateToPreserve(
                actionGate, playerText);
            bool removedSupersededKingdomCandidate = RemoveSupersededKingdomMembershipCandidates(
                candidates, acceptedKingdomCommand);
            bool plannerReturnedAcceptedKingdomCommand = !string.IsNullOrWhiteSpace(
                    acceptedKingdomCommand)
                && candidates.Any(candidate => string.Equals(
                    CanonicalCommand(ReadString(candidate, "command", "")),
                    acceptedKingdomCommand, StringComparison.OrdinalIgnoreCase));
            if (candidates.Count == 0
                || (!string.IsNullOrWhiteSpace(acceptedKingdomCommand)
                    && !plannerReturnedAcceptedKingdomCommand))
            {
                Dictionary<string, object> acceptedKingdomFallback =
                    BuildAcceptedKingdomMembershipFallbackAction(
                        actionGate, payload, hero, playerText, allowed);
                if (acceptedKingdomFallback != null)
                {
                    Dictionary<string, object> fallbackEnvelope = new Dictionary<string, object>
                    {
                        ["actions"] = new ArrayList { acceptedKingdomFallback }
                    };
                    candidates = NormalizeRouterActions(fallbackEnvelope, allowed, payload, hero,
                        plannerText, "accepted_kingdom_membership_fallback",
                        "Accepted kingdom membership fallback");
                    result["deterministicFallback"] = "accepted_kingdom_membership";
                    result["replacedMismatchedPlannerAction"] =
                        (!plannerReturnedAcceptedKingdomCommand || removedSupersededKingdomCandidate)
                        && parsed.Count > 0;
                }
            }
            string acceptedClanCommand = string.IsNullOrWhiteSpace(acceptedKingdomCommand)
                && string.IsNullOrWhiteSpace(acceptedPlayerServiceCommand)
                ? AcceptedClanMembershipCandidateToPreserve(actionGate, playerText)
                : "";
            bool removedSupersededClanCandidate = RemoveSupersededClanMembershipCandidates(
                candidates, acceptedClanCommand);
            bool plannerReturnedAcceptedClanCommand = !string.IsNullOrWhiteSpace(
                    acceptedClanCommand)
                && candidates.Any(candidate => string.Equals(
                    CanonicalCommand(ReadString(candidate, "command", "")),
                    acceptedClanCommand, StringComparison.OrdinalIgnoreCase));
            if (candidates.Count == 0
                || (!string.IsNullOrWhiteSpace(acceptedClanCommand)
                    && !plannerReturnedAcceptedClanCommand))
            {
                Dictionary<string, object> acceptedClanFallback =
                    BuildAcceptedClanMembershipFallbackAction(
                        actionGate, payload, hero, playerText, allowed);
                if (acceptedClanFallback != null)
                {
                    Dictionary<string, object> fallbackEnvelope = new Dictionary<string, object>
                    {
                        ["actions"] = new ArrayList { acceptedClanFallback }
                    };
                    candidates = NormalizeRouterActions(fallbackEnvelope, allowed, payload, hero,
                        plannerText, "accepted_clan_membership_fallback",
                        "Accepted clan membership fallback");
                    result["deterministicFallback"] = "accepted_clan_membership";
                    result["replacedMismatchedPlannerAction"] =
                        (!plannerReturnedAcceptedClanCommand || removedSupersededClanCandidate)
                        && parsed.Count > 0;
                }
            }
            bool negatesPartyMovementChange = NegatesPartyMovementStateChange(playerText);
            if (negatesPartyMovementChange)
            {
                RemoveAllPartyMovementCandidates(candidates);
                result["removedNegatedPartyMovementCandidates"] = true;
            }
            string acceptedPartyMovementCommand = negatesPartyMovementChange
                ? ""
                : AcceptedPartyMovementCandidateToPreserve(
                    actionGate, playerText);
            bool removedSupersededPartyMovementCandidate =
                RemoveSupersededPartyMovementCandidates(
                    candidates, acceptedPartyMovementCommand);
            bool plannerReturnedAcceptedPartyMovementCommand =
                !string.IsNullOrWhiteSpace(acceptedPartyMovementCommand)
                && candidates.Any(candidate => string.Equals(
                    CanonicalCommand(ReadString(candidate, "command", "")),
                    acceptedPartyMovementCommand,
                    StringComparison.OrdinalIgnoreCase));
            if (candidates.Count == 0
                || (!string.IsNullOrWhiteSpace(acceptedPartyMovementCommand)
                    && !plannerReturnedAcceptedPartyMovementCommand))
            {
                Dictionary<string, object> acceptedPartyMovementFallback =
                    BuildAcceptedPartyMovementFallbackAction(
                        actionGate, payload, hero, playerText, allowed);
                if (acceptedPartyMovementFallback != null)
                {
                    Dictionary<string, object> fallbackEnvelope =
                        new Dictionary<string, object>
                        {
                            ["actions"] = new ArrayList
                            {
                                acceptedPartyMovementFallback
                            }
                        };
                    candidates = NormalizeRouterActions(fallbackEnvelope, allowed,
                        payload, hero, plannerText,
                        "accepted_party_movement_fallback",
                        "Accepted party movement fallback");
                    result["deterministicFallback"] =
                        "accepted_party_movement";
                    result["replacedMismatchedPlannerAction"] =
                        (!plannerReturnedAcceptedPartyMovementCommand
                            || removedSupersededPartyMovementCandidate)
                        && parsed.Count > 0;
                }
            }
            if (candidates.Count == 0)
            {
                var giftFallback = DialogueService.BuildAcceptedNamedGiftFallback(actionGate, payload, hero, playerText, allowed);
                if (giftFallback != null)
                {
                    candidates.Add(giftFallback);
                    result["deterministicFallback"] = "accepted_resolved_named_gift";
                }
            }
            timer.Stop();
            result["ok"] = true;
            result["durationMs"] = timer.ElapsedMilliseconds;
            result["parsed"] = parsed;
            result["candidates"] = candidates;
            if (candidates.Count == 0)
            {
                result["errors"] = new List<string> { "Hidden action planner returned no executable candidate actions." };
                OperationsService.WriteAudit(campaignId, correlationId, "server", "action_planner", "planner.no_action", ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId"), "", ReadFirstString(payload, "eventId", "socialEventId"), "no_action", timer.ElapsedMilliseconds, "Planner returned no candidate actions for an actionable gate.", result);
            }
            else
            {
                OperationsService.WriteAudit(campaignId, correlationId, "server", "action_planner", "planner.candidates", ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId"), "", ReadFirstString(payload, "eventId", "socialEventId"), "completed", timer.ElapsedMilliseconds, "Planner produced candidate actions.", result);
            }

            return result;
        }

        internal static List<Dictionary<string, object>> BuildAllowedActionPlannerChoices(Dictionary<string, object> settings, Dictionary<string, object> payload, Dictionary<string, object> hero, Dictionary<string, object> actionGate, string playerText, string visibleReply, int maxCandidates)
        {
            string text = (ReadString(actionGate, "intent", "") + "\n" + playerText + "\n" + visibleReply).Trim();
            List<string> minimeTopics = DialogueService.TryExtractMinimeTopics(settings, text, ReadInt(settings, "minimeMaxTopics", 5));
            string retrievalText = DialogueService.AppendMinimeTopics(text, minimeTopics);
            List<string> domains = ClassifyActionRouterDomains(retrievalText);
            bool testMode = false;
            Dictionary<string, object> scene = ReadDictionary(payload, "sceneContext") ?? new Dictionary<string, object>();
            string mode = NormalizeLookup(ReadFirstString(payload, "mode", "requestType", "sceneMode") + " " + Json.Serialize(scene));
            if (ReadBool(hero, "isRuler", false) || !string.IsNullOrWhiteSpace(ReadFirstString(payload, "speakerKingdomId", "targetKingdomId")))
            {
                domains.Add("diplomacy");
                domains.Add("politics");
            }

            if (!string.IsNullOrWhiteSpace(ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId")) || ContainsAnyNormalized(mode, "conversation", "dialogue", "scene"))
            {
                domains.Add("regular");
            }

            if (ContainsAnyNormalized(mode, "event", "court"))
            {
                domains.Add("diplomacy");
                domains.Add("politics");
                domains.Add("regular");
            }

            domains = domains.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<Dictionary<string, object>> retrieved = RetrieveActionRouterCommands(retrievalText, domains, Math.Max(3, maxCandidates * 2));
            List<Dictionary<string, object>> commands = ReadDictionaryList(ActionCatalog(), "commands");
            foreach (Dictionary<string, object> command in commands)
            {
                string family = ReadString(command, "family", "");
                if (domains.Count > 0 && !domains.Any(x => string.Equals(x, family, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!retrieved.Any(x => string.Equals(ReadString(x, "command", ""), ReadString(command, "command", ""), StringComparison.OrdinalIgnoreCase)))
                {
                    Dictionary<string, object> copy = new Dictionary<string, object>(command, StringComparer.OrdinalIgnoreCase)
                    {
                        ["score"] = domains.Count > 0 ? 2 : 1,
                        ["aliases"] = ActionRouterAliases(ReadString(command, "command", ""))
                    };
                    retrieved.Add(copy);
                }
            }

            // Retrieval is an efficiency filter, not an authorization boundary. When the
            // accepted dialogue contains a strong deterministic command match, guarantee
            // that schema survives the top-N cut so the hidden planner can still decide
            // whether and how to execute it. This never queues an action by itself.
            string lexicalCommand = CanonicalCommand(CommandFromDirectiveText(
                (ReadString(actionGate, "intent", "") + "\n" + playerText).Trim()));
            string acceptedPartyMovement = AcceptedPartyMovementCandidateToPreserve(
                actionGate, playerText);
            if (!string.IsNullOrWhiteSpace(acceptedPartyMovement))
            {
                // The accepted player request is authoritative for movement routing.
                // Generated acceptance prose can mention a later "return to report",
                // which must not replace an explicit patrol with a travel command.
                lexicalCommand = acceptedPartyMovement;
                var partyMovementCommands = new HashSet<string>(
                    new[]
                    {
                        "follow",
                        "stop_following",
                        "go_to_settlement",
                        "patrol_around_settlement",
                        "wait_near_settlement",
                        "accept_temporary_party_guest",
                        "renew_temporary_party_guest",
                        "end_temporary_party_guest"
                    },
                    StringComparer.OrdinalIgnoreCase);
                retrieved.RemoveAll(candidate =>
                    partyMovementCommands.Contains(CanonicalCommand(
                        ReadString(candidate, "command", "")))
                    && !string.Equals(CanonicalCommand(ReadString(candidate,
                            "command", "")), acceptedPartyMovement,
                        StringComparison.OrdinalIgnoreCase));
            }
            if (LooksLikeNamedHeroGoldTransferDirective(payload, playerText))
            {
                // A named third-party recipient is authoritative direction evidence.
                // Generic money wording such as "pay Tynops 17 denars" otherwise
                // resembles a speaker-to-player gift and can remove transfer_gold
                // before the semantic planner sees it.
                lexicalCommand = "transfer_gold";
            }
            string acceptedKingdomMembership = AcceptedKingdomMembershipCandidateToPreserve(actionGate, playerText);
            string acceptedPlayerService = AcceptedPlayerServiceCandidateToPreserve(actionGate, playerText);
            if (!string.IsNullOrWhiteSpace(acceptedPlayerService))
            {
                lexicalCommand = acceptedPlayerService;
                var playerServiceCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "hire_player_as_mercenary", "dismiss_player_mercenary",
                    "offer_player_vassalage", "dismiss_player_vassal"
                };
                retrieved.RemoveAll(candidate => playerServiceCommands.Contains(CanonicalCommand(
                        ReadString(candidate, "command", "")))
                    && !string.Equals(CanonicalCommand(ReadString(candidate, "command", "")),
                        acceptedPlayerService, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(acceptedKingdomMembership))
            {
                lexicalCommand = acceptedKingdomMembership;
                var supersededKingdomCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "join_clan", "leave_clan",
                    string.Equals(acceptedKingdomMembership, "join_kingdom", StringComparison.OrdinalIgnoreCase)
                        ? "leave_kingdom" : "join_kingdom"
                };
                retrieved.RemoveAll(candidate => supersededKingdomCommands.Contains(CanonicalCommand(
                    ReadString(candidate, "command", ""))));
            }
            string acceptedClanMembership = string.IsNullOrWhiteSpace(acceptedKingdomMembership)
                ? AcceptedClanMembershipCandidateToPreserve(actionGate, playerText)
                : "";
            if (!string.IsNullOrWhiteSpace(acceptedClanMembership))
            {
                lexicalCommand = acceptedClanMembership;
                string supersededClanCommand = string.Equals(acceptedClanMembership,
                    "join_clan", StringComparison.OrdinalIgnoreCase)
                    ? "leave_clan"
                    : "join_clan";
                retrieved.RemoveAll(candidate => string.Equals(CanonicalCommand(
                    ReadString(candidate, "command", "")), supersededClanCommand,
                    StringComparison.OrdinalIgnoreCase));
            }
            if (DialogueService.ShouldPreserveAcceptedItemGift(actionGate, playerText, lexicalCommand))
                lexicalCommand = "transfer_item";
            string temporaryGuestCommand = TemporaryPartyGuestCandidateToPreserve(actionGate, playerText, payload, hero);
            string residentRecruitment = CharactersService.ResidentRecruitmentCandidate(hero, playerText + "\n" + ReadString(actionGate, "intent", ""));
            if (!string.IsNullOrEmpty(residentRecruitment))
            { lexicalCommand = residentRecruitment; temporaryGuestCommand = ""; }
            if (!string.IsNullOrWhiteSpace(temporaryGuestCommand))
            {
                lexicalCommand = temporaryGuestCommand;
                var temporaryGuestLifecycleCommands = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    "accept_temporary_party_guest",
                    "renew_temporary_party_guest",
                    "end_temporary_party_guest",
                    "acknowledge_own_faction_combat_risk"
                };
                retrieved.RemoveAll(candidate =>
                {
                    string candidateCommand = CanonicalCommand(
                        ReadString(candidate, "command", ""));
                    return temporaryGuestLifecycleCommands.Contains(candidateCommand)
                        && !string.Equals(candidateCommand, temporaryGuestCommand,
                            StringComparison.OrdinalIgnoreCase);
                });
            }
            if (!string.IsNullOrWhiteSpace(lexicalCommand))
            {
                // Keep accepted atomic exchanges atomic. Component schemas are useful for
                // simple transfers, but exposing them beside an explicitly identified
                // ransom or barter package lets the planner decompose one bargain into
                // partially executable actions. The specialized package remains a choice;
                // this filter does not authorize or queue it.
                HashSet<string> ambiguousDecompositions = null;
                if (string.Equals(lexicalCommand, "ransom_package", StringComparison.OrdinalIgnoreCase))
                {
                    ambiguousDecompositions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "trade_package",
                        "diplomatic_package",
                        "transfer_prisoner",
                        "give_gold_to_player",
                        "transfer_gold"
                    };
                }
                else if (string.Equals(lexicalCommand, "trade_package", StringComparison.OrdinalIgnoreCase))
                {
                    ambiguousDecompositions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "diplomatic_package",
                        "transfer_prisoner",
                        "transfer_item",
                        "give_gold_to_player",
                        "transfer_gold"
                    };
                }

                if (ambiguousDecompositions != null)
                {
                    retrieved.RemoveAll(x => ambiguousDecompositions.Contains(CanonicalCommand(ReadString(x, "command", ""))));
                }

                HashSet<string> lexicalCommandsToPreserve = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    lexicalCommand
                };
                if (string.Equals(lexicalCommand, "give_gold_to_player", StringComparison.OrdinalIgnoreCase))
                {
                    // GiveGoldToPlayer is the canonical specialized schema when the
                    // accepted speaker gives currency to the player. Keeping the generic
                    // TransferGold schema beside it makes equivalent natural phrasings
                    // report different commands even though their native effect matches.
                    retrieved.RemoveAll(candidate => string.Equals(CanonicalCommand(
                        ReadString(candidate, "command", "")), "transfer_gold",
                        StringComparison.OrdinalIgnoreCase));
                }
                else if (string.Equals(lexicalCommand, "transfer_gold",
                    StringComparison.OrdinalIgnoreCase))
                {
                    // Preserve the generic schema for an explicit FromHero/ToHero transfer
                    // and remove the player-specific alternative from this planner choice.
                    retrieved.RemoveAll(candidate => string.Equals(CanonicalCommand(
                        ReadString(candidate, "command", "")), "give_gold_to_player",
                        StringComparison.OrdinalIgnoreCase));
                }

                foreach (Dictionary<string, object> lexicalCandidate in retrieved.Where(x =>
                    lexicalCommandsToPreserve.Contains(CanonicalCommand(ReadString(x, "command", "")))))
                {
                    lexicalCandidate["score"] = Math.Max(100000, ReadInt(lexicalCandidate, "score", 0));
                }
            }

            return retrieved
                .Where(x => PlannerCanExposeAction(ReadString(x, "command", ""), testMode))
                .Where(x => DialogueService.DialoguePlannerCandidateEligible(ReadString(x, "command", ""), payload, hero, actionGate, playerText))
                .OrderByDescending(x => ReadInt(x, "score", 0))
                .ThenBy(x => ReadString(x, "command", ""))
                .Take(Math.Max(3, maxCandidates))
                .Select(x => new Dictionary<string, object>
                {
                    ["command"] = ReadString(x, "command", ""),
                    ["family"] = ReadString(x, "family", ""),
                    ["capability"] = ActionCapabilityForCommand(ReadString(x, "command", "")),
                    ["required"] = x.ContainsKey("required") ? x["required"] : new ArrayList(),
                    ["optional"] = x.ContainsKey("optional") ? x["optional"] : new ArrayList(),
                    ["description"] = ReadString(x, "description", ""),
                    ["aliases"] = x.ContainsKey("aliases") ? x["aliases"] : ActionRouterAliases(ReadString(x, "command", ""))
                })
                .ToList();
        }

        internal static string TemporaryPartyGuestCandidateToPreserve(
            Dictionary<string, object> actionGate,
            string playerText,
            Dictionary<string, object> payload = null,
            Dictionary<string, object> hero = null)
        {
            string command = TemporaryPartyGuestLifecycleCommandFromConversation(
                actionGate, playerText, true);
            return payload == null ? command : DialogueService.ContextualTemporaryGuestCommand(command, actionGate, playerText, payload, hero);
        }

        internal static bool IsTemporaryPartyGuestLifecycleConversation(
            Dictionary<string, object> actionGate,
            string playerText,
            IEnumerable<Dictionary<string, object>> queuedActions)
        {
            if ((queuedActions ?? Enumerable.Empty<Dictionary<string, object>>()).Any(action =>
                    RelationshipsService.IsTemporaryPartyGuestLifecycleAction(CanonicalCommand(
                        ReadFirstString(ReadDictionary(action, "record") ?? action,
                            "command", "type")))))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(
                TemporaryPartyGuestLifecycleCommandFromConversation(
                    actionGate, playerText, false));
        }

        private static string TemporaryPartyGuestLifecycleCommandFromConversation(
            Dictionary<string, object> actionGate,
            string playerText,
            bool requireAcceptedCommitment)
        {
            if (requireAcceptedCommitment
                && !string.Equals(ReadString(actionGate, "commitment", ""), "accepted",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            // Only a leading canonical decision identifies a present departure. Mentioning
            // a future departure safeguard inside an initial invitation must still join.
            bool explicitDeparture = ReadString(actionGate, "intent", "").Trim()
                .StartsWith("end_temporary_party_guest", StringComparison.OrdinalIgnoreCase);
            string acceptedExchange = NormalizeLookup(ReadString(actionGate, "intent", "") + "\n" + playerText);
            if (!explicitDeparture && ContainsAnyNormalized(acceptedExchange,
                    "accept banishment",
                    "accept that risk",
                    "fighting your own realm",
                    "own faction combat"))
            {
                return "acknowledge_own_faction_combat_risk";
            }

            if (!explicitDeparture && ContainsAnyNormalized(acceptedExchange,
                "join my party",
                "join the player party",
                "travel with my party",
                "ride with my party",
                "travel with me",
                "travel with us",
                "come along with us",
                "join us on the road",
                "traveling companion",
                "travelling companion",
                "temporary road cover",
                "serve as my scout",
                "scout arrangement", "service agreement", "seasonal service", "full service"))
            {
                return "accept_temporary_party_guest";
            }

            // An initial open-ended invitation commonly explains that either side may
            // "end the arrangement" at a future thirty-day review.  That safeguard is
            // not a present departure request.  Prefer the explicit current join verb;
            // genuine departure exchanges do not contain one of these join phrases.
            if (explicitDeparture || ContainsAnyNormalized(acceptedExchange,
                    "accepts release from the party",
                    "return to your people",
                    "return to your own party",
                    "leave my party",
                    "please leave now",
                    "end the arrangement"))
            {
                return "end_temporary_party_guest";
            }

            if (ContainsAnyNormalized(acceptedExchange,
                    "renew the arrangement",
                    "renew our arrangement",
                    "remain with us for another",
                    "stay with us while",
                    "review whether you stay"))
            {
                return "renew_temporary_party_guest";
            }

            return "";
        }

        internal static string AcceptedPartyMovementCandidateToPreserve(
            Dictionary<string, object> actionGate,
            string playerText)
        {
            if (!ActionGateShouldPlan(actionGate)
                || DialogueService.IsSharedTravelOnly(actionGate, playerText)
                || NegatesPartyMovementStateChange(playerText))
            {
                return "";
            }

            string command = CanonicalCommand(CommandFromDirectiveText(
                playerText ?? ""));
            switch (command)
            {
                case "follow":
                case "stop_following":
                case "go_to_settlement":
                case "patrol_around_settlement":
                case "wait_near_settlement":
                    return command;
                default:
                    return "";
            }
        }

        internal static bool NegatesPartyMovementStateChange(string playerText)
        {
            string text = NormalizeLookup(playerText ?? "");
            return Regex.IsMatch(text,
                       @"\b(?:do\s+not|don\s*t|never)\s+(?:stop|cease|quit)\s+(?:following|escorting|trailing)\b",
                       RegexOptions.IgnoreCase)
                || Regex.IsMatch(text,
                       @"\b(?:do\s+not|don\s*t|never)\s+(?:break\s+off(?:\s+the)?\s+escort|go\s+your\s+own\s+way)\b",
                       RegexOptions.IgnoreCase);
        }

        internal static bool RemoveAllPartyMovementCandidates(
            List<Dictionary<string, object>> candidates)
        {
            if (candidates == null)
            {
                return false;
            }

            var partyMovementCommands = new HashSet<string>(
                new[]
                {
                    "follow",
                    "stop_following",
                    "go_to_settlement",
                    "patrol_around_settlement",
                    "wait_near_settlement",
                    "accept_temporary_party_guest",
                    "renew_temporary_party_guest",
                    "end_temporary_party_guest"
                },
                StringComparer.OrdinalIgnoreCase);
            int before = candidates.Count;
            candidates.RemoveAll(candidate => partyMovementCommands.Contains(
                CanonicalCommand(ReadString(candidate, "command", ""))));
            return candidates.Count != before;
        }

        internal static bool RemoveSupersededPartyMovementCandidates(
            List<Dictionary<string, object>> candidates,
            string acceptedCommand)
        {
            if (candidates == null || string.IsNullOrWhiteSpace(acceptedCommand))
            {
                return false;
            }

            var partyMovementCommands = new HashSet<string>(
                new[]
                {
                    "follow",
                    "stop_following",
                    "go_to_settlement",
                    "patrol_around_settlement",
                    "wait_near_settlement",
                    "accept_temporary_party_guest",
                    "renew_temporary_party_guest",
                    "end_temporary_party_guest"
                },
                StringComparer.OrdinalIgnoreCase);
            int before = candidates.Count;
            candidates.RemoveAll(candidate =>
            {
                string command = CanonicalCommand(
                    ReadString(candidate, "command", ""));
                return partyMovementCommands.Contains(command)
                    && !string.Equals(command, acceptedCommand,
                        StringComparison.OrdinalIgnoreCase);
            });
            return candidates.Count != before;
        }

        internal static Dictionary<string, object>
            BuildAcceptedPartyMovementFallbackAction(
                Dictionary<string, object> actionGate,
                Dictionary<string, object> payload,
                Dictionary<string, object> hero,
                string playerText,
                HashSet<string> allowed)
        {
            string command = AcceptedPartyMovementCandidateToPreserve(
                actionGate, playerText);
            if (string.IsNullOrWhiteSpace(command)
                || allowed == null || !allowed.Contains(command))
            {
                return null;
            }

            string speakerHeroId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId",
                    "heroId"),
                ReadString(hero, "heroStringId", ""));
            if (string.IsNullOrWhiteSpace(speakerHeroId))
            {
                return null;
            }

            Dictionary<string, object> action =
                new Dictionary<string, object>
                {
                    ["command"] = command,
                    ["actorHeroStringId"] = speakerHeroId,
                    ["actorHeroId"] = speakerHeroId,
                    ["FromHero"] = speakerHeroId,
                    ["reason"] = "The speaker accepted the player's explicit "
                        + command + " order.",
                    ["source"] = "accepted_party_movement_fallback",
                    ["requiresAcceptance"] = false
                };
            if (string.Equals(command, "follow",
                    StringComparison.OrdinalIgnoreCase))
            {
                string playerHeroId = FirstNonEmpty(
                    ReadFirstString(payload, "playerHeroStringId",
                        "mainHeroStringId", "playerId"),
                    ReadFirstString(hero, "playerHeroStringId",
                        "mainHeroStringId"));
                if (string.IsNullOrWhiteSpace(playerHeroId))
                {
                    return null;
                }
                action["TargetHero"] = playerHeroId;
                action["targetHeroStringId"] = playerHeroId;
            }
            else if (!string.Equals(command, "stop_following",
                         StringComparison.OrdinalIgnoreCase))
            {
                string mentionedSettlement = FindMentionedSettlementName(
                    payload, playerText, "");
                if (string.IsNullOrWhiteSpace(mentionedSettlement))
                {
                    return null;
                }
                action["TargetSettlement"] = mentionedSettlement;
            }
            DialogueService.CompleteConversationActionTerms(action, payload, hero, playerText);
            return action;
        }

        internal static string AcceptedClanMembershipCandidateToPreserve(
            Dictionary<string, object> actionGate,
            string playerText)
        {
            if (!ReadBool(actionGate, "needed", false)
                || !string.Equals(ReadString(actionGate, "commitment", ""), "accepted",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            string request = playerText ?? "";
            string intent = ReadString(actionGate, "intent", "");
            if (Regex.IsMatch(request,
                    @"(?is)\b(?:join|joins|joining|enter|enters|become|becomes)\b.{0,160}\b(?:my|our|the player'?s)\s+(?:clan|house|banner)\b")
                || Regex.IsMatch(request,
                    @"(?is)\b(?:enter|enters|join|joins|become|becomes)\b.{1,120}\bas one of (?:us|our own)\b"))
            {
                return "join_clan";
            }

            if (Regex.IsMatch(request,
                    @"(?is)\b(?:leave|withdraw from|depart from|renounce|quit|abandon)\b.{0,120}\b(?:the |your |their |current )?(?:clan|house)\b")
                || Regex.IsMatch(request,
                    @"(?is)\b(?:stand|remain|become)\b.{0,80}\b(?:without a clan|clanless)\b"))
            {
                return "leave_clan";
            }

            if (Regex.IsMatch(intent,
                    @"(?is)\b(?:leave|withdraws? from|departs? from|renounces?|quits?|abandons?)\b.{0,160}\b(?:clan|house)\b")
                || Regex.IsMatch(intent, @"(?is)\b(?:without a clan|clanless)\b"))
            {
                return "leave_clan";
            }

            return Regex.IsMatch(intent,
                       @"(?is)\b(?:accepts?|accepted|agrees?|agreed|joins?|joining|become|becomes|enter|enters)\b.{0,160}\b(?:clan membership|(?:the |my |our |player'?s )?clan|house|banner)\b")
                ? "join_clan"
                : "";
        }

        internal static bool PromoteAcceptedClanMembershipGate(
            Dictionary<string, object> actionGate,
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string playerText,
            string visibleReply)
        {
            if (actionGate == null || ActionGateShouldPlan(actionGate)
                || ReadBool(ReadDictionary(payload, "intoxicationReceipt"), "suppressedCommitments", false))
                return false;

            string request = playerText ?? "";
            string reply = visibleReply ?? "";
            if (string.IsNullOrWhiteSpace(ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"))
                && string.IsNullOrWhiteSpace(ReadString(hero, "heroStringId", "")))
                return false;
            if (string.IsNullOrWhiteSpace(ReadFirstString(payload, "playerClanId", "actorClanId", "actorClanStringId"))
                && string.IsNullOrWhiteSpace(ReadString(hero, "playerClanId", "")))
                return false;

            bool explicitRequest = Regex.IsMatch(request,
                @"(?is)\b(?:join|enter|become)\b.{0,160}\b(?:my|our|the player'?s)\s+(?:clan|house)\b")
                || Regex.IsMatch(request,
                    @"(?is)\b(?:join|enter|become)\b.{1,120}\bas one of (?:us|our own)\b");
            if (!explicitRequest
                || Regex.IsMatch(request + "\n" + reply,
                    @"(?is)\b(?:temporary|temporarily|guest|scout|disguise|pretend|wear (?:my|our|the) colors|travel with|serve beside|for now|for a while|if|unless|maybe|perhaps|later|tomorrow|eventually)\b"))
                return false;

            bool membershipAcceptance = IsUnambiguousActionAcceptance(reply)
                && Regex.IsMatch(reply,
                    @"(?is)\b(?:accept|agree|join|enter|become|stand)\w*\b.{0,180}\b(?:member|clan|house|one of (?:you|us|your own)|beneath your banner)\b");
            if (!membershipAcceptance) return false;

            actionGate["needed"] = true;
            actionGate["commitment"] = "accepted";
            actionGate["intent"] = "The current speaker accepts membership in the player's clan.";
            actionGate["confidence"] = 1d;
            actionGate["reason"] = "Recovered an explicit present clan-membership request and the NPC's unambiguous final acceptance.";
            return true;
        }

        internal static string AcceptedPlayerServiceCandidateToPreserve(
            Dictionary<string, object> actionGate,
            string playerText)
        {
            if (!ReadBool(actionGate, "needed", false)
                || !string.Equals(ReadString(actionGate, "commitment", ""), "accepted",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            string request = playerText ?? "";
            string intent = ReadString(actionGate, "intent", "");
            string combined = request + "\n" + intent;
            if (Regex.IsMatch(combined,
                    @"(?is)\b(?:release|end|dismiss|terminate|cancel|free)\b.{0,180}\b(?:mercenar(?:y|ies)|paid service|contract|duty)\b"))
            {
                return "dismiss_player_mercenary";
            }
            if (Regex.IsMatch(combined,
                    @"(?is)\b(?:release|dismiss|free|end|cancel|let)\b.{0,180}\b(?:vassal(?:age)?|sworn service|oath)\b"))
            {
                return "dismiss_player_vassal";
            }
            if (Regex.IsMatch(combined,
                    @"(?is)\b(?:hire|employ|take|offer)\b.{0,180}\b(?:me|my clan|us|the player'?s clan|fen Domus)\b.{0,180}\bmercenar(?:y|ies)\b"))
            {
                return "hire_player_as_mercenary";
            }
            if (Regex.IsMatch(combined,
                    @"(?is)\b(?:offer|grant|take|accept|invite)\b.{0,180}\b(?:me|my clan|us|the player'?s clan|fen Domus)\b.{0,180}\bvassal(?:age|s)?\b"))
            {
                return "offer_player_vassalage";
            }

            string normalized = NormalizeLookup(intent);
            if (ContainsAnyNormalized(normalized,
                    "end mercenary service", "release from mercenary service",
                    "release from mercenary contract", "dismiss player mercenary"))
                return "dismiss_player_mercenary";
            if (ContainsAnyNormalized(normalized,
                    "end vassalage", "release from vassalage", "release vassal oath",
                    "dismiss player vassal"))
                return "dismiss_player_vassal";
            if (ContainsAnyNormalized(normalized,
                    "hire player as mercenary", "hire the player clan as mercenaries",
                    "mercenary service for the speaker kingdom"))
                return "hire_player_as_mercenary";
            return ContainsAnyNormalized(normalized,
                    "offer player vassalage", "accept the player clan as vassals",
                    "take the player clan as sworn vassals")
                ? "offer_player_vassalage"
                : "";
        }

        internal static Dictionary<string, object> BuildAcceptedPlayerServiceFallbackAction(
            Dictionary<string, object> actionGate,
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string playerText,
            HashSet<string> allowed)
        {
            string command = AcceptedPlayerServiceCandidateToPreserve(actionGate, playerText);
            if (string.IsNullOrWhiteSpace(command) || allowed == null || !allowed.Contains(command))
                return null;

            string speakerHeroId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""));
            string speakerClanId = FirstNonEmpty(
                ReadFirstString(payload, "speakerClanId", "npcClanId"),
                ReadString(hero, "clanId", ""));
            string speakerKingdomId = FirstNonEmpty(
                ReadFirstString(payload, "speakerKingdomId", "targetKingdomId", "targetKingdomStringId"),
                ReadString(hero, "kingdomId", ""));
            string playerClanId = FirstNonEmpty(
                ReadFirstString(payload, "playerClanId", "actorClanId", "actorClanStringId"),
                ReadString(hero, "playerClanId", ""));
            if (string.IsNullOrWhiteSpace(speakerHeroId)
                || string.IsNullOrWhiteSpace(speakerKingdomId)
                || string.IsNullOrWhiteSpace(playerClanId))
                return null;

            string intent = ReadString(actionGate, "intent", "");
            var fallback = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["command"] = command,
                ["actorHeroId"] = speakerHeroId,
                ["actorHeroStringId"] = speakerHeroId,
                ["actorKingdomId"] = speakerKingdomId,
                ["actorKingdomStringId"] = speakerKingdomId,
                ["targetClanId"] = playerClanId,
                ["targetClanStringId"] = playerClanId,
                ["targetKingdomId"] = speakerKingdomId,
                ["targetKingdomStringId"] = speakerKingdomId,
                ["TargetKingdom"] = speakerKingdomId,
                ["reason"] = FirstNonEmpty(DialogueService.LimitText(intent, 500),
                    "The sovereign explicitly accepted the player's service request."),
                ["source"] = "accepted_player_service_fallback",
                ["requiresAcceptance"] = false
            };
            if (!string.IsNullOrWhiteSpace(speakerClanId))
            {
                fallback["actorClanId"] = speakerClanId;
                fallback["actorClanStringId"] = speakerClanId;
            }
            return fallback;
        }

        private static bool RemoveSupersededPlayerServiceCandidates(
            List<Dictionary<string, object>> candidates,
            string acceptedCommand)
        {
            var commands = new HashSet<string>(new[]
            {
                "hire_player_as_mercenary", "dismiss_player_mercenary",
                "offer_player_vassalage", "dismiss_player_vassal"
            }, StringComparer.OrdinalIgnoreCase);
            if (candidates == null || !commands.Contains(acceptedCommand)) return false;
            int before = candidates.Count;
            candidates.RemoveAll(candidate => commands.Contains(CanonicalCommand(
                    ReadString(candidate, "command", "")))
                && !string.Equals(CanonicalCommand(ReadString(candidate, "command", "")),
                    acceptedCommand, StringComparison.OrdinalIgnoreCase));
            return candidates.Count != before;
        }

        internal static string AcceptedKingdomMembershipCandidateToPreserve(
            Dictionary<string, object> actionGate,
            string playerText)
        {
            if (!ReadBool(actionGate, "needed", false)
                || !string.Equals(ReadString(actionGate, "commitment", ""), "accepted",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            string request = playerText ?? "";
            string intent = ReadString(actionGate, "intent", "");
            if (Regex.IsMatch(request,
                    @"(?is)\b(?:join|joins|joining|enter|enters|entering|bring|pledge|swear)\b.{0,200}\b(?:my|our|the player'?s)\s+(?:kingdom|realm)\b")
                || Regex.IsMatch(request,
                    @"(?is)\b(?:your|their|the)\s+(?:whole\s+)?clan\b.{0,160}\b(?:join|enter|pledge|swear)\b.{0,120}\b(?:my|our|the player'?s)\s+(?:kingdom|realm)\b"))
            {
                return "join_kingdom";
            }

            if (Regex.IsMatch(request,
                    @"(?is)\b(?:leave|withdraw from|depart from|renounce|quit|abandon|break)\b.{0,160}\b(?:kingdom|realm|allegiance|empire)\b")
                || Regex.IsMatch(request,
                    @"(?is)\b(?:take|pull)\b.{0,160}\bout of\b.{0,120}\b(?:kingdom|realm|empire)\b")
                || Regex.IsMatch(request,
                    @"(?is)\bmake\b.{0,120}\b(?:your|their|the)\s+(?:whole\s+)?clan\s+independent\b"))
            {
                return "leave_kingdom";
            }

            string normalizedIntent = NormalizeLookup(intent);
            if (ContainsAnyNormalized(normalizedIntent,
                    "join kingdom", "join my kingdom", "join our kingdom",
                    "joins the player kingdom", "clan joins the player realm",
                    "accept defection", "accepts defection"))
            {
                return "join_kingdom";
            }
            return ContainsAnyNormalized(normalizedIntent,
                    "leave kingdom", "leaves the kingdom", "withdraw from kingdom",
                    "break allegiance", "renounce allegiance")
                ? "leave_kingdom"
                : "";
        }

        internal static Dictionary<string, object> BuildAcceptedKingdomMembershipFallbackAction(
            Dictionary<string, object> actionGate,
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string playerText,
            HashSet<string> allowed)
        {
            string command = AcceptedKingdomMembershipCandidateToPreserve(actionGate, playerText);
            if ((command != "join_kingdom" && command != "leave_kingdom")
                || allowed == null || !allowed.Contains(command))
            {
                return null;
            }

            string speakerHeroId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""));
            string speakerClanId = FirstNonEmpty(
                ReadFirstString(payload, "speakerClanId", "npcClanId"),
                ReadString(hero, "clanId", ""));
            if (string.IsNullOrWhiteSpace(speakerHeroId)
                || string.IsNullOrWhiteSpace(speakerClanId))
            {
                return null;
            }

            string intent = ReadString(actionGate, "intent", "");
            Dictionary<string, object> fallback = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["command"] = command,
                ["actorHeroId"] = speakerHeroId,
                ["actorHeroStringId"] = speakerHeroId,
                ["actorClanId"] = speakerClanId,
                ["actorClanStringId"] = speakerClanId,
                ["reason"] = FirstNonEmpty(DialogueService.LimitText(intent, 500),
                    command == "join_kingdom"
                        ? "The current clan leader explicitly accepted joining the player's kingdom."
                        : "The current clan leader explicitly accepted leaving their kingdom."),
                ["source"] = "accepted_kingdom_membership_fallback",
                ["requiresAcceptance"] = false
            };
            if (command == "join_kingdom")
            {
                string playerKingdomId = FirstNonEmpty(
                    ReadFirstString(payload, "playerKingdomId", "actorKingdomId", "actorKingdomStringId"),
                    ReadString(hero, "playerKingdomId", ""));
                if (string.IsNullOrWhiteSpace(playerKingdomId)) return null;
                fallback["targetKingdomId"] = playerKingdomId;
                fallback["targetKingdomStringId"] = playerKingdomId;
                fallback["TargetKingdom"] = playerKingdomId;
            }
            return fallback;
        }

        internal static bool RemoveSupersededKingdomMembershipCandidates(
            List<Dictionary<string, object>> candidates,
            string acceptedKingdomCommand)
        {
            if (candidates == null
                || (acceptedKingdomCommand != "join_kingdom"
                    && acceptedKingdomCommand != "leave_kingdom"))
            {
                return false;
            }
            var superseded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "join_clan", "leave_clan",
                acceptedKingdomCommand == "join_kingdom" ? "leave_kingdom" : "join_kingdom"
            };
            return candidates.RemoveAll(candidate => superseded.Contains(CanonicalCommand(
                ReadString(candidate, "command", "")))) > 0;
        }

        internal static Dictionary<string, object> BuildAcceptedClanMembershipFallbackAction(
            Dictionary<string, object> actionGate,
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string playerText,
            HashSet<string> allowed)
        {
            string command = AcceptedClanMembershipCandidateToPreserve(actionGate, playerText);
            if ((command != "join_clan" && command != "leave_clan")
                || allowed == null || !allowed.Contains(command))
            {
                return null;
            }

            string speakerHeroId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""));
            if (string.IsNullOrWhiteSpace(speakerHeroId))
            {
                return null;
            }

            string intent = ReadString(actionGate, "intent", "");
            Dictionary<string, object> fallback = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["command"] = command,
                ["actorHeroId"] = speakerHeroId,
                ["actorHeroStringId"] = speakerHeroId,
                ["reason"] = FirstNonEmpty(DialogueService.LimitText(intent, 500),
                    command == "join_clan"
                        ? "The current speaker explicitly accepted membership in the player's clan."
                        : "The current speaker explicitly accepted leaving their current clan."),
                ["source"] = "accepted_clan_membership_fallback",
                ["requiresAcceptance"] = false
            };
            if (command == "join_clan")
            {
                string playerClanId = FirstNonEmpty(
                    ReadFirstString(payload, "playerClanId", "actorClanId", "actorClanStringId"),
                    ReadString(hero, "playerClanId", ""));
                if (string.IsNullOrWhiteSpace(playerClanId))
                {
                    return null;
                }
                fallback["targetClanId"] = playerClanId;
                fallback["targetClanStringId"] = playerClanId;
                fallback["TargetClan"] = playerClanId;
            }
            return fallback;
        }

        internal static bool RemoveSupersededClanMembershipCandidates(
            List<Dictionary<string, object>> candidates,
            string acceptedClanCommand)
        {
            if (candidates == null
                || (!string.Equals(acceptedClanCommand, "join_clan",
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(acceptedClanCommand, "leave_clan",
                        StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            string supersededCommand = string.Equals(acceptedClanCommand, "join_clan",
                StringComparison.OrdinalIgnoreCase) ? "leave_clan" : "join_clan";
            return candidates.RemoveAll(candidate => string.Equals(CanonicalCommand(
                ReadString(candidate, "command", "")), supersededCommand,
                StringComparison.OrdinalIgnoreCase)) > 0;
        }

        internal static Dictionary<string, object> BuildAcceptedTemporaryPartyGuestFallbackAction(
            Dictionary<string, object> actionGate,
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string playerText,
            string visibleReply,
            HashSet<string> allowed)
        {
            string command = TemporaryPartyGuestCandidateToPreserve(actionGate, playerText, payload, hero);
            if (string.IsNullOrWhiteSpace(command)
                || allowed == null
                || !allowed.Contains(command)
                || !string.Equals(ReadString(actionGate, "commitment", ""), "accepted",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string speakerHeroId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""));
            if (string.IsNullOrWhiteSpace(speakerHeroId))
            {
                return null;
            }

            string intent = ReadString(actionGate, "intent", "");
            string acceptedExchange = NormalizeLookup(intent + "\n" + playerText + "\n" + visibleReply);
            Dictionary<string, object> terms = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (command == "accept_temporary_party_guest"
                || command == "renew_temporary_party_guest")
            {
                terms["purpose"] = FirstNonEmpty(DialogueService.LimitText(intent, 400),
                    "Temporary travel requested by the player and explicitly accepted by the current noble.");
                terms["consentConfirmed"] = true;
                if (!DialogueService.TryCompleteAcceptedGuestSchedule(terms, payload, acceptedExchange)) return null;
                if (command == "renew_temporary_party_guest" && ReadString(terms, "termKind", "") == "open_ended"
                    && !ContainsAnyNormalized(acceptedExchange, "open ended", "no fixed end date", "every thirty days"))
                    return null; // An unspecified extension must not silently replace a fixed term with open-ended service.
                var existing = DialogueService.TemporaryGuestDialogueContext(payload, speakerHeroId);
                if (ReadString(existing, "phase", "") == "AwaitingStart" && ReadBool(terms, "startNow", false))
                {
                    var pendingTerms = ReadDictionary(existing, "pendingTerms");
                    if (pendingTerms != null)
                    {
                        // Starting an existing agreement preserves its saved rate and
                        // duration; newly negotiated replacements belong to the planner.
                        terms = LlmProviderService.ToCaseInsensitiveDictionary(pendingTerms);
                        terms["startNow"] = true;
                        terms["deferStart"] = false;
                        terms["consentConfirmed"] = true;
                    }
                }

                if (ContainsAnyNormalized(acceptedExchange,
                        "detach from the army", "leave the present army", "disband the army",
                        "disband its formation", "army detachment"))
                {
                    terms["armyExitAccepted"] = true;
                }
            }
            else if (command == "acknowledge_own_faction_combat_risk")
            {
                terms["consentConfirmed"] = true;
            }
            else if (command == "end_temporary_party_guest")
            {
                terms["departureReason"] = FirstNonEmpty(DialogueService.LimitText(intent, 400),
                    "The current guest explicitly accepted departure from the player party.");
            }

            DialogueService.BindTemporaryGuestActionTerms(command, terms, payload, hero);
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["command"] = command,
                ["actorHeroStringId"] = speakerHeroId,
                ["actorHeroId"] = speakerHeroId,
                ["FromHero"] = speakerHeroId,
                ["reason"] = FirstNonEmpty(DialogueService.LimitText(intent, 500),
                    "The accepted temporary guest lifecycle decision was recovered after an empty planner result."),
                ["terms"] = terms,
                ["source"] = "accepted_temporary_guest_fallback",
                ["requiresAcceptance"] = false
            };
        }

        internal static bool PlannerCanExposeAction(string command, bool testMode)
        {
            string capability = ActionCapabilityForCommand(command);
            // Catalog membership is the production availability boundary. Commands that
            // remain registered must reach the planner; native validation and execution
            // still decide whether a selected action can run. Only explicitly retired
            // schemas are hidden. Keep testMode in the signature for existing test seams.
            _ = testMode;
            return !capability.Equals("unsupported", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> BuildPlannerResolverHints(Dictionary<string, object> payload, string text)
        {
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex") ?? new Dictionary<string, object>();
            Dictionary<string, object> hints = new Dictionary<string, object>
            {
                ["aliases"] = ReadDictionaryList(index, "aliases").Take(80).ToList(),
                ["mentionedSettlements"] = MentionedResolverEntries(index, "settlements", "name", text, 20),
                ["mentionedHeroes"] = MentionedResolverEntries(index, "heroes", "name", text, 20),
                ["mentionedKingdoms"] = MentionedResolverEntries(index, "kingdoms", "name", text, 20),
                ["mentionedClans"] = MentionedResolverEntries(index, "clans", "name", text, 20),
                ["mentionedItems"] = DialogueService.MentionedActionItems(index, text),
                ["recentContext"] = ReadDictionaryList(index, "recentContext").Take(20).ToList()
            };
            Dictionary<string, object> sceneContext = ReadDictionary(payload, "sceneContext");
            if (sceneContext != null)
            {
                hints["sceneContext"] = sceneContext;
            }

            return hints;
        }

        internal static List<Dictionary<string, object>> MentionedResolverEntries(Dictionary<string, object> index, string key, string nameKey, string text, int limit)
        {
            string q = NormalizeLookup(text);
            List<Dictionary<string, object>> rows = ReadDictionaryList(index, key);
            return rows
                .Where(row =>
                {
                    string name = NormalizeLookup(ReadString(row, nameKey, ""));
                    return name.Length >= 3 && ContainsNormalizedPhrase(q, name);
                })
                .Take(Math.Max(1, limit))
                .ToList();
        }

        internal static List<Dictionary<string, object>> BuildDeterministicActionCandidates(Dictionary<string, object> payload, Dictionary<string, object> hero, string text, string source, string reasonPrefix, List<string> errors)
        {
            List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
            string directiveText = StripDirectiveMarkers(text);
            string command = CommandFromDirectiveText(directiveText);
            if (string.IsNullOrWhiteSpace(command) && DialogueService.HasNativeItemGiftRequest(payload, directiveText)) command = "transfer_item";
            if (string.IsNullOrWhiteSpace(command) && LooksLikeNamedSettlementTransferDirective(payload, directiveText))
            {
                command = "demand_settlement_peace";
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                errors?.Add("No deterministic action mapping matched the text.");
                return candidates;
            }

            Dictionary<string, object> action = new Dictionary<string, object>
            {
                ["command"] = command,
                ["source"] = string.IsNullOrWhiteSpace(source) ? "deterministic_action_extractor" : source,
                ["requiresAcceptance"] = false,
                ["reason"] = (string.IsNullOrWhiteSpace(reasonPrefix) ? "Detected action" : reasonPrefix) + ": " + directiveText
            };
            string mentionedSettlement = FindMentionedSettlementName(payload, directiveText, "");
            if (!string.IsNullOrWhiteSpace(mentionedSettlement) && CanInferSettlementFromText(command))
            {
                action["TargetSettlement"] = mentionedSettlement;
            }

            if (string.Equals(source, "test_lab_extractor", StringComparison.Ordinal))
            {
                CompleteTestDirectiveActionDefaults(action, payload, hero, directiveText);
                CompleteFallbackDirectiveTerms(action, payload, hero, directiveText);
            }
            else DialogueService.CompleteConversationActionTerms(action, payload, hero, directiveText);
            action["capability"] = ActionCapabilityForCommand(command);
            candidates.Add(action);
            return candidates;
        }

        internal static List<Dictionary<string, object>> EvaluateAndQueueActionCandidates(string campaignId, Dictionary<string, object> payload, string resolutionText, List<Dictionary<string, object>> candidates, out List<string> errors, out List<Dictionary<string, object>> rejected)
        {
            errors = new List<string>();
            rejected = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> queued = new List<Dictionary<string, object>>();
            List<string> allErrors = new List<string>();

            foreach (Dictionary<string, object> candidate in candidates ?? new List<Dictionary<string, object>>())
            {
                if (DialogueService.IsCurrentPlayerTransferAlternative(candidate, payload)) continue;
                Dictionary<string, object> candidateCopy = VerificationService.CloneDictionary(candidate);
                Dictionary<string, object> normalized = NormalizeActionCommand(candidateCopy, campaignId, out List<string> actionErrors, payload, resolutionText);
                if (normalized != null && actionErrors.Count == 0)
                {
                    if (DialogueService.IsCurrentPlayerTransferAlternative(normalized, payload)) continue;
                    queued.Add(QueueAction(campaignId, normalized));
                    break;
                }

                string command = ReadFirstString(candidate, "command", "action", "commandType", "intent", "type");
                Dictionary<string, object> repaired = DialogueService.IsSinglePassConversation(payload) ? null
                    : TryRepairActionCandidate(campaignId, payload, resolutionText, candidateCopy, actionErrors);
                if (repaired != null)
                {
                    string originalSource = ReadString(candidateCopy, "source", "");
                    if (IsDialogueActionSource(originalSource))
                    {
                        repaired["source"] = originalSource;
                    }
                    DialogueService.BindAcceptedItemDelivery(repaired, ReadString(candidateCopy, "acceptedByHeroStringId", ""));
                    Dictionary<string, object> repairedNormalized = NormalizeActionCommand(repaired, campaignId, out List<string> repairErrors, payload, resolutionText);
                    if (repairedNormalized != null && repairErrors.Count == 0)
                    {
                        if (DialogueService.IsCurrentPlayerTransferAlternative(repairedNormalized, payload)) continue;
                        queued.Add(QueueAction(campaignId, repairedNormalized));
                        break;
                    }

                    rejected.Add(new Dictionary<string, object>
                    {
                        ["candidate"] = repaired,
                        ["command"] = ReadFirstString(repaired, "command", "action", "commandType", "intent", "type"),
                        ["capability"] = ActionCapabilityForCommand(ReadFirstString(repaired, "command", "action", "commandType", "intent", "type")),
                        ["failureClass"] = ClassifyActionFailure(1, repairErrors),
                        ["errors"] = repairErrors,
                        ["repairOf"] = candidate
                    });
                    allErrors.AddRange(repairErrors);
                }

                rejected.Add(new Dictionary<string, object>
                {
                    ["candidate"] = candidate,
                    ["command"] = command,
                    ["capability"] = ActionCapabilityForCommand(command),
                    ["failureClass"] = ClassifyActionFailure(1, actionErrors),
                    ["errors"] = actionErrors
                });
                allErrors.AddRange(actionErrors);
            }

            if (queued.Count == 0)
            {
                errors.AddRange(allErrors.Distinct(StringComparer.OrdinalIgnoreCase));
            }

            return queued;
        }
    }
}
