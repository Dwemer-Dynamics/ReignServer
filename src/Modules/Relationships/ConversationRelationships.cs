using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TerminalActionReportProtocol = Reign.Core.Contracts.Actions.TerminalActionReportProtocol;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private static readonly HashSet<string> ConversationRelationshipTiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "routine", "meaningful", "harmful_lie", "hostile", "severe", "transformative", "gift_witness", "gift_leverage"
        };

        internal static void EnsureConversationRelationshipSchema(ReignDbConnection connection)
        {
            EnsureMbtiRelationshipSchema(connection);
            DialogueService.EnsureWorldHistoryLieDetectionSchema(connection);
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_relationship_receipts (
receipt_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL DEFAULT '',exchange_id TEXT NOT NULL,
source_turn_id TEXT NOT NULL DEFAULT '',observer_id TEXT NOT NULL,target_id TEXT NOT NULL,mode TEXT NOT NULL DEFAULT '',
act_kind TEXT NOT NULL DEFAULT 'routine',severity_tier TEXT NOT NULL DEFAULT 'routine',valence TEXT NOT NULL DEFAULT 'positive',
base_delta INTEGER NOT NULL DEFAULT 0,modifier_delta INTEGER NOT NULL DEFAULT 0,final_delta INTEGER NOT NULL DEFAULT 0,
prior_affinity INTEGER NOT NULL DEFAULT 0,resulting_affinity INTEGER NOT NULL DEFAULT 0,prior_native_relation INTEGER NOT NULL DEFAULT 0,
resulting_native_relation INTEGER NOT NULL DEFAULT 0,native_pair_delta INTEGER NOT NULL DEFAULT 0,
lie_check_id TEXT NOT NULL DEFAULT '',benefit_event_id TEXT NOT NULL DEFAULT '',
gift_recipient_id TEXT NOT NULL DEFAULT '',
confidence REAL NOT NULL DEFAULT 0,evidence_json TEXT NOT NULL DEFAULT '[]',modifiers_json TEXT NOT NULL DEFAULT '[]',
            summary TEXT NOT NULL DEFAULT '',current_conduct_quote TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'applied',native_application_status TEXT NOT NULL DEFAULT 'pending',
native_applied_ts INTEGER NOT NULL DEFAULT 0,native_receipt_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
UNIQUE(exchange_id,observer_id,target_id));");
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "native_application_status", "TEXT NOT NULL DEFAULT 'pending'");
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "native_applied_ts", "INTEGER NOT NULL DEFAULT 0");
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "native_receipt_json", "TEXT NOT NULL DEFAULT '{}'");
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "native_pair_delta", "INTEGER NOT NULL DEFAULT 0");
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "gift_recipient_id", "TEXT NOT NULL DEFAULT ''");
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "current_conduct_quote", "TEXT NOT NULL DEFAULT ''");
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_relationship_recent ON conversation_relationship_receipts(created_ts DESC);");
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_relationship_pair ON conversation_relationship_receipts(observer_id,target_id,created_ts DESC);");
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_relationship_benefits (
benefit_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL DEFAULT '',giver_id TEXT NOT NULL,recipient_id TEXT NOT NULL,
benefit_kind TEXT NOT NULL DEFAULT 'gift',description TEXT NOT NULL DEFAULT '',source_event_id TEXT NOT NULL DEFAULT '',source_turn_id TEXT NOT NULL DEFAULT '',
original_delta INTEGER NOT NULL DEFAULT 0,retained_appreciation REAL NOT NULL DEFAULT 1,continued_utility REAL NOT NULL DEFAULT 1,
status TEXT NOT NULL DEFAULT 'retained',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
source_evidence_json TEXT NOT NULL DEFAULT '[]',payload_json TEXT NOT NULL DEFAULT '{}');");
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_benefits", "source_evidence_json", "TEXT NOT NULL DEFAULT '[]'");
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_benefit_recipient ON conversation_relationship_benefits(recipient_id,giver_id,updated_ts DESC);");
        }

        internal static List<Dictionary<string, object>> NormalizeConversationRelationshipAssessments(
            Dictionary<string, object> parsed,
            Dictionary<string, object> payload,
            string observerId,
            string relationshipSignal,
            string reactionTargetId,
            string sourceTurnId)
        {
            List<string> participants = Program.MergeStringLists(
                Program.MergeStringLists(ReadStringList(payload, "participants"), ReadStringList(payload, "activeHeroIds")),
                Program.MergeStringLists(ReadStringList(payload, "sceneParticipants"), new[]
                {
                    ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId"), observerId
                }));
            HashSet<string> allowed = new HashSet<string>(participants.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            string playerId = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId");
            string playerText = ReadFirstString(payload, "playerText", "text", "message");
            List<Dictionary<string, object>> rawRows = parsed == null
                ? new List<Dictionary<string, object>>()
                : ReadDictionaryList(parsed, "relationshipAssessments")
                    .Concat(ReadDictionaryList(parsed, "relationship_assessments")).ToList();
            Dictionary<string, Dictionary<string, object>> byTarget = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> raw in rawRows)
            {
                string target = ReadFirstString(raw, "targetHeroStringId", "targetId", "target_hero_string_id");
                if (string.IsNullOrWhiteSpace(target) || target.Equals(observerId, StringComparison.OrdinalIgnoreCase)) continue;
                if (allowed.Count > 0 && !allowed.Contains(target)) continue;
                string tier = ReadFirstString(raw, "severityTier", "tier", "severity_tier").ToLowerInvariant();
                if (!ConversationRelationshipTiers.Contains(tier)) tier = "routine";
                string valence = ReadString(raw, "valence", "").ToLowerInvariant();
                if (valence != "positive" && valence != "negative" && valence != "neutral") valence = "neutral";
                string evidenceIssue = "";
                if (valence == "negative" && target.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    && !ConversationNegativeAssessmentHasCurrentEvidence(raw, playerText))
                {
                    valence = "neutral";
                    evidenceIssue = "negative_assessment_missing_current_conduct";
                }
                string actKind = FirstNonEmpty(ReadFirstString(raw, "actKind", "actType", "act_kind"), tier);
                List<string> sourceIds = Program.MergeStringLists(new[] { sourceTurnId }, ReadStringList(raw, "sourceTurnIds"));
                byTarget[target] = new Dictionary<string, object>
                {
                    ["observerHeroStringId"] = observerId,
                    ["targetHeroStringId"] = target,
                    ["valence"] = valence,
                    ["assessmentIssue"] = evidenceIssue,
                    ["actKind"] = actKind,
                    ["severityTier"] = tier,
                    ["confidence"] = Program.ClampDouble(ReadDouble(raw, "confidence", 0.65d), 0d, 1d),
                    ["sourceTurnIds"] = sourceIds,
                    ["lieCheckId"] = ReadFirstString(raw, "lieCheckId", "lie_check_id"),
                    ["benefitEventId"] = ReadFirstString(raw, "benefitEventId", "benefitId", "benefit_event_id"),
                    ["giftRecipientHeroStringId"] = ReadFirstString(raw, "giftRecipientHeroStringId", "giftRecipientId", "gift_recipient_hero_string_id"),
                    ["summary"] = Program.LimitText(ReadFirstString(raw, "summary", "reason"), 1200),
                    ["importance"] = Program.ClampDouble(ReadDouble(raw, "importance", 0.5d), 0d, 1d),
                    ["continuedUtility"] = Program.ClampDouble(ReadDouble(raw, "continuedUtility", 1d), 0d, 1d),
                    ["retainedAppreciation"] = Program.ClampDouble(ReadDouble(raw, "retainedAppreciation", 1d), 0d, 1d),
                    ["coercionSeverity"] = Program.ClampDouble(ReadDouble(raw, "coercionSeverity", 0d), 0d, 1d),
                    ["evidenceSourceIds"] = ReadStringList(raw, "evidenceSourceIds"),
                    ["currentConductQuote"] = Program.LimitText(ReadFirstString(raw, "currentConductQuote", "current_conduct_quote", "evidenceQuote"), 600),
                    ["sourceText"] = target.Equals(playerId, StringComparison.OrdinalIgnoreCase) ? Program.LimitText(playerText, 4000) : ""
                };
            }

            Dictionary<string, object> verifiedLie = VerifiedConversationLieContext(payload, observerId);
            string verifiedLieCheckId = ReadString(verifiedLie, "lieCheckId", "");
            if (!string.IsNullOrWhiteSpace(verifiedLieCheckId)
                && !string.IsNullOrWhiteSpace(playerId)
                && !playerId.Equals(observerId, StringComparison.OrdinalIgnoreCase)
                && (allowed.Count == 0 || allowed.Contains(playerId)))
            {
                Dictionary<string, object> caught = byTarget.ContainsKey(playerId)
                    ? new Dictionary<string, object>(byTarget[playerId], StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, object>();
                caught["observerHeroStringId"] = observerId;
                caught["targetHeroStringId"] = playerId;
                caught["valence"] = "negative";
                caught["assessmentIssue"] = "";
                caught["actKind"] = "harmful_lie";
                caught["severityTier"] = "harmful_lie";
                caught["confidence"] = Math.Max(0.9d, ReadDouble(caught, "confidence", 0d));
                caught["sourceTurnIds"] = Program.MergeStringLists(ReadStringList(caught, "sourceTurnIds"), new[] { sourceTurnId });
                caught["lieCheckId"] = verifiedLieCheckId;
                caught["benefitEventId"] = ReadString(caught, "benefitEventId", "");
                caught["giftRecipientHeroStringId"] = ReadString(caught, "giftRecipientHeroStringId", "");
                caught["summary"] = "The observer directly detected an evidence-backed falsehood with deceptive intent.";
                caught["importance"] = Math.Max(0.75d, ReadDouble(caught, "importance", 0d));
                caught["continuedUtility"] = ReadDouble(caught, "continuedUtility", 1d);
                caught["retainedAppreciation"] = ReadDouble(caught, "retainedAppreciation", 1d);
                caught["coercionSeverity"] = ReadDouble(caught, "coercionSeverity", 0d);
                caught["evidenceSourceIds"] = Program.MergeStringLists(ReadStringList(caught, "evidenceSourceIds"), ReadStringList(verifiedLie, "evidenceSourceIds"));
                caught["currentConductQuote"] = Program.LimitText(playerText, 600);
                caught["sourceText"] = Program.LimitText(playerText, 4000);
                byTarget[playerId] = caught;
            }

            // A provider can correctly narrate and action-gate an accepted material
            // gift while still reducing the private relationship assessment to a
            // generic suspicious/routine reaction. Preserve distrust in the prose,
            // but do not lose the recipient-specific kindness or its later lineage.
            // This only applies to the named recipient, an explicit material gift,
            // and an affirmative acceptance in the parsed response.
            Dictionary<string, object> playerAssessment = !string.IsNullOrWhiteSpace(playerId) && byTarget.ContainsKey(playerId)
                ? byTarget[playerId]
                : null;
            string namedGiftRecipient = ReadFirstString(playerAssessment, "giftRecipientHeroStringId", "giftRecipientId");
            string playerTier = ReadFirstString(playerAssessment, "severityTier", "tier").ToLowerInvariant();
            if (playerAssessment != null
                && observerId.Equals(namedGiftRecipient, StringComparison.OrdinalIgnoreCase)
                && Program.HasExplicitMaterialGiftOffer(playerText)
                && ConversationGiftWasAccepted(parsed)
                && playerTier != "harmful_lie" && playerTier != "hostile" && playerTier != "severe" && playerTier != "gift_leverage")
            {
                playerAssessment["valence"] = "positive";
                playerAssessment["assessmentIssue"] = "";
                playerAssessment["actKind"] = "accepted_material_gift";
                if (playerTier != "transformative") playerAssessment["severityTier"] = "meaningful";
                playerAssessment["confidence"] = Math.Max(0.8d, ReadDouble(playerAssessment, "confidence", 0d));
                playerAssessment["importance"] = Math.Max(0.55d, ReadDouble(playerAssessment, "importance", 0d));
                playerAssessment["evidenceSourceIds"] = Program.MergeStringLists(
                    ReadStringList(playerAssessment, "evidenceSourceIds"), new[] { "accepted_material_gift" });
                playerAssessment["currentConductQuote"] = Program.LimitText(playerText, 600);
                string priorSummary = ReadString(playerAssessment, "summary", "");
                playerAssessment["summary"] = string.IsNullOrWhiteSpace(priorSummary)
                    ? "The named recipient accepted the player's explicit material gift."
                    : priorSummary + " The recipient explicitly accepted the material gift.";
                byTarget[playerId] = playerAssessment;
            }

            string fallbackTarget = FirstNonEmpty(reactionTargetId,
                ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId"));
            if (byTarget.Count == 0 && !string.IsNullOrWhiteSpace(fallbackTarget)
                && !fallbackTarget.Equals(observerId, StringComparison.OrdinalIgnoreCase)
                && (allowed.Count == 0 || allowed.Contains(fallbackTarget)))
            {
                byTarget[fallbackTarget] = new Dictionary<string, object>
                {
                    ["observerHeroStringId"] = observerId,
                    ["targetHeroStringId"] = fallbackTarget,
                    ["valence"] = "neutral",
                    ["assessmentIssue"] = DialogueService.ConversationAssessmentAbsenceReason(parsed),
                    ["actKind"] = "routine_conversation",
                    ["severityTier"] = "routine",
                    ["confidence"] = 0d,
                    ["sourceTurnIds"] = new List<string> { sourceTurnId },
                    ["lieCheckId"] = "", ["benefitEventId"] = "",
                    ["giftRecipientHeroStringId"] = "",
                    ["summary"] = "No supported relationship assessment was supplied; no change is inferred.",
                    ["importance"] = 0.25d, ["continuedUtility"] = 1d,
                    ["retainedAppreciation"] = 1d, ["coercionSeverity"] = 0d,
                    ["evidenceSourceIds"] = new List<string>(),
                    ["currentConductQuote"] = "",
                    ["sourceText"] = fallbackTarget.Equals(playerId, StringComparison.OrdinalIgnoreCase) ? Program.LimitText(playerText, 4000) : ""
                };
            }
            return byTarget.Values.ToList();
        }

        private static Dictionary<string, object> VerifiedConversationLieContext(Dictionary<string, object> payload, string observerId)
        {
            foreach (Dictionary<string, object> bundle in ReadDictionaryList(payload, "contextBundles"))
            {
                if (!ReadFirstString(bundle, "id", "pullId").Equals("verify_world_history", StringComparison.OrdinalIgnoreCase)
                    || !ReadBool(bundle, "ok", false))
                {
                    continue;
                }
                Dictionary<string, object> data = ReadDictionary(bundle, "data") ?? new Dictionary<string, object>();
                string lieCheckId = ReadString(data, "lieCheckId", "");
                string outcome = ReadFirstString(data, "detectionOutcome", "outcome").ToLowerInvariant();
                string verdict = ReadFirstString(data, "speakerVerdict", "objectiveVerdict").ToLowerInvariant();
                bool detected = outcome == "detected_firsthand" || outcome == "detected_secondhand";
                bool deceptiveIntent = ReadBool(data, "deceptiveIntent", false);
                if (string.IsNullOrWhiteSpace(lieCheckId) || !detected || verdict != "contradicted" || !deceptiveIntent) continue;
                return new Dictionary<string, object>
                {
                    ["lieCheckId"] = lieCheckId,
                    ["detectionOutcome"] = outcome,
                    ["speakerVerdict"] = verdict,
                    ["deceptiveIntent"] = true,
                    ["evidenceSourceIds"] = ReadStringList(data, "knownEvidenceEventIds")
                };
            }

            // Older running game-module builds may not yet route an immediately visible
            // location contradiction through verify_world_history. The server still has the
            // authoritative scene location. Reconcile that narrow case here and persist the
            // same production lie-check receipt before relationship adjudication.
            string playerId = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            string playerText = ReadFirstString(payload, "playerText", "text", "message");
            string settlementName = Program.SceneContextLocationName(ReadString(payload, "sceneContext", ""));
            string settlementId = ReadString(payload, "locationId", "");
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(observerId)
                || string.IsNullOrWhiteSpace(settlementId) || string.IsNullOrWhiteSpace(settlementName)
                || !DialogueService.ContradictsNativeCurrentSettlement(playerText, settlementName)
                || !DialogueService.ContainsConversationDeceptiveIntentEvidence(playerText))
            {
                return new Dictionary<string, object>();
            }

            Dictionary<string, object> direct = DialogueService.WorldHistoryLieCheckApi(new Dictionary<string, object>
            {
                ["campaignId"] = ReadString(payload, "campaignId", "default"),
                ["timelineId"] = ReadString(payload, "timelineId", "main"),
                ["claim"] = playerText,
                ["claimantId"] = playerId,
                ["targetId"] = observerId,
                ["targetKingdomId"] = ReadFirstString(ReadDictionary(payload, "speaker"), "kingdomId", "kingdomStringId"),
                ["worldDay"] = ReadDouble(payload, "worldDay", 0d),
                ["mode"] = ReadString(payload, "mode", "dialogue") + "_server_native_reconciliation",
                ["nativeContext"] = new Dictionary<string, object>
                {
                    ["claimantCurrentSettlementId"] = settlementId,
                    ["claimantCurrentSettlementName"] = settlementName,
                    ["targetCurrentSettlementId"] = settlementId,
                    ["targetCurrentSettlementName"] = settlementName,
                    ["sameCurrentSettlement"] = true
                }
            });
            Dictionary<string, object> packet = ReadDictionary(direct, "promptPacket") ?? new Dictionary<string, object>();
            string directOutcome = ReadFirstString(packet, "detectionOutcome", "outcome").ToLowerInvariant();
            string directVerdict = ReadFirstString(packet, "speakerVerdict", "objectiveVerdict").ToLowerInvariant();
            string directId = ReadString(direct, "lieCheckId", "");
            if (string.IsNullOrWhiteSpace(directId)
                || (directOutcome != "detected_firsthand" && directOutcome != "detected_secondhand")
                || directVerdict != "contradicted" || !ReadBool(packet, "deceptiveIntent", false))
            {
                return new Dictionary<string, object>();
            }
            return new Dictionary<string, object>
            {
                ["lieCheckId"] = directId,
                ["detectionOutcome"] = directOutcome,
                ["speakerVerdict"] = directVerdict,
                ["deceptiveIntent"] = true,
                ["evidenceSourceIds"] = ReadStringList(packet, "knownEvidenceEventIds")
            };
        }

        private static bool ConversationGiftWasAccepted(Dictionary<string, object> parsed)
        {
            if (parsed == null) return false;
            Dictionary<string, object> gate = ReadDictionary(parsed, "actionGate") ?? ReadDictionary(parsed, "action_gate") ?? new Dictionary<string, object>();
            string combined = Program.NormalizeLookup(string.Join(" ", new[]
            {
                ReadFirstString(parsed, "reply", "response", "text", "content"),
                ReadString(parsed, "intent", ""),
                ReadString(gate, "intent", ""),
                ReadString(gate, "reason", "")
            }));
            if (Program.ContainsAny(combined,
                "refuse the gift", "refuses the gift", "decline the gift", "declines the gift", "reject the gift", "rejects the gift",
                "will not take", "won't take", "would not take", "wouldn't take", "keep your gift", "keep the gift",
                "do not accept", "don't accept", "does not accept", "doesn't accept")) return false;
            if (Program.ContainsAny(combined,
                "accept the gift", "accepts the gift", "accepted the gift", "accept the cup", "accepts the cup", "accepted the cup",
                "i will take it", "i'll take it", "i take it", "takes the gift", "takes the cup", "physical handover"))
            {
                return true;
            }
            return Regex.IsMatch(combined,
                @"\b(?:i\s+)?(?:accept|accepts|accepted|take|takes|took)\s+(?:it|(?:the|this|that|a|an|your)\s+(?:(?:valuable|carved|silver|golden|fine|material|physical|genuine|sincere)\s+){0,4}(?:gift|cup|item|present|ring|necklace|jewel|gem|weapon|sword|armor|horse|mount|clothes|robe|food|grain|goods|wares))\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool ConversationNegativeAssessmentHasCurrentEvidence(
            Dictionary<string, object> assessment, string currentContribution)
        {
            // A disposition label or hostile NPC wording is not evidence of an
            // offense by the player. Verified lies and accepted gifts are resolved
            // separately below from their authoritative event evidence.
            string quote = ReadFirstString(assessment, "currentConductQuote", "current_conduct_quote", "evidenceQuote");
            return ReadDouble(assessment, "confidence", 0d) >= 0.6d
                && !string.IsNullOrWhiteSpace(ReadFirstString(assessment, "summary", "reason"))
                && DialogueService.AgencyContainsExactQuote(currentContribution, quote);
        }

        private static List<Dictionary<string, object>> LoadPriorPartyConversationContributions(
            Dictionary<string, object> payload,
            string exchangeId)
        {
            string campaignId = ReadString(payload, "campaignId", Program.LatestCampaignId());
            string sessionId = ReadFirstString(payload, "conversationSessionId", "sessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Match exchangeMatch = Regex.Match(exchangeId ?? "", @"^(.*)_turn_\d+$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (exchangeMatch.Success) sessionId = exchangeMatch.Groups[1].Value;
            }
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(sessionId))
                return new List<Dictionary<string, object>>();

            try
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    DialogueService.EnsureCategorizedMemorySchema(connection);
                    return ReignDatabase.QuerySql(connection, @"SELECT speaker_id,speaker_name,text,turn_order,exchange_id
FROM conversation_turns
WHERE session_id=$session AND exchange_id<>$exchange AND role='npc' AND status='active'
ORDER BY turn_order ASC;", new Dictionary<string, object>
                    {
                        ["session"] = sessionId,
                        ["exchange"] = exchangeId ?? ""
                    });
                }
            }
            catch
            {
                // Relationship adjudication must still complete for the current turn if historical
                // transcript lookup is temporarily unavailable.
                return new List<Dictionary<string, object>>();
            }
        }

        private static bool IsSilentPartySpeakerResult(Dictionary<string, object> result)
        {
            string participation = ReadString(result, "participation", "speak").Trim().ToLowerInvariant();
            string reply = ReadFirstString(result, "reply", "text", "dialogue");
            return string.IsNullOrWhiteSpace(reply)
                || new[] { "quiet", "silent", "silence", "none", "skip" }.Contains(participation);
        }

        internal static List<Dictionary<string, object>> CompleteSequentialPartyRelationshipAssessments(
            Dictionary<string, object> payload,
            List<Dictionary<string, object>> suppliedAssessments,
            string playerId,
            string exchangeId)
        {
            List<Dictionary<string, object>> results = ReadDictionaryList(payload, "speakerResults");
            if (results.Count == 0) return suppliedAssessments ?? new List<Dictionary<string, object>>();

            List<Dictionary<string, object>> assessments = (suppliedAssessments ?? new List<Dictionary<string, object>>())
                .Select(x => new Dictionary<string, object>(x))
                .ToList();
            Dictionary<string, string> participantNames = ReadDictionaryList(payload, "participantProfiles")
                .Where(row => !string.IsNullOrWhiteSpace(Program.CharacterIdFrom(row)))
                .GroupBy(Program.CharacterIdFrom, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => ReadString(group.First(), "name", ""), StringComparer.OrdinalIgnoreCase);
            HashSet<string> eligibleParticipants = new HashSet<string>(ReadStringList(payload, "participants"), StringComparer.OrdinalIgnoreCase);
            eligibleParticipants.UnionWith(participantNames.Keys);
            HashSet<string> priorSpeakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> contributions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(playerId))
            {
                priorSpeakers.Add(playerId);
                contributions[playerId] = ReadFirstString(payload, "playerText", "text", "message");
            }
            foreach (Dictionary<string, object> priorContribution in LoadPriorPartyConversationContributions(payload, exchangeId))
            {
                string priorSpeakerId = ReadString(priorContribution, "speaker_id", "");
                if (string.IsNullOrWhiteSpace(priorSpeakerId)
                    || priorSpeakerId.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    || (eligibleParticipants.Count > 0 && !eligibleParticipants.Contains(priorSpeakerId)))
                    continue;
                priorSpeakers.Add(priorSpeakerId);
                contributions[priorSpeakerId] = ReadString(priorContribution, "text", "");
            }

            foreach (Dictionary<string, object> result in results.Where(x => ReadBool(x, "ok", true) && !ReadBool(x, "replayed", false)))
            {
                string speakerId = ReadFirstString(result, "heroStringId", "heroId", "speakerHeroStringId");
                if (string.IsNullOrWhiteSpace(speakerId)) continue;
                if (IsSilentPartySpeakerResult(result))
                {
                    assessments.RemoveAll(x => ReadFirstString(x, "observerHeroStringId", "observerId")
                        .Equals(speakerId, StringComparison.OrdinalIgnoreCase));
                    continue;
                }
                List<Dictionary<string, object>> speakerAssessments = assessments
                    .Where(x => ReadFirstString(x, "observerHeroStringId", "observerId").Equals(speakerId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                Func<string, string, string, Dictionary<string, object>> routineFallback = (targetId, valence, summary) => new Dictionary<string, object>
                {
                    ["observerHeroStringId"] = speakerId,
                    ["targetHeroStringId"] = targetId,
                    ["valence"] = valence,
                    ["actKind"] = "routine_conversation",
                    ["severityTier"] = "routine",
                    ["confidence"] = 0.5d,
                    ["sourceTurnIds"] = new List<string> { exchangeId },
                    ["lieCheckId"] = "", ["benefitEventId"] = "", ["giftRecipientHeroStringId"] = "",
                    ["summary"] = summary,
                    ["importance"] = 0.25d, ["continuedUtility"] = 1d,
                    ["retainedAppreciation"] = 1d, ["coercionSeverity"] = 0d,
                    ["evidenceSourceIds"] = new List<string>()
                };

                if (!string.IsNullOrWhiteSpace(playerId)
                    && priorSpeakers.Contains(playerId)
                    && !speakerAssessments.Any(x => ReadFirstString(x, "targetHeroStringId", "targetId").Equals(playerId, StringComparison.OrdinalIgnoreCase)))
                {
                    Dictionary<string, object> fallback = routineFallback(playerId,
                        "neutral",
                        "The player assessment was omitted; no relationship change is inferred.");
                    assessments.Add(fallback);
                    speakerAssessments.Add(fallback);
                }

                string reactionTargetId = ReadFirstString(result, "reactionTargetHeroStringId", "reactionTargetId");
                if (!string.IsNullOrWhiteSpace(reactionTargetId)
                    && !reactionTargetId.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    && priorSpeakers.Contains(reactionTargetId)
                    && !speakerAssessments.Any(x => ReadFirstString(x, "targetHeroStringId", "targetId").Equals(reactionTargetId, StringComparison.OrdinalIgnoreCase)))
                {
                    Dictionary<string, object> fallback = routineFallback(reactionTargetId,
                        "neutral",
                        "The speaker engaged this participant but omitted an assessment; disagreement alone does not establish a personal offense.");
                    assessments.Add(fallback);
                    speakerAssessments.Add(fallback);
                }

                string replyText = ReadString(result, "reply", "");
                foreach (string namedPriorSpeaker in priorSpeakers.Where(id => !id.Equals(playerId, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    if (speakerAssessments.Any(x => ReadFirstString(x, "targetHeroStringId", "targetId").Equals(namedPriorSpeaker, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (!participantNames.TryGetValue(namedPriorSpeaker, out string priorName) || string.IsNullOrWhiteSpace(priorName))
                        continue;
                    string priorFirstName = WorldSimulationService.FirstName(priorName);
                    bool uniqueFirstName = priorFirstName.Length >= 4
                        && participantNames.Values.Count(name => WorldSimulationService.FirstName(name).Equals(priorFirstName, StringComparison.OrdinalIgnoreCase)) == 1;
                    if (!WorldSimulationService.ContainsWholePhrase(replyText, priorName) && !(uniqueFirstName && WorldSimulationService.ContainsWholePhrase(replyText, priorFirstName)))
                        continue;
                    Dictionary<string, object> fallback = routineFallback(namedPriorSpeaker,
                        "neutral",
                        "Naming a participant does not establish a positive or negative relationship reaction.");
                    assessments.Add(fallback);
                    speakerAssessments.Add(fallback);
                }

                foreach (Dictionary<string, object> assessment in speakerAssessments)
                {
                    assessment["eligibleTargetIds"] = priorSpeakers.ToList();
                    string targetId = ReadFirstString(assessment, "targetHeroStringId", "targetId");
                    if (string.IsNullOrWhiteSpace(ReadString(assessment, "sourceText", ""))
                        && contributions.TryGetValue(targetId, out string sourceText))
                        assessment["sourceText"] = Program.LimitText(sourceText, 4000);
                }
                priorSpeakers.Add(speakerId);
                contributions[speakerId] = replyText;
            }
            return assessments;
        }

        internal static Dictionary<string, object> ConversationRelationshipEvaluateApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", Program.LatestCampaignId());
            lock (CampaignRelationshipWriteLock(campaignId))
                return ConversationRelationshipEvaluateApiLocked(payload);
        }

        private static Dictionary<string, object> ConversationRelationshipEvaluateApiLocked(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", Program.LatestCampaignId());
            string exchangeId = FirstNonEmpty(ReadFirstString(payload, "exchangeId", "turnId", "sceneTurnId"), "conversation_" + Guid.NewGuid().ToString("N"));
            string timelineId = ReadString(payload, "timelineId", "");
            string mode = ReadString(payload, "mode", "conversation");
            string playerId = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<Dictionary<string, object>> assessments = ReadDictionaryList(payload, "assessments");
            if (mode.Equals("party_chat", StringComparison.OrdinalIgnoreCase))
            {
                assessments = CompleteSequentialPartyRelationshipAssessments(payload, assessments, playerId, exchangeId);
            }
            // Neutral/unresolved assessments occupy their slots during party
            // completion, then produce no receipt, native change or notification.
            assessments = assessments.Where(a => ReadString(a, "valence", "") == "positive"
                || ReadString(a, "valence", "") == "negative").ToList();
            List<Dictionary<string, object>> relationshipPairs = ReadDictionaryList(payload, "relationshipPairs");
            HashSet<string> participants = new HashSet<string>(ReadStringList(payload, "participants"), StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in relationshipPairs)
            {
                participants.Add(ReadString(row, "subjectId", ""));
                participants.Add(ReadString(row, "targetId", ""));
            }
            participants.RemoveWhere(string.IsNullOrWhiteSpace);

            List<Dictionary<string, object>> receipts = new List<Dictionary<string, object>>();
            Dictionary<string, bool> receiptIdempotency =
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> nativeChanges = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> pairReceiptIds =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureConversationRelationshipSchema(connection);
                ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;");
                try
                {
                    foreach (IGrouping<string, Dictionary<string, object>> group in assessments
                        .Where(x => !string.IsNullOrWhiteSpace(ReadFirstString(x, "observerHeroStringId", "observerId"))
                            && !string.IsNullOrWhiteSpace(ReadFirstString(x, "targetHeroStringId", "targetId")))
                        .GroupBy(x => ReadFirstString(x, "observerHeroStringId", "observerId") + "->" + ReadFirstString(x, "targetHeroStringId", "targetId"), StringComparer.OrdinalIgnoreCase))
                    {
                        Dictionary<string, object> assessment = SelectDominantConversationAssessment(group.ToList());
                        string observerId = ReadFirstString(assessment, "observerHeroStringId", "observerId");
                        string targetId = ReadFirstString(assessment, "targetHeroStringId", "targetId");
                        List<string> eligibleTargets = ReadStringList(assessment, "eligibleTargetIds");
                        string giftRecipientId = ReadFirstString(assessment, "giftRecipientHeroStringId", "giftRecipientId");
                        if (!string.IsNullOrWhiteSpace(giftRecipientId) && participants.Count > 0 && !participants.Contains(giftRecipientId))
                            assessment["giftRecipientHeroStringId"] = string.Empty;
                        if (observerId.Equals(targetId, StringComparison.OrdinalIgnoreCase)
                            || (participants.Count > 0 && (!participants.Contains(observerId) || !participants.Contains(targetId)))
                            || (eligibleTargets.Count > 0 && !eligibleTargets.Contains(targetId, StringComparer.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                        string receiptId = "conversation_relation_" + DialogueService.PromptHash(exchangeId + "|" + observerId + "|" + targetId).Substring(0, 24).ToLowerInvariant();
                        Dictionary<string, object> existing = ReignDatabase.QuerySql(connection,
                            "SELECT * FROM conversation_relationship_receipts WHERE receipt_id=$id LIMIT 1;",
                            new Dictionary<string, object> { ["id"] = receiptId }).FirstOrDefault();
                        if (existing != null)
                        {
                            ReconcilePendingConversationNativeApplication(connection, existing, relationshipPairs, playerId, nativeChanges, ts,
                                ReadString(payload, "nativeReceiptProtocol", "") == "reign-native-relationship-receipt-v2");
                            receiptIdempotency[receiptId] = true;
                            continue;
                        }

                        Dictionary<string, object> pairInput = FindRelationshipPair(relationshipPairs, observerId, targetId);
                        int nativeRelation = ReadInt(pairInput, "nativeRelation", 0);
                        string pairKey = AmbientPairKey(observerId, targetId);
                        string chemistrySelectSql =
                            ReignPostgreSqlDialect.IsPostgreSql(connection)
                                ? "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1 FOR UPDATE;"
                                : "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;";
                        Dictionary<string, object> chemistry = ReignDatabase.QuerySql(connection,
                            chemistrySelectSql,
                            new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                        bool observerIsA = chemistry == null
                            ? string.Compare(observerId, targetId, StringComparison.OrdinalIgnoreCase) <= 0
                            : ReadString(chemistry, "hero_a_id", "").Equals(observerId, StringComparison.OrdinalIgnoreCase);
                        int priorUnderlyingAffinity = chemistry == null ? 0
                            : ReadInt(chemistry, observerIsA ? "affinity_a_to_b" : "affinity_b_to_a", nativeRelation);
                        int observerSocialModifier = ReadInt(ResolveObserverPublicStanding(connection,
                            campaignId, timelineId, observerId, targetId), "value", 0);
                        int priorAffinity = Clamp(priorUnderlyingAffinity + observerSocialModifier, -100, 100);
                        Dictionary<string, object> adjudicated = AdjudicateConversationRelationship(connection, campaignId, assessment, priorAffinity);
                        int finalDelta = ReadInt(adjudicated, "finalDelta", 0);
                        int resultingUnderlyingAffinity = Clamp(priorUnderlyingAffinity + finalDelta, -100, 100);
                        int resultingAffinity = Clamp(resultingUnderlyingAffinity + observerSocialModifier, -100, 100);
                        if (chemistry == null)
                        {
                            string heroA = observerIsA ? observerId : targetId;
                            string heroB = observerIsA ? targetId : observerId;
                            int ab = observerIsA ? resultingUnderlyingAffinity : 0;
                            int ba = observerIsA ? 0 : resultingUnderlyingAffinity;
                            string typeA = ResolvePermanentMbtiTypeById(connection,
                                campaignId, heroA, (int)Math.Floor(worldDay));
                            string typeB = ResolvePermanentMbtiTypeById(connection,
                                campaignId, heroB, (int)Math.Floor(worldDay));
                            if (!MbtiDefinitions.ContainsKey(typeA)
                                || !MbtiDefinitions.ContainsKey(typeB))
                                throw new InvalidOperationException(
                                    "Conversation relationship could not resolve immutable MBTI for "
                                    + pairKey + ".");
                            int baseAB = MbtiCompatibility(typeA, typeB);
                            int baseBA = MbtiCompatibility(typeB, typeA);
                            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,
base_chance_b_to_a,chance_a_to_b,chance_b_to_a,
affinity_a_to_b,affinity_b_to_a,tag_a_to_b,tag_b_to_a,
projected_native_relation,first_day,last_day,last_context_kind,
last_context_id,processing_shard,compatibility_version,updated_ts)
VALUES($pair,$a,$b,$typeA,$typeB,$baseAB,$baseBA,$chanceAB,$chanceBA,
$ab,$ba,$tagAB,$tagBA,0,$day,$day,'conversation',$exchange,$shard,$version,$ts);",
                                new Dictionary<string, object> { ["pair"] = pairKey, ["a"] = heroA, ["b"] = heroB,
                                    ["typeA"] = typeA, ["typeB"] = typeB,
                                    ["baseAB"] = baseAB, ["baseBA"] = baseBA,
                                    ["chanceAB"] = AdjustedMbtiCompatibility(baseAB),
                                    ["chanceBA"] = AdjustedMbtiCompatibility(baseBA),
                                    ["ab"] = ab, ["ba"] = ba,
                                    ["tagAB"] = DirectionalRelationshipTag(campaignId,
                                        pairKey, "a_to_b", ab, ba),
                                    ["tagBA"] = DirectionalRelationshipTag(campaignId,
                                        pairKey, "b_to_a", ba, ab),
                                    ["day"] = (int)Math.Floor(worldDay),
                                    ["exchange"] = exchangeId,
                                    ["shard"] = RelationshipCadenceShard(campaignId,
                                        pairKey),
                                    ["version"] = MbtiChemistryVersion, ["ts"] = ts });
                            RecordRelationshipPairProvenance(connection, campaignId,
                                timelineId, pairKey, "direct_interaction", false,
                                worldDay, new Dictionary<string, object>
                                {
                                    ["exchangeId"] = exchangeId,
                                    ["mode"] = mode
                                });
                        }
                        else
                        {
                            chemistry = ApplyAtomicDirectionalRelationshipDelta(
                                connection, campaignId, observerId, targetId,
                                finalDelta, worldDay,
                                "conversation:" + exchangeId, timelineId)
                                ?? chemistry;
                            RecordRelationshipPairProvenance(connection, campaignId,
                                timelineId, pairKey, "direct_interaction", false,
                                worldDay, new Dictionary<string, object>
                                {
                                    ["exchangeId"] = exchangeId,
                                    ["mode"] = mode
                                });
                        }
                        chemistry = ReignDatabase.QuerySql(connection,
                            "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                            new Dictionary<string, object> { ["pair"] = pairKey })
                            .FirstOrDefault();
                        RefreshEffectivePairProjection(connection, campaignId,
                            timelineId, chemistry, worldDay,
                            "conversation_consequence");

                        // Directional affinity projection is private behavioral state,
                        // not Bannerlord's symmetric personal relation. Native relation
                        // arithmetic is finalized atomically per hero pair after every
                        // directional judgment for this speaking turn has been staged.
                        int resultingNative = nativeRelation;
                        string sourceTurnId = ReadStringList(assessment, "sourceTurnIds").FirstOrDefault() ?? "";
                        Dictionary<string, object> args = new Dictionary<string, object>
                        {
                            ["id"] = receiptId, ["campaign"] = campaignId, ["timeline"] = timelineId, ["exchange"] = exchangeId,
                            ["turn"] = sourceTurnId, ["observer"] = observerId, ["target"] = targetId, ["mode"] = mode,
                            ["act"] = ReadString(adjudicated, "actKind", "routine_conversation"), ["tier"] = ReadString(adjudicated, "severityTier", "routine"),
                            ["valence"] = ReadString(adjudicated, "valence", "positive"), ["base"] = ReadInt(adjudicated, "baseDelta", 0),
                            ["modifier"] = ReadInt(adjudicated, "modifierDelta", 0), ["final"] = finalDelta,
                            ["priorAffinity"] = priorAffinity, ["resultingAffinity"] = resultingAffinity,
                            ["priorNative"] = nativeRelation, ["resultingNative"] = resultingNative,
                            ["lie"] = ReadString(adjudicated, "lieCheckId", ""), ["benefit"] = ReadString(adjudicated, "benefitEventId", ""),
                            ["giftRecipient"] = ReadString(adjudicated, "giftRecipientHeroStringId", ""),
                            ["confidence"] = ReadDouble(assessment, "confidence", 0d), ["evidence"] = Json.Serialize(ReadStringList(assessment, "evidenceSourceIds")),
                            ["currentConductQuote"] = Program.LimitText(ReadFirstString(assessment, "currentConductQuote", "current_conduct_quote", "evidenceQuote"), 600),
                            ["modifiers"] = Json.Serialize(adjudicated.ContainsKey("modifiers") ? adjudicated["modifiers"] : new List<object>()), ["summary"] = ReadString(assessment, "summary", ""),
                            ["status"] = ReadString(adjudicated, "status", "applied"), ["ts"] = ts
                        };
                        ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts(
receipt_id,campaign_id,timeline_id,exchange_id,source_turn_id,observer_id,target_id,mode,act_kind,severity_tier,valence,
base_delta,modifier_delta,final_delta,prior_affinity,resulting_affinity,prior_native_relation,resulting_native_relation,native_pair_delta,
  lie_check_id,benefit_event_id,gift_recipient_id,confidence,evidence_json,modifiers_json,summary,current_conduct_quote,status,created_ts)
VALUES($id,$campaign,$timeline,$exchange,$turn,$observer,$target,$mode,$act,$tier,$valence,$base,$modifier,$final,
$priorAffinity,$resultingAffinity,$priorNative,$resultingNative,0,$lie,$benefit,$giftRecipient,$confidence,$evidence,$modifiers,$summary,$currentConductQuote,$status,$ts);", args);
                        if (finalDelta == 0)
                        {
                            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_relationship_receipts SET native_application_status='skipped' WHERE receipt_id=$id;",
                                new Dictionary<string, object> { ["id"] = receiptId });
                        }
                        UpdateConversationBenefitLedger(connection, campaignId, timelineId, assessment, adjudicated, receiptId, exchangeId, sourceTurnId, observerId, targetId, ts);
                        receiptIdempotency[receiptId] = false;
                        string nativeKey = AmbientPairKey(observerId, targetId);
                        if (!pairReceiptIds.TryGetValue(nativeKey, out List<string> stagedPairReceiptIds))
                        {
                            stagedPairReceiptIds = new List<string>();
                            pairReceiptIds[nativeKey] = stagedPairReceiptIds;
                        }
                        stagedPairReceiptIds.Add(receiptId);

                        if (finalDelta != 0)
                        {
                            if (!nativeChanges.TryGetValue(nativeKey, out Dictionary<string, object> native))
                            {
                                native = new Dictionary<string, object>
                                {
                                    ["subjectId"] = observerId, ["targetId"] = targetId, ["delta"] = 0,
                                    ["priorNativeRelation"] = nativeRelation,
                                    ["resultingNativeRelation"] = nativeRelation,
                                    ["showNotification"] = observerId.Equals(playerId, StringComparison.OrdinalIgnoreCase) || targetId.Equals(playerId, StringComparison.OrdinalIgnoreCase),
                                    ["receiptIds"] = new List<string>()
                                };
                                nativeChanges[nativeKey] = native;
                            }
                            native["delta"] = Clamp(ReadInt(native, "delta", 0) + finalDelta, -20, 20);
                            native["resultingNativeRelation"] = Clamp(
                                ReadInt(native, "priorNativeRelation", nativeRelation)
                                + ReadInt(native, "delta", 0), -100, 100);
                            ((List<string>)native["receiptIds"]).Add(receiptId);
                        }
                    }
                    foreach (KeyValuePair<string, Dictionary<string, object>> nativeEntry in nativeChanges)
                    {
                        Dictionary<string, object> native = nativeEntry.Value;
                        List<string> atomicReceiptIds = pairReceiptIds.TryGetValue(
                            nativeEntry.Key, out List<string> stagedReceiptIds)
                            ? stagedReceiptIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                            : ReadStringList(native, "receiptIds");
                        native["receiptIds"] = atomicReceiptIds;
                        int priorNative = ReadInt(native, "priorNativeRelation", 0);
                        int nativePairDelta = ReadInt(native, "delta", 0);
                        int resultingNative = Clamp(priorNative + nativePairDelta, -100, 100);
                        native["resultingNativeRelation"] = resultingNative;
                        foreach (string receiptId in atomicReceiptIds)
                        {
                            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_relationship_receipts
SET prior_native_relation=$prior,resulting_native_relation=$result,native_pair_delta=$delta
WHERE receipt_id=$id;", new Dictionary<string, object>
                            {
                                ["prior"] = priorNative, ["result"] = resultingNative,
                                ["delta"] = nativePairDelta, ["id"] = receiptId
                            });
                        }
                    }
                    ReignDatabase.ExecuteSql(connection, "COMMIT;");
                }
                catch
                {
                    try { ReignDatabase.ExecuteSql(connection, "ROLLBACK;"); } catch { }
                    throw;
                }
            }
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureConversationRelationshipSchema(connection);
                ReputationService.EnsureSocialReputationSchema(connection);
                bool courtPopularityChanged = false;
                foreach (string targetId in assessments
                    .Where(x => ReadInt(x, "finalDelta", 0) != 0
                        || !string.IsNullOrWhiteSpace(ReadFirstString(x, "observerHeroStringId", "observerId")))
                    .Select(x => ReadFirstString(x, "targetHeroStringId", "targetId"))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    bool popularityChanged = ReputationService.RecomputeCourtPopularityReputations(connection, campaignId,
                        FirstNonEmpty(timelineId, "main"), targetId, worldDay,
                        exchangeId + "|court_popularity|" + targetId);
                    if (popularityChanged)
                    {
                        courtPopularityChanged = true;
                        ReputationService.ReconcileSocialRelationshipsForSubjects(connection, campaignId,
                            FirstNonEmpty(timelineId, "main"), new[] { targetId }, worldDay);
                    }
                }
                if (courtPopularityChanged
                    && !campaignId.StartsWith("__", StringComparison.Ordinal))
                    ReputationService.SchedulePendingReputationReasonJobs(campaignId);
                foreach (KeyValuePair<string, bool> receipt in receiptIdempotency)
                {
                    Dictionary<string, object> stored = ReignDatabase.QuerySql(connection,
                        "SELECT * FROM conversation_relationship_receipts WHERE receipt_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = receipt.Key }).FirstOrDefault();
                    if (stored != null)
                    {
                        receipts.Add(ConversationReceiptResponse(stored, receipt.Value));
                    }
                }
            }
            Dictionary<string, object> response = new Dictionary<string, object>
            {
                ["ok"] = true, ["campaignId"] = campaignId, ["exchangeId"] = exchangeId,
                ["receipts"] = receipts, ["nativeChanges"] = nativeChanges.Values.ToList(),
                ["appliedCount"] = receipts.Count(x => !ReadBool(x, "idempotent", false) && ReadInt(x, "finalDelta", 0) != 0),
                ["idempotentCount"] = receipts.Count(x => ReadBool(x, "idempotent", false))
            };
            Program.WriteAudit(campaignId, ReadString(payload, "correlationId", ""), "server", mode,
                "relationship.conversation", "", "", exchangeId, "completed", 0,
                "Conversation relationship consequences adjudicated.", response);
            DialogueService.PersonalGossipRelationshipApplied(campaignId, payload);
            return response;
        }

        private static void ReconcilePendingConversationNativeApplication(
            ReignDbConnection connection,
            Dictionary<string, object> existing,
            List<Dictionary<string, object>> relationshipPairs,
            string playerId,
            Dictionary<string, Dictionary<string, object>> nativeChanges,
            long ts, bool requireMeasuredReceipt = false)
        {
            if (ReadInt(existing, "final_delta", 0) == 0
                || ReadString(existing, "native_application_status", "pending").Equals("applied", StringComparison.OrdinalIgnoreCase)) return;
            string observerId = ReadString(existing, "observer_id", "");
            string targetId = ReadString(existing, "target_id", "");
            string exchangeId = ReadString(existing, "exchange_id", "");
            Dictionary<string, object> pairInput = FindRelationshipPair(relationshipPairs, observerId, targetId);
            int currentNative = ReadInt(pairInput, "nativeRelation", int.MinValue);
            List<Dictionary<string, object>> pairReceipts = ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_relationship_receipts
WHERE exchange_id=$exchange AND ((observer_id=$a AND target_id=$b) OR (observer_id=$b AND target_id=$a));",
                new Dictionary<string, object> { ["exchange"] = exchangeId, ["a"] = observerId, ["b"] = targetId });
            int priorNative = pairReceipts.Count == 0 ? ReadInt(existing, "prior_native_relation", 0) : ReadInt(pairReceipts[0], "prior_native_relation", 0);
            int aggregateDelta = Clamp(pairReceipts.Sum(x => ReadInt(x, "final_delta", 0)), -20, 20);
            int expectedNative = Clamp(priorNative + aggregateDelta, -100, 100);
            List<string> receiptIds = pairReceipts.Select(x => ReadString(x, "receipt_id", "")).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (!requireMeasuredReceipt && currentNative != int.MinValue && currentNative == expectedNative)
            {
                foreach (string receiptId in receiptIds)
                {
                    ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_relationship_receipts
SET prior_native_relation=$prior,resulting_native_relation=$result,native_pair_delta=$delta,
native_application_status='applied',native_applied_ts=$ts,native_receipt_json=$receipt
WHERE receipt_id=$id;", new Dictionary<string, object>
                    {
                        ["prior"] = priorNative, ["result"] = currentNative,
                        ["delta"] = aggregateDelta, ["ts"] = ts,
                        ["receipt"] = Json.Serialize(new Dictionary<string, object>
                        {
                            ["reconciled"] = true, ["nativeRelation"] = currentNative,
                            ["delta"] = aggregateDelta, ["measurementStatus"] = "inferred_legacy_value_match"
                        }),
                        ["id"] = receiptId
                    });
                }
                return;
            }
            string key = AmbientPairKey(observerId, targetId);
            nativeChanges[key] = new Dictionary<string, object>
            {
                ["subjectId"] = observerId, ["targetId"] = targetId, ["delta"] = aggregateDelta,
                ["priorNativeRelation"] = priorNative,
                ["resultingNativeRelation"] = expectedNative,
                ["expectedNativeRelation"] = expectedNative,
                ["showNotification"] = observerId.Equals(playerId, StringComparison.OrdinalIgnoreCase) || targetId.Equals(playerId, StringComparison.OrdinalIgnoreCase),
                ["receiptIds"] = receiptIds
            };
        }

        internal static Dictionary<string, object> ConversationRelationshipNativeReceiptApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", Program.LatestCampaignId());
            List<string> receiptIds = ReadStringList(payload, "receiptIds").Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (receiptIds.Count == 0 || receiptIds.Count > 64) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Between one and 64 relationship receipt ids are required." };
            Dictionary<string, object> nativeReceipt = ReadDictionary(payload, "nativeReceipt")
                ?? new Dictionary<string, object>();
            int actualNativeRelation = ReadInt(nativeReceipt, "nativeRelation", int.MinValue);
            int actualPriorNativeRelation = ReadInt(nativeReceipt, "priorNativeRelation", int.MinValue);
            int requestedNativeDelta = ReadInt(nativeReceipt, "delta", int.MinValue);
            bool measuredReceipt = ReadString(nativeReceipt, "schema", "") == "reign-native-relationship-receipt-v2";
            string subjectId = ReadString(nativeReceipt, "subjectId", ""), targetId = ReadString(nativeReceipt, "targetId", "");
            if (actualNativeRelation != int.MinValue && (actualNativeRelation < -100 || actualNativeRelation > 100)
                || actualPriorNativeRelation != int.MinValue && (actualPriorNativeRelation < -100 || actualPriorNativeRelation > 100)
                || measuredReceipt && (subjectId.Length == 0 || targetId.Length == 0 || subjectId == targetId
                    || actualNativeRelation == int.MinValue || actualPriorNativeRelation == int.MinValue || requestedNativeDelta == int.MinValue))
                return TestDict("ok", false, "error", "Native receipt participants and measured before/after values are required and must be valid.");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int updated = 0;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureConversationRelationshipSchema(connection);
                using var transaction = connection.BeginTransaction();
                var receiptRows = new List<Dictionary<string, object>>();
                // The acknowledgment update fires the revision trigger on this row, so
                // take it for update before any receipt lock to keep one lock order.
                string currentGeneration = receiptIds.Any(id => id.StartsWith(TerminalActionReportProtocol.ReceiptIdPrefix, StringComparison.Ordinal))
                    ? ReadString(ReignDatabase.QuerySql(connection,
                        "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR NO KEY UPDATE;").FirstOrDefault(), "restore_generation", "")
                    : "";
                foreach (string receiptId in receiptIds.OrderBy(id => id, StringComparer.Ordinal))
                {
                    Dictionary<string, object> prior = ReignDatabase.QuerySql(connection,
                        @"SELECT * FROM conversation_relationship_receipts WHERE receipt_id=$id FOR UPDATE;",
                        new Dictionary<string, object> { ["id"] = receiptId }).FirstOrDefault();
                    if (prior == null) return TestDict("ok", false, "error", "A native relationship receipt does not exist in this campaign.", "receiptId", receiptId);
                    if (ReadString(prior, "receipt_id", "").StartsWith(TerminalActionReportProtocol.ReceiptIdPrefix, StringComparison.Ordinal))
                    {
                        string actionError = CompletedActionReceiptAckError(connection, currentGeneration, prior, nativeReceipt, measuredReceipt,
                            receiptIds.Count, ReadDictionary(payload, TerminalActionReportProtocol.LoadProofField));
                        if (actionError.Length > 0)
                        {
                            var refusal = TestDict("ok", false, "error", actionError, "receiptId", receiptId);
                            if (actionError == TerminalActionReportProtocol.LoadProofStale) refusal["reason"] = actionError;
                            return refusal;
                        }
                    }
                    string observer = ReadString(prior, "observer_id", ""), target = ReadString(prior, "target_id", "");
                    if (subjectId.Length > 0 || targetId.Length > 0)
                    {
                        bool pairMatches = (subjectId == observer && targetId == target) || (targetId == observer && subjectId == target);
                        if (!pairMatches) return TestDict("ok", false, "error", "The native receipt belongs to a different relationship pair.", "receiptId", receiptId);
                    }
                    else if (receiptRows.Count > 0)
                    {
                        string firstObserver = ReadString(receiptRows[0], "observer_id", ""), firstTarget = ReadString(receiptRows[0], "target_id", "");
                        if (!((observer == firstObserver && target == firstTarget) || (observer == firstTarget && target == firstObserver)))
                            return TestDict("ok", false, "error", "A legacy acknowledgment cannot cover multiple relationship pairs.");
                    }
                    if (ReadString(prior, "native_application_status", "") == "applied")
                    {
                        var original = TryParseJsonObject(ReadString(prior, "native_receipt_json", "{}"));
                        foreach (string field in new[] { "priorNativeRelation", "nativeRelation", "delta" })
                            if (original != null && original.ContainsKey(field) && nativeReceipt.ContainsKey(field)
                                && ReadInt(original, field, int.MinValue) != ReadInt(nativeReceipt, field, int.MinValue))
                                return TestDict("ok", false, "error", "A native relationship observation cannot change after acknowledgment.", "receiptId", receiptId);
                    }
                    else if (measuredReceipt && requestedNativeDelta != ReadInt(prior, "native_pair_delta", 0))
                        return TestDict("ok", false, "error", "The requested native delta does not match its adjudicated effect.", "receiptId", receiptId);
                    receiptRows.Add(prior);
                }
                foreach (var prior in receiptRows)
                {
                    if (ReadString(prior, "native_application_status", "") == "applied") continue;
                    string receiptId = ReadString(prior, "receipt_id", "");
                    var observation = new Dictionary<string, object>(nativeReceipt)
                    {
                        ["plannedNativeDelta"] = ReadInt(prior, "native_pair_delta", 0),
                        ["measurementStatus"] = actualNativeRelation != int.MinValue && actualPriorNativeRelation != int.MinValue
                            ? "observed_before_and_after" : "legacy_unmeasured_transition"
                    };
                    if (actualNativeRelation != int.MinValue && actualPriorNativeRelation != int.MinValue)
                    {
                        observation["observedDelta"] = actualNativeRelation - actualPriorNativeRelation;
                        if (requestedNativeDelta != int.MinValue)
                            observation["modifierOrCapDifference"] = actualNativeRelation - actualPriorNativeRelation - requestedNativeDelta;
                    }
                    Dictionary<string, object> args = new Dictionary<string, object>
                    {
                        ["ts"] = ts,
                        ["receipt"] = Json.Serialize(observation),
                        ["id"] = receiptId
                    };
                    if (actualNativeRelation != int.MinValue && actualPriorNativeRelation != int.MinValue)
                    {
                        int effectiveDelta = actualNativeRelation - actualPriorNativeRelation;
                        args["prior"] = actualPriorNativeRelation;
                        args["result"] = actualNativeRelation;
                        args["delta"] = effectiveDelta;
                        ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_relationship_receipts
SET prior_native_relation=$prior,resulting_native_relation=$result,native_pair_delta=$delta,
native_application_status='applied',native_applied_ts=$ts,native_receipt_json=$receipt
WHERE receipt_id=$id AND native_application_status!='applied';", args);
                    }
                    else
                    {
                        ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_relationship_receipts
SET native_application_status='applied',native_applied_ts=$ts,native_receipt_json=$receipt
WHERE receipt_id=$id AND native_application_status!='applied';", args);
                    }
                    updated++;
                }
                transaction.Commit();
            }
            return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaignId, ["updatedCount"] = updated, ["receiptIds"] = receiptIds };
        }

        // Opt-in terminal action report: one pending receipt for the aggregate pair
        // delta, inside the caller's transaction. Transcript, continuity and trust
        // readers select status='applied' AND severity_tier<>'routine', so this row
        // never becomes conversation memory. The delta is the action aggregate
        // (-25..15), never the conversation ±20 reconciliation.
        internal static string StageCompletedActionRelationshipReceipt(ReignDbConnection connection, string campaignId,
            string timelineId, string actionId, string generation, string reportSha256,
            string subjectId, string targetId, int sharedDelta, long ts)
        {
            string receiptId = TerminalActionReportProtocol.ReceiptId(actionId, timelineId, generation, reportSha256);
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts(
receipt_id,campaign_id,timeline_id,exchange_id,source_turn_id,observer_id,target_id,mode,act_kind,severity_tier,valence,
base_delta,modifier_delta,final_delta,native_pair_delta,evidence_json,summary,status,native_application_status,created_ts)
VALUES($id,$campaign,$timeline,$exchange,$action,$subject,$target,$mode,$mode,$tier,$valence,
$delta,0,$delta,$delta,$evidence,$summary,$status,'pending',$ts);", new Dictionary<string, object>
            {
                ["id"] = receiptId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                ["exchange"] = TerminalActionReportProtocol.ExchangeId(actionId, generation), ["action"] = actionId,
                ["subject"] = subjectId, ["target"] = targetId, ["mode"] = TerminalActionReportProtocol.ReceiptMode,
                ["tier"] = TerminalActionReportProtocol.ReceiptSeverityTier, ["valence"] = sharedDelta < 0 ? "negative" : "positive",
                ["delta"] = sharedDelta, ["status"] = TerminalActionReportProtocol.ReceiptStatus, ["ts"] = ts,
                ["summary"] = "Completed action native relationship effect.",
                ["evidence"] = Json.Serialize(new Dictionary<string, object>
                {
                    ["schema"] = TerminalActionReportProtocol.MarkerSchema, ["actionId"] = actionId,
                    ["timelineId"] = timelineId, ["reportGeneration"] = generation,
                    ["reportSha256"] = reportSha256, ["plannedNativeDelta"] = sharedDelta
                })
            });
            return receiptId;
        }

        // Reads the staged action receipt after re-deriving its identity. Applied
        // receipts are reported as such and are never offered as a new native effect.
        internal static Dictionary<string, object> CompletedActionRelationshipEffect(ReignDbConnection connection,
            string receiptId, string actionId, string timelineId, string generation, string reportSha256)
        {
            Dictionary<string, object> row = ReignDatabase.QuerySql(connection,
                "SELECT * FROM conversation_relationship_receipts WHERE receipt_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = receiptId }).FirstOrDefault();
            var evidence = TryParseJsonObject(ReadString(row, "evidence_json", "{}")) ?? new Dictionary<string, object>();
            if (row == null || receiptId != TerminalActionReportProtocol.ReceiptId(actionId, timelineId, generation, reportSha256)
                || ReadString(row, "timeline_id", "") != timelineId || ReadString(evidence, "actionId", "") != actionId
                || ReadString(evidence, "reportGeneration", "") != generation || ReadString(evidence, "reportSha256", "") != reportSha256)
                throw new InvalidOperationException("The accepted terminal report does not match its relationship receipt.");
            return new Dictionary<string, object>
            {
                ["receiptId"] = receiptId, ["subjectId"] = ReadString(row, "observer_id", ""), ["targetId"] = ReadString(row, "target_id", ""),
                ["delta"] = ReadInt(evidence, "plannedNativeDelta", 0), ["timelineId"] = timelineId,
                ["reportGeneration"] = generation, ["reportSha256"] = reportSha256,
                ["nativeApplicationStatus"] = ReadString(row, "native_application_status", "pending")
            };
        }

        // Measured acknowledgment of an action receipt stays bound to its exact
        // report, timeline and the current campaign generation, including repeats.
        // A recovered (admitted) report is acknowledged only in its admitting generation and
        // with an exact echo of that admission, so an ack from before a later restore cannot
        // land after a re-adoption. A load proof, when sent, must name the current verified load.
        private static string CompletedActionReceiptAckError(ReignDbConnection connection, string current, Dictionary<string, object> prior,
            Dictionary<string, object> nativeReceipt, bool measuredReceipt, int receiptCount, Dictionary<string, object> loadProof)
        {
            if (!measuredReceipt || receiptCount != 1)
                return "An action relationship receipt requires exactly one measured native acknowledgment.";
            var evidence = TryParseJsonObject(ReadString(prior, "evidence_json", "{}")) ?? new Dictionary<string, object>();
            string timeline = ReadString(evidence, "timelineId", ""), generation = ReadString(evidence, "reportGeneration", "");
            string sha = ReadString(evidence, "reportSha256", ""), actionId = ReadString(evidence, "actionId", "");
            bool bound;
            try
            {
                bound = ReadString(prior, "receipt_id", "") == TerminalActionReportProtocol.ReceiptId(actionId, timeline, generation, sha)
                    && ReadString(prior, "timeline_id", "") == timeline && ReadString(nativeReceipt, "timelineId", "") == timeline
                    && ReadString(nativeReceipt, "reportGeneration", "") == generation && ReadString(nativeReceipt, "reportSha256", "") == sha;
            }
            catch (ArgumentException) { bound = false; }
            if (!bound) return "The native acknowledgment does not match its action report binding.";
            string proofError = DialogueService.TerminalReportLoadProofError(connection, current, loadProof, out _);
            if (proofError.Length > 0) return proofError;
            var admission = DialogueService.TerminalReportMarkerAdmission(connection, actionId, sha);
            if (admission != null)
            {
                var echoed = ReadDictionary(nativeReceipt, TerminalActionReportProtocol.AdmissionField);
                string admitted = ReadString(admission, "generation", ""), session = ReadString(admission, "loadSessionId", "");
                bool exact = admitted.Length > 0 && current == admitted
                    && echoed != null && echoed.TryGetValue("generation", out object g) && g is string echoedGeneration && echoedGeneration == admitted
                    && echoed.TryGetValue("loadSessionId", out object l) && l is string echoedSession && echoedSession == session;
                return exact ? "" : "The native acknowledgment does not match the load that admitted this action report.";
            }
            return current.Length > 0 && current == generation ? "" : "The action relationship receipt belongs to a different campaign generation.";
        }
    }
}
