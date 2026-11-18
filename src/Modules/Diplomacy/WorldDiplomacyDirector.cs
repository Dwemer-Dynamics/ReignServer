using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Reign.Core.Contracts.Diplomacy;

namespace ReignBetaServer
{
    internal static partial class DiplomacyService
    {
        private const int DirectorCampaignDaysPerYear = 126;
        private const int DirectorEvaluationIntervalDays = 3;
        private const double AutonomousWorldStartupGraceDays = 5d;
        private const double DirectorOpposedBaseline = 0.005d;
        private const double DirectorNeutralBaseline = 0.015d;
        private const double DirectorAlignedBaseline = 0.03d;
        private const double DirectorOpportunityCeiling = 0.06d;
        private const double DirectorPeaceCeiling = 0.12d;
        private static readonly string[] DirectorCourtVirtueKeys =
        {
            "compassion", "boldness", "honor", "loyalty", "responsibility", "courage", "judgment"
        };
        internal static readonly HashSet<string> DirectorSupportedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "declare_war", "make_peace", "offer_tribute_peace", "record_promise", "demand_reparations_peace",
            "demand_settlement_peace", "demand_surrender_peace", "sign_trade_agreement", "sign_non_aggression_pact",
            "sign_alliance", "sign_defensive_pact", "break_treaty", "exchange_prisoners", "ransom_package",
            "hostage_guarantee", "war_indemnity", "recognize_conquest", "return_occupied_settlement",
            "demilitarized_border", "caravan_protection_agreement", "supply_agreement", "loan_or_subsidy", "pay_to_stay_neutral",
            "pay_to_join_war", "guarantee_independence", "protectorate_or_vassalage", "diplomatic_package"
        };

