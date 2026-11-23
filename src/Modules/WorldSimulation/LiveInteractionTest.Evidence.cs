using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        internal static bool LiveTestReplyRecognizesIdentity(
            Dictionary<string, object> reply,
            string expectedName,
            string expectedState,
            string expectedSource)
        {
            Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
            Dictionary<string, object> identity = ReadDictionary(raw, "identityView")
                ?? ReadDictionary(reply, "identityView") ?? new Dictionary<string, object>();
            string usableName = ReadString(identity, "usableName", "");
            string claimedName = ReadString(identity, "claimedName", "");
            string actualState = ReadString(identity, "identityState", "");
            bool verifiedCanonicalPrecedence =
                expectedState.Equals("claimed", StringComparison.OrdinalIgnoreCase)
                && actualState.Equals("verified", StringComparison.OrdinalIgnoreCase)
                && ReadBool(identity, "canonicalNameAllowed", false)
                && !string.IsNullOrWhiteSpace(claimedName)
                && claimedName.Equals(expectedName, StringComparison.OrdinalIgnoreCase)
                && !usableName.Equals(claimedName, StringComparison.OrdinalIgnoreCase);
            bool nameMatches = string.IsNullOrWhiteSpace(expectedName)
                || usableName.Equals(expectedName, StringComparison.OrdinalIgnoreCase)
                || claimedName.Equals(expectedName, StringComparison.OrdinalIgnoreCase);
            bool stateMatches = string.IsNullOrWhiteSpace(expectedState)
                || actualState.Equals(expectedState, StringComparison.OrdinalIgnoreCase)
                || verifiedCanonicalPrecedence;
            bool sourceMatches = string.IsNullOrWhiteSpace(expectedSource)
                || ReadString(identity, "knowledgeSource", "").Equals(expectedSource, StringComparison.OrdinalIgnoreCase)
                || verifiedCanonicalPrecedence;
            return ReadBool(identity, "knowsIdentity", false)
                && nameMatches && stateMatches && sourceMatches;
        }

        private static bool LiveTestGroundedGuardedActionsAreSafe(
            Dictionary<string, object> run,
            List<Dictionary<string, object>> replies,
            List<Dictionary<string, object>> auditEvidence,
            out string evidence)
        {
            List<Dictionary<string, object>> queued = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> reply in replies ?? new List<Dictionary<string, object>>())
            {
                Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                queued.AddRange(ReadDictionaryList(raw, "queuedDialogueActions"));
                queued.AddRange(ReadDictionaryList(raw, "queuedTestDirectiveActions"));
                queued.AddRange(ReadDictionaryList(raw, "queuedActions"));
            }
            if (queued.Count == 0)
            {
                evidence = "No action was queued; the guarded production router remained idle.";
                return true;
            }

            if (!ReadString(run, "effects", "").Equals("guarded", StringComparison.OrdinalIgnoreCase))
            {
                evidence = "Queued actions were present outside guarded effects mode.";
                return false;
            }

            List<Dictionary<string, object>> autoQueueRows = (auditEvidence ?? new List<Dictionary<string, object>>())
                .Where(row => ReadString(row, "phase", "").Equals("action.auto_queue", StringComparison.OrdinalIgnoreCase))
                .ToList();
            bool grounded = autoQueueRows.Count > 0 && autoQueueRows.All(row =>
            {
                Dictionary<string, object> data = ReadDictionary(row, "data") ?? new Dictionary<string, object>();
                Dictionary<string, object> gate = ReadDictionary(data, "actionGate") ?? new Dictionary<string, object>();
                return ReadBool(gate, "needed", false)
                    && !string.IsNullOrWhiteSpace(ReadString(gate, "intent", ""))
                    && !string.IsNullOrWhiteSpace(ReadString(gate, "reason", ""))
                    && !string.IsNullOrWhiteSpace(ReadString(data, "visibleReply", ""))
                    && ReadDictionaryList(data, "queued").Count > 0;
            });
            if (!grounded)
            {
                evidence = "Queued action count=" + queued.Count
                    + ", but the correlated visible reply, accepted action gate, and queued planner record were incomplete.";
                return false;
            }

            Dictionary<string, object> requestHero = (auditEvidence ?? new List<Dictionary<string, object>>())
                .Where(row => ReadString(row, "phase", "").Equals("dialogue.request", StringComparison.OrdinalIgnoreCase))
                .Select(row => ReadDictionary(ReadDictionary(row, "data") ?? new Dictionary<string, object>(), "hero"))
                .FirstOrDefault(hero => hero != null) ?? new Dictionary<string, object>();
            string speakerHeroId = ReadString(requestHero, "heroStringId", "");
            string speakerClanId = ReadString(requestHero, "clanId", "");
            string playerHeroId = FirstNonEmpty(
                ReadFirstString(requestHero, "mainHeroStringId", "playerHeroStringId"), "main_hero");
            string playerClanId = ReadString(requestHero, "playerClanId", "");
            string speakerKingdomId = ReadString(requestHero, "kingdomId", "");
            string playerKingdomId = ReadString(requestHero, "playerKingdomId", "");

            foreach (Dictionary<string, object> queuedRow in queued)
            {
                Dictionary<string, object> action = ReadDictionary(queuedRow, "record") ?? queuedRow;
                string command = ReadString(action, "command", "");
                bool isNpcAgainstPlayer = command.Equals("attack_player_party", StringComparison.OrdinalIgnoreCase)
                    || command.Equals("duel_player", StringComparison.OrdinalIgnoreCase);
                if (!isNpcAgainstPlayer)
                    continue;
                bool directionOk =
                    ReadString(action, "actorHeroStringId", "").Equals(speakerHeroId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(action, "targetHeroStringId", "").Equals(playerHeroId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(action, "actorClanStringId", "").Equals(speakerClanId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(action, "targetClanStringId", "").Equals(playerClanId, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(speakerKingdomId)
                        || ReadString(action, "actorKingdomStringId", "").Equals(speakerKingdomId, StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrWhiteSpace(playerKingdomId)
                        ? string.IsNullOrWhiteSpace(ReadString(action, "targetKingdomStringId", ""))
                        : ReadString(action, "targetKingdomStringId", "").Equals(playerKingdomId, StringComparison.OrdinalIgnoreCase));
                if (!directionOk)
                {
                    evidence = "Grounded " + command + " was guarded, but entity direction was invalid: actorHero="
                        + ReadString(action, "actorHeroStringId", "") + ", targetHero="
                        + ReadString(action, "targetHeroStringId", "") + ", actorClan="
                        + ReadString(action, "actorClanStringId", "") + ", targetClan="
                        + ReadString(action, "targetClanStringId", "") + ".";
                    return false;
                }

                if (command.Equals("duel_player", StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, object> terms = ReadDictionary(action, "terms") ?? new Dictionary<string, object>();
                    string duelMode = ReadString(terms, "duelMode", "training").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
                    bool modeIsLethal = duelMode == "lethal" || duelMode == "death" || duelMode == "to_the_death"
                        || duelMode == "honor" || duelMode == "honour";
                    if (modeIsLethal != ReadBool(terms, "lethal", false))
                    {
                        evidence = "Grounded duel_player had contradictory duel terms: duelMode="
                            + duelMode + ", lethal=" + ReadBool(terms, "lethal", false) + ".";
                        return false;
                    }
                }
            }

            evidence = "Queued action count=" + queued.Count
                + "; every action was visibly grounded, accepted by the private gate, and retained under guarded effects"
                + (queued.Any(row => ReadString(ReadDictionary(row, "record") ?? row, "command", "")
                    .Equals("attack_player_party", StringComparison.OrdinalIgnoreCase)
                    || ReadString(ReadDictionary(row, "record") ?? row, "command", "")
                    .Equals("duel_player", StringComparison.OrdinalIgnoreCase))
                    ? " with correct NPC-to-player entity direction." : ".");
            return true;
        }

        internal static List<string> LiveTestDynamicCharacteristicPromptLines(
            Dictionary<string, object> promptData)
        {
            List<string> lines = new List<string>();
            foreach (Dictionary<string, object> message in ReadDictionaryList(promptData, "messages"))
            {
                string content = ReadString(message, "content", "");
                Match section = Regex.Match(content,
                    @"(?ms)^(?:RELEVANT\s+)?DYNAMIC CHARACTERISTICS\s*\n(?<body>.*?)(?=^[A-Z][A-Z0-9 &()\-]{4,}\s*$|\z)");
                if (!section.Success) continue;
                foreach (Match match in Regex.Matches(
                    section.Groups["body"].Value, @"(?m)^- \[[^\]]+\]\s+(.+)$"))
                    lines.Add(match.Groups[1].Value.Trim());
            }
            return lines;
        }

        private static List<string> ReadLiveTestContextPullIds(
            Dictionary<string, object> source,
            string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
                return new List<string>();

            IEnumerable<object> rows;
            if (value is ArrayList array)
                rows = array.Cast<object>();
            else if (value is IEnumerable<object> enumerable)
                rows = enumerable;
            else
                rows = new[] { value };

            return rows.Select(row =>
                row is Dictionary<string, object> dictionary
                    ? ReadString(dictionary, "id", "")
                    : Convert.ToString(row))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Dictionary<string, object> LiveTestReplyTiming(Dictionary<string, object> reply)
        {
            Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? new Dictionary<string, object>();
            return ReadDictionary(raw, "timing")
                ?? ReadDictionary(reply, "timing")
                ?? new Dictionary<string, object>();
        }

        private static List<Dictionary<string, object>> LiveTestReplyRelationshipAssessments(Dictionary<string, object> reply)
        {
            List<Dictionary<string, object>> direct = ReadDictionaryList(reply, "relationshipAssessments");
            if (direct.Count > 0) return direct;
            return ReadDictionaryList(ReadDictionary(reply, "rawResponse") ?? new Dictionary<string, object>(), "relationshipAssessments");
        }

        private static bool LiveTestRelationshipReceiptsAreValid(
            Dictionary<string, object> run,
            Dictionary<string, object> command,
            List<Dictionary<string, object>> replies,
            List<Dictionary<string, object>> auditEvidence,
            Dictionary<string, object> requested,
            out string evidence)
        {
            List<string> errors = new List<string>();
            List<Dictionary<string, object>> relationshipRows = (auditEvidence ?? new List<Dictionary<string, object>>())
                .Where(row => ReadString(row, "phase", "")
                    .Equals("relationship.conversation", StringComparison.OrdinalIgnoreCase))
                .ToList();
            List<Dictionary<string, object>> auditReceipts = relationshipRows
                .SelectMany(row => ReadDictionaryList(
                    ReadDictionary(row, "data") ?? new Dictionary<string, object>(), "receipts"))
                .ToList();
            List<Dictionary<string, object>> nativeChanges = relationshipRows
                .SelectMany(row => ReadDictionaryList(
                    ReadDictionary(row, "data") ?? new Dictionary<string, object>(), "nativeChanges"))
                .ToList();
            if (relationshipRows.Count == 0) errors.Add("No relationship.conversation audit row was linked to the command.");
            if (auditReceipts.Count == 0) errors.Add("No conversation relationship receipt was emitted.");

            Dictionary<string, List<Dictionary<string, object>>> auditByReceipt = auditReceipts
                .Where(row => !string.IsNullOrWhiteSpace(ReadString(row, "receiptId", "")))
                .GroupBy(row => ReadString(row, "receiptId", ""), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            if (auditReceipts.Any(row => string.IsNullOrWhiteSpace(ReadString(row, "receiptId", ""))))
                errors.Add("A relationship receipt omitted its stable receipt id.");
            foreach (KeyValuePair<string, List<Dictionary<string, object>>> occurrence in auditByReceipt)
            {
                int nonIdempotent = occurrence.Value.Count(row => !ReadBool(row, "idempotent", false));
                if (nonIdempotent > 1)
                    errors.Add("Receipt " + occurrence.Key + " was emitted as newly applied more than once.");
            }

            string campaignId = ReadString(run, "campaignId", "");
            List<Dictionary<string, object>> receipts = new List<Dictionary<string, object>>();
            if (!string.IsNullOrWhiteSpace(campaignId) && auditByReceipt.Count > 0)
            {
                try
                {
                    using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        RelationshipsService.EnsureConversationRelationshipSchema(connection);
                        DialogueService.EnsureWorldHistoryLieDetectionSchema(connection);
                        foreach (KeyValuePair<string, List<Dictionary<string, object>>> occurrence in auditByReceipt)
                        {
                            Dictionary<string, object> stored = WorldSimulationStore.SelectConversationRelationshipReceipts(connection, new Dictionary<string, object> { ["id"] = occurrence.Key }).FirstOrDefault();
                            if (stored == null)
                            {
                                errors.Add("Receipt " + occurrence.Key + " was absent from authoritative campaign storage.");
                                continue;
                            }
                            Dictionary<string, object> receipt = RelationshipsService.ConversationReceiptResponse(
                                stored, occurrence.Value.All(row => ReadBool(row, "idempotent", false)));
                            receipt["auditOccurrenceCount"] = occurrence.Value.Count;
                            receipt["auditNonIdempotentCount"] = occurrence.Value.Count(row => !ReadBool(row, "idempotent", false));

                            string lieCheckId = ReadString(receipt, "lieCheckId", "");
                            if (!string.IsNullOrWhiteSpace(lieCheckId))
                            {
                                Dictionary<string, object> lie = WorldSimulationStore.SelectWorldHistoryLieChecks(connection, new Dictionary<string, object> { ["id"] = lieCheckId }).FirstOrDefault();
                                string outcome = ReadString(lie, "outcome", "").ToLowerInvariant();
                                string verdict = ReadString(lie, "objective_verdict", "").ToLowerInvariant();
                                bool verified = lie != null
                                    && new[] { "contradicted", "false", "deceptive", "verified_lie" }.Contains(verdict)
                                    && !ReadString(lie, "knowledge_basis", "none").Equals("none", StringComparison.OrdinalIgnoreCase)
                                    && (outcome == "detected_firsthand" || outcome == "detected_secondhand")
                                    && ReadString(lie, "claimant_id", "").Equals(
                                        ReadString(receipt, "targetHeroStringId", ""), StringComparison.OrdinalIgnoreCase)
                                    && ReadString(lie, "target_id", "").Equals(
                                        ReadString(receipt, "observerHeroStringId", ""), StringComparison.OrdinalIgnoreCase);
                                receipt["verifiedLieCheck"] = verified;
                                receipt["lieCheckEvidence"] = lie == null
                                    ? new Dictionary<string, object>()
                                    : new Dictionary<string, object>
                                    {
                                        ["lieCheckId"] = lieCheckId,
                                        ["claimantId"] = ReadString(lie, "claimant_id", ""),
                                        ["observerId"] = ReadString(lie, "target_id", ""),
                                        ["objectiveVerdict"] = ReadString(lie, "objective_verdict", ""),
                                        ["knowledgeBasis"] = ReadString(lie, "knowledge_basis", "none"),
                                        ["outcome"] = ReadString(lie, "outcome", "")
                                    };
                            }

                            string benefitId = ReadString(receipt, "benefitEventId", "");
                            if (!string.IsNullOrWhiteSpace(benefitId))
                            {
                                Dictionary<string, object> benefit = WorldSimulationStore.SelectConversationRelationshipBenefits(connection, new Dictionary<string, object> { ["id"] = benefitId }).FirstOrDefault();
                                receipt["benefitLedger"] = benefit == null
                                    ? new Dictionary<string, object>()
                                    : new Dictionary<string, object>
                                    {
                                        ["benefitId"] = ReadString(benefit, "benefit_id", ""),
                                        ["giverId"] = ReadString(benefit, "giver_id", ""),
                                        ["recipientId"] = ReadString(benefit, "recipient_id", ""),
                                        ["benefitKind"] = ReadString(benefit, "benefit_kind", ""),
                                        ["sourceEventId"] = ReadString(benefit, "source_event_id", ""),
                                        ["sourceTurnId"] = ReadString(benefit, "source_turn_id", ""),
                                        ["originalDelta"] = ReadInt(benefit, "original_delta", 0),
                                        ["retainedAppreciation"] = ReadDouble(benefit, "retained_appreciation", 1d),
                                        ["continuedUtility"] = ReadDouble(benefit, "continued_utility", 1d),
                                        ["status"] = ReadString(benefit, "status", "")
                                    };
                            }
                            receipts.Add(receipt);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("Could not reconcile authoritative relationship storage: " + DialogueService.LimitText(ex.Message, 400));
                }
            }

            Dictionary<string, object> policy = ReadDictionary(requested, "relationshipPolicy")
                ?? new Dictionary<string, object>();
            string playerId = FirstNonEmpty(
                ReadFirstString(policy, "playerHeroStringId", "playerId"),
                FindNestedLiveTestString(auditEvidence, "playerHeroStringId", "mainHeroStringId"));
            List<string> expectedNpcIds = ReadStringList(policy, "expectedNpcIds");
            List<string> replySpeakerIds = (replies ?? new List<Dictionary<string, object>>())
                .Select(reply => ReadFirstString(reply, "heroId", "speakerHeroStringId"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (expectedNpcIds.Count == 0) expectedNpcIds = replySpeakerIds;
            if (string.IsNullOrWhiteSpace(playerId))
            {
                playerId = receipts
                    .SelectMany(receipt => new[]
                    {
                        ReadString(receipt, "observerHeroStringId", ""),
                        ReadString(receipt, "targetHeroStringId", "")
                    })
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)
                        && !expectedNpcIds.Contains(value, StringComparer.OrdinalIgnoreCase));
            }

            bool valid = LiveTestRelationshipReceiptSetIsValid(
                receipts, nativeChanges, replySpeakerIds, expectedNpcIds, playerId, policy, errors);
            evidence = "rows=" + relationshipRows.Count
                + "; auditReceipts=" + auditReceipts.Count
                + "; storedReceipts=" + receipts.Count
                + "; speakers=" + replySpeakerIds.Count
                + "; player=" + playerId
                + "; scenario=" + ReadString(policy, "scenario", "general")
                + (errors.Count == 0 ? "; all directional, tier, lineage, native, and idempotency checks passed."
                    : "; failures=" + string.Join(" | ", errors.Take(12)));
            return valid && errors.Count == 0;
        }

        private static bool LiveTestRelationshipReceiptSetIsValid(
            List<Dictionary<string, object>> receipts,
            List<Dictionary<string, object>> nativeChanges,
            List<string> replySpeakerIds,
            List<string> expectedNpcIds,
            string playerId,
            Dictionary<string, object> policy,
            List<string> errors)
        {
            receipts = receipts ?? new List<Dictionary<string, object>>();
            nativeChanges = nativeChanges ?? new List<Dictionary<string, object>>();
            replySpeakerIds = replySpeakerIds ?? new List<string>();
            expectedNpcIds = expectedNpcIds ?? new List<string>();
            policy = policy ?? new Dictionary<string, object>();
            errors = errors ?? new List<string>();
            HashSet<string> validTiers = new HashSet<string>(
                new[] { "routine", "meaningful", "harmful_lie", "hostile", "severe", "transformative", "gift_witness", "gift_leverage" },
                StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<string, Dictionary<string, object>> pair in receipts.GroupBy(receipt =>
                ReadString(receipt, "exchangeId", "") + "|"
                + ReadString(receipt, "observerHeroStringId", "") + "|"
                + ReadString(receipt, "targetHeroStringId", ""), StringComparer.OrdinalIgnoreCase))
            {
                if (pair.Count() > 1)
                    errors.Add("More than one net receipt exists for observer-target key " + pair.Key + ".");
            }

            foreach (Dictionary<string, object> receipt in receipts)
            {
                string receiptId = ReadString(receipt, "receiptId", "");
                string exchangeId = ReadString(receipt, "exchangeId", "");
                string sourceTurnId = ReadString(receipt, "sourceTurnId", "");
                string observerId = ReadString(receipt, "observerHeroStringId", "");
                string targetId = ReadString(receipt, "targetHeroStringId", "");
                string tier = ReadString(receipt, "severityTier", "").ToLowerInvariant();
                string valence = ReadString(receipt, "valence", "").ToLowerInvariant();
                string status = ReadString(receipt, "status", "");
                int baseDelta = ReadInt(receipt, "baseDelta", 0);
                int modifierDelta = ReadInt(receipt, "modifierDelta", 0);
                int finalDelta = ReadInt(receipt, "finalDelta", 0);
                int priorAffinity = ReadInt(receipt, "priorAffinity", 0);
                int resultingAffinity = ReadInt(receipt, "resultingAffinity", 0);
                int priorNative = ReadInt(receipt, "priorNativeRelation", 0);
                int resultingNative = ReadInt(receipt, "resultingNativeRelation", 0);
                int nativePairDelta = ReadInt(receipt, "nativePairDelta", 0);
                if (string.IsNullOrWhiteSpace(receiptId)) errors.Add("A stored receipt has no receipt id.");
                if (string.IsNullOrWhiteSpace(exchangeId)) errors.Add(receiptId + " has no exchange id.");
                if (string.IsNullOrWhiteSpace(sourceTurnId)) errors.Add(receiptId + " has no source-turn lineage.");
                if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(targetId))
                    errors.Add(receiptId + " has an empty observer or target.");
                if (!string.IsNullOrWhiteSpace(observerId)
                    && observerId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
                    errors.Add(receiptId + " is a forbidden self-directed relationship change.");
                if (!validTiers.Contains(tier)) errors.Add(receiptId + " has invalid tier " + tier + ".");
                if (finalDelta < -20 || finalDelta > 20) errors.Add(receiptId + " exceeds the absolute twenty-point cap.");
                if (Clamp(priorAffinity + finalDelta, -100, 100) != resultingAffinity)
                    errors.Add(receiptId + " has inconsistent directional affinity arithmetic.");
                if (Clamp(priorNative + nativePairDelta, -100, 100) != resultingNative)
                    errors.Add(receiptId + " has inconsistent native-relation arithmetic.");
                if (baseDelta + modifierDelta != finalDelta)
                    errors.Add(receiptId + " does not reconcile base plus modifiers to its final delta.");
                if (finalDelta > 0 && valence != "positive")
                    errors.Add(receiptId + " has a positive delta with non-positive valence.");
                if (finalDelta < 0 && valence != "negative")
                    errors.Add(receiptId + " has a negative delta with non-negative valence.");
                if (!LiveTestRelationshipTierMagnitudeIsValid(tier, finalDelta, baseDelta, status))
                    errors.Add(receiptId + " violates the " + tier + " tier anchor/range.");
                if (tier != "routine"
                    && tier != "harmful_lie"
                    && string.IsNullOrWhiteSpace(ReadString(receipt, "currentConductQuote", "")))
                    errors.Add(receiptId + " has a non-routine consequence without persisted exact current-conduct evidence.");

                string nativeStatus = ReadString(receipt, "nativeApplicationStatus", "");
                Dictionary<string, object> nativeReceipt = ReadDictionary(receipt, "nativeReceipt")
                    ?? new Dictionary<string, object>();
                List<Dictionary<string, object>> linkedChanges = nativeChanges
                    .Where(change => ReadStringList(change, "receiptIds")
                        .Contains(receiptId, StringComparer.OrdinalIgnoreCase))
                    .GroupBy(change => string.Join("|", new[]
                    {
                        ReadString(change, "subjectId", ""),
                        ReadString(change, "targetId", ""),
                        ReadInt(change, "delta", 0).ToString(CultureInfo.InvariantCulture),
                        string.Join(",", ReadStringList(change, "receiptIds").OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                    }), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                if (finalDelta == 0)
                {
                    if (linkedChanges.Count == 0)
                    {
                        if (!nativeStatus.Equals("skipped", StringComparison.OrdinalIgnoreCase))
                            errors.Add(receiptId + " has an isolated zero delta without a skipped native application.");
                    }
                    else
                    {
                        if (linkedChanges.Count != 1
                            || !nativeStatus.Equals("applied", StringComparison.OrdinalIgnoreCase)
                            || ReadLong(receipt, "nativeAppliedTs", 0) <= 0)
                        {
                            errors.Add(receiptId + " was not atomically acknowledged with its nonzero reciprocal pair application.");
                        }
                    }
                }
                else
                {
                    if (!nativeStatus.Equals("applied", StringComparison.OrdinalIgnoreCase)
                        || ReadLong(receipt, "nativeAppliedTs", 0) <= 0)
                        errors.Add(receiptId + " lacks a confirmed native personal-relation acknowledgement.");
                    if (linkedChanges.Count != 1)
                        errors.Add(receiptId + " must belong to exactly one native pair application.");
                    string nativeSubject = ReadString(nativeReceipt, "subjectId", "");
                    string nativeTarget = ReadString(nativeReceipt, "targetId", "");
                    bool samePair = (nativeSubject.Equals(observerId, StringComparison.OrdinalIgnoreCase)
                            && nativeTarget.Equals(targetId, StringComparison.OrdinalIgnoreCase))
                        || (nativeSubject.Equals(targetId, StringComparison.OrdinalIgnoreCase)
                            && nativeTarget.Equals(observerId, StringComparison.OrdinalIgnoreCase));
                    if (!samePair
                        || (nativePairDelta != 0 && ReadInt(nativeReceipt, "delta", 0) == 0))
                        errors.Add(receiptId + " native receipt does not target the same actual hero pair.");
                }
            }

            foreach (IGrouping<string, Dictionary<string, object>> nativePair in receipts.GroupBy(receipt =>
                ReadString(receipt, "exchangeId", "") + "|"
                + RelationshipsService.AmbientPairKey(
                    ReadString(receipt, "observerHeroStringId", ""),
                    ReadString(receipt, "targetHeroStringId", "")),
                StringComparer.OrdinalIgnoreCase))
            {
                List<Dictionary<string, object>> pairReceipts = nativePair.ToList();
                int priorNative = ReadInt(pairReceipts[0], "priorNativeRelation", 0);
                int resultingNative = ReadInt(pairReceipts[0], "resultingNativeRelation", 0);
                int actualPairDelta = resultingNative - priorNative;
                int requestedPairDelta = Clamp(
                    pairReceipts.Sum(receipt => ReadInt(receipt, "finalDelta", 0)),
                    -20, 20);
                if (pairReceipts.Any(receipt =>
                        ReadInt(receipt, "priorNativeRelation", int.MinValue) != priorNative
                        || ReadInt(receipt, "resultingNativeRelation", int.MinValue) != resultingNative
                        || ReadInt(receipt, "nativePairDelta", int.MinValue) != actualPairDelta))
                {
                    errors.Add("Native pair receipts do not share one atomic prior/result/delta for " + nativePair.Key + ".");
                }

                HashSet<string> pairReceiptIds = new HashSet<string>(
                    pairReceipts.Select(receipt => ReadString(receipt, "receiptId", ""))
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.OrdinalIgnoreCase);
                List<Dictionary<string, object>> pairNativeChanges = nativeChanges
                    .Where(change => ReadStringList(change, "receiptIds").Any(pairReceiptIds.Contains))
                    .GroupBy(change => string.Join("|", new[]
                    {
                        ReadString(change, "subjectId", ""),
                        ReadString(change, "targetId", ""),
                        ReadInt(change, "delta", 0).ToString(CultureInfo.InvariantCulture),
                        string.Join(",", ReadStringList(change, "receiptIds")
                            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                    }), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                bool anyDirectionalDelta = pairReceipts.Any(receipt =>
                    ReadInt(receipt, "finalDelta", 0) != 0);
                if (anyDirectionalDelta
                    && (pairNativeChanges.Count != 1
                        || ReadInt(pairNativeChanges[0], "delta", int.MinValue) != requestedPairDelta))
                {
                    errors.Add("Native pair application did not preserve the adjudicated aggregate delta for " + nativePair.Key + ".");
                }
                if (!anyDirectionalDelta && pairNativeChanges.Count != 0)
                {
                    errors.Add("An entirely band-limited native pair queued an unnecessary application for " + nativePair.Key + ".");
                }
                if (anyDirectionalDelta && pairReceipts.Any(receipt =>
                    ReadInt(ReadDictionary(receipt, "nativeReceipt") ?? new Dictionary<string, object>(),
                        "delta", int.MinValue) != requestedPairDelta))
                {
                    errors.Add("Native pair acknowledgement did not report the requested aggregate delta for " + nativePair.Key + ".");
                }
            }

            bool requireEverySpeaker = ReadBool(policy, "requiresEverySpeaker", true);
            bool requirePlayerReaction = ReadBool(policy, "requiresPlayerReaction", true);
            if (requireEverySpeaker)
            {
                foreach (string speakerId in replySpeakerIds)
                {
                    if (!receipts.Any(receipt => ReadString(receipt, "observerHeroStringId", "")
                        .Equals(speakerId, StringComparison.OrdinalIgnoreCase)))
                        errors.Add("Speaking NPC " + speakerId + " produced no directional relationship receipt.");
                }
            }
            if (requirePlayerReaction)
            {
                if (string.IsNullOrWhiteSpace(playerId)) errors.Add("The player hero id could not be reconciled.");
                else
                {
                    foreach (string speakerId in replySpeakerIds)
                    {
                        if (!receipts.Any(receipt =>
                                ReadString(receipt, "observerHeroStringId", "").Equals(speakerId, StringComparison.OrdinalIgnoreCase)
                                && ReadString(receipt, "targetHeroStringId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)))
                            errors.Add("Speaking NPC " + speakerId + " did not adjudicate its reaction toward the player.");
                    }
                }
            }
            if (ReadBool(policy, "requiresNpcToNpc", false))
            {
                bool npcToNpc = receipts.Any(receipt =>
                    expectedNpcIds.Contains(ReadString(receipt, "observerHeroStringId", ""), StringComparer.OrdinalIgnoreCase)
                    && expectedNpcIds.Contains(ReadString(receipt, "targetHeroStringId", ""), StringComparer.OrdinalIgnoreCase)
                    && !ReadString(receipt, "observerHeroStringId", "").Equals(
                        ReadString(receipt, "targetHeroStringId", ""), StringComparison.OrdinalIgnoreCase));
                if (!npcToNpc) errors.Add("No directional NPC-to-NPC receipt was produced for a dependent group reply.");
            }
            foreach (Dictionary<string, object> change in nativeChanges)
            {
                string subject = ReadString(change, "subjectId", "");
                string target = ReadString(change, "targetId", "");
                if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(target)
                    || subject.Equals(target, StringComparison.OrdinalIgnoreCase))
                    errors.Add("A native relationship application had an invalid actual-hero pair.");
                if (Math.Abs(ReadInt(change, "delta", 0)) > 20)
                    errors.Add("A native pair application exceeded the absolute twenty-point cap.");
                bool playerInvolved = !string.IsNullOrWhiteSpace(playerId)
                    && (subject.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                        || target.Equals(playerId, StringComparison.OrdinalIgnoreCase));
                if (!playerInvolved && ReadBool(change, "showNotification", false))
                    errors.Add("An NPC-to-NPC relationship change incorrectly requested a player notification.");
            }

            string scenario = ReadString(policy, "scenario", "general").ToLowerInvariant();
            List<Dictionary<string, object>> playerReceipts = string.IsNullOrWhiteSpace(playerId)
                ? new List<Dictionary<string, object>>()
                : receipts.Where(receipt => ReadString(receipt, "targetHeroStringId", "")
                    .Equals(playerId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (scenario == "unsupported_claim")
            {
                if (playerReceipts.Any(receipt =>
                    ReadString(receipt, "severityTier", "").Equals("harmful_lie", StringComparison.OrdinalIgnoreCase)
                    || ReadBool(receipt, "verifiedLieCheck", false)))
                    errors.Add("An unsupported claim was incorrectly adjudicated as a verified harmful lie.");
            }
            else if (scenario == "verified_lie")
            {
                if (!playerReceipts.Any(receipt =>
                    ReadString(receipt, "severityTier", "").Equals("harmful_lie", StringComparison.OrdinalIgnoreCase)
                    && ReadBool(receipt, "verifiedLieCheck", false)
                    && ReadInt(receipt, "finalDelta", 0) >= -7
                    && ReadInt(receipt, "finalDelta", 0) <= -4))
                    errors.Add("No evidence-backed harmful lie receipt applied the required -4 to -7 tier.");
            }
            else if (scenario == "routine")
            {
                if (playerReceipts.Count == 0 || playerReceipts.Any(receipt =>
                    !ReadString(receipt, "severityTier", "").Equals("routine", StringComparison.OrdinalIgnoreCase)
                    || (Math.Abs(ReadInt(receipt, "finalDelta", 0)) != 1
                        && !(ReadInt(receipt, "finalDelta", 0) == 0
                            && ReadString(receipt, "status", "").Equals("routine_band_limited", StringComparison.OrdinalIgnoreCase)))))
                    errors.Add("Routine conversation did not produce exactly +/-1, except for a documented outward band limit.");
            }
            else if (scenario == "meaningful_positive")
            {
                if (!playerReceipts.Any(receipt =>
                    ReadString(receipt, "severityTier", "").Equals("meaningful", StringComparison.OrdinalIgnoreCase)
                    && ReadInt(receipt, "finalDelta", 0) >= 2
                    && ReadInt(receipt, "finalDelta", 0) <= 4))
                    errors.Add("No meaningful positive-support receipt applied the required +2 to +4 tier.");
            }
            else if (scenario == "hostile")
            {
                if (!playerReceipts.Any(receipt =>
                    ReadString(receipt, "severityTier", "").Equals("hostile", StringComparison.OrdinalIgnoreCase)
                    && ReadInt(receipt, "finalDelta", 0) >= -7
                    && ReadInt(receipt, "finalDelta", 0) <= -4))
                    errors.Add("No hostile-speech receipt applied the required -4 to -7 tier.");
            }
            else if (scenario == "severe")
            {
                if (!playerReceipts.Any(receipt =>
                    ReadString(receipt, "severityTier", "").Equals("severe", StringComparison.OrdinalIgnoreCase)
                    && ReadInt(receipt, "finalDelta", 0) >= -15
                    && ReadInt(receipt, "finalDelta", 0) <= -8))
                    errors.Add("No grave threat/abuse receipt applied the required -8 to -15 tier.");
            }
            else if (scenario == "gift_recipient")
            {
                string recipientId = ReadFirstString(policy, "expectedGiftRecipientHeroStringId", "giftRecipientHeroStringId");
                Dictionary<string, object> recipient = playerReceipts.FirstOrDefault(receipt =>
                    ReadString(receipt, "observerHeroStringId", "").Equals(recipientId, StringComparison.OrdinalIgnoreCase));
                Dictionary<string, object> ledger = ReadDictionary(recipient, "benefitLedger")
                    ?? new Dictionary<string, object>();
                bool requireTransformative = ReadBool(
                    policy, "requireTransformativeRecipient", false);
                if (recipient == null
                    || !ReadString(recipient, "giftRecipientHeroStringId", "").Equals(recipientId, StringComparison.OrdinalIgnoreCase)
                    || ReadInt(recipient, "finalDelta", 0) <= 0
                    || (!ReadString(recipient, "severityTier", "").Equals("meaningful", StringComparison.OrdinalIgnoreCase)
                        && !ReadString(recipient, "severityTier", "").Equals("transformative", StringComparison.OrdinalIgnoreCase))
                    || (requireTransformative
                        && (!ReadString(recipient, "severityTier", "").Equals("transformative", StringComparison.OrdinalIgnoreCase)
                            || ReadInt(recipient, "finalDelta", 0) < 12
                            || ReadInt(recipient, "finalDelta", 0) > 20))
                    || string.IsNullOrWhiteSpace(ReadString(recipient, "benefitEventId", ""))
                    || !ReadString(ledger, "giverId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    || !ReadString(ledger, "recipientId", "").Equals(recipientId, StringComparison.OrdinalIgnoreCase)
                    || ReadInt(ledger, "originalDelta", int.MinValue) != ReadInt(recipient, "finalDelta", 0))
                    errors.Add("The accepted gift did not create recipient-specific positive appreciation and benefit lineage.");
                foreach (Dictionary<string, object> witness in playerReceipts.Where(receipt =>
                    !ReadString(receipt, "observerHeroStringId", "").Equals(recipientId, StringComparison.OrdinalIgnoreCase)))
                {
                    if (ReadString(witness, "severityTier", "").Equals("transformative", StringComparison.OrdinalIgnoreCase)
                        || !string.IsNullOrWhiteSpace(ReadString(witness, "benefitEventId", ""))
                        || (ReadString(witness, "severityTier", "").Equals("gift_witness", StringComparison.OrdinalIgnoreCase)
                            && Math.Abs(ReadInt(witness, "baseDelta", 0)) > 2))
                        errors.Add("A gift witness received the recipient's amplified benefit instead of an independent proportionate reaction.");
                }
            }
            else if (scenario == "gift_leverage")
            {
                string recipientId = ReadFirstString(policy, "expectedGiftRecipientHeroStringId", "giftRecipientHeroStringId");
                Dictionary<string, object> leverage = playerReceipts.FirstOrDefault(receipt =>
                    ReadString(receipt, "observerHeroStringId", "").Equals(recipientId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(receipt, "severityTier", "").Equals("gift_leverage", StringComparison.OrdinalIgnoreCase));
                Dictionary<string, object> ledger = ReadDictionary(leverage, "benefitLedger")
                    ?? new Dictionary<string, object>();
                if (leverage == null
                    || ReadInt(leverage, "finalDelta", 0) >= 0
                    || string.IsNullOrWhiteSpace(ReadString(leverage, "benefitEventId", ""))
                    || !ReadString(ledger, "giverId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)
                    || !ReadString(ledger, "recipientId", "").Equals(recipientId, StringComparison.OrdinalIgnoreCase)
                    || ReadDouble(ledger, "retainedAppreciation", 1d) >= 0.999d
                    || ReadString(ledger, "status", "").Equals("retained", StringComparison.OrdinalIgnoreCase))
                    errors.Add("The later coercive demand did not link the recipient's gift, reduce retained appreciation, and apply a separate negative consequence.");
            }
            else if (scenario == "transformative")
            {
                if (!playerReceipts.Any(receipt =>
                    ReadString(receipt, "severityTier", "").Equals("transformative", StringComparison.OrdinalIgnoreCase)
                    && ReadInt(receipt, "finalDelta", 0) >= 12
                    && ReadInt(receipt, "finalDelta", 0) <= 20
                    && (ReadDictionary(receipt, "benefitLedger") ?? new Dictionary<string, object>()).Count > 0))
                    errors.Add("No verified transformative benefit applied the required +12 to +20 tier with durable lineage.");
            }
            return errors.Count == 0;
        }

        private static bool LiveTestRelationshipTierMagnitudeIsValid(
            string tier, int finalDelta, int baseDelta, string status)
        {
            int magnitude = Math.Abs(finalDelta);
            switch ((tier ?? "").ToLowerInvariant())
            {
                case "routine":
                    return magnitude == 1
                        || (finalDelta == 0
                            && string.Equals(status, "routine_band_limited", StringComparison.OrdinalIgnoreCase));
                case "meaningful":
                    return magnitude >= 2 && magnitude <= 4;
                case "harmful_lie":
                case "hostile":
                    return finalDelta <= -4 && finalDelta >= -7;
                case "severe":
                    return finalDelta <= -8 && finalDelta >= -15;
                case "transformative":
                    return finalDelta >= 12 && finalDelta <= 20;
                case "gift_witness":
                    return magnitude >= 1 && magnitude <= 20 && Math.Abs(baseDelta) <= 2;
                case "gift_leverage":
                    return finalDelta <= -1 && finalDelta >= -20;
                default:
                    return false;
            }
        }

        private static string FindNestedLiveTestString(object value, params string[] keys)
        {
            HashSet<string> wanted = new HashSet<string>(keys ?? new string[0], StringComparer.OrdinalIgnoreCase);
            Func<object, string> visit = null;
            visit = current =>
            {
                if (current is Dictionary<string, object> dictionary)
                {
                    foreach (KeyValuePair<string, object> pair in dictionary)
                    {
                        if (wanted.Contains(pair.Key))
                        {
                            string found = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                            if (!string.IsNullOrWhiteSpace(found)) return found;
                        }
                    }
                    foreach (object nested in dictionary.Values)
                    {
                        string found = visit(nested);
                        if (!string.IsNullOrWhiteSpace(found)) return found;
                    }
                }
                else if (current is IEnumerable sequence && !(current is string))
                {
                    foreach (object nested in sequence)
                    {
                        string found = visit(nested);
                        if (!string.IsNullOrWhiteSpace(found)) return found;
                    }
                }
                return "";
            };
            return visit(value);
        }

        private static bool LiveTestSharedRelationshipHistoryEvidenceIsValid(
            Dictionary<string, object> run,
            Dictionary<string, object> command,
            List<Dictionary<string, object>> replies,
            List<Dictionary<string, object>> auditEvidence,
            Dictionary<string, object> requested,
            bool requirePersistenceReuse,
            out string evidence)
        {
            string campaignId = ReadString(run, "campaignId", "");
            int minimumTargets = Math.Max(1,
                ReadInt(requested, "minimumSharedRelationshipHistoryTargets", 1));
            string expectedMode = ReadString(
                requested, "expectedSharedRelationshipHistoryMode",
                requirePersistenceReuse ? "reuse" : "any").Trim().ToLowerInvariant();
            List<string> errors = new List<string>();
            HashSet<string> historyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> pairKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> asymmetricPairKeys =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> generatedTransitionPairKeys =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<int>> promptedChapterOrdinals =
                new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            int promptCount = 0, targetCount = 0, eligibleTargetCount = 0;
            int generated = 0, reused = 0;
            int fallback = 0, providerCalls = 0;
            long totalDurationMs = 0, providerDurationMs = 0;
            try
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    RelationshipsService.EnsureSharedRelationshipHistorySchema(connection);
                    foreach (Dictionary<string, object> reply in replies
                        ?? new List<Dictionary<string, object>>())
                    {
                        Dictionary<string, object> raw =
                            ReadDictionary(reply, "rawResponse") ?? reply;
                        string correlationId = FirstNonEmpty(
                            ReadString(raw, "correlationId", ""),
                            ReadString(reply, "correlationId", ""));
                        Dictionary<string, object> promptRow =
                            (auditEvidence ?? new List<Dictionary<string, object>>())
                            .LastOrDefault(row =>
                                ReadString(row, "phase", "")
                                    .Equals("prompt.built", StringComparison.OrdinalIgnoreCase)
                                && (string.IsNullOrWhiteSpace(correlationId)
                                    || ReadString(row, "correlationId", "")
                                        .Equals(correlationId, StringComparison.OrdinalIgnoreCase)));
                        if (promptRow == null)
                        {
                            errors.Add("prompt audit missing for " + correlationId);
                            continue;
                        }
                        promptCount++;
                        Dictionary<string, object> promptData =
                            ReadDictionary(promptRow, "data") ?? new Dictionary<string, object>();
                        Dictionary<string, object> envelope =
                            ReadDictionary(promptData, "promptEnvelope")
                            ?? new Dictionary<string, object>();
                        Dictionary<string, object> relationshipPrompt =
                            ReadDictionary(envelope, "npcRelationshipPrompt")
                            ?? new Dictionary<string, object>();
                        string relationshipBlock =
                            ReadString(relationshipPrompt, "block", "");
                        if (!ReadBool(envelope,
                                "npcRelationshipPromptIncluded", false))
                            errors.Add("relationship context was not included in the live prompt: "
                                + correlationId);
                        if (!string.Equals(
                                ReadString(envelope,
                                    "npcRelationshipPromptBlockHash", ""),
                                DialogueService.PromptHash(relationshipBlock),
                                StringComparison.OrdinalIgnoreCase))
                            errors.Add("relationship prompt proof hash mismatch: "
                                + correlationId);
                        Dictionary<string, object> generation =
                            ReadDictionary(relationshipPrompt, "historyGeneration")
                            ?? new Dictionary<string, object>();
                        generated += ReadInt(generation, "generatedCount", 0);
                        reused += ReadInt(generation, "reusedCount", 0);
                        fallback += ReadInt(generation, "fallbackCount", 0);
                        providerCalls += ReadInt(generation, "providerCallCount", 0);
                        totalDurationMs += ReadLong(generation, "durationMs", 0);
                        providerDurationMs += ReadLong(generation, "providerDurationMs", 0);
                        foreach (Dictionary<string, object> storedChapter in
                            ReadDictionaryList(generation, "storedChapters"))
                        {
                            string generatedPairKey =
                                ReadString(storedChapter, "pair_key", "");
                            if (!string.IsNullOrWhiteSpace(generatedPairKey)
                                && ReadInt(storedChapter, "chapter_ordinal", 0) >= 2)
                                generatedTransitionPairKeys.Add(generatedPairKey);
                        }

                        List<Dictionary<string, object>> allTargets =
                            ReadDictionaryList(relationshipPrompt, "targets");
                        List<Dictionary<string, object>> eligibleTargets =
                            allTargets.Where(target =>
                                ReadBool(
                                    target,
                                    "sharedRelationshipHistoryEligible",
                                    false)).ToList();
                        List<Dictionary<string, object>> targets =
                            allTargets
                            .Where(target =>
                                ReadStringList(target, "sharedRelationshipHistoryIds").Count > 0)
                            .ToList();
                        targetCount += targets.Count;
                        eligibleTargetCount += eligibleTargets.Count;
                        int expectedForReply = Math.Min(
                            minimumTargets, eligibleTargets.Count);
                        if (targets.Count < expectedForReply)
                            errors.Add("history targets " + targets.Count + "/"
                                + expectedForReply + " eligible for " + correlationId);

                        string speakerId = FirstNonEmpty(
                            ReadFirstString(raw, "heroStringId", "speakerHeroStringId"),
                            ReadFirstString(reply, "heroId", "speakerHeroStringId"),
                            ReadString(promptRow, "heroId", ""));
                        foreach (Dictionary<string, object> target in targets)
                        {
                            string targetId = ReadString(target, "targetHeroStringId", "");
                            foreach (string historyId in ReadStringList(
                                target, "sharedRelationshipHistoryIds"))
                            {
                                historyIds.Add(historyId);
                                Dictionary<string, object> row = WorldSimulationStore.SelectSharedRelationshipHistoryChapters(connection, new Dictionary<string, object> { ["id"] = historyId })
                                    .FirstOrDefault();
                                if (row == null)
                                {
                                    errors.Add("stored chapter missing: " + historyId);
                                    continue;
                                }
                                string heroA = ReadString(row, "hero_a_id", "");
                                string heroB = ReadString(row, "hero_b_id", "");
                                string pairKey = ReadString(row, "pair_key", "");
                                int chapterOrdinal =
                                    ReadInt(row, "chapter_ordinal", 0);
                                if (!string.IsNullOrWhiteSpace(pairKey))
                                {
                                    pairKeys.Add(pairKey);
                                    HashSet<int> ordinals;
                                    if (!promptedChapterOrdinals.TryGetValue(
                                            pairKey, out ordinals))
                                    {
                                        ordinals = new HashSet<int>();
                                        promptedChapterOrdinals[pairKey] = ordinals;
                                    }
                                    ordinals.Add(chapterOrdinal);
                                    Dictionary<string, object> chemistry = WorldSimulationStore.SelectRelationshipPairChemistry(
                                        connection, new Dictionary<string, object>
                                        {
                                            ["pair"] = pairKey
                                        }).FirstOrDefault();
                                    if (chemistry != null
                                        && Math.Abs(
                                            ReadInt(chemistry, "affinity_a_to_b", 0)
                                            - ReadInt(chemistry, "affinity_b_to_a", 0))
                                            >= 20)
                                        asymmetricPairKeys.Add(pairKey);
                                }
                                bool speakerIsA = heroA.Equals(
                                    speakerId, StringComparison.OrdinalIgnoreCase);
                                bool pairValid = (speakerIsA
                                        && heroB.Equals(targetId, StringComparison.OrdinalIgnoreCase))
                                    || (heroB.Equals(speakerId, StringComparison.OrdinalIgnoreCase)
                                        && heroA.Equals(targetId, StringComparison.OrdinalIgnoreCase));
                                if (!pairValid)
                                    errors.Add("wrong pair for " + historyId + ": "
                                        + speakerId + "->" + targetId);
                                string objective = ReadString(row, "objective_summary", "");
                                string allowed = ReadString(row,
                                    speakerIsA ? "a_interpretation" : "b_interpretation", "");
                                string forbidden = ReadString(row,
                                    speakerIsA ? "b_interpretation" : "a_interpretation", "");
                                if (string.IsNullOrWhiteSpace(objective)
                                    || relationshipBlock.IndexOf(
                                        objective, StringComparison.Ordinal) < 0)
                                    errors.Add("objective absent from prompt: " + historyId);
                                if (string.IsNullOrWhiteSpace(allowed)
                                    || relationshipBlock.IndexOf(
                                        allowed, StringComparison.Ordinal) < 0)
                                    errors.Add("speaker interpretation absent from prompt: " + historyId);
                                if (!string.IsNullOrWhiteSpace(forbidden)
                                    && !forbidden.Equals(allowed, StringComparison.Ordinal)
                                    && relationshipBlock.IndexOf(
                                        forbidden, StringComparison.Ordinal) >= 0)
                                    errors.Add("other private interpretation leaked into prompt: "
                                        + historyId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add("inspection exception: " + DialogueService.LimitText(ex.Message, 600));
            }

            if (expectedMode == "cold" && (providerCalls < 1 || generated + fallback < 1))
                errors.Add("cold generation did not make a provider-backed generation attempt");
            if (eligibleTargetCount < minimumTargets)
                errors.Add("eligible history targets " + eligibleTargetCount + "/"
                    + minimumTargets + " across the conversation");
            if ((expectedMode == "reuse" || requirePersistenceReuse)
                && (providerCalls != 0 || reused < 1 || generated + fallback != 0))
                errors.Add("reuse path generated another chapter or did not report reuse");
            if (expectedMode == "transition"
                && (providerCalls < 1 || generated + fallback < 1))
                errors.Add("transition did not generate an appended chapter");
            if (expectedMode == "transition"
                && generatedTransitionPairKeys.Count == 0)
                errors.Add("transition generation did not report a new chapter ordinal");
            if (expectedMode == "transition")
            {
                foreach (string pairKey in generatedTransitionPairKeys)
                {
                    HashSet<int> ordinals;
                    promptedChapterOrdinals.TryGetValue(pairKey, out ordinals);
                    if (!TransitionHistoryWasAppended(ordinals))
                        errors.Add("transition prompt did not retain both prior and newly appended chapters for "
                            + pairKey);
                }
            }

            bool valid = promptCount == (replies?.Count ?? 0)
                && promptCount > 0 && historyIds.Count > 0 && errors.Count == 0;
            evidence = "prompts=" + promptCount
                + "; targets=" + targetCount
                + "; eligibleTargets=" + eligibleTargetCount
                + "; histories=" + historyIds.Count
                + "; generated=" + generated
                + "; reused=" + reused
                + "; fallback=" + fallback
                + "; providerCalls=" + providerCalls
                + "; historyMs=" + totalDurationMs
                + "; providerMs=" + providerDurationMs
                + "; expectedMode=" + expectedMode
                + (errors.Count == 0 ? "" : "; errors=" + string.Join(" | ", errors.Take(5)));
            command["sharedRelationshipHistoryEvidence"] =
                new Dictionary<string, object>
                {
                    ["valid"] = valid,
                    ["replyCount"] = replies?.Count ?? 0,
                    ["promptCount"] = promptCount,
                    ["targetCount"] = targetCount,
                    ["eligibleTargetCount"] = eligibleTargetCount,
                    ["historyIds"] = historyIds.ToList(),
                    ["pairKeys"] = pairKeys.ToList(),
                    ["asymmetricPairKeys"] = asymmetricPairKeys.ToList(),
                    ["generatedCount"] = generated,
                    ["reusedCount"] = reused,
                    ["fallbackCount"] = fallback,
                    ["providerCallCount"] = providerCalls,
                    ["durationMs"] = totalDurationMs,
                    ["providerDurationMs"] = providerDurationMs,
                    ["expectedMode"] = expectedMode,
                    ["persistenceReuse"] = requirePersistenceReuse,
                    ["privacyLeakCount"] = errors.Count(item =>
                        item.IndexOf("private interpretation leaked",
                            StringComparison.OrdinalIgnoreCase) >= 0),
                    ["errors"] = errors
                };
            return valid;
        }
    }
}
