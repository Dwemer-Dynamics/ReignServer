using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Anonymized, minimal reproductions of retained September 2026 failures.
        // No provider calls or player campaign data are used by this suite.
        private static List<Dictionary<string, object>> RunDialogueAccuracySelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, object data = null) => results.Add(TestDict(
                "caseId", "dialogue_accuracy_" + id, "passed", passed, "summary", id, "data", data));
            Dictionary<string, object> Reply(string text)
            {
                var row = AgencyTestReply(null);
                row["reply"] = text; row["body"] = text;
                row["actionGate"] = TestDict("needed", false, "commitment", "roleplay_only", "intent", "", "confidence", 1d);
                return CompleteConversationStructuredResponse(row, "dialogue", out _);
            }
            Dictionary<string, object> Assessment(string valence, string quote, string tier = "routine") => TestDict(
                "targetHeroStringId", "player", "valence", valence, "confidence", .9d,
                "severityTier", tier, "summary", "A response to the supplied current conduct.",
                "currentConductQuote", quote, "sourceTurnIds", new[] { "past-reference" });
            Dictionary<string, object> Normalize(Dictionary<string, object> parsed, string text) =>
                NormalizeConversationRelationshipAssessments(parsed,
                    TestDict("playerHeroStringId", "player", "playerText", text, "participants", new[] { "npc", "player" }),
                    "npc", "suspicious", "player", "current-turn").Single();
            string campaign = "dialogue_accuracy_" + Guid.NewGuid().ToString("N");
            try
            {
                var samples = new (string Id, string Player, string Reply, string Tier, int RepairCalls)[]
                {
                    ("ordinary_interest", "The mare seems calmer today.", "She is settling in well.", "routine", 0),
                    ("ordinary_answer", "The road was quiet this morning.", "A welcome change from yesterday.", "routine", 0),
                    ("negated_suspicion", "There is honey in the cup.", "What kind? My curiosity is not suspicious.", "routine", 0),
                    ("shared_company", "I enjoyed the evening with you.", "So did I. The conversation was pleasant.", "routine", 0),
                    ("meaningful_support", "I will stand beside you at the hearing and risk my position to support your claim.", "It helps to have your support.", "meaningful", 1),
                    ("family_pride", "Your daughter showed remarkable skill today.", "She has worked hard for it.", "routine", 0),
                    ("warm_correction", "I meant the pride I could see in her eyes.", "That explains what you meant.", "routine", 0),
                    ("third_party_threat", "Your mother was proud of you.", "Her threat was directed at my spouse. The threat was hers.", "routine", 0)
                };
                foreach (var sample in samples)
                {
                    var context = AgencyTestContext(); context["playerText"] = sample.Player;
                    var original = Reply(sample.Reply); original.Remove("proposalDecisions");
                    original["relationshipAssessments"] = new List<Dictionary<string, object>> { Assessment("positive", sample.Player, sample.Tier) };
                    original["memoryWrites"] = new List<Dictionary<string, object>> { TestDict("text", "A supported recollection.") };
                    // A prior refusal must not force repairs for ordinary conversation.
                    // Explicit support still exercises the metadata-only repair path.
                    context["records"] = new List<Dictionary<string, object>> { TestDict("topic", "travel", "disposition", "personal_refusal") };
                    int calls = 0;
                    var response = EnforceConversationAgencyResponse(TestDict("ok", true, "content", Json.Serialize(original)),
                        TestDict("requestType", "dialogue"), TestDict("conversationAgency", context), TestDict(), campaign,
                        sample.Id, "dialogue", "npc", req => { calls++; return TestDict("ok", true, "content", "{\"proposalDecisions\":[]}"); }, false);
                    var final = TryParseJsonObject(ReadString(response, "content", ""));
                    var normalized = Normalize(final, sample.Player);
                    Check(sample.Id + "_preserves_assessment", calls == sample.RepairCalls
                        && ReadBool(ReadDictionary(response, "conversationAgency"), "accepted", false)
                        && ReadString(normalized, "valence", "") == "positive"
                        && ReadString(normalized, "severityTier", "") == sample.Tier
                        && ReadStringList(normalized, "sourceTurnIds").First() == "current-turn"
                        && ReadDictionaryList(final, "memoryWrites").Count == 1
                        && ReadString(final, "reply", "").StartsWith(sample.Reply.TrimEnd('.'), StringComparison.Ordinal), response);
                    var missing = Reply(sample.Reply); missing.Remove("relationshipAssessments");
                    Check(sample.Id + "_missing_never_infers_sign", ReadString(Normalize(missing, sample.Player), "valence", "") == "neutral");
                    if (sample.Tier == "meaningful")
                    {
                        var awarded = ConversationRelationshipEvaluateApi(TestDict("campaignId", campaign, "exchangeId", "meaningful",
                            "mode", "dialogue", "playerHeroStringId", "player", "participants", new[] { "npc", "player" },
                            "assessments", new List<Dictionary<string, object>> { normalized }));
                        Check("repair_preserves_meaningful_award", ReadInt(ReadDictionaryList(awarded, "receipts").Single(), "finalDelta", 0) >= 2, awarded);
                    }
                }

                var missingAssessment = Reply("I am not suspicious."); missingAssessment.Remove("relationshipAssessments");
                var emptyAssessment = Reply("Her threat was hers.");
                var invalidAssessment = Reply("No judgment was supplied."); invalidAssessment["relationshipAssessments"] = "negative";
                var clearedAssessment = VisibleFailedRepairWithoutEffects(Reply("I accept the offer."));
                Check("absence_states_are_distinct", ReadString(Normalize(missingAssessment, "Hello."), "assessmentIssue", "").EndsWith("_missing")
                    && ReadString(Normalize(emptyAssessment, "Hello."), "assessmentIssue", "").EndsWith("_explicitly_empty")
                    && ReadString(Normalize(invalidAssessment, "Hello."), "assessmentIssue", "").EndsWith("_invalid")
                    && ReadString(Normalize(clearedAssessment, "Hello."), "assessmentIssue", "").EndsWith("_intentionally_cleared"));

                var negative = Reply("That threat is unacceptable.");
                negative["relationshipAssessments"] = new List<Dictionary<string, object>> { Assessment("negative", "I will burn your house.", "hostile") };
                Check("real_current_threat_retains_negative", ReadString(Normalize(negative, "I will burn your house."), "valence", "") == "negative");
                Check("past_quote_does_not_support_current_penalty", ReadString(Normalize(negative, "I was pleased to see you."), "valence", "") == "neutral");
                var pride = Reply("I do not need consolation.");
                pride["relationshipAssessments"] = new List<Dictionary<string, object>> { Assessment("negative", "We all fall sometimes.") };
                Check("supported_pride_reaction_not_forced_positive", ReadString(Normalize(pride, "We all fall sometimes."), "valence", "") == "negative");
                pride["relationshipAssessments"] = new List<Dictionary<string, object>> { Assessment("negative", "We all fall sometimes.", "meaningful") };
                var slightReceipt = ConversationRelationshipEvaluateApi(TestDict("campaignId", campaign, "exchangeId", "pride-slight",
                    "mode", "dialogue", "playerHeroStringId", "player", "participants", new[] { "npc", "player" },
                    "assessments", new[] { Normalize(pride, "We all fall sometimes.") }));
                Check("proud_slight_remains_negative_but_capped", ReadInt(ReadDictionaryList(slightReceipt, "receipts").Single(), "finalDelta", 0) == -1, slightReceipt);
                var threatReceipt = ConversationRelationshipEvaluateApi(TestDict("campaignId", campaign, "exchangeId", "real-threat",
                    "mode", "dialogue", "playerHeroStringId", "player", "participants", new[] { "npc", "player" },
                    "assessments", new[] { Normalize(negative, "I will burn your house.") }));
                Check("independent_direct_threat_retains_stronger_loss", ReadInt(ReadDictionaryList(threatReceipt, "receipts").Single(), "finalDelta", 0) <= -4, threatReceipt);
                ReadDictionaryList(negative, "relationshipAssessments")[0]["confidence"] = .2d;
                Check("uncertain_negative_has_no_score", ReadString(Normalize(negative, "I will burn your house."), "valence", "") == "neutral");

                var casualContext = AgencyTestContext(); casualContext["playerText"] = "The weather is pleasant today.";
                var casual = Reply("It is a fine afternoon."); casual.Remove("proposalDecisions");
                int casualCalls = 0;
                var casualResult = EnforceConversationAgencyResponse(TestDict("ok", true, "content", Json.Serialize(casual)), TestDict(),
                    TestDict("conversationAgency", casualContext), TestDict(), campaign, "casual", "dialogue", "npc",
                    req => { casualCalls++; throw new InvalidOperationException("Ordinary metadata must not request a provider."); }, false);
                Check("ordinary_missing_decisions_complete_without_provider", casualCalls == 0
                    && ReadBool(ReadDictionary(casualResult, "conversationAgency"), "completedOrdinaryMetadata", false)
                    && ReadString(TryParseJsonObject(ReadString(casualResult, "content", "")), "reply", "") == "It is a fine afternoon.");
                var offered = Reply("I need to consider the terms."); offered.Remove("proposalDecisions");
                Check("real_offer_cannot_use_ordinary_completion", !TryCompleteOrdinaryConversationAgency(offered, AgencyTestContext()));
                foreach (object bad in new object[] { null, "accepted", TestDict("topic", "travel"), new[] { "invented" } })
                {
                    var malformed = Reply("I need to consider the terms."); malformed["proposalDecisions"] = bad;
                    Check("invalid_decision_collection_" + (bad == null ? "null" : bad.GetType().Name),
                        !ReadBool(EvaluateConversationAgency(malformed, AgencyTestContext()), "ok", true));
                }

                var evidenceContext = AgencyTestContext();
                var refusal = AgencyTestProposal(evidenceContext);
                refusal["evidenceKeys"] = new[] { "trait.judgment=75" };
                Check("matching_typed_evidence_reference_is_normalized", ReadBool(EvaluateConversationAgency(AgencyTestReply(refusal), evidenceContext), "ok", false));
                refusal["evidenceKeys"] = new[] { "trait.judgment=99" };
                Check("altered_fact_value_is_rejected", !ReadBool(EvaluateConversationAgency(AgencyTestReply(refusal), evidenceContext), "ok", true));
                var originalRefusal = AgencyTestReply(refusal);
                var altered = CloneDictionary(refusal); altered["evidenceKeys"] = new[] { "trait.judgment" };
                altered["disposition"] = "accepted"; altered["terms"] = TestDict("gold", 99999d);
                var patch = TestDict("proposalDecisions", new List<Dictionary<string, object>> { altered },
                    "relationshipAssessments", new List<Dictionary<string, object>> { Assessment("negative", "anything") });
                var merged = ApplyAgencyMetadataPatch(originalRefusal, patch);
                Check("metadata_patch_cannot_upgrade_consent_or_terms", merged != null
                    && ReadString(ReadDictionaryList(merged, "proposalDecisions").Single(), "disposition", "") == "terms_refusal"
                    && ReadDouble(ReadDictionary(ReadDictionaryList(merged, "proposalDecisions").Single(), "terms"), "gold", -1) == 0d
                    && ReadDictionaryList(merged, "relationshipAssessments").Count == 0);
                var assessedRefusal = CloneDictionary(originalRefusal);
                assessedRefusal["relationshipAssessments"] = new List<Dictionary<string, object>> {
                    Assessment("positive", ReadString(evidenceContext, "playerText", "")) };
                assessedRefusal["memoryWrites"] = new List<Dictionary<string, object>> {
                    TestDict("text", "An unrelated supported recollection.") };
                var assessedPatch = ApplyAgencyMetadataPatch(assessedRefusal, patch);
                Check("metadata_repair_preserves_unrelated_assessment_and_memory",
                    ReadString(ReadDictionaryList(assessedPatch, "relationshipAssessments").Single(), "valence", "") == "positive"
                    && ReadString(ReadDictionaryList(assessedPatch, "memoryWrites").Single(), "text", "") == "An unrelated supported recollection."
                    && ReadFirstString(assessedPatch, "reply", "body") == ReadFirstString(originalRefusal, "reply", "body"));
                patch["reply"] = "I agree to leave immediately.";
                Check("metadata_patch_rejects_rewritten_prose", ApplyAgencyMetadataPatch(originalRefusal, patch) == null);

                Check("descriptive_camp_is_not_wrong_settlement", !TryGetConversationSettlementContradiction(
                    "Deep Night — Camp on the Open Road\n*She rests by the fire.*", "Hideout", out _));
                Check("wrong_named_heading_still_rejected", TryGetConversationSettlementContradiction(
                    "Deep Night — Marunath\n*She rests by the fire.*", "Hideout", out string heading) && heading == "Marunath");
                Check("wrong_explicit_location_still_rejected", TryGetConversationSettlementContradiction(
                    "We are in Marunath.", "Hideout", out string place) && place == "Marunath");
                Check("named_camp_not_whitelisted", !IsDescriptiveConversationVenue("Camp in Marunath"));
                Check("literal_financial_readiness_is_not_social_debt", !IsConversationAccounting("He said the coin is real and the man pays promptly."));
                Check("social_accounting_still_observable", IsConversationAccounting("You owe me an answer."));
                const string politicalReply = "There is much to consider before we settle this matter.";
                var politicalPosture = TestDict("maximumDefianceTier", 1, "groupRhetoricalLedger",
                    new List<Dictionary<string, object>> { TestDict("opening", "there is much to consider") });
                var political = ClassifyPoliticalConduct(politicalReply, politicalPosture, TestDict());
                Check("political_repetition_is_only_a_warning", ReadStringList(political, "violations").Count == 0
                    && ReadStringList(political, "warnings").Contains("group_rhetorical_convergence"), political);
                Check("actual_political_defiance_remains_blocking", ReadStringList(ClassifyPoliticalConduct(
                    "You fool, I will destroy you.", politicalPosture, TestDict()), "violations").Contains("defiance_tier_exceeded"));
                Check("wording_lecture_is_a_style_warning", IsConversationStyleWarning(TestDict("type", "repeated_wording_lecture")));

                var style = Reply("You owe me an answer about your intentions.");
                var history = new List<Dictionary<string, object>>
                {
                    TestDict("role", "npc", "speaker", "Mira", "speaker_id", "npc", "text", "I count your questions as a debt."),
                    TestDict("role", "npc", "speaker", "Mira", "speaker_id", "npc", "text", "The price of your answers is courtesy.")
                };
                int styleCalls = 0;
                var styleResult = RetryRoleplayContinuityViolation(TestDict("ok", true, "content", Json.Serialize(style)), TestDict(),
                    TestDict("playerText", "I came to listen."), TestDict("knowsIdentity", true), history, campaign, "style",
                    "dialogue", "npc", "Mira", "Player", "", req => { styleCalls++; throw new InvalidOperationException("Style must not call a provider."); });
                Check("style_warning_preserves_reply_without_retry", styleCalls == 0
                    && ReadString(ReadDictionary(styleResult, "roleplayContinuityRepair"), "method", "") == "style_warning_no_provider_rewrite"
                    && ReadString(TryParseJsonObject(ReadString(styleResult, "content", "")), "reply", "") == ReadString(style, "reply", ""), styleResult);
                Check("action_and_identity_failures_are_not_style", !IsConversationStyleWarning(TestDict("type", "player_narration"))
                    && !IsConversationStyleWarning(TestDict("type", "unverified_material_handover"))
                    && !IsConversationStyleWarning(TestDict("type", "current_speaker_claimed_absent")));

                var firstFailure = Reply("A checked reply."); MarkRepairedVisibleResponse(firstFailure, false);
                var laterSuccess = Reply("A later corrected reply."); CarryConversationRepairState(firstFailure, laterSuccess);
                MarkRepairedVisibleResponse(laterSuccess, true);
                Check("later_success_does_not_mask_earlier_failure", ReadString(laterSuccess, "reply", "").EndsWith(".,", StringComparison.Ordinal)
                    && !ReadBool(ReadDictionary(laterSuccess, "repairStatus"), "allChecksCleared", true)
                    && ReadInt(ReadDictionary(laterSuccess, "repairStatus"), "stageCount", 0) == 2);
                var legacyFailure = Reply("A previously rejected reply.,"); MarkRepairedVisibleResponse(legacyFailure, true);
                Check("legacy_marker_keeps_aggregate_failed", !ReadBool(ReadDictionary(legacyFailure, "repairStatus"), "allChecksCleared", true));

                var empty = TestDict("ok", true, "content", "{}", "finishReason", "stop");
                Check("empty_output_and_limit_have_distinct_evidence", StructuredConversationFailureKind(empty) == "empty_object"
                    && StructuredConversationFailureKind(TestDict("content", "{}", "finishReason", "length")) == "empty_object_at_output_limit");
                int formatCalls = 0;
                var formatRequest = TestDict("requestType", "dialogue", "prompt", "Good afternoon.");
                var recovered = RetryMalformedStructuredResponse(CloneDictionary(empty), formatRequest, campaign, "format-success", "dialogue", "npc", "",
                    req => { formatCalls++; return TestDict("ok", true, "content", "{\"reply\":\"A fine afternoon to you as well.\"}", "finishReason", "stop"); });
                Check("empty_output_recovers_once_with_local_metadata", formatCalls == 1 && ReadBool(recovered, "ok", false)
                    && StructuredResponseIsComplete(ReadString(recovered, "content", ""), "dialogue"), recovered);
                formatCalls = 0;
                var failed = RetryMalformedStructuredResponse(CloneDictionary(empty), formatRequest, campaign, "format-failed", "dialogue", "npc", "",
                    req => { formatCalls++; return CloneDictionary(empty); });
                Check("empty_output_stops_after_two_attempts_without_effects", formatCalls == 2 && !ReadBool(failed, "ok", true)
                    && ReadString(failed, "errorCode", "") == "structured_response_incomplete_after_retries"
                    && ReadDictionaryList(ReadDictionary(failed, "formatRetry"), "attempts").Count == 2, failed);

                var neutralRows = new List<Dictionary<string, object>> { Normalize(missingAssessment, "Hello.") };
                var neutralReceipt = ConversationRelationshipEvaluateApi(TestDict("campaignId", campaign, "exchangeId", "neutral",
                    "mode", "dialogue", "playerHeroStringId", "player", "participants", new[] { "npc", "player" }, "assessments", neutralRows));
                Check("missing_evidence_produces_no_persisted_or_native_delta", ReadBool(neutralReceipt, "ok", false)
                    && ReadDictionaryList(neutralReceipt, "receipts").Count == 0
                    && ReadDictionaryList(neutralReceipt, "nativeChanges").Count == 0, neutralReceipt);
            }
            catch (Exception ex) { Check("suite_exception", false, ex.ToString()); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
