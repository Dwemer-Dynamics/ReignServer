using System;
using System.Collections.Generic;
using System.Linq;

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
            if ((command == "transfer_gold" || command == "transfer_item")
                && ReadString(record, "actorHeroStringId", "") == ReadString(payload, "playerHeroStringId", "")
                && ReignBeta.Shared.Dialogue.PlayerActionRules.IsNonActionContext(ReadFirstString(payload, "playerText", "text", "message")))
            { errors.Add("A quoted example or hypothetical does not authorize a player asset transfer."); return; }
            if (BindPlayerTransferAuthority(record, terms, payload, command, errors)) return;
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
            var rows = RunDialogueMarriageActionAssertions();
            rows.AddRange(RunStructuredActionContinuityRepairAssertions());
            return rows;
        }

        private static List<Dictionary<string, object>> RunStructuredActionContinuityRepairAssertions()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string name, bool pass) => rows.Add(TestDict("name",
                "structured_action_continuity_" + name, "passed", pass, "detail", name));
            var agreement = new Reign.Core.Contracts.Actions.ActionAgreement
            {
                Id = "continuity_gift", PlayerId = "player", NpcId = "npc", ProposerId = "player",
                ConversationId = "continuity_conversation", Origin = Reign.Core.Contracts.Actions.ActionOrigin.Conversation,
                State = Reign.Core.Contracts.Actions.AgreementState.Offered
            };
            agreement.Clauses.Add(new Reign.Core.Contracts.Actions.ActionClause
            {
                Id = "gift_gold", ActionId = "give.gold", ActorId = "player", RecipientId = "npc",
                Arguments = new SortedDictionary<string, string> { ["quantity"] = "17" }
            });
            agreement.PlayerAcceptedHash = agreement.TermsHash();
            var payload = TestDict("structuredActionProtocolVersion", 1, "playerHeroStringId", "player",
                "heroStringId", "npc", "worldDay", 10d, "playerText", "Please accept this gift.",
                "structuredActionContext", TestDict("playerId", "player", "npcId", "npc",
                    "conversationId", agreement.ConversationId, "proposal",
                    TryParseJsonObject(Json.Serialize(agreement))));
            ReadDictionary(payload, "structuredActionContext")["termsHash"] = agreement.TermsHash();
            string premature = "I confirm receipt of one prisoner and two Imperial Elite Cataphracts.";
            Check("pending_receipt_claim_blocked", StructuredActionReceiptContradictions(premature, payload).Any());
            foreach (string safe in new[] { "I accept both arrangements. The prisoner will pass into my custody.",
                "I will confirm receipt of the troops when they arrive.", "I cannot confirm receipt of the prisoner yet.",
                "I acknowledge receipt of your letter about the prisoner.", "If I confirm receipt of the prisoner, notify the guards." })
                Check("pending_receipt_safe_" + rows.Count, !StructuredActionReceiptContradictions(safe, payload).Any());
            var delivered = CloneDictionary(payload);
            ReadDictionary(delivered, "structuredActionContext")["nativeReceipt"] = TestDict("State", "Completed",
                "OperationId", agreement.Id, "TermsHash", agreement.TermsHash());
            Check("matching_completed_receipt_allowed", !StructuredActionReceiptContradictions(premature, delivered).Any());
            ReadDictionary(ReadDictionary(delivered, "structuredActionContext"), "nativeReceipt")["TermsHash"] = "different";
            Check("other_terms_receipt_blocked", StructuredActionReceiptContradictions(premature, delivered).Any());
            ReadDictionary(ReadDictionary(delivered, "structuredActionContext"), "nativeReceipt")["TermsHash"] = agreement.TermsHash();
            ReadDictionary(ReadDictionary(delivered, "structuredActionContext"), "nativeReceipt")["State"] = "InTransit";
            Check("in_transit_receipt_blocked", StructuredActionReceiptContradictions(premature, delivered).Any());
            Dictionary<string, object> Decision(string kind) => TestDict("kind", kind,
                "proposalId", agreement.Id, "revision", agreement.Revision, "termsHash", agreement.TermsHash(),
                "reason", "The original choice.", "clauses", new List<Dictionary<string, object>>());
            Dictionary<string, object> Reply(string text, Dictionary<string, object> decision)
            {
                var response = TestDict("reply", text, "emotion", "calm", "intent", "answer",
                    "relationshipSignal", "unchanged", "relationshipAssessments", new List<Dictionary<string, object>>(),
                    "decisionBrief", TestDict(), "actionGate", TestDict("needed", false, "commitment", "none"));
                if (decision != null) response["structuredActionDecision"] = decision;
                return response;
            }
            foreach (string kind in new[] { "none", "accept", "reject", "counter", "propose" })
            {
                var original = Reply("Original answer.", Decision(kind));
                var candidate = Reply("Corrected answer.", Decision(kind));
                Check(kind + "_choice_preserved", StructuredActionContinuityDecisionPreserved(payload, original, candidate));
                candidate.Remove("structuredActionDecision");
                Check(kind + "_missing_decision_rejected", !StructuredActionContinuityDecisionPreserved(payload, original, candidate));
            }
            var accepted = Reply("Original answer.", Decision("accept"));
            var altered = Reply("Corrected answer.", Decision("reject"));
            Check("accept_cannot_become_reject", !StructuredActionContinuityDecisionPreserved(payload, accepted, altered));
            altered["structuredActionDecision"] = Decision("accept");
            ReadDictionary(altered, "structuredActionDecision")["termsHash"] = "changed";
            Check("terms_hash_cannot_change", !StructuredActionContinuityDecisionPreserved(payload, accepted, altered));
            var counter = Reply("A counteroffer.", Decision("counter"));
            ReadDictionary(counter, "structuredActionDecision")["clauses"] = new List<Dictionary<string, object>> {
                TestDict("actionId", "give.gold", "actorId", "npc", "recipientId", "player",
                    "arguments", new List<Dictionary<string, object>> { TestDict("key", "quantity", "value", "5") }) };
            var changedCounter = TryParseJsonObject(Json.Serialize(counter));
            ReadDictionaryList(ReadDictionary(changedCounter, "structuredActionDecision"), "clauses")[0]["recipientId"] = "npc";
            Check("counter_terms_cannot_change", !StructuredActionContinuityDecisionPreserved(payload, counter, changedCounter));
            Check("legacy_repair_is_unchanged", StructuredActionContinuityDecisionPreserved(TestDict(), accepted, Reply("Legacy.", null)));
            foreach (string variant in new[] { "accept", "reject", "missing", "changed", "premature_receipt" })
            {
                var originalDecision = Decision(variant == "reject" ? "reject" : "accept");
                var original = Reply(variant == "premature_receipt" ? premature : "I am not here. I will answer your offer.", originalDecision);
                var candidate = Reply(variant == "reject" ? "I decline the gift." : "I agree to receive the gift.",
                    variant == "missing" ? null : Decision(variant == "changed" || variant == "reject" ? "reject" : "accept"));
                int calls = 0; bool protocol = false; bool bounded = false;
                var repaired = RetryRoleplayContinuityViolation(TestDict("ok", true, "content", Json.Serialize(original)),
                    TestDict("requestType", "dialogue", "maxTokens", 3000), payload, TestDict("knowsIdentity", true),
                    new List<Dictionary<string, object>>(), "structured_action_continuity_tests", "repair-" + variant,
                    "dialogue", "npc", "Speaker", "Player", "", request =>
                    {
                        calls++;
                        var messages = ReadDictionaryList(request, "messages");
                        string contract = ReadString(messages[0], "content", "");
                        protocol = UsesStructuredActions(request) && contract.Contains("structuredActionDecision");
                        bounded = !Json.Serialize(messages).Contains("REGISTERED INTERPERSONAL ACTIONS");
                        return TestDict("ok", true, "content", Json.Serialize(candidate));
                    });
                var final = TryParseJsonObject(ReadString(repaired, "content", ""));
                var captured = CloneDictionary(payload);
                CaptureStructuredActionDecision(captured, final);
                var decision = ReadDictionary(captured, "validatedStructuredActionDecision");
                bool success = variant == "accept" || variant == "reject" || variant == "premature_receipt";
                Check(variant + "_one_bounded_repair", calls == 1 && protocol && bounded);
                Check(variant + "_native_authority", success
                    ? ReadBool(decision, "valid", false) && ReadString(decision, "kind", "") == (variant == "reject" ? "reject" : "accept")
                    : !ReadBool(decision, "valid", true) && ConversationRepairFailed(final));
            }
            return rows;
        }
    }
}
