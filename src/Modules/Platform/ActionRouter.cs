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
        internal static Dictionary<string, object> RouteActionsApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Stopwatch timer = Stopwatch.StartNew();
            string campaignId = ReadString(payload, "campaignId", "default");
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>
            {
                ["heroStringId"] = ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId")
            };
            string playerText = ReadFirstString(payload, "playerText", "text", "message");
            string npcReply = ReadFirstString(payload, "npcReplyText", "visibleReply", "reply", "response");
            string source = ReadString(payload, "source", "api_action_router");
            bool allowLlm = ReadBool(payload, "useLlm", ReadBool(SettingsService.LoadSettings(), "enableActionRouterLlm", true));

            Dictionary<string, object> route = RouteActionTurn(campaignId, payload, hero, playerText, npcReply, new List<Dictionary<string, object>>(), source, "Action router", allowLlm);
            List<Dictionary<string, object>> candidates = ReadDictionaryList(route, "candidates");
            List<Dictionary<string, object>> normalized = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> rejected = new List<Dictionary<string, object>>();
            List<string> errors = new List<string>();
            string resolutionText = BuildRouterResolutionText(playerText, npcReply);
            foreach (Dictionary<string, object> candidate in candidates)
            {
                Dictionary<string, object> record = NormalizeActionCommand(VerificationService.CloneDictionary(candidate), campaignId, out List<string> actionErrors, payload, resolutionText);
                if (record != null && actionErrors.Count == 0)
                {
                    normalized.Add(record);
                }
                else
                {
                    rejected.Add(new Dictionary<string, object>
                    {
                        ["candidate"] = candidate,
                        ["command"] = ReadFirstString(candidate, "command", "action", "commandType", "intent", "type"),
                        ["failureClass"] = ClassifyActionFailure(1, actionErrors),
                        ["errors"] = actionErrors
                    });
                    errors.AddRange(actionErrors);
                }
            }

            timer.Stop();
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["durationMs"] = timer.ElapsedMilliseconds,
                ["route"] = route,
                ["candidates"] = candidates,
                ["normalizedActions"] = normalized,
                ["rejectedCandidates"] = rejected,
                ["errors"] = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        internal static Dictionary<string, object> PlanActionsApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Stopwatch timer = Stopwatch.StartNew();
            string campaignId = ReadString(payload, "campaignId", "web_planner_preview");
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>
            {
                ["heroStringId"] = ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ["name"] = ReadFirstString(payload, "speakerName", "heroName")
            };
            string playerText = ReadFirstString(payload, "playerText", "text", "message");
            string npcReply = ReadFirstString(payload, "npcReplyText", "visibleReply", "reply", "response");
            Dictionary<string, object> actionGate = NormalizeActionGate(ReadDictionary(payload, "actionGate"), playerText, npcReply);
            if (!ReadBool(actionGate, "needed", false) && !string.IsNullOrWhiteSpace(ReadFirstString(payload, "actionGateIntent", "intent")))
            {
                actionGate["needed"] = true;
                string commitment = ReadFirstString(payload, "actionGateCommitment", "commitment");
                actionGate["commitment"] = string.IsNullOrWhiteSpace(commitment) ? "accepted" : commitment;
                actionGate["intent"] = ReadFirstString(payload, "actionGateIntent", "intent");
                actionGate["confidence"] = ReadDouble(payload, "actionGateConfidence", 0.9d);
                actionGate["reason"] = "Web planner preview.";
            }

            Dictionary<string, object> planner = PlanActionsFromGate(campaignId, payload, hero, playerText, npcReply, actionGate);
            List<Dictionary<string, object>> normalized = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> rejected = new List<Dictionary<string, object>>();
            List<string> errors = new List<string>();
            string resolutionText = BuildRouterResolutionText(playerText + "\n" + ReadString(actionGate, "intent", ""), npcReply);
            foreach (Dictionary<string, object> candidate in ReadDictionaryList(planner, "candidates"))
            {
                Dictionary<string, object> record = NormalizeActionCommand(VerificationService.CloneDictionary(candidate), campaignId, out List<string> actionErrors, payload, resolutionText);
                if (record != null && actionErrors.Count == 0)
                {
                    normalized.Add(record);
                }
                else
                {
                    rejected.Add(new Dictionary<string, object>
                    {
                        ["candidate"] = candidate,
                        ["command"] = ReadFirstString(candidate, "command", "action", "commandType", "intent", "type"),
                        ["failureClass"] = ClassifyActionFailure(1, actionErrors),
                        ["errors"] = actionErrors
                    });
                    errors.AddRange(actionErrors);
                }
            }

            timer.Stop();
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["durationMs"] = timer.ElapsedMilliseconds,
                ["actionGate"] = actionGate,
                ["planner"] = planner,
                ["normalizedActions"] = normalized,
                ["rejectedCandidates"] = rejected,
                ["errors"] = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        internal static Dictionary<string, object> RouteActionTurn(
            string campaignId,
            Dictionary<string, object> payload,
            Dictionary<string, object> hero,
            string playerText,
            string npcReply,
            List<Dictionary<string, object>> suggestedActions,
            string source,
            string reasonPrefix,
            bool allowLlm)
        {
            Stopwatch timer = Stopwatch.StartNew();
            payload = payload ?? new Dictionary<string, object>();
            hero = hero ?? new Dictionary<string, object>();
            string correlationId = OperationsService.EnsureCorrelationId(payload);
            string routeText = BuildRouterResolutionText(playerText, npcReply);
            Dictionary<string, object> settings = SettingsService.LoadSettings();
            int maxCandidates = Math.Max(3, ReadInt(settings, "actionRouterCandidateCount", 10));
            List<string> minimeTopics = DialogueService.TryExtractMinimeTopics(settings, routeText, ReadInt(settings, "minimeMaxTopics", 5));
            string retrievalText = DialogueService.AppendMinimeTopics(routeText, minimeTopics);
            List<string> domains = ClassifyActionRouterDomains(retrievalText);
            List<Dictionary<string, object>> retrieved = RetrieveActionRouterCommands(retrievalText, domains, maxCandidates);
            List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
            List<string> errors = new List<string>();
            List<string> deterministicErrors = new List<string>();

            foreach (Dictionary<string, object> deterministic in BuildDeterministicActionCandidates(payload, hero, routeText, string.IsNullOrWhiteSpace(source) ? "action_router_deterministic" : source + "_deterministic", reasonPrefix, deterministicErrors))
            {
                AppendUniqueActionCandidate(candidates, deterministic);
            }

            if (suggestedActions != null)
            {
                foreach (Dictionary<string, object> suggested in suggestedActions)
                {
                    Dictionary<string, object> copy = new Dictionary<string, object>(suggested ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
                    copy["source"] = ReadString(copy, "source", "dialogue_suggested_action");
                    AppendUniqueActionCandidate(candidates, copy);
                }
            }

            Dictionary<string, object> llmRoute = new Dictionary<string, object>();
            // A command name without its required terms is only a guess. It must
            // not suppress semantic extraction or win deduplication over it.
            var executableCandidates = DialogueService.ExecutableRouterCandidates(candidates, campaignId, payload, routeText);
            bool shouldRunLlm = allowLlm && ShouldRunActionRouterLlm(settings, routeText, executableCandidates.Count);
            if (shouldRunLlm)
            {
                llmRoute = TryRouteActionsWithLlm(settings, campaignId, correlationId, payload, hero, routeText, retrieved, source, reasonPrefix);
                var resolvedCandidates = ReadDictionaryList(llmRoute, "candidates");
                if (resolvedCandidates.Count > 0)
                    candidates = DialogueService.PreferResolvedRouterCandidates(resolvedCandidates, executableCandidates);

                errors.AddRange(ReadStringList(llmRoute, "errors"));
            }

            if (candidates.Count == 0)
            {
                errors.AddRange(deterministicErrors);
            }

            timer.Stop();
            Dictionary<string, object> route = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["shouldFireAction"] = candidates.Count > 0,
                ["durationMs"] = timer.ElapsedMilliseconds,
                ["domains"] = domains,
                ["minimeTopics"] = minimeTopics,
                ["retrievedCommands"] = retrieved,
                ["candidateCount"] = candidates.Count,
                ["candidates"] = candidates,
                ["deterministicErrors"] = deterministicErrors,
                ["llmRouter"] = llmRoute,
                ["errors"] = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };

            OperationsService.WriteAudit(campaignId, correlationId, "server", "action_router", "action.router", ReadFirstString(payload, "heroStringId", "heroId", "speakerHeroStringId"), "", ReadFirstString(payload, "eventId", "socialEventId"), candidates.Count > 0 ? "completed" : "no_action", timer.ElapsedMilliseconds, candidates.Count > 0 ? "Action router produced candidates." : "Action router found no action.", route);
            return route;
        }

        internal static string BuildRouterResolutionText(string playerText, string npcReply)
        {
            StringBuilder builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(playerText))
            {
                builder.AppendLine("player: " + playerText.Trim());
            }

            if (!string.IsNullOrWhiteSpace(npcReply))
            {
                builder.AppendLine("npc: " + npcReply.Trim());
            }

            return builder.ToString().Trim();
        }

        private static bool ShouldRunActionRouterLlm(Dictionary<string, object> settings, string routeText, int existingCandidateCount)
        {
            if (!ReadBool(settings, "enableActionRouterLlm", true))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(routeText) || !LlmProviderService.LlmProviderConfigured(settings))
            {
                return false;
            }

            return existingCandidateCount == 0 || ReadBool(settings, "actionRouterAlwaysRun", false);
        }

        private static List<string> ClassifyActionRouterDomains(string text)
        {
            string q = NormalizeLookup(text);
            List<string> domains = new List<string>();
            if (ContainsAnyNormalized(q, "peace", "war", "tribute", "treaty", "alliance", "pact", "surrender", "reparations", "ransom", "prisoner exchange", "kingdom", "settlement for peace"))
            {
                domains.Add("diplomacy");
            }

            if (ContainsAnyNormalized(q, "raid", "besiege", "attack", "army", "recruit", "patrol", "follow", "go to", "travel", "wait near", "capture", "party"))
            {
                domains.Add("regular");
                domains.Add("strategy");
            }

            if (ContainsAnyNormalized(q, "gold", "denar", "give", "pay", "transfer", "item", "armor", "armour", "grain", "horse", "workshop", "ships", "fleet"))
            {
                domains.Add("regular");
                domains.Add("diplomacy");
            }

            if (ContainsAnyNormalized(q, "rebellion", "ruling clan", "claimant", "defect", "exile", "restore", "marriage", "mediate", "clan dispute"))
            {
                domains.Add("politics");
            }

            if (ContainsAnyNormalized(q, "duel", "kill", "execute", "surrender to me", "leave me alone", "hire me", "vassal", "mercenary"))
            {
                domains.Add("regular");
            }

            return domains.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<Dictionary<string, object>> RetrieveActionRouterCommands(string text, List<string> domains, int maxCandidates)
        {
            string normalized = NormalizeLookup(text);
            List<Dictionary<string, object>> commands = ReadDictionaryList(ActionCatalog(), "commands");
            List<Dictionary<string, object>> scored = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> command in commands)
            {
                string commandId = ReadString(command, "command", "");
                string family = ReadString(command, "family", "");
                int score = 0;
                if (domains.Any(x => string.Equals(x, family, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 6;
                }

                foreach (string alias in ActionRouterAliases(commandId))
                {
                    if (ContainsAnyNormalized(normalized, alias))
                    {
                        score += 12;
                    }
                }

                string commandWords = NormalizeLookup(commandId.Replace("_", " ") + " " + ReadString(command, "description", ""));
                foreach (string token in normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 4).Distinct())
                {
                    if ((" " + commandWords + " ").Contains(" " + token + " "))
                    {
                        score += 1;
                    }
                }

                if (score > 0)
                {
                    Dictionary<string, object> copy = new Dictionary<string, object>(command, StringComparer.OrdinalIgnoreCase)
                    {
                        ["score"] = score,
                        ["aliases"] = ActionRouterAliases(commandId)
                    };
                    scored.Add(copy);
                }
            }

            return scored
                .OrderByDescending(x => ReadInt(x, "score", 0))
                .ThenBy(x => ReadString(x, "command", ""))
                .Take(Math.Max(1, maxCandidates))
                .ToList();
        }

        private static List<string> ActionRouterAliases(string command)
        {
            switch (CanonicalCommand(command))
            {
                case "declare_war": return new List<string> { "declare war", "start war", "go to war", "make war" };
                case "make_peace": return new List<string> { "make peace", "end the war", "cease fighting", "settle hostilities" };
                case "offer_tribute_peace": return new List<string> { "pay tribute", "offer tribute", "tribute for peace" };
                case "demand_reparations_peace": return new List<string> { "reparations", "compensation", "indemnity", "pay for peace" };
                case "demand_settlement_peace": return new List<string> { "give me a town", "give me a city", "give me a castle", "surrender town", "surrender city", "cede settlement" };
                case "demand_surrender_peace": return new List<string> { "full surrender", "capitulation", "all your towns", "all your castles", "submit kingdom" };
                case "sign_trade_agreement": return new List<string> { "trade agreement", "trade treaty", "commerce pact" };
                case "sign_non_aggression_pact": return new List<string> { "non aggression", "non aggression pact", "do not attack", "no attack pact" };
                case "sign_alliance": return new List<string> { "alliance", "ally with me", "be allies" };
                case "sign_defensive_pact": return new List<string> { "defensive pact", "defend each other" };
                case "diplomatic_package": return new List<string> { "treaty package", "peace package", "prisoners for peace", "resources for alliance", "recognition package" };
                case "trade_package": return new List<string> { "in exchange", "for denars", "for gold", "gold for", "denars for", "trade for", "price", "sell me", "buy your", "purchase", "barter", "ransom for", "ships for" };
                case "supply_agreement": return new List<string> { "ships", "fleet", "supplies", "grain", "horses", "naval" };
                case "follow": return new List<string> { "follow me", "follow my party", "follow our party", "escort me", "escort us", "come with me", "stay close", "walk after me", "stay behind our party", "keep to our route" };
                case "stop_following": return new List<string> { "stop following", "break off the escort", "go your own way", "no need to trail", "trail us any longer" };
                case "go_to_settlement": return new List<string> { "go to", "head to", "travel to", "ride to", "move to" };
                case "patrol_around_settlement": return new List<string> { "patrol", "patrol around", "guard the roads" };
                case "wait_near_settlement": return new List<string> { "wait near", "wait outside", "hold near" };
                case "raid_village": return new List<string> { "raid village", "raid the village", "burn village", "pillage village" };
                case "besiege_settlement": return new List<string> { "besiege", "lay siege", "surround the city", "take the castle" };
                case "attack_party": return new List<string> { "attack party", "attack that party", "hunt them down" };
                case "attack_player_party": return new List<string> { "attack me", "attack my party" };
                case "surrender_to_player": return new List<string> { "surrender to me", "yield to me", "lay down arms" };
                case "leave_player_alone": return new List<string> { "leave me alone", "stop pursuing me", "let me go" };
                case "duel_player": return new List<string> { "duel", "training duel", "duel to the death", "cross swords" };
                case "prepare_arrest": return new List<string> { "arrest", "guards take", "take to dungeon", "take to prison", "seize", "detain" };
                case "confirm_arrest": return new List<string> { "take them away", "proceed with arrest", "carry out arrest", "to the dungeon" };
                case "release_arrested_character": return new List<string> { "release prisoner", "free them", "let them out" };
                case "rescind_arrest_accusation": return new List<string> { "rescind accusation", "drop charges", "clear their name", "exonerate" };
                case "player_attack_party": return new List<string> { "order my men to attack", "attack their party", "seize by force" };
                case "give_gold_to_player": return new List<string> { "give me gold", "give me denars", "pay me" };
                case "transfer_gold": return new List<string> { "transfer gold", "send gold", "pay denars", "give denars" };
                case "transfer_item": return new List<string> { "give me your armor", "give item", "transfer item", "take this grain", "give me your sword" };
                case "transfer_prisoner": return new List<string> { "release prisoner", "give me prisoner", "transfer prisoner" };
                case "transfer_workshop": return new List<string> { "give workshop", "transfer workshop" };
                case "hire_player_as_mercenary": return new List<string> { "hire me as mercenary", "hire player as mercenary" };
                case "offer_player_vassalage": return new List<string> { "make me your vassal", "offer vassalage" };
                case "join_kingdom": return new List<string> { "join my kingdom", "join kingdom" };
                case "leave_kingdom": return new List<string> { "leave kingdom" };
                case "hire_mercenary_clan": return new List<string> { "hire mercenary clan", "hire your clan" };
                case "recruit_and_recover": return new List<string> { "recruit", "recover", "gather troops" };
                case "form_army": return new List<string> { "form army", "raise an army", "assemble army" };
                case "attack_settlement": return new List<string> { "attack settlement", "attack town", "attack castle" };
                case "capture_settlement_plan": return new List<string> { "capture settlement", "take city", "take castle", "conquer" };
                case "start_ruling_clan_rebellion": return new List<string> { "rebellion", "overthrow ruler", "challenge ruling clan" };
                case "recruit_lord_to_rebellion": return new List<string> { "join my rebellion", "join our rebellion", "recruit lord to rebellion" };
                case "join_rebellion": return new List<string> { "join your rebellion", "fight for your rebellion", "join the rebels" };
                case "surrender_rebellion": return new List<string> { "surrender the rebellion", "yield the rebellion", "rebellion surrender" };
                case "consent_government_reduction": return new List<string> { "consent to reduce government power", "agree to lower government authority", "support taking one level of power from the council", "agree to reduce the senate's power" };
                case "install_ruling_clan": return new List<string> { "install ruling clan", "make them ruler", "replace ruling clan" };
                case "encourage_clan_defection": return new List<string> { "defect", "join our side", "switch sides" };
                case "exile_clan": return new List<string> { "exile clan", "banish clan" };
                case "restore_exiled_clan": return new List<string> { "restore exiled clan", "bring back clan" };
                case "mediate_clan_dispute": return new List<string> { "mediate dispute", "settle dispute" };
                default: return new List<string> { CanonicalCommand(command).Replace("_", " ") };
            }
        }

        private static Dictionary<string, object> TryRouteActionsWithLlm(Dictionary<string, object> settings, string campaignId, string correlationId, Dictionary<string, object> payload, Dictionary<string, object> hero, string routeText, List<Dictionary<string, object>> retrieved, string source, string reasonPrefix)
        {
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["candidates"] = new List<Dictionary<string, object>>(),
                ["errors"] = new List<string>()
            };
            if (retrieved == null || retrieved.Count == 0)
            {
                result["errors"] = new List<string> { "No router command candidates were retrieved." };
                return result;
            }

            List<Dictionary<string, object>> actionChoices = retrieved.Select(x => new Dictionary<string, object>
            {
                ["command"] = ReadString(x, "command", ""),
                ["family"] = ReadString(x, "family", ""),
                ["capability"] = ReadString(x, "capability", ""),
                ["required"] = x.ContainsKey("required") ? x["required"] : new ArrayList(),
                ["description"] = ReadString(x, "description", ""),
                ["aliases"] = x.ContainsKey("aliases") ? x["aliases"] : new ArrayList()
            }).ToList();

            string system = "You are Bannerlord Reign's silent action router. You do not roleplay. You read a conversation turn and return strict JSON only. Never invent internal game IDs. Use natural fields like TargetSettlement, TargetHero, TargetKingdom, FromHero, ToHero, GoldAmount, Item, Amount, or PrisonerHero. If no action is clearly finalized or commanded, return should_fire_action false.";
            string candidatePrefix = "CANDIDATE ACTIONS\n" + DialogueService.CanonicalActionCatalogJson(actionChoices)
                + "\n\nOUTPUT CONTRACT\nReturn JSON exactly like {\"should_fire_action\":true,\"action\":\"command_name\",\"confidence\":0.91,\"arguments\":{\"TargetSettlement\":\"Lycaron\"},\"missing_required_fields\":[]} or {\"should_fire_action\":false,\"reason\":\"not actionable\"}.";
            DialogueService.PromptEnvelope promptEnvelope = DialogueService.CreatePromptEnvelope("action_router", "candidate-set", system, candidatePrefix, "CONVERSATION TURN\n" + routeText);
            Dictionary<string, object> llm = LlmProviderService.ChatWithLlm(new Dictionary<string, object>
            {
                ["requestType"] = "action_router",
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId,
                ["heroStringId"] = ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId"),
                ["messages"] = promptEnvelope.Messages,
                ["promptEnvelope"] = promptEnvelope.Diagnostics,
                ["promptCacheEligible"] = true,
                ["temperature"] = 0.05d,
                ["maxTokens"] = ReadInt(settings, "actionRouterMaxTokens", 700),
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            });

            result["llm"] = llm;
            if (!ReadBool(llm, "ok", false))
            {
                result["errors"] = new List<string> { "Action router LLM failed: " + ReadString(llm, "error", "") };
                return result;
            }

            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", "")) ?? new Dictionary<string, object>();
            result["parsed"] = parsed;
            HashSet<string> allowed = new HashSet<string>(retrieved.Select(x => CanonicalCommand(ReadString(x, "command", ""))), StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> candidates = NormalizeRouterActions(parsed, allowed, payload, hero, routeText, string.IsNullOrWhiteSpace(source) ? "action_router_llm" : source + "_llm", reasonPrefix);
            result["ok"] = true;
            result["candidates"] = candidates;
            return result;
        }

        internal static List<Dictionary<string, object>> NormalizeRouterActions(Dictionary<string, object> parsed, HashSet<string> allowedCommands, Dictionary<string, object> payload, Dictionary<string, object> hero, string routeText, string source, string reasonPrefix)
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            if (parsed == null || parsed.Count == 0)
            {
                return results;
            }

            List<Dictionary<string, object>> rawActions = ReadDictionaryList(parsed, "actions");
            if (rawActions.Count == 0 && (ReadBool(parsed, "should_fire_action", ReadBool(parsed, "shouldFireAction", false)) || parsed.ContainsKey("action") || parsed.ContainsKey("command")))
            {
                rawActions.Add(parsed);
            }

            foreach (Dictionary<string, object> raw in rawActions)
            {
                Dictionary<string, object> action = FlattenActionFields(raw);
                string command = CanonicalCommand(ReadFirstString(action, "command", "action", "commandType", "intent", "type"));
                if (string.IsNullOrWhiteSpace(command) || (allowedCommands != null && allowedCommands.Count > 0 && !allowedCommands.Contains(command)))
                {
                    continue;
                }

                double confidence = ReadDouble(action, "confidence", 1d);
                if (confidence < 0.25d)
                {
                    continue;
                }

                Dictionary<string, object> copy = new Dictionary<string, object>(action, StringComparer.OrdinalIgnoreCase)
                {
                    ["command"] = command,
                    ["source"] = string.IsNullOrWhiteSpace(source) ? "action_router_llm" : source,
                    ["requiresAcceptance"] = false
                };
                if (string.IsNullOrWhiteSpace(ReadString(copy, "reason", "")))
                {
                    copy["reason"] = (string.IsNullOrWhiteSpace(reasonPrefix) ? "Action router" : reasonPrefix) + ": " + DialogueService.LimitText(routeText, 700);
                }

                ApplyRouterArguments(copy, ReadDictionary(action, "arguments") ?? ReadDictionary(action, "args") ?? new Dictionary<string, object>(), command);
                DialogueService.CompleteConversationActionTerms(copy, payload, hero, routeText);
                copy["capability"] = ActionCapabilityForCommand(command);
                results.Add(copy);
            }

            return results;
        }

        private static void ApplyRouterArguments(Dictionary<string, object> action, Dictionary<string, object> arguments, string command)
        {
            foreach (KeyValuePair<string, object> pair in arguments ?? new Dictionary<string, object>())
            {
                string key = NormalizeLookup(pair.Key);
                // FlattenActionFields already retains canonical fields. Do not reinterpret
                // metadata or explicit actor/target IDs as vague natural-language aliases.
                if (WorldSimulationService.IsCanonicalRouterArgument(key)) continue;
                string targetKey = RouterArgumentTargetKey(key, command);
                if (!string.IsNullOrWhiteSpace(targetKey) && (!action.ContainsKey(targetKey) || action[targetKey] == null || string.IsNullOrWhiteSpace(Convert.ToString(action[targetKey]))))
                {
                    action[targetKey] = pair.Value;
                }
            }
        }

        internal static string RouterArgumentTargetKey(string normalizedKey, string command)
        {
            if (WorldSimulationService.IsCanonicalRouterArgument(normalizedKey)) return "";
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                return "";
            }

            if (normalizedKey.Contains("settlement") || normalizedKey.Contains("town") || normalizedKey.Contains("city") || normalizedKey.Contains("castle") || normalizedKey.Contains("village") || normalizedKey.Contains("fief"))
            {
                return "TargetSettlement";
            }

            if (normalizedKey.Contains("kingdom") || normalizedKey.Contains("faction"))
            {
                return "TargetKingdom";
            }

            if (normalizedKey.Contains("clan"))
            {
                return "TargetClan";
            }

            if (normalizedKey.Contains("from") || normalizedKey.Contains("giver")
                || normalizedKey == "source hero" || normalizedKey == "sourcehero")
            {
                return "FromHero";
            }

            if (normalizedKey.Contains("to") || normalizedKey.Contains("recipient") || normalizedKey.Contains("receiver"))
            {
                return CanonicalCommand(command).StartsWith("transfer_", StringComparison.OrdinalIgnoreCase) ? "ToHero" : "TargetHero";
            }

            if (normalizedKey.Contains("killer"))
            {
                return "KillerHero";
            }

            if (normalizedKey.Contains("hero") || normalizedKey.Contains("lord") || normalizedKey.Contains("character") || normalizedKey.Contains("npc") || normalizedKey == "target")
            {
                return CanInferSettlementFromText(command) ? "TargetSettlement" : "TargetHero";
            }

            if (normalizedKey.Contains("gold") || normalizedKey.Contains("denar") || normalizedKey.Contains("money") || normalizedKey.Contains("price"))
            {
                return "GoldAmount";
            }

            if (normalizedKey.Contains("item") || normalizedKey.Contains("asset") || normalizedKey.Contains("goods") || normalizedKey.Contains("armor") || normalizedKey.Contains("armour"))
            {
                return "Item";
            }

            if (normalizedKey.Contains("amount") || normalizedKey.Contains("count") || normalizedKey.Contains("quantity"))
            {
                return "Amount";
            }

            if (normalizedKey.Contains("workshop"))
            {
                return "Workshop";
            }

            if (normalizedKey.Contains("prisoner"))
            {
                return "PrisonerHero";
            }

            return "";
        }

        internal static void AppendUniqueActionCandidate(List<Dictionary<string, object>> candidates, Dictionary<string, object> candidate)
        {
            if (candidate == null || candidate.Count == 0)
            {
                return;
            }

            string key = ActionCandidateKey(candidate);
            if (!candidates.Any(x => string.Equals(ActionCandidateKey(x), key, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(candidate);
            }
        }

        private static string ActionCandidateKey(Dictionary<string, object> candidate)
        {
            string command = CanonicalCommand(ReadFirstString(candidate, "command", "action", "commandType", "intent", "type"));
            string target = ReadFirstString(candidate, "TargetSettlement", "targetSettlementStringId", "TargetHero", "targetHeroStringId", "TargetKingdom", "targetKingdomStringId", "Item", "GoldAmount");
            return command + "|" + NormalizeLookup(target);
        }

        private static Dictionary<string, object> TryRepairActionCandidate(string campaignId, Dictionary<string, object> payload, string resolutionText, Dictionary<string, object> candidate, List<string> errors)
        {
            Dictionary<string, object> settings = SettingsService.LoadSettings();
            if (!ReadBool(settings, "enableActionRouterRepair", true) || !LlmProviderService.LlmProviderConfigured(settings))
            {
                return null;
            }

            string source = ReadString(candidate, "source", "");
            if (source.IndexOf("router", StringComparison.OrdinalIgnoreCase) < 0
                && source.IndexOf("planner", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            string command = CanonicalCommand(ReadFirstString(candidate, "command", "action", "commandType", "intent", "type"));
            if (string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            string system = "You repair one Bannerlord Reign action JSON object. Return strict JSON only. Do not invent internal IDs. Use natural fields if needed. Keep the same intended action unless the error proves it is the wrong command.";
            string definitionPrefix = "ACTION DEFINITION\n" + CanonicalJson(RetrieveActionRouterCommands(command.Replace("_", " "), new List<string>(), 5))
                + "\n\nOUTPUT CONTRACT\nReturn {\"action\":{\"command\":\"" + command + "\",\"arguments\":{...},\"reason\":\"...\"}}.";
            string liveRepair = "CONVERSATION/ACTION TEXT\n" + resolutionText
                + "\n\nORIGINAL ACTION\n" + Json.Serialize(candidate)
                + "\n\nVALIDATION ERRORS\n" + Json.Serialize(errors ?? new List<string>());
            DialogueService.PromptEnvelope promptEnvelope = DialogueService.CreatePromptEnvelope("action_repair", command, system, definitionPrefix, liveRepair);
            Dictionary<string, object> llm = LlmProviderService.ChatWithLlm(new Dictionary<string, object>
            {
                ["requestType"] = "action_repair",
                ["campaignId"] = campaignId,
                ["correlationId"] = OperationsService.EnsureCorrelationId(payload),
                ["heroStringId"] = ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId"),
                ["messages"] = promptEnvelope.Messages,
                ["promptEnvelope"] = promptEnvelope.Diagnostics,
                ["promptCacheEligible"] = true,
                ["temperature"] = 0.05d,
                ["maxTokens"] = ReadInt(settings, "actionRouterMaxTokens", 700),
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            });

            if (!ReadBool(llm, "ok", false))
            {
                return null;
            }

            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", "")) ?? new Dictionary<string, object>();
            Dictionary<string, object> repaired = ReadDictionary(parsed, "action") ?? parsed;
            if (repaired == null || repaired.Count == 0)
            {
                return null;
            }

            HashSet<string> allowed = new HashSet<string>(new[] { command }, StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> normalized = NormalizeRouterActions(repaired, allowed, payload, ReadDictionary(payload, "hero") ?? new Dictionary<string, object>(), resolutionText, "action_router_repair", "Action router repair");
            Dictionary<string, object> first = normalized.FirstOrDefault();
            if (first != null)
            {
                first["repairOf"] = candidate;
            }

            return first;
        }

        internal static string ClassifyActionFailure(int candidateCount, List<string> errors)
        {
            string joined = string.Join(" | ", errors ?? new List<string>()).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(joined))
            {
                return candidateCount <= 0 ? "no_action_detected" : "completed_no_effect";
            }

            if (joined.Contains("unsupported action command") || joined.Contains("no deterministic action mapping"))
            {
                return "bad_command_mapping";
            }

            if (joined.Contains("required") || joined.Contains("must be positive") || joined.Contains("is missing"))
            {
                return "missing_required_field";
            }

            if (joined.Contains("could not resolve") || joined.Contains("ambiguous") || joined.Contains("not_found"))
            {
                return "resolver_failed";
            }

            if (joined.Contains("validation") || joined.Contains("illegal") || joined.Contains("not allowed"))
            {
                return "validation_failed";
            }

            if (joined.Contains("executor") || joined.Contains("failed to execute"))
            {
                return "executor_failed";
            }

            return "validation_failed";
        }

        internal static string BuildActionShadowPreview(List<Dictionary<string, object>> queuedTestDirectiveActions, List<Dictionary<string, object>> queuedDialogueActions, List<string> testDirectiveErrors, List<string> dialogueActionErrors)
        {
            Dictionary<string, object> queued = (queuedTestDirectiveActions ?? new List<Dictionary<string, object>>()).Concat(queuedDialogueActions ?? new List<Dictionary<string, object>>()).FirstOrDefault();
            if (queued != null)
            {
                Dictionary<string, object> record = ReadDictionary(queued, "record") ?? queued;
                string command = ReadString(record, "command", "");
                string from = ReadFirstString(record, "actorHeroStringId", "actorKingdomStringId", "actorClanStringId");
                string to = ReadFirstString(record, "targetHeroStringId", "targetKingdomStringId", "targetClanStringId", "targetSettlementStringId");
                return "Detected action: " + command + " | capability: " + ActionCapabilityForCommand(command) + " | resolved: " + (string.IsNullOrWhiteSpace(from) ? "<none>" : from) + " -> " + (string.IsNullOrWhiteSpace(to) ? "<none>" : to) + " | status: queued";
            }

            List<string> errors = new List<string>();
            errors.AddRange(testDirectiveErrors ?? new List<string>());
            errors.AddRange(dialogueActionErrors ?? new List<string>());
            if (errors.Count == 0)
            {
                return "";
            }

            return "Detected action: none | status: " + ClassifyActionFailure(1, errors) + ": " + errors.First();
        }

        private static bool ShouldAutoQueueDialogueAction(Dictionary<string, object> action, string playerText, string visibleReply, out string reason)
        {
            reason = "not enough acceptance evidence";
            string command = ReadString(action, "command", "");
            string player = (playerText ?? string.Empty).ToLowerInvariant();
            string reply = (visibleReply ?? string.Empty).ToLowerInvariant();
            bool explicitCommit = !ReadBool(action, "requiresAcceptance", true)
                || ReadBool(action, "accepted", false)
                || ReadBool(action, "committed", false)
                || ReadBool(action, "executeNow", false)
                || ReadBool(action, "npcAccepted", false);

            if (ContainsAnyPhrase(reply, DialogueActionBlockingPhrases()))
            {
                reason = "visible reply still blocks, bargains, refuses, or asks for more proof";
                return false;
            }

            if (explicitCommit)
            {
                reason = "model marked action as accepted";
                return true;
            }

            if (ContainsAnyPhrase(reply, DialogueActionAcceptancePhrases()))
            {
                reason = "NPC reply commits to the agreement";
                return true;
            }

            if (IsTreatyOrDiplomacyCommand(command) && ContainsAnyPhrase(player, PlayerCommitmentPhrases()))
            {
                reason = "player explicitly finalized a diplomatic agreement and NPC did not block it";
                return true;
            }

            return false;
        }

        private static bool IsTreatyOrDiplomacyCommand(string command)
        {
            switch (CanonicalCommand(command))
            {
                case "sign_trade_agreement":
                case "sign_non_aggression_pact":
                case "sign_alliance":
                case "sign_defensive_pact":
                case "make_peace":
                case "offer_tribute_peace":
                case "demand_reparations_peace":
                case "demand_settlement_peace":
                case "demand_surrender_peace":
                case "exchange_prisoners":
                case "ransom_package":
                case "hostage_guarantee":
                case "war_indemnity":
                case "recognize_conquest":
                case "return_occupied_settlement":
                case "demilitarized_border":
                case "supply_agreement":
                case "loan_or_subsidy":
                case "pay_to_stay_neutral":
                case "pay_to_join_war":
                case "guarantee_independence":
                case "protectorate_or_vassalage":
                case "record_promise":
                    return true;
                default:
                    return false;
            }
        }

        internal static bool ContainsAnyPhrase(string text, IEnumerable<string> phrases)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            foreach (string phrase in phrases ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(phrase) && text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> DialogueActionBlockingPhrases()
        {
            return new[]
            {
                "i refuse",
                "i decline",
                "i do not agree",
                "i don't agree",
                "i will not",
                "i won't",
                "not yet",
                "not agree",
                "not agreed",
                "no pact",
                "no agreement",
                "convince me",
                "prove",
                "tell me what",
                "tell me plainly",
                "before i agree",
                "before agreeing",
                "before i commit",
                "before committing",
                "i will consider",
                "i shall consider",
                "perhaps",
                "maybe",
                "what goods",
                "what tariffs",
                "what security",
                "what do you offer",
                "what flows",
                "would hear what",
                "i do not refuse"
            };
        }

        internal static IEnumerable<string> DialogueActionAcceptancePhrases()
        {
            return new[]
            {
                "i agree",
                "we are agreed",
                "agreed.",
                "i accept",
                "accepted.",
                "i consent",
                "let it be done",
                "so be it",
                "done.",
                "you have my word",
                "you have my seal",
                "my seal",
                "i will sign",
                "i shall sign",
                "i sign",
                "we have a pact",
                "we have an agreement",
                "the pact stands",
                "let the pact stand",
                "let this pact stand",
                "we shall not raise arms",
                "we will not raise arms",
                "i will not raise arms",
                "i shall not raise arms",
                "we will keep peace",
                "we shall keep peace",
                "let our kingdoms be bound"
            };
        }

        private static IEnumerable<string> PlayerCommitmentPhrases()
        {
            return new[]
            {
                "i agree",
                "i accept",
                "i sign",
                "i will sign",
                "i shall sign",
                "we are agreed",
                "let us sign",
                "you have my word",
                "i swear",
                "i pledge",
                "i give my oath",
                "i seal",
                "i will ensure",
                "i consent"
            };
        }
    }
}