        private static readonly HashSet<string> DirectorUnilateralCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "declare_war", "break_treaty"
        };

        private static readonly Dictionary<string, HashSet<string>> DirectorIntentCommands = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["expansion"] = new HashSet<string>(new[] { "declare_war", "break_treaty", "demand_reparations_peace", "demand_settlement_peace", "demand_surrender_peace", "war_indemnity", "pay_to_join_war", "protectorate_or_vassalage", "diplomatic_package" }, StringComparer.OrdinalIgnoreCase),
            ["prosperity"] = new HashSet<string>(new[] { "sign_trade_agreement", "caravan_protection_agreement", "supply_agreement", "loan_or_subsidy", "pay_to_stay_neutral", "ransom_package", "diplomatic_package" }, StringComparer.OrdinalIgnoreCase),
            ["security"] = new HashSet<string>(new[] { "record_promise", "sign_non_aggression_pact", "sign_alliance", "sign_defensive_pact", "hostage_guarantee", "guarantee_independence", "diplomatic_package" }, StringComparer.OrdinalIgnoreCase),
            ["peace"] = new HashSet<string>(new[] { "make_peace", "offer_tribute_peace", "demand_reparations_peace", "demand_settlement_peace", "demand_surrender_peace", "exchange_prisoners", "ransom_package", "war_indemnity", "recognize_conquest", "return_occupied_settlement", "demilitarized_border", "diplomatic_package" }, StringComparer.OrdinalIgnoreCase)
        };

        internal static Dictionary<string, object> EvaluateWorldDiplomacy(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            int day = (int)Math.Floor(worldDay);
            Dictionary<string, object> startupGrace = BuildAutonomousWorldStartupGrace(payload);
            if (ReadBool(startupGrace, "locked", false))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = "startup_grace_period",
                    ["worldDay"] = worldDay,
                    ["startupGrace"] = startupGrace
                };
            }
            bool retryFailedEvaluation = ReadBool(payload, "retryFailedEvaluation", false);
            bool debugInitiativeTest = ReadBool(payload, "debugInitiativeTest", false);
            string debugCommandId = debugInitiativeTest ? ReadFirstString(payload, "commandId", "debugCommandId") : string.Empty;
            string playerKingdomId = ReadString(payload, "playerKingdomId", "");
            List<Dictionary<string, object>> kingdoms = ReadDictionaryList(payload, "kingdoms");
            IngestNativeDiplomacyRelationshipState(payload);
            if (kingdoms.Count < 2)
            {
                return DirectorError("World diplomacy requires at least two live kingdoms in the snapshot.");
            }
            AttachDirectorRulerAttitudes(campaignId, timelineId, kingdoms);

            Dictionary<string, object> settings = Program.LoadSettings();
            string diplomacyApiUrl = Program.LlmApiUrl(settings);
            bool localDiplomacyEndpoint = diplomacyApiUrl.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0
                || diplomacyApiUrl.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0;
            if (string.IsNullOrWhiteSpace(diplomacyApiUrl) || string.IsNullOrWhiteSpace(Program.ModelForRequest(settings, "diplomacy")))
            {
                return DirectorError("The diplomacy AI route is not configured. Check the API URL and Diplomacy Model in the Reign server control center.");
            }
            if (!Program.UsesCodexSubscription(settings) && !localDiplomacyEndpoint && !Program.ProviderKeyConfigured(settings, Program.LlmApiKeySlot(settings)))
            {
                return DirectorError("The main LLM API key is not configured. Open the Reign Control Center, save the key under Server Diagnostics, then run Test LLM Key.");
            }

            Dictionary<string, object> state;
            lock (Program.FileLock)
            {
                state = ReadDirectorState(campaignId);
                int lastEvaluatedDay = ReadInt(state, "lastEvaluatedDay", int.MinValue);
                if (!debugInitiativeTest && lastEvaluatedDay == day && !retryFailedEvaluation)
                {
                    return new Dictionary<string, object> { ["ok"] = true, ["status"] = "already_evaluated", ["worldDay"] = worldDay };
                }

                if (!debugInitiativeTest) state["lastEvaluatedDay"] = day;
                UpdateDirectorWarStates(state, payload, worldDay);
                WriteDirectorState(campaignId, state);
            }
            payload["recentDiplomaticEvents"] = ReadDiplomaticEventQueue(campaignId)
                .OrderByDescending(x => ReadDouble(x, "worldDay", 0d))
                .Take(30)
                .Select(x => (object)PublicDirectorEventMemory(x))
                .ToList();
            payload["nationalPowerAssessments"] = BuildNationalPowerAssessments(payload, state);

            if (!debugInitiativeTest && ReadInt(state, "lastResolvedDay", int.MinValue) == day)
            {
                return new Dictionary<string, object> { ["ok"] = true, ["status"] = "daily_event_cap_reached", ["worldDay"] = worldDay };
            }

            Dictionary<string, object> relationshipSelected =
                !debugInitiativeTest && !ReadBool(payload, "skipRelationshipOpportunity", false)
                    ? SelectRulerDiplomacyOpportunity(campaignId, timelineId,
                        worldDay, kingdoms, state)
                    : null;

            int lastInitiativeEvaluationDay = ReadInt(state, "lastInitiativeEvaluationDay", int.MinValue);
            bool normalEvaluationDue = debugInitiativeTest || retryFailedEvaluation
                || lastInitiativeEvaluationDay == int.MinValue
                || day - lastInitiativeEvaluationDay >= DirectorEvaluationIntervalDays;
            Dictionary<string, object> politicalPressureSelected =
                relationshipSelected == null && !debugInitiativeTest && normalEvaluationDue
                    ? SelectPoliticalPressureOpportunity(campaignId, timelineId,
                        worldDay, kingdoms, state)
                    : null;
            if (relationshipSelected == null && politicalPressureSelected == null
                && !debugInitiativeTest && !retryFailedEvaluation && !normalEvaluationDue)
            {
                return new Dictionary<string, object> { ["ok"] = true, ["status"] = "waiting_for_evaluation_window", ["worldDay"] = worldDay };
            }
            if (!debugInitiativeTest && relationshipSelected == null)
            {
                state["lastInitiativeEvaluationDay"] = day;
                SaveDirectorState(campaignId, state);
            }

            List<Dictionary<string, object>> eligibleKingdoms = kingdoms
                .Where(x => !ReadBool(x, "isPlayerKingdom", false))
                .Where(x => !string.Equals(ReadString(x, "kingdomId", ""), playerKingdomId, StringComparison.OrdinalIgnoreCase))
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "leaderHeroId", "")))
                .ToList();
            if (debugInitiativeTest)
            {
                string selectedRulerId = ReadString(payload, "debugSelectedRulerId", "");
                eligibleKingdoms = eligibleKingdoms
                    .Where(x => string.Equals(ReadString(x, "leaderHeroId", ""), selectedRulerId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            List<Dictionary<string, object>> initiativeAttempts;
            Dictionary<string, object> selected;
            if (relationshipSelected != null)
            {
                selected = relationshipSelected;
                initiativeAttempts = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["source"] = "ruler_relationship_breakthrough",
                        ["opportunityId"] = ReadString(selected, "relationshipOpportunityId", ""),
                        ["polarity"] = ReadString(selected, "relationshipPolarity", ""),
                        ["threshold"] = ReadInt(selected, "relationshipThreshold", 0),
                        ["passed"] = true
                    }
                };
            }
            else if (politicalPressureSelected != null)
            {
                selected = politicalPressureSelected;
                initiativeAttempts = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["source"] = "political_pressure",
                        ["directionId"] = ReadString(selected,
                            "politicalPressureId", ""),
                        ["polarity"] = ReadString(selected,
                            "politicalPressurePolarity", ""),
                        ["pressure"] = ReadInt(selected,
                            "politicalPressureValue", 0),
                        ["channel"] = ReadString(selected,
                            "politicalPressureChannel", ""),
                        ["chance"] = ReadDouble(selected, "finalChance", 0d),
                        ["roll"] = ReadDouble(selected, "roll", 0d),
                        ["passed"] = true
                    }
                };
            }
            else
            {
                selected = SelectDirectorRuler(campaignId, day, worldDay,
                    eligibleKingdoms, payload, state,
                    debugInitiativeTest ? debugCommandId : string.Empty,
                    out initiativeAttempts);
            }
            if (selected == null)
            {
                Dictionary<string, object> noInitiative = new Dictionary<string, object> { ["ok"] = true, ["status"] = debugInitiativeTest ? "no_initiative" : "no_ruler_eligible", ["worldDay"] = worldDay };
                if (debugInitiativeTest)
                {
                    Dictionary<string, object> testedRuler = eligibleKingdoms.FirstOrDefault();
                    noInitiative["debugInitiativeTest"] = true;
                    noInitiative["idempotent"] = false;
                    noInitiative["actorKingdomName"] = ReadString(testedRuler, "name", "Unknown realm");
                    noInitiative["rulerName"] = ReadString(testedRuler, "leaderName", "Unknown ruler");
                    noInitiative["initiativeAttempts"] = initiativeAttempts;
                }
                WorldSimulationService.RecordWorldTestDiplomacyEvaluation(campaignId, timelineId, day,
                    ReadString(noInitiative, "status", "no_ruler_eligible"),
                    initiativeAttempts.Count, false, false);
                return noInitiative;
            }

            Dictionary<string, object> ruler = ReadDictionary(selected, "ruler") ?? new Dictionary<string, object>();
            string mode = ReadString(selected, "mode", "expansion");
            string fixedTargetId = ReadString(selected, "targetKingdomId", "");
            string relationshipOpportunityId = ReadString(selected,
                "relationshipOpportunityId", "");
            string relationshipPolarity = ReadString(selected,
                "relationshipPolarity", "");
            string politicalPressureId = ReadString(selected,
                "politicalPressureId", "");
            string politicalPressurePolarity = ReadString(selected,
                "politicalPressurePolarity", "");
            if (!string.IsNullOrWhiteSpace(relationshipOpportunityId))
            {
                payload["requiredRelationshipPolarity"] = relationshipPolarity;
                payload["relationshipOpportunityId"] = relationshipOpportunityId;
            }
            if (!string.IsNullOrWhiteSpace(politicalPressureId))
            {
                payload["requiredPoliticalPressurePolarity"] = politicalPressurePolarity;
                payload["politicalPressureId"] = politicalPressureId;
                payload["politicalPressureValue"] = ReadInt(selected,
                    "politicalPressureValue", 0);
                payload["politicalPressureChannel"] = ReadString(selected,
                    "politicalPressureChannel", "");
            }
            Dictionary<string, object> rulerState = GetDirectorRulerState(state, ruler, worldDay);
            Dictionary<string, object> traits = LoadDirectorTraits(campaignId, ruler);
            Dictionary<string, object> decision = AskRulerForDiplomaticDecision(campaignId, payload, ruler, traits, rulerState, mode, fixedTargetId);
            if (!ReadBool(decision, "ok", false))
            {
                WorldSimulationService.RecordWorldTestDiplomacyEvaluation(campaignId, timelineId, day,
                    "model_failure", initiativeAttempts.Count, true, true,
                    new Dictionary<string, object> { ["intent"] = mode });
                return DirectorError(ReadString(decision, "error", "The diplomacy model did not return a usable decision."));
            }

            Dictionary<string, object> parsed = ReadDictionary(decision, "decision") ?? new Dictionary<string, object>();
            UpdateDirectorAgenda(rulerState, parsed, worldDay);
            string decisionKind = ReadString(parsed, "decision", "none").Trim().ToLowerInvariant();
            if (decisionKind == "none")
            {
                if (!string.IsNullOrWhiteSpace(relationshipOpportunityId))
                {
                    FinishRelationshipOpportunity(campaignId, timelineId,
                        relationshipOpportunityId, worldDay, "consumed",
                        "no_credible_action", "");
                }
                if (!string.IsNullOrWhiteSpace(politicalPressureId))
                {
                    Dictionary<string, object> pressureTarget = FindDirectorKingdom(
                        payload, fixedTargetId);
                    RecordPoliticalPressureNoAction(campaignId, timelineId,
                        worldDay, ruler, pressureTarget, politicalPressureId,
                        "The ruler found no credible action matching the court's political pressure.");
                }
                rulerState["lastConsideredDay"] = worldDay;
                SaveDirectorState(campaignId, state);
                WorldSimulationService.RecordWorldTestDiplomacyEvaluation(campaignId, timelineId, day,
                    "ruler_chose_no_action", initiativeAttempts.Count, true, false,
                    new Dictionary<string, object>
                    {
                        ["intent"] = mode,
                        ["actorKingdom"] = ReadString(ruler, "kingdomId", ReadString(ruler, "name", ""))
                    });
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = "ruler_chose_no_action",
                    ["rulerId"] = ReadString(ruler, "leaderHeroId", ""),
                    ["rulerName"] = ReadString(ruler, "leaderName", ""),
                    ["actorKingdomName"] = ReadString(ruler, "name", ""),
                    ["debugInitiativeTest"] = debugInitiativeTest,
                    ["initiativeAttempts"] = initiativeAttempts
                };
            }

            Dictionary<string, object> candidate = BuildDirectorCandidate(parsed, ruler);
            candidate["timelineId"] = timelineId;
            candidate["intentFamily"] = mode;
            if (!string.IsNullOrWhiteSpace(relationshipOpportunityId))
            {
                candidate["relationshipOpportunityId"] = relationshipOpportunityId;
                candidate["requiredPolarity"] = relationshipPolarity;
                candidate["relationshipThreshold"] = ReadInt(selected,
                    "relationshipThreshold", 0);
            }
            if (!string.IsNullOrWhiteSpace(politicalPressureId))
            {
                candidate["politicalPressureId"] = politicalPressureId;
                candidate["politicalPressureValue"] = ReadInt(selected,
                    "politicalPressureValue", 0);
                candidate["politicalPressureChannel"] = ReadString(selected,
                    "politicalPressureChannel", "");
                candidate["requiredPolarity"] = politicalPressurePolarity;
            }
            candidate["initiativeDiagnostics"] = new Dictionary<string, object>
            {
                ["family"] = mode,
                ["traitGroupScore"] = ReadDouble(selected, "traitGroupScore", 50d),
                ["traitGroupInputs"] = ReadDictionary(selected, "traitGroupInputs") ?? new Dictionary<string, object>(),
                ["personalityBaseline"] = ReadDouble(selected, "personalityBaseline", 0d),
                ["opportunity"] = ReadDouble(selected, "opportunity", 0d),
                ["finalChance"] = ReadDouble(selected, "finalChance", 0d),
                ["roll"] = ReadDouble(selected, "roll", 0d),
                ["priority"] = ReadDouble(selected, "priority", 0d)
            };
            List<string> errors = ValidateDirectorCandidate(candidate, payload, playerKingdomId, mode, fixedTargetId);
            if (ShouldFallbackPoliticalPressurePeace(candidate, mode, errors))
            {
                candidate = BuildPoliticalPressurePeaceFallback(candidate, ruler,
                    FindDirectorKingdom(payload, fixedTargetId));
                errors = ValidateDirectorCandidate(candidate, payload,
                    playerKingdomId, mode, fixedTargetId);
            }
            string proposedTargetId = ReadFirstString(candidate, "targetKingdomId", "targetKingdomStringId");
            Dictionary<string, object> pairCooldowns = ReadDictionary(rulerState, "pairCooldowns") ?? new Dictionary<string, object>();
            if (ReadDouble(pairCooldowns, proposedTargetId, 0d) > worldDay)
            {
                errors.Add("the ruler-target pair is still on diplomatic cooldown");
            }
            if (errors.Count > 0)
            {
                WriteDirectorAudit(campaignId, "candidate_rejected", ruler, candidate, errors);
                if (!string.IsNullOrWhiteSpace(relationshipOpportunityId))
                {
                    FinishRelationshipOpportunity(campaignId, timelineId,
                        relationshipOpportunityId, worldDay, "consumed",
                        "no_credible_action", "");
                }
                if (!string.IsNullOrWhiteSpace(politicalPressureId))
                {
                    Dictionary<string, object> pressureTarget = FindDirectorKingdom(
                        payload, fixedTargetId);
                    RecordPoliticalPressureNoAction(campaignId, timelineId,
                        worldDay, ruler, pressureTarget, politicalPressureId,
                        "No executable action matched the pressure and current strategic constraints.");
                }
                return DirectorRejected("invalid_candidate_ignored", errors);
            }

            string command = ReadString(candidate, "command", "");
            Dictionary<string, object> target = FindDirectorKingdom(payload, ReadString(candidate, "targetKingdomId", ""));
            ApplyDirectorTargetIdentity(candidate, target);
            candidate["actorEffectiveAttitude"] =
                FindDirectorRulerAttitude(ruler, target);
            candidate["targetEffectiveAttitude"] =
                FindDirectorRulerAttitude(target, ruler);
            bool targetIsPlayer = ReadBool(target, "isPlayerKingdom", false)
                || string.Equals(ReadString(target, "kingdomId", ""), playerKingdomId, StringComparison.OrdinalIgnoreCase);
            bool accepted = DirectorUnilateralCommands.Contains(command);
            string targetReason = string.Empty;
            Dictionary<string, object> finalCandidate = candidate;
            string negotiationOutcome = accepted ? "unilateral" : "refused";

