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
                Dictionary<string, object> lie = string.IsNullOrWhiteSpace(lieCheckId) ? null : ReignDatabase.QuerySql(connection,
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

            if (valence == "negative" && tier == "meaningful"
                && !ConversationDirectHostileSpeechIsSupported(assessment)
                && !ConversationSevereConductIsSupported(connection, assessment, actKind))
            {
                tier = "routine";
                modifiers.Add("perceived_slight_capped_without_stronger_conduct");
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
                    : ReignDatabase.QuerySql(connection,
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
                double retained = Program.ClampDouble(ReadDouble(assessment, "retainedAppreciation", 1d), 0d, 1d);
                int lost = benefit == null ? 0 : RoundAwayFromZero(ReadInt(benefit, "original_delta", 0) * (1d - retained));
                int coercion = RoundAwayFromZero(15d * Program.ClampDouble(ReadDouble(assessment, "coercionSeverity", 0d), 0d, 1d));
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

            string source = Program.NormalizeLookup(
                ReadString(assessment, "sourceText", "")
                + " " + ReadFirstString(assessment,
                    "currentConductQuote", "current_conduct_quote",
                    "evidenceQuote"));
            bool explicitlyInvokesBenefit =
                Program.ContainsAny(source,
                    "gift", "gave you", "gave the recipient",
                    "home", "house", "deed", "estate", "land",
                    "cup", "gold", "aid", "help i gave",
                    "saved your", "rescued your")
                && Program.ContainsAny(source,
                    "invoke", "repay", "owe", "because",
                    "demand", "in return", "return the favor",
                    "leverage", "after i gave", "i just gave");
            List<string> evidenceIds =
                ReadStringList(assessment, "evidenceSourceIds");
            if (!explicitlyInvokesBenefit
                && evidenceIds.Count == 0)
                return null;

            List<Dictionary<string, object>> candidates = ReignDatabase.QuerySql(
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
                        Program.NormalizeLookup(
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
            return ReignDatabase.QuerySql(connection, @"SELECT *
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
            string source = Program.NormalizeLookup(ReadString(assessment, "sourceText", ""));
            string quote = Program.NormalizeLookup(ReadFirstString(assessment,
                "currentConductQuote", "current_conduct_quote", "evidenceQuote"));
            if (string.IsNullOrWhiteSpace(source) || quote.Length < 6) return false;
            if (!source.Contains(quote)) return false;
            return quote.Count(char.IsLetterOrDigit) >= 6;
        }

        private static bool ConversationDirectHostileSpeechIsSupported(
            Dictionary<string, object> assessment)
        {
            if (!ConversationCurrentConductIsSupported(assessment)) return false;
            string quote = Program.NormalizeLookup(ReadFirstString(assessment,
                "currentConductQuote", "current_conduct_quote", "evidenceQuote"));
            string source = Program.NormalizeLookup(ReadString(assessment, "sourceText", ""));
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
            string source = Program.NormalizeLookup(ReadString(assessment, "sourceText", ""));
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
            Dictionary<string, object> evidence = ReignDatabase.QuerySql(connection,
                "SELECT event_type,summary,payload_json,'' AS category,'completed' AS phase,participants_json,'events' AS source_table FROM events WHERE event_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = sourceId }).FirstOrDefault();
            if (evidence == null)
            {
                evidence = ReignDatabase.QuerySql(connection,
                    "SELECT event_type,summary,payload_json,category,phase,'[]' AS participants_json,'world_history_events' AS source_table FROM world_history_events WHERE event_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = sourceId }).FirstOrDefault();
            }
            if (evidence == null) return false;
            string phase = ReadString(evidence, "phase", "completed").ToLowerInvariant();
            if (phase == "planned" || phase == "proposed" || phase == "cancelled"
                || phase == "failed" || phase == "rumored")
                return false;
            string kind = Program.NormalizeLookup(
                ReadString(evidence, "event_type", "") + " "
                + ReadString(evidence, "category", ""));
            string details = Program.NormalizeLookup(
                ReadString(evidence, "summary", "") + " "
                + ReadString(evidence, "payload_json", "{}"));
            if (Program.ContainsAny(kind, "dialogue", "conversation", "reported speech")
                && !Program.ContainsAny(kind, "action receipt", "native receipt", "verified event"))
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
                    ReignDatabase.QuerySql(connection,
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
                return Program.ContainsAny(kind + " " + details,
                    "betrayal", "betrayed", "treachery", "sold out", "defection");
            if ((actKind ?? "").Contains("humiliat"))
                return Program.ContainsAny(kind + " " + details,
                    "humiliation", "humiliated", "public disgrace", "stripped of honor");
            return (actKind ?? "").Contains("grave_abuse")
                && Program.ContainsAny(kind + " " + details,
                    "grave abuse", "torture", "enslavement", "abduction", "atrocity");
        }

        private static string ConversationGiftWitnessValence(string campaignId, string observerId, string requestedValence)
        {
            Dictionary<string, object> traits = Program.ReadJsonObject(Program.CharacterFile(campaignId, observerId, "traits.json"));
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
            Dictionary<string, object> evidence = ReignDatabase.QuerySql(connection,
                "SELECT event_type,summary,payload_json,'' AS category,'completed' AS phase,participants_json,'events' AS source_table FROM events WHERE event_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = sourceId }).FirstOrDefault();
            if (evidence == null)
            {
                evidence = ReignDatabase.QuerySql(connection,
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
                    ReignDatabase.QuerySql(connection,
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
            string kind = Program.NormalizeLookup(ReadString(evidence, "event_type", "") + " " + ReadString(evidence, "category", ""));
            string details = Program.NormalizeLookup(ReadString(evidence, "summary", "") + " " + ReadString(evidence, "payload_json", "{}")
                + " " + ReadString(assessment, "summary", ""));
            bool gift = actKind.Contains("gift");
            if (gift)
            {
                bool verifiedTransfer = Program.ContainsAny(kind, "gift", "transfer item", "item transfer", "transfer gold", "gold transfer", "property transfer", "property_transfer");
                if (!verifiedTransfer) return false;
                bool transformativeScale = Program.ContainsAny(details,
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
                && Program.ContainsAny(kind, "rescue", "saved", "freed", "liberated", "aid")
                && Program.ContainsAny(details, "family", "spouse", "husband", "wife", "child", "daughter", "son", "mother", "father", "life", "important");
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
            foreach (Dictionary<string, object> row in ReignDatabase.QuerySql(
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
            Dictionary<string, object> traits = Program.ReadJsonObject(Program.CharacterFile(campaignId, observerId, "traits.json"));
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

        internal static Dictionary<string, object> SelectDominantConversationAssessment(List<Dictionary<string, object>> rows)
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

        internal static Dictionary<string, object> FindRelationshipPair(List<Dictionary<string, object>> pairs, string observerId, string targetId)
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
                        + DialogueService.PromptHash(receiptId).Substring(0, 24)
                            .ToLowerInvariant())
                    : ReadString(existing, "benefit_id", "");
                adjudicated["benefitEventId"] = benefitId;
                if (existing == null)
                {
                    ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_relationship_benefits(
benefit_id,campaign_id,timeline_id,giver_id,recipient_id,benefit_kind,description,source_event_id,source_turn_id,
original_delta,retained_appreciation,continued_utility,status,created_ts,updated_ts,source_evidence_json,payload_json)
VALUES($id,$campaign,$timeline,$giver,$recipient,$kind,$description,$event,$turn,$delta,1,$utility,'retained',$ts,$ts,$evidence,$payload);",
                        new Dictionary<string, object> { ["id"] = benefitId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["giver"] = targetId, ["recipient"] = observerId, ["kind"] = act, ["description"] = ReadString(assessment, "summary", ""),
                            ["event"] = exchangeId, ["turn"] = sourceTurnId, ["delta"] = delta,
                            ["utility"] = ReadDouble(assessment, "continuedUtility", 1d), ["ts"] = ts,
                            ["evidence"] = Json.Serialize(evidenceIds), ["payload"] = Json.Serialize(assessment) });
                }
                ReignDatabase.ExecuteSql(connection, "UPDATE conversation_relationship_receipts SET benefit_event_id=$benefit WHERE receipt_id=$receipt;",
                    new Dictionary<string, object> { ["benefit"] = benefitId, ["receipt"] = receiptId });
            }
            else if (tier == "gift_leverage" && !string.IsNullOrWhiteSpace(benefitId))
            {
                double retained = Program.ClampDouble(ReadDouble(assessment, "retainedAppreciation", 1d), 0d, 1d);
                ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_relationship_benefits SET retained_appreciation=$retained,
status=$status,updated_ts=$ts,payload_json=$payload WHERE benefit_id=$id;",
                    new Dictionary<string, object> { ["retained"] = retained, ["status"] = retained <= 0.01d ? "repudiated" : retained < 0.999d ? "diminished" : "retained",
                        ["ts"] = ts, ["payload"] = Json.Serialize(assessment), ["id"] = benefitId });
            }
        }

        internal static Dictionary<string, object> ConversationReceiptResponse(Dictionary<string, object> row, bool idempotent)
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

        internal static Dictionary<string, object> ConversationRelationshipRecentApi(Dictionary<string, string> query)
        {
            string campaignId = query != null && query.TryGetValue("campaignId", out string supplied) ? supplied : Program.LatestCampaignId();
            int limit = Math.Max(1, Math.Min(250, query != null && query.TryGetValue("limit", out string raw) && int.TryParse(raw, out int parsed) ? parsed : 80));
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureConversationRelationshipSchema(connection);
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["campaignId"] = campaignId,
                    ["receipts"] = ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts ORDER BY created_ts DESC LIMIT " + limit.ToString(CultureInfo.InvariantCulture) + ";"),
                    ["benefits"] = ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_benefits ORDER BY updated_ts DESC LIMIT " + limit.ToString(CultureInfo.InvariantCulture) + ";")
                };
            }
        }
    }
}
