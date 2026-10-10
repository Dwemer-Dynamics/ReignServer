using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunDialogueClaimEvidenceAssertions()
        {
            var rows = new List<Dictionary<string, object>>();
            void Add(string name, bool passed, string detail) => rows.Add(TestDict("name", "claim_evidence_" + name, "passed", passed, "detail", detail));
            var payload = TestDict("playerHeroStringId", "player", "worldDay", 10d, "participantProfiles", new List<Dictionary<string, object>> {
                TestDict("heroStringId", "npc", "name", "Rhea"), TestDict("heroStringId", "player", "name", "Michael"), TestDict("heroStringId", "daughter", "name", "Ira") });
            var original = TestDict("id", "original_player", "speakerId", "player", "text", "I met Rhea before Ira. I left the salon, not the city.", "worldDay", 9d);
            var history = new List<Dictionary<string, object>> { original };
            var prose = TestDict("reply", "Michael met Ira before Rhea. The room is warm.");
            var errors = FindDialogueFactualContradictions(prose, payload, history, "npc");
            Add("meeting_order_reversal", errors.Count == 1 && ReadString(errors[0], "sourceId", "") == "original_player", "Chronology is proved by an original self-report with the same actor and both named people.");
            Add("chronology_ledger_preserves_source_speaker", BuildDialogueAgencyLedger("npc", history, errors).Contains("\"speakerHeroStringId\":\"player\""), "The repair ledger attributes original chronology to the player who reported it, rather than the current NPC.");
            Add("meeting_order_correct", FindDialogueFactualContradictions(TestDict("reply", "Michael met Rhea before Ira."), payload, history, "npc").Count == 0, "Correct ordering remains untouched.");
            foreach (string control in new[] { "Perhaps Michael met Ira before Rhea.", "If Michael met Ira before Rhea, he knows her.", "Did Michael meet Ira before Rhea?", "*Michael met Ira before Rhea.*", "Michael never met Ira before Rhea.", "Last year Michael met Ira before Rhea." })
                Add("meeting_control_" + rows.Count, FindDialogueFactualContradictions(TestDict("reply", control), payload, history, "npc").Count == 0, "Hypothesis, question, narration, negation or a different episode is not an interchangeable positive assertion: " + control);
            Add("meeting_different_actor", FindDialogueFactualContradictions(TestDict("reply", "Ira met Michael before Rhea."), payload, history, "npc").Count == 0, "Same people with a different subject are not the same relation.");
            var conflict = new List<Dictionary<string, object>> { original, TestDict("id", "conflicting_self", "speakerId", "player", "text", "I met Ira before Rhea.") };
            Add("unresolved_conflicting_originals", FindDialogueFactualContradictions(prose, payload, conflict, "npc").Count == 0, "Unresolved conflicting first-person reports do not justify a repair.");
            var current = CloneDictionary(payload); current["playerText"] = "No, I met Rhea before Ira.";
            Add("current_correction_controls", FindDialogueFactualContradictions(prose, current, conflict, "npc").Count == 1, "An explicit current correction is available before storage.");
            var future = new List<Dictionary<string, object>> { TestDict("id", "future", "speakerId", "player", "text", original["text"], "worldDay", 11d) };
            Add("future_order_not_used", FindDialogueFactualContradictions(prose, payload, future, "npc").Count == 0, "Replays never leak a later answer into an earlier turn.");
            var privateHistory = new List<Dictionary<string, object>> { TestDict("id", "private", "speakerId", "player", "text", original["text"], "privateMental", true) };
            Add("private_order_not_used", FindDialogueFactualContradictions(prose, payload, privateHistory, "npc").Count == 0, "Unheard private thoughts cannot prove observer recall.");
            var nativePayload = CloneDictionary(payload);
            ReadDictionaryList(nativePayload, "participantProfiles").First(p => CharacterIdFrom(p) == "daughter")["spouseId"] = "husband";
            Add("native_current_spouse", FindDialogueFactualContradictions(TestDict("reply", "Ira is unmarried."), nativePayload, new(), "npc").Count == 1, "A nonempty current spouse ID contradicts an explicit present unmarried assertion.");
            foreach (string control in new[] { "Ira is not married to Michael.", "Ira is not married by choice.", "Ira refused the marriage.", "I arranged her marriage and watched it fail.", "Perhaps Ira is unmarried.", "Ira was unmarried before that." })
                Add("native_control_" + rows.Count, FindDialogueFactualContradictions(TestDict("reply", control), nativePayload, new(), "npc").Count == 0, "Current native state does not erase historical conduct, qualified marriage statements or subjective views: " + control);
            Add("empty_native_spouse_unknown", FindDialogueFactualContradictions(TestDict("reply", "Ira is married to Michael."), payload, new(), "npc").Count == 0, "An empty native field cannot erase an unmodeled household.");

            var correctionHistory = new List<Dictionary<string, object>> {
                TestDict("id", "mistake", "speakerId", "npc", "text", "Michael met Ira before Rhea."),
                TestDict("id", "clarification", "speakerId", "player", "text", "Actually, I met Rhea before Ira."),
                TestDict("id", "ack", "speakerId", "npc", "text", "I was wrong. Thank you for correcting me.") };
            string ledger = BuildDialogueAcknowledgedCorrectionLedger(payload, correctionHistory, "npc");
            Add("acknowledged_correction_pinned", ledger.Contains("clarification") && ledger.Contains("ack") && ledger.Contains("supersede"), "The writer receives exact acknowledged correction content and source hashes without rewriting historical records.");
            var unacknowledged = correctionHistory.Take(2).ToList();
            Add("no_invented_acknowledgment", BuildDialogueAcknowledgedCorrectionLedger(payload, unacknowledged, "npc").Length == 0, "A correction ledger does not invent an acknowledgment.");
            var actualCorrection = new List<Dictionary<string, object>> {
                TestDict("id", "rhagaea_player_correction", "speakerId", "player", "text", "Love, slow down, you are wrong and jumping to the wrong conclusions here. I did not meet her before you. When I left I ran into her and thats the first time we met. She did not refuse you, she married Maxax."),
                TestDict("id", "rhagaea_acknowledgment", "speakerId", "npc", "text", "I was wrong. I heard the word met and I built a timeline that put her before me, and then I built a speech around that timeline as though it were fact, and it was not fact.") };
            Add("audited_correction_wording", BuildDialogueAcknowledgedCorrectionLedger(payload, actualCorrection, "npc").Contains("rhagaea_player_correction"), "The audited player correction and original acknowledgment survive the ledger, including negated chronology and corrected marriage.");

            var topical = new List<Dictionary<string, object>> { TestDict("id", "prior_npc", "speakerId", "npc", "text", "There are two loves tangled in this room. You cannot love her because she looks like me. She is her own person.") };
            var plan = BuildDialogueClaimPlan(TestDict("reply", "I said you cannot love her because she looks like me."), payload, topical, "npc");
            var pairs = ReadDictionaryList(plan, "pairs");
            Add("relevant_clause_survives", pairs.Count == 1 && ReadString(pairs[0], "sourceQuote", "").Contains("You cannot love her because she looks like me")
                && !ReadString(pairs[0], "sourceQuote", "").Contains("two loves tangled"), "Choose the decisive source sentence rather than the shared-word preamble.");
            Add("original_span_is_exact", pairs.Count == 1 && ReadString(topical[0], "text", "").Substring(ReadInt(pairs[0], "sourceStart", -1), ReadInt(pairs[0], "sourceLength", -1)) == ReadString(pairs[0], "sourceQuote", ""), "Coverage pins exact source offsets and content.");
            var narrated = new List<Dictionary<string, object>> { TestDict("id", "stage_source", "speakerId", "player", "text", "*I look away, then smile.*\nI met Rhea before Ira.") };
            plan = BuildDialogueClaimPlan(TestDict("reply", "*She smiles.*\nYou met Rhea before Ira."), payload, narrated, "npc");
            pairs = ReadDictionaryList(plan, "pairs");
            var extracted = ReadDictionaryList(plan, "claims");
            Add("stage_offsets_and_natural_document", pairs.Count == 1 && ReadString(pairs[0], "sourceQuote", "") == "I met Rhea before Ira."
                && ReadString(pairs[0], "premise", "").EndsWith(".") && extracted.All(c => "*She smiles.*\nYou met Rhea before Ira.".Substring(ReadInt(c, "start", -1), ReadInt(c, "length", 0)) == ReadString(c, "claim", "")), "Stage masking retains exact offsets and natural document punctuation without including unrelated narration.");
            var linked = new List<Dictionary<string, object>> { TestDict("id", "answer", "speakerId", "player", "text", "Did you leave the city? No, I left the salon. But I remained in the city.") };
            plan = BuildDialogueClaimPlan(TestDict("reply", "You said you left the salon."), payload, linked, "npc");
            pairs = ReadDictionaryList(plan, "pairs");
            Add("question_answer_qualification_together", pairs.Count == 1 && ReadString(pairs[0], "sourceQuote", "").Contains("Did you leave the city?")
                && ReadString(pairs[0], "sourceQuote", "").Contains("remained in the city"), "A selected answer carries its adjacent question and limiting qualification.");
            var linkedTurns = new List<Dictionary<string, object>> {
                TestDict("id", "question_turn", "speakerId", "npc", "text", "Did you leave the city?", "addressedIds", new List<string> { "player" }, "acceptedSequence", 1L, "conversationId", "scene"),
                TestDict("id", "answer_turn", "speakerId", "player", "text", "No, I left the salon.", "replyToTurnId", "question_turn", "acceptedSequence", 2L, "conversationId", "scene") };
            plan = BuildDialogueClaimPlan(TestDict("memoryWrites", new List<Dictionary<string, object>> {
                TestDict("text", "Michael left the salon.", "sourceEvidence", TestDict("turnId", "answer_turn", "speakerId", "player", "quote", "No, I left the salon.")) }), payload, linkedTurns, "npc");
            pairs = ReadDictionaryList(plan, "pairs");
            Add("explicit_cross_turn_question_link", pairs.Count == 1 && ReadString(pairs[0], "premise", "").Contains("asked:")
                && ReadDictionaryList(pairs[0], "sourceSpans").Count == 2, "An explicit accepted-turn link preserves both question and answer with separate original hashes.");
            linkedTurns[1].Remove("replyToTurnId");
            plan = BuildDialogueClaimPlan(TestDict("reply", "You left the salon."), payload, linkedTurns, "npc");
            Add("adjacency_is_not_question_ownership", ReadDictionaryList(plan, "pairs").All(p => !ReadString(p, "premise", "").Contains("asked:")), "Adjacent speakers alone never invent question ownership or answer resolution.");
            linkedTurns[0]["role"] = "player"; linkedTurns[0]["speakerId"] = "player"; linkedTurns[0]["addressedIds"] = new List<string> { "npc" };
            linkedTurns[0]["exchangeId"] = "accepted_exchange";
            linkedTurns[1]["role"] = "npc"; linkedTurns[1]["speakerId"] = "npc"; linkedTurns[1]["exchangeId"] = "accepted_exchange";
            plan = BuildDialogueClaimPlan(TestDict("reply", "I left the salon."), payload, linkedTurns, "npc");
            Add("accepted_exchange_question_link", ReadDictionaryList(plan, "pairs").Any(p => ReadDictionaryList(p, "sourceSpans").Count == 2), "One original player question and NPC answer in the same accepted exchange can be linked without guessing from proximity.");
            linkedTurns[0]["text"] = "Did you leave the city? Did you send the letter?";
            plan = BuildDialogueClaimPlan(TestDict("reply", "I left the salon."), payload, linkedTurns, "npc");
            Add("multiple_questions_abstain", ReadDictionaryList(plan, "pairs").All(p => ReadDictionaryList(p, "sourceSpans").Count == 1), "A turn link alone does not choose which of several questions a short answer resolves.");
            var noEvidence = BuildDialogueClaimPlan(TestDict("reply", "You met Ira before Rhea."), payload, new(), "npc");
            Add("missing_evidence_explicit", ReadInt(noEvidence, "claimCount", 0) > 0 && ReadInt(noEvidence, "uncomparedClaims", 0) > 0, "An extracted fact without an original remains an explicit gap.");
            var manyFacts = TestDict("reply", string.Join(" ", Enumerable.Range(1, 12).Select(i => "You received " + i + " letters.")));
            var manyHistory = new List<Dictionary<string, object>> { TestDict("id", "letters", "speakerId", "player", "text", string.Join(" ", Enumerable.Range(1, 12).Select(i => "I received " + i + " letters."))) };
            plan = BuildDialogueClaimPlan(manyFacts, payload, manyHistory, "npc");
            Add("capacity_reported", ReadInt(plan, "claimCount", 0) == 12 && ReadDictionaryList(plan, "pairs").Count <= 8
                && ReadInt(plan, "uncomparedClaims", 0) >= 4, "Scoring eight pairs never certifies all twelve facts.");
            var longHistory = new List<Dictionary<string, object>> { TestDict("id", "long", "speakerId", "player", "text", "I met Rhea before Ira " + new string('x', 1800) + ".") };
            plan = BuildDialogueClaimPlan(TestDict("reply", "You met Rhea before Ira."), payload, longHistory, "npc");
            Add("long_complete_evidence_abstains", ReadDictionaryList(plan, "pairs").Count == 0 && ReadString(ReadDictionaryList(plan, "claims")[0], "status", "") == "complete_evidence_over_budget", "No decisive sentence is clipped to fit the model.");

            Dictionary<string, object> Write(string text, string quote, string type = "spoken_statement") => TestDict("text", text, "evidenceType", type,
                "sourceEvidence", TestDict("turnId", "original_player", "speakerId", "player", "quote", quote));
            var writes = TestDict("reply", "The room is warm.", "memoryWrites", new List<Dictionary<string, object>> {
                Write("Michael met Ira before meeting Rhea.", "I met Rhea before Ira."),
                Write("Michael met Rhea before Ira.", "I met Rhea before Ira."),
                Write("Michael left Onira.", "I left the salon, not the city."), Write("Rhea fears she was the last choice.", "I met Rhea before Ira.", "subjective_belief") });
            var held = QuarantineDialogueClaimEffects(writes, payload, history, "npc");
            var incomplete = QuarantineUncoveredDialogueFacts(writes, payload, history, "npc");
            Add("bad_sequence_write_withheld", held.Count == 1, "A false lasting chronology is withheld independently from useful prose.");
            Add("invented_city_withheld", incomplete.Count == 1 && ReadString(incomplete[0], "reason", "") == "added_specificity_not_established_by_cited_originals", "Leaving a salon does not establish leaving a named city.");
            Add("independent_write_and_belief_survive", ReadDictionaryList(writes, "memoryWrites").Count == 2 && ReadString(writes, "reply", "") == "The room is warm.", "Correct facts and an explicitly subjective belief survive quarantine.");
            var metadata = TestDict("memoryWrites", new List<Dictionary<string, object>> { Write("Michael revealed he met Ira before Rhea.", "I met Ira.") });
            var shortSource = new List<Dictionary<string, object>> { TestDict("id", "original_player", "speakerId", "player", "text", "I met Ira.") };
            Add("true_quote_not_extra_chronology", QuarantineUncoveredDialogueFacts(metadata, payload, shortSource, "npc").Count == 1, "A true cited quote cannot validate an added before relation.");
            metadata = TestDict("memoryWrites", new List<Dictionary<string, object>> { Write("Michael received 16 letters.", "I received letters.") });
            Add("invented_count_withheld", QuarantineUncoveredDialogueFacts(metadata, payload, new() { TestDict("id", "original_player", "speakerId", "player", "text", "I received letters.") }, "npc").Count == 1, "An unspecified quantity is not an exact total.");
            metadata = TestDict("memoryWrites", new List<Dictionary<string, object>> { Write("Michael received 16 letters.", "I received letters.") });
            Add("other_original_can_supply_count", QuarantineUncoveredDialogueFacts(metadata, payload, new() {
                TestDict("id", "original_player", "speakerId", "player", "text", "I received letters."), TestDict("id", "other_player", "speakerId", "player", "text", "Michael received 16 letters.") }, "npc").Count == 0, "Eligible additional original evidence can establish detail omitted by one quote.");

            var pending = BuildDialogueClaimPlan(TestDict("memoryWrites", new List<Dictionary<string, object>> {
                TestDict("text", "Michael received 16 letters.", "sourceEvidence", TestDict("turnId", "current_npc", "speakerId", "npc", "quote", "I received 16 letters.")) }),
                payload, new() { TestDict("id", "older_similar", "speakerId", "player", "text", "I received 16 letters.") }, "npc");
            Add("pending_citation_does_not_search_other_episode", ReadDictionaryList(pending, "pairs").Count == 0
                && ReadString(ReadDictionaryList(pending, "claims")[0], "status", "") == "explicit_citation_not_yet_bound",
                "An unbound current draft citation cannot be replaced by a thematically similar historical event.");
            var draftWrite = TestDict("reply", "Go, my love. Before I find a reason to lock the door again.",
                "memoryWrites", new List<Dictionary<string, object>> { TestDict("text", "Michael kissed Rhea passionately as a farewell before departing Onira to continue building his revolutionary road.",
                    "sourceEvidence", TestDict("turnId", "current_npc", "speakerId", "npc", "evidenceType", "narrated_action", "quote", "Go, my love. Before I find a reason to lock the door again.")) });
            Add("audited_unrelated_before_quote_withheld", QuarantineUncoveredDialogueFacts(draftWrite, payload, new(), "npc").Count == 1
                && ReadString(draftWrite, "reply", "") == "Go, my love. Before I find a reason to lock the door again.",
                "The latest farewell quote cannot certify a different before relation; dialogue survives while the proposed fact stays incomplete.");
            draftWrite = TestDict("reply", "I received 16 letters.", "memoryWrites", new List<Dictionary<string, object>> {
                TestDict("text", "Rhea received 16 letters.", "sourceEvidence", TestDict("turnId", "current_npc", "speakerId", "npc", "quote", "I received 16 letters.")) });
            Add("provisional_quote_support_is_not_native_proof", QuarantineUncoveredDialogueFacts(draftWrite, payload, new(), "npc").Count == 0
                && FindDialogueFactualContradictions(TestDict("reply", "Michael met Ira before Rhea."), payload, new(), "npc").Count == 0,
                "A relevant provisional self-report remains writable as reported speech, but is not original-history or native execution authority.");
            draftWrite = TestDict("reply", "Rhea sent 16 parcels.", "memoryWrites", new List<Dictionary<string, object>> {
                TestDict("text", "Rhea received 16 letters.", "sourceEvidence", TestDict("turnId", "current_npc", "speakerId", "npc", "quote", "Rhea sent 16 parcels.")) });
            Add("same_number_wrong_event_withheld", QuarantineUncoveredDialogueFacts(draftWrite, payload, new(), "npc").Count == 1,
                "A matching number about parcels does not prove how many letters were received.");

            int writerCalls = 0;
            var priorHelper = DialogueClaimHelperReplay.Value;
            try
            {
                DialogueClaimHelperReplay.Value = _ => throw new TimeoutException("fixture");
                var complete = CompleteConversationStructuredResponse(AgencyTestReply(null), "dialogue", out _);
                complete["reply"] = ReadString(prose, "reply", ""); complete.Remove("body");
                var result = RetryDialogueIntegrityViolation(TestDict("ok", true, "content", Json.Serialize(complete)), TestDict("requestType", "dialogue"), payload, history,
                    "fixture", "fixture", "dialogue", "npc", "", _ => { writerCalls++; return TestDict("ok", false, "content", "{}"); }, false);
                var accepted = TryParseJsonObject(ReadString(result, "content", ""));
                Add("failed_chronology_repair_preserves_prose", ReadBool(result, "ok", false) && writerCalls == 1
                    && ReadString(accepted, "reply", "").Contains("The room is warm") && !ReadString(accepted, "reply", "").Contains("met Ira before"), "One failed writer revision withholds the proved false clause without replacing the conversation.");
                complete["reply"] = "Michael met Rhea before Ira. The room is warm.";
                result = RetryDialogueIntegrityViolation(TestDict("ok", true, "content", Json.Serialize(complete)), TestDict("requestType", "dialogue"), payload, history,
                    "fixture", "fixture", "dialogue", "npc", "", _ => { writerCalls++; return TestDict("ok", false); }, false);
                Add("timeout_no_false_repair", ReadBool(result, "ok", false) && writerCalls == 1, "Timeout does not rewrite correct chronology or create a new call.");
            }
            finally { DialogueClaimHelperReplay.Value = priorHelper; }
            try
            {
                DialogueClaimHelperReplay.Value = request => TestDict("ok", true, "schema", DialogueClaimSchema, "modelHash", DialogueGroundingHash,
                    "tokenizerHash", DialogueGroundingTokenizerHash, "weightsHash", DialogueGroundingWeightsHash, "scoringKind", "grounding_binary", "backend", "cpu",
                    "remoteCalls", 0, "durationMs", 750L, "loadDurationMs", 50L, "coldLoad", false, "workBudgetMs", 8000,
                    "rows", ReadDictionaryList(request, "pairs").Select(p => {
                        var row = CloneDictionary(p); row["status"] = "scored"; row["label"] = "supported"; row["outcome"] = "unresolved";
                        row["scores"] = TestDict("unsupported", .4, "supported", .6); row["inputTokens"] = 30;
                        row["coverage"] = "complete_pair"; row["authority"] = "comparison_hint_only"; return row;
                    }).ToList());
                var receipt = CompareDialogueRecallClaims(TestDict("reply", "*She remembers yesterday.* You met Rhea before Ira."), payload, history, "npc", TestDict());
                Add("coverage_is_not_accuracy", ReadString(receipt, "status", "") == "compared" && ReadInt(receipt, "scoredClaims", 0) > 0
                    && ReadInt(receipt, "supportedClaims", -1) == 0 && ReadInt(receipt, "unresolvedClaims", 0) > 0
                    && ReadDictionaryList(receipt, "excludedNarrationSpans").Count == 1, "Scored uncertain claims and excluded narration cannot become full-response semantic verification.");
                var timing = ReadDictionary(receipt, "workerTiming");
                Add("worker_and_caller_timing_distinct", ReadLong(timing, "loadDurationMs", -1) == 50 && ReadLong(timing, "durationMs", -1) == 750
                    && !ReadBool(timing, "coldLoad", true) && ReadString(receipt, "coverageStage", "") == "proposed_response_before_effect_quarantine",
                    "Receipts preserve worker load/inference context separately from caller latency and later accepted prose.");
                int sentBudget = 0, budgetCalls = 0;
                DialogueClaimHelperReplay.Value = request => {
                    sentBudget = ReadInt(request, "workBudgetMs", 0); budgetCalls++;
                    return TestDict("ok", true, "schema", DialogueClaimSchema, "modelHash", DialogueGroundingHash,
                        "tokenizerHash", DialogueGroundingTokenizerHash, "weightsHash", DialogueGroundingWeightsHash,
                        "scoringKind", "grounding_binary", "backend", "cpu", "remoteCalls", 0,
                        "rows", ReadDictionaryList(request, "pairs").Select(p => {
                            var row = CloneDictionary(p); row["status"] = "time_budget"; row["outcome"] = "unresolved";
                            row["coverage"] = "not_scored"; row["authority"] = "comparison_hint_only"; return row;
                        }).ToList());
                };
                var large = Enumerable.Range(1, 8).Select(i => TestDict("id", "budget_" + i, "speakerId", "player",
                    "text", "I received " + i + " letters " + new string('x', 950) + ".")).ToList();
                var largeDraft = TestDict("memoryWrites", large.Select(s => TestDict("text", "Michael received " + ReadString(s, "id", "").Split('_').Last() + " letters.",
                    "sourceEvidence", TestDict("turnId", s["id"], "speakerId", "player", "quote", s["text"]))).ToList());
                receipt = CompareDialogueRecallClaims(largeDraft, payload, large, "npc", TestDict());
                bool adaptive = sentBudget == 15000 && ReadInt(receipt, "callerBudgetMs", 0) == 17000;
                receipt = CompareDialogueRecallClaims(largeDraft, payload, large, "npc", TestDict("dialogueClaimWorkBudgetMs", 500));
                Add("heavy_allowance_respects_explicit_timeout", adaptive && sentBudget == 500 && budgetCalls == 2
                    && ReadString(receipt, "budgetPolicy", "") == "configured" && ReadString(receipt, "status", "") == "partial",
                    "Long bounded requests get measured deadline headroom; an explicit shorter setting is never silently extended or retried.");
            }
            finally { DialogueClaimHelperReplay.Value = priorHelper; }
            return rows;
        }
    }
}