#if REIGN_EXCLUDE_COURT
            if (!DirectorUnilateralCommands.Contains(command) && targetIsPlayer)
            {
                return DirectorRejected("player_target_excluded_without_court", new List<string> { "Player-kingdom diplomacy proposals require the excluded Court system." });
            }
#else
            if (!DirectorUnilateralCommands.Contains(command) && targetIsPlayer)
            {
                Dictionary<string, object> normalizationPayload = BuildDirectorNormalizationPayload(payload, ruler);
                Dictionary<string, object> pendingAction = Program.NormalizeActionCommand(candidate, campaignId, out errors, normalizationPayload, ReadString(candidate, "publicReason", ""));
                if (pendingAction == null || errors.Count > 0)
                {
                    WriteDirectorAudit(campaignId, "player_proposal_normalization_failed", ruler, candidate, errors);
                    return DirectorValidationRejected("player_proposal_normalization_failed", errors);
                }
                pendingAction["source"] = "player_court_diplomacy_proposal";
                pendingAction["requiresAcceptance"] = false;
                string matterId = QueuePlayerDiplomacyCourtMatter(campaignId, payload, ruler, target, candidate, pendingAction);
                ApplyDirectorCooldowns(state, rulerState, candidate, false, worldDay);
                state["lastResolvedDay"] = day;
                SaveDirectorState(campaignId, state);
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = "proposal_queued_for_player_court",
                    ["matterId"] = matterId,
                    ["accepted"] = false,
                    ["outcome"] = "awaiting_player"
                };
            }
