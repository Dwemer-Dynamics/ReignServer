using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunDialogueAnswerSupportAssertions()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string name, bool pass) => rows.Add(TestDict("name", "answer_support_" + name, "passed", pass));
            var payload = TestDict("playerHeroStringId", "player", "worldDay", 10d, "playerText", "My friends give advance warning.",
                "participantHeroIds", new[] { "npc", "player", "ira" }, "participantProfiles", new[] {
                    TestDict("heroStringId", "npc", "name", "Rhea"), TestDict("heroStringId", "player", "name", "Michael"), TestDict("heroStringId", "ira", "name", "Ira") });
            const string question = "What does the army cost in a year?";
            const string answer = "The army costs twenty thousand denars in a year.";
            var history = new List<Dictionary<string, object>> {
                TestDict("id", "q", "speakerId", "ira", "role", "npc", "text", question, "worldDay", 9d, "audienceIds", new[] { "npc", "player", "ira" }),
                TestDict("id", "a", "speakerId", "player", "role", "player", "text", answer, "worldDay", 9d, "audienceIds", new[] { "npc", "player", "ira" }) };
            var request = TestDict("requestType", "dialogue", "model", "z-ai/glm-5.3-flash", "messages", new[] { TestDict("role", "user", "content", "protected original") });
            const string claim = "Ira asked what the army costs in a year, and you answered with a number.";
            Dictionary<string, object> Response(string relation = "yes", string quotedAnswer = answer, string actor = "player", string sourceId = "a") =>
                TestDict("ok", true, "content", Json.Serialize(TestDict("question", TestDict("found", true, "sourceId", "q", "speakerId", "ira", "quote", question),
                    "answer", TestDict("found", true, "sourceId", sourceId, "speakerId", actor, "quote", quotedAnswer), "actorsMatch", "yes", "responds", relation, "reason", "source support test")));
            Dictionary<string, object> Run(Dictionary<string, object> draft, Func<Dictionary<string, object>, Dictionary<string, object>> responder) =>
                RetryDialogueIntegrityViolation(TestDict("ok", true, "content", Json.Serialize(draft)), request, payload, history, "", "", "dialogue", "npc", "", responder, false);
            string Visible(Dictionary<string, object> result) => ReadFirstString(TryParseJsonObject(ReadString(result, "content", "")), "reply", "body");
            int calls = 0;
            var valid = TestDict("reply", "Good. " + claim + " We can plan.", "memoryWrites", new[] { TestDict("text", "The grain is dry.") });
            var kept = Run(valid, _ => { calls++; return Response(); });
            Check("valid_relation_kept_verbatim", calls == 1 && Visible(kept) == ReadString(valid, "reply", ""));
            Check("independent_memory_kept", ReadDictionaryList(TryParseJsonObject(ReadString(kept, "content", "")), "memoryWrites").Count == 1);
            var removed = Run(valid, _ => Response("no"));
            Check("unsupported_relation_withheld_without_replacement_story", ReadBool(removed, "ok", false)
                && !Visible(removed).Contains(claim, StringComparison.Ordinal) && Visible(removed).Contains("Good.") && Visible(removed).Contains("We can plan."));
            Check("new_gate_runs_with_no_exact_contradiction", FindDialogueIntegrityViolations(valid, payload, history, "npc").Count == 0
                && ReadInt(ReadDictionary(removed, "dialogueAnswerSupport"), "withheldClaims", 0) == 1);
            var finalReceipt = ReadDictionary(removed, "dialogueClaimValidation");
            Check("changed_reply_rechecked_at_final_hash", ReadBool(finalReceipt, "replyHashMatchesEvaluatedText", false));
            foreach (var malformed in new[] { Response("yes", "invented quote"), Response("yes", answer, "ira"), Response("yes", answer, "player", "foreign"),
                TestDict("ok", false, "errorCode", "timeout"), TestDict("ok", true, "content", "{}"), TestDict("ok", true, "content", "not json") })
                Check("unbound_or_unusable_never_supports_" + rows.Count, !Visible(Run(valid, _ => malformed)).Contains(claim, StringComparison.Ordinal));
            var truncated = Response(); truncated["finishReason"] = "length";
            Check("truncation_never_supports", !Visible(Run(valid, _ => truncated)).Contains(claim, StringComparison.Ordinal));
            foreach (string excluded in new[] { "If Ira asked, you answered with a number.", "Ira asked, and you never answered.", "Ira asked; you will answer tomorrow.",
                "Ira asked, and you answered with a number?", "*Ira asked, and you answered with a number.*", "Ira's question will be answered tomorrow.",
                "Ira asked whether you answered with a number." })
            {
                calls = 0; var result = Run(TestDict("reply", excluded), _ => { calls++; return Response("no"); });
                Check("excluded_frame_untouched_" + rows.Count, calls == 0 && Visible(result) == excluded);
            }
            foreach (string indirect in new[] {
                "Ira asked whether you had counted the dragon’s yearly feed bill, and you answered with scouts.",
                "Ira asked if you knew what the army costs, and you replied with scouts.",
                "Ira asked whether you would answer tomorrow, and you answered with scouts.",
                "Ira asked whether you had never answered before, and you answered with scouts.",
                "Ira asked if you'd counted the cost; but Michael replied with scouts."
            })
            {
                calls = 0;
                var result = Run(TestDict("reply", "Good. " + indirect + " We can plan.",
                    "continuityWrites", new[] { TestDict("meaning", indirect), TestDict("meaning", "The grain is dry.") }),
                    _ => { calls++; return Response("no"); });
                Check("indirect_question_independent_false_answer_held_" + rows.Count,
                    calls == 1 && ReadBool(result, "ok", false) && !Visible(result).Contains(indirect, StringComparison.Ordinal)
                    && ReadDictionaryList(TryParseJsonObject(ReadString(result, "content", "")), "continuityWrites").Count == 1);
            }
            foreach (string indirect in new[] {
                "Ira asked whether you had counted the yearly cost, and you answered with twenty thousand denars.",
                "Ira asked if you'd counted the army’s cost, and you've already answered with a number."
            })
            {
                calls = 0; string prose = "Good. " + indirect + " We can plan.";
                var result = Run(TestDict("reply", prose), _ => { calls++; return Response(); });
                Check("indirect_question_supported_answer_preserved_" + rows.Count, calls == 1 && Visible(result) == prose);
            }
            foreach (string embedded in new[] {
                "Ira asked whether you had answered and whether Michael had replied.",
                "Ira asked if you knew, and if you replied with scouts.",
                "Ira asked whether the note said \", and you answered with scouts\".",
                "Ira asked whether the note said “, and you answered with scouts”.",
                "Ira asked whether you knew, and whether Michael had answered.",
                "Ira asked if you knew, and you never answered.",
                "Ira asked whether you knew, and you may have replied with scouts.",
                "Ira asked if you knew, and you will answer tomorrow."
            })
            {
                calls = 0; var result = Run(TestDict("reply", embedded), _ => { calls++; return Response("no"); });
                Check("indirect_embedded_or_nonaffirmative_untouched_" + rows.Count, calls == 0 && Visible(result) == embedded);
            }
            var namedIndirect = TestDict("reply", "Ira asked whether the cost was counted; but michael of calradia replied with a number.");
            var multiwordPayload = CloneDictionary(payload);
            multiwordPayload["participantProfiles"] = new[] { TestDict("heroStringId", "player", "name", "Michael of Calradia") };
            Check("indirect_main_answer_known_multiword_name", AnswerSupportClaims(namedIndirect, multiwordPayload).Count == 1);
            Check("indirect_missing_profiles_does_not_guess_names", AnswerSupportClaims(namedIndirect).Count == 0);
            calls = 0;
            var lie = Run(TestDict("reply", claim, "intent", "deliberate_deception"), _ => { calls++; return Response("no"); });
            Check("explicit_deception_untouched", calls == 0 && Visible(lie) == claim);
            calls = 0;
            var deceptiveMemory = Run(TestDict("reply", "I have no answer.", "intent", "deliberate_deception", "memoryWrites", new[] { TestDict("text", claim) }),
                _ => { calls++; return Response("no"); });
            Check("deception_does_not_exempt_factual_memory", calls == 1
                && ReadDictionaryList(TryParseJsonObject(ReadString(deceptiveMemory, "content", "")), "memoryWrites").Count == 0);
            var onlyBad = Run(TestDict("reply", claim), _ => Response("no"));
            Check("no_useful_remainder_does_not_ship_original", !ReadBool(onlyBad, "ok", true) && ReadString(onlyBad, "content", "missing") == "");
            var dependent = Run(TestDict("reply", "Welcome. " + claim + " That proves my point."), _ => Response("no"));
            Check("dangling_dependents_do_not_ship", !ReadBool(dependent, "ok", true) && ReadString(dependent, "content", "missing") == "");
            calls = 0;
            var crowded = Run(TestDict("reply", claim + " " + claim + " " + claim), _ => { calls++; return Response(); });
            Check("duplicate_claims_share_one_check", calls == 1 && ReadBool(crowded, "ok", false));
            calls = 0;
            var overflow = Run(TestDict("reply", "Ira asked about grain, and you answered with twenty. Ira asked about pay, and you answered with thirty. Ira asked about roads, and you answered with ten."),
                _ => { calls++; return Response(); });
            Check("unique_overflow_not_silently_truncated", calls == 0 && !ReadBool(overflow, "ok", true));
            calls = 0;
            var withWrite = TestDict("reply", "Good. " + claim + " We can plan.", "continuityWrites", new[] {
                TestDict("meaning", claim, "evidenceQuote", "We can plan."), TestDict("meaning", "Ira agreed to meet tomorrow.") });
            var both = Run(withWrite, _ => { calls++; return Response("no"); });
            var bothParsed = TryParseJsonObject(ReadString(both, "content", ""));
            Check("false_continuity_meaning_held_despite_different_quote", calls == 1 && ReadDictionaryList(bothParsed, "continuityWrites").Count == 1
                && ReadFirstString(ReadDictionaryList(bothParsed, "continuityWrites")[0], "meaning") == "Ira agreed to meet tomorrow.");
            var sources = DialogueClaimOriginalSources(payload, history, "npc");
            var candidate = AnswerSupportClaims(valid)[0];
            var auditRequest = BuildAnswerSupportRequest(candidate, request, payload, sources, "npc", 0);
            Check("current_input_in_offered_sources", ReadString(ReadDictionaryList(auditRequest, "messages")[1], "content", "").Contains("My friends give advance warning."));
            Check("original_request_untouched", ReadString(ReadDictionaryList(request, "messages")[0], "content", "") == "protected original");
            Check("answer_support_requests_high_reasoning", EffectiveReasoningEffort(TestDict("reasoningMode", "selective", "reasoningEffort", "medium"), auditRequest, "dialogue_answer_support") == "high");
            Check("explicit_reasoning_off_preserved", EffectiveReasoningEffort(TestDict("reasoningMode", "off"), auditRequest, "dialogue_answer_support") == "none");
            foreach (string alias in new[] { "body", "response", "text" })
            {
                var aliasResult = Run(TestDict(alias, "Welcome. " + claim + " We can plan."), _ => Response("no"));
                var aliasParsed = TryParseJsonObject(ReadString(aliasResult, "content", ""));
                Check("visible_alias_withheld_" + alias, ReadBool(aliasResult, "ok", false) && !ReadString(aliasParsed, alias, "").Contains(claim)
                    && !aliasParsed.ContainsKey("reply"));
            }
            var duplicatedAlias = Run(TestDict("reply", "Welcome. " + claim, "body", claim), _ => Response());
            Check("multiple_aliases_cannot_hide_copy", !ReadBool(duplicatedAlias, "ok", true));
            var actionsFirst = Run(TestDict("reply", "*She smiles.* Welcome. " + claim + " We can plan."), _ => Response("no"));
            Check("raw_offset_preserves_narration", Visible(actionsFirst).StartsWith("*She smiles.* Welcome.") && !Visible(actionsFirst).Contains(claim));
            var inline = Run(TestDict("reply", "Welcome. Ira asked *she taps her cup* what the army costs, and you answered with scouts. We can plan."), _ => Response("no"));
            Check("inline_action_preserved", Visible(inline).Contains("*she taps her cup*") && !Visible(inline).Contains("answered with scouts"));
            foreach (string detected in new[] { "Ira asked \"What does it cost?\", and you answered with scouts.",
                "Ira answers your question about the cost with scouts.", "Ira asked about the cost, and Michael answered that it was not counted." })
                Check("additional_affirmative_form_detected_" + rows.Count, AnswerSupportClaims(TestDict("reply", detected)).Count == 1);
            var hiddenMeaning = Run(TestDict("reply", "We can plan.", "memoryWrites", new[] {
                TestDict("text", "The grain is dry.", "meaning", claim, "evidenceType", "subjective_belief") }), _ => Response("no"));
            Check("secondary_meaning_and_self_label_cannot_bypass", ReadDictionaryList(TryParseJsonObject(ReadString(hiddenMeaning, "content", "")), "memoryWrites").Count == 0);
            var duplicateFields = Run(TestDict("reply", "Welcome. " + claim + " We can plan.", "memoryWrites", new[] { TestDict("text", claim) },
                "continuityWrites", new[] { TestDict("meaning", claim) }), _ => { calls++; return Response(); });
            Check("same_relation_across_three_fields_not_over_capacity", ReadBool(duplicateFields, "ok", false));
            Check("withheld_claim_not_kept_in_receipt_content", !ReadString(removed, "content", "").Contains(claim, StringComparison.Ordinal));
            var wrongName = Run(TestDict("reply", "Welcome. Michael asked about the cost, and Ira answered with a number. We can plan."), _ => Response());
            Check("named_actor_crosscheck_rejects_misbound_yes", !Visible(wrongName).Contains("Michael asked"));
            var wrongYou = Run(TestDict("reply", "Welcome. " + claim + " We can plan.", "addressedHeroStringIds", new[] { "ira" }), _ => Response());
            Check("explicit_you_crosscheck_rejects_misbound_yes", !Visible(wrongYou).Contains(claim));
            var independentBrief = TestDict("facts", new[] { "The yearly cost remains a matter for discussion." }, "activeDomains", new[] { "trade", "politics" }, "appliedEvidenceKeys", new[] { "source_grain" });
            var independentWrites = new[] { TestDict("text", "The grain is dry.", "tags", new[] { "grain", "supply" }) };
            var independent = ApplyAnswerSupportGuard(TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "Welcome. " + claim + " We can plan.",
                "decisionBrief", independentBrief, "memoryWrites", independentWrites))), request, payload, history, "npc", _ => Response("no"));
            var independentParsed = TryParseJsonObject(ReadString(independent, "content", ""));
            Check("independent_metadata_and_nested_tags_preserved", CanonicalJson(independentParsed["decisionBrief"]) == CanonicalJson(independentBrief)
                && CanonicalJson(independentParsed["memoryWrites"]) == CanonicalJson(independentWrites));
            var linked = Run(TestDict("reply", "Welcome. " + claim + " We can plan.", "memoryReferences", new[] {
                TestDict("origin", "npc_reply", "originQuote", claim), TestDict("origin", "npc_reply", "originQuote", "We can plan."),
                TestDict("origin", "player_input", "originQuote", claim) }), _ => Response("no"));
            Check("verse_links_to_removed_npc_words_invalidated_only", ReadDictionaryList(TryParseJsonObject(ReadString(linked, "content", "")), "memoryReferences").Count == 2);
            foreach (string background in new[] {
                "As a child, my mentor asked why I took the road, and I answered for my family. You can judge the road today.",
                "When I was young, my old mentor asked \"Why did you take the road?\", and I answered for my family. You can judge the road today." })
            {
                calls = 0; var personal = Run(TestDict("reply", background), _ => { calls++; return Response("no"); });
                Check("explicit_personal_background_preserved_" + rows.Count, calls == 0 && Visible(personal) == background);
            }
            calls = 0;
            var playerBackground = Run(TestDict("reply", "Welcome. As a child, my mentor Michael asked about the army, and I answered with scouts. We can plan."),
                _ => { calls++; return Response("no"); });
            Check("personal_frame_does_not_exempt_named_player", calls == 1 && !Visible(playerBackground).Contains("mentor Michael asked"));
            var policyMessages = ReadDictionaryList(auditRequest, "messages");
            string policyBefore = CanonicalJson(policyMessages); ApplyPrivateReasoningPolicyToMessages(policyMessages);
            Check("native_reasoning_policy_already_in_frozen_request", policyBefore == CanonicalJson(policyMessages)
                && ReadString(policyMessages[0], "content", "").Contains("PRIVATE REASONING POLICY:"));
            string preDispatchHash = "";
            var mutated = Run(valid, q => { preDispatchHash = PromptHash(CanonicalJson(q)); q["providerDiagnostic"] = "mutation"; return Response(); });
            Check("request_hash_bound_before_provider_mutation", ReadString(ReadDictionaryList(ReadDictionary(mutated, "dialogueAnswerSupport"), "checks")[0], "requestHash", "") == preDispatchHash);
            foreach (string alias in new[] { "reply", "body", "response", "text", "letter" })
            {
                var aliasDraft = TestDict(alias, "Welcome. " + claim + " We can plan.");
                var aliasHeld = Run(aliasDraft, _ => Response("no"));
                var aliasBody = TryParseJsonObject(ReadString(aliasHeld, "content", ""));
                Check("all_delivery_aliases_held_" + alias, ReadBool(aliasHeld, "ok", false)
                    && !ReadString(aliasBody, alias, "").Contains(claim));
                Check("all_delivery_aliases_hash_actual_text_" + alias,
                    ReadString(ReadDictionary(aliasHeld, "dialogueAnswerSupport"), "acceptedReplyHash", "") == PromptHash(ReadString(aliasBody, alias, "")));
            }
            calls = 0;
            var hiddenLetter = Run(TestDict("reply", "Welcome.", "letter", claim), _ => { calls++; return Response(); });
            Check("clean_reply_cannot_hide_bad_letter", !ReadBool(hiddenLetter, "ok", true) && calls == 0);
            var quietHeld = Run(TestDict("participation", "quiet", "reply", "", "continuityWrites", new[] {
                TestDict("meaning", claim), TestDict("meaning", "The grain is dry.") }), _ => Response("no"));
            var quietBody = TryParseJsonObject(ReadString(quietHeld, "content", ""));
            Check("intentional_quiet_keeps_independent_record", ReadBool(quietHeld, "ok", false)
                && NormalizeSocialEventParticipation(quietBody) == "quiet"
                && ReadDictionaryList(quietBody, "continuityWrites").Count == 1
                && ReadBool(EnsureConversationDelivery(quietHeld, allowQuiet: true), "ok", false));
            foreach (string alias in new[] { "reply", "body", "letter" })
            {
                var quietSpoken = Run(TestDict("participation", "quiet", alias, claim), _ => Response("no"));
                Check("bad_spoken_alias_cannot_become_quiet_" + alias, !ReadBool(quietSpoken, "ok", true));
            }
            string sealedBefore = CanonicalJson(auditRequest);
            PrepareCountedRouteProviderRequest(auditRequest, request);
            Check("sealed_source_request_wire_preserved", IsSealedAnswerSupportAudit(auditRequest)
                && CanonicalJson(auditRequest) == sealedBefore && !auditRequest.ContainsKey("politicalAuthors"));
            foreach (var forged in new[] { CloneDictionary(auditRequest), TryParseJsonObject(Json.Serialize(auditRequest)) })
            {
                Check("matching_json_is_not_authority_" + rows.Count, !IsSealedAnswerSupportAudit(forged));
                PrepareCountedRouteProviderRequest(forged, request);
                Check("unsealed_copies_get_route_guidance_" + rows.Count, forged.ContainsKey("politicalAuthors"));
            }
            var ordinaryWriter = CloneDictionary(request);
            PrepareCountedRouteProviderRequest(ordinaryWriter, request);
            Check("ordinary_route_request_still_prepared", ordinaryWriter.ContainsKey("politicalAuthors"));
            bool dispatchSealed = false;
            Run(valid, input => { dispatchSealed = IsSealedAnswerSupportAudit(input); return Response(); });
            Check("dispatch_clone_has_native_reference_seal", dispatchSealed);
            foreach (string channel in new[] { "individual", "event", "correspondence", "bounded" })
            {
                string alias = channel == "correspondence" ? "letter" : "reply";
                foreach (bool supported in new[] { false, true })
                {
                    calls = 0; int earlyCalls = -1;
                    string lateText = "Welcome. " + claim + " We can plan.";
                    var final = FinishDialogueRepairsWithAnswerSupport(() =>
                    {
                        var initial = RetryDialogueIntegrityViolationCore(TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "Welcome."))),
                            request, payload, history, "", "", channel, "npc", "", _ => { calls++; return Response(); }, false);
                        earlyCalls = calls;
                        if (!ReadBool(initial, "ok", false)) return initial;
                        return TestDict("ok", true, "content", Json.Serialize(TestDict(alias, lateText,
                            "continuityWrites", new[] { TestDict("meaning", claim), TestDict("meaning", "The grain is dry.") })));
                    }, request, payload, history, "npc", _ => { calls++; return Response(supported ? "yes" : "no"); });
                    var finalBody = TryParseJsonObject(ReadString(final, "content", ""));
                    string finalText = ReadString(finalBody, alias, "");
                    Check("late_repair_final_gate_" + channel + "_" + supported,
                        earlyCalls == 0 && calls == 1 && ReadBool(final, "ok", false)
                        && (supported ? finalText == lateText : !finalText.Contains(claim))
                        && ReadDictionaryList(finalBody, "continuityWrites").Count == (supported ? 2 : 1));
                }
            }
            var finalFailure = FinishDialogueRepairsWithAnswerSupport(() => TestDict("ok", false, "content", ""), request, payload, history, "npc", _ => Response());
            Check("failed_late_repair_does_not_restore_earlier_reply", !ReadBool(finalFailure, "ok", true) && ReadString(finalFailure, "content", "missing") == "");
            calls = 0;
            var replayed = TestDict("ok", true, "content", Json.Serialize(valid), "conversationAgency", TestDict("replayed", true));
            var sameReplay = FinishDialogueRepairsWithAnswerSupport(() => replayed, request, payload, history, "npc", _ => { calls++; return Response("no"); });
            Check("native_agency_replay_unchanged_without_audit", ReferenceEquals(sameReplay, replayed) && calls == 0);
            var markedFailure = TestDict("ok", true, "content", "", "visibleRepairFailure", true, "diagnosticMarker", "kept");
            Check("marked_failure_metadata_preserved", ReferenceEquals(markedFailure,
                FinishDialogueRepairsWithAnswerSupport(() => markedFailure, request, payload, history, "npc", _ => Response())));
            int postChecks = 0;
            var refusedAfter = FinishDialogueRepairsWithAnswerSupport(() => TestDict("ok", true, "content", Json.Serialize(valid)),
                request, payload, history, "npc", _ => Response("no"), _ => { postChecks++; return false; });
            Check("changed_reply_cannot_bypass_existing_postcheck", postChecks == 1 && !ReadBool(refusedAfter, "ok", true)
                && ReadString(refusedAfter, "content", "missing") == "");
            postChecks = 0;
            FinishDialogueRepairsWithAnswerSupport(() => TestDict("ok", true, "content", Json.Serialize(valid)),
                request, payload, history, "npc", _ => Response(), _ => { postChecks++; return true; });
            Check("unchanged_reply_does_not_repeat_postcheck", postChecks == 0);
            var plans = Run(TestDict("reply", "Welcome. " + claim + " We can plan.", "communicationPlans", new[] {
                TestDict("key", "changed_quote", "evidenceQuote", claim),
                TestDict("key", "changed_content", "evidenceQuote", "We can plan.", "contentEvidence", new[] { TestDict("turnId", "current_npc", "quote", claim) }),
                TestDict("key", "stale_seal", "evidenceQuote", "We can plan.", "semanticCommitmentReceipt", "old_reply_bound_receipt"),
                TestDict("key", "independent", "evidenceQuote", "We can plan.") }), _ => Response("no"));
            var remainingPlans = ReadDictionaryList(TryParseJsonObject(ReadString(plans, "content", "")), "communicationPlans");
            Check("changed_quote_content_and_reply_bound_plan_seals_held", remainingPlans.Count == 1
                && ReadString(remainingPlans[0], "key", "") == "independent");
            const string compound = "What did I propose, and what did you say after I clarified?";
            const string proposal = "You meant separate baths.";
            const string acknowledged = "I accepted that arrangement.";
            var combinedPayload = CloneDictionary(payload); combinedPayload["playerText"] = compound;
            string combinedReply = claim + " Ira asked how much the army eats, and you answered with a number. " + proposal + " " + acknowledged;
            int coverageCalls = 0; calls = 0;
            var combined = ApplyAnswerSupportGuard(TestDict("ok", true, "content", Json.Serialize(TestDict("reply", combinedReply,
                "continuityWrites", new[] { TestDict("kind", "question", "questionState", TestDict("answerState", "answered",
                    "question", TestDict("turnId", "current_player", "speakerId", "player", "quote", compound), "addresseeIds", new[] { "npc" },
                    "answers", new[] { TestDict("turnId", "current_npc", "speakerId", "npc", "quote", proposal),
                        TestDict("turnId", "current_npc", "speakerId", "npc", "quote", acknowledged) })) }))),
                request, combinedPayload, history, "npc", check => {
                    if (check.ContainsKey("questionCoverageSchema")) { coverageCalls++; return TestDict("ok", false); }
                    calls++; return Response();
                });
            Check("prose_audits_keep_priority_with_shared_question_capacity", calls == 2 && coverageCalls == 0 && Visible(combined) == combinedReply
                && ReadDictionaryList(TryParseJsonObject(ReadString(combined, "content", "")), "continuityWrites").Count == 0);
            foreach (string unasked in new[] { "I answered a question no one had asked.", "I got ahead of what was offered and answered a question nobody had asked.",
                "I replied to a question that not anyone had asked." })
            {
                calls = 0; var result = Run(TestDict("reply", unasked), _ => { calls++; return Response("no"); });
                Check("unasked_rhetorical_frame_preserved_" + rows.Count, calls == 0 && Visible(result) == unasked);
            }
            calls = 0;
            const string positiveWithUnasked = "Ira asked what the army costs, and you answered a question no one had asked.";
            var stillChecked = Run(TestDict("reply", "Welcome. " + positiveWithUnasked + " We can plan."), _ => { calls++; return Response("no"); });
            Check("unasked_relative_clause_cannot_hide_positive_asking", calls == 1 && !Visible(stillChecked).Contains(positiveWithUnasked));
            const string historicalActorWithUnasked = "Constalia asked what the army costs, and you answered a question no one had asked.";
            calls = 0;
            var historicalActorChecked = ApplyAnswerSupportGuard(TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "Welcome. " + historicalActorWithUnasked + " We can plan."))),
                request, payload, history, "npc", _ => { calls++; return Response("no"); });
            Check("unasked_exception_cannot_hide_actor_absent_from_current_profiles", calls == 1
                && !Visible(historicalActorChecked).Contains(historicalActorWithUnasked) && Visible(historicalActorChecked).Contains("We can plan."));
            rows.AddRange(RunDialogueQuestionCoverageAssertions());
            return rows;
        }

        private static List<Dictionary<string, object>> RunDialogueQuestionCoverageAssertions()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string name, bool pass) => rows.Add(TestDict("name", "question_coverage_" + name, "passed", pass));
            const string question = "What did I propose, and what did you say after I clarified?";
            const string first = "You meant separate baths.";
            const string second = "I accepted that arrangement.";
            const string reply = first + " I was surprised. " + second;
            var payload = TestDict("playerHeroStringId", "player", "playerText", question);
            var request = TestDict("requestType", "dialogue", "model", "z-ai/glm-5.3-flash");
            Dictionary<string, object> Draft(string state = "answered") => TestDict("reply", reply, "continuityWrites", new[] {
                TestDict("kind", "question", "subject", "The proposal and reply", "questionState", TestDict(
                    "question", TestDict("turnId", "current_player", "speakerId", "player", "quote", question),
                    "addresseeIds", new[] { "npc" }, "answerState", state, "answers", new[] {
                        TestDict("turnId", "current_npc", "speakerId", "npc", "quote", first),
                        TestDict("turnId", "current_npc", "speakerId", "npc", "quote", second) })),
                TestDict("kind", "episode", "meaning", "The grain is dry.") });
            Dictionary<string, object> Reply(Dictionary<string, object> check, string coverage = "all")
            {
                var offered = TryParseJsonObject(ReadString(ReadDictionaryList(check, "messages").Last(), "content", ""));
                return TestDict("ok", true, "finishReason", "stop", "content", Json.Serialize(TestDict("coverage", coverage,
                    "parts", new[] { TestDict("questionQuote", ReadString(offered, "question", ""), "covered", coverage == "all" ? "yes" : "no",
                        "answerIds", coverage == "all" ? ReadDictionaryList(offered, "answers").Select(a => ReadString(a, "answerId", "")).ToArray() : Array.Empty<string>(),
                        "reason", "controlled coverage verdict") })));
            }
            Dictionary<string, object> Run(Dictionary<string, object> draft, Func<Dictionary<string, object>, Dictionary<string, object>> responder, int cap = 2) =>
                ApplyQuestionCoverageGuard(TestDict("ok", true, "content", Json.Serialize(draft)), request, payload, "npc", cap, responder);
            Dictionary<string, object> Body(Dictionary<string, object> result) => TryParseJsonObject(ReadString(result, "content", ""));
            int calls = 0; Dictionary<string, object> captured = null;
            var valid = Run(Draft(), check => { calls++; captured = check; return Reply(check); });
            Check("valid_question_kept_and_reply_unchanged", calls == 1 && ReadString(Body(valid), "reply", "") == reply
                && ReadDictionaryList(Body(valid), "continuityWrites").Count == 2);
            Check("uses_sealed_bounded_selected_model_request", IsSealedAnswerSupportAudit(captured)
                && ReadString(captured, "model", "") == "z-ai/glm-5.3-flash" && ReadInt(captured, "maxTokens", 0) == 16000
                && ReadBool(captured, "singleProviderAttempt", false) && ReadDouble(captured, "temperature", 0d) == .2d);
            var packet = TryParseJsonObject(ReadString(ReadDictionaryList(captured, "messages").Last(), "content", ""));
            Check("only_declared_spans_enter_audit", packet.Keys.ToHashSet().SetEquals(new[] { "question", "questionSpeakerId", "addresseeId", "answers" })
                && !Json.Serialize(packet).Contains("I was surprised."));
            foreach (string coverage in new[] { "partial", "none", "unclear" })
            {
                var result = Run(Draft(), check => Reply(check, coverage));
                Check("incomplete_whole_item_held_" + coverage, ReadDictionaryList(Body(result), "continuityWrites").Count == 1
                    && ReadString(Body(result), "reply", "") == reply);
            }
            foreach (string state in new[] { "partial", "refused", "deferred", "unanswered" })
            {
                calls = 0; var result = Run(Draft(state), _ => { calls++; return TestDict(); });
                Check("nonanswered_state_not_reclassified_" + state, calls == 0 && ReadDictionaryList(Body(result), "continuityWrites").Count == 2);
            }
            calls = 0; var exhausted = Run(Draft(), _ => { calls++; return TestDict(); }, 0);
            Check("shared_capacity_holds_metadata_without_call_or_reply_edit", calls == 0
                && ReadDictionaryList(Body(exhausted), "continuityWrites").Count == 1 && ReadString(Body(exhausted), "reply", "") == reply);
            calls = 0; var negativeCap = Run(Draft(), _ => { calls++; return TestDict(); }, -1);
            Check("negative_capacity_cannot_dispatch", calls == 0 && ReadDictionaryList(Body(negativeCap), "continuityWrites").Count == 1);
            var three = Draft(); var threeOriginals = ReadDictionaryList(three, "continuityWrites");
            three["continuityWrites"] = new[] { threeOriginals[0], CloneDictionary(threeOriginals[0]), CloneDictionary(threeOriginals[0]), threeOriginals[1] };
            calls = 0; var threeResult = Run(three, check => { calls++; return Reply(check); });
            Check("three_bound_candidates_make_only_two_calls", calls == 2 && ReadDictionaryList(Body(threeResult), "continuityWrites").Count == 3
                && ReadInt(ReadDictionary(threeResult, "dialogueQuestionCoverage"), "withheldQuestionItems", 0) == 1);
            var two = Draft(); var twoWrites = ReadDictionaryList(two, "continuityWrites");
            var nextQuestion = CloneDictionary(twoWrites[0]);
            var nextState = ReadDictionary(nextQuestion, "questionState");
            ReadDictionary(nextState, "question")["quote"] = "Did I greet you?";
            nextState["answers"] = new[] { TestDict("turnId", "current_npc", "speakerId", "npc", "quote", "Yes.") };
            two["reply"] = reply + " Yes.";
            two["continuityWrites"] = new[] { twoWrites[0], nextQuestion, twoWrites[1] };
            payload["playerText"] = question + " Did I greet you?";
            calls = 0; var twoResult = Run(two, check => { calls++; return Reply(check); });
            payload["playerText"] = question;
            Check("two_independent_questions_fit_shared_cap", calls == 2 && ReadDictionaryList(Body(twoResult), "continuityWrites").Count == 3);
            foreach (string defect in new[] { "splice", "actor", "empty", "stale", "question", "mixed_ref" })
            {
                var draft = Draft(); var state = ReadDictionary(ReadDictionaryList(draft, "continuityWrites")[0], "questionState");
                if (defect == "splice") ReadDictionaryList(state, "answers")[0]["quote"] = first + " " + second;
                if (defect == "actor") ReadDictionaryList(state, "answers")[0]["speakerId"] = "player";
                if (defect == "empty") state["answers"] = Array.Empty<object>();
                if (defect == "stale") draft["reply"] = first + " I left.";
                if (defect == "question") ReadDictionary(state, "question")["quote"] = "What did I promise?";
                if (defect == "mixed_ref") ReadDictionaryList(state, "answers")[1]["turnId"] = "historical_answer";
                calls = 0; var result = Run(draft, _ => { calls++; return TestDict(); });
                Check("unbound_source_held_without_model_" + defect, calls == 0 && ReadDictionaryList(Body(result), "continuityWrites").Count == 1
                    && ReadString(Body(result), "reply", "") == ReadString(draft, "reply", ""));
            }
            var requestPrefix = Draft();
            const string fullRequest = "Tell me where the army is and why we must leave.";
            ReadDictionary(ReadDictionary(ReadDictionaryList(requestPrefix, "continuityWrites")[0], "questionState"), "question")["quote"] = "Tell me where the army is";
            payload["playerText"] = fullRequest; calls = 0;
            var prefixResult = Run(requestPrefix, _ => { calls++; return TestDict(); });
            payload["playerText"] = question;
            Check("writer_cannot_narrow_one_request_unit_to_supported_prefix", calls == 0 && ReadDictionaryList(Body(prefixResult), "continuityWrites").Count == 1);
            var nativeCase = Draft(); ReadDictionaryList(nativeCase, "continuityWrites")[0]["kind"] = "Question";
            calls = 0; var nativeCaseResult = Run(nativeCase, check => { calls++; return Reply(check, "partial"); });
            Check("kind_case_matches_native_normalization", calls == 1 && ReadDictionaryList(Body(nativeCaseResult), "continuityWrites").Count == 1);
            foreach (string defect in new[] { "omitted_words", "foreign_ref", "extra_field", "truncated", "unavailable" })
            {
                var result = Run(Draft(), check => {
                    if (defect == "unavailable") return TestDict("ok", false);
                    var response = Reply(check); var body = TryParseJsonObject(ReadString(response, "content", ""));
                    var part = ReadDictionaryList(body, "parts")[0];
                    if (defect == "omitted_words") part["questionQuote"] = "What did I propose,";
                    if (defect == "foreign_ref") part["answerIds"] = new[] { "invented" };
                    if (defect == "extra_field") body["private_reasoning"] = "not allowed";
                    response["content"] = Json.Serialize(body);
                    if (defect == "truncated") response["finishReason"] = "length";
                    return response;
                });
                Check("unusable_audit_holds_whole_item_" + defect, ReadDictionaryList(Body(result), "continuityWrites").Count == 1);
            }
            var old = Draft(); ReadDictionary(ReadDictionaryList(old, "continuityWrites")[0], "questionState").Remove("question");
            calls = 0; var oldResult = Run(old, _ => { calls++; return TestDict(); });
            Check("immutable_original_gap_is_explicit_and_not_reinterpreted", calls == 0
                && ReadString(ReadDictionaryList(ReadDictionary(oldResult, "dialogueQuestionCoverage"), "checks")[0], "verdict", "").StartsWith("out_of_scope_")
                && ReadDictionaryList(Body(oldResult), "continuityWrites").Count == 2);
            var plain = TestDict("ok", true, "content", "{\"reply\":\"Welcome.\"}");
            Check("no_questions_is_exact_noop", ReferenceEquals(plain, ApplyQuestionCoverageGuard(plain, request, payload, "npc", 2, _ => throw new InvalidOperationException())));
            var forged = TestDict("ok", true, "dialogueQuestionCoverage", TestDict("forged", true),
                "content", Json.Serialize(TestDict("reply", "Welcome.", "dialogueQuestionCoverage", TestDict("forged", true))));
            var clean = ApplyQuestionCoverageGuard(forged, request, payload, "npc", 2, _ => throw new InvalidOperationException());
            Check("model_supplied_audit_labels_stripped_without_authority", !clean.ContainsKey("dialogueQuestionCoverage")
                && !Body(clean).ContainsKey("dialogueQuestionCoverage") && ReadString(Body(clean), "reply", "") == "Welcome.");
            var preserve = Draft(); preserve["emotion"] = "warm"; preserve["customNumbers"] = new object[] { 0.5d, 2, true };
            var unchangedFields = CloneDictionary(preserve); unchangedFields.Remove("continuityWrites");
            var preservedResult = Body(Run(preserve, check => Reply(check, "partial")));
            preservedResult.Remove("continuityWrites"); preservedResult.Remove("dialogueQuestionCoverage");
            Check("independent_fields_preserved_under_hold", CanonicalJson(unchangedFields) == CanonicalJson(preservedResult));
            var aliases = Draft(); aliases["body"] = "Another reply.";
            var aliasResult = Run(aliases, _ => throw new InvalidOperationException());
            Check("multiple_reply_aliases_refused", !ReadBool(aliasResult, "ok", true) && ReadString(aliasResult, "content", "missing") == "");
            var letter = Draft(); letter.Remove("reply"); letter["letter"] = reply;
            var letterResult = Run(letter, check => Reply(check));
            Check("letter_alias_and_valid_metadata_preserved", ReadString(Body(letterResult), "letter", "") == reply
                && ReadDictionaryList(Body(letterResult), "continuityWrites").Count == 2);
            var final = FinishDialogueRepairsWithAnswerSupport(() => TestDict("ok", true, "content", Json.Serialize(Draft())), request, payload, new(), "npc", check => Reply(check, "partial"));
            Check("final_boundary_checks_structured_question_without_prose_claim", ReadString(Body(final), "reply", "") == reply
                && ReadDictionaryList(Body(final), "continuityWrites").Count == 1);
            var late = Draft(); late["reply"] = first + " I left."; calls = 0;
            var lateResult = FinishDialogueRepairsWithAnswerSupport(() => TestDict("ok", true, "content", Json.Serialize(late)), request, payload, new(), "npc",
                check => { calls++; return Reply(check); });
            Check("late_prose_edit_invalidates_stale_answer_before_dispatch", calls == 0 && ReadString(Body(lateResult), "reply", "") == ReadString(late, "reply", "")
                && ReadDictionaryList(Body(lateResult), "continuityWrites").Count == 1);
            return rows;
        }
    }
}
