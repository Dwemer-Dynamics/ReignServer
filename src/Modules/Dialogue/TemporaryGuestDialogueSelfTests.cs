using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunTemporaryGuestDialogueSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> check = (id, passed, data) => results.Add(
                TestDict("caseId", "temporary_guest_dialogue_" + id, "passed", passed, "summary", id, "data", data));
            const string heroId = "fixture_speaker";
            var native = TestDict("schema", "reign-temporary-guest-dialogue-v1", "enabled", true,
                "heroId", heroId, "agreementId", "guest_fixture_agreement", "purpose", "One-day Phycaon outing",
                "termKind", "Fixed", "phase", "Active", "startedDay", 110d, "reviewDueDay", 111d,
                "inMainParty", true, "observedWorldDay", 110.1d);
            var payload = PromptParityPayload("in_person", false);
            payload["heroStringId"] = heroId;
            payload["worldDay"] = 110.1d;
            payload["temporaryPartyGuest"] = native;
            payload["playerText"] = "*I stand to say farewell.* I appreciate the time, I am sure I will be back around this way not too long from now, I expect you to still have the horse too.";
            var uncommitted = TestDict("needed", false, "commitment", "roleplay_only", "intent", "Farewell only");
            var accepted = TestDict("needed", true, "commitment", "accepted", "intent", "end_temporary_party_guest: the speaker chooses to end this outing now.");
            Func<string, Dictionary<string, object>, Dictionary<string, object>> reply = (text, gate) =>
                TestDict("reply", text, "actionGate", gate, "emotion", "neutral", "intent", "farewell",
                    "relationshipSignal", new Dictionary<string, object>(), "relationshipAssessments", new List<Dictionary<string, object>>(),
                    "decisionBrief", new Dictionary<string, object>(), "participation", "speak");
            Func<string, Dictionary<string, object>, bool> flagged = (text, gate) =>
                FindTemporaryGuestDepartureViolations(reply(text, gate), payload, heroId).Count > 0;
            const string captured = "Go, then. Come back through the western gate when you do. *She sits back down at the boards and does not watch him leave.*";
            check("captured_farewell_requires_reconciliation", flagged(captured, uncommitted), null);
            payload["playerText"] = "*She agreed to leave my party, I head off to other things*";
            check("captured_second_turn_requires_reconciliation", flagged("*She stays seated with her ledger.* *She does not watch him go.*", uncommitted), null);
            check("player_narration_alone_does_not_grant_consent", !flagged("We should discuss how long this outing will last.", uncommitted)
                && !ActionGateShouldPlan(uncommitted), null);
            check("ordinary_goodbye_keeps_party", !flagged("Goodbye for now. I will remain with your party.", uncommitted), null);
            check("refusal_is_not_departure", !flagged("I won't leave your party.", TestDict("needed", false, "commitment", "refused")), null);
            check("future_condition_is_not_present_departure", !flagged("If we reach Phycaon, I will return home.", uncommitted), null);
            check("current_choice_requires_action", flagged("I will leave your party.", uncommitted), null);
            check("accepted_choice_can_reach_executor", !flagged("I will leave your party.", accepted) && ActionGateShouldPlan(accepted), null);
            check("accepted_gate_cannot_contradict_refusal", flagged("I won't leave your party.", accepted), null);
            check("accepted_gate_cannot_promote_condition", flagged("If we reach Phycaon, I will return home.", accepted), null);
            check("cannot_claim_removal_before_execution", flagged("I have already left your party.", accepted), null);
            var fallback = BuildAcceptedTemporaryPartyGuestFallbackAction(accepted, payload, PromptParityProfile(),
                ReadString(payload, "playerText", ""), "I will leave your party.", new HashSet<string> { "end_temporary_party_guest" });
            check("accepted_end_command_preserved", TemporaryPartyGuestCandidateToPreserve(accepted, "Farewell.") == "end_temporary_party_guest"
                && ReadString(fallback, "command", "") == "end_temporary_party_guest", fallback);
            check("fallback_binds_actual_speaker", Json.Serialize(fallback).Contains(heroId), fallback);
            check("current_departure_outranks_historical_invitation", TemporaryPartyGuestCandidateToPreserve(accepted,
                "You agreed to join my party earlier; can we part now?") == "end_temporary_party_guest", null);
            check("initial_join_keeps_future_departure_safeguard", TemporaryPartyGuestCandidateToPreserve(
                TestDict("needed", true, "commitment", "accepted", "intent", "I agree to join the player party; a later end_temporary_party_guest may end the arrangement."),
                "Join my party with a five-day review.") == "accept_temporary_party_guest", null);
            check("refused_gate_cannot_preserve_action", TemporaryPartyGuestCandidateToPreserve(uncommitted, "leave my party") == "", null);
            check("native_context_serialization_roundtrip", BuildTemporaryGuestDialoguePromptBlock(TryParseJsonObject(Json.Serialize(payload)), heroId)
                .Contains("guest_fixture_agreement"), null);
            check("other_speaker_has_no_guest_context", BuildTemporaryGuestDialoguePromptBlock(payload, "fixture_other") == "", null);
            check("old_client_without_context_supported", BuildTemporaryGuestDialoguePromptBlock(TestDict("worldDay", 110.1d), heroId) == "", null);
            native["observedWorldDay"] = 109d;
            check("stale_native_context_rejected", BuildTemporaryGuestDialoguePromptBlock(payload, heroId) == "", null);
            native["observedWorldDay"] = double.NaN;
            check("invalid_clock_rejected", BuildTemporaryGuestDialoguePromptBlock(payload, heroId) == "", null);
            native["observedWorldDay"] = 110.1d;
            foreach (string phase in new[] { "Departing", "Returning", "Completed" })
            {
                native["phase"] = phase;
                check("no_duplicate_departure_" + phase, flagged("I will leave your party.", accepted), null);
            }
            native["phase"] = "Active";

            string campaign = "guest_dialogue_test_" + Guid.NewGuid().ToString("N");
            UpsertCharacterProfile(campaign, new Dictionary<string, object>(PromptParityProfile()) { ["heroStringId"] = heroId });
            var empty = new Dictionary<string, object>();
            var lines = new List<Dictionary<string, object>>();
            var dialogueEnvelope = BuildDialoguePromptEnvelope(campaign, heroId, "Fixture Speaker", "Traveler", "Traveler",
                "Farewell.", "Phycaon", PromptParityProfile(), PromptParityCharacteristics(), PromptParityState(), empty, empty,
                lines, lines, lines, lines, PromptParityIdentity(), payload, "", "");
            string dialoguePrompt = Json.Serialize(dialogueEnvelope.Messages);
            check("production_dialogue_envelope_has_native_agreement", dialoguePrompt.Contains("guest_fixture_agreement")
                && dialoguePrompt.Contains("end_temporary_party_guest"), null);
            payload["mode"] = "party_chat";
            var motive = TestDict("sanitizedState", PromptParityState(), "prompt", "", "relationshipNpcToTarget", empty, "relationshipNpcToSpouse", empty);
            var eventEnvelope = BuildEventPromptEnvelope(campaign, "fixture_party", heroId, "Fixture Speaker", "Traveler", "Traveler",
                "Farewell.", "Phycaon", payload, PromptParityProfile(), PromptParityCharacteristics(), PromptParityState(), empty, empty,
                lines, lines, lines, lines, PromptParityIdentity(), "", "", motive);
            check("production_party_envelope_has_native_agreement", Json.Serialize(eventEnvelope.Messages).Contains("guest_fixture_agreement"), null);
            native["heroId"] = "fixture_other";
            var foreignEnvelope = BuildEventPromptEnvelope(campaign, "fixture_party", heroId, "Fixture Speaker", "Traveler", "Traveler",
                "Farewell.", "Phycaon", payload, PromptParityProfile(), PromptParityCharacteristics(), PromptParityState(), empty, empty,
                lines, lines, lines, lines, PromptParityIdentity(), "", "", motive);
            check("production_party_envelope_excludes_other_guest", !Json.Serialize(foreignEnvelope.Messages).Contains("guest_fixture_agreement"), null);
            native["heroId"] = heroId;

            int repairCalls = 0;
            bool repairHasNative = false;
            Func<Dictionary<string, object>, Dictionary<string, object>> acceptedRepair = request =>
            {
                repairCalls++;
                repairHasNative = Json.Serialize(ReadDictionaryList(request, "messages")).Contains("guest_fixture_agreement");
                return TestDict("ok", true, "content", Json.Serialize(reply("I will leave your party. Thank you for the outing.", accepted)));
            };
            var requestStub = TestDict("requestType", "dialogue", "maxTokens", 3000);
            var original = reply(captured, uncommitted);
            var repaired = RetryRoleplayContinuityViolation(TestDict("ok", true, "content", Json.Serialize(original)), requestStub,
                payload, PromptParityIdentity(), lines, campaign, "guest-accepted", "dialogue", heroId, "Fixture Speaker", "Traveler", "", acceptedRepair);
            var repairedParsed = TryParseJsonObject(ReadString(repaired, "content", ""));
            check("real_repair_is_bounded_and_native_informed", repairCalls == 1 && repairHasNative, repaired);
            check("real_repair_opens_departure_gate", ReadBool(repaired, "ok", false)
                && ActionGateShouldPlan(ReadDictionary(repairedParsed, "actionGate")), repaired);
            var clarified = RetryRoleplayContinuityViolation(TestDict("ok", true, "content", Json.Serialize(original)), requestStub,
                payload, PromptParityIdentity(), lines, campaign, "guest-clarified", "party_chat", heroId, "Fixture Speaker", "Traveler", "",
                request => TestDict("ok", true, "content", Json.Serialize(reply("I will remain with your party. We can speak again later.", uncommitted))));
            check("real_repair_may_clarify_without_departure", ReadBool(clarified, "ok", false)
                && !ActionGateShouldPlan(ReadDictionary(TryParseJsonObject(ReadString(clarified, "content", "")), "actionGate")), clarified);
            var unresolved = RetryRoleplayContinuityViolation(TestDict("ok", true, "content", Json.Serialize(original)), requestStub,
                payload, PromptParityIdentity(), lines, campaign, "guest-unresolved", "dialogue", heroId, "Fixture Speaker", "Traveler", "",
                request => TestDict("ok", true, "content", Json.Serialize(original)));
            check("unresolved_mechanical_contradiction_cannot_persist", !ReadBool(unresolved, "ok", true), unresolved);
            // The existing Lab owns this unique fixture campaign and isolated storage lifecycle.
            return results;
        }
    }
}
