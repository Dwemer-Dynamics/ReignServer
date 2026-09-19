using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
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
        private static readonly HashSet<string> DirectorSupportedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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

        private static Dictionary<string, object> EvaluateWorldDiplomacy(Dictionary<string, object> payload)
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

            Dictionary<string, object> settings = LoadSettings();
            string diplomacyApiUrl = LlmApiUrl(settings);
            bool localDiplomacyEndpoint = diplomacyApiUrl.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0
                || diplomacyApiUrl.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0;
            if (string.IsNullOrWhiteSpace(diplomacyApiUrl) || string.IsNullOrWhiteSpace(ModelForRequest(settings, "diplomacy")))
            {
                return DirectorError("The diplomacy AI route is not configured. Check the API URL and Diplomacy Model in the Reign server control center.");
            }
            if (!UsesCodexSubscription(settings) && !localDiplomacyEndpoint && string.IsNullOrWhiteSpace(LlmApiKey(settings)))
            {
                return DirectorError("The main LLM API key is not configured. Open the Reign Control Center, save the key under Server Diagnostics, then run Test LLM Key.");
            }

            Dictionary<string, object> state;
            lock (FileLock)
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
                RecordWorldTestDiplomacyEvaluation(campaignId, timelineId, day,
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
                RecordWorldTestDiplomacyEvaluation(campaignId, timelineId, day,
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
                RecordWorldTestDiplomacyEvaluation(campaignId, timelineId, day,
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
                Dictionary<string, object> pendingAction = NormalizeActionCommand(candidate, campaignId, out errors, normalizationPayload, ReadString(candidate, "publicReason", ""));
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
                normalized = NormalizeActionCommand(finalCandidate, campaignId, out errors, normalizationPayload, ReadString(finalCandidate, "publicReason", ""));
                if (normalized == null || errors.Count > 0)
                {
                    WriteDirectorAudit(campaignId, "normalization_failed", ruler, finalCandidate, errors);
                    return DirectorValidationRejected("accepted_action_normalization_failed", errors);
                }

                normalized["source"] = "world_diplomacy_director";
                normalized["reason"] = ReadString(finalCandidate, "publicReason", "A ruler-driven diplomatic decision.");
                normalized["requiresAcceptance"] = false;
                normalized["serverActionId"] = ReadString(normalized, "actionId", Guid.NewGuid().ToString("N"));
                Dictionary<string, object> queued = QueueAction(campaignId, normalized);
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
            RecordWorldTestDiplomacyEvaluation(campaignId, timelineId, day,
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

        private static Dictionary<string, object> BuildAutonomousWorldStartupGrace(
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
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                EnsureWorldRelationshipSchema(connection);
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
                            ResolveRulerDiplomaticAttitude(connection, campaignId,
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
                    double fatiguePressure = Math.Pow(effectiveFatigue / 100d, 2d);
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
                return eligibleWarTargets.Max(target =>
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
            Dictionary<string, object> rulerProfile = ReadJsonObject(CharacterFile(campaignId, rulerHeroId, "profile.json"));
            string rulerMbti = BuildCharacterMbtiPromptBlock(campaignId, rulerHeroId, rulerProfile,
                ReadJsonObject(CharacterFile(campaignId, rulerHeroId, "traits.json")));
            List<Dictionary<string, object>> legalActionCatalog =
                CompactActionCatalog(ActionCatalog()).Where(x =>
                        DirectorCommandAllowedForIntent(mode,
                            ReadString(x, "command", ""))
                        && !(fixedTargetIsCivilWar
                            && IsDirectorPeaceCommand(ReadString(x,
                                "command", ""))))
                    .ToList();
            string rulerJson = Json.Serialize(ruler);
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
            LogOperational("diplomacy.prompt_sections", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["rulerHeroId"] = rulerHeroId,
                ["intent"] = mode,
                ["fixedTargetId"] = fixedTargetId,
                ["fixedTargetIsCivilWar"] = fixedTargetIsCivilWar,
                ["sectionCharacters"] = promptSections
            });
            Dictionary<string, object> llm = ChatWithLlm(llmRequest);
            if (!ReadBool(llm, "ok", false))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = ReadString(llm, "error", "Diplomacy model unavailable.") };
            }

            llm = RetryMalformedStructuredResponse(llm, llmRequest, campaignId,
                EnsureCorrelationId(llmRequest), "diplomacy_decision", rulerHeroId, "");
            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", ""));
            if (parsed == null)
            {
                WriteAudit(campaignId, EnsureCorrelationId(llmRequest), "server", "diplomacy",
                    "diplomacy.format_fallback", rulerHeroId, "", "", "completed", 0,
                    "Malformed ruler decision was converted to a safe no-initiative evaluation.",
                    new Dictionary<string, object> { ["fallback"] = "none" });
            }
            return parsed == null
                ? MalformedDiplomacyDecisionFallback()
                : new Dictionary<string, object> { ["ok"] = true, ["decision"] = parsed };
        }

        private static Dictionary<string, object> AskRulerToRespond(
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
            Dictionary<string, object> responderProfile = ReadJsonObject(CharacterFile(campaignId, responderHeroId, "profile.json"));
            string responderMbti = BuildCharacterMbtiPromptBlock(campaignId, responderHeroId, responderProfile,
                ReadJsonObject(CharacterFile(campaignId, responderHeroId, "traits.json")));
            List<string> counterCommands = DirectorIntentCommands.TryGetValue(mode,
                    out HashSet<string> legalCommands)
                ? legalCommands.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
            string prompt = role
                + " Personality and strategic reality must govern the answer. Return JSON only. Never invent IDs or assets.\n\nRESPONDER:\n" + Json.Serialize(responder)
                + "\n\nRESPONDER MBTI:\n" + responderMbti
                + "\n\nRESPONDER COURT VIRTUE GROUPS (0..100):\n" + Json.Serialize(traits)
                + "\n\nPROPOSER:\n" + Json.Serialize(proposer)
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
            Dictionary<string, object> llm = ChatWithLlm(llmRequest);
            if (!ReadBool(llm, "ok", false))
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = ReadString(llm, "error", "Diplomacy responder unavailable.") };
            }

            llm = RetryMalformedStructuredResponse(llm, llmRequest, campaignId,
                EnsureCorrelationId(llmRequest), "diplomacy_response", responderHeroId, "");
            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", ""));
            if (parsed == null)
            {
                WriteAudit(campaignId, EnsureCorrelationId(llmRequest), "server", "diplomacy",
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

        private static Dictionary<string, object> BuildDirectorCandidate(Dictionary<string, object> decision, Dictionary<string, object> ruler)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["command"] = CanonicalCommand(ReadFirstString(decision, "command", "action", "type")),
                ["actorHeroStringId"] = ReadString(ruler, "leaderHeroId", ""),
                ["actorKingdomStringId"] = ReadString(ruler, "kingdomId", ""),
                ["actorClanStringId"] = ReadString(ruler, "rulingClanId", ""),
                ["targetKingdomId"] = ReadFirstString(decision, "targetKingdomId", "targetKingdomStringId"),
                ["targetKingdomStringId"] = ReadFirstString(decision, "targetKingdomId", "targetKingdomStringId"),
                ["targetSettlementId"] = ReadFirstString(decision, "targetSettlementId", "targetSettlementStringId"),
                ["targetSettlementStringId"] = ReadFirstString(decision, "targetSettlementId", "targetSettlementStringId"),
                ["terms"] = ReadDictionary(decision, "terms") ?? new Dictionary<string, object>(),
                ["publicReason"] = RequirePublicReason(decision, ReadString(ruler, "leaderName", "The ruler") + " acted in the interests of the realm."),
                ["reason"] = RequirePublicReason(decision, "A ruler-driven diplomatic decision."),
                ["decisionFactors"] = decision.ContainsKey("decisionFactors") ? decision["decisionFactors"] : new ArrayList(),
                ["confidence"] = ReadDouble(decision, "confidence", 0.5d),
                ["source"] = "world_diplomacy_director",
                ["correlationId"] = "world-diplomacy-" + Guid.NewGuid().ToString("N")
            };
        }

        private static void ApplyDirectorTargetIdentity(
            Dictionary<string, object> candidate,
            Dictionary<string, object> target)
        {
            if (candidate == null || target == null) return;
            string targetRulerId = ReadString(target, "leaderHeroId", "");
            string targetClanId = ReadString(target, "rulingClanId", "");
            if (!string.IsNullOrWhiteSpace(targetRulerId))
                candidate["targetHeroStringId"] = targetRulerId;
            if (!string.IsNullOrWhiteSpace(targetClanId))
                candidate["targetClanStringId"] = targetClanId;
        }

        private static Dictionary<string, object> ApplyCounteroffer(Dictionary<string, object> original, Dictionary<string, object> answer)
        {
            Dictionary<string, object> next = new Dictionary<string, object>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[]
            {
                "actorHeroStringId", "actorKingdomStringId", "actorClanStringId",
                "targetHeroStringId", "targetClanStringId",
                "targetKingdomId", "targetKingdomStringId", "source",
                "correlationId", "confidence", "timelineId", "requiredPolarity",
                "relationshipOpportunityId", "relationshipPolarity",
                "relationshipThreshold", "politicalPressureId",
                "politicalPressurePolarity", "politicalPressureValue",
                "politicalPressureChannel", "initiativeDiagnostics"
            })
            {
                if (original.TryGetValue(key, out object value)) next[key] = value;
            }

            next["command"] = CanonicalCommand(ReadFirstString(answer,
                "command", "action", "type"));
            string settlementId = ReadFirstString(answer, "targetSettlementId",
                "targetSettlementStringId");
            next["targetSettlementId"] = settlementId;
            next["targetSettlementStringId"] = settlementId;
            next["terms"] = ReadDictionary(answer, "terms")
                ?? new Dictionary<string, object>();
            string reason = RequirePublicReason(answer, "The responding ruler proposed revised terms.");
            next["counterPublicReason"] = reason;
            next["publicReason"] = reason;
            next["reason"] = reason;
            next["decisionFactors"] = answer.ContainsKey("decisionFactors")
                ? answer["decisionFactors"] : new ArrayList();
            return next;
        }

        private static string InvalidCounterofferPublicReason(
            Dictionary<string, object> candidate, List<string> errors)
        {
            List<string> failures = errors ?? new List<string>();
            if (failures.Any(x => x.IndexOf("requires positive polarity",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || x.IndexOf("requires negative polarity",
                    StringComparison.OrdinalIgnoreCase) >= 0))
                return "The revised terms reversed the purpose of this diplomatic approach, so no agreement was reached.";
            if (failures.Any(x => x.StartsWith("unsupported command",
                    StringComparison.OrdinalIgnoreCase)))
                return "The counteroffer named no valid diplomatic action, so the negotiation ended without an agreement.";
            if (failures.Any(x => x.IndexOf("settlement",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || x.IndexOf("surrender",
                    StringComparison.OrdinalIgnoreCase) >= 0))
                return "The territorial counteroffer did not identify lawful holdings that could be transferred, so it was refused.";
            if (failures.Any(x => x.IndexOf("reparations",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || x.IndexOf("tribute",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || x.IndexOf("ransom",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || x.IndexOf("afford",
                    StringComparison.OrdinalIgnoreCase) >= 0))
                return "The counteroffer's payment terms were incomplete or impossible to honor, so no agreement was reached.";
            if (failures.Any(x => x.IndexOf("durationDays",
                    StringComparison.OrdinalIgnoreCase) >= 0))
                return "The proposed treaty omitted a valid duration, so it could not be concluded.";
            string command = CanonicalCommand(ReadString(candidate, "command", ""))
                .Replace('_', ' ');
            return "The revised " + (string.IsNullOrWhiteSpace(command)
                ? "diplomatic" : command) + " terms were not executable, so no agreement was reached.";
        }

        private static bool IsDirectorAllianceProposal(Dictionary<string, object> candidate)
        {
            string command = ReadString(candidate, "command", "");
            Dictionary<string, object> terms = ReadDictionary(candidate, "terms") ?? new Dictionary<string, object>();
            return command.Equals("sign_alliance", StringComparison.OrdinalIgnoreCase)
                || (command.Equals("diplomatic_package", StringComparison.OrdinalIgnoreCase)
                    && (ReadString(terms, "treatyKind", "").Equals("alliance_package", StringComparison.OrdinalIgnoreCase) || ReadBool(terms, "includeAlliance", false)));
        }

        private static Dictionary<string, object> BuildAllianceLeverage(Dictionary<string, object> world, Dictionary<string, object> proposer, Dictionary<string, object> responder, Dictionary<string, object> candidate)
        {
            double proposerPower = DirectorPowerScore(world, ReadString(proposer, "kingdomId", ""), ReadDouble(proposer, "strength", 1d));
            double responderPower = DirectorPowerScore(world, ReadString(responder, "kingdomId", ""), ReadDouble(responder, "strength", 1d));
            double ratio = proposerPower / Math.Max(0.01d, responderPower);
            int modifier;
            int required;
            string band;
            if (ratio >= 1.50d) { modifier = 15; required = 0; band = "dominant"; }
            else if (ratio >= 1.15d) { modifier = 8; required = 0; band = "stronger"; }
            else if (ratio >= 0.85d) { modifier = 0; required = 0; band = "peer"; }
            else if (ratio >= 0.67d) { modifier = -8; required = 10; band = "weaker"; }
            else { modifier = -15; required = 20; band = "much_weaker"; }
            double consideration = DirectorAllianceConsideration(candidate, world, proposer, responder);
            return new Dictionary<string, object>
            {
                ["proposerPower"] = proposerPower, ["responderPower"] = responderPower, ["powerRatio"] = ratio,
                ["powerBand"] = band, ["powerModifier"] = modifier, ["requiredConsideration"] = required,
                ["considerationValue"] = consideration, ["considerationRequirementMet"] = consideration + 0.0001d >= required
            };
        }

        private static double DirectorPowerScore(Dictionary<string, object> world, string kingdomId, double fallback)
        {
            Dictionary<string, object> assessment = ReadDictionaryList(world, "nationalPowerAssessments")
                .FirstOrDefault(x => string.Equals(ReadString(x, "kingdomId", ""), kingdomId, StringComparison.OrdinalIgnoreCase));
            return Math.Max(0.01d, assessment == null ? Math.Max(1d, fallback) : ReadDouble(assessment, "score", 1d));
        }

        private static double DirectorAllianceConsideration(Dictionary<string, object> candidate, Dictionary<string, object> world, Dictionary<string, object> proposer, Dictionary<string, object> responder)
        {
            Dictionary<string, object> terms = ReadDictionary(candidate, "terms") ?? new Dictionary<string, object>();
            string proposerId = ReadString(proposer, "kingdomId", "");
            string responderId = ReadString(responder, "kingdomId", "");
            string candidateActorId = ReadFirstString(candidate, "actorKingdomStringId", "actorKingdomId");
            string fromHeroId = ReadFirstString(terms, "goldFromHeroStringId", "fromHeroStringId");
            string toHeroId = ReadFirstString(terms, "goldToHeroStringId", "toHeroStringId");
            bool defaultDirection = string.IsNullOrWhiteSpace(fromHeroId) && string.IsNullOrWhiteSpace(toHeroId) && string.Equals(candidateActorId, proposerId, StringComparison.OrdinalIgnoreCase);
            bool explicitDirection = string.Equals(fromHeroId, ReadString(proposer, "leaderHeroId", ""), StringComparison.OrdinalIgnoreCase)
                && string.Equals(toHeroId, ReadString(responder, "leaderHeroId", ""), StringComparison.OrdinalIgnoreCase);
            double value = 0d;
            if (defaultDirection || explicitDirection)
            {
                int gold = ReadInt(terms, "gold", 0);
                double economicScale = Math.Max(10000d, ReadDouble(responder, "treasury", 0d) * 0.25d);
                value += Math.Min(15d, 15d * gold / economicScale);
                if (terms.ContainsKey("items") || !string.IsNullOrWhiteSpace(ReadString(terms, "itemId", ""))) value += 5d;
            }

            string marriageA = ReadFirstString(terms, "marriageHero1StringId", "groomHeroStringId");
            string marriageB = ReadFirstString(terms, "marriageHero2StringId", "brideHeroStringId", "spouseHeroStringId");
            if (DirectorMarriageOptionExists(world, proposerId, responderId, marriageA, marriageB)) value += 20d;

            string settlementFrom = ReadString(terms, "settlementFromKingdomId", candidateActorId);
            string settlementTo = ReadString(terms, "settlementToKingdomId", string.Equals(candidateActorId, proposerId, StringComparison.OrdinalIgnoreCase) ? responderId : "");
            if (string.Equals(settlementFrom, proposerId, StringComparison.OrdinalIgnoreCase) && string.Equals(settlementTo, responderId, StringComparison.OrdinalIgnoreCase))
            {
                double responderProsperity = Math.Max(1000d, ReadDouble(responder, "settlementProsperity", 0d));
                double offeredProsperity = ReadStringList(terms, "settlementIds").Select(id => ReadDictionaryList(world, "settlements").FirstOrDefault(x => string.Equals(ReadString(x, "settlementId", ""), id, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).Sum(x => ReadDouble(x, "prosperity", 0d));
                if (offeredProsperity > 0d) value += Math.Min(25d, 10d + 15d * offeredProsperity / responderProsperity);
            }

            if (!string.IsNullOrWhiteSpace(ReadFirstString(terms, "hostageHeroStringId", "prisonerHeroStringId"))) value += 8d;
            if (ReadBool(terms, "guaranteeIndependence", false)) value += 10d;
            if (ReadBool(terms, "militaryCommitment", false) || ReadBool(terms, "warSupport", false)) value += 10d;
            return Math.Min(30d, value);
        }

        private static bool DirectorMarriageOptionExists(Dictionary<string, object> world, string firstKingdomId, string secondKingdomId, string firstHeroId, string secondHeroId)
        {
            if (string.IsNullOrWhiteSpace(firstHeroId) || string.IsNullOrWhiteSpace(secondHeroId)) return false;
            bool legacyMatch = ReadDictionaryList(world, "allianceMarriageOptions").Any(x =>
            {
                bool kingdomsMatch = (string.Equals(ReadString(x, "kingdomAId", ""), firstKingdomId, StringComparison.OrdinalIgnoreCase) && string.Equals(ReadString(x, "kingdomBId", ""), secondKingdomId, StringComparison.OrdinalIgnoreCase))
                    || (string.Equals(ReadString(x, "kingdomAId", ""), secondKingdomId, StringComparison.OrdinalIgnoreCase) && string.Equals(ReadString(x, "kingdomBId", ""), firstKingdomId, StringComparison.OrdinalIgnoreCase));
                bool heroesMatch = (string.Equals(ReadString(x, "heroAId", ""), firstHeroId, StringComparison.OrdinalIgnoreCase) && string.Equals(ReadString(x, "heroBId", ""), secondHeroId, StringComparison.OrdinalIgnoreCase))
                    || (string.Equals(ReadString(x, "heroAId", ""), secondHeroId, StringComparison.OrdinalIgnoreCase) && string.Equals(ReadString(x, "heroBId", ""), firstHeroId, StringComparison.OrdinalIgnoreCase));
                return kingdomsMatch && heroesMatch;
            });
            if (legacyMatch) return true;
            Dictionary<string, object> ideal = SelectIdealDirectorMarriageOption(world, firstKingdomId, secondKingdomId);
            if (ideal == null) return false;
            return (string.Equals(ReadString(ideal, "heroAId", ""), firstHeroId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(ideal, "heroBId", ""), secondHeroId, StringComparison.OrdinalIgnoreCase))
                || (string.Equals(ReadString(ideal, "heroAId", ""), secondHeroId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(ideal, "heroBId", ""), firstHeroId, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ResolveAllianceAcceptance(string campaignId, double worldDay, Dictionary<string, object> world, Dictionary<string, object> proposer, Dictionary<string, object> responder, Dictionary<string, object> candidate, Dictionary<string, object> answer, bool counter, out Dictionary<string, object> audit)
        {
            audit = new Dictionary<string, object>();
            if (!IsDirectorAllianceProposal(candidate))
            {
                double negotiatedBaseWillingness = Clamp(0d, 100d,
                    ReadDouble(answer, "baseWillingness", 50d));
                Dictionary<string, object> attitude =
                    FindDirectorRulerAttitude(responder, proposer);
                double effectiveAttitude = ReadDouble(attitude,
                    "effectiveAttitude", 0d);
                string command = ReadString(candidate, "command", "");
                double factor = IsDirectorPeaceCommand(command) ? 0.15d
                    : ContainsAny(command, "exchange_prisoners", "ransom_package",
                        "loan_or_subsidy", "supply_agreement") ? 0.10d
                    : ContainsAny(command, "demand_", "war_indemnity",
                        "protectorate", "vassalage", "demilitarized") ? 0.08d
                    : 0.20d;
                double cap = IsDirectorPeaceCommand(command) ? 15d
                    : factor == 0.10d ? 10d : factor == 0.08d ? 8d : 20d;
                double negotiatedRelationshipModifier = Clamp(-cap, cap,
                    effectiveAttitude * factor);
                double negotiatedFinalChance = Clamp(5d, 95d,
                    negotiatedBaseWillingness + negotiatedRelationshipModifier);
                double negotiatedRoll = StableDirectorRoll(campaignId,
                    (int)Math.Floor(worldDay), "negotiated_accept|" + command + "|"
                        + ReadString(proposer, "kingdomId", "") + "|"
                        + ReadString(responder, "kingdomId", "") + "|"
                        + (counter ? "counter|" : "initial|")
                        + Json.Serialize(ReadDictionary(candidate, "terms")
                            ?? new Dictionary<string, object>()));
                audit = new Dictionary<string, object>
                {
                    ["baseWillingness"] = negotiatedBaseWillingness,
                    ["relationshipModifier"] = negotiatedRelationshipModifier,
                    ["finalChance"] = negotiatedFinalChance, ["roll"] = negotiatedRoll,
                    ["responderEffectiveAttitude"] = effectiveAttitude,
                    ["passed"] = negotiatedRoll < negotiatedFinalChance / 100d
                };
                return ReadBool(audit, "passed", false);
            }
            Dictionary<string, object> leverage = BuildAllianceLeverage(world, proposer, responder, candidate);
            double baseWillingness = Clamp(0d, 100d, ReadDouble(answer, "baseWillingness", 50d));
            Dictionary<string, object> responderAttitude =
                FindDirectorRulerAttitude(responder, proposer);
            double relationshipModifier = Clamp(-25d, 25d,
                ReadDouble(responderAttitude, "effectiveAttitude", 0d) / 4d);
            double finalChance = Clamp(5d, 95d, baseWillingness
                + ReadDouble(leverage, "powerModifier", 0d)
                + ReadDouble(leverage, "considerationValue", 0d)
                + relationshipModifier);
            double roll = StableDirectorRoll(campaignId, (int)Math.Floor(worldDay), "alliance_accept|" + ReadString(proposer, "kingdomId", "") + "|" + ReadString(responder, "kingdomId", "") + "|" + (counter ? "counter|" : "initial|") + Json.Serialize(ReadDictionary(candidate, "terms") ?? new Dictionary<string, object>()));
            audit = new Dictionary<string, object>(leverage, StringComparer.OrdinalIgnoreCase)
            {
                ["baseWillingness"] = baseWillingness, ["finalChance"] = finalChance, ["roll"] = roll,
                ["relationshipModifier"] = relationshipModifier,
                ["responderPersonalAffinity"] = ReadInt(responderAttitude,
                    "personalAffinity", 0),
                ["proposerPublicStanding"] = ReadInt(responderAttitude,
                    "targetPublicStanding", 0),
                ["responderEffectiveAttitude"] = ReadInt(responderAttitude,
                    "effectiveAttitude", 0),
                ["publicStandingRevision"] = ReadInt(responderAttitude,
                    "publicStandingRevision", 0),
                ["passed"] = ReadBool(leverage, "considerationRequirementMet", false) && roll < finalChance / 100d
            };
            return ReadBool(audit, "passed", false);
        }

        private static void AddAllianceAcceptanceAudit(Dictionary<string, object> candidate, Dictionary<string, object> audit)
        {
            if (candidate == null || audit == null || audit.Count == 0) return;
            Dictionary<string, object> diagnostics = ReadDictionary(candidate, "initiativeDiagnostics") ?? new Dictionary<string, object>();
            candidate["initiativeDiagnostics"] = diagnostics;
            ArrayList attempts = diagnostics.TryGetValue("allianceAcceptance", out object value) && value is ArrayList list ? list : new ArrayList();
            diagnostics["allianceAcceptance"] = attempts;
            attempts.Add(audit);
        }

        private static List<string> ValidateDirectorCandidate(Dictionary<string, object> candidate, Dictionary<string, object> world, string playerKingdomId, string mode, string fixedTargetId)
        {
            List<string> errors = new List<string>();
            string command = CanonicalCommand(ReadString(candidate, "command", ""));
            string actorId = ReadFirstString(candidate, "actorKingdomStringId", "actorKingdomId");
            string targetId = ReadFirstString(candidate, "targetKingdomStringId", "targetKingdomId");
            Dictionary<string, object> actor = FindDirectorKingdom(world, actorId);
            Dictionary<string, object> target = FindDirectorKingdom(world, targetId);
            if (!DirectorSupportedCommands.Contains(command)) errors.Add("unsupported command " + command);
            if (actor == null) errors.Add("actor kingdom does not exist");
            if (target == null) errors.Add("target kingdom does not exist");
            if (string.Equals(actorId, targetId, StringComparison.OrdinalIgnoreCase)) errors.Add("actor and target are the same kingdom");
            if (!string.IsNullOrWhiteSpace(playerKingdomId) && string.Equals(actorId, playerKingdomId, StringComparison.OrdinalIgnoreCase)) errors.Add("the diplomacy director cannot decide for the player kingdom");
            if (ReadBool(actor, "isPlayerKingdom", false)) errors.Add("the diplomacy director cannot decide for the player kingdom");
            if (ReadDouble(candidate, "confidence", 0d) < 0.35d) errors.Add("confidence is below 0.35");
            if (string.IsNullOrWhiteSpace(ReadString(candidate, "publicReason", ""))) errors.Add("public reason is required");
            bool atWar = ReadStringList(actor, "enemies").Any(x => string.Equals(x, targetId, StringComparison.OrdinalIgnoreCase));
            bool civilWarPair = string.Equals(ReadString(actor, "civilWarOpponentKingdomId", ""), targetId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadString(target, "civilWarOpponentKingdomId", ""), actorId, StringComparison.OrdinalIgnoreCase);
            if (civilWarPair && IsDirectorPeaceCommand(command)) errors.Add("civil-war peace requires an explicit negotiated political result");
            if (command == "declare_war" && atWar) errors.Add("kingdoms are already at war");
            if (command == "declare_war" && !atWar && actor != null && target != null && !DirectorCanDeclareAdditionalWar(world, actor, target)) errors.Add("a kingdom already at war must be stronger than all existing enemies and the proposed target combined");
            if (IsDirectorPeaceCommand(command) && !atWar) errors.Add("peace terms require an active war");
            if (IsDirectorPeacefulAgreement(command) && atWar) errors.Add("peaceful agreement cannot be signed during war");
            if (!DirectorCommandAllowedForIntent(mode, command)) errors.Add("command does not belong to the evaluated intent family");
            string requiredPolarity = ReadString(candidate, "requiredPolarity", "");
            if (!string.IsNullOrWhiteSpace(requiredPolarity))
            {
                string actualPolarity = ClassifyRulerDiplomacyPolarity(candidate);
                if (!actualPolarity.Equals(requiredPolarity,
                    StringComparison.OrdinalIgnoreCase))
                    errors.Add("relationship bonus requires " + requiredPolarity
                        + " polarity but the proposed action is " + actualPolarity);
            }
            if (!string.IsNullOrWhiteSpace(fixedTargetId) && !string.Equals(targetId, fixedTargetId, StringComparison.OrdinalIgnoreCase)) errors.Add("peace target differs from the evaluated war");
            if (ReadBool(actor, "rulerIsPrisoner", false) && !IsDirectorCaptiveCommand(command)) errors.Add("captive ruler may only negotiate peace, ransom, or prisoner exchange");
            ValidateDirectorTerms(candidate, world, actor, target, errors);
            return errors;
        }

        private static bool ShouldFallbackPoliticalPressurePeace(
            Dictionary<string, object> candidate, string mode,
            List<string> errors)
        {
            return mode.Equals("peace", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(ReadString(candidate,
                    "politicalPressureId", ""))
                && ReadString(candidate, "requiredPolarity", "")
                    .Equals("positive", StringComparison.OrdinalIgnoreCase)
                && errors.Any(x => x.IndexOf("requires positive polarity",
                    StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static Dictionary<string, object>
            BuildPoliticalPressurePeaceFallback(
                Dictionary<string, object> candidate,
                Dictionary<string, object> ruler,
                Dictionary<string, object> target)
        {
            Dictionary<string, object> fallback =
                new Dictionary<string, object>(candidate,
                    StringComparer.OrdinalIgnoreCase);
            string actorName = ReadString(ruler, "name", "The proposing realm");
            string targetName = ReadString(target, "name", "the opposing realm");
            fallback["command"] = "make_peace";
            fallback["targetSettlementId"] = "";
            fallback["targetSettlementStringId"] = "";
            fallback["terms"] = new Dictionary<string, object>();
            fallback["publicReason"] = actorName
                + " proposes a clean peace with " + targetName
                + " so both realms may end the war without further conditions.";
            fallback["reason"] = ReadString(fallback, "publicReason", "");
            fallback["decisionFactors"] = new ArrayList
            {
                "political_pressure:the court requires a legal conciliatory action toward the fixed wartime target",
                "fallback:the ruler's first proposal violated the binding diplomatic polarity, so one clean-peace proposal was attempted"
            };
            fallback["confidence"] = Math.Max(0.35d,
                ReadDouble(fallback, "confidence", 0.35d));
            fallback["politicalPressureFallback"] = "clean_peace";
            return fallback;
        }

        private static void ValidateDirectorTerms(Dictionary<string, object> candidate, Dictionary<string, object> world, Dictionary<string, object> actor, Dictionary<string, object> target, List<string> errors)
        {
            Dictionary<string, object> terms = ReadDictionary(candidate, "terms") ?? new Dictionary<string, object>();
            string command = CanonicalCommand(ReadString(candidate, "command", ""));
            string settlementId = FirstNonEmpty(ReadFirstString(candidate, "targetSettlementId", "targetSettlementStringId"), ReadFirstString(terms, "targetSettlementId", "settlementId"));
            List<string> settlementIds = ReadStringList(terms, "settlementIds")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!string.IsNullOrWhiteSpace(settlementId)
                && !settlementIds.Contains(settlementId,
                    StringComparer.OrdinalIgnoreCase))
                settlementIds.Add(settlementId);
            if (!string.IsNullOrWhiteSpace(settlementId))
            {
                Dictionary<string, object> settlement = ReadDictionaryList(world, "settlements").FirstOrDefault(x => string.Equals(ReadString(x, "settlementId", ""), settlementId, StringComparison.OrdinalIgnoreCase));
                if (settlement == null) errors.Add("target settlement does not exist");
                else ValidateDirectorSettlementOwner(ReadString(candidate, "command", ""), settlement, actor, target, terms, errors);
            }

            foreach (string id in ReadStringList(terms, "settlementIds"))
            {
                Dictionary<string, object> settlement = ReadDictionaryList(world, "settlements").FirstOrDefault(x => string.Equals(ReadString(x, "settlementId", ""), id, StringComparison.OrdinalIgnoreCase));
                if (settlement == null) errors.Add("settlementIds contains unknown id " + id);
                else ValidateDirectorSettlementOwner(ReadString(candidate, "command", ""), settlement, actor, target, terms, errors);
            }

            string enemyId = ReadFirstString(terms, "enemyKingdomId", "thirdKingdomId");
            if (!string.IsNullOrWhiteSpace(enemyId) && FindDirectorKingdom(world, enemyId) == null) errors.Add("enemy kingdom does not exist");
            foreach (string key in new[] { "gold", "loanGold", "reparationsGold", "indemnityGold", "dailyTribute" })
            {
                int amount = ReadInt(terms, key, 0);
                if (amount < 0) errors.Add(key + " cannot be negative");
                if (amount > 5000000) errors.Add(key + " exceeds the diplomacy safety ceiling");
            }

            if (command == "demand_settlement_peace"
                && settlementIds.Count == 0)
                errors.Add("settlement peace requires at least one explicit settlement id");
            if (command == "demand_surrender_peace"
                && settlementIds.Count == 0
                && !ReadBool(terms, "allTargetFortifications", false))
                errors.Add("surrender peace requires explicit settlements or allTargetFortifications");
            if (command == "offer_tribute_peace"
                && (ReadInt(terms, "dailyTribute", 0) <= 0
                    || ReadInt(terms, "durationDays", 0) <= 0))
                errors.Add("tribute peace requires positive dailyTribute and durationDays");
            if (command == "demand_reparations_peace")
            {
                int reparations = ReadInt(terms, "reparationsGold", 0);
                int tribute = ReadInt(terms, "dailyTribute", 0);
                if (reparations <= 0 && tribute <= 0)
                    errors.Add("reparations peace requires positive reparationsGold or dailyTribute");
                if (tribute > 0 && ReadInt(terms, "durationDays", 0) <= 0)
                    errors.Add("reparations tribute requires positive durationDays");
            }
            if (command == "ransom_package" && ReadInt(terms, "gold", 0) <= 0)
                errors.Add("ransom package requires positive gold");
            if (ContainsAny(command, "sign_trade_agreement",
                    "sign_non_aggression_pact", "sign_alliance",
                    "sign_defensive_pact", "demilitarized_border")
                && ReadInt(terms, "durationDays", 0) <= 0)
                errors.Add(command + " requires positive durationDays");

            int outgoingGold = Math.Max(ReadInt(terms, "gold", 0), ReadInt(terms, "loanGold", 0));
            bool targetPays = command == "war_indemnity" || command == "ransom_package" || command == "demand_reparations_peace" || command == "demand_surrender_peace";
            if (command == "diplomatic_package" && string.Equals(ReadFirstString(terms, "goldFromHeroStringId", "fromHeroStringId"), ReadString(target, "leaderHeroId", ""), StringComparison.OrdinalIgnoreCase)) targetPays = true;
            Dictionary<string, object> payer = targetPays ? target : actor;
            // Packages debit the named ruler, not the sum of every clan's wealth.
            // Old snapshots without a payer balance must not authorize a payment.
            int availableGold = command == "diplomatic_package"
                ? ReadInt(payer, "leaderGold", -1) : ReadInt(payer, "treasury", int.MaxValue);
            if (outgoingGold > 0 && availableGold < 0) errors.Add("payer gold balance is unavailable");
            else if (outgoingGold > 0 && outgoingGold > availableGold) errors.Add((targetPays ? "target" : "actor") + " cannot afford proposed gold transfer");

            if (IsDirectorAllianceProposal(candidate))
            {
                if (command == "diplomatic_package" && !ReadString(terms, "treatyKind", "").Equals("alliance_package", StringComparison.OrdinalIgnoreCase)) errors.Add("alliance package must declare treatyKind alliance_package");
                if (ReadInt(terms, "gold", 0) > 0)
                {
                    string goldFrom = ReadFirstString(terms, "goldFromHeroStringId", "fromHeroStringId");
                    string goldTo = ReadFirstString(terms, "goldToHeroStringId", "toHeroStringId");
                    HashSet<string> participantLeaders = new HashSet<string>(new[] { ReadString(actor, "leaderHeroId", ""), ReadString(target, "leaderHeroId", "") }, StringComparer.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(goldFrom) || string.IsNullOrWhiteSpace(goldTo) || string.Equals(goldFrom, goldTo, StringComparison.OrdinalIgnoreCase) || !participantLeaders.Contains(goldFrom) || !participantLeaders.Contains(goldTo)) errors.Add("alliance package gold requires explicit opposing participant ruler IDs");
                }
                string marriageA = ReadFirstString(terms, "marriageHero1StringId", "groomHeroStringId");
                string marriageB = ReadFirstString(terms, "marriageHero2StringId", "brideHeroStringId", "spouseHeroStringId");
                if ((!string.IsNullOrWhiteSpace(marriageA) || !string.IsNullOrWhiteSpace(marriageB))
                    && !DirectorMarriageOptionExists(world, ReadString(actor, "kingdomId", ""), ReadString(target, "kingdomId", ""), marriageA, marriageB)) errors.Add("marriage pair is unavailable or includes a protected heir");
            }
        }

        private static void ValidateDirectorSettlementOwner(string command, Dictionary<string, object> settlement, Dictionary<string, object> actor, Dictionary<string, object> target, Dictionary<string, object> terms, List<string> errors)
        {
            string ownerId = ReadString(settlement, "ownerKingdomId", "");
            string expectedOwnerId = command == "return_occupied_settlement" ? ReadString(actor, "kingdomId", "")
                : command == "diplomatic_package" ? ReadString(terms, "settlementFromKingdomId", ReadString(target, "kingdomId", ""))
                : ReadString(target, "kingdomId", "");
            if ((command == "demand_settlement_peace" || command == "demand_surrender_peace" || command == "diplomatic_package" || command == "return_occupied_settlement")
                && !string.Equals(ownerId, expectedOwnerId, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("settlement " + ReadString(settlement, "settlementId", "") + " is not owned by the required kingdom");
            }
        }

        private static Dictionary<string, object> BuildDirectorNormalizationPayload(Dictionary<string, object> world, Dictionary<string, object> ruler)
        {
            List<object> heroes = ReadDictionaryList(world, "kingdoms").Select(x => (object)new Dictionary<string, object>
            {
                ["heroId"] = ReadString(x, "leaderHeroId", ""),
                ["heroStringId"] = ReadString(x, "leaderHeroId", ""),
                ["name"] = ReadString(x, "leaderName", ""),
                ["kingdomId"] = ReadString(x, "kingdomId", "")
            }).ToList();
            heroes.AddRange(ReadDictionaryList(world, "prisoners").Select(x => (object)new Dictionary<string, object>
            {
                ["heroId"] = ReadString(x, "heroId", ""),
                ["heroStringId"] = ReadString(x, "heroId", ""),
                ["name"] = ReadString(x, "name", ""),
                ["kingdomId"] = ReadString(x, "kingdomId", "")
            }));
            heroes.AddRange(ReadDictionaryList(world, "allianceMarriageOptions").SelectMany(x => new[]
            {
                (object)new Dictionary<string, object> { ["heroId"] = ReadString(x, "heroAId", ""), ["heroStringId"] = ReadString(x, "heroAId", ""), ["name"] = ReadString(x, "heroAName", ""), ["kingdomId"] = ReadString(x, "kingdomAId", "") },
                (object)new Dictionary<string, object> { ["heroId"] = ReadString(x, "heroBId", ""), ["heroStringId"] = ReadString(x, "heroBId", ""), ["name"] = ReadString(x, "heroBName", ""), ["kingdomId"] = ReadString(x, "kingdomBId", "") }
            }).GroupBy(x => ReadString((Dictionary<string, object>)x, "heroId", ""), StringComparer.OrdinalIgnoreCase).Select(x => x.First()));
            heroes.AddRange(ReadDictionaryList(world, "allianceMarriageCandidates").Select(x => (object)new Dictionary<string, object>
            {
                ["heroId"] = ReadString(x, "heroId", ""),
                ["heroStringId"] = ReadString(x, "heroId", ""),
                ["name"] = ReadString(x, "heroName", ""),
                ["kingdomId"] = ReadString(x, "kingdomId", "")
            }).GroupBy(x => ReadString((Dictionary<string, object>)x, "heroId", ""), StringComparer.OrdinalIgnoreCase).Select(x => x.First()));
            return new Dictionary<string, object>
            {
                ["campaignId"] = ReadString(world, "campaignId", "default"),
                ["hero"] = ruler,
                ["actionResolutionIndex"] = new Dictionary<string, object>
                {
                    ["kingdoms"] = world.ContainsKey("kingdoms") ? world["kingdoms"] : new ArrayList(),
                    ["clans"] = world.ContainsKey("clans") ? world["clans"] : new ArrayList(),
                    ["settlements"] = world.ContainsKey("settlements") ? world["settlements"] : new ArrayList(),
                    ["heroes"] = heroes
                }
            };
        }

#if !REIGN_EXCLUDE_COURT
        private static string QueuePlayerDiplomacyCourtMatter(string campaignId, Dictionary<string, object> world,
            Dictionary<string, object> actor, Dictionary<string, object> target, Dictionary<string, object> candidate,
            Dictionary<string, object> normalizedAction)
        {
            string timelineId = ReadString(world, "timelineId", "main");
            double day = ReadDouble(world, "worldDay", 0d);
            string command = ReadString(candidate, "command", "diplomatic_package");
            string actorId = ReadString(actor, "kingdomId", "");
            string matterId = "court_diplomacy_" + CourtHash(timelineId + "|" + actorId + "|" + command + "|" + Math.Floor(day)).Substring(0, 24);
            int severity = Reign.Core.Contracts.Court.ReignInternationalDocketRules.SeverityForRoll(
                StableAmbassadorInt(matterId + "|severity", 100)) is Reign.Core.Contracts.Court.ReignNobleMatterSeverity rank ? (int)rank : 0;
            Dictionary<string, object> data = new Dictionary<string, object>
            {
                ["matterId"] = matterId, ["kind"] = "proposal", ["severityRank"] = severity,
                ["foreignKingdomId"] = actorId, ["foreignRulerId"] = ReadString(actor, "leaderHeroId", ""),
                ["playerKingdomId"] = ReadString(target, "kingdomId", ""), ["createdDay"] = day,
                ["title"] = ReadString(actor, "name", "A foreign realm") + " sends a " + command.Replace('_', ' ') + " proposal",
                ["summary"] = ReadString(candidate, "publicReason", "A foreign ruler has sent exact terms for consideration."),
                ["candidate"] = candidate, ["normalizedAction"] = normalizedAction,
                ["foreign"] = actor, ["player"] = target, ["channel"] = "trade",
                ["termsSummary"] = BuildDirectorTermsText(ReadDictionary(candidate, "terms"))
            };
            StoreInternationalQueueItem(campaignId, timelineId, matterId, "proposal", severity, day, data);
            return matterId;
        }
#endif

        private static Dictionary<string, object> BuildDiplomaticEvent(string campaignId, double worldDay, Dictionary<string, object> actor, Dictionary<string, object> target, Dictionary<string, object> candidate, bool accepted, string outcome, string targetReason, string actionId)
        {
            string command = ReadString(candidate, "command", "");
            string actorName = ReadString(actor, "leaderName", "An unnamed ruler");
            string targetName = ReadString(target, "leaderName", "another ruler");
            string actorKingdom = ReadString(actor, "name", "an unnamed kingdom");
            string targetKingdom = ReadString(target, "name", "another kingdom");
            string summary = BuildDirectorEventSummary(command, actorName, actorKingdom, targetName, targetKingdom, accepted, outcome);
            return new Dictionary<string, object>
            {
                ["eventId"] = Guid.NewGuid().ToString("N"),
                ["campaignId"] = campaignId,
                ["timelineId"] = ReadString(candidate, "timelineId", "main"),
                ["worldDay"] = worldDay,
                ["createdTs"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["actionId"] = actionId,
                ["command"] = command,
                ["accepted"] = accepted,
                ["outcome"] = HumanizeDirectorOutcome(outcome),
                ["title"] = IsDirectorAllianceProposal(candidate) ? (accepted ? "Alliance Package" : "Alliance Package Refused") : DirectorEventTitle(command, accepted),
                ["summary"] = summary,
                ["terms"] = ReadDictionary(candidate, "terms") ?? new Dictionary<string, object>(),
                ["termsText"] = BuildDirectorTermsText(ReadDictionary(candidate, "terms")),
                ["actorHeroId"] = ReadString(actor, "leaderHeroId", ""),
                ["actorName"] = actorName,
                ["actorKingdomId"] = ReadString(actor, "kingdomId", ""),
                ["actorKingdomName"] = actorKingdom,
                ["actorPublicReason"] = ReadString(candidate, "publicReason", ""),
                ["targetHeroId"] = ReadString(target, "leaderHeroId", ""),
                ["targetName"] = targetName,
                ["targetKingdomId"] = ReadString(target, "kingdomId", ""),
                ["targetKingdomName"] = targetKingdom,
                ["targetPublicReason"] = targetReason,
                ["privateDecisionFactors"] = candidate.ContainsKey("decisionFactors") ? candidate["decisionFactors"] : new ArrayList(),
                ["initiativeDiagnostics"] = candidate.ContainsKey("initiativeDiagnostics") ? candidate["initiativeDiagnostics"] : new Dictionary<string, object>(),
                ["relationshipOpportunityId"] = ReadString(candidate,
                    "relationshipOpportunityId", ""),
                ["relationshipPolarity"] = ReadString(candidate,
                    "requiredPolarity", ""),
                ["politicalPressureId"] = ReadString(candidate,
                    "politicalPressureId", ""),
                ["politicalPressureValue"] = ReadInt(candidate,
                    "politicalPressureValue", 0),
                ["politicalPressureChannel"] = ReadString(candidate,
                    "politicalPressureChannel", ""),
                ["politicalPressurePolarity"] = ReadString(candidate,
                    "politicalPressureId", "").Length > 0
                        ? ReadString(candidate, "requiredPolarity", "")
                        : "",
                ["correlationId"] = ReadString(candidate, "correlationId", ""),
                ["consequenceTags"] = DirectorConsequenceTags(command, accepted, ReadDictionary(candidate, "terms")),
                ["executionStatus"] = accepted ? "pending" : "not_required",
                ["announcementReady"] = !accepted,
                ["delivered"] = false,
                ["acknowledged"] = false
            };
        }

        private static void QueueDiplomaticEvent(string campaignId, Dictionary<string, object> diplomaticEvent)
        {
            lock (FileLock)
            {
                List<Dictionary<string, object>> queue = ReadDiplomaticEventQueue(campaignId);
                queue.Add(diplomaticEvent);
                if (queue.Count > 500) queue = queue.Skip(queue.Count - 500).ToList();
                WriteDiplomaticEventQueue(campaignId, queue);
                AppendJsonLineToPath(CampaignFile(campaignId, "diplomacy", "events.jsonl"), diplomaticEvent);
                if (!ReadBool(diplomaticEvent, "accepted", false)) AppendDiplomaticConsequence(campaignId, diplomaticEvent);
            }
            try
            {
                List<string> participants = new[]
                {
                    ReadString(diplomaticEvent, "actorHeroId", ""),
                    ReadString(diplomaticEvent, "targetHeroId", "")
                }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                StoreWorldMemoryEvent(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["eventId"] = ReadString(diplomaticEvent, "eventId", ""),
                    ["eventType"] = "world_diplomacy",
                    ["eventSubtype"] = ReadFirstString(diplomaticEvent, "command", "actionType", "intent"),
                    ["worldDay"] = ReadDouble(diplomaticEvent, "worldDay", 0d),
                    ["summary"] = ReadString(diplomaticEvent, "summary", ""),
                    ["participants"] = participants,
                    ["aboutEntityIds"] = participants,
                    ["visibility"] = "public",
                    ["importance"] = ReadBool(diplomaticEvent, "accepted", false) ? 0.9d : 0.7d,
                    ["source"] = "world_diplomacy_director"
                }, "world_diplomacy_director");
            }
            catch (Exception ex)
            {
                LogOperational("world_diplomacy.public_history_failed", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["eventId"] = ReadString(diplomaticEvent, "eventId", ""),
                    ["error"] = ex.Message
                });
            }
        }

        private static Dictionary<string, object> NextDiplomaticEvents(Dictionary<string, object> payload, Dictionary<string, string> query)
        {
            payload = payload ?? new Dictionary<string, object>();
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.TryGetValue("campaignId", out string campaignQuery) ? campaignQuery : ReadString(payload, "campaignId", "default");
            int limit = query.TryGetValue("limit", out string limitText) && int.TryParse(limitText, out int parsed) ? parsed : 10;
            limit = Math.Max(1, Math.Min(20, limit));
            List<Dictionary<string, object>> selected = new List<Dictionary<string, object>>();
            lock (FileLock)
            {
                List<Dictionary<string, object>> queue = ReadDiplomaticEventQueue(campaignId);
                foreach (Dictionary<string, object> item in queue.Where(x => ReadBool(x, "announcementReady", false) && !ReadBool(x, "acknowledged", false)).Take(limit))
                {
                    item["lastFetchedTs"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    selected.Add(new Dictionary<string, object>(item, StringComparer.OrdinalIgnoreCase));
                }
                WriteDiplomaticEventQueue(campaignId, queue);
            }
            foreach (Dictionary<string, object> item in selected)
            {
                item.Remove("privateDecisionFactors");
                item.Remove("initiativeDiagnostics");
            }
            return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaignId, ["count"] = selected.Count, ["events"] = selected };
        }

        private static Dictionary<string, object> AcknowledgeDiplomaticEvent(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string eventId = ReadString(payload, "eventId", "");
            bool found = false;
            lock (FileLock)
            {
                List<Dictionary<string, object>> queue = ReadDiplomaticEventQueue(campaignId);
                Dictionary<string, object> item = queue.FirstOrDefault(x => string.Equals(ReadString(x, "eventId", ""), eventId, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    MarkDiplomaticEventAcknowledged(item,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    found = true;
                }
                WriteDiplomaticEventQueue(campaignId, queue);
            }
            return new Dictionary<string, object> { ["ok"] = found, ["eventId"] = eventId, ["error"] = found ? "" : "Diplomatic event was not found." };
        }

        private static void MarkDiplomaticEventAcknowledged(
            Dictionary<string, object> item, long acknowledgedTs)
        {
            if (item == null) return;
            item["acknowledged"] = true;
            item["acknowledgedTs"] = acknowledgedTs;
            // An acknowledgement is the strongest available evidence that the
            // game client fetched and surfaced the announcement. Keep the two
            // states separate for in-flight popups, but do not leave an
            // acknowledged event permanently "undelivered".
            item["delivered"] = true;
            item["deliveredTs"] = acknowledgedTs;
        }

        private static void UpdateDiplomaticEventForAction(string campaignId, string actionId, string status, Dictionary<string, object> report)
        {
            if (string.IsNullOrWhiteSpace(actionId)) return;
            List<Dictionary<string, object>> pendingRelationshipFeedback =
                new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> pendingRulerActivity =
                new List<Dictionary<string, object>>();
            lock (FileLock)
            {
                List<Dictionary<string, object>> queue = ReadDiplomaticEventQueue(campaignId);
                bool changed = false;
                foreach (Dictionary<string, object> item in queue.Where(x => string.Equals(ReadString(x, "actionId", ""), actionId, StringComparison.OrdinalIgnoreCase)))
                {
                    string currentStatus = ReadString(item, "executionStatus", "");
                    bool legacyRefusalCorrection = currentStatus == "failed" && status == "rejected"
                        && ReadString(report, "statusCorrection", "") == "legacy_government_refusal"
                        && IsRecordedGovernmentRefusalResult(ReadDictionary(report, "result")
                            ?? new Dictionary<string, object>());
                    if (!ShouldAcceptActionStatusTransition(currentStatus, status) && !legacyRefusalCorrection)
                        continue;
                    item["executionStatus"] = status;
                    if (status.Equals("completed", StringComparison.OrdinalIgnoreCase))
                    {
                        item["announcementReady"] = true;
                        if (!ReadBool(item, "consequenceRecorded", false))
                        {
                            AppendDiplomaticConsequence(campaignId, item);
                            item["consequenceRecorded"] = true;
                        }
                        if (!ReadBool(item, "relationshipFeedbackRecorded", false))
                        {
                            // Relationship feedback takes the per-campaign relationship
                            // writer lock. Never take it while FileLock is held: the
                            // continuous relationship worker legitimately takes those
                            // locks in the opposite direction when materializing profile
                            // data, which otherwise deadlocks every server request.
                            pendingRelationshipFeedback.Add(
                                new Dictionary<string, object>(item,
                                    StringComparer.OrdinalIgnoreCase));
                        }
                        // Activity persistence ensures database schema and takes the
                        // PostgreSQL mutation lock. Defer it with relationship feedback
                        // so FileLock never encloses database or relationship work.
                        pendingRulerActivity.Add(
                            new Dictionary<string, object>(item,
                                StringComparer.OrdinalIgnoreCase));
                    }
                    else if (status.Equals("rejected", StringComparison.OrdinalIgnoreCase)
                        && ReadString(report, "statusNormalization", "")
                            .Equals("resolved_npc_government_refusal",
                                StringComparison.OrdinalIgnoreCase))
                    {
                        Dictionary<string, object> result =
                            ReadDictionary(report, "result")
                                ?? new Dictionary<string, object>();
                        string resultCode = ReadFirstString(result,
                            "resultCode", "ResultCode");
                        string message = FirstNonEmpty(
                            ReadString(report, "message", ""),
                            ReadFirstString(result, "debugMessage", "DebugMessage",
                                "message", "Message"),
                            "The ruler yielded to the realm's government and abandoned the proposed action.");
                        string command = ReadString(item, "command", "");
                        item["accepted"] = false;
                        item["outcome"] = HumanizeDirectorOutcome(resultCode);
                        item["title"] = DirectorEventTitle(command, false);
                        item["summary"] = message;
                        item["governmentResolution"] = resultCode;
                        item["governmentResolutionMessage"] = message;
                        item["consequenceTags"] = DirectorConsequenceTags(command,
                            false, ReadDictionary(item, "terms"));
                        item["announcementReady"] = true;
                        item["executionError"] = "";
                    }
                    else if (status.Equals("failed", StringComparison.OrdinalIgnoreCase) || status.Equals("blocked", StringComparison.OrdinalIgnoreCase) || status.Equals("expired", StringComparison.OrdinalIgnoreCase))
                    {
                        item["announcementReady"] = false;
                        item["executionError"] = ReadString(report, "message", "Diplomatic action failed in the game client.");
                    }
                    changed = true;
                }
                if (changed) WriteDiplomaticEventQueue(campaignId, queue);
            }

            foreach (Dictionary<string, object> item in pendingRulerActivity)
                RecordRulerDiplomacyEventActivity(campaignId, item,
                    "native_receipt", "completed");

            foreach (Dictionary<string, object> item in pendingRelationshipFeedback)
            {
                ApplyDiplomaticRelationshipFeedback(campaignId, item, actionId);
                MarkDiplomaticRelationshipFeedbackRecorded(campaignId, actionId,
                    ReadString(item, "eventId", ""));
            }
        }

        private static void MarkDiplomaticRelationshipFeedbackRecorded(string campaignId,
            string actionId, string eventId)
        {
            lock (FileLock)
            {
                List<Dictionary<string, object>> queue = ReadDiplomaticEventQueue(campaignId);
                Dictionary<string, object> item = queue.FirstOrDefault(candidate =>
                    string.Equals(ReadString(candidate, "actionId", ""), actionId,
                        StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(eventId)
                        || string.Equals(ReadString(candidate, "eventId", ""), eventId,
                            StringComparison.OrdinalIgnoreCase)));
                if (item == null || ReadBool(item, "relationshipFeedbackRecorded", false))
                    return;
                item["relationshipFeedbackRecorded"] = true;
                WriteDiplomaticEventQueue(campaignId, queue);
            }
        }

        private static void ApplyDirectorCooldowns(Dictionary<string, object> state, Dictionary<string, object> rulerState, Dictionary<string, object> candidate, bool accepted, double worldDay)
        {
            rulerState["lastInitiativeDay"] = worldDay;
            rulerState["cooldownUntilDay"] = worldDay + 15d;
            string targetId = ReadFirstString(candidate, "targetKingdomId", "targetKingdomStringId");
            Dictionary<string, object> pairCooldowns = ReadDictionary(rulerState, "pairCooldowns") ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            rulerState["pairCooldowns"] = pairCooldowns;
            if (!accepted) pairCooldowns[targetId] = worldDay + 15d;
            if (accepted && ReadString(candidate, "command", "").Equals("declare_war", StringComparison.OrdinalIgnoreCase)) pairCooldowns[targetId] = worldDay + 30d;
            Dictionary<string, object> rulers = ReadDictionary(state, "rulers") ?? new Dictionary<string, object>();
            if (rulers.TryGetValue(targetId, out object targetValue) && targetValue is Dictionary<string, object> targetState)
            {
                Dictionary<string, object> reverseCooldowns = ReadDictionary(targetState, "pairCooldowns") ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                targetState["pairCooldowns"] = reverseCooldowns;
                string actorId = ReadFirstString(candidate, "actorKingdomId", "actorKingdomStringId");
                if (!accepted) reverseCooldowns[actorId] = worldDay + 15d;
                if (accepted && ReadString(candidate, "command", "").Equals("declare_war", StringComparison.OrdinalIgnoreCase)) reverseCooldowns[actorId] = worldDay + 30d;
            }
        }

        private static Dictionary<string, object> GetDirectorRulerState(Dictionary<string, object> state, Dictionary<string, object> ruler, double worldDay)
        {
            Dictionary<string, object> rulers = ReadDictionary(state, "rulers") ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            state["rulers"] = rulers;
            string kingdomId = ReadString(ruler, "kingdomId", "");
            string heroId = ReadString(ruler, "leaderHeroId", "");
            if (!(rulers.TryGetValue(kingdomId, out object value) && value is Dictionary<string, object> record))
            {
                record = NewDirectorRulerState(heroId, worldDay);
                rulers[kingdomId] = record;
            }
            else if (!string.Equals(ReadString(record, "rulerHeroId", ""), heroId, StringComparison.OrdinalIgnoreCase))
            {
                record = NewDirectorRulerState(heroId, worldDay);
                rulers[kingdomId] = record;
            }
            string fingerprint = ReadDouble(ruler, "strength", 0d).ToString("0", CultureInfo.InvariantCulture)
                + "|" + ReadInt(ruler, "treasury", 0)
                + "|" + ReadInt(ruler, "fiefCount", 0)
                + "|" + string.Join(",", ReadStringList(ruler, "enemies").OrderBy(x => x))
                + "|" + ReadBool(ruler, "rulerIsPrisoner", false);
            string previousFingerprint = ReadString(record, "strategicFingerprint", "");
            if (!string.IsNullOrWhiteSpace(previousFingerprint) && !string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal)) record["agendaNeedsRefresh"] = true;
            record["strategicFingerprint"] = fingerprint;
            return record;
        }

        private static Dictionary<string, object> NewDirectorRulerState(string heroId, double worldDay)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["rulerHeroId"] = heroId,
                ["reignStartedDay"] = worldDay,
                ["agendaUpdatedDay"] = -1000d,
                ["agenda"] = new Dictionary<string, object>(),
                ["pairCooldowns"] = new Dictionary<string, object>()
            };
        }

        private static void UpdateDirectorAgenda(Dictionary<string, object> rulerState, Dictionary<string, object> decision, double worldDay)
        {
            Dictionary<string, object> agenda = ReadDictionary(decision, "agenda");
            if (agenda != null && agenda.Count > 0)
            {
                rulerState["agenda"] = agenda;
                rulerState["agendaUpdatedDay"] = worldDay;
                rulerState["agendaNeedsRefresh"] = false;
            }
        }

        private static Dictionary<string, object> LoadDirectorTraits(string campaignId, Dictionary<string, object> ruler)
        {
            string heroId = ReadString(ruler, "leaderHeroId", "");
            Dictionary<string, object> document = string.IsNullOrWhiteSpace(heroId) ? new Dictionary<string, object>() : ReadJsonObject(CharacterFile(campaignId, heroId, "traits.json"));
            if (document.Count > 0 && EnsureTraitPercentageData(document, heroId))
                WriteJsonObject(CharacterFile(campaignId, heroId, "traits.json"), document);
            Dictionary<string, object> persisted = ReadDictionary(document, "courtVirtues");
            if (TraitPercentageDocumentReady(document) && HasCompleteDirectorVirtueProfile(persisted)) return NormalizeDirectorVirtues(persisted);

            if (!string.IsNullOrWhiteSpace(heroId)
                && LoadCharacterProfileLibrary().TryGetValue(heroId, out Dictionary<string, object> shipped))
            {
                Dictionary<string, object> shippedTraits = ReadDictionary(shipped, "traits") ?? new Dictionary<string, object>();
                Dictionary<string, object> shippedVirtues = ReadDictionary(shippedTraits, "courtVirtues");
                if (TraitPercentageDocumentReady(shippedTraits) && HasCompleteDirectorVirtueProfile(shippedVirtues)) return NormalizeDirectorVirtues(shippedVirtues);
            }

            return NeutralDirectorVirtues();
        }

        private static bool HasCompleteDirectorVirtueProfile(Dictionary<string, object> virtues)
        {
            return virtues != null && DirectorCourtVirtueKeys.All(virtues.ContainsKey);
        }

        private static Dictionary<string, object> NormalizeDirectorVirtues(Dictionary<string, object> virtues)
        {
            Dictionary<string, object> normalized = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in DirectorCourtVirtueKeys) normalized[key] = DirectorVirtue(virtues, key);
            return normalized;
        }

        private static Dictionary<string, object> NeutralDirectorVirtues()
        {
            return DirectorCourtVirtueKeys.ToDictionary(x => x, x => (object)50d, StringComparer.OrdinalIgnoreCase);
        }

        private static ArrayList BuildNationalPowerAssessments(Dictionary<string, object> world, Dictionary<string, object> state)
        {
            List<Dictionary<string, object>> kingdoms = ReadDictionaryList(world, "kingdoms");
            if (kingdoms.Count == 0) return new ArrayList();
            double militaryMedian = DirectorMedian(kingdoms.Select(x => Math.Max(1d, ReadDouble(x, "strength", 1d))));
            double territoryMedian = DirectorMedian(kingdoms.Select(x => DirectorTerritoryPower(x)));
            double economyMedian = DirectorMedian(kingdoms.Select(x => DirectorEconomicPower(x)));
            double politicalMedian = DirectorMedian(kingdoms.Select(x => Math.Max(1d, ReadDouble(x, "clanCount", 1d))));
            ArrayList assessments = new ArrayList();
            foreach (Dictionary<string, object> kingdom in kingdoms)
            {
                double military = Math.Max(1d, ReadDouble(kingdom, "strength", 1d)) / militaryMedian;
                double territory = DirectorTerritoryPower(kingdom) / territoryMedian;
                double economy = DirectorEconomicPower(kingdom) / economyMedian;
                double political = Math.Max(1d, ReadDouble(kingdom, "clanCount", 1d)) / politicalMedian;
                int warCount = ReadStringList(kingdom, "enemies").Count;
                double fatigue = DirectorKingdomAverageFatigue(state, ReadString(kingdom, "kingdomId", ""));
                double negativeIncome = Math.Max(0d, -ReadDouble(kingdom, "dailyGoldChange", 0d));
                double incomePenalty = Clamp(0d, 0.10d, negativeIncome / Math.Max(1000d, ReadDouble(kingdom, "treasury", 0d)));
                double readinessPenalty = Math.Min(0.24d, warCount * 0.08d)
                    + Math.Min(0.15d, fatigue * 0.0015d)
                    + (ReadBool(kingdom, "rulerIsPrisoner", false) ? 0.10d : 0d)
                    + incomePenalty;
                double score = Math.Max(0.05d, (0.55d * military + 0.20d * territory + 0.15d * economy + 0.10d * political) * Math.Max(0.35d, 1d - readinessPenalty));
                assessments.Add(new Dictionary<string, object>
                {
                    ["kingdomId"] = ReadString(kingdom, "kingdomId", ""), ["score"] = score,
                    ["military"] = military, ["territory"] = territory, ["economy"] = economy, ["politicalDepth"] = political,
                    ["warCount"] = warCount, ["averageWarFatigue"] = fatigue, ["readinessPenalty"] = readinessPenalty
                });
            }
            return assessments;
        }

        private static double DirectorTerritoryPower(Dictionary<string, object> kingdom)
        {
            return Math.Max(1d, 1000d * ReadDouble(kingdom, "fiefCount", 0d)
                + 0.15d * ReadDouble(kingdom, "settlementProsperity", 0d)
                + 5d * ReadDouble(kingdom, "garrisonStrength", 0d));
        }

        private static double DirectorEconomicPower(Dictionary<string, object> kingdom)
        {
            return Math.Max(1d, ReadDouble(kingdom, "treasury", 0d) + 126d * Math.Max(0d, ReadDouble(kingdom, "dailyGoldChange", 0d)));
        }

        private static double DirectorMedian(IEnumerable<double> values)
        {
            List<double> ordered = values.Where(x => x > 0d).OrderBy(x => x).ToList();
            if (ordered.Count == 0) return 1d;
            int middle = ordered.Count / 2;
            return ordered.Count % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2d;
        }

        private static double DirectorKingdomAverageFatigue(Dictionary<string, object> state, string kingdomId)
        {
            List<double> values = new List<double>();
            foreach (Dictionary<string, object> war in (ReadDictionary(state, "wars") ?? new Dictionary<string, object>()).Values.OfType<Dictionary<string, object>>())
            {
                if (!ReadBool(war, "active", false)) continue;
                if (string.Equals(ReadString(war, "kingdomAId", ""), kingdomId, StringComparison.OrdinalIgnoreCase)) values.Add(ReadDouble(war, "fatigueA", 0d));
                else if (string.Equals(ReadString(war, "kingdomBId", ""), kingdomId, StringComparison.OrdinalIgnoreCase)) values.Add(ReadDouble(war, "fatigueB", 0d));
            }
            return values.Count == 0 ? 0d : values.Average();
        }

        private static Dictionary<string, object> CompactDirectorWorld(
            Dictionary<string, object> world,
            string perspectiveKingdomId,
            string counterpartKingdomId = "",
            string intent = "")
        {
            bool includeMarriageOptions = string.IsNullOrWhiteSpace(intent)
                || intent.Equals("security", StringComparison.OrdinalIgnoreCase);
            HashSet<string> counterpartIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(counterpartKingdomId)) counterpartIds.Add(counterpartKingdomId);
            else
            {
                foreach (Dictionary<string, object> kingdom in ReadDictionaryList(world, "kingdoms"))
                {
                    string id = ReadString(kingdom, "kingdomId", "");
                    if (!string.IsNullOrWhiteSpace(id) && !string.Equals(id, perspectiveKingdomId, StringComparison.OrdinalIgnoreCase))
                        counterpartIds.Add(id);
                }
                foreach (Dictionary<string, object> candidate in includeMarriageOptions
                    ? ReadDictionaryList(world, "allianceMarriageCandidates")
                    : new List<Dictionary<string, object>>())
                {
                    string id = ReadString(candidate, "kingdomId", "");
                    if (!string.IsNullOrWhiteSpace(id) && !string.Equals(id, perspectiveKingdomId, StringComparison.OrdinalIgnoreCase))
                        counterpartIds.Add(id);
                }
                foreach (Dictionary<string, object> option in includeMarriageOptions
                    ? ReadDictionaryList(world, "allianceMarriageOptions")
                    : new List<Dictionary<string, object>>())
                {
                    string id = DirectorMarriageCounterparty(option, perspectiveKingdomId);
                    if (!string.IsNullOrWhiteSpace(id)) counterpartIds.Add(id);
                }
            }
            List<Dictionary<string, object>> idealMarriageOptions =
                includeMarriageOptions
                    ? counterpartIds
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .Select(x => SelectIdealDirectorMarriageOption(world,
                            perspectiveKingdomId, x))
                        .Where(x => x != null).ToList()
                    : new List<Dictionary<string, object>>();
            ArrayList marriageSummary = new ArrayList(counterpartIds
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => (object)new Dictionary<string, object>
                {
                    ["counterpartyKingdomId"] = x,
                    ["selected"] = idealMarriageOptions.Any(y => DirectorMarriageOptionInvolvesKingdom(y, x))
                })
                .ToList());
            Dictionary<string, object> perspective = ReadDictionaryList(world,
                "kingdoms").FirstOrDefault(row => ReadString(row, "kingdomId", "")
                    .Equals(perspectiveKingdomId, StringComparison.OrdinalIgnoreCase));
            int toneDay = (int)Math.Floor(ReadDouble(world, "worldDay", 0d));
            ArrayList relationshipGuidance = new ArrayList((perspective == null
                    ? new List<Dictionary<string, object>>()
                    : ReadDictionaryList(perspective, "rulerAttitudes"))
                .Where(attitude => counterpartIds.Contains(ReadString(attitude,
                    "targetKingdomId", "")))
                .Select(attitude =>
                {
                    int effective = ReadInt(attitude, "effectiveAttitude", 0);
                    int generousChance = Clamp(25 + effective / 5, 10, 45);
                    int harshChance = Clamp(25 - effective / 5, 10, 45);
                    int toneRoll = 1 + (int)((uint)StableDirectorSeed(
                        ReadString(world, "campaignId", "") + "|tone", toneDay,
                        perspectiveKingdomId + "|" + ReadString(attitude,
                            "targetKingdomId", "")) % 100u);
                    string tone = toneRoll <= generousChance ? "generous"
                        : toneRoll <= generousChance + harshChance ? "harsh"
                        : "balanced";
                    return (object)new Dictionary<string, object>
                    {
                        ["targetKingdomId"] = ReadString(attitude,
                            "targetKingdomId", ""),
                        ["effectiveAttitude"] = effective,
                        ["cooperationPreferencePoints"] = Clamp(effective / 4,
                            -25, 25),
                        ["hostilityPreferencePoints"] = Clamp(-effective / 4,
                            -25, 25),
                        ["generousChance"] = generousChance,
                        ["harshChance"] = harshChance,
                        ["toneRoll"] = toneRoll, ["termTone"] = tone
                    };
                }).ToList());
            HashSet<string> relevantKingdomIds = new HashSet<string>(
                counterpartIds, StringComparer.OrdinalIgnoreCase)
            {
                perspectiveKingdomId
            };
            List<Dictionary<string, object>> compactKingdoms =
                ReadDictionaryList(world, "kingdoms")
                    .Where(row => relevantKingdomIds.Contains(ReadString(row,
                        "kingdomId", "")))
                    .Select(row => CompactDirectorKingdomRow(row,
                        relevantKingdomIds)).ToList();
            IEnumerable<Dictionary<string, object>> relevantSettlements =
                ReadDictionaryList(world, "settlements")
                    .Where(row => relevantKingdomIds.Contains(ReadFirstString(
                        row, "ownerKingdomId", "kingdomId")));
            if (string.IsNullOrWhiteSpace(counterpartKingdomId))
            {
                relevantSettlements = relevantSettlements
                    .GroupBy(row => ReadFirstString(row, "ownerKingdomId",
                        "kingdomId"), StringComparer.OrdinalIgnoreCase)
                    .SelectMany(group => group
                        .OrderByDescending(row => ReadBool(row,
                            "isFortification", false))
                        .ThenBy(row => ReadInt(row, "garrison", int.MaxValue))
                        .ThenBy(row => ReadDouble(row, "loyalty", double.MaxValue))
                        .ThenByDescending(row => ReadDouble(row, "prosperity", 0d))
                        .ThenBy(row => ReadString(row, "settlementId", ""),
                            StringComparer.OrdinalIgnoreCase)
                        .Take(group.Key.Equals(perspectiveKingdomId,
                            StringComparison.OrdinalIgnoreCase) ? 8 : 3));
            }
            List<Dictionary<string, object>> compactSettlements =
                relevantSettlements.Select(CompactDirectorSettlementRow)
                    .OrderByDescending(row => ReadBool(row,
                        "isFortification", false))
                    .ThenBy(row => ReadString(row, "settlementId", ""),
                        StringComparer.OrdinalIgnoreCase)
                    .Take(80).ToList();
            List<Dictionary<string, object>> compactWars =
                ReadDictionaryList(world, "wars")
                    .Where(row => DirectorRecordInvolvesAnyKingdom(row,
                        relevantKingdomIds))
                    .Select(row => CompactDirectorRecord(row, new[]
                    {
                        "warId", "kingdomAId", "kingdomBId", "attackerKingdomId",
                        "defenderKingdomId", "active", "startedDay", "durationDays",
                        "fatigueA", "fatigueB", "casualtiesA", "casualtiesB",
                        "raidsA", "raidsB", "settlementsGainedA", "settlementsGainedB"
                    })).Take(40).ToList();
            List<Dictionary<string, object>> compactAgreements =
                ReadDictionaryList(world, "agreements")
                    .Where(row => DirectorRecordInvolvesAnyKingdom(row,
                        relevantKingdomIds))
                    .Select(row => CompactDirectorRecord(row, new[]
                    {
                        "agreementId", "kind", "actorKingdomId", "targetKingdomId",
                        "createdDay", "expireDay", "isActive", "reason"
                    })).Take(60).ToList();
            List<Dictionary<string, object>> compactRelations =
                ReadDictionaryList(world, "relations")
                    .Where(row => DirectorRecordInvolvesAnyKingdom(row,
                        relevantKingdomIds))
                    .Select(row => CompactDirectorRecord(row, new[]
                    {
                        "actorKingdomId", "targetKingdomId", "kingdomAId",
                        "kingdomBId", "relation", "value", "stance"
                    })).Take(80).ToList();
            List<Dictionary<string, object>> compactPrisoners =
                ReadDictionaryList(world, "prisoners")
                    .Where(row => DirectorRecordInvolvesAnyKingdom(row,
                        relevantKingdomIds))
                    .Select(row => CompactDirectorRecord(row, new[]
                    {
                        "heroId", "name", "kingdomId", "captorKingdomId",
                        "captorHeroId", "isRuler", "value"
                    })).Take(50).ToList();
            List<Dictionary<string, object>> compactRecentEvents =
                ReadDictionaryList(world, "recentDiplomaticEvents")
                    .Where(row => DirectorRecordInvolvesAnyKingdom(row,
                        relevantKingdomIds))
                    .Select(row => CompactDirectorRecord(row, new[]
                    {
                        "eventId", "worldDay", "command", "accepted", "outcome",
                        "summary", "actorKingdomId", "targetKingdomId",
                        "consequenceTags"
                    })).Take(20).ToList();
            List<Dictionary<string, object>> compactPower =
                ReadDictionaryList(world, "nationalPowerAssessments")
                    .Where(row => relevantKingdomIds.Contains(ReadString(row,
                        "kingdomId", "")))
                    .Select(row => CompactDirectorRecord(row, new[]
                    {
                        "kingdomId", "strength", "relativeStrength", "treasury",
                        "fortificationCount", "fiefCount", "warCount", "fatigue",
                        "assessment"
                    })).ToList();
            return new Dictionary<string, object>
            {
                ["worldDay"] = ReadDouble(world, "worldDay", 0d),
                ["calendar"] = world.ContainsKey("calendar") ? world["calendar"] : new Dictionary<string, object>
                {
                    ["daysPerYear"] = DirectorCampaignDaysPerYear,
                    ["daysPerSeason"] = 31.5d,
                    ["seasonsPerYear"] = 4
                },
                ["playerKingdomId"] = ReadString(world, "playerKingdomId", ""),
                ["perspectiveKingdomId"] = perspectiveKingdomId,
                ["kingdoms"] = new ArrayList(compactKingdoms),
                ["settlements"] = new ArrayList(compactSettlements),
                ["wars"] = new ArrayList(compactWars),
                ["agreements"] = new ArrayList(compactAgreements),
                ["relations"] = new ArrayList(compactRelations),
                ["prisoners"] = new ArrayList(compactPrisoners),
                ["recentDiplomaticEvents"] = new ArrayList(compactRecentEvents)
                , ["nationalPowerAssessments"] = new ArrayList(compactPower)
                , ["allianceMarriageOptions"] = new ArrayList(idealMarriageOptions)
                , ["allianceMarriageOptionSummary"] = marriageSummary
                , ["relationshipTargetGuidance"] = relationshipGuidance
            };
        }

        private static Dictionary<string, object> CompactDirectorKingdomRow(
            Dictionary<string, object> row, HashSet<string> relevantKingdomIds)
        {
            Dictionary<string, object> compact = CompactDirectorRecord(row,
                new[]
                {
                    "kingdomId", "name", "leaderHeroId", "leaderName", "rulerHeroId",
                    "rulerName", "isPlayerKingdom", "isEliminated", "isRebelRealm",
                    "civilWarOpponentKingdomId", "strength", "treasury", "leaderGold", "fiefCount",
                    "fortificationCount", "rulerIsPrisoner", "enemies", "allies"
                });
            compact["rulerAttitudes"] = new ArrayList(ReadDictionaryList(row,
                    "rulerAttitudes")
                .Where(attitude => relevantKingdomIds.Contains(ReadString(attitude,
                    "targetKingdomId", "")))
                .Select(attitude => CompactDirectorRecord(attitude, new[]
                {
                    "targetKingdomId", "targetRulerHeroId", "effectiveAttitude",
                    "personalAffinity", "publicStanding", "tag"
                })).ToList());
            return compact;
        }

        private static Dictionary<string, object> CompactDirectorSettlementRow(
            Dictionary<string, object> row)
        {
            return CompactDirectorRecord(row, new[]
            {
                "settlementId", "name", "ownerKingdomId", "kingdomId", "kind",
                "isFortification", "prosperity", "security", "loyalty", "garrison"
            });
        }

        private static Dictionary<string, object> CompactDirectorRecord(
            Dictionary<string, object> row, IEnumerable<string> keys)
        {
            Dictionary<string, object> compact =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (row == null) return compact;
            foreach (string key in keys ?? Enumerable.Empty<string>())
                if (row.TryGetValue(key, out object value)) compact[key] = value;
            return compact;
        }

        private static bool DirectorRecordInvolvesAnyKingdom(
            Dictionary<string, object> row, HashSet<string> kingdomIds)
        {
            if (row == null || kingdomIds == null || kingdomIds.Count == 0)
                return false;
            foreach (string key in new[]
            {
                "kingdomId", "ownerKingdomId", "captorKingdomId",
                "actorKingdomId", "targetKingdomId", "kingdomAId", "kingdomBId",
                "attackerKingdomId", "defenderKingdomId", "rebelKingdomId",
                "parentKingdomId", "firstKingdomId", "secondKingdomId"
            })
                if (kingdomIds.Contains(ReadString(row, key, ""))) return true;
            foreach (string key in new[] { "kingdomIds", "participants", "enemies", "allies" })
                if (ReadStringList(row, key).Any(kingdomIds.Contains)) return true;
            return false;
        }

        private static Dictionary<string, object> SelectIdealDirectorMarriageOption(
            Dictionary<string, object> world,
            string firstKingdomId,
            string secondKingdomId)
        {
            List<Dictionary<string, object>> candidates = ReadDictionaryList(world, "allianceMarriageCandidates");
            List<Dictionary<string, object>> firstCandidates = candidates
                .Where(x => string.Equals(ReadString(x, "kingdomId", ""), firstKingdomId, StringComparison.OrdinalIgnoreCase))
                .Where(DirectorMarriageCandidateEligible)
                .ToList();
            List<Dictionary<string, object>> secondCandidates = candidates
                .Where(x => string.Equals(ReadString(x, "kingdomId", ""), secondKingdomId, StringComparison.OrdinalIgnoreCase))
                .Where(DirectorMarriageCandidateEligible)
                .ToList();
            Dictionary<string, object> selected = firstCandidates
                .SelectMany(first => secondCandidates
                    .Where(second => DirectorMarriageCandidatesCompatible(first, second))
                    .Select(second => new Dictionary<string, object>
                    {
                        ["kingdomAId"] = firstKingdomId,
                        ["kingdomBId"] = secondKingdomId,
                        ["heroAId"] = ReadString(first, "heroId", ""),
                        ["heroAName"] = ReadString(first, "heroName", ReadString(first, "heroId", "")),
                        ["clanAId"] = ReadString(first, "clanId", ""),
                        ["heroBId"] = ReadString(second, "heroId", ""),
                        ["heroBName"] = ReadString(second, "heroName", ReadString(second, "heroId", "")),
                        ["clanBId"] = ReadString(second, "clanId", ""),
                        ["selectionScore"] = DirectorMarriagePairScore(first, second),
                        ["selectionReason"] = "Highest deterministic dynastic value among the current eligible ruling-clan candidates; native suitability is revalidated before execution."
                    }))
                .OrderByDescending(x => ReadDouble(x, "selectionScore", 0d))
                .ThenBy(DirectorMarriageOptionKey, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (selected != null) return selected;

            return ReadDictionaryList(world, "allianceMarriageOptions")
                .Where(x => DirectorMarriageOptionInvolvesKingdom(x, firstKingdomId)
                    && DirectorMarriageOptionInvolvesKingdom(x, secondKingdomId))
                .OrderBy(DirectorMarriageOptionKey, StringComparer.OrdinalIgnoreCase)
                .Select(x => new Dictionary<string, object>(x, StringComparer.OrdinalIgnoreCase)
                {
                    ["selectionReason"] = "Best deterministic legacy native-suitable pair supplied by the current client."
                })
                .FirstOrDefault();
        }

        private static bool DirectorMarriageCandidateEligible(Dictionary<string, object> candidate)
        {
            return ReadBool(candidate, "eligibleForPoliticalMarriage", true)
                && !string.IsNullOrWhiteSpace(ReadString(candidate, "heroId", ""))
                && ReadDouble(candidate, "age", 18d) >= 18d
                && !ReadBool(candidate, "isProtectedHeir", false)
                && !ReadBool(candidate, "isMarried", false);
        }

        private static bool DirectorMarriageCandidatesCompatible(Dictionary<string, object> first, Dictionary<string, object> second)
        {
            return !string.Equals(ReadString(first, "heroId", ""), ReadString(second, "heroId", ""), StringComparison.OrdinalIgnoreCase)
                && ReadBool(first, "isFemale", false) != ReadBool(second, "isFemale", false);
        }

        private static double DirectorMarriagePairScore(Dictionary<string, object> first, Dictionary<string, object> second)
        {
            return DirectorMarriageCandidateScore(first)
                + DirectorMarriageCandidateScore(second)
                - 2d * Math.Abs(ReadDouble(first, "age", 30d) - ReadDouble(second, "age", 30d));
        }

        private static double DirectorMarriageCandidateScore(Dictionary<string, object> candidate)
        {
            double age = ReadDouble(candidate, "age", 30d);
            double score = 100d - 2d * Math.Abs(age - 26d);
            if (ReadBool(candidate, "isRulerChild", false))
                score += 160d - 10d * Math.Min(10, Math.Max(1, ReadInt(candidate, "successionRank", 10)));
            if (ReadBool(candidate, "isClanLeader", false)) score += 20d;
            score += 8d * Math.Max(0, ReadInt(candidate, "clanTier", 0));
            score += 0.2d * Clamp(-100d, 100d, ReadDouble(candidate, "relationToRuler", 0d));
            return score;
        }

        private static bool DirectorMarriageOptionInvolvesKingdom(Dictionary<string, object> option, string kingdomId)
        {
            return !string.IsNullOrWhiteSpace(kingdomId)
                && (string.Equals(ReadString(option, "kingdomAId", ""), kingdomId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ReadString(option, "kingdomBId", ""), kingdomId, StringComparison.OrdinalIgnoreCase));
        }

        private static string DirectorMarriageCounterparty(Dictionary<string, object> option, string perspectiveKingdomId)
        {
            string first = ReadString(option, "kingdomAId", "");
            string second = ReadString(option, "kingdomBId", "");
            if (string.Equals(first, perspectiveKingdomId, StringComparison.OrdinalIgnoreCase)) return second;
            if (string.Equals(second, perspectiveKingdomId, StringComparison.OrdinalIgnoreCase)) return first;
            return string.Empty;
        }

        private static string DirectorMarriageOptionKey(Dictionary<string, object> option)
        {
            return ReadString(option, "kingdomAId", "") + "|"
                + ReadString(option, "kingdomBId", "") + "|"
                + ReadString(option, "heroAId", "") + "|"
                + ReadString(option, "heroBId", "");
        }

        private static Dictionary<string, object> PublicDirectorEventMemory(Dictionary<string, object> item)
        {
            return new Dictionary<string, object>
            {
                ["worldDay"] = ReadDouble(item, "worldDay", 0d),
                ["command"] = ReadString(item, "command", ""),
                ["accepted"] = ReadBool(item, "accepted", false),
                ["outcome"] = ReadString(item, "outcome", ""),
                ["actorKingdomId"] = ReadString(item, "actorKingdomId", ""),
                ["targetKingdomId"] = ReadString(item, "targetKingdomId", ""),
                ["actorPublicReason"] = ReadString(item, "actorPublicReason", ""),
                ["targetPublicReason"] = ReadString(item, "targetPublicReason", "")
            };
        }

        private static void AppendDiplomaticConsequence(string campaignId, Dictionary<string, object> diplomaticEvent)
        {
            AppendJsonLineToPath(CampaignFile(campaignId, "diplomacy", "consequences.jsonl"), new Dictionary<string, object>
            {
                ["eventId"] = ReadString(diplomaticEvent, "eventId", ""),
                ["worldDay"] = ReadDouble(diplomaticEvent, "worldDay", 0d),
                ["actorKingdomId"] = ReadString(diplomaticEvent, "actorKingdomId", ""),
                ["targetKingdomId"] = ReadString(diplomaticEvent, "targetKingdomId", ""),
                ["command"] = ReadString(diplomaticEvent, "command", ""),
                ["accepted"] = ReadBool(diplomaticEvent, "accepted", false),
                ["tags"] = diplomaticEvent.ContainsKey("consequenceTags") ? diplomaticEvent["consequenceTags"] : new ArrayList()
            });
        }

        private static Dictionary<string, object> FindDirectorKingdom(Dictionary<string, object> world, string id)
        {
            return ReadDictionaryList(world, "kingdoms").FirstOrDefault(x => string.Equals(ReadString(x, "kingdomId", ""), id, StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object> GetDirectorWarState(Dictionary<string, object> state, string sideId, string enemyId)
        {
            Dictionary<string, object> wars = ReadDictionary(state, "wars") ?? new Dictionary<string, object>();
            if (!(wars.TryGetValue(DirectorPairKey(sideId, enemyId), out object value) && value is Dictionary<string, object> record)) return new Dictionary<string, object>();
            bool sideIsA = string.Equals(ReadString(record, "kingdomAId", ""), sideId, StringComparison.OrdinalIgnoreCase);
            return new Dictionary<string, object> { ["fatigue"] = ReadDouble(record, sideIsA ? "fatigueA" : "fatigueB", 0d), ["active"] = ReadBool(record, "active", false) };
        }

        private static bool HasDirectorWarBlockingAgreement(Dictionary<string, object> world, string actorId, string targetId)
        {
            foreach (Dictionary<string, object> agreement in ReadDictionaryList(world, "agreements"))
            {
                string kind = ReadString(agreement, "kind", "").ToLowerInvariant();
                bool blocking = kind == "alliance" || kind == "defensive_pact" || kind == "non_aggression_pact" || kind == "demilitarized_border" || kind == "guarantee_independence" || kind == "trade_agreement" || kind == "recognized_independence";
                bool pair = (string.Equals(ReadString(agreement, "actorKingdomId", ""), actorId, StringComparison.OrdinalIgnoreCase) && string.Equals(ReadString(agreement, "targetKingdomId", ""), targetId, StringComparison.OrdinalIgnoreCase))
                    || (string.Equals(ReadString(agreement, "actorKingdomId", ""), targetId, StringComparison.OrdinalIgnoreCase) && string.Equals(ReadString(agreement, "targetKingdomId", ""), actorId, StringComparison.OrdinalIgnoreCase));
                if (blocking && pair) return true;
            }
            return false;
        }

        private static bool IsDirectorPeaceCommand(string command)
        {
            return command == "make_peace" || command == "offer_tribute_peace" || command == "demand_reparations_peace" || command == "demand_settlement_peace" || command == "demand_surrender_peace";
        }

        private static bool IsDirectorCivilWarPair(
            Dictionary<string, object> world, string firstKingdomId,
            string secondKingdomId)
        {
            if (string.IsNullOrWhiteSpace(firstKingdomId)
                || string.IsNullOrWhiteSpace(secondKingdomId)) return false;
            Dictionary<string, object> first = FindDirectorKingdom(world,
                firstKingdomId);
            Dictionary<string, object> second = FindDirectorKingdom(world,
                secondKingdomId);
            return string.Equals(ReadString(first,
                    "civilWarOpponentKingdomId", ""), secondKingdomId,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadString(second,
                    "civilWarOpponentKingdomId", ""), firstKingdomId,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool DirectorCommandAllowedForIntent(string intent, string command)
        {
            return DirectorIntentCommands.TryGetValue(intent ?? string.Empty, out HashSet<string> commands)
                && commands.Contains(command ?? string.Empty);
        }

        private static string DirectorIntentForCandidate(
            Dictionary<string, object> candidate,
            Dictionary<string, object> world)
        {
            string command = CanonicalCommand(ReadString(candidate, "command", ""));
            if (IsDirectorPeaceCommand(command)) return "peace";
            if (IsDirectorAllianceProposal(candidate)
                || DirectorIntentCommands["security"].Contains(command))
                return "security";
            if (command == "diplomatic_package")
            {
                string actorId = ReadFirstString(candidate,
                    "actorKingdomStringId", "actorKingdomId");
                string targetId = ReadFirstString(candidate,
                    "targetKingdomStringId", "targetKingdomId");
                Dictionary<string, object> actor = FindDirectorKingdom(world,
                    actorId);
                if (actor != null && ReadStringList(actor, "enemies").Contains(
                        targetId, StringComparer.OrdinalIgnoreCase))
                    return "peace";
            }
            if (DirectorIntentCommands["prosperity"].Contains(command))
                return "prosperity";
            return "expansion";
        }

        private static bool IsDirectorPeacefulAgreement(string command)
        {
            return command == "sign_trade_agreement" || command == "sign_non_aggression_pact" || command == "sign_alliance" || command == "sign_defensive_pact" || command == "demilitarized_border";
        }

        private static bool IsDirectorCaptiveCommand(string command)
        {
            return IsDirectorPeaceCommand(command) || command == "exchange_prisoners" || command == "ransom_package";
        }

        private static string DirectorPairKey(string first, string second)
        {
            return string.Compare(first ?? "", second ?? "", StringComparison.OrdinalIgnoreCase) <= 0 ? (first + "|" + second) : (second + "|" + first);
        }

        private static double StableDirectorRoll(string campaignId, int day, string subject)
        {
            uint hash = (uint)StableDirectorSeed(campaignId, day, subject);
            return (hash % 1000000) / 1000000d;
        }

        private static int StableDirectorSeed(string campaignId, int day, string subject)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in (campaignId + "|" + day.ToString(CultureInfo.InvariantCulture) + "|" + subject + "|world_diplomacy_v1"))
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return (int)(hash & 0x7fffffff);
            }
        }

        private static double DirectorVirtue(Dictionary<string, object> virtues, string key) => Clamp(0d, 100d, ReadDouble(virtues, key, 50d));
        private static double Clamp(double min, double max, double value) => Math.Max(min, Math.Min(max, value));

        private static string RequirePublicReason(Dictionary<string, object> source, string fallback)
        {
            string reason = ReadFirstString(source, "publicReason", "reason", "explanation").Trim();
            return string.IsNullOrWhiteSpace(reason) ? fallback : reason.Length <= 900 ? reason : reason.Substring(0, 900);
        }

        private static string SelectDiplomaticOutcomeReason(
            Dictionary<string, object> source, string reportedAnswerKind,
            bool accepted, string acceptFallback, string refuseFallback)
        {
            string explicitReason = accepted
                ? ReadFirstString(source, "acceptReason", "accept_reason")
                : ReadFirstString(source, "refuseReason", "refuse_reason");
            if (!string.IsNullOrWhiteSpace(explicitReason))
                return explicitReason.Length <= 900 ? explicitReason : explicitReason.Substring(0, 900);

            bool reportedMatches = accepted
                ? string.Equals(reportedAnswerKind, "accept", StringComparison.OrdinalIgnoreCase)
                : string.Equals(reportedAnswerKind, "refuse", StringComparison.OrdinalIgnoreCase);
            if (reportedMatches)
            {
                string legacyReason = RequirePublicReason(source, string.Empty);
                if (!string.IsNullOrWhiteSpace(legacyReason)) return legacyReason;
            }
            return accepted ? acceptFallback : refuseFallback;
        }

        private static string DirectorEventTitle(string command, bool accepted)
        {
            string subject = command.Replace('_', ' ');
            subject = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(subject);
            return accepted ? subject : subject + " Refused";
        }

        private static string HumanizeDirectorOutcome(string outcome)
        {
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase((outcome ?? "resolved").Replace('_', ' '));
        }

        private static string BuildDirectorEventSummary(string command, string actorName, string actorKingdom, string targetName, string targetKingdom, bool accepted, string outcome)
        {
            if (DirectorUnilateralCommands.Contains(command)) return actorName + " of " + actorKingdom + " has acted against " + targetKingdom + ".";
            return accepted
                ? actorName + " of " + actorKingdom + " and " + targetName + " of " + targetKingdom + " have concluded an agreement."
                : actorName + " of " + actorKingdom + " approached " + targetName + " of " + targetKingdom + ", but no agreement was reached.";
        }

        private static string BuildDirectorTermsText(Dictionary<string, object> terms)
        {
            if (terms == null || terms.Count == 0) return "No additional material terms were announced.";
            return string.Join("\n", terms.Select(x => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(x.Key.Replace('_', ' ')) + ": " + FormatDirectorTerm(x.Value)));
        }

        private static string FormatDirectorTerm(object value)
        {
            if (value is ArrayList array) return string.Join(", ", array.Cast<object>().Select(Convert.ToString));
            if (value is IEnumerable<object> enumerable) return string.Join(", ", enumerable.Select(Convert.ToString));
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static ArrayList DirectorConsequenceTags(string command, bool accepted, Dictionary<string, object> terms)
        {
            ArrayList tags = new ArrayList { accepted ? "resolved" : "refused_offer" };
            if (command == "declare_war") tags.Add("aggression");
            if (IsDirectorPeaceCommand(command)) tags.Add("war_weariness");
            if (command.Contains("settlement") || command.Contains("surrender")) tags.Add("territorial_stakes");
            if (command.Contains("tribute") || command.Contains("reparations") || command.Contains("indemnity") || command.Contains("subsidy")) tags.Add("financial_burden");
            if (command == "break_treaty") tags.Add("broken_oath");
            if (command.Contains("alliance") || command.Contains("pact") || command.Contains("guarantee")) tags.Add("fulfilled_or_rejected_obligation");
            if (string.Equals(ReadString(terms, "treatyKind", ""), "alliance_package", StringComparison.OrdinalIgnoreCase)) tags.Add("fulfilled_or_rejected_obligation");
            return tags;
        }

        private static Dictionary<string, object> DirectorError(string error)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["status"] = "paused", ["error"] = DiplomacyErrorForDisplay(error) };
        }

        private static string DiplomacyErrorForDisplay(string error)
        {
            string raw = error ?? string.Empty;
            string lowered = raw.ToLowerInvariant();
            if (lowered.Contains("missing_api_key") || lowered.Contains("invalid authentication")
                || lowered.Contains("invalid_api_key") || lowered.Contains("unauthorized") || lowered.Contains("\"status\":401"))
            {
                return "The main LLM API key is missing or rejected. Open the Reign Control Center, save the key under Server Diagnostics, then run Test LLM Key.";
            }

            if (lowered.Contains("forbidden") || lowered.Contains("\"status\":403"))
            {
                return "The AI provider rejected the configured key, endpoint, or diplomacy model. Open Server Diagnostics and run Test LLM Key.";
            }

            return string.IsNullOrWhiteSpace(raw) ? "Unknown diplomacy director error." : raw;
        }

        private static Dictionary<string, object> DirectorRejected(string status, List<string> errors)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["status"] = status,
                ["errors"] = errors ?? new List<string>()
            };
        }

        private static Dictionary<string, object> DirectorValidationRejected(string status, List<string> errors)
        {
            Dictionary<string, object> result = DirectorRejected(status, errors);
            result["failureClass"] = "validation_failed";
            result["retryable"] = false;
            return result;
        }

        private static Dictionary<string, object> ReadDirectorState(string campaignId)
        {
            Dictionary<string, object> state = ReadJsonObject(CampaignFile(campaignId, "diplomacy", "director-state.json"));
            if (!state.ContainsKey("version")) state["version"] = 1;
            return state;
        }

        private static void SaveDirectorState(string campaignId, Dictionary<string, object> state)
        {
            lock (FileLock) WriteDirectorState(campaignId, state);
        }

        private static void WriteDirectorState(string campaignId, Dictionary<string, object> state)
        {
            WriteJsonObject(CampaignFile(campaignId, "diplomacy", "director-state.json"), state ?? new Dictionary<string, object>());
        }

        private static List<Dictionary<string, object>> ReadDiplomaticEventQueue(string campaignId)
        {
            string path = CampaignFile(campaignId, "diplomacy", "event-queue.json");
            if (!File.Exists(path)) return new List<Dictionary<string, object>>();
            try
            {
                return Json.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(path)) ?? new List<Dictionary<string, object>>();
            }
            catch
            {
                return new List<Dictionary<string, object>>();
            }
        }

        private static void WriteDiplomaticEventQueue(string campaignId, List<Dictionary<string, object>> queue)
        {
            string path = CampaignFile(campaignId, "diplomacy", "event-queue.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.Serialize(queue ?? new List<Dictionary<string, object>>()));
        }

        private static void WriteDirectorAudit(string campaignId, string phase, Dictionary<string, object> ruler, Dictionary<string, object> candidate, List<string> errors)
        {
            WriteAudit(campaignId, ReadString(candidate, "correlationId", ""), "server", "diplomacy", "diplomacy." + phase,
                ReadString(ruler, "leaderHeroId", ""), "", "", "failed", 0, "World diplomacy candidate failed validation.", new Dictionary<string, object>
                {
                    ["candidate"] = candidate,
                    ["errors"] = errors
                });
        }

        private static Dictionary<string, object> RunWorldDiplomacySelfTests()
        {
            List<Dictionary<string, object>> tests = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (name, passed, detail) => tests.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = passed, ["detail"] = detail });
            var payerActor = TestDirectorKingdom("payer_a", false);
            var payerTarget = TestDirectorKingdom("payer_b", false);
            payerActor["treasury"] = 2000000;
            payerActor["leaderGold"] = 100000;
            var paymentTerms = new Dictionary<string, object>
            {
                ["treatyKind"] = "alliance_package", ["durationDays"] = 84,
                ["gold"] = 650000, ["goldFromHeroStringId"] = "payer_a_ruler",
                ["goldToHeroStringId"] = "payer_b_ruler"
            };
            var payment = new Dictionary<string, object> { ["command"] = "diplomatic_package", ["terms"] = paymentTerms };
            Func<List<string>> paymentErrors = () =>
            {
                var errors = new List<string>();
                ValidateDirectorTerms(payment, new Dictionary<string, object>(), payerActor, payerTarget, errors);
                return errors;
            };
            add("package_rejects_rich_realm_poor_ruler", paymentErrors().Any(e => e.Contains("cannot afford")), "650000 cannot be paid from 100000 personal gold despite 2000000 realm wealth");
            paymentTerms["gold"] = 100000;
            add("package_accepts_exact_payer_balance", paymentErrors().Count == 0, "exact balance is executable");
            paymentTerms["gold"] = 100001;
            add("package_rechecks_raised_counteroffer", paymentErrors().Any(e => e.Contains("cannot afford")), "revised payment is checked again");
            payerActor.Remove("leaderGold");
            add("package_missing_payer_balance_fails_closed", paymentErrors().Any(e => e.Contains("unavailable")), "old snapshots cannot authorize money");
            paymentTerms["gold"] = 0;
            add("package_no_payment_needs_no_balance", paymentErrors().Count == 0, "unpaid alliance stays available");
            paymentTerms["gold"] = 100001;
            paymentTerms["goldFromHeroStringId"] = "payer_b_ruler";
            paymentTerms["goldToHeroStringId"] = "payer_a_ruler";
            add("package_reverse_payment_checks_target", paymentErrors().Any(e => e.StartsWith("target cannot afford")), "explicit reverse payment checks the actual payer");
            Dictionary<string, object> aggressive = TestDirectorVirtues(0d, 100d, 50d, 50d, 50d, 100d, 50d);
            Dictionary<string, object> peaceful = TestDirectorVirtues(100d, 0d, 100d, 100d, 100d, 0d, 100d);
            double aggressiveExpansion = DirectorPersonalityBaseline(DirectorIntentScore("expansion", aggressive));
            double peacefulExpansion = DirectorPersonalityBaseline(DirectorIntentScore("expansion", peaceful));
            double peacefulProsperity = DirectorPersonalityBaseline(DirectorIntentScore("prosperity", peaceful));
            add("aligned_family_baseline", Math.Abs(aggressiveExpansion - DirectorAlignedBaseline) < 0.00001d, aggressiveExpansion.ToString("0.0000"));
            add("opposed_family_baseline", Math.Abs(peacefulExpansion - DirectorOpposedBaseline) < 0.00001d, peacefulExpansion.ToString("0.0000"));
            add("auth_error_is_safe_for_game_ui",
                DiplomacyErrorForDisplay("{\"error\":{\"message\":\"Invalid Authentication\",\"code\":\"missing_api_key\",\"status\":401}}")
                    == "The main LLM API key is missing or rejected. Open the Reign Control Center, save the key under Server Diagnostics, then run Test LLM Key.",
                "provider JSON is replaced with an actionable local diagnostic");
            add("provider_gateway_error_is_retryable",
                IsTransientLlmFailure("{\"error\":{\"message\":\"Invalid request parameters. Please check your input and try again.\",\"type\":\"invalid_request_error\",\"code\":\"provider_error\"}}"),
                "The provider's generic gateway rejection is retried before diplomacy is paused.");
            Dictionary<string, object> validationRejection = DirectorValidationRejected("accepted_action_normalization_failed", new List<string> { "missing clan" });
            add("validation_failure_does_not_pause_ai",
                ReadBool(validationRejection, "ok", false)
                    && ReadString(validationRejection, "status", "") == "accepted_action_normalization_failed"
                    && ReadString(validationRejection, "failureClass", "") == "validation_failed"
                    && !ReadBool(validationRejection, "retryable", true),
                "world-data validation failures are recorded without masquerading as an AI-service outage");
            Dictionary<string, object> malformedDecisionFallback = MalformedDiplomacyDecisionFallback();
            Dictionary<string, object> malformedResponseFallback = MalformedDiplomacyResponseFallback();
            add("malformed_decision_becomes_safe_no_action",
                ReadBool(malformedDecisionFallback, "ok", false)
                    && ReadString(ReadDictionary(malformedDecisionFallback, "decision"), "decision", "") == "none",
                "an unrepaired malformed decision records a safe no-initiative evaluation instead of pausing diplomacy");
            add("malformed_response_becomes_safe_refusal",
                ReadBool(malformedResponseFallback, "ok", false)
                    && ReadString(ReadDictionary(malformedResponseFallback, "response"), "response", "") == "refuse",
                "an unrepaired malformed proposal response becomes a safe refusal instead of pausing diplomacy");
            Dictionary<string, object> dualOutcomeReasons = new Dictionary<string, object>
            {
                ["publicReason"] = "I accept these terms.",
                ["acceptReason"] = "These terms serve us well.",
                ["refuseReason"] = "These terms do not serve us."
            };
            add("authoritative_refusal_uses_refusal_narration",
                SelectDiplomaticOutcomeReason(dualOutcomeReasons, "accept", false,
                    "accepted fallback", "refused fallback") == "These terms do not serve us.",
                "a failed deterministic roll cannot publish acceptance narration");
            add("authoritative_acceptance_uses_acceptance_narration",
                SelectDiplomaticOutcomeReason(dualOutcomeReasons, "refuse", true,
                    "accepted fallback", "refused fallback") == "These terms serve us well.",
                "a passed deterministic roll cannot publish refusal narration");
            add("legacy_mismatched_narration_fails_closed",
                SelectDiplomaticOutcomeReason(new Dictionary<string, object>
                    { ["publicReason"] = "I accept these terms." }, "accept", false,
                    "accepted fallback", "refused fallback") == "refused fallback",
                "legacy publicReason is ignored when its reported outcome contradicts the authoritative roll");
            Dictionary<string, object> acknowledgedAnnouncement =
                new Dictionary<string, object>
                {
                    ["delivered"] = false, ["acknowledged"] = false
                };
            MarkDiplomaticEventAcknowledged(acknowledgedAnnouncement, 1234L);
            add("acknowledged_announcement_is_delivered",
                ReadBool(acknowledgedAnnouncement, "delivered", false)
                    && ReadBool(acknowledgedAnnouncement, "acknowledged", false)
                    && ReadLong(acknowledgedAnnouncement,
                        "deliveredTs", 0L) == 1234L,
                "client acknowledgement closes both delivery states instead of leaving a permanent false undelivered warning");
            ArrayList excessiveMarriageOptions = new ArrayList();
            for (int targetIndex = 0; targetIndex < 3; targetIndex++)
            {
                for (int optionIndex = 0; optionIndex < 20; optionIndex++)
                {
                    excessiveMarriageOptions.Add(new Dictionary<string, object>
                    {
                        ["kingdomAId"] = "perspective",
                        ["kingdomBId"] = "target_" + targetIndex.ToString(CultureInfo.InvariantCulture),
                        ["heroAId"] = "a_" + targetIndex.ToString(CultureInfo.InvariantCulture) + "_" + optionIndex.ToString(CultureInfo.InvariantCulture),
                        ["heroBId"] = "b_" + targetIndex.ToString(CultureInfo.InvariantCulture) + "_" + optionIndex.ToString(CultureInfo.InvariantCulture)
                    });
                }
            }
            excessiveMarriageOptions.Add(new Dictionary<string, object>
            {
                ["kingdomAId"] = "unrelated_a", ["kingdomBId"] = "unrelated_b",
                ["heroAId"] = "unrelated_hero_a", ["heroBId"] = "unrelated_hero_b"
            });
            Dictionary<string, object> compactMarriageWorld = CompactDirectorWorld(
                new Dictionary<string, object> { ["allianceMarriageOptions"] = excessiveMarriageOptions },
                "perspective");
            List<Dictionary<string, object>> compactMarriageRows = ReadDictionaryList(compactMarriageWorld, "allianceMarriageOptions");
            add("marriage_prompt_uses_one_server_selected_pair_per_counterparty",
                compactMarriageRows.Count == 3
                    && compactMarriageRows.All(x => DirectorMarriageOptionInvolvesKingdom(x, "perspective")),
                compactMarriageRows.Count.ToString(CultureInfo.InvariantCulture) + " ideal pairs selected from " + excessiveMarriageOptions.Count.ToString(CultureInfo.InvariantCulture));
            Dictionary<string, object> counterpartMarriageWorld = CompactDirectorWorld(
                new Dictionary<string, object> { ["allianceMarriageOptions"] = excessiveMarriageOptions },
                "perspective",
                "target_1");
            add("responder_marriage_options_are_pair_scoped",
                ReadDictionaryList(counterpartMarriageWorld, "allianceMarriageOptions").Count == 1
                    && ReadDictionaryList(counterpartMarriageWorld, "allianceMarriageOptions")
                        .All(x => DirectorMarriageOptionInvolvesKingdom(x, "target_1")),
                "Responder prompts include only the two negotiating kingdoms.");
            Dictionary<string, object> idealCandidateWorld = new Dictionary<string, object>
            {
                ["allianceMarriageCandidates"] = new ArrayList
                {
                    new Dictionary<string, object> { ["heroId"] = "ordinary_a", ["heroName"] = "Ordinary A", ["kingdomId"] = "a", ["clanId"] = "clan_a", ["age"] = 30d, ["isFemale"] = false, ["clanTier"] = 4 },
                    new Dictionary<string, object> { ["heroId"] = "dynastic_a", ["heroName"] = "Dynastic A", ["kingdomId"] = "a", ["clanId"] = "clan_a", ["age"] = 25d, ["isFemale"] = false, ["clanTier"] = 4, ["isRulerChild"] = true, ["successionRank"] = 2 },
                    new Dictionary<string, object> { ["heroId"] = "ordinary_b", ["heroName"] = "Ordinary B", ["kingdomId"] = "b", ["clanId"] = "clan_b", ["age"] = 29d, ["isFemale"] = true, ["clanTier"] = 4 },
                    new Dictionary<string, object> { ["heroId"] = "dynastic_b", ["heroName"] = "Dynastic B", ["kingdomId"] = "b", ["clanId"] = "clan_b", ["age"] = 24d, ["isFemale"] = true, ["clanTier"] = 4, ["isRulerChild"] = true, ["successionRank"] = 2 }
                }
            };
            Dictionary<string, object> idealCandidatePair = SelectIdealDirectorMarriageOption(idealCandidateWorld, "a", "b");
            add("server_selects_highest_value_dynastic_couple",
                ReadString(idealCandidatePair, "heroAId", "") == "dynastic_a"
                    && ReadString(idealCandidatePair, "heroBId", "") == "dynastic_b"
                    && DirectorMarriageOptionExists(idealCandidateWorld, "a", "b", "dynastic_a", "dynastic_b"),
                Json.Serialize(idealCandidatePair));
            add("peaceful_ruler_favors_prosperity", peacefulProsperity > peacefulExpansion, peacefulProsperity.ToString("0.0000") + " > " + peacefulExpansion.ToString("0.0000"));
            add("neutral_family_baseline", Math.Abs(DirectorPersonalityBaseline(50d) - DirectorNeutralBaseline) < 0.00001d, DirectorPersonalityBaseline(50d).ToString("0.0000"));
            add("zero_score_baseline", Math.Abs(DirectorPersonalityBaseline(0d) - DirectorOpposedBaseline) < 0.00001d, DirectorPersonalityBaseline(0d).ToString("0.0000"));
            add("hundred_score_baseline", Math.Abs(DirectorPersonalityBaseline(100d) - DirectorAlignedBaseline) < 0.00001d, DirectorPersonalityBaseline(100d).ToString("0.0000"));
            Dictionary<string, object> weighted = TestDirectorVirtues(20d, 80d, 10d, 30d, 40d, 40d, 70d);
            add("expansion_exact_weights", Math.Abs(DirectorIntentScore("expansion", weighted) - 72d) < 0.00001d, DirectorIntentScore("expansion", weighted).ToString("0.00"));
            add("prosperity_exact_weights", Math.Abs(DirectorIntentScore("prosperity", weighted) - 54.5d) < 0.00001d, DirectorIntentScore("prosperity", weighted).ToString("0.00"));
            add("security_exact_weights", Math.Abs(DirectorIntentScore("security", weighted) - 42.5d) < 0.00001d, DirectorIntentScore("security", weighted).ToString("0.00"));
            add("peace_exact_weights", Math.Abs(DirectorIntentScore("peace", weighted) - 37d) < 0.00001d, DirectorIntentScore("peace", weighted).ToString("0.00"));
            Dictionary<string, object> onlyCompassionChanged = TestDirectorVirtues(80d, 80d, 10d, 30d, 40d, 40d, 70d);
            add("expansion_inverts_compassion", Math.Abs(DirectorIntentScore("expansion", weighted) - DirectorIntentScore("expansion", onlyCompassionChanged) - 36d) < 0.00001d, "raising Compassion by 60 lowers Expansion by 36");
            Dictionary<string, object> assessment = DirectorIntentAssessment("expansion", weighted);
            Dictionary<string, object> contributors = ReadDictionary(assessment, "contributors") ?? new Dictionary<string, object>();
            add("initiative_uses_only_main_groups", contributors.Count == 3 && contributors.Keys.All(DirectorCourtVirtueKeys.Contains), string.Join(",", contributors.Keys));
            Dictionary<string, object> missingProfile = LoadDirectorTraits("__director_self_test_missing_campaign__", new Dictionary<string, object> { ["leaderHeroId"] = "__director_self_test_missing_hero__" });
            add("missing_profile_is_neutral", missingProfile.Count == DirectorCourtVirtueKeys.Length && DirectorCourtVirtueKeys.All(x => Math.Abs(ReadDouble(missingProfile, x, -1d) - 50d) < 0.00001d), Json.Serialize(missingProfile));
            add("defensive_war_not_expansion", DirectorIntentCommands["expansion"].Contains("declare_war") && !DirectorIntentCommands["security"].Contains("declare_war"), "Security cannot emit a conquest declaration");
            add("opportunity_blends_to_six_percent", Math.Abs(BlendDirectorChance(DirectorOpposedBaseline, DirectorOpportunityCeiling, 1d) - DirectorOpportunityCeiling) < 0.00001d, DirectorOpportunityCeiling.ToString("0.0000"));
            add("peace_curve_is_nonlinear", Math.Abs(BlendDirectorChance(DirectorNeutralBaseline, DirectorPeaceCeiling, Math.Pow(0.5d, 2d)) - 0.04125d) < 0.00001d, "50 fatigue yields 4.125 percent");
            add("war_fatigue_remains_strategic", Math.Abs(EffectiveWarFatigue(50d) - 50d) < 0.00001d, "personality is applied through the Peace score, not folded into objective fatigue");
            add("retired_actions_excluded", !DirectorSupportedCommands.Contains("sign_temporary_truce") && !DirectorSupportedCommands.Contains("trade_embargo") && DirectorSupportedCommands.Contains("caravan_protection_agreement"), "Only temporary truce and trade embargo remain retired; caravan protection is supported.");
            add("stable_roll", Math.Abs(StableDirectorRoll("campaign", 10, "ruler") - StableDirectorRoll("campaign", 10, "ruler")) < double.Epsilon, "seeded result repeats");
            add("supported_commands_map_to_actions", DirectorSupportedCommands.All(x => !string.IsNullOrWhiteSpace(MapCommandToActionType(x))), "all " + DirectorSupportedCommands.Count + " supported commands map to action types");
            add("harsh_peace_terms_remain_positive",
                ClassifyRulerDiplomacyPolarity(new Dictionary<string, object>
                    { ["command"] = "demand_reparations_peace" }) == "positive"
                && ClassifyRulerDiplomacyPolarity(new Dictionary<string, object>
                    { ["command"] = "demand_settlement_peace" }) == "positive"
                && ClassifyRulerDiplomacyPolarity(new Dictionary<string, object>
                    { ["command"] = "demand_surrender_peace" }) == "positive",
                "Ending an active war is conciliatory for opportunity routing while harsh terms remain part of willingness and counteroffer logic.");
            AddRandomDiplomacyDebugSelfTests(add);

            Dictionary<string, object> weakPowerKingdom = TestDirectorKingdom("weak", false);
            Dictionary<string, object> strongPowerKingdom = TestDirectorKingdom("strong", false);
            Dictionary<string, object> nordPowerKingdom = TestDirectorKingdom("nords", false);
            weakPowerKingdom["strength"] = 500d; weakPowerKingdom["fiefCount"] = 1; weakPowerKingdom["settlementProsperity"] = 2000d; weakPowerKingdom["garrisonStrength"] = 200d; weakPowerKingdom["clanCount"] = 2; weakPowerKingdom["dailyGoldChange"] = -500d;
            strongPowerKingdom["strength"] = 2000d; strongPowerKingdom["fiefCount"] = 6; strongPowerKingdom["settlementProsperity"] = 18000d; strongPowerKingdom["garrisonStrength"] = 1800d; strongPowerKingdom["clanCount"] = 8; strongPowerKingdom["dailyGoldChange"] = 1000d;
            nordPowerKingdom["strength"] = 1200d; nordPowerKingdom["fiefCount"] = 3; nordPowerKingdom["settlementProsperity"] = 7000d; nordPowerKingdom["garrisonStrength"] = 700d; nordPowerKingdom["clanCount"] = 4; nordPowerKingdom["dailyGoldChange"] = 200d;
            Dictionary<string, object> powerWorld = new Dictionary<string, object> { ["kingdoms"] = new ArrayList { weakPowerKingdom, strongPowerKingdom, nordPowerKingdom }, ["settlements"] = new ArrayList(), ["allianceMarriageOptions"] = new ArrayList() };
            powerWorld["nationalPowerAssessments"] = BuildNationalPowerAssessments(powerWorld, new Dictionary<string, object>());
            add("custom_nords_receive_power_assessment", ReadDictionaryList(powerWorld, "nationalPowerAssessments").Any(x => ReadString(x, "kingdomId", "") == "nords"), "dynamic nords kingdom included");
            add("composite_power_rewards_full_strength", DirectorPowerScore(powerWorld, "strong", 0d) > DirectorPowerScore(powerWorld, "weak", 0d), "military, territory, economy, and clans contribute");
            double nordReadyPower = DirectorPowerScore(powerWorld, "nords", 0d);
            nordPowerKingdom["enemies"] = new ArrayList { "strong" };
            nordPowerKingdom["rulerIsPrisoner"] = true;
            nordPowerKingdom["dailyGoldChange"] = -5000d;
            Dictionary<string, object> burdenedState = new Dictionary<string, object> { ["wars"] = new Dictionary<string, object> { ["nords|strong"] = new Dictionary<string, object> { ["active"] = true, ["kingdomAId"] = "nords", ["kingdomBId"] = "strong", ["fatigueA"] = 80d, ["fatigueB"] = 10d } } };
            powerWorld["nationalPowerAssessments"] = BuildNationalPowerAssessments(powerWorld, burdenedState);
            add("war_readiness_reduces_power", DirectorPowerScore(powerWorld, "nords", 0d) < nordReadyPower, "war, fatigue, captivity, and negative income reduce usable power");
            nordPowerKingdom["enemies"] = new ArrayList(); nordPowerKingdom["rulerIsPrisoner"] = false; nordPowerKingdom["dailyGoldChange"] = 200d;

            powerWorld["nationalPowerAssessments"] = new ArrayList
            {
                new Dictionary<string, object> { ["kingdomId"] = "weak", ["score"] = 0.60d },
                new Dictionary<string, object> { ["kingdomId"] = "strong", ["score"] = 1.00d },
                new Dictionary<string, object> { ["kingdomId"] = "nords", ["score"] = 1.60d }
            };
            Dictionary<string, object> bareAlliance = new Dictionary<string, object> { ["command"] = "sign_alliance", ["actorKingdomStringId"] = "weak", ["terms"] = new Dictionary<string, object>() };
            Dictionary<string, object> weakLeverage = BuildAllianceLeverage(powerWorld, weakPowerKingdom, strongPowerKingdom, bareAlliance);
            Dictionary<string, object> strongLeverage = BuildAllianceLeverage(powerWorld, nordPowerKingdom, strongPowerKingdom, bareAlliance);
            add("much_weaker_alliance_modifier", ReadInt(weakLeverage, "powerModifier", 0) == -15 && ReadInt(weakLeverage, "requiredConsideration", 0) == 20, "0.60 ratio requires major consideration");
            add("bare_weak_alliance_fails_consideration", !ReadBool(weakLeverage, "considerationRequirementMet", true), "bare weak offer cannot pass");
            add("dominant_alliance_modifier", ReadInt(strongLeverage, "powerModifier", 0) == 15 && ReadInt(strongLeverage, "requiredConsideration", -1) == 0, "1.60 ratio gains 15 points");
            Dictionary<string, object> weakAssessment = ReadDictionaryList(powerWorld, "nationalPowerAssessments").First(x => ReadString(x, "kingdomId", "") == "weak");
            bool bandsExact = true;
            foreach (Tuple<double, int, int> bandCase in new[] { Tuple.Create(0.66d, -15, 20), Tuple.Create(0.67d, -8, 10), Tuple.Create(0.85d, 0, 0), Tuple.Create(1.15d, 8, 0), Tuple.Create(1.50d, 15, 0) })
            {
                weakAssessment["score"] = bandCase.Item1;
                Dictionary<string, object> band = BuildAllianceLeverage(powerWorld, weakPowerKingdom, strongPowerKingdom, bareAlliance);
                bandsExact &= ReadInt(band, "powerModifier", 999) == bandCase.Item2 && ReadInt(band, "requiredConsideration", 999) == bandCase.Item3;
            }
            add("alliance_ratio_boundaries", bandsExact, "0.67, 0.85, 1.15, and 1.50 boundaries are exact");
            weakAssessment["score"] = 0.60d;
            Dictionary<string, object> marriageTerms = new Dictionary<string, object> { ["treatyKind"] = "alliance_package", ["marriageHero1StringId"] = "weak_spare", ["marriageHero2StringId"] = "strong_spare" };
            powerWorld["allianceMarriageOptions"] = new ArrayList { new Dictionary<string, object> { ["kingdomAId"] = "weak", ["kingdomBId"] = "strong", ["heroAId"] = "weak_spare", ["heroBId"] = "strong_spare" } };
            Dictionary<string, object> marriageAlliance = new Dictionary<string, object> { ["command"] = "diplomatic_package", ["actorKingdomStringId"] = "weak", ["terms"] = marriageTerms };
            Dictionary<string, object> marriageLeverage = BuildAllianceLeverage(powerWorld, weakPowerKingdom, strongPowerKingdom, marriageAlliance);
            add("marriage_satisfies_major_consideration", Math.Abs(ReadDouble(marriageLeverage, "considerationValue", 0d) - 20d) < 0.001d && ReadBool(marriageLeverage, "considerationRequirementMet", false), "valid marriage contributes 20 points");
            marriageTerms["guaranteeIndependence"] = true; marriageTerms["militaryCommitment"] = true;
            add("consideration_value_caps_at_thirty", Math.Abs(ReadDouble(BuildAllianceLeverage(powerWorld, weakPowerKingdom, strongPowerKingdom, marriageAlliance), "considerationValue", 0d) - 30d) < 0.001d, "compound consideration capped at 30");
            marriageTerms.Remove("guaranteeIndependence"); marriageTerms.Remove("militaryCommitment");
            ResolveAllianceAcceptance("test", 5d, powerWorld, weakPowerKingdom, strongPowerKingdom, marriageAlliance, new Dictionary<string, object> { ["baseWillingness"] = 50d }, false, out Dictionary<string, object> allianceAudit);
            add("alliance_final_chance_formula", Math.Abs(ReadDouble(allianceAudit, "finalChance", 0d) - 55d) < 0.001d, "50 base - 15 power + 20 marriage = 55");

            Dictionary<string, object> kingdomA = TestDirectorKingdom("a", false, "b");
            Dictionary<string, object> kingdomB = TestDirectorKingdom("b", false, "a");
            kingdomA["civilWarOpponentKingdomId"] = "b";
            kingdomB["civilWarOpponentKingdomId"] = "a";
            Dictionary<string, object> playerKingdom = TestDirectorKingdom("player", true);
            Dictionary<string, object> testWorld = new Dictionary<string, object>
            {
                ["playerKingdomId"] = "player",
                ["kingdoms"] = new ArrayList { kingdomA, kingdomB, playerKingdom },
                ["clans"] = new ArrayList
                {
                    new Dictionary<string, object> { ["clanId"] = "clan_a", ["name"] = "Clan A", ["leaderHeroId"] = ReadString(kingdomA, "leaderHeroId", ""), ["kingdomId"] = "a" },
                    new Dictionary<string, object> { ["clanId"] = "clan_b", ["name"] = "Clan B", ["leaderHeroId"] = ReadString(kingdomB, "leaderHeroId", ""), ["kingdomId"] = "b" }
                },
                ["settlements"] = new ArrayList(),
                ["agreements"] = new ArrayList()
            };
            Dictionary<string, object> normalizationPayload = BuildDirectorNormalizationPayload(testWorld, kingdomA);
            Dictionary<string, object> normalizationIndex = ReadDictionary(normalizationPayload, "actionResolutionIndex") ?? new Dictionary<string, object>();
            add("normalization_carries_clan_index",
                ReadDictionaryList(normalizationIndex, "clans").Count == 2,
                "diplomacy validation receives the authoritative native clan records");
            Dictionary<string, object> ransomCandidate = BuildDirectorCandidate(
                new Dictionary<string, object>
                {
                    ["command"] = "ransom_package",
                    ["targetKingdomId"] = "b",
                    ["terms"] = new Dictionary<string, object>
                    {
                        ["gold"] = 20000
                    },
                    ["publicReason"] = "a ruler offered b ruler a broad ransom for captive nobles."
                }, kingdomA);
            ApplyDirectorTargetIdentity(ransomCandidate, kingdomB);
            Dictionary<string, object> ransomNormalizationWorld =
                new Dictionary<string, object>(testWorld,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["settlements"] = new ArrayList
                    {
                        new Dictionary<string, object>
                        {
                            ["settlementId"] = "town_a",
                            ["name"] = "Town A",
                            ["ownerKingdomId"] = "a"
                        }
                    }
                };
            Dictionary<string, object> normalizedRansom = NormalizeActionCommand(
                ransomCandidate, "director_ransom_selftest",
                out List<string> ransomErrors,
                BuildDirectorNormalizationPayload(ransomNormalizationWorld,
                    kingdomA),
                ReadString(ransomCandidate, "publicReason", ""));
            Dictionary<string, object> normalizedRansomTerms = normalizedRansom == null
                ? new Dictionary<string, object>()
                : Json.Deserialize<Dictionary<string, object>>(
                    ReadString(normalizedRansom, "termsJson", "{}"))
                    ?? new Dictionary<string, object>();
            add("autonomous_ransom_keeps_ruler_and_prisoner_roles_separate",
                normalizedRansom != null && ransomErrors.Count == 0
                && ReadString(normalizedRansom, "actorHeroStringId", "")
                    == "a_ruler"
                && ReadString(normalizedRansom, "targetHeroStringId", "")
                    == "b_ruler"
                && !normalizedRansomTerms.ContainsKey("prisonerHeroStringId"),
                "A broad ruler-to-ruler ransom cannot infer a named prisoner from its public explanation or overwrite the responding ruler identity.");
            Dictionary<string, object> playerCandidate = new Dictionary<string, object>
            {
                ["command"] = "sign_trade_agreement", ["actorKingdomStringId"] = "a", ["targetKingdomStringId"] = "player",
                ["publicReason"] = "test", ["confidence"] = 1d, ["terms"] = new Dictionary<string, object>()
            };
            add("player_proposal_target_allowed", !ValidateDirectorCandidate(playerCandidate, testWorld, "player", "prosperity", "").Any(x => x.Contains("player kingdom") || x.Contains("cannot decide for the player")), "NPC proposal may target the player court");
            Dictionary<string, object> playerWarCandidate = new Dictionary<string, object>(playerCandidate, StringComparer.OrdinalIgnoreCase)
            {
                ["command"] = "declare_war"
            };
            add("player_unilateral_target_allowed",
                !ValidateDirectorCandidate(playerWarCandidate, testWorld, "player", "expansion", "").Any(x =>
                    x.Contains("cannot target the player kingdom")),
                "NPC unilateral diplomacy may target the player while the player remains excluded as an autonomous actor.");
            Dictionary<string, object> playerActorCandidate = new Dictionary<string, object>(playerCandidate, StringComparer.OrdinalIgnoreCase)
            {
                ["actorKingdomStringId"] = "player", ["targetKingdomStringId"] = "a"
            };
            add("player_actor_never_autonomous", ValidateDirectorCandidate(playerActorCandidate, testWorld, "player", "prosperity", "").Any(x => x.Contains("cannot decide for the player")), "director never answers or acts for player ruler");
            Dictionary<string, object> civilWarPeaceCandidate =
                new Dictionary<string, object>
                {
                    ["command"] = "demand_surrender_peace",
                    ["actorKingdomStringId"] = "a",
                    ["targetKingdomStringId"] = "b",
                    ["publicReason"] = "test", ["confidence"] = 1d,
                    ["terms"] = new Dictionary<string, object>
                    {
                        ["allTargetFortifications"] = true
                    }
                };
            add("generic_civil_war_peace_is_rejected",
                IsDirectorCivilWarPair(testWorld, "a", "b")
                && ValidateDirectorCandidate(civilWarPeaceCandidate, testWorld,
                    "player", "peace", "b").Any(x =>
                        x.Contains("explicit negotiated political result")),
                "A normal diplomacy action cannot end a civil war; only the dedicated recognition settlement can do so.");

            ((ArrayList)testWorld["agreements"]).Add(new Dictionary<string, object> { ["kind"] = "non_aggression_pact", ["actorKingdomId"] = "a", ["targetKingdomId"] = "b" });
            Dictionary<string, object> pactCandidate = new Dictionary<string, object>
            {
                ["command"] = "declare_war", ["actorKingdomStringId"] = "a", ["targetKingdomStringId"] = "b",
                ["publicReason"] = "test", ["confidence"] = 1d, ["terms"] = new Dictionary<string, object>()
            };
            kingdomA["enemies"] = new ArrayList();
            kingdomB["enemies"] = new ArrayList();
            add("war_atomically_breaks_incompatible_pacts", !ValidateDirectorCandidate(pactCandidate, testWorld, "player", "expansion", "").Any(x => x.Contains("pact")), "native declaration records and penalizes every incompatible agreement breach");
            TreatyBreachPenalties("trade_agreement", out int tradeBetrayed, out int tradeBreaker);
            TreatyBreachPenalties("non_aggression_pact", out int napBetrayed, out int napBreaker);
            TreatyBreachPenalties("defensive_pact", out int defenseBetrayed, out int defenseBreaker);
            add("treaty_breach_relationship_values", tradeBetrayed == -20 && tradeBreaker == -5
                && napBetrayed == -40 && napBreaker == -10 && defenseBetrayed == -50 && defenseBreaker == -10,
                "trade, non-aggression, and defensive breach directions use approved penalties");
            add("war_breach_transaction_cap", Math.Max(-100, -50 + napBetrayed + defenseBetrayed) == -100
                && Math.Max(-100, -10 + napBreaker + defenseBreaker) == -30,
                "declaration consequences stack per agreement and clamp at -100 per direction");
            Dictionary<string, object> tradeCandidate = new Dictionary<string, object>(pactCandidate, StringComparer.OrdinalIgnoreCase) { ["command"] = "sign_trade_agreement" };
            add("intent_rejects_wrong_action_family", ValidateDirectorCandidate(tradeCandidate, testWorld, "player", "expansion", "").Any(x => x.Contains("intent family")), "trade rejected from expansion evaluation");

            Dictionary<string, object> multiWarActor = TestDirectorKingdom("multi", false, "enemy");
            Dictionary<string, object> existingEnemy = TestDirectorKingdom("enemy", false, "multi");
            Dictionary<string, object> proposedEnemy = TestDirectorKingdom("proposed", false);
            existingEnemy["strength"] = 1000d;
            proposedEnemy["strength"] = 600d;
            Dictionary<string, object> multiWarWorld = new Dictionary<string, object>
            {
                ["kingdoms"] = new ArrayList { multiWarActor, existingEnemy, proposedEnemy },
                ["settlements"] = new ArrayList(), ["agreements"] = new ArrayList()
            };
            Dictionary<string, object> secondWarCandidate = new Dictionary<string, object>
            {
                ["command"] = "declare_war", ["actorKingdomStringId"] = "multi", ["targetKingdomStringId"] = "proposed",
                ["publicReason"] = "test", ["confidence"] = 1d, ["terms"] = new Dictionary<string, object>()
            };
            multiWarActor["strength"] = 1600d;
            add("equal_combined_power_blocks_second_war", ValidateDirectorCandidate(secondWarCandidate, multiWarWorld, "", "expansion", "").Any(x => x.Contains("stronger than all existing enemies")), "1600 cannot attack combined strength 1600");
            multiWarActor["strength"] = 1601d;
            add("greater_combined_power_allows_second_war", !ValidateDirectorCandidate(secondWarCandidate, multiWarWorld, "", "expansion", "").Any(x => x.Contains("stronger than all existing enemies")), "1601 may attack combined strength 1600");
            Dictionary<string, object> wrongPolarityPressureCandidate =
                new Dictionary<string, object>(secondWarCandidate,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["targetKingdomStringId"] = "enemy",
                    ["targetKingdomId"] = "enemy",
                    ["politicalPressureId"] = "multi->enemy",
                    ["requiredPolarity"] = "positive"
                };
            List<string> wrongPolarityErrors = ValidateDirectorCandidate(
                wrongPolarityPressureCandidate, multiWarWorld, "", "peace",
                "enemy");
            Dictionary<string, object> pressureFallback =
                BuildPoliticalPressurePeaceFallback(
                    wrongPolarityPressureCandidate, multiWarActor,
                    existingEnemy);
            add("political_pressure_gets_one_clean_peace_fallback",
                ShouldFallbackPoliticalPressurePeace(
                    wrongPolarityPressureCandidate, "peace",
                    wrongPolarityErrors)
                && ReadString(pressureFallback, "command", "")
                    == "make_peace"
                && ValidateDirectorCandidate(pressureFallback,
                    multiWarWorld, "", "peace", "enemy").Count == 0,
                "A polarity-invalid pressure selection retries once as an executable clean-peace proposal.");

            Dictionary<string, object> baseline = new Dictionary<string, object>
            {
                ["startedDay"] = 0d, ["baselineStrengthA"] = 1000d, ["baselineStrengthB"] = 1000d,
                ["baselineTreasuryA"] = 100000d, ["baselineTreasuryB"] = 100000d,
                ["baselineFiefsA"] = 4, ["baselineFiefsB"] = 4
            };
            Dictionary<string, object> quietWar = new Dictionary<string, object> { ["casualtiesA"] = 0, ["casualtiesB"] = 0, ["raidsA"] = 0, ["raidsB"] = 0 };
            Dictionary<string, object> costlyWar = new Dictionary<string, object> { ["casualtiesA"] = 800, ["casualtiesB"] = 0, ["raidsA"] = 0, ["raidsB"] = 5 };
            Dictionary<string, object> stableSide = TestDirectorKingdom("a", false, "b");
            stableSide["strength"] = 1000d; stableSide["treasury"] = 100000; stableSide["fiefCount"] = 4;
            Dictionary<string, object> stressedSide = new Dictionary<string, object>(stableSide, StringComparer.OrdinalIgnoreCase) { ["strength"] = 300d, ["treasury"] = 10000, ["fiefCount"] = 2, ["rulerIsPrisoner"] = true, ["capturedNobleCount"] = 2 };
            double quietFatigue = CalculateObjectiveFatigue(baseline, quietWar, stableSide, kingdomB, true, 30d);
            double costlyFatigue = CalculateObjectiveFatigue(baseline, costlyWar, stressedSide, kingdomB, true, 30d);
            add("war_losses_raise_fatigue", costlyFatigue > quietFatigue, costlyFatigue.ToString("0.0") + " > " + quietFatigue.ToString("0.0"));

            Dictionary<string, object> original = new Dictionary<string, object>
            {
                ["command"] = "demand_settlement_peace",
                ["actorKingdomStringId"] = "multi",
                ["targetKingdomId"] = "enemy",
                ["targetKingdomStringId"] = "enemy",
                ["targetSettlementId"] = "town_old",
                ["targetSettlementStringId"] = "town_old",
                ["terms"] = new Dictionary<string, object>
                {
                    ["settlementIds"] = new ArrayList { "town_old" },
                    ["reparationsGold"] = 1000
                }
            };
            Dictionary<string, object> counter = new Dictionary<string, object>
            {
                ["command"] = "make_peace",
                ["targetSettlementId"] = "",
                ["terms"] = new Dictionary<string, object>(),
                ["publicReason"] = "No land changes hands."
            };
            Dictionary<string, object> replacement = ApplyCounteroffer(original,
                counter);
            add("counteroffer_is_complete_replacement",
                ReadString(replacement, "command", "") == "make_peace"
                    && string.IsNullOrWhiteSpace(ReadString(replacement,
                        "targetSettlementId", ""))
                    && (ReadDictionary(replacement, "terms")
                        ?? new Dictionary<string, object>()).Count == 0,
                "ordinary peace cannot inherit the original settlement or payment");
            Dictionary<string, object> missingCommandCounter =
                ApplyCounteroffer(original, new Dictionary<string, object>
                {
                    ["terms"] = new Dictionary<string, object>(),
                    ["publicReason"] = "A vague revision."
                });
            add("counteroffer_without_command_fails_closed",
                ValidateDirectorCandidate(missingCommandCounter, multiWarWorld,
                    "", "peace", "enemy").Any(x =>
                        x.StartsWith("unsupported command",
                            StringComparison.OrdinalIgnoreCase)),
                "a counter must identify its executable replacement command");
            add("invalid_counteroffer_narrates_polarity_failure",
                InvalidCounterofferPublicReason(missingCommandCounter,
                    new List<string>
                    {
                        "relationship bonus requires positive polarity but the proposed action is negative"
                    }).IndexOf("reversed the purpose",
                        StringComparison.OrdinalIgnoreCase) >= 0,
                "a safely rejected polarity reversal receives a specific public explanation");

            Dictionary<string, object> emptySettlementPeace =
                new Dictionary<string, object>(replacement,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["command"] = "demand_settlement_peace"
                };
            add("settlement_peace_requires_asset",
                ValidateDirectorCandidate(emptySettlementPeace, multiWarWorld,
                    "", "peace", "enemy").Any(x =>
                        x.Contains("at least one explicit settlement")),
                "an empty territorial demand is rejected");
            Dictionary<string, object> emptyReparations =
                new Dictionary<string, object>(replacement,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["command"] = "demand_reparations_peace"
                };
            add("reparations_require_value",
                ValidateDirectorCandidate(emptyReparations, multiWarWorld,
                    "", "peace", "enemy").Any(x =>
                        x.Contains("positive reparationsGold")),
                "an empty financial demand is rejected");
            Dictionary<string, object> emptyRansom =
                new Dictionary<string, object>(replacement,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["command"] = "ransom_package"
                };
            add("ransom_requires_payment",
                ValidateDirectorCandidate(emptyRansom, multiWarWorld,
                    "", "peace", "enemy").Any(x =>
                        x.Contains("requires positive gold")),
                "an empty ransom package is rejected");

            Dictionary<string, object> compactScopeWorld =
                new Dictionary<string, object>
                {
                    ["kingdoms"] = new ArrayList
                    {
                        TestDirectorKingdom("scope_a", false),
                        TestDirectorKingdom("scope_b", false),
                        TestDirectorKingdom("scope_c", false)
                    },
                    ["settlements"] = new ArrayList(),
                    ["wars"] = new ArrayList(), ["agreements"] = new ArrayList(),
                    ["relations"] = new ArrayList(), ["prisoners"] = new ArrayList(),
                    ["recentDiplomaticEvents"] = new ArrayList(),
                    ["nationalPowerAssessments"] = new ArrayList(),
                    ["allianceMarriageOptions"] = new ArrayList()
                };
            ArrayList scopeSettlements = (ArrayList)compactScopeWorld["settlements"];
            foreach (string owner in new[] { "scope_a", "scope_b", "scope_c" })
            {
                for (int index = 0; index < 12; index++)
                    scopeSettlements.Add(new Dictionary<string, object>
                    {
                        ["settlementId"] = owner + "_" + index,
                        ["ownerKingdomId"] = owner,
                        ["isFortification"] = index < 6,
                        ["garrison"] = 100 + index,
                        ["loyalty"] = 50d, ["prosperity"] = 1000d - index
                    });
            }
            Dictionary<string, object> pairScope = CompactDirectorWorld(
                compactScopeWorld, "scope_a", "scope_b", "peace");
            add("response_prompt_is_bilateral",
                ReadDictionaryList(pairScope, "kingdoms").Count == 2
                    && ReadDictionaryList(pairScope, "settlements").All(x =>
                        !ReadString(x, "ownerKingdomId", "").Equals("scope_c",
                            StringComparison.OrdinalIgnoreCase))
                    && ReadDictionaryList(pairScope,
                        "allianceMarriageOptions").Count == 0,
                "responses carry only the two kingdoms and intent-relevant data");
            Dictionary<string, object> broadScope = CompactDirectorWorld(
                compactScopeWorld, "scope_a", "", "expansion");
            add("initial_prompt_bounds_settlement_summaries",
                ReadDictionaryList(broadScope, "settlements").Count <= 14
                    && ReadDictionaryList(broadScope,
                        "allianceMarriageOptions").Count == 0,
                "perspective assets are capped at eight and each possible target at three");

            int totalSimulatedEvents = 0;
            const int simulatedCampaigns = 200;
            for (int campaign = 0; campaign < simulatedCampaigns; campaign++)
            {
                Dictionary<string, int> cooldownUntil = Enumerable.Range(0, 8).ToDictionary(x => "ruler" + x, x => 0);
                for (int simulatedDay = 1; simulatedDay <= DirectorCampaignDaysPerYear; simulatedDay += DirectorEvaluationIntervalDays)
                {
                    bool resolved = false;
                    foreach (string rulerId in cooldownUntil.Keys.ToList())
                    {
                        if (simulatedDay < cooldownUntil[rulerId]) continue;
                        foreach (string family in new[] { "expansion", "prosperity", "security" })
                        {
                            if (StableDirectorRoll("simulated_campaign_" + campaign, simulatedDay, rulerId + "|" + family) >= DirectorNeutralBaseline) continue;
                            totalSimulatedEvents++;
                            cooldownUntil[rulerId] = simulatedDay + 15;
                            resolved = true;
                            break;
                        }
                        if (resolved) break;
                    }
                }
            }
            double meanSimulatedEvents = totalSimulatedEvents / (double)simulatedCampaigns;
            double meanEventInterval = DirectorCampaignDaysPerYear / meanSimulatedEvents;
            add("reign_calendar_year_length", DirectorCampaignDaysPerYear == 126, DirectorCampaignDaysPerYear + " campaign days per Reign year");
            add("three_day_evaluation_cadence", DirectorEvaluationIntervalDays == 3, "evaluates every " + DirectorEvaluationIntervalDays + " days");
            add("seeded_year_pacing", meanEventInterval >= 9d && meanEventInterval <= 11d, meanSimulatedEvents.ToString("0.00") + " events; one every " + meanEventInterval.ToString("0.00") + " days");
            tests.AddRange(RunClanAccordSelfTests());
            tests.AddRange(RunClanAccordServerSelfTests());
            bool passedAll = tests.All(x => ReadBool(x, "passed", false));
            return new Dictionary<string, object> { ["passed"] = passedAll, ["count"] = tests.Count, ["tests"] = tests };
        }

        private static Dictionary<string, object> TestDirectorKingdom(string id, bool player, params string[] enemies)
        {
            return new Dictionary<string, object>
            {
                ["kingdomId"] = id,
                ["name"] = id,
                ["leaderHeroId"] = id + "_ruler",
                ["leaderName"] = id + " ruler",
                ["rulingClanId"] = id + "_clan",
                ["isPlayerKingdom"] = player,
                ["strength"] = 1000d,
                ["treasury"] = 100000,
                ["leaderGold"] = 100000,
                ["fiefCount"] = 4,
                ["enemies"] = new ArrayList(enemies ?? new string[0])
            };
        }

        private static Dictionary<string, object> TestDirectorVirtues(double compassion, double boldness, double honor, double loyalty, double responsibility, double courage, double judgment)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["compassion"] = compassion,
                ["boldness"] = boldness,
                ["honor"] = honor,
                ["loyalty"] = loyalty,
                ["responsibility"] = responsibility,
                ["courage"] = courage,
                ["judgment"] = judgment
            };
        }
    }
}
