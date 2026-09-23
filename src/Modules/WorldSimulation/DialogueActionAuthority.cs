using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void BindDialogueActionAuthority(
            Dictionary<string, object> record, Dictionary<string, object> terms,
            Dictionary<string, object> payload, string command, string mappedType,
            string correlationId, List<string> errors, Dictionary<string, object> raw = null)
        {
            if (!IsDialogueActionSource(ReadString(record, "source", ""))) return;
            Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>();
            string speakerId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadFirstString(hero, "heroStringId", "heroId", "id"));
            string playerId = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex") ?? new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(speakerId) || string.IsNullOrWhiteSpace(playerId))
            {
                errors.Add("Dialogue-created actions require exact NPC and player hero identities.");
                return;
            }
            if (index.Count == 0)
            {
                errors.Add("Dialogue-created actions require the live action-resolution index.");
                return;
            }

            Dictionary<string, object> receipt = ReadDictionary(terms, "authorityReceipt")
                ?? new Dictionary<string, object>();
            var agencyAction = new Dictionary<string, object>(record);
            foreach (var field in raw ?? new Dictionary<string, object>())
                if (!agencyAction.ContainsKey(field.Key)) agencyAction[field.Key] = field.Value;
            if (!BindConversationAgencyAuthority(payload, command, terms, receipt, errors, agencyAction)) return;
            string policy;
            if (mappedType.StartsWith("Diplomacy", StringComparison.OrdinalIgnoreCase))
            {
                policy = BindDialogueDiplomacyAuthority(record, receipt, index, speakerId, errors);
            }
            else if (mappedType.Equals("PoliticsMarriageAlliance", StringComparison.OrdinalIgnoreCase))
            {
                policy = BindPersonalMarriageConsent(record, terms, speakerId, playerId, errors);
            }
            else
            {
                policy = BindDialogueParticipantAuthority(record, mappedType, speakerId, playerId, errors);
            }
            if (errors.Count > 0) return;

            receipt["policy"] = policy;
            receipt["acceptedByHeroStringId"] = speakerId;
            receipt["command"] = command;
            receipt["actorHeroStringId"] = ReadString(record, "actorHeroStringId", "");
            receipt["targetHeroStringId"] = ReadString(record, "targetHeroStringId", "");
            receipt["actorKingdomStringId"] = ReadString(record, "actorKingdomStringId", "");
            receipt["targetKingdomStringId"] = ReadString(record, "targetKingdomStringId", "");
            receipt["source"] = "dialogue_authority_v1";
            terms["authorityReceipt"] = receipt;
            record["acceptedByHeroStringId"] = speakerId;
            record["authorizationMode"] = "dialogue_acceptance";
            record["negotiationId"] = correlationId;
            record["termsJson"] = Json.Serialize(terms);
            record["termsHash"] = ComputeActionTermsHash(command, terms);
        }

        private static string BindDialogueDiplomacyAuthority(
            Dictionary<string, object> record, Dictionary<string, object> receipt,
            Dictionary<string, object> index, string speakerId, List<string> errors)
        {
            string actorKingdomId = ReadString(record, "actorKingdomStringId", "");
            string targetKingdomId = ReadString(record, "targetKingdomStringId", "");
            Dictionary<string, object> actor = FindIndexedRow(index, "kingdoms", "kingdomId", actorKingdomId);
            Dictionary<string, object> target = FindIndexedRow(index, "kingdoms", "kingdomId", targetKingdomId);
            string actorLeader = ReadString(actor, "leaderHeroStringId", "");
            string targetLeader = ReadString(target, "leaderHeroStringId", "");
            string authorityKind = ReadString(receipt, "authorityKind", "");
            if (authorityKind.Equals("ambassador_charter_direct", StringComparison.OrdinalIgnoreCase))
            {
                string representedKingdom = ReadString(receipt, "representedKingdomId", "");
                string representedRuler = ReadString(receipt, "representedRulerHeroStringId", "");
                Dictionary<string, object> represented = FindIndexedRow(index, "kingdoms", "kingdomId", representedKingdom);
                if (!ReadString(receipt, "acceptedByHeroStringId", "").Equals(speakerId, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(ReadString(receipt, "postingId", ""))
                    || ReadLong(receipt, "authorityRevision", 0) < 1
                    || (!representedKingdom.Equals(actorKingdomId, StringComparison.OrdinalIgnoreCase)
                        && !representedKingdom.Equals(targetKingdomId, StringComparison.OrdinalIgnoreCase))
                    || !ReadString(represented, "leaderHeroStringId", "").Equals(representedRuler, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("The ambassador charter receipt is missing, stale, or does not represent either kingdom in this action.");
                    return "ambassador_charter_direct";
                }
                record["actorClanStringId"] = "";
                record["targetClanStringId"] = "";
                return "ambassador_charter_direct";
            }
            if (!speakerId.Equals(actorLeader, StringComparison.OrdinalIgnoreCase)
                && !speakerId.Equals(targetLeader, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("A dialogue-created national action requires a current ruler or an exact granted ambassador charter receipt.");
                return "kingdom_ruler";
            }
            receipt["representedKingdomId"] = speakerId.Equals(actorLeader, StringComparison.OrdinalIgnoreCase) ? actorKingdomId : targetKingdomId;
            receipt["representedRulerHeroStringId"] = speakerId;
            return "kingdom_ruler";
        }

        private static string BindDialogueParticipantAuthority(
            Dictionary<string, object> record, string mappedType, string speakerId,
            string playerId, List<string> errors)
        {
            string actor = ReadString(record, "actorHeroStringId", "");
            string target = ReadString(record, "targetHeroStringId", "");
            bool speakerParticipates = speakerId.Equals(actor, StringComparison.OrdinalIgnoreCase)
                || speakerId.Equals(target, StringComparison.OrdinalIgnoreCase);
            bool playerParticipates = playerId.Equals(actor, StringComparison.OrdinalIgnoreCase)
                || playerId.Equals(target, StringComparison.OrdinalIgnoreCase);
            if (!speakerParticipates && !playerParticipates)
                errors.Add("A dialogue-created personal, political, or strategic action must involve the agreeing NPC or player; third-party authority was not established.");
            if (mappedType.StartsWith("Strategy", StringComparison.OrdinalIgnoreCase)
                && !speakerId.Equals(actor, StringComparison.OrdinalIgnoreCase))
                errors.Add("Only the NPC who will lead a dialogue-created strategy action may authorize it.");
            return mappedType.StartsWith("Politics", StringComparison.OrdinalIgnoreCase)
                ? "political_participant_consent"
                : mappedType.StartsWith("Strategy", StringComparison.OrdinalIgnoreCase)
                    ? "personal_command_consent" : "personal_or_owner_consent";
        }

        private static List<Dictionary<string, object>> RunDialogueActionAuthorityAssertions()
        {
            return RunDialogueMarriageActionAssertions();
        }
    }
}
