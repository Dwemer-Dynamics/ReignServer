using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationEffectAcceptanceSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string name, bool passed, object data = null) => results.Add(TestDict("caseId", "effect_acceptance_" + name,
                "passed", passed, "summary", name, "data", data));
            string campaign = "effect_acceptance_test_" + Guid.NewGuid().ToString("N");
            try
            {
                var context = AgencyTestContext();
                var motive = TestDict("conversationAgency", context);
                var profile = TestDict("name", "Mira", "isFemale", true, "attributes", TestDict("endurance", 2));
                string generation;
                using (var connection = OpenCampaignConnection(campaign))
                {
                    EnsureConversationAgencySchema(connection);
                    generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                }
                Dictionary<string, object> Payload(string turn) => TestDict("campaignId", campaign,
                    "timelineId", "agency_timeline", "worldDay", 20d, "sceneTurnId", turn, "turnId", turn,
                    "playerText", ReadString(context, "playerText", ""), "conversationSessionId", "accepted_effect_fixture");
                Dictionary<string, object> Draft() {
                    var parsed = AgencyTestReply(AgencyTestProposal(context));
                    parsed["reply"] = ReadString(parsed, "reply", "") + " *She takes a sip of wine.*";
                    parsed["body"] = parsed["reply"];
                    return parsed;
                }
                int providerCalls = 0;
                var payload = Payload("atomic_effect_turn");
                var llm = EnforceConversationAgencyResponse(TestDict("ok", true, "content", Json.Serialize(Draft())), TestDict(), motive,
                    payload, campaign, "atomic_effect_turn", "dialogue", "npc", request => { providerCalls++; throw new InvalidOperationException("No provider expected."); }, persist: false);
                var prepared = TryParseJsonObject(ReadString(llm, "content", ""));
                string reply = ApplyLegacyConversationDrinking(campaign, "npc", payload, profile, null, prepared, ReadString(prepared, "reply", ""), persist: false);
                int Negotiations() { using var c = OpenCampaignConnection(campaign); return QuerySql(c, "SELECT * FROM conversation_negotiation_turns;").Count; }
                int Drinks() { using var c = OpenCampaignConnection(campaign); return QuerySql(c, "SELECT * FROM conversation_drinking_turns;").Count; }
                double Amount() { using var c = OpenCampaignConnection(campaign); return ReadDouble(TryParseJsonObject(ReadString(QuerySql(c,
                    "SELECT state_json FROM conversation_intoxication WHERE hero_id='npc';").SingleOrDefault(), "state_json", "{}")), "drinks", 0); }
                Check("prepare_has_no_negotiation_or_drink_effect", Negotiations() == 0 && Drinks() == 0 && Amount() == 0
                    && ReadString(ReadDictionary(payload, "conversationAgencyReceipt"), "commitState", "") == "prepared");
                var quietPayload = CloneDictionary(payload);
                var quietDraft = CloneDictionary(prepared);
                quietDraft["beliefWrites"] = new List<Dictionary<string, object>> { TestDict("content", "The player asked about travel.") };
                var quiet = CommitFinalConversationEffects(campaign, "npc", quietPayload, motive, profile, null, quietDraft, "", generation);
                Check("quiet_discards_unspoken_effects_preserves_observations", ReadBool(quiet, "ok", false)
                    && ReadString(quiet, "status", "") == "unspoken_effects_discarded"
                    && !quietPayload.ContainsKey("intoxicationReceipt")
                    && ReadDictionaryList(ReadDictionary(quietPayload, "conversationAgencyReceipt"), "updates").Count == 0
                    && ReadDictionaryList(quietDraft, "beliefWrites").Count == 1 && Negotiations() == 0 && Drinks() == 0);
                foreach (string earlyReturn in new[] { "quiet", "malformed", "isolated", "missing_turn" })
                {
                    var reused = CloneDictionary(payload);
                    var reusedDraft = CloneDictionary(prepared);
                    if (earlyReturn == "isolated") reused["governmentCertificationMemoryIsolation"] = true;
                    if (earlyReturn == "missing_turn") { reused.Remove("turnId"); reused.Remove("sceneTurnId"); reused.Remove("correlationId"); }
                    ApplyLegacyConversationDrinking(campaign, "npc", reused, profile, null,
                        earlyReturn == "malformed" ? null : reusedDraft, earlyReturn == "quiet" ? "" : reply, persist: false);
                    Check("preview_clears_stale_receipt_on_" + earlyReturn, !reused.ContainsKey("intoxicationReceipt") && Drinks() == 0);
                }
                foreach (string boundary in new[] { "after_agency", "before_commit" })
                {
                    bool failed = false;
                    try { CommitFinalConversationEffects(campaign, "npc", payload, motive, profile, null, prepared, reply, generation,
                        phase => { if (phase == boundary) throw new InvalidOperationException("Injected acceptance boundary failure."); }); }
                    catch (InvalidOperationException) { failed = true; }
                    Check("rollback_both_at_" + boundary, failed && Negotiations() == 0 && Drinks() == 0 && Amount() == 0);
                }
                bool changedQuoteRejected = false;
                try { CommitFinalConversationEffects(campaign, "npc", payload, motive, profile, null, prepared,
                    "My household does not matter to me. *She takes a sip of wine.*", generation); }
                catch (InvalidOperationException) { changedQuoteRejected = true; }
                Check("final_prose_invalidates_old_negotiation_quote", changedQuoteRejected && Negotiations() == 0 && Drinks() == 0);
                var replayPayload = CloneDictionary(payload);
                string finalReply = reply + "\n\n[Reign: Action pending native confirmation.]";
                var accepted = CommitFinalConversationEffects(campaign, "npc", payload, motive, profile, null, prepared, finalReply, generation);
                var replay = CommitFinalConversationEffects(campaign, "npc", replayPayload, motive, profile, null, prepared, finalReply, generation);
                Check("final_text_binds_both_receipts_and_retry", ReadBool(accepted, "ok", false) && ReadBool(replay, "ok", false)
                    && Negotiations() == 1 && Drinks() == 1 && Amount() == .25
                    && ReadString(ReadDictionary(accepted, "agency"), "acceptedReplyHash", "") == PromptHash(finalReply)
                    && ReadString(ReadDictionary(accepted, "drinking"), "acceptedReplyHash", "") == PromptHash(finalReply), accepted);
                using (var connection = OpenCampaignConnection(campaign))
                    context["records"] = ReadConversationNegotiations(connection, "agency_timeline", "npc", "player");
                var stalePayload = Payload("stale_drink_decision");
                var staleDraft = Draft();
                string staleReply = ApplyLegacyConversationDrinking(campaign, "npc", stalePayload, profile, null, staleDraft, ReadString(staleDraft, "reply", ""), persist: false);
                var concurrentDraft = DrinkingFixture("She takes a sip of wine.");
                ApplyLegacyConversationDrinking(campaign, "npc", Payload("concurrent_drink"), profile, null, concurrentDraft, ReadString(concurrentDraft, "reply", ""));
                bool staleRejected = false;
                try { CommitFinalConversationEffects(campaign, "npc", stalePayload, motive, profile, null, staleDraft, staleReply, generation); }
                catch (InvalidOperationException) { staleRejected = true; }
                Check("concurrent_drink_rolls_back_prepared_negotiation", staleRejected && Negotiations() == 1 && Drinks() == 2 && Amount() == .5);
                var removedPayload = Payload("drink_removed_from_final");
                var removedDraft = Draft();
                ApplyLegacyConversationDrinking(campaign, "npc", removedPayload, profile, null, removedDraft, ReadString(removedDraft, "reply", ""), persist: false);
                var removed = CommitFinalConversationEffects(campaign, "npc", removedPayload, motive, profile, null, removedDraft,
                    "My household matters to me.", generation);
                Check("removed_narrated_consumption_is_recalculated", ReadBool(removed, "ok", false) && Amount() == .5
                    && ReadDouble(ReadDictionary(removed, "drinking"), "consumed", -1) == 0, removed);

                var preparedAuthority = TestDict("ok", true, "commitState", "prepared", "receiptId", "fixture_uncommitted", "updates",
                    new List<Dictionary<string, object>> { TestDict("topic", "travel", "disposition", "accepted", "id", "prepared_agreement",
                        "revision", 1, "terms", TestDict("gold", 0d, "durationDays", 1d)) });
                var authorityPayload = Payload("uncommitted_authority");
                authorityPayload["playerHeroStringId"] = "player";
                authorityPayload["heroStringId"] = "npc";
                authorityPayload["conversationAgencyReceipt"] = preparedAuthority;
                Check("prepared_authority_rejected_outside_staging", !BindConversationAgencyAuthority(authorityPayload,
                    "accept_temporary_party_guest", TestDict("durationDays", 1d), TestDict(), new List<string>()));
                bool plannerAllowed = false; string unpublishedId = "";
                var unpublished = RunPlayerActionTurn(authorityPayload, () => {
                    plannerAllowed = BindConversationAgencyAuthority(authorityPayload, "accept_temporary_party_guest",
                        TestDict("durationDays", 1d), TestDict(), new List<string>());
                    var row = QueueAction(campaign, TestDict("campaignId", campaign, "source", "dialogue_auto_commit",
                        "command", "accept_temporary_party_guest", "actorHeroStringId", "npc", "targetHeroStringId", "player",
                        "termsHash", "uncommitted_terms", "termsJson", "{}"));
                    unpublishedId = ReadString(row, "id", "");
                    return TestDict("ok", true, "reply", "I agree.", "queuedDialogueActions", new List<Dictionary<string, object>> { row });
                });
                Check("prepared_authority_plans_but_cannot_publish", plannerAllowed && !ReadBool(unpublished, "ok", true)
                    && ReadDurableConversationAction(campaign, unpublishedId) == null, unpublished);
                var consentContext = AgencyTestContext();
                consentContext["ownerId"] = "consenting_npc";
                consentContext["playerText"] = "Will you travel with me for one day?";
                var consent = AgencyTestProposal(consentContext, "accepted", "");
                ReadDictionary(consent, "terms")["durationDays"] = 1d;
                consent["replyQuote"] = "I will travel with you for one day.";
                var consentDraft = AgencyTestReply(consent);
                consentDraft["reply"] = consentDraft["body"] = consent["replyQuote"];
                consentDraft["actionGate"] = TestDict("needed", true, "commitment", "accepted", "intent", "accept_temporary_party_guest", "confidence", 1d);
                var consentPayload = Payload("valid_consent");
                var consentAccepted = CommitFinalConversationEffects(campaign, "consenting_npc", consentPayload,
                    TestDict("conversationAgency", consentContext), profile, null, consentDraft, ReadString(consentDraft, "reply", ""), generation);
                Check("bounded_valid_consent_survives_final_acceptance", ReadBool(consentAccepted, "ok", false)
                    && ReadString(ReadDictionaryList(ReadDictionary(consentAccepted, "agency"), "updates").Single(), "disposition", "") == "accepted");
                int beforeStructuredNegotiations = Negotiations(), beforeStructuredDrinks = Drinks();
                var structuredPayload = Payload("structured_ordinary_reply");
                structuredPayload["structuredActionProtocolVersion"] = Reign.Core.Contracts.Actions.ActionNegotiation.ProtocolVersion;
                var structuredDraft = TestDict("reply", "Tell me what you need.");
                var structuredAccepted = CommitFinalConversationEffects(campaign, "structured_npc", structuredPayload,
                    motive, profile, null, structuredDraft, ReadString(structuredDraft, "reply", ""), generation);
                Check("structured_reply_without_legacy_metadata_is_accepted", ReadBool(structuredAccepted, "ok", false)
                    && !structuredDraft.ContainsKey("proposalDecisions") && Negotiations() == beforeStructuredNegotiations
                    && Drinks() == beforeStructuredDrinks, structuredAccepted);
                bool legacyMissingMetadataRejected = false;
                try { CommitFinalConversationEffects(campaign, "structured_npc", Payload("legacy_missing_metadata"),
                    motive, profile, null, structuredDraft, ReadString(structuredDraft, "reply", ""), generation); }
                catch (InvalidOperationException) { legacyMissingMetadataRejected = true; }
                Check("legacy_metadata_remains_required", legacyMissingMetadataRejected
                    && Negotiations() == beforeStructuredNegotiations && Drinks() == beforeStructuredDrinks);
                var structuredDrinkPayload = Payload("structured_drink");
                structuredDrinkPayload["structuredActionProtocolVersion"] = Reign.Core.Contracts.Actions.ActionNegotiation.ProtocolVersion;
                var structuredDrinkDraft = DrinkingFixture("She takes a sip of wine.");
                structuredDrinkDraft.Remove("proposalDecisions");
                string structuredDrinkReply = ApplyLegacyConversationDrinking(campaign, "structured_npc", structuredDrinkPayload,
                    profile, null, structuredDrinkDraft, ReadString(structuredDrinkDraft, "reply", ""), persist: false);
                var structuredDrinkReplayPayload = CloneDictionary(structuredDrinkPayload);
                var structuredDrinkAccepted = CommitFinalConversationEffects(campaign, "structured_npc", structuredDrinkPayload,
                    motive, profile, null, structuredDrinkDraft, structuredDrinkReply, generation);
                var structuredDrinkReplay = CommitFinalConversationEffects(campaign, "structured_npc", structuredDrinkReplayPayload,
                    motive, profile, null, structuredDrinkDraft, structuredDrinkReply, generation);
                Check("structured_turn_retains_atomic_drinking_and_replay", ReadBool(structuredDrinkAccepted, "ok", false)
                    && ReadBool(structuredDrinkReplay, "ok", false) && Negotiations() == beforeStructuredNegotiations
                    && Drinks() == beforeStructuredDrinks + 1
                    && ReadString(ReadDictionary(structuredDrinkAccepted, "drinking"), "acceptedReplyHash", "") == PromptHash(structuredDrinkReply), structuredDrinkAccepted);
                var restorePayload = Payload("pre_restore_effects");
                var restoreDraft = DrinkingFixture("She takes a sip of wine.");
                string restoreReply = ApplyLegacyConversationDrinking(campaign, "npc", restorePayload, profile, null, restoreDraft, ReadString(restoreDraft, "reply", ""), persist: false);
                int beforeRestore = Drinks();
                AdvanceMemoryRestoreGeneration(campaign);
                bool replaced = false;
                try { CommitFinalConversationEffects(campaign, "npc", restorePayload, TestDict(), profile, null, restoreDraft, restoreReply, generation); }
                catch (InvalidOperationException) { replaced = true; }
                Check("old_generation_cannot_commit_prepared_effects", replaced && Drinks() == beforeRestore && Amount() == .5);
                Check("final_local_checks_add_no_provider_calls", providerCalls == 0);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