#endif

            if (!DirectorUnilateralCommands.Contains(command))
            {
                Dictionary<string, object> response = AskRulerToRespond(
                    campaignId, payload, ruler, target, candidate, mode, false);
                if (!ReadBool(response, "ok", false))
                {
                    return DirectorError(ReadString(response, "error", "The responding diplomacy model failed."));
                }

                Dictionary<string, object> answer = ReadDictionary(response, "response") ?? new Dictionary<string, object>();
                string answerKind = ReadString(answer, "response", "refuse").Trim().ToLowerInvariant();
                targetReason = RequirePublicReason(answer, ReadString(target, "leaderName", "The responding ruler") + " declined without offering a public explanation.");
                if (answerKind != "counter")
                {
                    string reportedAnswerKind = answerKind;
                    accepted = ResolveAllianceAcceptance(campaignId, worldDay, payload, ruler, target, candidate, answer, false, out Dictionary<string, object> acceptanceAudit);
                    AddAllianceAcceptanceAudit(candidate, acceptanceAudit);
                    answerKind = accepted ? "accept" : "refuse";
                    negotiationOutcome = accepted ? "accepted" : "deterministic_willingness_failed";
                    targetReason = SelectDiplomaticOutcomeReason(answer, reportedAnswerKind,
                        accepted,
                        ReadString(target, "leaderName", "The responding ruler") + " accepted after weighing the relationship, strategy, and executable terms.",
                        IsDirectorAllianceProposal(candidate)
                            ? "The balance of power and the offered terms did not justify an alliance."
                            : ReadString(target, "leaderName", "The responding ruler") + " declined after the final willingness roll did not support an agreement.");
                }
                if (answerKind == "counter")
                {
                    finalCandidate = ApplyCounteroffer(candidate, answer);
                    errors = ValidateDirectorCandidate(finalCandidate, payload, playerKingdomId, mode, fixedTargetId);
                    if (errors.Count > 0)
                    {
                        accepted = false;
                        negotiationOutcome = "counteroffer_invalid";
                        targetReason = InvalidCounterofferPublicReason(
                            finalCandidate, errors);
                    }
                    else
                    {
                        Dictionary<string, object> finalResponse =
                            AskRulerToRespond(campaignId, payload, target,
                                ruler, finalCandidate, mode, true);
                        if (!ReadBool(finalResponse, "ok", false))
                        {
                            return DirectorError(ReadString(finalResponse,
                                "error", "The initiating ruler could not answer the counteroffer."));
                        }

                        Dictionary<string, object> finalAnswer =
                            ReadDictionary(finalResponse, "response")
                                ?? new Dictionary<string, object>();
                        string reportedFinalAnswerKind = ReadString(finalAnswer,
                            "response", "refuse").Trim().ToLowerInvariant();
                        accepted = ResolveAllianceAcceptance(campaignId,
                            worldDay, payload, target, ruler,
                            finalCandidate, finalAnswer, true,
                            out Dictionary<string, object> acceptanceAudit);
                        AddAllianceAcceptanceAudit(finalCandidate,
                            acceptanceAudit);
                        negotiationOutcome = accepted
                            ? "counteroffer_accepted" : "counteroffer_refused";
                        finalCandidate["publicReason"] =
                            SelectDiplomaticOutcomeReason(finalAnswer,
                                reportedFinalAnswerKind, accepted,
                                ReadString(ruler, "leaderName",
                                    "The proposing ruler")
                                    + " accepted the revised terms.",
                                ReadString(ruler, "leaderName",
                                    "The proposing ruler")
                                    + " declined the revised terms after the final willingness roll.");
                    }
                }
            }

            string actionId = string.Empty;
            Dictionary<string, object> normalized = null;
            if (accepted)
            {
                Dictionary<string, object> normalizationPayload = BuildDirectorNormalizationPayload(payload, ruler);
                normalized = Program.NormalizeActionCommand(finalCandidate, campaignId, out errors, normalizationPayload, ReadString(finalCandidate, "publicReason", ""));
                if (normalized == null || errors.Count > 0)
                {
                    WriteDirectorAudit(campaignId, "normalization_failed", ruler, finalCandidate, errors);
                    return DirectorValidationRejected("accepted_action_normalization_failed", errors);
                }

                normalized["source"] = "world_diplomacy_director";
                normalized["reason"] = ReadString(finalCandidate, "publicReason", "A ruler-driven diplomatic decision.");
                normalized["requiresAcceptance"] = false;
                normalized["serverActionId"] = ReadString(normalized, "actionId", Guid.NewGuid().ToString("N"));
                Dictionary<string, object> queued = Program.QueueAction(campaignId, normalized);
                actionId = ReadString(queued, "id", ReadString(normalized, "actionId", ""));
            }

            Dictionary<string, object> diplomaticEvent = BuildDiplomaticEvent(
                campaignId, worldDay, ruler, target, finalCandidate, accepted, negotiationOutcome, targetReason, actionId);
            if (debugInitiativeTest)
            {
                diplomaticEvent["debugCommandId"] = debugCommandId;
                diplomaticEvent["debugMcmTest"] = true;
            }
            QueueDiplomaticEvent(campaignId, diplomaticEvent);
            RecordRulerDiplomacyEventActivity(campaignId, diplomaticEvent,
                "proposal_response", accepted ? "accepted" : "refused");
            if (!accepted && string.IsNullOrWhiteSpace(politicalPressureId))
                ApplyDiplomaticRefusalRelationshipFeedback(campaignId, diplomaticEvent);
            if (!string.IsNullOrWhiteSpace(relationshipOpportunityId))
                FinishRelationshipOpportunity(campaignId, timelineId,
                    relationshipOpportunityId, worldDay, "consumed",
                    accepted ? "action_resolved" : "proposal_refused", actionId);
            if (!string.IsNullOrWhiteSpace(politicalPressureId))
                CompletePoliticalPressureDiplomacy(campaignId, timelineId,
                    worldDay, ruler, target, politicalPressureId, accepted,
                    actionId, ReadString(diplomaticEvent, "eventId", ""),
                    negotiationOutcome);
            ApplyDirectorCooldowns(state, rulerState, finalCandidate, accepted, worldDay);
            state["lastResolvedDay"] = day;
            SaveDirectorState(campaignId, state);
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["status"] = accepted ? "action_queued" : "proposal_refused",
                ["eventId"] = ReadString(diplomaticEvent, "eventId", ""),
                ["actionId"] = actionId,
                ["accepted"] = accepted,
                ["outcome"] = negotiationOutcome
            };
            if (debugInitiativeTest)
            {
                result["debugInitiativeTest"] = true;
                result["idempotent"] = false;
                result["command"] = ReadString(finalCandidate, "command", "");
                result["actionLabel"] = DirectorEventTitle(ReadString(finalCandidate, "command", ""), accepted);
                result["actorKingdomName"] = ReadString(ruler, "name", "");
                result["targetKingdomName"] = ReadString(target, "name", "");
                result["rulerName"] = ReadString(ruler, "leaderName", "");
                result["initiativeAttempts"] = initiativeAttempts;
                if (normalized != null) result["record"] = normalized;
            }
            WorldSimulationService.RecordWorldTestDiplomacyEvaluation(campaignId, timelineId, day,
                ReadString(result, "status", ""), initiativeAttempts.Count, true, false,
                new Dictionary<string, object>
                {
                    ["intent"] = mode,
                    ["command"] = ReadString(finalCandidate, "command", ""),
                    ["outcome"] = negotiationOutcome,
                    ["actorKingdom"] = ReadString(ruler, "kingdomId", ReadString(ruler, "name", "")),
                    ["targetKingdom"] = ReadString(target, "kingdomId", ReadString(target, "name", ""))
                });
            return result;
        }

        internal static Dictionary<string, object> BuildAutonomousWorldStartupGrace(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            double originDay = ReadDouble(payload, "campaignStartDay", -1d);
            bool hasOrigin = originDay >= 0d && originDay <= worldDay + 0.0001d;
            double elapsed = hasOrigin
                ? Math.Max(0d, worldDay - originDay)
                : AutonomousWorldStartupGraceDays;
            return new Dictionary<string, object>
            {
                ["policy"] = "reign_autonomous_world_day_5",
                ["originWorldDay"] = hasOrigin ? originDay : -1d,
                ["unlockWorldDay"] = hasOrigin
                    ? originDay + AutonomousWorldStartupGraceDays : worldDay,
                ["elapsedCampaignDays"] = elapsed,
                ["graceDays"] = AutonomousWorldStartupGraceDays,
                ["locked"] = hasOrigin && elapsed + 0.0001d
                    < AutonomousWorldStartupGraceDays,
                ["legacyCompatible"] = !hasOrigin
            };
        }

        private static void AttachDirectorRulerAttitudes(string campaignId,
            string timelineId, List<Dictionary<string, object>> kingdoms)
        {
            using (ReignDbConnection connection = Program.OpenCampaignConnection(campaignId))
            {
                RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                RelationshipsService.EnsureWorldRelationshipSchema(connection);
                foreach (Dictionary<string, object> observer in kingdoms)
                {
                    string observerHeroId = ReadString(observer,
                        "leaderHeroId", "");
                    ArrayList attitudes = new ArrayList();
                    foreach (Dictionary<string, object> target in kingdoms)
                    {
                        if (ReferenceEquals(observer, target)) continue;
                        string targetHeroId = ReadString(target,
                            "leaderHeroId", "");
                        if (string.IsNullOrWhiteSpace(observerHeroId)
                            || string.IsNullOrWhiteSpace(targetHeroId))
                            continue;
                        Dictionary<string, object> attitude =
                            RelationshipsService.ResolveRulerDiplomaticAttitude(connection, campaignId,
                                timelineId, observerHeroId, targetHeroId,
                                "world_diplomacy");
                        attitude["targetKingdomId"] =
                            ReadString(target, "kingdomId", "");
                        attitudes.Add(attitude);
                    }
                    observer["rulerAttitudes"] = attitudes;
                }
            }
        }

        private static Dictionary<string, object> FindDirectorRulerAttitude(
            Dictionary<string, object> observer,
            Dictionary<string, object> target)
        {
            string targetId = ReadString(target, "kingdomId", "");
            return ReadDictionaryList(observer, "rulerAttitudes")
                .FirstOrDefault(item => ReadString(item, "targetKingdomId", "")
                    .Equals(targetId, StringComparison.OrdinalIgnoreCase))
                ?? new Dictionary<string, object>
                {
                    ["hasPair"] = false,
                    ["personalAffinity"] = 0,
                    ["targetPublicStanding"] = 0,
                    ["effectiveAttitude"] = 0,
                    ["publicStandingRevision"] = 0
                };
        }

        private static Dictionary<string, object> SelectDirectorRuler(string campaignId, int day, double worldDay, List<Dictionary<string, object>> kingdoms, Dictionary<string, object> payload, Dictionary<string, object> state, string rollSalt, out List<Dictionary<string, object>> attempts)
        {
            List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
            attempts = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> ruler in kingdoms)
            {
            Dictionary<string, object> rulerState = GetDirectorRulerState(state, ruler, worldDay);
                rulerState["agendaNeedsRefresh"] = ReadBool(rulerState, "agendaNeedsRefresh", false) || worldDay - ReadDouble(rulerState, "agendaUpdatedDay", -1000d) >= 30d;
                if (worldDay < ReadDouble(rulerState, "cooldownUntilDay", 0d))
                {
                    continue;
                }

                Dictionary<string, object> traits = LoadDirectorTraits(campaignId, ruler);
                Dictionary<string, object> peaceAssessment = DirectorIntentAssessment("peace", traits);
                double peaceBaseline = DirectorPersonalityBaseline(ReadDouble(peaceAssessment, "score", 50d));
                foreach (string enemyId in ReadStringList(ruler, "enemies"))
                {
                    Dictionary<string, object> war = GetDirectorWarState(state, ReadString(ruler, "kingdomId", ""), enemyId);
                    double effectiveFatigue = EffectiveWarFatigue(ReadDouble(war, "fatigue", 0d));
                    double fatiguePressure = EconomyPeacePressure(effectiveFatigue, CurrentEconomicStress(ruler, worldDay),
                        string.Equals(ReadString(ruler, "civilWarOpponentKingdomId", ""), enemyId, StringComparison.OrdinalIgnoreCase));
                    double peaceChance = BlendDirectorChance(peaceBaseline, DirectorPeaceCeiling, fatiguePressure);
                    double roll = StableDirectorRoll(campaignId, day, ReadString(ruler, "leaderHeroId", "") + "|peace|" + enemyId + "|" + (rollSalt ?? string.Empty));
                    attempts.Add(DirectorInitiativeAttempt(ruler, "peace", enemyId, peaceAssessment, peaceBaseline, fatiguePressure, peaceChance, roll));
                    if (roll < peaceChance)
                    {
                        candidates.Add(new Dictionary<string, object>
                        {
                            ["ruler"] = ruler,
                            ["mode"] = "peace",
                            ["targetKingdomId"] = enemyId,
                            ["traitGroupScore"] = ReadDouble(peaceAssessment, "score", 50d),
                            ["traitGroupInputs"] = ReadDictionary(peaceAssessment, "contributors") ?? new Dictionary<string, object>(),
                            ["personalityBaseline"] = peaceBaseline,
                            ["opportunity"] = fatiguePressure,
                            ["finalChance"] = peaceChance,
                            ["roll"] = roll,
                            ["priority"] = DirectorCandidatePriority(peaceChance, DirectorPeaceCeiling, fatiguePressure, roll)
                        });
                    }
                }

                foreach (string family in new[] { "expansion", "prosperity", "security" })
                {
                    double opportunity = DirectorStrategicOpportunity(family, ruler, ReadDictionaryList(payload, "kingdoms"), payload);
                    if (opportunity < 0d) continue;
                    Dictionary<string, object> assessment = DirectorIntentAssessment(family, traits);
                    double baseline = DirectorPersonalityBaseline(ReadDouble(assessment, "score", 50d));
                    double chance = BlendDirectorChance(baseline, DirectorOpportunityCeiling, opportunity);
                    double roll = StableDirectorRoll(campaignId, day, ReadString(ruler, "leaderHeroId", "") + "|" + family + "|" + (rollSalt ?? string.Empty));
                    attempts.Add(DirectorInitiativeAttempt(ruler, family, string.Empty, assessment, baseline, opportunity, chance, roll));
                    if (roll < chance)
                    {
                        candidates.Add(new Dictionary<string, object>
                        {
                            ["ruler"] = ruler,
                            ["mode"] = family,
                            ["targetKingdomId"] = string.Empty,
                            ["traitGroupScore"] = ReadDouble(assessment, "score", 50d),
                            ["traitGroupInputs"] = ReadDictionary(assessment, "contributors") ?? new Dictionary<string, object>(),
                            ["personalityBaseline"] = baseline,
                            ["opportunity"] = opportunity,
                            ["finalChance"] = chance,
                            ["roll"] = roll,
                            ["priority"] = DirectorCandidatePriority(chance, DirectorOpportunityCeiling, opportunity, roll)
                        });
                    }
                }
            }

            return candidates.OrderByDescending(x => ReadDouble(x, "priority", 0d)).FirstOrDefault();
        }

        private static Dictionary<string, object> DirectorInitiativeAttempt(Dictionary<string, object> ruler, string family, string targetKingdomId,
            Dictionary<string, object> assessment, double baseline, double opportunity, double chance, double roll)
        {
            return new Dictionary<string, object>
            {
                ["rulerId"] = ReadString(ruler, "leaderHeroId", ""),
                ["rulerName"] = ReadString(ruler, "leaderName", ""),
                ["kingdomId"] = ReadString(ruler, "kingdomId", ""),
                ["family"] = family,
                ["targetKingdomId"] = targetKingdomId ?? string.Empty,
                ["traitGroupScore"] = ReadDouble(assessment, "score", 50d),
                ["traitGroupInputs"] = ReadDictionary(assessment, "contributors") ?? new Dictionary<string, object>(),
                ["personalityBaseline"] = baseline,
                ["opportunity"] = opportunity,
                ["finalChance"] = chance,
                ["roll"] = roll,
                ["passed"] = roll < chance
            };
        }

        private static double DirectorPersonalityBaseline(double score)
        {
            score = Clamp(0d, 100d, score);
            return score <= 50d
                ? DirectorOpposedBaseline + (DirectorNeutralBaseline - DirectorOpposedBaseline) * score / 50d
                : DirectorNeutralBaseline + (DirectorAlignedBaseline - DirectorNeutralBaseline) * (score - 50d) / 50d;
        }

        private static double DirectorIntentScore(string family, Dictionary<string, object> traits)
        {
            return ReadDouble(DirectorIntentAssessment(family, traits), "score", 50d);
        }

        private static Dictionary<string, object> DirectorIntentAssessment(string family, Dictionary<string, object> virtues)
        {
            Dictionary<string, object> contributors = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            double score = 0d;
            Action<string, double, bool> add = (key, weight, inverse) =>
            {
                double value = DirectorVirtue(virtues, key);
                double effectiveValue = inverse ? 100d - value : value;
                double contribution = weight * effectiveValue;
                score += contribution;
                contributors[key] = new Dictionary<string, object>
                {
                    ["value"] = value,
                    ["inverse"] = inverse,
                    ["effectiveValue"] = effectiveValue,
                    ["weight"] = weight,
                    ["contribution"] = contribution
                };
            };

            switch ((family ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "expansion":
                    add("compassion", 0.60d, true);
                    add("boldness", 0.20d, false);
                    add("courage", 0.20d, false);
                    break;
                case "prosperity":
                    add("judgment", 0.40d, false);
                    add("responsibility", 0.35d, false);
                    add("loyalty", 0.15d, false);
                    add("boldness", 0.10d, false);
                    break;
                case "security":
                    add("loyalty", 0.35d, false);
                    add("judgment", 0.30d, false);
                    add("responsibility", 0.25d, false);
                    add("honor", 0.10d, false);
                    break;
                default:
                    add("compassion", 0.45d, false);
                    add("judgment", 0.30d, false);
                    add("responsibility", 0.15d, false);
                    add("honor", 0.10d, false);
                    break;
            }

            return new Dictionary<string, object>
            {
                ["family"] = string.IsNullOrWhiteSpace(family) ? "peace" : family,
                ["score"] = Clamp(0d, 100d, score),
                ["contributors"] = contributors
            };
        }

        private static double BlendDirectorChance(double baseline, double ceiling, double opportunity)
        {
            return baseline + (ceiling - baseline) * Clamp(0d, 1d, opportunity);
        }

        private static double DirectorCandidatePriority(double chance, double ceiling, double opportunity, double roll)
        {
            double chanceStrength = ceiling <= 0d ? 0d : chance / ceiling;
            double rollMargin = chance <= 0d ? 0d : Clamp(0d, 1d, (chance - roll) / chance);
            return 0.45d * chanceStrength + 0.35d * Clamp(0d, 1d, opportunity) + 0.20d * rollMargin;
        }

        private static double DirectorStrategicOpportunity(string family, Dictionary<string, object> ruler, List<Dictionary<string, object>> kingdoms, Dictionary<string, object> world)
        {
            string rulerId = ReadString(ruler, "kingdomId", "");
            HashSet<string> enemies = new HashSet<string>(ReadStringList(ruler, "enemies"), StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> peacefulTargets = kingdoms.Where(x => !string.Equals(ReadString(x, "kingdomId", ""), rulerId, StringComparison.OrdinalIgnoreCase) && !enemies.Contains(ReadString(x, "kingdomId", ""))).ToList();
            if (peacefulTargets.Count == 0) return -1d;

            double strength = Math.Max(1d, ReadDouble(ruler, "strength", 1d));
            double treasury = Math.Max(0d, ReadDouble(ruler, "treasury", 0d));
            double income = ReadDouble(ruler, "dailyGoldChange", 0d);
            if (family == "expansion")
            {
                List<Dictionary<string, object>> eligibleWarTargets = peacefulTargets.Where(target => DirectorCanDeclareAdditionalWar(world, ruler, target)).ToList();
                if (eligibleWarTargets.Count == 0) return 0d;
                return (1d - CurrentEconomicStress(ruler, ReadDouble(world, "worldDay", -1))) * eligibleWarTargets.Max(target =>
                {
                    double targetStrength = Math.Max(1d, ReadDouble(target, "strength", 1d));
                    double advantage = Clamp(0d, 1d, (strength / targetStrength - 0.85d) / 1.15d);
                    double riches = Clamp(0d, 1d, ReadDouble(target, "treasury", 0d) / Math.Max(50000d, treasury + ReadDouble(target, "treasury", 0d)));
                    bool border = DirectorRelation(world, rulerId, ReadString(target, "kingdomId", ""), "sharesBorder") > 0d;
                    return Clamp(0d, 1d, 0.55d * advantage + 0.25d * riches + (border ? 0.20d : 0d));
                });
            }
            if (family == "prosperity")
            {
                double economicNeed = Clamp(0d, 1d, (-income / Math.Max(100d, Math.Abs(income) + 100d)) + (treasury < 50000d ? 0.25d : 0d));
                double bestPartner = peacefulTargets.Max(target => Clamp(0d, 1d, 0.35d + ReadDouble(target, "townCount", 0d) * 0.1d + (DirectorRelation(world, rulerId, ReadString(target, "kingdomId", ""), "sharesBorder") > 0d ? 0.2d : 0d)));
                return Clamp(0d, 1d, 0.55d * bestPartner + 0.45d * economicNeed);
            }

            int wars = enemies.Count;
            double exposure = Clamp(0d, 1d, wars * 0.35d + (ReadInt(ruler, "fiefCount", 0) <= 2 ? 0.25d : 0d));
            double trustedPartner = peacefulTargets.Max(target => Clamp(0d, 1d,
                0.45d + ReadDouble(FindDirectorRulerAttitude(ruler, target),
                    "effectiveAttitude", 0d) / 200d));
            return Clamp(0d, 1d, 0.55d * trustedPartner + 0.45d * exposure);
        }

        private static double CurrentEconomicStress(Dictionary<string, object> ruler, double worldDay)
        {
            var stress = ReadDictionary(ruler, "economicStress");
            if (stress == null || ReadInt(stress, "schemaVersion", 0) < 2) return 0;
            double observed = ReadDouble(stress, "observedDay", -1);
            double value = ReadDouble(stress, "maximum", 0);
            if (!double.IsFinite(worldDay) || !double.IsFinite(observed) || !double.IsFinite(value)
                || worldDay < 0 || observed < 0 || Math.Abs(worldDay - observed) > 1) return 0;
            return Clamp(0, 1, value);
        }

        private static double EconomyPeacePressure(double effectiveFatigue, double stress, bool civilWar)
        {
            double fatigue = Math.Pow(effectiveFatigue / 100d, 2d);
            return civilWar ? fatigue : Math.Max(fatigue, Math.Pow(stress, 2));
        }

        private static double DirectorRelation(Dictionary<string, object> world, string firstId, string secondId, string field)
        {
            Dictionary<string, object> relation = ReadDictionaryList(world, "relations").FirstOrDefault(x =>
                (string.Equals(ReadString(x, "kingdomAId", ""), firstId, StringComparison.OrdinalIgnoreCase) && string.Equals(ReadString(x, "kingdomBId", ""), secondId, StringComparison.OrdinalIgnoreCase))
                || (string.Equals(ReadString(x, "kingdomAId", ""), secondId, StringComparison.OrdinalIgnoreCase) && string.Equals(ReadString(x, "kingdomBId", ""), firstId, StringComparison.OrdinalIgnoreCase)));
            if (relation == null) return 0d;
            return field == "sharesBorder" ? (ReadBool(relation, field, false) ? 1d : 0d) : ReadDouble(relation, field, 0d);
        }

        private static bool DirectorCanDeclareAdditionalWar(Dictionary<string, object> world, Dictionary<string, object> actor, Dictionary<string, object> proposedTarget)
        {
            List<string> enemyIds = ReadStringList(actor, "enemies").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (enemyIds.Count == 0) return true;

            double opposingStrength = Math.Max(0d, ReadDouble(proposedTarget, "strength", 0d));
            foreach (string enemyId in enemyIds)
            {
                Dictionary<string, object> enemy = FindDirectorKingdom(world, enemyId);
                if (enemy == null) return false;
                opposingStrength += Math.Max(0d, ReadDouble(enemy, "strength", 0d));
            }
            return ReadDouble(actor, "strength", 0d) > opposingStrength;
        }

        private static double EffectiveWarFatigue(double fatigue)
        {
            return Clamp(0d, 100d, fatigue);
        }

        private static void UpdateDirectorWarStates(Dictionary<string, object> state, Dictionary<string, object> payload, double worldDay)
        {
            Dictionary<string, object> warStates = ReadDictionary(state, "wars") ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            state["wars"] = warStates;
            HashSet<string> activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> war in ReadDictionaryList(payload, "wars"))
            {
                string aId = ReadString(war, "kingdomAId", "");
                string bId = ReadString(war, "kingdomBId", "");
                string key = DirectorPairKey(aId, bId);
                activeKeys.Add(key);
                Dictionary<string, object> a = FindDirectorKingdom(payload, aId);
                Dictionary<string, object> b = FindDirectorKingdom(payload, bId);
                if (!(warStates.TryGetValue(key, out object value) && value is Dictionary<string, object> record))
                {
                    record = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["kingdomAId"] = aId,
                        ["kingdomBId"] = bId,
                        ["startedDay"] = ReadDouble(war, "startedDay", worldDay),
                        ["baselineStrengthA"] = ReadDouble(a, "strength", 1d),
                        ["baselineStrengthB"] = ReadDouble(b, "strength", 1d),
                        ["baselineTreasuryA"] = ReadDouble(a, "treasury", 1d),
                        ["baselineTreasuryB"] = ReadDouble(b, "treasury", 1d),
                        ["baselineFiefsA"] = ReadInt(a, "fiefCount", 0),
                        ["baselineFiefsB"] = ReadInt(b, "fiefCount", 0)
                    };
                    warStates[key] = record;
                }

                record["active"] = true;
                record["lastObservedDay"] = worldDay;
                record["fatigueA"] = CalculateObjectiveFatigue(record, war, a, b, true, worldDay);
                record["fatigueB"] = CalculateObjectiveFatigue(record, war, b, a, false, worldDay);
            }

            foreach (KeyValuePair<string, object> pair in warStates.ToList())
            {
                if (pair.Value is Dictionary<string, object> record && !activeKeys.Contains(pair.Key))
                {
                    record["active"] = false;
                    if (!record.ContainsKey("closedDay")) record["closedDay"] = worldDay;
                }
            }
        }

        private static double CalculateObjectiveFatigue(Dictionary<string, object> baseline, Dictionary<string, object> war, Dictionary<string, object> side, Dictionary<string, object> enemy, bool isA, double worldDay)
        {
            double startedDay = ReadDouble(baseline, "startedDay", worldDay);
            double warDays = Math.Max(0d, worldDay - startedDay);
            double baselineStrength = Math.Max(1d, ReadDouble(baseline, isA ? "baselineStrengthA" : "baselineStrengthB", 1d));
            double baselineTreasury = Math.Max(1d, ReadDouble(baseline, isA ? "baselineTreasuryA" : "baselineTreasuryB", 1d));
            int baselineFiefs = ReadInt(baseline, isA ? "baselineFiefsA" : "baselineFiefsB", ReadInt(side, "fiefCount", 0));
            double strength = Math.Max(0d, ReadDouble(side, "strength", 0d));
            double enemyStrength = Math.Max(1d, ReadDouble(enemy, "strength", 1d));
            double treasury = Math.Max(0d, ReadDouble(side, "treasury", 0d));
            int currentFiefs = ReadInt(side, "fiefCount", 0);
            int lost = Math.Max(0, baselineFiefs - currentFiefs);
            int gained = Math.Max(0, currentFiefs - baselineFiefs);
            int casualties = ReadInt(war, isA ? "casualtiesA" : "casualtiesB", 0);
            int sufferedRaids = ReadInt(war, isA ? "raidsB" : "raidsA", 0);
            int concurrentWars = ReadStringList(side, "enemies").Count;
            double casualtyPressure = 20d * Clamp(0d, 1d, casualties / baselineStrength);
            double disadvantage = 15d * Clamp(0d, 1d, 1d - strength / enemyStrength);
            double treasuryStress = 10d * Clamp(0d, 1d, 1d - treasury / baselineTreasury);
            double fatigue = 0.35d * warDays
                + 8d * lost
                - 4d * gained
                + 2d * sufferedRaids
                + 3d * Math.Max(0, ReadInt(side, "capturedNobleCount", 0))
                + (ReadBool(side, "rulerIsPrisoner", false) ? 12d : 0d)
                + 10d * Math.Max(0, concurrentWars - 1)
                + casualtyPressure
                + disadvantage
                + treasuryStress;
            return Clamp(0d, 100d, fatigue);
        }

        private static Dictionary<string, object> AskRulerForDiplomaticDecision(string campaignId, Dictionary<string, object> world, Dictionary<string, object> ruler, Dictionary<string, object> traits, Dictionary<string, object> rulerState, string mode, string fixedTargetId)
        {
            string system = "You are the strict Bannerlord Reign world diplomacy director. Decide only for the named NPC ruler and only within the supplied intent family. Personality and persistent agenda govern motives, but live strategic facts and legal actions constrain the result. You may send a non-unilateral proposal to the player kingdom, but you never answer, counter, accept, reject, or execute on the player's behalf; code queues that proposal for the player's royal court. For alliances, compare nationalPowerAssessments; a weaker proposer should use an executable diplomatic_package with treatyKind alliance_package and sufficient consideration, using only listed allianceMarriageOptions for marriage terms. Gold alliance packages must fit the payer leaderGold balance, including counteroffers; treasury is combined realm wealth and cannot fund a ruler payment. Missing leaderGold means no paid package. Gold alliance packages require explicit goldFromHeroStringId and goldToHeroStringId; settlements require settlementFromKingdomId and settlementToKingdomId. Return JSON only. Never invent IDs or resources. Return no action when no credible move exists. Public reasons are what the ruler announces; decisionFactors are short structured audit factors, never hidden chain-of-thought.";
            Dictionary<string, object> compactWorld = CompactDirectorWorld(
                world, ReadString(ruler, "kingdomId", ""), fixedTargetId, mode);
            string rulerKingdomId = ReadString(ruler, "kingdomId", "");
            bool fixedTargetIsCivilWar = !string.IsNullOrWhiteSpace(fixedTargetId)
                && IsDirectorCivilWarPair(world, rulerKingdomId,
                    fixedTargetId);
            List<string> eligibleWarTargetIds = ReadDictionaryList(world, "kingdoms")
                .Where(x => !string.Equals(ReadString(x, "kingdomId", ""), ReadString(ruler, "kingdomId", ""), StringComparison.OrdinalIgnoreCase))
                .Where(x => !ReadStringList(ruler, "enemies").Contains(ReadString(x, "kingdomId", ""), StringComparer.OrdinalIgnoreCase))
                .Where(x => DirectorCanDeclareAdditionalWar(world, ruler, x))
                .Select(x => ReadString(x, "kingdomId", ""))
                .ToList();
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                ["decision"] = "none|unilateral|proposal",
                ["command"] = "one supported command or empty",
                ["targetKingdomId"] = "real kingdom id or empty; player kingdom is allowed as the recipient of an NPC action or proposal",
                ["targetSettlementId"] = "real settlement id when required",
                ["terms"] = new Dictionary<string, object>(),
                ["publicReason"] = "concise public justification",
                ["decisionFactors"] = new ArrayList { "personality:...", "strategy:...", "memory:..." },
                ["confidence"] = 0.0,
                ["agenda"] = new Dictionary<string, object>
                {
                    ["priorities"] = new ArrayList(), ["preferredTargets"] = new ArrayList(), ["redLines"] = new ArrayList(), ["obligations"] = new ArrayList(), ["grievances"] = new ArrayList()
                }
            };
            string rulerHeroId = ReadString(ruler, "leaderHeroId", "");
            Dictionary<string, object> rulerProfile = Program.ReadJsonObject(Program.CharacterFile(campaignId, rulerHeroId, "profile.json"));
            string rulerMbti = RelationshipsService.BuildCharacterMbtiPromptBlock(campaignId, rulerHeroId, rulerProfile,
                Program.ReadJsonObject(Program.CharacterFile(campaignId, rulerHeroId, "traits.json")));
            List<Dictionary<string, object>> legalActionCatalog =
                Program.CompactActionCatalog(Program.ActionCatalog()).Where(x =>
                        DirectorCommandAllowedForIntent(mode,
                            ReadString(x, "command", ""))
                        && !(fixedTargetIsCivilWar
                            && IsDirectorPeaceCommand(ReadString(x,
                                "command", ""))))
                    .ToList();
            string rulerJson = DirectorRulerPromptJson(ruler);
            string traitsJson = Json.Serialize(traits);
            string stateJson = Json.Serialize(rulerState);
            string actionCatalogJson = Json.Serialize(legalActionCatalog);
            string worldJson = Json.Serialize(compactWorld);
            string schemaJson = Json.Serialize(schema);
            string prompt = "RULER:\n" + rulerJson
                + "\n\nRULER MBTI:\n" + rulerMbti
                + "\n\nCOURT VIRTUE GROUPS (0..100; additional numeric personality inputs):\n" + traitsJson
                + "\n\nPERSISTENT STATE:\n" + stateJson
                + "\n\nINTENT FAMILY: " + mode + ". Choose only an action in this family's catalog; multipart terms must support this intent."
                + (mode == "expansion" ? "\nLEGAL NEW-WAR TARGET IDS: " + Json.Serialize(eligibleWarTargetIds) + ". A declaration of war may target only one of these IDs." : "")
                + (string.IsNullOrWhiteSpace(fixedTargetId) ? "" : "\nFIXED TARGET: " + fixedTargetId)
                + (fixedTargetIsCivilWar
                    ? "\nCIVIL-WAR SAFETY: Generic peace and surrender commands are unavailable for this pair. Only the dedicated deterministic rebellion-recognition process can end this civil war without a leadership victory."
                    : "")
                + (string.IsNullOrWhiteSpace(ReadString(world, "requiredRelationshipPolarity", "")) ? ""
                    : "\nRELATIONSHIP BREAKTHROUGH: This is a bonus opportunity fixed to "
                        + ReadString(world, "requiredRelationshipPolarity", "")
                        + " polarity. Choose only an action of that polarity toward the fixed target. Mixed or ambiguous packages are forbidden. Return none if no matching action credibly benefits the kingdom.")
                + (string.IsNullOrWhiteSpace(ReadString(world, "requiredPoliticalPressurePolarity", "")) ? ""
                    : "\nPOLITICAL PRESSURE: The ruler's court is pressing for a "
                        + ReadString(world, "requiredPoliticalPressurePolarity", "")
                        + " diplomatic action toward the fixed target. Current signed pressure is "
                        + ReadInt(world, "politicalPressureValue", 0).ToString(CultureInfo.InvariantCulture)
                        + " through the " + ReadString(world, "politicalPressureChannel", "general")
                        + " channel. Choose only an action of that polarity that is legal and benefits the kingdom. A command that ends an active war remains positive even when its reparations, tribute, surrender, or settlement terms are harsh; term harshness affects negotiation willingness, not diplomatic polarity. Mixed or ambiguous packages are forbidden. Return none when no credible matching action exists.")
                + "\n\nSUPPORTED ACTION CATALOG:\n" + actionCatalogJson
                + "\n\nWORLD:\n" + worldJson
                + "\n\nOUTPUT SHAPE:\n" + schemaJson;
            Dictionary<string, object> promptSections =
                new Dictionary<string, object>
                {
                    ["system"] = system.Length,
                    ["ruler"] = rulerJson.Length,
                    ["mbti"] = rulerMbti.Length,
                    ["traits"] = traitsJson.Length,
                    ["persistentState"] = stateJson.Length,
                    ["actionCatalog"] = actionCatalogJson.Length,
                    ["world"] = worldJson.Length,
                    ["outputShape"] = schemaJson.Length,
                    ["totalRequestCharacters"] = system.Length + prompt.Length
                };
            Dictionary<string, object> llmRequest = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["requestType"] = "diplomacy",
                ["heroStringId"] = ReadString(ruler, "leaderHeroId", ""),
                ["messages"] = new ArrayList
                {
                    new Dictionary<string, object> { ["role"] = "system", ["content"] = system },
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = prompt }
                },
                ["temperature"] = 0.35d,
                ["maxTokens"] = 2000,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" },
                ["seed"] = StableDirectorSeed(campaignId, (int)ReadDouble(world, "worldDay", 0d), ReadString(ruler, "leaderHeroId", "")),
                ["promptSections"] = promptSections
            };
            Program.LogOperational("diplomacy.prompt_sections", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["rulerHeroId"] = rulerHeroId,
                ["intent"] = mode,
                ["fixedTargetId"] = fixedTargetId,
                ["fixedTargetIsCivilWar"] = fixedTargetIsCivilWar,
                ["sectionCharacters"] = promptSections
            });
            Dictionary<string, object> llm = Program.ChatWithLlm(llmRequest);
            if (!ReadBool(llm, "ok", false))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = ReadString(llm, "error", "Diplomacy model unavailable.") };
            }

            llm = Program.RetryMalformedStructuredResponse(llm, llmRequest, campaignId,
                Program.EnsureCorrelationId(llmRequest), "diplomacy_decision", rulerHeroId, "");
            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", ""));
            if (parsed == null)
            {
                Program.WriteAudit(campaignId, Program.EnsureCorrelationId(llmRequest), "server", "diplomacy",
                    "diplomacy.format_fallback", rulerHeroId, "", "", "completed", 0,
                    "Malformed ruler decision was converted to a safe no-initiative evaluation.",
                    new Dictionary<string, object> { ["fallback"] = "none" });
            }
            return parsed == null
                ? MalformedDiplomacyDecisionFallback()
                : new Dictionary<string, object> { ["ok"] = true, ["decision"] = parsed };
        }

        internal static Dictionary<string, object> AskRulerToRespond(
            string campaignId,
            Dictionary<string, object> world,
            Dictionary<string, object> proposer,
            Dictionary<string, object> responder,
            Dictionary<string, object> candidate,
            string mode,
            bool finalCounterDecision)
        {
            Dictionary<string, object> traits = LoadDirectorTraits(campaignId, responder);
            Dictionary<string, object> allianceLeverage = BuildAllianceLeverage(world, proposer, responder, candidate);
            string role = finalCounterDecision
                ? "You are the original proposing NPC ruler answering one counteroffer. Accept or refuse it immediately."
                : "You are the target NPC ruler answering a diplomatic proposal. Accept, refuse, or make one counteroffer.";
            string responderHeroId = ReadString(responder, "leaderHeroId", "");
            Dictionary<string, object> responderProfile = Program.ReadJsonObject(Program.CharacterFile(campaignId, responderHeroId, "profile.json"));
            string responderMbti = RelationshipsService.BuildCharacterMbtiPromptBlock(campaignId, responderHeroId, responderProfile,
                Program.ReadJsonObject(Program.CharacterFile(campaignId, responderHeroId, "traits.json")));
            List<string> counterCommands = DirectorIntentCommands.TryGetValue(mode,
                    out HashSet<string> legalCommands)
                ? legalCommands.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
            string prompt = role
                + " Personality and strategic reality must govern the answer. Return JSON only. Never invent IDs or assets.\n\nRESPONDER:\n" + DirectorRulerPromptJson(responder)
                + "\n\nRESPONDER MBTI:\n" + responderMbti
                + "\n\nRESPONDER COURT VIRTUE GROUPS (0..100):\n" + Json.Serialize(traits)
                + "\n\nPROPOSER:\n" + DirectorRulerPromptJson(proposer)
                + "\n\nPROPOSAL:\n" + Json.Serialize(candidate)
                + (string.IsNullOrWhiteSpace(ReadString(candidate,
                        "requiredPolarity", ""))
                    ? ""
                    : "\n\nBINDING DIPLOMATIC PURPOSE: Any counteroffer must remain "
                        + ReadString(candidate, "requiredPolarity", "")
                        + ". Do not reverse a conciliatory opportunity into a hostile demand or a hostile opportunity into a conciliatory agreement.")
                + (IsDirectorAllianceProposal(candidate) ? "\n\nBINDING ALLIANCE LEVERAGE:\n" + Json.Serialize(allianceLeverage) + "\nBase willingness must exclude relationship, power, and consideration because deterministic code applies them. If consideration is below requiredConsideration, counter with an executable alliance_package that meets it or refuse. Gold and item counters require explicit fromHeroStringId and toHeroStringId; settlement counters require settlementFromKingdomId and settlementToKingdomId; marriage counters require two explicit IDs from allianceMarriageOptions." : "\nBase willingness must exclude deterministic relationship modifiers.")
                + "\n\nWORLD:\n" + Json.Serialize(CompactDirectorWorld(
                    world,
                    ReadString(responder, "kingdomId", ""),
                    ReadString(proposer, "kingdomId", ""), mode))
                + (finalCounterDecision ? "" : "\n\nA counteroffer is a complete executable replacement, never a patch. Its command must be one of: "
                    + Json.Serialize(counterCommands)
                    + ". Include only the replacement command's own targetSettlementId and terms; omit assets, payments, treaties, and prisoners that are not part of the replacement.")
                + "\n\nReturn {\"response\":\"accept|refuse" + (finalCounterDecision ? "" : "|counter")
                + "\",\"command\":\"" + (finalCounterDecision
                    ? "empty"
                    : "required legal replacement command when response is counter; otherwise empty")
                + "\",\"targetSettlementId\":\"real settlement id only when required by replacement command; otherwise empty\",\"baseWillingness\":\"0..100 excluding power and payment\",\"acceptReason\":\"public explanation if the authoritative roll accepts\",\"refuseReason\":\"public explanation if the authoritative roll refuses\",\"publicReason\":\"counteroffer explanation only\",\"terms\":{},\"decisionFactors\":[\"short audit factors\"]}. The server makes the final accept/refuse roll, so both outcome reasons must match their named result.";
            Dictionary<string, object> llmRequest = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["requestType"] = "diplomacy",
                ["heroStringId"] = ReadString(responder, "leaderHeroId", ""),
                ["system"] = "Act only for the responding Bannerlord ruler. Output strict JSON and no prose outside it.",
                ["prompt"] = prompt,
                ["temperature"] = 0.3d,
                ["maxTokens"] = 1200,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            };
            Dictionary<string, object> llm = Program.ChatWithLlm(llmRequest);
            if (!ReadBool(llm, "ok", false))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = ReadString(llm, "error", "Diplomacy responder unavailable.") };
            }

            llm = Program.RetryMalformedStructuredResponse(llm, llmRequest, campaignId,
                Program.EnsureCorrelationId(llmRequest), "diplomacy_response", responderHeroId, "");
            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", ""));
            if (parsed == null)
            {
                Program.WriteAudit(campaignId, Program.EnsureCorrelationId(llmRequest), "server", "diplomacy",
                    "diplomacy.format_fallback", responderHeroId, "", "", "completed", 0,
                    "Malformed ruler response was converted to a safe refusal.",
                    new Dictionary<string, object> { ["fallback"] = "refuse" });
            }
            return parsed == null
                ? MalformedDiplomacyResponseFallback()
                : new Dictionary<string, object> { ["ok"] = true, ["response"] = parsed };
        }

        private static Dictionary<string, object> MalformedDiplomacyDecisionFallback()
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["formatFallback"] = true,
                ["decision"] = new Dictionary<string, object>
                {
                    ["decision"] = "none",
                    ["command"] = "",
                    ["targetKingdomId"] = "",
                    ["targetSettlementId"] = "",
                    ["terms"] = new Dictionary<string, object>(),
                    ["publicReason"] = "No diplomatic initiative was issued.",
                    ["decisionFactors"] = new ArrayList { "system: malformed structured response fallback" },
                    ["confidence"] = 0d,
                    ["agenda"] = new Dictionary<string, object>()
                }
            };
        }

        private static Dictionary<string, object> MalformedDiplomacyResponseFallback()
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["formatFallback"] = true,
                ["response"] = new Dictionary<string, object>
                {
                    ["response"] = "refuse",
                    ["baseWillingness"] = 0d,
                    ["acceptReason"] = "The terms would have been acceptable.",
                    ["refuseReason"] = "No agreement was reached.",
                    ["publicReason"] = "No agreement was reached.",
                    ["terms"] = new Dictionary<string, object>(),
                    ["decisionFactors"] = new ArrayList { "system: malformed structured response fallback" }
                }
            };
        }
    }
}
