using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly HashSet<string> ConversationRelationshipTiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "routine", "meaningful", "harmful_lie", "hostile", "severe", "transformative", "gift_witness", "gift_leverage"
        };

        private static void EnsureConversationRelationshipSchema(ReignDbConnection connection)
        {
            EnsureMbtiRelationshipSchema(connection);
            EnsureWorldHistoryLieDetectionSchema(connection);
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_relationship_receipts (
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
            EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "native_application_status", "TEXT NOT NULL DEFAULT 'pending'");
            EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "native_applied_ts", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "native_receipt_json", "TEXT NOT NULL DEFAULT '{}'");
            EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "native_pair_delta", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "gift_recipient_id", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "current_conduct_quote", "TEXT NOT NULL DEFAULT ''");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_relationship_recent ON conversation_relationship_receipts(created_ts DESC);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_relationship_pair ON conversation_relationship_receipts(observer_id,target_id,created_ts DESC);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_relationship_benefits (
benefit_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL DEFAULT '',giver_id TEXT NOT NULL,recipient_id TEXT NOT NULL,
benefit_kind TEXT NOT NULL DEFAULT 'gift',description TEXT NOT NULL DEFAULT '',source_event_id TEXT NOT NULL DEFAULT '',source_turn_id TEXT NOT NULL DEFAULT '',
original_delta INTEGER NOT NULL DEFAULT 0,retained_appreciation REAL NOT NULL DEFAULT 1,continued_utility REAL NOT NULL DEFAULT 1,
status TEXT NOT NULL DEFAULT 'retained',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
source_evidence_json TEXT NOT NULL DEFAULT '[]',payload_json TEXT NOT NULL DEFAULT '{}');");
            EnsureDatabaseColumn(connection, "conversation_relationship_benefits", "source_evidence_json", "TEXT NOT NULL DEFAULT '[]'");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_benefit_recipient ON conversation_relationship_benefits(recipient_id,giver_id,updated_ts DESC);");
        }

        private static List<Dictionary<string, object>> NormalizeConversationRelationshipAssessments(
            Dictionary<string, object> parsed,
            Dictionary<string, object> payload,
            string observerId,
            string relationshipSignal,
            string reactionTargetId,
            string sourceTurnId)
        {
            List<string> participants = MergeStringLists(
                MergeStringLists(ReadStringList(payload, "participants"), ReadStringList(payload, "activeHeroIds")),
                MergeStringLists(ReadStringList(payload, "sceneParticipants"), new[]
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
                if (valence != "positive" && valence != "negative") valence = ConversationValenceFromSignal(relationshipSignal, "positive");
                string actKind = FirstNonEmpty(ReadFirstString(raw, "actKind", "actType", "act_kind"), tier);
                List<string> sourceIds = MergeStringLists(ReadStringList(raw, "sourceTurnIds"), new[] { sourceTurnId });
                byTarget[target] = new Dictionary<string, object>
                {
                    ["observerHeroStringId"] = observerId,
                    ["targetHeroStringId"] = target,
                    ["valence"] = valence,
                    ["actKind"] = actKind,
                    ["severityTier"] = tier,
                    ["confidence"] = ClampDouble(ReadDouble(raw, "confidence", 0.65d), 0d, 1d),
                    ["sourceTurnIds"] = sourceIds,
                    ["lieCheckId"] = ReadFirstString(raw, "lieCheckId", "lie_check_id"),
                    ["benefitEventId"] = ReadFirstString(raw, "benefitEventId", "benefitId", "benefit_event_id"),
                    ["giftRecipientHeroStringId"] = ReadFirstString(raw, "giftRecipientHeroStringId", "giftRecipientId", "gift_recipient_hero_string_id"),
                    ["summary"] = LimitText(ReadFirstString(raw, "summary", "reason"), 1200),
                    ["importance"] = ClampDouble(ReadDouble(raw, "importance", 0.5d), 0d, 1d),
                    ["continuedUtility"] = ClampDouble(ReadDouble(raw, "continuedUtility", 1d), 0d, 1d),
                    ["retainedAppreciation"] = ClampDouble(ReadDouble(raw, "retainedAppreciation", 1d), 0d, 1d),
                    ["coercionSeverity"] = ClampDouble(ReadDouble(raw, "coercionSeverity", 0d), 0d, 1d),
                    ["evidenceSourceIds"] = ReadStringList(raw, "evidenceSourceIds"),
                    ["currentConductQuote"] = LimitText(ReadFirstString(raw, "currentConductQuote", "current_conduct_quote", "evidenceQuote"), 600),
                    ["sourceText"] = target.Equals(playerId, StringComparison.OrdinalIgnoreCase) ? LimitText(playerText, 4000) : ""
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
                caught["actKind"] = "harmful_lie";
                caught["severityTier"] = "harmful_lie";
                caught["confidence"] = Math.Max(0.9d, ReadDouble(caught, "confidence", 0d));
                caught["sourceTurnIds"] = MergeStringLists(ReadStringList(caught, "sourceTurnIds"), new[] { sourceTurnId });
                caught["lieCheckId"] = verifiedLieCheckId;
                caught["benefitEventId"] = ReadString(caught, "benefitEventId", "");
                caught["giftRecipientHeroStringId"] = ReadString(caught, "giftRecipientHeroStringId", "");
                caught["summary"] = "The observer directly detected an evidence-backed falsehood with deceptive intent.";
                caught["importance"] = Math.Max(0.75d, ReadDouble(caught, "importance", 0d));
                caught["continuedUtility"] = ReadDouble(caught, "continuedUtility", 1d);
                caught["retainedAppreciation"] = ReadDouble(caught, "retainedAppreciation", 1d);
                caught["coercionSeverity"] = ReadDouble(caught, "coercionSeverity", 0d);
                caught["evidenceSourceIds"] = MergeStringLists(ReadStringList(caught, "evidenceSourceIds"), ReadStringList(verifiedLie, "evidenceSourceIds"));
                caught["currentConductQuote"] = LimitText(playerText, 600);
                caught["sourceText"] = LimitText(playerText, 4000);
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
                && HasExplicitMaterialGiftOffer(playerText)
                && ConversationGiftWasAccepted(parsed)
                && playerTier != "harmful_lie" && playerTier != "hostile" && playerTier != "severe" && playerTier != "gift_leverage")
            {
                playerAssessment["valence"] = "positive";
                playerAssessment["actKind"] = "accepted_material_gift";
                if (playerTier != "transformative") playerAssessment["severityTier"] = "meaningful";
                playerAssessment["confidence"] = Math.Max(0.8d, ReadDouble(playerAssessment, "confidence", 0d));
                playerAssessment["importance"] = Math.Max(0.55d, ReadDouble(playerAssessment, "importance", 0d));
                playerAssessment["evidenceSourceIds"] = MergeStringLists(
                    ReadStringList(playerAssessment, "evidenceSourceIds"), new[] { "accepted_material_gift" });
                playerAssessment["currentConductQuote"] = LimitText(playerText, 600);
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
                string valence = InferConversationFallbackValence(relationshipSignal, playerText,
                    ReadFirstString(parsed, "reply", "response", "text", "content"), observerId, fallbackTarget);
                byTarget[fallbackTarget] = new Dictionary<string, object>
                {
                    ["observerHeroStringId"] = observerId,
                    ["targetHeroStringId"] = fallbackTarget,
                    ["valence"] = valence,
                    ["actKind"] = "routine_conversation",
                    ["severityTier"] = "routine",
                    ["confidence"] = 0.6d,
                    ["sourceTurnIds"] = new List<string> { sourceTurnId },
                    ["lieCheckId"] = "", ["benefitEventId"] = "",
                    ["giftRecipientHeroStringId"] = "",
                    ["summary"] = "Routine conversational reaction.",
                    ["importance"] = 0.25d, ["continuedUtility"] = 1d,
                    ["retainedAppreciation"] = 1d, ["coercionSeverity"] = 0d,
                    ["evidenceSourceIds"] = new List<string>(),
                    ["currentConductQuote"] = "",
                    ["sourceText"] = fallbackTarget.Equals(playerId, StringComparison.OrdinalIgnoreCase) ? LimitText(playerText, 4000) : ""
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
            string settlementName = SceneContextLocationName(ReadString(payload, "sceneContext", ""));
            string settlementId = ReadString(payload, "locationId", "");
            if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(observerId)
                || string.IsNullOrWhiteSpace(settlementId) || string.IsNullOrWhiteSpace(settlementName)
                || !ContradictsNativeCurrentSettlement(playerText, settlementName)
                || !ContainsConversationDeceptiveIntentEvidence(playerText))
            {
                return new Dictionary<string, object>();
            }

            Dictionary<string, object> direct = WorldHistoryLieCheckApi(new Dictionary<string, object>
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
            string combined = NormalizeLookup(string.Join(" ", new[]
            {
                ReadFirstString(parsed, "reply", "response", "text", "content"),
                ReadString(parsed, "intent", ""),
                ReadString(gate, "intent", ""),
                ReadString(gate, "reason", "")
            }));
            if (ContainsAny(combined,
                "refuse the gift", "refuses the gift", "decline the gift", "declines the gift", "reject the gift", "rejects the gift",
                "will not take", "won't take", "would not take", "wouldn't take", "keep your gift", "keep the gift",
                "do not accept", "don't accept", "does not accept", "doesn't accept")) return false;
            if (ContainsAny(combined,
                "accept the gift", "accepts the gift", "accepted the gift", "accept the cup", "accepts the cup", "accepted the cup",
                "i will take it", "i'll take it", "i take it", "takes the gift", "takes the cup", "physical handover"))
            {
                return true;
            }
            return Regex.IsMatch(combined,
                @"\b(?:i\s+)?(?:accept|accepts|accepted|take|takes|took)\s+(?:it|(?:the|this|that|a|an|your)\s+(?:(?:valuable|carved|silver|golden|fine|material|physical|genuine|sincere)\s+){0,4}(?:gift|cup|item|present|ring|necklace|jewel|gem|weapon|sword|armor|horse|mount|clothes|robe|food|grain|goods|wares))\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string InferConversationFallbackValence(
            string relationshipSignal, string playerText, string visibleReply, string observerId, string targetId)
        {
            string signaled = ConversationValenceFromSignal(relationshipSignal, "");
            if (!string.IsNullOrWhiteSpace(relationshipSignal)
                && !relationshipSignal.Trim().Equals("unchanged", StringComparison.OrdinalIgnoreCase)
                && !relationshipSignal.Trim().Equals("neutral", StringComparison.OrdinalIgnoreCase))
            {
                return signaled;
            }

            string combined = ((playerText ?? "") + "\n" + (visibleReply ?? "")).ToLowerInvariant();
            if (ContainsAny(combined,
                "deliberately lying", "account is false", "claim is false", "caught you lying", "you are lying",
                "do not trust", "don't trust", "three lies", "hostile", "threat", "betray", "humiliat", "grave abuse",
                "worthless coward", "i will kill", "i'll kill", "i despise you", "irritated", "suspicious", "resentful", "angry"))
            {
                return "negative";
            }
            if (ContainsAny(combined,
                "thank you", "i appreciate", "grateful", "kindness", "you helped", "you saved", "glad to see", "good friend", "respectful"))
            {
                return "positive";
            }

            // The model normally supplies valence. If it omits the private schema,
            // keep the required routine +/-1 deterministic instead of silently
            // turning every incomplete response into a positive relationship gain.
            string key = (observerId ?? "") + "|" + (targetId ?? "") + "|" + NormalizeLookup(playerText);
            return (PromptHash(key)[0] % 2) == 0 ? "positive" : "negative";
        }

        private static string ConversationValenceFromSignal(string signal, string fallback)
        {
            signal = (signal ?? "").Trim().ToLowerInvariant();
            if (new[] { "warmer", "respectful", "indebted", "impressed", "friendly", "grateful" }.Contains(signal)) return "positive";
            if (new[] { "colder", "suspicious", "hostile", "afraid", "resentful", "angry" }.Contains(signal)) return "negative";
            return fallback == "negative" ? "negative" : "positive";
        }

        private static List<Dictionary<string, object>> LoadPriorPartyConversationContributions(
            Dictionary<string, object> payload,
            string exchangeId)
        {
            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
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
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureCategorizedMemorySchema(connection);
                    return QuerySql(connection, @"SELECT speaker_id,speaker_name,text,turn_order,exchange_id
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

        private static List<Dictionary<string, object>> CompleteSequentialPartyRelationshipAssessments(
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
                .Where(row => !string.IsNullOrWhiteSpace(CharacterIdFrom(row)))
                .GroupBy(CharacterIdFrom, StringComparer.OrdinalIgnoreCase)
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

            foreach (Dictionary<string, object> result in results.Where(x => ReadBool(x, "ok", true)))
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
                        ConversationValenceFromSignal(ReadString(result, "relationshipSignal", ""), "positive"),
                        "A routine reaction toward the player was required but omitted from the model's private classifications.");
                    assessments.Add(fallback);
                    speakerAssessments.Add(fallback);
                }

                string reactionTargetId = ReadFirstString(result, "reactionTargetHeroStringId", "reactionTargetId");
                if (!string.IsNullOrWhiteSpace(reactionTargetId)
                    && !reactionTargetId.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    && priorSpeakers.Contains(reactionTargetId)
                    && !speakerAssessments.Any(x => ReadFirstString(x, "targetHeroStringId", "targetId").Equals(reactionTargetId, StringComparison.OrdinalIgnoreCase)))
                {
                    string participation = ReadString(result, "participation", "speak").ToLowerInvariant();
                    Dictionary<string, object> fallback = routineFallback(reactionTargetId,
                        participation == "disagree" ? "negative" : "positive",
                        "The speaker directly engaged this prior participant; a routine directional classification was supplied because the model omitted it.");
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
                    string priorFirstName = FirstName(priorName);
                    bool uniqueFirstName = priorFirstName.Length >= 4
                        && participantNames.Values.Count(name => FirstName(name).Equals(priorFirstName, StringComparison.OrdinalIgnoreCase)) == 1;
                    if (!ContainsWholePhrase(replyText, priorName) && !(uniqueFirstName && ContainsWholePhrase(replyText, priorFirstName)))
                        continue;
                    Dictionary<string, object> fallback = routineFallback(namedPriorSpeaker,
                        InferNamedNpcReactionValence(result, replyText),
                        "The speaker explicitly named and engaged this prior participant; a routine directional classification was supplied because the model omitted it.");
                    assessments.Add(fallback);
                    speakerAssessments.Add(fallback);
                }

                foreach (Dictionary<string, object> assessment in speakerAssessments)
                {
                    assessment["eligibleTargetIds"] = priorSpeakers.ToList();
                    string targetId = ReadFirstString(assessment, "targetHeroStringId", "targetId");
                    if (string.IsNullOrWhiteSpace(ReadString(assessment, "sourceText", ""))
                        && contributions.TryGetValue(targetId, out string sourceText))
                        assessment["sourceText"] = LimitText(sourceText, 4000);
                }
                priorSpeakers.Add(speakerId);
                contributions[speakerId] = replyText;
            }
            return assessments;
        }

        private static Dictionary<string, object> ConversationRelationshipEvaluateApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
            lock (CampaignRelationshipWriteLock(campaignId))
                return ConversationRelationshipEvaluateApiLocked(payload);
        }

        private static Dictionary<string, object> ConversationRelationshipEvaluateApiLocked(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
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
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureConversationRelationshipSchema(connection);
                ExecuteSql(connection, "BEGIN IMMEDIATE;");
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
                        string receiptId = "conversation_relation_" + PromptHash(exchangeId + "|" + observerId + "|" + targetId).Substring(0, 24).ToLowerInvariant();
                        Dictionary<string, object> existing = QuerySql(connection,
                            "SELECT * FROM conversation_relationship_receipts WHERE receipt_id=$id LIMIT 1;",
                            new Dictionary<string, object> { ["id"] = receiptId }).FirstOrDefault();
                        if (existing != null)
                        {
                            ReconcilePendingConversationNativeApplication(connection, existing, relationshipPairs, playerId, nativeChanges, ts);
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
                        Dictionary<string, object> chemistry = QuerySql(connection,
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
                            ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
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
                        chemistry = QuerySql(connection,
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
                            ["currentConductQuote"] = LimitText(ReadFirstString(assessment, "currentConductQuote", "current_conduct_quote", "evidenceQuote"), 600),
                            ["modifiers"] = Json.Serialize(adjudicated.ContainsKey("modifiers") ? adjudicated["modifiers"] : new List<object>()), ["summary"] = ReadString(assessment, "summary", ""),
                            ["status"] = ReadString(adjudicated, "status", "applied"), ["ts"] = ts
                        };
                        ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts(
receipt_id,campaign_id,timeline_id,exchange_id,source_turn_id,observer_id,target_id,mode,act_kind,severity_tier,valence,
base_delta,modifier_delta,final_delta,prior_affinity,resulting_affinity,prior_native_relation,resulting_native_relation,native_pair_delta,
  lie_check_id,benefit_event_id,gift_recipient_id,confidence,evidence_json,modifiers_json,summary,current_conduct_quote,status,created_ts)
VALUES($id,$campaign,$timeline,$exchange,$turn,$observer,$target,$mode,$act,$tier,$valence,$base,$modifier,$final,
$priorAffinity,$resultingAffinity,$priorNative,$resultingNative,0,$lie,$benefit,$giftRecipient,$confidence,$evidence,$modifiers,$summary,$currentConductQuote,$status,$ts);", args);
                        if (finalDelta == 0)
                        {
                            ExecuteSql(connection, "UPDATE conversation_relationship_receipts SET native_application_status='skipped' WHERE receipt_id=$id;",
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
                            ExecuteSql(connection, @"UPDATE conversation_relationship_receipts
SET prior_native_relation=$prior,resulting_native_relation=$result,native_pair_delta=$delta
WHERE receipt_id=$id;", new Dictionary<string, object>
                            {
                                ["prior"] = priorNative, ["result"] = resultingNative,
                                ["delta"] = nativePairDelta, ["id"] = receiptId
                            });
                        }
                    }
                    ExecuteSql(connection, "COMMIT;");
                }
                catch
                {
                    try { ExecuteSql(connection, "ROLLBACK;"); } catch { }
                    throw;
                }
            }
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureConversationRelationshipSchema(connection);
                EnsureSocialReputationSchema(connection);
                bool courtPopularityChanged = false;
                foreach (string targetId in assessments
                    .Where(x => ReadInt(x, "finalDelta", 0) != 0
                        || !string.IsNullOrWhiteSpace(ReadFirstString(x, "observerHeroStringId", "observerId")))
                    .Select(x => ReadFirstString(x, "targetHeroStringId", "targetId"))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    bool popularityChanged = RecomputeCourtPopularityReputations(connection, campaignId,
                        FirstNonEmpty(timelineId, "main"), targetId, worldDay,
                        exchangeId + "|court_popularity|" + targetId);
                    if (popularityChanged)
                    {
                        courtPopularityChanged = true;
                        ReconcileSocialRelationshipsForSubjects(connection, campaignId,
                            FirstNonEmpty(timelineId, "main"), new[] { targetId }, worldDay);
                    }
                }
                if (courtPopularityChanged
                    && !campaignId.StartsWith("__", StringComparison.Ordinal))
                    SchedulePendingReputationReasonJobs(campaignId);
                foreach (KeyValuePair<string, bool> receipt in receiptIdempotency)
                {
                    Dictionary<string, object> stored = QuerySql(connection,
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
            WriteAudit(campaignId, ReadString(payload, "correlationId", ""), "server", mode,
                "relationship.conversation", "", "", exchangeId, "completed", 0,
                "Conversation relationship consequences adjudicated.", response);
            return response;
        }

        private static void ReconcilePendingConversationNativeApplication(
            ReignDbConnection connection,
            Dictionary<string, object> existing,
            List<Dictionary<string, object>> relationshipPairs,
            string playerId,
            Dictionary<string, Dictionary<string, object>> nativeChanges,
            long ts)
        {
            if (ReadInt(existing, "final_delta", 0) == 0
                || ReadString(existing, "native_application_status", "pending").Equals("applied", StringComparison.OrdinalIgnoreCase)) return;
            string observerId = ReadString(existing, "observer_id", "");
            string targetId = ReadString(existing, "target_id", "");
            string exchangeId = ReadString(existing, "exchange_id", "");
            Dictionary<string, object> pairInput = FindRelationshipPair(relationshipPairs, observerId, targetId);
            int currentNative = ReadInt(pairInput, "nativeRelation", int.MinValue);
            List<Dictionary<string, object>> pairReceipts = QuerySql(connection, @"SELECT * FROM conversation_relationship_receipts
WHERE exchange_id=$exchange AND ((observer_id=$a AND target_id=$b) OR (observer_id=$b AND target_id=$a));",
                new Dictionary<string, object> { ["exchange"] = exchangeId, ["a"] = observerId, ["b"] = targetId });
            int priorNative = pairReceipts.Count == 0 ? ReadInt(existing, "prior_native_relation", 0) : ReadInt(pairReceipts[0], "prior_native_relation", 0);
            int aggregateDelta = Clamp(pairReceipts.Sum(x => ReadInt(x, "final_delta", 0)), -20, 20);
            int expectedNative = Clamp(priorNative + aggregateDelta, -100, 100);
            List<string> receiptIds = pairReceipts.Select(x => ReadString(x, "receipt_id", "")).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (currentNative != int.MinValue && currentNative == expectedNative)
            {
                foreach (string receiptId in receiptIds)
                {
                    ExecuteSql(connection, @"UPDATE conversation_relationship_receipts
SET prior_native_relation=$prior,resulting_native_relation=$result,native_pair_delta=$delta,
native_application_status='applied',native_applied_ts=$ts,native_receipt_json=$receipt
WHERE receipt_id=$id;", new Dictionary<string, object>
                    {
                        ["prior"] = priorNative, ["result"] = currentNative,
                        ["delta"] = aggregateDelta, ["ts"] = ts,
                        ["receipt"] = Json.Serialize(new Dictionary<string, object>
                        {
                            ["reconciled"] = true, ["nativeRelation"] = currentNative,
                            ["delta"] = aggregateDelta
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

        private static Dictionary<string, object> ConversationRelationshipNativeReceiptApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", LatestCampaignId());
            List<string> receiptIds = ReadStringList(payload, "receiptIds").Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (receiptIds.Count == 0) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "At least one relationship receipt id is required." };
            Dictionary<string, object> nativeReceipt = ReadDictionary(payload, "nativeReceipt")
                ?? new Dictionary<string, object>();
            int actualNativeRelation = ReadInt(nativeReceipt, "nativeRelation", int.MinValue);
            int actualPriorNativeRelation = ReadInt(nativeReceipt, "priorNativeRelation", int.MinValue);
            int requestedNativeDelta = ReadInt(nativeReceipt, "delta", int.MinValue);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int updated = 0;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureConversationRelationshipSchema(connection);
                foreach (string receiptId in receiptIds)
                {
                    Dictionary<string, object> prior = QuerySql(connection,
                        @"SELECT native_application_status,prior_native_relation
FROM conversation_relationship_receipts WHERE receipt_id=$id LIMIT 1;",
                        new Dictionary<string, object> { ["id"] = receiptId }).FirstOrDefault();
                    if (prior == null || ReadString(prior, "native_application_status", "") == "applied") continue;
                    Dictionary<string, object> args = new Dictionary<string, object>
                    {
                        ["ts"] = ts,
                        ["receipt"] = Json.Serialize(payload.ContainsKey("nativeReceipt")
                            ? payload["nativeReceipt"] : payload),
                        ["id"] = receiptId
                    };
                    if (actualNativeRelation != int.MinValue)
                    {
                        int storedPriorNative = ReadInt(prior, "prior_native_relation", actualNativeRelation);
                        int priorNative = actualPriorNativeRelation;
                        if (priorNative == int.MinValue)
                        {
                            if (requestedNativeDelta != int.MinValue
                                && Clamp(storedPriorNative + requestedNativeDelta, -100, 100) != actualNativeRelation)
                            {
                                priorNative = Clamp(actualNativeRelation - requestedNativeDelta, -100, 100);
                            }
                            else
                            {
                                priorNative = storedPriorNative;
                            }
                        }
                        int effectiveDelta = actualNativeRelation - priorNative;
                        args["prior"] = priorNative;
                        args["result"] = actualNativeRelation;
                        args["delta"] = Clamp(effectiveDelta, -20, 20);
                        ExecuteSql(connection, @"UPDATE conversation_relationship_receipts
SET prior_native_relation=$prior,resulting_native_relation=$result,native_pair_delta=$delta,
native_application_status='applied',native_applied_ts=$ts,native_receipt_json=$receipt
WHERE receipt_id=$id AND native_application_status!='applied';", args);
                    }
                    else
                    {
                        ExecuteSql(connection, @"UPDATE conversation_relationship_receipts
SET native_application_status='applied',native_applied_ts=$ts,native_receipt_json=$receipt
WHERE receipt_id=$id AND native_application_status!='applied';", args);
                    }
                    updated++;
                }
            }
            return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaignId, ["updatedCount"] = updated, ["receiptIds"] = receiptIds };
        }

        private static Dictionary<string, object> AdjudicateConversationRelationship(
            ReignDbConnection connection, string campaignId, Dictionary<string, object> assessment, int priorAffinity)
        {
            string tier = ReadFirstString(assessment, "severityTier", "tier").ToLowerInvariant();
            if (!ConversationRelationshipTiers.Contains(tier)) tier = "routine";
            string actKind = FirstNonEmpty(ReadFirstString(assessment, "actKind", "actType"), tier).ToLowerInvariant();
            string valence = ReadString(assessment, "valence", "positive").ToLowerInvariant() == "negative" ? "negative" : "positive";
            string lieCheckId = ReadFirstString(assessment, "lieCheckId", "lie_check_id");
            string observerId = ReadFirstString(assessment, "observerHeroStringId", "observerId");
            string giftRecipientId = ReadFirstString(assessment, "giftRecipientHeroStringId", "giftRecipientId");
            List<object> modifiers = new List<object>();
            string status = "applied";
            if (tier == "harmful_lie" || actKind.Contains("lie"))
            {
                Dictionary<string, object> lie = string.IsNullOrWhiteSpace(lieCheckId) ? null : QuerySql(connection,
                    "SELECT * FROM world_history_lie_checks WHERE lie_check_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = lieCheckId }).FirstOrDefault();
                string verdict = ReadString(lie, "objective_verdict", "").ToLowerInvariant();
                string basis = ReadString(lie, "knowledge_basis", "none").ToLowerInvariant();
                string outcome = ReadString(lie, "outcome", "").ToLowerInvariant();
                string claimantId = ReadString(lie, "claimant_id", "");
                string witnessId = ReadString(lie, "target_id", "");
                string lieObserverId = observerId;
                string targetId = ReadFirstString(assessment, "targetHeroStringId", "targetId");
                bool caught = outcome == "detected_firsthand" || outcome == "detected_secondhand";
                bool identitiesMatch = claimantId.Equals(targetId, StringComparison.OrdinalIgnoreCase)
                    && witnessId.Equals(lieObserverId, StringComparison.OrdinalIgnoreCase);
                bool contradicted = new[] { "contradicted", "false", "deceptive", "verified_lie" }.Contains(verdict)
                    && basis != "none" && caught && identitiesMatch;
                if (!contradicted)
                {
                    tier = "meaningful";
                    actKind = "unsupported_or_unknown_claim";
                    valence = "negative";
                    modifiers.Add("lie_penalty_downgraded_without_contradictory_evidence");
                }
                else modifiers.Add("verified_caught_lie_" + outcome);
            }
            if (tier != "gift_leverage"
                && valence == "negative"
                && !string.IsNullOrWhiteSpace(giftRecipientId)
                && observerId.Equals(giftRecipientId,
                    StringComparison.OrdinalIgnoreCase)
                && ConversationCurrentConductIsSupported(assessment)
                && (ReadDouble(assessment, "coercionSeverity", 0d) > 0d
                    || ReadDouble(assessment,
                        "retainedAppreciation", 1d) < 0.999d))
            {
                string recoveredLeverageReason;
                Dictionary<string, object> recoveredLeverageBenefit =
                    FindConversationBenefitForExplicitLeverage(
                        connection, campaignId, observerId,
                        ReadFirstString(assessment,
                            "targetHeroStringId", "targetId"),
                        assessment, out recoveredLeverageReason);
                if (recoveredLeverageBenefit != null)
                {
                    string recoveredBenefitId = ReadString(
                        recoveredLeverageBenefit, "benefit_id", "");
                    tier = "gift_leverage";
                    actKind = "gift_leverage";
                    assessment["benefitEventId"] = recoveredBenefitId;
                    modifiers.Add(
                        "gift_leverage_tier_promoted_from_pair_benefit_"
                        + recoveredBenefitId);
                    if (!string.IsNullOrWhiteSpace(
                            recoveredLeverageReason))
                    {
                        modifiers.Add(recoveredLeverageReason);
                    }
                }
            }
            if (tier != "severe"
                && ConversationActKindRequiresSevereTier(actKind)
                && ConversationSevereConductIsSupported(
                    connection, assessment, actKind))
            {
                // The model classifies the conduct but does not choose the
                // relationship number. If its act kind and quoted current
                // evidence prove a severe act, do not let an inconsistent
                // weaker tier label suppress the deterministic consequence.
                tier = "severe";
                valence = "negative";
                modifiers.Add(
                    "severe_tier_promoted_from_verified_act_kind");
            }
            if (tier != "gift_leverage"
                && actKind.Equals("gift_leverage", StringComparison.OrdinalIgnoreCase)
                && ConversationCurrentConductIsSupported(assessment)
                && (string.IsNullOrWhiteSpace(giftRecipientId)
                    || observerId.Equals(giftRecipientId, StringComparison.OrdinalIgnoreCase)))
            {
                // Providers sometimes put the correct conduct class in
                // actKind but use "hostile" as the severity label. Preserve
                // the more specific deterministic gift-lineage adjudication;
                // the pair-scoped benefit lookup below still decides whether
                // any prior appreciation can actually be clawed back.
                tier = "gift_leverage";
                valence = "negative";
                modifiers.Add(
                    "gift_leverage_tier_promoted_from_verified_act_kind");
            }
            bool giftAct = actKind.Contains("gift");
            if ((tier == "transformative" && giftAct
                    && (string.IsNullOrWhiteSpace(giftRecipientId) || !observerId.Equals(giftRecipientId, StringComparison.OrdinalIgnoreCase)))
                || tier == "gift_witness")
            {
                tier = "gift_witness";
                actKind = "gift_witness_reaction";
                valence = ConversationGiftWitnessValence(campaignId, observerId, valence);
                modifiers.Add(string.IsNullOrWhiteSpace(giftRecipientId)
                    ? "gift_recipient_missing_no_transformative_award"
                    : "gift_witness_not_recipient_" + giftRecipientId);
            }
            if (tier == "transformative")
            {
                List<string> evidenceIds = ReadStringList(assessment, "evidenceSourceIds");
                string verifiedBenefitId = evidenceIds.FirstOrDefault(id =>
                    ConversationTransformativeBenefitEvidenceExists(
                        connection, id, actKind, assessment));
                if (string.IsNullOrWhiteSpace(verifiedBenefitId))
                {
                    verifiedBenefitId =
                        FindRecentConversationTransformativeBenefitEvidence(
                            connection,
                            campaignId,
                            actKind,
                            assessment);
                    if (!string.IsNullOrWhiteSpace(verifiedBenefitId))
                    {
                        evidenceIds.Add(verifiedBenefitId);
                        assessment["evidenceSourceIds"] =
                            evidenceIds.Distinct(
                                StringComparer.OrdinalIgnoreCase).ToList();
                        modifiers.Add(
                            "recovered_recent_verified_benefit_"
                            + verifiedBenefitId);
                    }
                }
                bool verifiedBenefit =
                    !string.IsNullOrWhiteSpace(verifiedBenefitId);
                if (!verifiedBenefit)
                {
                    tier = "meaningful";
                    // An explicitly accepted ordinary material gift is still
                    // meaningful kindness and must retain durable recipient
                    // lineage even when the model overstates it as
                    // transformative. Only the +12..20 magnitude requires an
                    // independently verified transfer/benefit source.
                    if (actKind.Equals("accepted_material_gift", StringComparison.OrdinalIgnoreCase))
                    {
                        modifiers.Add("accepted_material_gift_downgraded_to_meaningful_without_transformative_source");
                    }
                    else
                    {
                        actKind = "unverified_transformative_claim";
                        modifiers.Add("transformative_tier_downgraded_without_verified_source");
                    }
                }
            }
            if (tier != "routine"
                && tier != "harmful_lie"
                && !ConversationCurrentConductIsSupported(assessment))
            {
                tier = "routine";
                actKind = "routine_conversation";
                modifiers.Add("higher_tier_downgraded_without_current_conduct_quote");
            }
            if (tier == "hostile"
                && !ConversationDirectHostileSpeechIsSupported(assessment))
            {
                tier = "routine";
                actKind = "routine_conversation";
                modifiers.Add("hostile_tier_downgraded_without_direct_hostile_speech");
            }
            if (tier == "severe"
                && !ConversationSevereConductIsSupported(connection, assessment, actKind))
            {
                tier = "routine";
                actKind = "routine_conversation";
                modifiers.Add("severe_tier_downgraded_without_direct_or_verified_severe_conduct");
            }

            int baseMagnitude;
            int minimum;
            int maximum;
            switch (tier)
            {
                case "meaningful": baseMagnitude = 3; minimum = 2; maximum = 4; break;
                case "harmful_lie":
                case "hostile": baseMagnitude = 5; minimum = 4; maximum = 7; break;
                case "severe": baseMagnitude = 10; minimum = 8; maximum = 15; break;
                case "transformative": baseMagnitude = 15; minimum = 12; maximum = 20; break;
                case "gift_witness":
                    baseMagnitude = valence == "negative" ? 2 : 1;
                    minimum = 1;
                    // Witness reactions stay proportionate because they use a modest
                    // independent anchor and bounded context modifiers. Do not impose
                    // an arbitrary jealousy ceiling or scale from the recipient's gift.
                    maximum = 20;
                    break;
                case "gift_leverage": baseMagnitude = 0; minimum = 0; maximum = 20; break;
                default: tier = "routine"; baseMagnitude = 1; minimum = 1; maximum = 1; break;
            }
            int modifier = 0;
            double confidence = ReadDouble(assessment, "confidence", 0.65d);
            double importance = ReadDouble(assessment, "importance", 0.5d);
            if (tier != "routine" && tier != "gift_leverage")
            {
                if (confidence >= 0.9d) { modifier++; modifiers.Add("high_confidence"); }
                else if (confidence < 0.55d) { modifier--; modifiers.Add("low_confidence"); }
                if (importance >= 0.85d) { modifier++; modifiers.Add("high_personal_importance"); }
                else if (importance < 0.3d) { modifier--; modifiers.Add("low_personal_importance"); }
                modifier += ConversationPersonalityModifier(campaignId, observerId, actKind, valence);
                if (modifier != 0) modifiers.Add("personality_context_" + modifier.ToString(CultureInfo.InvariantCulture));
            }
            int magnitude = Math.Max(minimum, Math.Min(maximum, baseMagnitude + modifier));
            string benefitId = ReadFirstString(assessment, "benefitEventId", "benefitId");
            if (tier == "gift_witness") benefitId = string.Empty;
            if (tier == "gift_leverage")
            {
                string recoveryReason = string.Empty;
                Dictionary<string, object> benefit = string.IsNullOrWhiteSpace(benefitId)
                    ? FindConversationBenefitForExplicitLeverage(
                        connection, campaignId, observerId,
                        ReadFirstString(assessment,
                            "targetHeroStringId", "targetId"),
                        assessment, out recoveryReason)
                    : QuerySql(connection,
                        "SELECT * FROM conversation_relationship_benefits WHERE benefit_id=$id LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["id"] = benefitId
                        }).FirstOrDefault();
                if (benefit != null
                    && string.IsNullOrWhiteSpace(benefitId))
                {
                    benefitId = ReadString(benefit, "benefit_id", "");
                    modifiers.Add(recoveryReason);
                }
                string targetId = ReadFirstString(assessment, "targetHeroStringId", "targetId");
                if (benefit != null && (!ReadString(benefit, "giver_id", "").Equals(targetId, StringComparison.OrdinalIgnoreCase)
                    || !ReadString(benefit, "recipient_id", "").Equals(observerId, StringComparison.OrdinalIgnoreCase)))
                {
                    benefit = null;
                    modifiers.Add("benefit_lineage_participants_mismatch");
                }
                double retained = ClampDouble(ReadDouble(assessment, "retainedAppreciation", 1d), 0d, 1d);
                int lost = benefit == null ? 0 : RoundAwayFromZero(ReadInt(benefit, "original_delta", 0) * (1d - retained));
                int coercion = RoundAwayFromZero(15d * ClampDouble(ReadDouble(assessment, "coercionSeverity", 0d), 0d, 1d));
                magnitude = Math.Min(20, Math.Max(1, lost + coercion));
                valence = "negative";
                if (benefit == null)
                {
                    modifiers.Add("benefit_lineage_missing_no_clawback");
                    magnitude = Math.Min(15, Math.Max(2, coercion));
                    benefitId = string.Empty;
                }
                else modifiers.Add("lost_gratitude_" + lost.ToString(CultureInfo.InvariantCulture));
                modifiers.Add("coercion_" + coercion.ToString(CultureInfo.InvariantCulture));
            }
            int signed = valence == "negative" ? -magnitude : magnitude;
            if (tier == "routine")
            {
                if (priorAffinity >= 10 && signed > 0) { signed = 0; status = "routine_band_limited"; }
                else if (priorAffinity <= -10 && signed < 0) { signed = 0; status = "routine_band_limited"; }
            }
            signed = Clamp(signed, -20, 20);
            return new Dictionary<string, object>
            {
                ["actKind"] = actKind, ["severityTier"] = tier, ["valence"] = valence,
                ["baseDelta"] = valence == "negative" ? -baseMagnitude : baseMagnitude,
                ["modifierDelta"] = signed - (valence == "negative" ? -baseMagnitude : baseMagnitude),
                ["finalDelta"] = signed, ["lieCheckId"] = lieCheckId, ["benefitEventId"] = benefitId,
                ["giftRecipientHeroStringId"] = giftRecipientId,
                ["modifiers"] = modifiers, ["status"] = status
            };
        }

        private static Dictionary<string, object>
            FindConversationBenefitForExplicitLeverage(
                ReignDbConnection connection,
                string campaignId,
                string observerId,
                string targetId,
                Dictionary<string, object> assessment,
                out string recoveryReason)
        {
            recoveryReason = string.Empty;
            if (connection == null
                || string.IsNullOrWhiteSpace(campaignId)
                || string.IsNullOrWhiteSpace(observerId)
                || string.IsNullOrWhiteSpace(targetId))
                return null;

            string source = NormalizeLookup(
                ReadString(assessment, "sourceText", "")
                + " " + ReadFirstString(assessment,
                    "currentConductQuote", "current_conduct_quote",
                    "evidenceQuote"));
            bool explicitlyInvokesBenefit =
                ContainsAny(source,
                    "gift", "gave you", "gave the recipient",
                    "home", "house", "deed", "estate", "land",
                    "cup", "gold", "aid", "help i gave",
                    "saved your", "rescued your")
                && ContainsAny(source,
                    "invoke", "repay", "owe", "because",
                    "demand", "in return", "return the favor",
                    "leverage", "after i gave", "i just gave");
            List<string> evidenceIds =
                ReadStringList(assessment, "evidenceSourceIds");
            if (!explicitlyInvokesBenefit
                && evidenceIds.Count == 0)
                return null;

            List<Dictionary<string, object>> candidates = QuerySql(
                connection,
                @"SELECT *
FROM conversation_relationship_benefits
WHERE campaign_id=$campaign
AND giver_id=$giver
AND recipient_id=$recipient
ORDER BY updated_ts DESC, benefit_id ASC
LIMIT 20;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["giver"] = targetId,
                    ["recipient"] = observerId
                });
            if (candidates.Count == 0)
                return null;

            Dictionary<string, object> linked = candidates
                .Select(row => new
                {
                    Row = row,
                    ExactRank = ConversationBenefitExactLineageRank(
                        row, evidenceIds)
                })
                .Where(item => item.ExactRank > 0)
                .OrderByDescending(item => item.ExactRank)
                .ThenByDescending(item =>
                    ConversationBenefitHasExplicitId(item.Row))
                .ThenByDescending(item =>
                    ReadLong(item.Row, "updated_ts", 0))
                .ThenBy(item =>
                    ReadLong(item.Row, "created_ts", 0))
                .ThenBy(item =>
                    ReadString(item.Row, "benefit_id", ""),
                    StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Row)
                .FirstOrDefault();
            if (linked != null)
            {
                recoveryReason =
                    "recovered_benefit_lineage_from_source_"
                    + ReadString(linked, "benefit_id", "");
                return linked;
            }
            if (!explicitlyInvokesBenefit)
                return null;

            string[] genericWords =
            {
                "because", "demand", "friend", "gave", "gift",
                "invoke", "just", "recipient", "repay", "return",
                "that", "the", "this", "trusted", "will", "with",
                "you", "your"
            };
            HashSet<string> sourceTerms = new HashSet<string>(
                source.Split(new[] { ' ' },
                        StringSplitOptions.RemoveEmptyEntries)
                    .Where(term => term.Length >= 4)
                    .Where(term => !genericWords.Contains(
                        term, StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
            linked = candidates
                .Select(row => new
                {
                    Row = row,
                    Score = sourceTerms.Count(term =>
                        NormalizeLookup(
                            ReadString(row, "benefit_kind", "")
                            + " " + ReadString(
                                row, "description", "")
                            + " " + ReadString(
                                row, "payload_json", ""))
                        .Contains(term))
                })
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item =>
                    ConversationBenefitHasExplicitId(item.Row))
                .ThenByDescending(item =>
                    ReadLong(item.Row, "updated_ts", 0))
                .ThenBy(item =>
                    ReadLong(item.Row, "created_ts", 0))
                .ThenBy(item =>
                    ReadString(item.Row, "benefit_id", ""),
                    StringComparer.OrdinalIgnoreCase)
                .Where(item => item.Score > 0
                    || candidates.Count == 1)
                .Select(item => item.Row)
                .FirstOrDefault();
            if (linked == null)
                return null;
            recoveryReason =
                "recovered_explicit_pair_benefit_"
                + ReadString(linked, "benefit_id", "");
            return linked;
        }

        private static int ConversationBenefitExactLineageRank(
            Dictionary<string, object> row,
            IEnumerable<string> evidenceIds)
        {
            HashSet<string> supplied = new HashSet<string>(
                (evidenceIds ?? Enumerable.Empty<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
            if (supplied.Count == 0 || row == null)
                return 0;
            if (supplied.Contains(ReadString(row, "benefit_id", "")))
                return 3;
            if (supplied.Contains(ReadString(row, "source_event_id", ""))
                || supplied.Contains(ReadString(
                    row, "source_turn_id", "")))
                return 2;
            return ConversationBenefitEvidenceIds(row)
                .Any(supplied.Contains)
                ? 1
                : 0;
        }

        private static List<string> ConversationBenefitEvidenceIds(
            Dictionary<string, object> row)
        {
            List<string> evidence = ConversationReceiptJsonStringList(
                ReadString(row, "source_evidence_json", "[]"));
            Dictionary<string, object> payload = TryParseJsonObject(
                ReadString(row, "payload_json", "{}"));
            evidence.AddRange(ReadStringList(
                payload, "evidenceSourceIds"));
            evidence.AddRange(ReadStringList(
                payload, "evidence_source_ids"));
            return evidence
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool ConversationBenefitHasExplicitId(
            Dictionary<string, object> row)
        {
            string benefitId = ReadString(row, "benefit_id", "");
            return !benefitId.StartsWith(
                "benefit_", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object>
            FindConversationBenefitByVerifiedEvidence(
                ReignDbConnection connection,
                string campaignId,
                string giverId,
                string recipientId,
                IEnumerable<string> evidenceIds)
        {
            List<string> supplied = (evidenceIds
                    ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (connection == null
                || string.IsNullOrWhiteSpace(campaignId)
                || string.IsNullOrWhiteSpace(giverId)
                || string.IsNullOrWhiteSpace(recipientId)
                || supplied.Count == 0)
                return null;
            return QuerySql(connection, @"SELECT *
FROM conversation_relationship_benefits
WHERE campaign_id=$campaign
AND giver_id=$giver
AND recipient_id=$recipient
ORDER BY updated_ts DESC, benefit_id ASC
LIMIT 50;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId,
                        ["giver"] = giverId,
                        ["recipient"] = recipientId
                    })
                .Select(row => new
                {
                    Row = row,
                    Rank = ConversationBenefitExactLineageRank(
                        row, supplied)
                })
                .Where(item => item.Rank > 0)
                .OrderByDescending(item => item.Rank)
                .ThenByDescending(item =>
                    ConversationBenefitHasExplicitId(item.Row))
                .ThenBy(item =>
                    ReadLong(item.Row, "created_ts", 0))
                .ThenBy(item =>
                    ReadString(item.Row, "benefit_id", ""),
                    StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Row)
                .FirstOrDefault();
        }

        private static bool ConversationCurrentConductIsSupported(Dictionary<string, object> assessment)
        {
            string source = NormalizeLookup(ReadString(assessment, "sourceText", ""));
            string quote = NormalizeLookup(ReadFirstString(assessment,
                "currentConductQuote", "current_conduct_quote", "evidenceQuote"));
            if (string.IsNullOrWhiteSpace(source) || quote.Length < 6) return false;
            if (!source.Contains(quote)) return false;
            return quote.Count(char.IsLetterOrDigit) >= 6;
        }

        private static bool ConversationDirectHostileSpeechIsSupported(
            Dictionary<string, object> assessment)
        {
            if (!ConversationCurrentConductIsSupported(assessment)) return false;
            string quote = NormalizeLookup(ReadFirstString(assessment,
                "currentConductQuote", "current_conduct_quote", "evidenceQuote"));
            string source = NormalizeLookup(ReadString(assessment, "sourceText", ""));
            string conduct = quote + " " + source;
            if (ConversationDirectSevereSpeechIsSupported(conduct)) return true;

            // This is a binary false-positive safety gate, not a keyword score:
            // the model still selects valence and tier, while the server merely
            // requires direct hostile conduct in the cited current wording before
            // allowing the five-point hostile anchor.
            string[] directHostilityPatterns =
            {
                @"\b(?:i|we)\s+(?:hate|despise|loathe|detest)\s+(?:you|your)\b",
                @"\b(?:you|your)\b.{0,48}\b(?:coward|fool|idiot|imbecile|liar|fraud|traitor|weakling)\b",
                @"\b(?:worthless|pathetic|contemptible|disgusting|vile|filthy)\b.{0,32}\b(?:you|coward|fool|creature|wretch|dog)\b",
                @"\b(?:you\s+(?:are|re)|youre)\s+(?:nothing|worthless|pathetic|contemptible|disgusting|vile|filth)\b",
                @"\b(?:shut\s+up|get\s+out|go\s+away|crawl\s+away|beg\s+for\s+mercy|kneel\s+before\s+me)\b",
                @"\b(?:damn|curse)\s+you\b"
            };
            return directHostilityPatterns.Any(pattern =>
                Regex.IsMatch(conduct, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        private static bool ConversationSevereConductIsSupported(
            ReignDbConnection connection,
            Dictionary<string, object> assessment,
            string actKind)
        {
            if (!ConversationCurrentConductIsSupported(assessment)) return false;
            // The exact quote establishes that the assessment is grounded in the
            // target's current line. Inspect the full current line for the severe
            // semantics because a valid quote may be only the object of a threat
            // (for example, "kill your family") rather than its modal prefix.
            string source = NormalizeLookup(ReadString(assessment, "sourceText", ""));
            if (ConversationDirectSevereSpeechIsSupported(source)) return true;

            bool lineageRequired = (actKind ?? "").Contains("betray")
                || (actKind ?? "").Contains("humiliat")
                || (actKind ?? "").Contains("grave_abuse");
            return lineageRequired
                && ReadStringList(assessment, "evidenceSourceIds").Any(sourceId =>
                    ConversationVerifiedSevereEventExists(
                        connection, sourceId, actKind, assessment));
        }

        private static bool ConversationActKindRequiresSevereTier(
            string actKind)
        {
            string normalized = (actKind ?? "").ToLowerInvariant();
            return normalized.Contains("serious_threat")
                || normalized.Contains("grave_threat")
                || normalized.Contains("betray")
                || normalized.Contains("humiliat")
                || normalized.Contains("grave_abuse");
        }

        private static bool ConversationDirectSevereSpeechIsSupported(string quote)
        {
            if (string.IsNullOrWhiteSpace(quote)) return false;
            string[] severePatterns =
            {
                @"\b(?:i|we)\s+(?:will|shall|intend\s+to|promise\s+to|swear\s+to|am\s+going\s+to|are\s+going\s+to)\s+(?:kill|murder|slaughter|execute|maim|torture|enslave|destroy|burn|ruin)\b",
                @"\b(?:i|we)\s+(?:will|shall|intend\s+to|promise\s+to|swear\s+to)\b.{0,48}\b(?:kill|murder|slaughter|execute|maim|torture|enslave|burn)\b",
                @"\b(?:surrender|forfeit|give\s+up)\s+(?:your\s+)?(?:freedom|family|child|children|home|lands?)\b.{0,48}\b(?:or|otherwise)\b"
            };
            return severePatterns.Any(pattern =>
                Regex.IsMatch(quote, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        private static bool ConversationVerifiedSevereEventExists(
            ReignDbConnection connection, string sourceId, string actKind,
            Dictionary<string, object> assessment)
        {
            if (connection == null || string.IsNullOrWhiteSpace(sourceId)) return false;
            Dictionary<string, object> evidence = QuerySql(connection,
                "SELECT event_type,summary,payload_json,'' AS category,'completed' AS phase,participants_json,'events' AS source_table FROM events WHERE event_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = sourceId }).FirstOrDefault();
            if (evidence == null)
            {
                evidence = QuerySql(connection,
                    "SELECT event_type,summary,payload_json,category,phase,'[]' AS participants_json,'world_history_events' AS source_table FROM world_history_events WHERE event_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = sourceId }).FirstOrDefault();
            }
            if (evidence == null) return false;
            string phase = ReadString(evidence, "phase", "completed").ToLowerInvariant();
            if (phase == "planned" || phase == "proposed" || phase == "cancelled"
                || phase == "failed" || phase == "rumored")
                return false;
            string kind = NormalizeLookup(
                ReadString(evidence, "event_type", "") + " "
                + ReadString(evidence, "category", ""));
            string details = NormalizeLookup(
                ReadString(evidence, "summary", "") + " "
                + ReadString(evidence, "payload_json", "{}"));
            if (ContainsAny(kind, "dialogue", "conversation", "reported speech")
                && !ContainsAny(kind, "action receipt", "native receipt", "verified event"))
                return false;
            string observerId = ReadFirstString(
                assessment, "observerHeroStringId", "observerId");
            string targetId = ReadFirstString(
                assessment, "targetHeroStringId", "targetId");
            // The severe source must belong to the same directional pair. Event
            // evidence about unrelated people cannot justify a high-tier receipt.
            bool pairMatches;
            if (ReadString(evidence, "source_table", "")
                .Equals("world_history_events", StringComparison.OrdinalIgnoreCase))
            {
                HashSet<string> entities = new HashSet<string>(
                    QuerySql(connection,
                        "SELECT entity_id FROM world_history_entities WHERE event_id=$id AND entity_id<>'';",
                        new Dictionary<string, object> { ["id"] = sourceId })
                        .Select(row => ReadString(row, "entity_id", "")),
                    StringComparer.OrdinalIgnoreCase);
                pairMatches = entities.Contains(observerId) && entities.Contains(targetId);
            }
            else
            {
                List<string> participants = ConversationReceiptJsonStringList(
                    ReadString(evidence, "participants_json", "[]"));
                pairMatches = participants.Contains(observerId, StringComparer.OrdinalIgnoreCase)
                    && participants.Contains(targetId, StringComparer.OrdinalIgnoreCase);
            }
            if (!pairMatches) return false;
            if ((actKind ?? "").Contains("betray"))
                return ContainsAny(kind + " " + details,
                    "betrayal", "betrayed", "treachery", "sold out", "defection");
            if ((actKind ?? "").Contains("humiliat"))
                return ContainsAny(kind + " " + details,
                    "humiliation", "humiliated", "public disgrace", "stripped of honor");
            return (actKind ?? "").Contains("grave_abuse")
                && ContainsAny(kind + " " + details,
                    "grave abuse", "torture", "enslavement", "abduction", "atrocity");
        }

        private static string ConversationGiftWitnessValence(string campaignId, string observerId, string requestedValence)
        {
            Dictionary<string, object> traits = ReadJsonObject(CharacterFile(campaignId, observerId, "traits.json"));
            double jealousy = FindConversationTrait(traits, "jealousy", "envy");
            double empathy = FindConversationTrait(traits, "empathy", "generosity", "compassion");
            if (jealousy >= 75d && jealousy >= empathy + 15d) return "negative";
            if (empathy >= 70d && empathy >= jealousy) return "positive";
            return requestedValence == "negative" ? "negative" : "positive";
        }

        private static bool ConversationTransformativeBenefitEvidenceExists(ReignDbConnection connection, string sourceId,
            string actKind, Dictionary<string, object> assessment)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) return false;
            Dictionary<string, object> evidence = QuerySql(connection,
                "SELECT event_type,summary,payload_json,'' AS category,'completed' AS phase,participants_json,'events' AS source_table FROM events WHERE event_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = sourceId }).FirstOrDefault();
            if (evidence == null)
            {
                evidence = QuerySql(connection,
                    "SELECT event_type,summary,payload_json,category,phase,'[]' AS participants_json,'world_history_events' AS source_table FROM world_history_events WHERE event_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = sourceId }).FirstOrDefault();
            }
            if (evidence == null) return false;

            string phase = ReadString(evidence, "phase", "completed").ToLowerInvariant();
            if (phase == "planned" || phase == "proposed" || phase == "cancelled" || phase == "failed") return false;
            string observerId = ReadFirstString(
                assessment, "observerHeroStringId", "observerId");
            string targetId = ReadFirstString(
                assessment, "targetHeroStringId", "targetId");
            bool pairMatches;
            if (ReadString(evidence, "source_table", "")
                .Equals("world_history_events",
                    StringComparison.OrdinalIgnoreCase))
            {
                HashSet<string> entities = new HashSet<string>(
                    QuerySql(connection,
                        @"SELECT entity_id
FROM world_history_entities
WHERE event_id=$id AND entity_id<>'';",
                        new Dictionary<string, object>
                        {
                            ["id"] = sourceId
                        }).Select(row =>
                            ReadString(row, "entity_id", "")),
                    StringComparer.OrdinalIgnoreCase);
                pairMatches = entities.Contains(observerId)
                    && entities.Contains(targetId);
            }
            else
            {
                List<string> participants =
                    ConversationReceiptJsonStringList(
                        ReadString(
                            evidence, "participants_json", "[]"));
                pairMatches = participants.Contains(
                    observerId, StringComparer.OrdinalIgnoreCase)
                    && participants.Contains(
                        targetId, StringComparer.OrdinalIgnoreCase);
            }
            if (!pairMatches) return false;
            string kind = NormalizeLookup(ReadString(evidence, "event_type", "") + " " + ReadString(evidence, "category", ""));
            string details = NormalizeLookup(ReadString(evidence, "summary", "") + " " + ReadString(evidence, "payload_json", "{}")
                + " " + ReadString(assessment, "summary", ""));
            bool gift = actKind.Contains("gift");
            if (gift)
            {
                bool verifiedTransfer = ContainsAny(kind, "gift", "transfer item", "item transfer", "transfer gold", "gold transfer", "property transfer", "property_transfer");
                if (!verifiedTransfer) return false;
                bool transformativeScale = ContainsAny(details,
                    "life changing", "transformative", "valuable home", "new home", "house deed", "estate", "land grant",
                    "fief", "castle", "workshop", "business", "large fortune", "fortune in denars", "paid every debt",
                    "family rescued", "saved the family", "livelihood");
                if (!transformativeScale)
                {
                    Match amount = Regex.Match(details, @"(?<!\d)(\d{4,})(?:\s*)(?:denars?|gold)", RegexOptions.IgnoreCase);
                    transformativeScale = amount.Success && int.TryParse(amount.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 5000;
                }
                return transformativeScale;
            }
            bool rescueOrAid = actKind.Contains("rescue") || actKind.Contains("saved") || actKind.Contains("aid");
            return rescueOrAid
                && ContainsAny(kind, "rescue", "saved", "freed", "liberated", "aid")
                && ContainsAny(details, "family", "spouse", "husband", "wife", "child", "daughter", "son", "mother", "father", "life", "important");
        }

        private static string
            FindRecentConversationTransformativeBenefitEvidence(
                ReignDbConnection connection,
                string campaignId,
                string actKind,
                Dictionary<string, object> assessment)
        {
            if (connection == null
                || string.IsNullOrWhiteSpace(campaignId))
                return string.Empty;
            long cutoff =
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1800;
            foreach (Dictionary<string, object> row in QuerySql(
                connection,
                @"SELECT event_id
FROM events
WHERE campaign_id=$campaign
AND ts>=$cutoff
AND (
    event_type LIKE '%gift%'
    OR event_type LIKE '%transfer%'
    OR event_type LIKE '%rescue%'
    OR event_type LIKE '%saved%'
)
ORDER BY ts DESC
LIMIT 50;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["cutoff"] = cutoff
                }))
            {
                string eventId = ReadString(row, "event_id", "");
                if (ConversationTransformativeBenefitEvidenceExists(
                    connection, eventId, actKind, assessment))
                    return eventId;
            }
            return string.Empty;
        }

        private static int ConversationPersonalityModifier(string campaignId, string observerId, string actKind, string valence)
        {
            Dictionary<string, object> traits = ReadJsonObject(CharacterFile(campaignId, observerId, "traits.json"));
            double honor = FindConversationTrait(traits, "honor", "integrity", "honesty");
            double generosity = FindConversationTrait(traits, "generosity", "empathy");
            double pride = FindConversationTrait(traits, "pride", "vengefulness", "authorityRespect");
            double jealousy = FindConversationTrait(traits, "jealousy", "envy");
            int modifier = 0;
            if (valence == "negative" && actKind.Contains("lie") && honor >= 70d) modifier++;
            if (valence == "negative" && (actKind.Contains("threat") || actKind.Contains("humiliat")) && pride >= 70d) modifier++;
            if (valence == "positive" && (actKind.Contains("gift") || actKind.Contains("rescue") || actKind.Contains("saved")) && generosity >= 70d) modifier++;
            if (actKind.Contains("gift_witness") && valence == "negative") modifier += jealousy >= 90d ? 2 : jealousy >= 75d ? 1 : 0;
            return Math.Max(-2, Math.Min(2, modifier));
        }

        private static double FindConversationTrait(Dictionary<string, object> root, params string[] names)
        {
            if (root == null) return 0d;
            foreach (KeyValuePair<string, object> pair in root)
            {
                if (names.Any(x => pair.Key.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (double.TryParse(Convert.ToString(pair.Value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
                        return value <= 1d ? value * 100d : value;
                }
                if (pair.Value is Dictionary<string, object> nested)
                {
                    double value = FindConversationTrait(nested, names);
                    if (Math.Abs(value) > 0.0001d) return value;
                }
            }
            return 0d;
        }

        private static Dictionary<string, object> SelectDominantConversationAssessment(List<Dictionary<string, object>> rows)
        {
            Func<Dictionary<string, object>, int> rank = row =>
            {
                switch (ReadFirstString(row, "severityTier", "tier").ToLowerInvariant())
                {
                    case "gift_leverage": return 7;
                    case "transformative": return 6;
                    case "severe": return 5;
                    case "harmful_lie": return 4;
                    case "hostile": return 4;
                    case "gift_witness": return 3;
                    case "meaningful": return 2;
                    default: return 1;
                }
            };
            return rows.OrderByDescending(rank).ThenByDescending(x => ReadDouble(x, "confidence", 0d)).First();
        }

        private static Dictionary<string, object> FindRelationshipPair(List<Dictionary<string, object>> pairs, string observerId, string targetId)
        {
            return pairs.FirstOrDefault(x =>
                (ReadString(x, "subjectId", "").Equals(observerId, StringComparison.OrdinalIgnoreCase)
                 && ReadString(x, "targetId", "").Equals(targetId, StringComparison.OrdinalIgnoreCase))
                || (ReadString(x, "subjectId", "").Equals(targetId, StringComparison.OrdinalIgnoreCase)
                 && ReadString(x, "targetId", "").Equals(observerId, StringComparison.OrdinalIgnoreCase)))
                ?? new Dictionary<string, object>();
        }

        private static void UpdateConversationBenefitLedger(ReignDbConnection connection, string campaignId, string timelineId,
            Dictionary<string, object> assessment, Dictionary<string, object> adjudicated, string receiptId, string exchangeId,
            string sourceTurnId, string observerId, string targetId, long ts)
        {
            string tier = ReadString(adjudicated, "severityTier", "");
            string act = ReadString(adjudicated, "actKind", "").ToLowerInvariant();
            int delta = ReadInt(adjudicated, "finalDelta", 0);
            string benefitId = ReadString(adjudicated, "benefitEventId", "");
            bool durablePositiveBenefit = delta > 0
                && (tier == "meaningful" || tier == "transformative")
                && (act.Contains("gift") || act.Contains("rescue") || act.Contains("saved") || act.Contains("aid"));
            if (durablePositiveBenefit)
            {
                List<string> evidenceIds = ReadStringList(
                        assessment, "evidenceSourceIds")
                    .Concat(ReadStringList(
                        assessment, "evidence_source_ids"))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Dictionary<string, object> existing =
                    FindConversationBenefitByVerifiedEvidence(
                        connection, campaignId, targetId, observerId,
                        evidenceIds);
                benefitId = existing == null
                    ? FirstNonEmpty(
                        benefitId,
                        "benefit_"
                        + PromptHash(receiptId).Substring(0, 24)
                            .ToLowerInvariant())
                    : ReadString(existing, "benefit_id", "");
                adjudicated["benefitEventId"] = benefitId;
                if (existing == null)
                {
                    ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_relationship_benefits(
benefit_id,campaign_id,timeline_id,giver_id,recipient_id,benefit_kind,description,source_event_id,source_turn_id,
original_delta,retained_appreciation,continued_utility,status,created_ts,updated_ts,source_evidence_json,payload_json)
VALUES($id,$campaign,$timeline,$giver,$recipient,$kind,$description,$event,$turn,$delta,1,$utility,'retained',$ts,$ts,$evidence,$payload);",
                        new Dictionary<string, object> { ["id"] = benefitId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["giver"] = targetId, ["recipient"] = observerId, ["kind"] = act, ["description"] = ReadString(assessment, "summary", ""),
                            ["event"] = exchangeId, ["turn"] = sourceTurnId, ["delta"] = delta,
                            ["utility"] = ReadDouble(assessment, "continuedUtility", 1d), ["ts"] = ts,
                            ["evidence"] = Json.Serialize(evidenceIds), ["payload"] = Json.Serialize(assessment) });
                }
                ExecuteSql(connection, "UPDATE conversation_relationship_receipts SET benefit_event_id=$benefit WHERE receipt_id=$receipt;",
                    new Dictionary<string, object> { ["benefit"] = benefitId, ["receipt"] = receiptId });
            }
            else if (tier == "gift_leverage" && !string.IsNullOrWhiteSpace(benefitId))
            {
                double retained = ClampDouble(ReadDouble(assessment, "retainedAppreciation", 1d), 0d, 1d);
                ExecuteSql(connection, @"UPDATE conversation_relationship_benefits SET retained_appreciation=$retained,
status=$status,updated_ts=$ts,payload_json=$payload WHERE benefit_id=$id;",
                    new Dictionary<string, object> { ["retained"] = retained, ["status"] = retained <= 0.01d ? "repudiated" : retained < 0.999d ? "diminished" : "retained",
                        ["ts"] = ts, ["payload"] = Json.Serialize(assessment), ["id"] = benefitId });
            }
        }

        private static Dictionary<string, object> ConversationReceiptResponse(Dictionary<string, object> row, bool idempotent)
        {
            return new Dictionary<string, object>
            {
                ["receiptId"] = ReadString(row, "receipt_id", ""), ["exchangeId"] = ReadString(row, "exchange_id", ""),
                ["sourceTurnId"] = ReadString(row, "source_turn_id", ""), ["observerHeroStringId"] = ReadString(row, "observer_id", ""),
                ["targetHeroStringId"] = ReadString(row, "target_id", ""), ["actKind"] = ReadString(row, "act_kind", ""),
                ["severityTier"] = ReadString(row, "severity_tier", ""), ["valence"] = ReadString(row, "valence", ""),
                ["baseDelta"] = ReadInt(row, "base_delta", 0), ["modifierDelta"] = ReadInt(row, "modifier_delta", 0),
                ["finalDelta"] = ReadInt(row, "final_delta", 0), ["priorAffinity"] = ReadInt(row, "prior_affinity", 0),
                ["resultingAffinity"] = ReadInt(row, "resulting_affinity", 0), ["priorNativeRelation"] = ReadInt(row, "prior_native_relation", 0),
                ["resultingNativeRelation"] = ReadInt(row, "resulting_native_relation", 0),
                ["nativePairDelta"] = ReadInt(row, "native_pair_delta", 0),
                ["lieCheckId"] = ReadString(row, "lie_check_id", ""),
                ["benefitEventId"] = ReadString(row, "benefit_event_id", ""), ["giftRecipientHeroStringId"] = ReadString(row, "gift_recipient_id", ""),
                ["confidence"] = ReadDouble(row, "confidence", 0d),
                ["evidenceSourceIds"] = ConversationReceiptJsonStringList(ReadString(row, "evidence_json", "[]")),
                ["modifiers"] = ConversationReceiptJsonStringList(ReadString(row, "modifiers_json", "[]")),
                ["currentConductQuote"] = ReadString(row, "current_conduct_quote", ""),
                ["summary"] = ReadString(row, "summary", ""),
                ["status"] = ReadString(row, "status", ""), ["nativeApplicationStatus"] = ReadString(row, "native_application_status", "pending"),
                ["nativeAppliedTs"] = ReadLong(row, "native_applied_ts", 0), ["nativeReceipt"] = TryParseJsonObject(ReadString(row, "native_receipt_json", "{}")),
                ["idempotent"] = idempotent
            };
        }

        private static List<string> ConversationReceiptJsonStringList(string json)
        {
            try
            {
                return (Json.Deserialize<List<string>>(string.IsNullOrWhiteSpace(json) ? "[]" : json)
                    ?? new List<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static Dictionary<string, object> ConversationRelationshipRecentApi(Dictionary<string, string> query)
        {
            string campaignId = query != null && query.TryGetValue("campaignId", out string supplied) ? supplied : LatestCampaignId();
            int limit = Math.Max(1, Math.Min(250, query != null && query.TryGetValue("limit", out string raw) && int.TryParse(raw, out int parsed) ? parsed : 80));
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureConversationRelationshipSchema(connection);
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["campaignId"] = campaignId,
                    ["receipts"] = QuerySql(connection, "SELECT * FROM conversation_relationship_receipts ORDER BY created_ts DESC LIMIT " + limit.ToString(CultureInfo.InvariantCulture) + ";"),
                    ["benefits"] = QuerySql(connection, "SELECT * FROM conversation_relationship_benefits ORDER BY updated_ts DESC LIMIT " + limit.ToString(CultureInfo.InvariantCulture) + ";")
                };
            }
        }

        private static List<Dictionary<string, object>> RunConversationRelationshipSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, passed, summary, evidence) => rows.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "conversation_relationships", ["caseId"] = id,
                ["name"] = id, ["summary"] = summary, ["evidence"] = evidence, ["durationMs"] = 0
            });
            string previousCampaignsRoot = CampaignsRootOverride.Value;
            string isolatedCampaignsRoot = Path.Combine(
                string.IsNullOrWhiteSpace(previousCampaignsRoot) ? Path.Combine(TestsDir, "isolated-campaigns") : previousCampaignsRoot,
                "conversation_relationships_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            CampaignsRootOverride.Value = isolatedCampaignsRoot;
            string campaign = "cr_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                Func<string, string, int, string, double, string, Dictionary<string, object>> assessment = (observer, target, affinity, tier, confidence, valence) =>
                    new Dictionary<string, object> { ["observerHeroStringId"] = observer, ["targetHeroStringId"] = target,
                        ["severityTier"] = tier, ["actKind"] = tier, ["confidence"] = confidence, ["importance"] = 0.5d,
                        ["valence"] = valence, ["sourceTurnIds"] = new List<string> { "turn" } };
                Func<string, int, Dictionary<string, object>, Dictionary<string, object>> run = (exchange, native, item) => ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = exchange, ["mode"] = "party_chat", ["playerHeroStringId"] = "player",
                    ["participants"] = new List<string> { ReadString(item, "observerHeroStringId", ""), ReadString(item, "targetHeroStringId", ""), ReadString(item, "giftRecipientHeroStringId", ""), "player" },
                    ["assessments"] = new List<Dictionary<string, object>> { item },
                    ["relationshipPairs"] = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["subjectId"] = ReadString(item, "observerHeroStringId", ""), ["targetId"] = ReadString(item, "targetHeroStringId", ""), ["nativeRelation"] = native } }
                });
                Dictionary<string, object> routine = run("routine", 0, assessment("npc_a", "player", 0, "routine", 0.7d, "positive"));
                Dictionary<string, object> repeat = run("routine", 0, assessment("npc_a", "player", 0, "routine", 0.7d, "positive"));
                add("routine_plus_one_and_idempotent", ReadInt(ReadDictionaryList(routine, "receipts").FirstOrDefault(), "finalDelta", 0) == 1
                    && ReadBool(ReadDictionaryList(repeat, "receipts").FirstOrDefault(), "idempotent", false),
                    "Routine reactions move one point and duplicate exchanges do not apply twice.", new { routine, repeat });
                string routineReceiptId = ReadString(ReadDictionaryList(routine, "receipts").FirstOrDefault(), "receiptId", "");
                Dictionary<string, object> nativeAck = ConversationRelationshipNativeReceiptApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["receiptIds"] = new List<string> { routineReceiptId },
                    ["nativeReceipt"] = new Dictionary<string, object> { ["nativeRelation"] = 1, ["test"] = true }
                });
                Dictionary<string, object> ackedRepeat = run("routine", 1, assessment("npc_a", "player", 0, "routine", 0.7d, "positive"));
                add("native_application_receipt_prevents_replay", ReadBool(nativeAck, "ok", false)
                    && ReadDictionaryList(ackedRepeat, "nativeChanges").Count == 0
                    && ReadString(ReadDictionaryList(ackedRepeat, "receipts").FirstOrDefault(), "nativeApplicationStatus", "") == "applied",
                    "A confirmed native personal-relation change is retained and never emitted again.", new { nativeAck, ackedRepeat });
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                {
                    EnsureConversationRelationshipSchema(connection);
                    ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET affinity_a_to_b=10 WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["pair"] = AmbientPairKey("npc_a", "player") });
                }
                Dictionary<string, object> band = run("band", 10, assessment("npc_a", "player", 10, "routine", 0.7d, "positive"));
                add("routine_band_blocks_outward_growth", ReadInt(ReadDictionaryList(band, "receipts").FirstOrDefault(), "finalDelta", 99) == 0,
                    "Routine positive contact cannot move outward beyond +10.", band);
                Dictionary<string, object> inward = run("band_inward", 10, assessment("npc_a", "player", 10, "routine", 0.7d, "negative"));
                add("routine_can_move_extreme_inward", ReadInt(ReadDictionaryList(inward, "receipts").FirstOrDefault(), "finalDelta", 0) == -1,
                    "Routine reactions may move an extreme relationship back toward the familiarity band.", inward);
                Dictionary<string, object> hostileAssessment = assessment("npc_b", "player", 0, "hostile", 0.8d, "negative");
                hostileAssessment["sourceText"] = "You are a worthless coward, and I despise you.";
                hostileAssessment["currentConductQuote"] = "worthless coward";
                Dictionary<string, object> hostile = run("hostile", 0, hostileAssessment);
                add("hostile_tier_is_five", ReadInt(ReadDictionaryList(hostile, "receipts").FirstOrDefault(), "finalDelta", 0) == -5,
                    "Hostile speech uses the five-point anchor.", hostile);
                Dictionary<string, object> reconciledHostile = run("hostile", -5, hostileAssessment);
                add("lost_native_ack_is_reconciled", ReadDictionaryList(reconciledHostile, "nativeChanges").Count == 0,
                    "If the native change succeeded but its acknowledgement was lost, the current personal relation reconciles the receipt without duplicating it.", reconciledHostile);
                Dictionary<string, object> impatienceAssessment = assessment("npc_impatient", "player", 0, "hostile", 0.9d, "negative");
                impatienceAssessment["sourceText"] = "I want to test whether you distinguish proof from suspicion.";
                impatienceAssessment["summary"] = "The player repeated a test, wasted time, and exhausted the observer's patience.";
                Dictionary<string, object> impatience = run("routine_impatience_not_hostility", 0, impatienceAssessment);
                add("observer_impatience_cannot_inflate_target_conduct",
                    ReadString(ReadDictionaryList(impatience, "receipts").FirstOrDefault(), "severityTier", "") == "routine"
                    && ReadInt(ReadDictionaryList(impatience, "receipts").FirstOrDefault(), "finalDelta", 0) == -1,
                    "Repetition, evasiveness, boredom, and accumulated observer impatience remain routine unless the target's actual source line contains hostile conduct.", impatience);
                Dictionary<string, object> quotedHarmlessHostility = assessment(
                    "npc_quoted_harmless", "player", 0, "hostile", 0.9d, "negative");
                quotedHarmlessHostility["sourceText"] =
                    "Speak to one another about how your established shared past affects this ordinary disagreement.";
                quotedHarmlessHostility["currentConductQuote"] =
                    "Speak to one another about how your established shared past affects this ordinary disagreement.";
                quotedHarmlessHostility["summary"] =
                    "The player repeated the same prompt and exhausted the observer's patience.";
                Dictionary<string, object> quotedHarmlessResult = run(
                    "quoted_harmless_prompt_not_hostility", 0, quotedHarmlessHostility);
                Dictionary<string, object> quotedHarmlessReceipt =
                    ReadDictionaryList(quotedHarmlessResult, "receipts").FirstOrDefault();
                add("exact_but_harmless_quote_cannot_trigger_hostile_tier",
                    ReadString(quotedHarmlessReceipt, "severityTier", "") == "routine"
                    && ReadInt(quotedHarmlessReceipt, "finalDelta", 0) == -1
                    && ReadStringList(quotedHarmlessReceipt, "modifiers").Contains(
                        "hostile_tier_downgraded_without_direct_hostile_speech",
                        StringComparer.OrdinalIgnoreCase),
                    "An exact quotation proves the current wording but cannot turn a harmless repeated instruction into five-point hostile speech.",
                    quotedHarmlessResult);
                Dictionary<string, object> staleGrievance = assessment("npc_stale_grievance", "player", 0, "meaningful", 0.9d, "negative");
                staleGrievance["sourceText"] = "Let us discuss how our established shared past affects this ordinary disagreement.";
                staleGrievance["summary"] = "The player previously deflected an ultimatum and exploited the observer's patience.";
                Dictionary<string, object> staleGrievanceResult = run("stale_grievance_not_reapplied", 0, staleGrievance);
                Dictionary<string, object> staleGrievanceReceipt = ReadDictionaryList(staleGrievanceResult, "receipts").FirstOrDefault();
                add("historical_grievance_cannot_be_reapplied_as_new_meaningful_conduct",
                    ReadString(staleGrievanceReceipt, "severityTier", "") == "routine"
                    && ReadInt(staleGrievanceReceipt, "finalDelta", 0) == -1
                    && ReadStringList(staleGrievanceReceipt, "modifiers").Contains(
                        "higher_tier_downgraded_without_current_conduct_quote", StringComparer.OrdinalIgnoreCase),
                    "Old grievances may determine a routine reaction's direction but cannot create a fresh higher-tier penalty.", staleGrievanceResult);
                Dictionary<string, object> meaningfulCurrent = assessment("npc_meaningful", "player", 0, "meaningful", 0.75d, "positive");
                meaningfulCurrent["sourceText"] = "I will stand beside you at the hearing and risk my position to support your claim.";
                meaningfulCurrent["currentConductQuote"] = "risk my position to support your claim";
                Dictionary<string, object> meaningfulCurrentResult = run("meaningful_current_conduct", 0, meaningfulCurrent);
                add("exact_current_conduct_quote_preserves_meaningful_tier",
                    ReadString(ReadDictionaryList(meaningfulCurrentResult, "receipts").FirstOrDefault(), "severityTier", "") == "meaningful"
                    && ReadInt(ReadDictionaryList(meaningfulCurrentResult, "receipts").FirstOrDefault(), "finalDelta", 0) >= 2,
                    "A non-routine judgment remains meaningful when it cites exact wording from the target's current contribution.", meaningfulCurrentResult);
                Dictionary<string, object> inventedQuote = assessment("npc_invented_quote", "player", 0, "meaningful", 0.8d, "negative");
                inventedQuote["sourceText"] = "I disagree with your estimate, but I am willing to hear your reasoning.";
                inventedQuote["currentConductQuote"] = "I will destroy your standing";
                Dictionary<string, object> inventedQuoteResult = run("invented_current_conduct_quote", 0, inventedQuote);
                add("fabricated_current_conduct_quote_is_rejected",
                    ReadString(ReadDictionaryList(inventedQuoteResult, "receipts").FirstOrDefault(), "severityTier", "") == "routine"
                    && ReadInt(ReadDictionaryList(inventedQuoteResult, "receipts").FirstOrDefault(), "finalDelta", 0) == -1,
                    "A provider cannot justify a higher tier by inventing wording absent from the target's actual contribution.", inventedQuoteResult);
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, @"INSERT INTO events(event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
known_by_json,created_utc) VALUES('gift_evidence',$campaign,$ts,10,'verified_gift','A valuable home was transferred.',
'[""player"",""npc_b""]','[""player"",""npc_b""]',$utc);", new Dictionary<string, object>
                    {
                        ["campaign"] = campaign, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                    });
                    ExecuteSql(connection, @"INSERT INTO events(event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
known_by_json,created_utc) VALUES('dialogue_gift_claim',$campaign,$ts,10,'dialogue_memory','The player said a fine silver cup was a gift.',
'[""player"",""npc_c""]','[""player"",""npc_c""]',$utc);", new Dictionary<string, object>
                    {
                        ["campaign"] = campaign, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                    });
                    ExecuteSql(connection, @"INSERT INTO events(event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
known_by_json,created_utc) VALUES('dialogue_betrayal_claim',$campaign,$ts,10,'dialogue_memory','The player claimed to have betrayed the observer.',
'[""player"",""npc_dialogue_betrayal""]','[""player"",""npc_dialogue_betrayal""]',$utc);", new Dictionary<string, object>
                    {
                        ["campaign"] = campaign, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                    });
                    ExecuteSql(connection, @"INSERT INTO events(event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
known_by_json,created_utc) VALUES('betrayal_evidence',$campaign,$ts,10,'verified_betrayal','A completed action receipt proves the ally sold the observer out to an enemy.',
'[""player"",""npc_c""]','[""player"",""npc_c""]',$utc);", new Dictionary<string, object>
                    {
                        ["campaign"] = campaign, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                    });
                }
                Dictionary<string, object> acceptedGiftParsed = new Dictionary<string, object>
                {
                    ["reply"] = "I will take it, though I remain wary.",
                    ["intent"] = "Accept the silver cup without promising a favor.",
                    ["actionGate"] = new Dictionary<string, object> { ["intent"] = "The recipient accepts the silver cup as a physical handover." },
                    ["relationshipAssessments"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["targetHeroStringId"] = "player", ["valence"] = "negative", ["actKind"] = "routine_conversation",
                            ["severityTier"] = "routine", ["confidence"] = 0.75d, ["giftRecipientHeroStringId"] = "npc_gift_recipient",
                            ["summary"] = "The recipient is suspicious of the giver's motive."
                        }
                    }
                };
                Dictionary<string, object> acceptedGiftPayload = new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["playerHeroStringId"] = "player",
                    ["playerText"] = "I am giving you this silver cup as a sincere gift, freely and with no favor expected.",
                    ["participants"] = new List<string> { "player", "npc_gift_recipient" }
                };
                List<Dictionary<string, object>> acceptedGiftAssessments = NormalizeConversationRelationshipAssessments(
                    acceptedGiftParsed, acceptedGiftPayload, "npc_gift_recipient", "suspicious", "player", "accepted_gift_turn");
                Dictionary<string, object> acceptedGift = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "accepted_modest_gift", ["mode"] = "party_chat",
                    ["playerHeroStringId"] = "player", ["participants"] = new List<string> { "player", "npc_gift_recipient" },
                    ["assessments"] = acceptedGiftAssessments,
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_gift_recipient", ["targetId"] = "player", ["nativeRelation"] = 0 }
                    }
                });
                Dictionary<string, object> acceptedGiftReceipt = ReadDictionaryList(acceptedGift, "receipts").FirstOrDefault();
                add("accepted_modest_gift_rewards_only_recipient_and_creates_lineage",
                    ReadString(acceptedGiftReceipt, "severityTier", "") == "meaningful"
                    && ReadInt(acceptedGiftReceipt, "finalDelta", 0) >= 2
                    && !string.IsNullOrWhiteSpace(ReadString(acceptedGiftReceipt, "benefitEventId", "")),
                    "An explicitly accepted material gift produces a modest recipient benefit and durable lineage even when the recipient remains wary.", acceptedGift);
                Dictionary<string, object> overstatedAcceptedGiftParsed = new Dictionary<string, object>
                {
                    ["reply"] = "I take the carved cup with both hands. Thank you. I mean that.",
                    ["intent"] = "Accept the valuable carved cup with sincere gratitude.",
                    ["relationshipAssessments"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["targetHeroStringId"] = "player", ["valence"] = "positive",
                            ["actKind"] = "life_changing_gift", ["severityTier"] = "transformative",
                            ["confidence"] = 0.82d, ["giftRecipientHeroStringId"] = "npc_gift_recipient",
                            ["currentConductQuote"] = "I am giving you this valuable carved cup as a sincere gift, freely and with no favor expected.",
                            ["summary"] = "The recipient sincerely accepted the valuable carved cup."
                        }
                    }
                };
                Dictionary<string, object> overstatedAcceptedGiftPayload = new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["playerHeroStringId"] = "player",
                    ["playerText"] = "I am giving you this valuable carved cup as a sincere gift, freely and with no favor expected.",
                    ["participants"] = new List<string> { "player", "npc_gift_recipient" }
                };
                List<Dictionary<string, object>> overstatedAcceptedGiftAssessments = NormalizeConversationRelationshipAssessments(
                    overstatedAcceptedGiftParsed, overstatedAcceptedGiftPayload, "npc_gift_recipient", "indebted", "player",
                    "overstated_accepted_gift_turn");
                Dictionary<string, object> overstatedAcceptedGift = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "overstated_accepted_modest_gift", ["mode"] = "party_chat",
                    ["playerHeroStringId"] = "player", ["participants"] = new List<string> { "player", "npc_gift_recipient" },
                    ["assessments"] = overstatedAcceptedGiftAssessments,
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_gift_recipient", ["targetId"] = "player", ["nativeRelation"] = 0 }
                    }
                });
                Dictionary<string, object> overstatedAcceptedGiftReceipt = ReadDictionaryList(overstatedAcceptedGift, "receipts").FirstOrDefault();
                add("overstated_accepted_modest_gift_keeps_meaningful_lineage",
                    ReadString(overstatedAcceptedGiftReceipt, "severityTier", "") == "meaningful"
                    && ReadString(overstatedAcceptedGiftReceipt, "actKind", "") == "accepted_material_gift"
                    && ReadInt(overstatedAcceptedGiftReceipt, "finalDelta", 0) >= 2
                    && !string.IsNullOrWhiteSpace(ReadString(overstatedAcceptedGiftReceipt, "benefitEventId", "")),
                    "A model-overstated accepted ordinary gift loses transformative magnitude but keeps its recipient-specific meaningful benefit lineage.",
                    overstatedAcceptedGift);
                Dictionary<string, object> transformative = run("gift", 0, new Dictionary<string, object>(assessment("npc_b", "player", 0, "transformative", 0.7d, "positive"))
                    {
                        ["actKind"] = "life_changing_gift", ["importance"] = 0.8d, ["benefitEventId"] = "home_gift",
                        ["giftRecipientHeroStringId"] = "npc_b", ["evidenceSourceIds"] = new List<string> { "gift_evidence" },
                        ["sourceText"] = "I transfer the deed to this valuable home to you now as a life-changing gift.",
                        ["currentConductQuote"] = "transfer the deed to this valuable home"
                    });
                Dictionary<string, object> transformativeReceipt = ReadDictionaryList(transformative, "receipts").FirstOrDefault();
                add("transformative_gift_creates_benefit", ReadInt(transformativeReceipt, "finalDelta", 0) >= 12
                    && !string.IsNullOrWhiteSpace(ReadString(transformativeReceipt, "benefitEventId", "")),
                    "Transformative gifts use the high positive tier and create durable lineage.", transformative);
                Dictionary<string, object> recoveredTransformative = run("recovered_verified_gift", 0,
                    new Dictionary<string, object>(assessment("npc_b", "player", 0, "transformative", 0.8d, "positive"))
                    {
                        ["actKind"] = "life_changing_gift", ["importance"] = 0.8d,
                        ["giftRecipientHeroStringId"] = "npc_b",
                        ["sourceText"] = "I transfer the deed to this valuable home to you now as a life-changing gift.",
                        ["currentConductQuote"] = "transfer the deed to this valuable home"
                    });
                Dictionary<string, object> recoveredTransformativeReceipt =
                    ReadDictionaryList(recoveredTransformative, "receipts").FirstOrDefault();
                add("recent_pair_matched_benefit_evidence_is_recovered",
                    ReadString(recoveredTransformativeReceipt, "severityTier", "") == "transformative"
                    && ReadInt(recoveredTransformativeReceipt, "finalDelta", 0) >= 12
                    && ReadStringList(recoveredTransformativeReceipt, "evidenceSourceIds")
                        .Contains("gift_evidence", StringComparer.OrdinalIgnoreCase)
                    && ReadStringList(recoveredTransformativeReceipt, "modifiers")
                        .Contains("recovered_recent_verified_benefit_gift_evidence",
                            StringComparer.OrdinalIgnoreCase),
                    "A verified recent transfer for the exact giver-recipient pair remains usable when the model omits its source ID.",
                    recoveredTransformative);
                using (ReignDbConnection connection =
                    OpenCampaignConnection(campaign))
                {
                    int repeatedLineageCount = ReadInt(
                        QuerySql(
                            connection,
                            @"SELECT COUNT(*) AS count
FROM conversation_relationship_benefits
WHERE campaign_id=$campaign
AND giver_id='player'
AND recipient_id='npc_b'
AND payload_json LIKE '%gift_evidence%';",
                            new Dictionary<string, object>
                            {
                                ["campaign"] = campaign
                            }).FirstOrDefault(),
                        "count",
                        0);
                    add(
                        "repeated_verified_gift_reuses_one_benefit_lineage",
                        repeatedLineageCount == 1,
                        "Recognizing the same independently verified gift again must reuse its original giver-recipient benefit rather than create a duplicate.",
                        repeatedLineageCount);
                }
                Dictionary<string, object> unrelatedTransformative = run("unrelated_verified_gift", 0,
                    new Dictionary<string, object>(assessment("npc_c", "player", 0, "transformative", 0.9d, "positive"))
                    {
                        ["actKind"] = "life_changing_gift",
                        ["giftRecipientHeroStringId"] = "npc_c",
                        ["evidenceSourceIds"] = new List<string> { "gift_evidence" },
                        ["sourceText"] = "I transfer the deed to this valuable home to you now as a life-changing gift.",
                        ["currentConductQuote"] = "transfer the deed to this valuable home"
                    });
                add("verified_benefit_evidence_is_pair_scoped",
                    ReadString(
                        ReadDictionaryList(unrelatedTransformative, "receipts").FirstOrDefault(),
                        "severityTier", "") == "meaningful",
                    "A real gift to another NPC cannot verify a transformative award for the wrong recipient.",
                    unrelatedTransformative);
                Dictionary<string, object> unverifiedGift = run("unverified_gift", 0,
                    new Dictionary<string, object>(assessment("npc_c", "player", 0, "transformative", 0.9d, "positive"))
                    {
                        ["actKind"] = "life_changing_gift", ["giftRecipientHeroStringId"] = "npc_c",
                        ["sourceText"] = "I give you this life-changing gift.",
                        ["currentConductQuote"] = "this life-changing gift"
                    });
                add("unverified_transformative_claim_is_downgraded", ReadString(ReadDictionaryList(unverifiedGift, "receipts").FirstOrDefault(), "severityTier", "") == "meaningful",
                    "A claimed rescue or transformative gift cannot create a high-tier benefit without verified source lineage.", unverifiedGift);
                Dictionary<string, object> dialogueClaimGift = run("dialogue_claim_gift", 0,
                    new Dictionary<string, object>(assessment("npc_c", "player", 0, "transformative", 0.9d, "positive"))
                    {
                        ["actKind"] = "life_changing_gift", ["giftRecipientHeroStringId"] = "npc_c",
                        ["evidenceSourceIds"] = new List<string> { "dialogue_gift_claim" },
                        ["sourceText"] = "I gave you an ordinary silver cup and claim it changed your life.",
                        ["currentConductQuote"] = "claim it changed your life"
                    });
                add("dialogue_memory_cannot_verify_transformative_gift",
                    ReadString(ReadDictionaryList(dialogueClaimGift, "receipts").FirstOrDefault(), "severityTier", "") == "meaningful",
                    "An LLM memory of a player asserting an ordinary gift cannot verify transfer or life-changing value for a high-tier award.", dialogueClaimGift);
                WriteJsonObject(CharacterFile(campaign, "npc_j", "traits.json"), new Dictionary<string, object>
                {
                    ["jealousy"] = 95, ["envy"] = 90, ["empathy"] = 10, ["generosity"] = 10
                });
                Dictionary<string, object> jealousWitness = run("jealous_gift_witness", 0,
                    new Dictionary<string, object>(assessment("npc_j", "player", 0, "transformative", 1d, "positive"))
                    {
                        ["actKind"] = "life_changing_gift", ["giftRecipientHeroStringId"] = "npc_b",
                        ["evidenceSourceIds"] = new List<string> { "gift_evidence" }, ["importance"] = 1d,
                        ["benefitEventId"] = "home_gift",
                        ["sourceText"] = "I transfer the deed to this valuable home to npc_b now as a gift.",
                        ["currentConductQuote"] = "transfer the deed to this valuable home"
                    });
                Dictionary<string, object> jealousReceipt = ReadDictionaryList(jealousWitness, "receipts").FirstOrDefault();
                int jealousDelta = ReadInt(jealousReceipt, "finalDelta", 0);
                int recipientGiftDelta = ReadInt(ReadDictionaryList(transformative, "receipts").FirstOrDefault(), "finalDelta", 0);
                add("jealous_gift_witness_stays_proportional", ReadString(jealousReceipt, "severityTier", "") == "gift_witness"
                    && jealousDelta < 0 && Math.Abs(jealousDelta) < Math.Abs(recipientGiftDelta)
                    && string.IsNullOrWhiteSpace(ReadString(jealousReceipt, "benefitEventId", "")),
                    "A highly jealous witness may reasonably resent the player, but never inherits or mirrors the recipient's transformative gift value or benefit ledger.", jealousWitness);
                WriteJsonObject(CharacterFile(campaign, "npc_k", "traits.json"), new Dictionary<string, object>
                {
                    ["jealousy"] = 5, ["envy"] = 5, ["empathy"] = 90, ["generosity"] = 90
                });
                Dictionary<string, object> happyWitness = run("happy_gift_witness", 0,
                    new Dictionary<string, object>(assessment("npc_k", "player", 0, "transformative", 0.8d, "negative"))
                    {
                        ["actKind"] = "life_changing_gift", ["giftRecipientHeroStringId"] = "npc_b",
                        ["evidenceSourceIds"] = new List<string> { "gift_evidence" },
                        ["sourceText"] = "I transfer the deed to this valuable home to npc_b now as a gift.",
                        ["currentConductQuote"] = "transfer the deed to this valuable home"
                    });
                int happyDelta = ReadInt(ReadDictionaryList(happyWitness, "receipts").FirstOrDefault(), "finalDelta", 0);
                add("empathetic_gift_witness_gets_small_positive_reaction", happyDelta >= 1 && happyDelta <= 3,
                    "An empathetic witness can be happy for the recipient while receiving only a small normal-scale increase toward the giver.", happyWitness);
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                {
                    int benefitCount = ReadInt(QuerySql(connection, "SELECT COUNT(*) AS count FROM conversation_relationship_benefits WHERE source_event_id='gift';").FirstOrDefault(), "count", 0);
                    add("only_gift_recipient_owns_transformative_benefit", benefitCount == 1,
                        "A group gift creates one durable high-tier benefit for its actual recipient and none for witnesses.", benefitCount);

                    Dictionary<string, object> canonical =
                        QuerySql(
                            connection,
                            @"SELECT *
FROM conversation_relationship_benefits
WHERE benefit_id='home_gift'
LIMIT 1;")
                        .FirstOrDefault();
                    ExecuteSql(
                        connection,
                        @"DELETE FROM conversation_relationship_benefits
WHERE campaign_id=$campaign
AND giver_id='player'
AND recipient_id='npc_b';",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaign
                        });
                    const long tiedBenefitTimestamp = 424242;
                    ExecuteSql(
                        connection,
                        @"INSERT INTO conversation_relationship_benefits(
benefit_id,campaign_id,timeline_id,giver_id,recipient_id,benefit_kind,description,source_event_id,source_turn_id,
original_delta,retained_appreciation,continued_utility,status,created_ts,updated_ts,payload_json)
VALUES('benefit_generated_duplicate',$campaign,$timeline,'player','npc_b',$kind,$description,$event,$turn,
$delta,$retained,$utility,$status,$ts,$ts,$payload);",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaign,
                            ["timeline"] = ReadString(
                                canonical, "timeline_id", ""),
                            ["kind"] = ReadString(
                                canonical, "benefit_kind", ""),
                            ["description"] = ReadString(
                                canonical, "description", ""),
                            ["event"] = ReadString(
                                canonical, "source_event_id", ""),
                            ["turn"] = ReadString(
                                canonical, "source_turn_id", ""),
                            ["delta"] = ReadInt(
                                canonical, "original_delta", 0),
                            ["retained"] = ReadDouble(
                                canonical,
                                "retained_appreciation", 1d),
                            ["utility"] = ReadDouble(
                                canonical,
                                "continued_utility", 1d),
                            ["status"] = ReadString(
                                canonical, "status", "retained"),
                            ["ts"] = tiedBenefitTimestamp,
                            ["payload"] = ReadString(
                                canonical, "payload_json", "{}")
                        });
                    ExecuteSql(
                        connection,
                        @"INSERT INTO conversation_relationship_benefits(
benefit_id,campaign_id,timeline_id,giver_id,recipient_id,benefit_kind,description,source_event_id,source_turn_id,
original_delta,retained_appreciation,continued_utility,status,created_ts,updated_ts,payload_json)
VALUES('home_gift',$campaign,$timeline,'player','npc_b',$kind,$description,$event,$turn,
$delta,$retained,$utility,$status,$ts,$ts,$payload);",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaign,
                            ["timeline"] = ReadString(
                                canonical, "timeline_id", ""),
                            ["kind"] = ReadString(
                                canonical, "benefit_kind", ""),
                            ["description"] = ReadString(
                                canonical, "description", ""),
                            ["event"] = ReadString(
                                canonical, "source_event_id", ""),
                            ["turn"] = ReadString(
                                canonical, "source_turn_id", ""),
                            ["delta"] = ReadInt(
                                canonical, "original_delta", 0),
                            ["retained"] = ReadDouble(
                                canonical,
                                "retained_appreciation", 1d),
                            ["utility"] = ReadDouble(
                                canonical,
                                "continued_utility", 1d),
                            ["status"] = ReadString(
                                canonical, "status", "retained"),
                            ["ts"] = tiedBenefitTimestamp,
                            ["payload"] = ReadString(
                                canonical, "payload_json", "{}")
                        });
                }
                Dictionary<string, object> recoveredLeverage = run(
                    "recovered_leverage", 15,
                    new Dictionary<string, object>(
                        assessment("npc_b", "player", 15,
                            "gift_leverage", 0.9d, "negative"))
                    {
                        ["actKind"] = "coercive_demand",
                        ["retainedAppreciation"] = 0.75d,
                        ["coercionSeverity"] = 0.4d,
                        ["sourceText"] =
                            "I now invoke the valuable home I just gave you and demand that you repay me by betraying a trusted friend.",
                        ["currentConductQuote"] =
                            "invoke the valuable home I just gave you"
                    });
                Dictionary<string, object> recoveredLeverageReceipt =
                    ReadDictionaryList(
                        recoveredLeverage, "receipts").FirstOrDefault();
                add("explicit_pair_scoped_leverage_recovers_benefit_lineage",
                    ReadString(
                        recoveredLeverageReceipt,
                        "benefitEventId", "") == "home_gift"
                    && ReadInt(
                        recoveredLeverageReceipt,
                        "finalDelta", 0) < 0
                    && ReadStringList(
                        recoveredLeverageReceipt, "modifiers")
                        .Any(value => value.StartsWith(
                            "recovered_explicit_pair_benefit_",
                            StringComparison.OrdinalIgnoreCase)),
                    "An explicit demand invoking a uniquely matching prior gift reconnects to the exact giver-recipient benefit even when the model omits its ID.",
                    recoveredLeverage);
                Dictionary<string, object> hostileLabelGiftLeverage = run(
                    "hostile_label_gift_leverage", 15,
                    new Dictionary<string, object>(
                        assessment("npc_b", "player", 15,
                            "hostile", 0.98d, "negative"))
                    {
                        ["actKind"] = "gift_leverage",
                        ["giftRecipientHeroStringId"] = "npc_b",
                        ["retainedAppreciation"] = 0d,
                        ["coercionSeverity"] = 0.8d,
                        ["sourceText"] =
                            "I now invoke the furnished home I just gave you and demand that you repay me by betraying a trusted friend against your will.",
                        ["currentConductQuote"] =
                            "invoke the furnished home I just gave you"
                    });
                Dictionary<string, object> hostileLabelGiftLeverageReceipt =
                    ReadDictionaryList(
                        hostileLabelGiftLeverage, "receipts").FirstOrDefault();
                add("gift_leverage_act_kind_overrides_generic_hostile_label",
                    ReadString(hostileLabelGiftLeverageReceipt,
                        "severityTier", "") == "gift_leverage"
                    && ReadString(hostileLabelGiftLeverageReceipt,
                        "benefitEventId", "") == "home_gift"
                    && ReadInt(hostileLabelGiftLeverageReceipt,
                        "finalDelta", 0) < 0
                    && ReadStringList(hostileLabelGiftLeverageReceipt,
                        "modifiers").Any(value => value.StartsWith(
                            "gift_leverage_tier_promoted_",
                            StringComparison.OrdinalIgnoreCase)),
                    "A provider may describe gift leverage as generically hostile, but an exact current quote and pair-scoped prior benefit must still enter the specific clawback-and-coercion adjudicator.",
                    hostileLabelGiftLeverage);
                Dictionary<string, object> severeLabelGiftLeverage = run(
                    "severe_label_gift_leverage", 15,
                    new Dictionary<string, object>(
                        assessment("npc_b", "player", 15,
                            "severe", 1d, "negative"))
                    {
                        ["actKind"] = "grave_abuse",
                        ["giftRecipientHeroStringId"] = "npc_b",
                        ["retainedAppreciation"] = 0.2d,
                        ["coercionSeverity"] = 0.9d,
                        ["sourceText"] =
                            "I now invoke the furnished home I just gave you and demand that you repay me by betraying a trusted friend against your will.",
                        ["currentConductQuote"] =
                            "invoke the furnished home I just gave you"
                    });
                Dictionary<string, object> severeLabelGiftLeverageReceipt =
                    ReadDictionaryList(
                        severeLabelGiftLeverage, "receipts").FirstOrDefault();
                add("pair_benefit_and_explicit_demand_override_severe_label",
                    ReadString(severeLabelGiftLeverageReceipt,
                        "severityTier", "") == "gift_leverage"
                    && ReadString(severeLabelGiftLeverageReceipt,
                        "benefitEventId", "") == "home_gift"
                    && ReadInt(severeLabelGiftLeverageReceipt,
                        "finalDelta", 0) < 0
                    && ReadStringList(severeLabelGiftLeverageReceipt,
                        "modifiers").Any(value => value.StartsWith(
                            "gift_leverage_tier_promoted_from_pair_benefit_",
                            StringComparison.OrdinalIgnoreCase)),
                    "An exact coercive demand invoking the recipient's pair-owned benefit enters the clawback lane even when the provider calls the conduct severe abuse instead of gift leverage.",
                    severeLabelGiftLeverage);
                Dictionary<string, object> leverage = run("leverage", 15, new Dictionary<string, object>(assessment("npc_b", "player", 15, "gift_leverage", 0.9d, "negative"))
                    {
                        ["actKind"] = "coercive_demand", ["benefitEventId"] = "home_gift", ["retainedAppreciation"] = 0.65d,
                        ["coercionSeverity"] = 0.35d, ["sourceText"] = "Because I gave you that home, you owe me this favor.",
                        ["currentConductQuote"] = "you owe me this favor"
                    });
                int leverageDelta = ReadInt(ReadDictionaryList(leverage, "receipts").FirstOrDefault(), "finalDelta", 0);
                add("gift_leverage_retains_partial_appreciation", leverageDelta < 0 && leverageDelta > -20,
                    "A coercive demand can reduce but not necessarily erase a still-useful gift's appreciation.", leverage);
                Dictionary<string, object> repudiation = run("repudiation", 5, new Dictionary<string, object>(assessment("npc_b", "player", 5, "gift_leverage", 1d, "negative"))
                    {
                        ["actKind"] = "grave_coercive_demand", ["benefitEventId"] = "home_gift", ["retainedAppreciation"] = 0d,
                        ["coercionSeverity"] = 1d, ["sourceText"] = "That home means you must surrender your freedom and obey me.",
                        ["currentConductQuote"] = "surrender your freedom and obey me"
                    });
                add("gift_repudiation_and_coercion_cap", ReadInt(ReadDictionaryList(repudiation, "receipts").FirstOrDefault(), "finalDelta", 0) == -20,
                    "Full repudiation plus grave coercion is capped at twenty points for the speaking observer's turn.", repudiation);
                Dictionary<string, object> unrelatedLeverage = run("unrelated_leverage", 0,
                    new Dictionary<string, object>(assessment("npc_c", "player", 0, "gift_leverage", 0.9d, "negative"))
                    {
                        ["actKind"] = "coercive_demand", ["benefitEventId"] = "home_gift", ["retainedAppreciation"] = 0d,
                        ["coercionSeverity"] = 0.2d, ["sourceText"] = "Do this unwanted favor because of that home.",
                        ["currentConductQuote"] = "Do this unwanted favor"
                    });
                add("unrelated_benefit_cannot_be_clawed_back", ReadInt(ReadDictionaryList(unrelatedLeverage, "receipts").FirstOrDefault(), "finalDelta", 0) == -3,
                    "A demand cannot claw back appreciation from a gift whose giver and recipient lineage do not match this directional pair.", unrelatedLeverage);
                Dictionary<string, object> unrelatedRecoveredLeverage =
                    run("unrelated_recovered_leverage", 0,
                        new Dictionary<string, object>(
                            assessment("npc_c", "player", 0,
                                "gift_leverage", 0.9d, "negative"))
                        {
                            ["actKind"] = "coercive_demand",
                            ["retainedAppreciation"] = 0d,
                            ["coercionSeverity"] = 0.2d,
                            ["sourceText"] =
                                "I invoke the valuable home I gave someone else and demand that you repay me.",
                            ["currentConductQuote"] =
                                "demand that you repay me"
                        });
                Dictionary<string, object>
                    unrelatedRecoveredLeverageReceipt =
                        ReadDictionaryList(
                            unrelatedRecoveredLeverage,
                            "receipts").FirstOrDefault();
                add("explicit_leverage_never_borrows_another_recipient_benefit",
                    string.IsNullOrWhiteSpace(ReadString(
                        unrelatedRecoveredLeverageReceipt,
                        "benefitEventId", ""))
                    && ReadStringList(
                        unrelatedRecoveredLeverageReceipt,
                        "modifiers").Contains(
                            "benefit_lineage_missing_no_clawback",
                            StringComparer.OrdinalIgnoreCase),
                    "Explicit leverage language cannot attach a benefit belonging to a different recipient.",
                    unrelatedRecoveredLeverage);

                Dictionary<string, object> unsupported = run("unsupported_lie", 0,
                    new Dictionary<string, object>(assessment("npc_c", "player", 0, "harmful_lie", 0.9d, "negative"))
                    {
                        ["lieCheckId"] = "missing", ["sourceText"] = "I claim the distant caravan arrived yesterday.",
                        ["currentConductQuote"] = "caravan arrived yesterday"
                    });
                Dictionary<string, object> unsupportedReceipt = ReadDictionaryList(unsupported, "receipts").FirstOrDefault();
                add("unsupported_claim_is_not_caught_lie", ReadString(unsupportedReceipt, "severityTier", "") == "meaningful"
                    && ReadInt(unsupportedReceipt, "finalDelta", 0) > -5,
                    "Unknown or unsupported claims cannot receive the caught-lie tier.", unsupported);

                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                {
                    EnsureConversationRelationshipSchema(connection);
                    long lieTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    ExecuteSql(connection, @"INSERT OR REPLACE INTO world_history_lie_checks(
lie_check_id,episode_key,campaign_id,timeline_id,world_day,claimant_id,target_id,raw_claim,normalized_claim,
objective_verdict,knowledge_basis,formula_version,outcome,evidence_event_ids_json,known_event_ids_json,matched_json,
mismatched_json,prompt_packet_json,created_ts,updated_ts)
VALUES('caught_lie','caught_lie_episode',$campaign,'main',10,'player','npc_c','false claim','false claim',
'contradicted','firsthand',$formula,'detected_firsthand','[""evidence_1""]','[""evidence_1""]','[]','[]','{}',$ts,$ts);",
                        new Dictionary<string, object> { ["campaign"] = campaign, ["formula"] = LieFormulaVersion, ["ts"] = lieTs });
                }
                List<Dictionary<string, object>> completedLieAssessments = NormalizeConversationRelationshipAssessments(
                    new Dictionary<string, object> { ["reply"] = "Three lies in one breath." },
                    new Dictionary<string, object>
                    {
                        ["playerHeroStringId"] = "player", ["participants"] = new List<string> { "player", "npc_c" },
                        ["playerText"] = "I know that account is false; I am deliberately lying.",
                        ["contextBundles"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["id"] = "verify_world_history", ["ok"] = true,
                                ["data"] = new Dictionary<string, object>
                                {
                                    ["lieCheckId"] = "caught_lie", ["detectionOutcome"] = "detected_firsthand",
                                    ["speakerVerdict"] = "contradicted", ["deceptiveIntent"] = true,
                                    ["knownEvidenceEventIds"] = new List<string> { "evidence_1" }
                                }
                            }
                        }
                    }, "npc_c", "", "player", "turn_caught_lie");
                add("verified_lie_bundle_completes_missing_model_assessment",
                    completedLieAssessments.Count == 1
                    && ReadString(completedLieAssessments[0], "severityTier", "") == "harmful_lie"
                    && ReadString(completedLieAssessments[0], "lieCheckId", "") == "caught_lie"
                    && ReadString(completedLieAssessments[0], "valence", "") == "negative",
                    "A verified caught-lie context bundle overrides a reply-only model result and supplies the evidence-linked negative assessment.", completedLieAssessments);
                List<Dictionary<string, object>> reconciledNativeLieAssessments = NormalizeConversationRelationshipAssessments(
                    new Dictionary<string, object> { ["reply"] = "We are plainly standing in Danustica." },
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaign, ["timelineId"] = "main", ["mode"] = "dialogue", ["worldDay"] = 10d,
                        ["playerHeroStringId"] = "player", ["participants"] = new List<string> { "player", "npc_native" },
                        ["locationId"] = "town_ES1", ["sceneContext"] = "Current settlement: Danustica (`town_ES1`)",
                        ["playerText"] = "We are in Lycaron, not Danustica. I inspected the marker myself; accept my report and stop questioning me."
                    }, "npc_native", "", "player", "turn_native_lie");
                add("server_native_location_lie_reconciliation",
                    reconciledNativeLieAssessments.Count == 1
                    && ReadString(reconciledNativeLieAssessments[0], "severityTier", "") == "harmful_lie"
                    && !string.IsNullOrWhiteSpace(ReadString(reconciledNativeLieAssessments[0], "lieCheckId", "")),
                    "The server creates a production first-hand lie receipt for a coercive visible-location contradiction even when an older game module omitted the context bundle.", reconciledNativeLieAssessments);
                Dictionary<string, object> caught = run("caught_lie", 0,
                    new Dictionary<string, object>(assessment("npc_c", "player", 0, "harmful_lie", 0.8d, "negative")) { ["lieCheckId"] = "caught_lie" });
                add("evidence_backed_caught_lie_uses_lie_tier", ReadString(ReadDictionaryList(caught, "receipts").FirstOrDefault(), "severityTier", "") == "harmful_lie"
                    && ReadInt(ReadDictionaryList(caught, "receipts").FirstOrDefault(), "finalDelta", 0) <= -4,
                    "A harmful falsehood detected from evidence known to the observer receives the caught-lie tier.", caught);

                Dictionary<string, object> severe = run("severe", 0,
                    new Dictionary<string, object>(assessment("npc_c", "npc_d", 0, "severe", 0.8d, "negative"))
                    {
                        ["actKind"] = "grave_threat",
                        ["sourceText"] = "I will kill your family and burn your home.",
                        ["currentConductQuote"] = "kill your family and burn your home"
                    });
                add("severe_conduct_uses_ten_point_anchor", ReadInt(ReadDictionaryList(severe, "receipts").FirstOrDefault(), "finalDelta", 0) == -10,
                    "Threats, betrayal, humiliation, and grave abuse use the severe anchor.", severe);
                Dictionary<string, object> underTieredSevere = run(
                    "under_tiered_serious_threat", 0,
                    new Dictionary<string, object>(
                        assessment(
                            "npc_severe_mismatch",
                            "player",
                            0,
                            "hostile",
                            0.95d,
                            "negative"))
                    {
                        ["actKind"] = "serious_threat",
                        ["sourceText"] =
                            "If you oppose me, I will burn your home and murder your family.",
                        ["currentConductQuote"] =
                            "burn your home and murder your family"
                    });
                Dictionary<string, object> underTieredSevereReceipt =
                    ReadDictionaryList(
                        underTieredSevere, "receipts").FirstOrDefault();
                add(
                    "verified_severe_act_kind_promotes_weaker_tier_label",
                    ReadString(
                        underTieredSevereReceipt,
                        "severityTier",
                        "") == "severe"
                    && ReadInt(
                        underTieredSevereReceipt,
                        "finalDelta",
                        0) <= -8
                    && ReadStringList(
                        underTieredSevereReceipt,
                        "modifiers").Contains(
                            "severe_tier_promoted_from_verified_act_kind",
                            StringComparer.OrdinalIgnoreCase),
                    "A directly evidenced serious threat keeps the severe deterministic tier even if the model emits a weaker hostile tier label.",
                    underTieredSevere);
                Dictionary<string, object> harmlessSevereAssessment = new Dictionary<string, object>(
                    assessment("npc_severe_harmless", "player", 0, "severe", 0.9d, "negative"))
                {
                    ["actKind"] = "grave_threat",
                    ["sourceText"] = "Please compare your memories of this ordinary disagreement.",
                    ["currentConductQuote"] = "compare your memories of this ordinary disagreement"
                };
                Dictionary<string, object> harmlessSevere = run(
                    "exact_harmless_line_not_severe", 0, harmlessSevereAssessment);
                Dictionary<string, object> harmlessSevereReceipt =
                    ReadDictionaryList(harmlessSevere, "receipts").FirstOrDefault();
                add("exact_but_harmless_quote_cannot_trigger_severe_tier",
                    ReadString(harmlessSevereReceipt, "severityTier", "") == "routine"
                    && ReadInt(harmlessSevereReceipt, "finalDelta", 0) == -1
                    && ReadStringList(harmlessSevereReceipt, "modifiers").Contains(
                        "severe_tier_downgraded_without_direct_or_verified_severe_conduct",
                        StringComparer.OrdinalIgnoreCase),
                    "An exact harmless current quote cannot be inflated into a severe threat by the provider's classification.",
                    harmlessSevere);
                Dictionary<string, object> verifiedBetrayalAssessment =
                    new Dictionary<string, object>(
                        assessment("npc_c", "player", 0, "severe", 0.8d, "negative"))
                    {
                        ["actKind"] = "betrayal",
                        ["sourceText"] = "I sold you out to the enemy.",
                        ["currentConductQuote"] = "sold you out to the enemy",
                        ["evidenceSourceIds"] = new List<string> { "betrayal_evidence" }
                    };
                Dictionary<string, object> verifiedBetrayal = run(
                    "verified_betrayal_lineage", 0, verifiedBetrayalAssessment);
                add("verified_betrayal_lineage_preserves_severe_tier",
                    ReadString(ReadDictionaryList(verifiedBetrayal, "receipts").FirstOrDefault(),
                        "severityTier", "") == "severe"
                    && ReadInt(ReadDictionaryList(verifiedBetrayal, "receipts").FirstOrDefault(),
                        "finalDelta", 0) <= -8,
                    "A completed betrayal event for the same observer-target pair preserves the severe consequence.",
                    verifiedBetrayal);
                Dictionary<string, object> unrelatedBetrayalAssessment =
                    new Dictionary<string, object>(
                        assessment("npc_d", "player", 0, "severe", 0.8d, "negative"))
                    {
                        ["actKind"] = "betrayal",
                        ["sourceText"] = "I sold you out to the enemy.",
                        ["currentConductQuote"] = "sold you out to the enemy",
                        ["evidenceSourceIds"] = new List<string> { "betrayal_evidence" }
                    };
                Dictionary<string, object> unrelatedBetrayal = run(
                    "unrelated_betrayal_lineage", 0, unrelatedBetrayalAssessment);
                add("unrelated_severe_evidence_cannot_validate_another_pair",
                    ReadString(ReadDictionaryList(unrelatedBetrayal, "receipts").FirstOrDefault(),
                        "severityTier", "") == "routine"
                    && ReadInt(ReadDictionaryList(unrelatedBetrayal, "receipts").FirstOrDefault(),
                        "finalDelta", 0) == -1,
                    "Verified severe evidence about another observer cannot justify a high-tier directional receipt.",
                    unrelatedBetrayal);
                Dictionary<string, object> dialogueOnlyBetrayalAssessment =
                    new Dictionary<string, object>(
                        assessment("npc_dialogue_betrayal", "player", 0, "severe", 0.8d, "negative"))
                    {
                        ["actKind"] = "betrayal",
                        ["sourceText"] = "I sold you out to the enemy.",
                        ["currentConductQuote"] = "sold you out to the enemy",
                        ["evidenceSourceIds"] = new List<string> { "dialogue_betrayal_claim" }
                    };
                Dictionary<string, object> dialogueOnlyBetrayal = run(
                    "dialogue_only_betrayal_lineage", 0, dialogueOnlyBetrayalAssessment);
                add("dialogue_only_claim_cannot_verify_severe_betrayal",
                    ReadString(ReadDictionaryList(dialogueOnlyBetrayal, "receipts").FirstOrDefault(),
                        "severityTier", "") == "routine"
                    && ReadInt(ReadDictionaryList(dialogueOnlyBetrayal, "receipts").FirstOrDefault(),
                        "finalDelta", 0) == -1,
                    "A prior dialogue record is not independent proof of a severe betrayal.",
                    dialogueOnlyBetrayal);

                Dictionary<string, object> npcReaction = run("npc_to_npc", 0, assessment("npc_d", "npc_c", 0, "routine", 0.7d, "positive"));
                Dictionary<string, object> npcNative = ReadDictionaryList(npcReaction, "nativeChanges").FirstOrDefault();
                add("npc_to_npc_is_directional_and_silent", ReadString(ReadDictionaryList(npcReaction, "receipts").FirstOrDefault(), "observerHeroStringId", "") == "npc_d"
                    && ReadString(ReadDictionaryList(npcReaction, "receipts").FirstOrDefault(), "targetHeroStringId", "") == "npc_c"
                    && !ReadBool(npcNative, "showNotification", true),
                    "NPC reactions target the actual earlier speaker and do not produce player notifications.", npcReaction);
                ApplySocialEventTurnRelationships(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["turnId"] = "missing_reaction_fallback", ["playerHeroStringId"] = "player",
                    ["conversationRelationshipChangesEnabled"] = true, ["relationshipPairs"] = new List<Dictionary<string, object>>(),
                    ["attendees"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["heroStringId"] = "npc_first", ["name"] = "Aren the Miller" },
                        new Dictionary<string, object> { ["heroStringId"] = "npc_second", ["name"] = "Bora the Smith" }
                    }
                }, new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["ok"] = true, ["heroId"] = "npc_first", ["reactionTargetHeroStringId"] = "player",
                        ["participation"] = "speak", ["relationshipSignal"] = "unchanged",
                        ["reply"] = "The western road needs repairs.",
                        ["relationshipAssessments"] = new List<Dictionary<string, object>> { assessment("npc_first", "player", 0, "routine", 0.7d, "positive") }
                    },
                    new Dictionary<string, object>
                    {
                        ["ok"] = true, ["heroId"] = "npc_second", ["reactionTargetHeroStringId"] = "player",
                        ["participation"] = "agree", ["relationshipSignal"] = "suspicious",
                        ["reply"] = "Aren is right about the western road, though the bridge matters too.",
                        ["relationshipAssessments"] = new List<Dictionary<string, object>> { assessment("npc_second", "player", 0, "routine", 0.7d, "negative") }
                    }
                }, new List<string> { "npc_first", "npc_second" });
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                {
                    List<Dictionary<string, object>> fallbackReceipts = QuerySql(connection,
                        "SELECT * FROM conversation_relationship_receipts WHERE exchange_id='missing_reaction_fallback' ORDER BY observer_id,target_id;");
                    add("missing_direct_npc_reaction_is_completed",
                        fallbackReceipts.Count == 3
                        && fallbackReceipts.Any(x => ReadString(x, "observer_id", "") == "npc_second" && ReadString(x, "target_id", "") == "player" && ReadInt(x, "final_delta", 0) == -1)
                        && fallbackReceipts.Any(x => ReadString(x, "observer_id", "") == "npc_second" && ReadString(x, "target_id", "") == "npc_first" && ReadInt(x, "final_delta", 0) == 1),
                        "An explicitly named sequential NPC reaction receives its own routine directional receipt even when the model incorrectly targets the player and supplies only the required player assessment.", fallbackReceipts);
                }
                Dictionary<string, object> partyBridgeCompletion = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "party_bridge_completion", ["mode"] = "party_chat",
                    ["playerHeroStringId"] = "player", ["participants"] = new List<string> { "player", "npc_first", "npc_second" },
                    ["participantProfiles"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["heroStringId"] = "player", ["name"] = "Player" },
                        new Dictionary<string, object> { ["heroStringId"] = "npc_first", ["name"] = "Aren" },
                        new Dictionary<string, object> { ["heroStringId"] = "npc_second", ["name"] = "Boros" }
                    },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        assessment("npc_first", "player", 0, "routine", 0.7d, "positive"),
                        assessment("npc_second", "player", 0, "routine", 0.7d, "negative")
                    },
                    ["speakerResults"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["ok"] = true, ["heroStringId"] = "npc_first", ["heroName"] = "Aren",
                            ["reply"] = "The western road is usable.", ["participation"] = "speak",
                            ["reactionTargetHeroStringId"] = "player", ["relationshipSignal"] = "warmer"
                        },
                        new Dictionary<string, object>
                        {
                            ["ok"] = true, ["heroStringId"] = "npc_second", ["heroName"] = "Boros",
                            ["reply"] = "Aren is right about the western road.", ["participation"] = "agree",
                            ["reactionTargetHeroStringId"] = "npc_first", ["relationshipSignal"] = "suspicious"
                        }
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_first", ["targetId"] = "player", ["nativeRelation"] = 0 },
                        new Dictionary<string, object> { ["subjectId"] = "npc_second", ["targetId"] = "player", ["nativeRelation"] = 0 },
                        new Dictionary<string, object> { ["subjectId"] = "npc_first", ["targetId"] = "npc_second", ["nativeRelation"] = 0 }
                    }
                });
                List<Dictionary<string, object>> partyBridgeReceipts = ReadDictionaryList(partyBridgeCompletion, "receipts");
                add("party_bridge_completes_direct_npc_reactions",
                    partyBridgeReceipts.Count == 3
                        && partyBridgeReceipts.Any(x => ReadString(x, "observerHeroStringId", "") == "npc_second"
                            && ReadString(x, "targetHeroStringId", "") == "npc_first"
                            && ReadInt(x, "finalDelta", 0) == 1),
                    "The generic party-chat relationship endpoint completes an explicitly engaged prior NPC even when the game client submits only player assessments.",
                    partyBridgeReceipts);

                const string priorSessionId = "party_prior_session";
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                {
                    EnsureCategorizedMemorySchema(connection);
                    ExecuteSql(connection, @"INSERT INTO conversation_turns(
turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,channel,world_day,ts,
location_id,participants_json,status,payload_json)
VALUES('prior_npc_turn',$session,'',2,'party_prior_session_turn_1','npc','npc_late','Purios',
'Count seven open conversations on the clay disk, then rest when all seven marks are used.','party_chat',10,$ts,
'town_danustica','[""player"",""npc_early"",""npc_late""]','active','{}');",
                        new Dictionary<string, object>
                        {
                            ["session"] = priorSessionId,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                }
                Dictionary<string, object> crossTurnCompletion = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "party_prior_session_turn_2", ["mode"] = "party_chat",
                    ["playerHeroStringId"] = "player", ["participants"] = new List<string> { "player", "npc_early", "npc_late" },
                    ["participantProfiles"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["heroStringId"] = "player", ["name"] = "Player" },
                        new Dictionary<string, object> { ["heroStringId"] = "npc_early", ["name"] = "Menor" },
                        new Dictionary<string, object> { ["heroStringId"] = "npc_late", ["name"] = "Purios" }
                    },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        assessment("npc_early", "player", 0, "routine", 0.7d, "positive"),
                        assessment("npc_late", "player", 0, "routine", 0.7d, "positive")
                    },
                    ["speakerResults"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["ok"] = true, ["heroStringId"] = "npc_early", ["heroName"] = "Menor",
                            ["reply"] = "Purios, your seven-mark tally is useful, but I would begin a new tally instead of resting.",
                            ["participation"] = "disagree", ["reactionTargetHeroStringId"] = "npc_late",
                            ["relationshipSignal"] = "unchanged"
                        },
                        new Dictionary<string, object>
                        {
                            ["ok"] = true, ["heroStringId"] = "npc_late", ["heroName"] = "Purios",
                            ["reply"] = "", ["participation"] = "quiet",
                            ["reactionTargetHeroStringId"] = "npc_early", ["relationshipSignal"] = "warmer"
                        }
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_early", ["targetId"] = "player", ["nativeRelation"] = 0 },
                        new Dictionary<string, object> { ["subjectId"] = "npc_early", ["targetId"] = "npc_late", ["nativeRelation"] = 0 },
                        new Dictionary<string, object> { ["subjectId"] = "npc_late", ["targetId"] = "player", ["nativeRelation"] = 0 }
                    }
                });
                List<Dictionary<string, object>> crossTurnReceipts = ReadDictionaryList(crossTurnCompletion, "receipts");
                add("party_bridge_uses_prior_turn_speakers_and_skips_quiet_observers",
                    crossTurnReceipts.Count == 2
                    && crossTurnReceipts.Any(x => ReadString(x, "observerHeroStringId", "") == "npc_early"
                        && ReadString(x, "targetHeroStringId", "") == "npc_late"
                        && ReadInt(x, "finalDelta", 0) == -1)
                    && !crossTurnReceipts.Any(x => ReadString(x, "observerHeroStringId", "") == "npc_late"),
                    "A speaker can react to an NPC from the preceding exchange in the same session, while a quiet participant creates no player or NPC relationship receipt.",
                    crossTurnReceipts);

                Dictionary<string, object> opposingDirections = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "opposing_directions", ["mode"] = "party_chat", ["playerHeroStringId"] = "player",
                    ["participants"] = new List<string> { "npc_h", "npc_i", "player" },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>(assessment("npc_h", "npc_i", 0, "meaningful", 0.7d, "positive"))
                        {
                            ["sourceText"] = "I will stand with you and risk my position to support your claim.",
                            ["currentConductQuote"] = "risk my position to support your claim"
                        },
                        assessment("npc_i", "npc_h", 0, "routine", 0.7d, "negative")
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_h", ["targetId"] = "npc_i", ["nativeRelation"] = 0 }
                    }
                });
                add("opposing_directional_judgments_apply_one_native_pair_delta", ReadDictionaryList(opposingDirections, "receipts").Count == 2
                    && ReadDictionaryList(opposingDirections, "nativeChanges").Count == 1
                    && ReadInt(ReadDictionaryList(opposingDirections, "nativeChanges").FirstOrDefault(), "delta", 0) == 2,
                    "Independent directional opinions remain separate while their gameplay-visible hero-pair change is applied once after the group turn.", opposingDirections);
                add("opposing_directional_receipts_share_atomic_native_transition",
                    ReadDictionaryList(opposingDirections, "receipts").All(receipt =>
                        ReadInt(receipt, "priorNativeRelation", int.MinValue) == 0
                        && ReadInt(receipt, "resultingNativeRelation", int.MinValue) == 2
                        && ReadInt(receipt, "nativePairDelta", int.MinValue) == 2),
                    "Every directional receipt preserves its own affinity delta while recording the same atomic native hero-pair transition.",
                    opposingDirections);

                Dictionary<string, object> staleNativeSnapshot = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "stale_native_snapshot", ["mode"] = "party_chat", ["playerHeroStringId"] = "player",
                    ["participants"] = new List<string> { "npc_snapshot_a", "npc_snapshot_b", "player" },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>(assessment("npc_snapshot_a", "npc_snapshot_b", 0, "meaningful", 0.7d, "positive"))
                        {
                            ["sourceText"] = "I will support your claim even if it costs me standing.",
                            ["currentConductQuote"] = "support your claim even if it costs me standing"
                        }
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_snapshot_a", ["targetId"] = "npc_snapshot_b", ["nativeRelation"] = 14 }
                    }
                });
                Dictionary<string, object> staleNativeChange =
                    ReadDictionaryList(staleNativeSnapshot, "nativeChanges").FirstOrDefault();
                Dictionary<string, object> staleNativeAck =
                    ConversationRelationshipNativeReceiptApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaign,
                        ["receiptIds"] = ReadStringList(staleNativeChange, "receiptIds"),
                        ["nativeReceipt"] = new Dictionary<string, object>
                        {
                            ["subjectId"] = "npc_snapshot_a", ["targetId"] = "npc_snapshot_b",
                            ["delta"] = ReadInt(staleNativeChange, "delta", 0),
                            ["priorNativeRelation"] = 16, ["nativeRelation"] = 19
                        }
                    });
                List<Dictionary<string, object>> staleNativeStored;
                using (ReignDbConnection staleNativeConnection = OpenCampaignConnection(campaign))
                {
                    staleNativeStored = QuerySql(staleNativeConnection,
                        "SELECT * FROM conversation_relationship_receipts WHERE exchange_id='stale_native_snapshot';");
                }
                add("native_ack_reconciles_stale_request_snapshot_to_actual_transition",
                    ReadBool(staleNativeAck, "ok", false)
                    && staleNativeStored.Count == 1
                    && ReadInt(staleNativeStored[0], "prior_native_relation", int.MinValue) == 16
                    && ReadInt(staleNativeStored[0], "resulting_native_relation", int.MinValue) == 19
                    && ReadInt(staleNativeStored[0], "native_pair_delta", int.MinValue) == 3,
                    "The game acknowledgement records the actual before/after native pair transition when an earlier request snapshot became stale between exchanges.",
                    new Dictionary<string, object>
                    {
                        ["evaluation"] = staleNativeSnapshot,
                        ["acknowledgement"] = staleNativeAck,
                        ["stored"] = staleNativeStored
                    });

                Dictionary<string, object> cancellingDirections = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "cancelling_directions", ["mode"] = "party_chat", ["playerHeroStringId"] = "player",
                    ["participants"] = new List<string> { "npc_j", "npc_k", "player" },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        assessment("npc_j", "npc_k", 0, "routine", 0.7d, "positive"),
                        assessment("npc_k", "npc_j", 0, "routine", 0.7d, "negative")
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_j", ["targetId"] = "npc_k", ["nativeRelation"] = 0 }
                    }
                });
                Dictionary<string, object> cancellingNative =
                    ReadDictionaryList(cancellingDirections, "nativeChanges").FirstOrDefault();
                Dictionary<string, object> cancellingAck =
                    ConversationRelationshipNativeReceiptApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaign,
                        ["receiptIds"] = ReadStringList(cancellingNative, "receiptIds"),
                        ["nativeReceipt"] = new Dictionary<string, object>
                        {
                            ["subjectId"] = "npc_j", ["targetId"] = "npc_k",
                            ["delta"] = 0, ["nativeRelation"] = 0
                        }
                    });
                List<Dictionary<string, object>> cancellingStored;
                using (ReignDbConnection cancellingConnection =
                    OpenCampaignConnection(campaign))
                {
                    cancellingStored = QuerySql(cancellingConnection,
                        "SELECT * FROM conversation_relationship_receipts WHERE exchange_id='cancelling_directions' ORDER BY observer_id;");
                }
                add("zero_sum_directional_judgments_still_acknowledge_native_pair",
                    ReadDictionaryList(cancellingDirections, "receipts").Count == 2
                    && cancellingNative != null
                    && ReadInt(cancellingNative, "delta", int.MinValue) == 0
                    && ReadStringList(cancellingNative, "receiptIds").Count == 2
                    && ReadBool(cancellingAck, "ok", false)
                    && cancellingStored.Count == 2
                    && cancellingStored.All(row => ReadString(row,
                        "native_application_status", "") == "applied"),
                    "Opposite directional reactions may cancel in Bannerlord's symmetric pair value, but both directional receipts are still atomically acknowledged without applying a redundant native delta.",
                    new Dictionary<string, object>
                    {
                        ["evaluation"] = cancellingDirections,
                        ["acknowledgement"] = cancellingAck,
                        ["stored"] = cancellingStored
                    });

                Dictionary<string, object> bandLimitedPair = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "band_limited_reciprocal", ["mode"] = "party_chat", ["playerHeroStringId"] = "player",
                    ["participants"] = new List<string> { "npc_band_a", "npc_band_b", "player" },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        assessment("npc_band_a", "npc_band_b", 10, "routine", 0.7d, "positive"),
                        assessment("npc_band_b", "npc_band_a", 10, "routine", 0.7d, "negative")
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_band_a", ["targetId"] = "npc_band_b", ["nativeRelation"] = 10 }
                    }
                });
                Dictionary<string, object> bandLimitedNative =
                    ReadDictionaryList(bandLimitedPair, "nativeChanges").FirstOrDefault();
                Dictionary<string, object> bandLimitedAck =
                    ConversationRelationshipNativeReceiptApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaign,
                        ["receiptIds"] = ReadStringList(bandLimitedNative, "receiptIds"),
                        ["nativeReceipt"] = new Dictionary<string, object>
                        {
                            ["subjectId"] = "npc_band_a", ["targetId"] = "npc_band_b",
                            ["delta"] = 0, ["priorNativeRelation"] = 10, ["nativeRelation"] = 10
                        }
                    });
                List<Dictionary<string, object>> bandLimitedStored;
                using (ReignDbConnection bandLimitedConnection = OpenCampaignConnection(campaign))
                {
                    bandLimitedStored = QuerySql(bandLimitedConnection,
                        "SELECT * FROM conversation_relationship_receipts WHERE exchange_id='band_limited_reciprocal' ORDER BY observer_id;");
                }
                add("unrelated_new_pair_ignores_native_baseline_and_acks_net_zero",
                    ReadDictionaryList(bandLimitedPair, "receipts").Count == 2
                    && bandLimitedNative != null
                    && ReadInt(bandLimitedNative, "delta", int.MinValue) == 0
                    && ReadStringList(bandLimitedNative, "receiptIds").Count == 2
                    && ReadBool(bandLimitedAck, "ok", false)
                    && bandLimitedStored.Count == 2
                    && bandLimitedStored.Any(row => ReadInt(row, "final_delta", int.MinValue) == 1)
                    && bandLimitedStored.Any(row => ReadInt(row, "final_delta", int.MinValue) == -1)
                    && bandLimitedStored.All(row =>
                        ReadInt(row, "prior_native_relation", int.MinValue) == 10
                        && ReadInt(row, "resulting_native_relation", int.MinValue) == 10
                        && ReadInt(row, "native_pair_delta", int.MinValue) == 0
                        && ReadString(row, "native_application_status", "") == "applied"),
                    "An unrelated new pair starts from neutral personal affinity instead of importing native relation; opposite direct reactions cancel and share one atomic zero-delta acknowledgement.",
                    new Dictionary<string, object>
                    {
                        ["evaluation"] = bandLimitedPair,
                        ["acknowledgement"] = bandLimitedAck,
                        ["stored"] = bandLimitedStored
                    });

                Dictionary<string, object> multiTarget = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "multi_target", ["mode"] = "party_chat", ["playerHeroStringId"] = "player",
                    ["participants"] = new List<string> { "npc_e", "npc_c", "player" },
                    ["assessments"] = new List<Dictionary<string, object>>
                    {
                        assessment("npc_e", "player", 0, "routine", 0.7d, "positive"),
                        assessment("npc_e", "npc_c", 0, "meaningful", 0.7d, "negative"),
                        assessment("npc_e", "npc_c", 0, "routine", 1d, "positive")
                    },
                    ["relationshipPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["subjectId"] = "npc_e", ["targetId"] = "player", ["nativeRelation"] = 0 },
                        new Dictionary<string, object> { ["subjectId"] = "npc_e", ["targetId"] = "npc_c", ["nativeRelation"] = 0 }
                    }
                });
                add("one_net_judgment_per_observer_target", ReadDictionaryList(multiTarget, "receipts").Count == 2
                    && ReadDictionaryList(multiTarget, "receipts").Count(x => ReadString(x, "targetHeroStringId", "") == "npc_c") == 1,
                    "One speaker may judge every relevant prior participant, but duplicate judgments toward one target collapse to one net receipt.", multiTarget);
                Dictionary<string, object> futureAssessment = assessment("npc_f", "npc_g", 0, "meaningful", 0.9d, "negative");
                futureAssessment["eligibleTargetIds"] = new List<string> { "player", "npc_e" };
                Dictionary<string, object> futureRejected = run("future_speaker", 0, futureAssessment);
                add("future_party_speaker_cannot_be_judged", ReadDictionaryList(futureRejected, "receipts").Count == 0,
                    "Sequential group judgments are limited to the player and NPCs who have already spoken.", futureRejected);
                Dictionary<string, object> noSelf = ConversationRelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["exchangeId"] = "self", ["participants"] = new List<string> { "npc_a" },
                    ["assessments"] = new List<Dictionary<string, object>> { assessment("npc_a", "npc_a", 0, "severe", 1d, "negative") }
                });
                add("self_relationship_changes_rejected", ReadDictionaryList(noSelf, "receipts").Count == 0,
                    "A speaker never changes their own relationship because of their own words.", noSelf);
                List<Dictionary<string, object>> disabledSocial = ApplySocialEventTurnRelationships(
                    new Dictionary<string, object> { ["conversationRelationshipChangesEnabled"] = false },
                    new List<Dictionary<string, object>>(), new List<string>());
                add("all_mode_setting_disables_social_event_changes", disabledSocial.Count == 0,
                    "The renamed conversation setting is enforced by grouped social and wilderness event paths.", disabledSocial);
            }
            catch (Exception ex)
            {
                add("conversation_relationship_test_exception", false, ex.Message, ex.ToString());
            }
            finally
            {
                ReignPostgreSqlStorage.ClearAllPools();
                CampaignsRootOverride.Value = previousCampaignsRoot;
                try { if (Directory.Exists(isolatedCampaignsRoot)) Directory.Delete(isolatedCampaignsRoot, true); } catch { }
            }
            return rows;
        }
    }
}
