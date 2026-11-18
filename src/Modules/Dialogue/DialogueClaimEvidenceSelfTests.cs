using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static List<Dictionary<string, object>> RunDialogueClaimEvidenceAssertions()
        {
            var rows = new List<Dictionary<string, object>>();
            void Add(string name, bool passed, string detail) => rows.Add(TestDict("name", "claim_evidence_" + name, "passed", passed, "detail", detail));
            var payload = TestDict("playerHeroStringId", "player", "worldDay", 10d, "participantProfiles", new List<Dictionary<string, object>> {
                TestDict("heroStringId", "npc", "name", "Rhea"), TestDict("heroStringId", "player", "name", "Michael"), TestDict("heroStringId", "daughter", "name", "Ira") });
            var original = TestDict("id", "original_player", "speakerId", "player", "text", "I met Rhea before Ira. I left the salon, not the city.", "worldDay", 9d);
            var history = new List<Dictionary<string, object>> { original };
            var readerClaim = new RecalledDialogueClaim { Clause = "I will learn who read it", Canonical = "reader inquiry", ClaimedActor = "npc" };
            var irrelevantWho = TestDict("id", "irrelevant_who", "speakerId", "npc", "text", "As a woman who has chosen to walk into it.");
            const string unknownReadingQuote = "Whether it reached you or was opened on the road, I do not yet know — and I will know, because my correspondence is not another's to read.";
            var unknownReading = TestDict("id", "true_unknown_letter", "speakerId", "npc", "text", unknownReadingQuote);
            foreach (var sourceOrder in new[] { new List<Dictionary<string, object>> { irrelevantWho, unknownReading }, new List<Dictionary<string, object>> { unknownReading, irrelevantWho } })
            {
                var readerSpans = DialogueClaimEvidenceSpans(readerClaim, sourceOrder);
                Add("reader_evidence_ignores_unrelated_who_" + rows.Count, readerSpans.Count == 1
                    && ReadString(ReadDictionary(readerSpans[0], "source"), "id", "") == "true_unknown_letter"
                    && ReadString(readerSpans[0], "text", "") == unknownReadingQuote,
                    "An unrelated who-clause cannot displace the source's complete unknown-reading statement, regardless of source order.");
            }
            Add("wh_only_claim_does_not_select_arbitrary_evidence", DialogueClaimEvidenceSpans(
                new RecalledDialogueClaim { Clause = "Who was it", Canonical = "unbound identity question", ClaimedActor = "npc" },
                new List<Dictionary<string, object>> { irrelevantWho, unknownReading }).Count == 0,
                "A claim without topical terms abstains instead of selecting a sentence on a question word alone.");
            Add("long_claim_question_words_alone_abstain", DialogueClaimEvidenceSpans(
                new RecalledDialogueClaim { Clause = "Who knows what happened", Canonical = "unbound long inquiry", ClaimedActor = "npc" },
                new List<Dictionary<string, object>> { TestDict("id", "question_words_only", "speakerId", "npc", "text", "Who chose what to carry?") }).Count == 0,
                "Multiple shared question words cannot satisfy the long-claim threshold without a topical anchor.");
            var lordPricingSpans = DialogueClaimEvidenceSpans(new RecalledDialogueClaim {
                Clause = "A man who signs away every claim he has signs because he is cornered or because he is greedy, and both kinds remember the hand that stripped them",
                Canonical = "lord pricing concern", ClaimedActor = "npc" }, new List<Dictionary<string, object>> {
                TestDict("id", "lord_sale_warning", "speakerId", "daughter", "text", "First: a lord who sells his land remembers the price he was paid and the hand that paid it."),
                TestDict("id", "name_list", "speakerId", "npc", "text", "I will make that list slowly, in my own hand, and every name on it becomes a debt I answer for at this table.") });
            Add("question_word_guard_preserves_lord_pricing_span", lordPricingSpans.Count > 0
                && ReadString(ReadDictionary(lordPricingSpans[0], "source"), "id", "") == "lord_sale_warning",
                "Rejecting question-word-only anchors leaves original topical overlap counts and ranking intact for the natural sale-warning claim.");
            var unknownLetter = new List<Dictionary<string, object>> { TestDict("id", "letter_uncertainty", "speakerId", "npc",
                "text", "I sealed a summons. Whether it reached Ira or was opened on the road, I do not yet know.", "worldDay", 9d) };
            foreach (string recalled in new[] { "The summons letter traveled a road and did not arrive.",
                "I will find whose hands opened my sealed letter.", "I do not yet know whose hands read my letter." })
            {
                var recall = TestDict("reply", recalled);
                Add("delivery_presupposition_covered_" + rows.Count, ExtractDialogueFactualClaims(recalled, payload, recall, "npc").Count > 0,
                    "Negative arrival and unknown-agent access claims are represented for source checking.");
                Add("unknown_delivery_is_not_proof_of_falsehood_" + rows.Count, FindDialogueIntegrityViolations(recall, payload, unknownLetter, "npc").Count == 0,
                    "An explicit unknown source does not authorize an automatic factual correction.");
            }
            foreach (string allowed in new[] { "I do not yet know whether my letter was opened.", "If the letter was opened, I will investigate.", "I will open the letter tomorrow." })
                Add("uncertain_or_future_delivery_not_completed_" + rows.Count,
                    ExtractDialogueFactualClaims(allowed, payload, TestDict("reply", allowed), "npc").Count == 0,
                    "Uncertainty about occurrence, conditionals and future actions do not become completed delivery claims.");
            string customMemoryRules = "Use the character's private voice. Keep promises with their conditions.";
            string scopedMemoryRules = ComposeSourceScopedMemoryWriteRules(customMemoryRules);
            Add("custom_memory_rules_keep_source_scope", scopedMemoryRules.StartsWith(customMemoryRules, StringComparison.Ordinal)
                && ComposeSourceScopedMemoryWriteRules(scopedMemoryRules) == scopedMemoryRules,
                "Server scope guidance retains custom rules and composes once across repeated prompt paths.");
            string stockMemoryRules = string.Join("\n", StockMemoryWriteRuleLines.Select(line => line.Original));
            string compactMemoryRules = ComposeSourceScopedMemoryWriteRules(stockMemoryRules);
            Add("stock_memory_rules_compact_with_source_scope", compactMemoryRules.Length < stockMemoryRules.Length
                && compactMemoryRules.Contains(SourceScopedMemoryWriteRules, StringComparison.Ordinal)
                && ComposeSourceScopedMemoryWriteRules(compactMemoryRules) == compactMemoryRules,
                "The complete stock template gets source scope without increasing its size or composing twice.");
            Add("stock_memory_rules_preserve_obligation_and_state_limits",
                compactMemoryRules.Contains("Identify owed_by/owed_to, with canonical IDs when known", StringComparison.Ordinal)
                && compactMemoryRules.Contains("all conditions and its exact source quote", StringComparison.Ordinal)
                && compactMemoryRules.Contains("never renew fulfilled, withdrawn or superseded commitments", StringComparison.Ordinal)
                && compactMemoryRules.Contains("stateUpdates: Only", StringComparison.Ordinal),
                "Compaction retains unnamed-ID parties, promise conditions and lifecycle, and the state field restriction.");
            Add("stock_memory_rules_keep_unknown_occurrence_unknown",
                compactMemoryRules.Contains("An uncertain event must stay uncertain, never confirmed with only its actor unknown", StringComparison.Ordinal)
                && compactMemoryRules.Contains("not transcripts", StringComparison.Ordinal),
                "Occurrence uncertainty cannot be replaced by actor uncertainty; episodic records remain concise.");
            Add("stock_memory_rules_normalize_line_endings_only_for_matching",
                ComposeSourceScopedMemoryWriteRules(stockMemoryRules.Replace("\n", "  \r\n") + "\r\n") == compactMemoryRules,
                "CRLF and trailing whitespace do not disable stock matching.");
            string partlyCustomRules = stockMemoryRules.Replace("Only current mood", "Only current emotional mood");
            Add("partly_custom_memory_rules_remain_verbatim",
                ComposeSourceScopedMemoryWriteRules(partlyCustomRules).StartsWith(partlyCustomRules, StringComparison.Ordinal),
                "A partially edited template is not partly rewritten by the stock compaction.");
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
            var current = Program.CloneDictionary(payload); current["playerText"] = "No, I met Rhea before Ira.";
            Add("current_correction_controls", FindDialogueFactualContradictions(prose, current, conflict, "npc").Count == 1, "An explicit current correction is available before storage.");
            var future = new List<Dictionary<string, object>> { TestDict("id", "future", "speakerId", "player", "text", original["text"], "worldDay", 11d) };
            Add("future_order_not_used", FindDialogueFactualContradictions(prose, payload, future, "npc").Count == 0, "Replays never leak a later answer into an earlier turn.");
            var privateHistory = new List<Dictionary<string, object>> { TestDict("id", "private", "speakerId", "player", "text", original["text"], "privateMental", true) };
            Add("private_order_not_used", FindDialogueFactualContradictions(prose, payload, privateHistory, "npc").Count == 0, "Unheard private thoughts cannot prove observer recall.");
            var nativePayload = Program.CloneDictionary(payload);
            ReadDictionaryList(nativePayload, "participantProfiles").First(p => Program.CharacterIdFrom(p) == "daughter")["spouseId"] = "husband";
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
            var relationDraft = TestDict("reply", "I asked whether you left the city, and you answered that you left the salon.");
            var relationPlan = BuildDialogueClaimPlan(relationDraft, payload, linkedTurns, "npc");
            Add("answer_relation_retains_linked_question_and_answer", ReadDictionaryList(relationPlan, "pairs").Any(p => ReadDictionaryList(p, "sourceSpans").Count == 2),
                "A verified linked answer can carry both clauses into comparison.");
            var questionOnly = BuildDialogueClaimPlan(relationDraft, payload, linkedTurns.Take(1).ToList(), "npc");
            Add("question_alone_is_not_complete_answer_relation", ReadDictionaryList(questionOnly, "pairs").Count == 0
                && ReadDictionaryList(questionOnly, "claims").Any(c => ReadString(c, "status", "") == "answer_relation_context_missing"),
                "Question-only evidence stays an explicit relationship coverage gap, not a complete semantic comparison.");
            linkedTurns[1]["acceptedSequence"] = 0L;
            Add("missing_answer_sequence_does_not_attest_order", ReadDictionaryList(BuildDialogueClaimPlan(relationDraft, payload, linkedTurns, "npc"), "pairs").Count == 0,
                "Missing sequence is unknown and never a chronological answer proof.");
            linkedTurns[1]["acceptedSequence"] = 2L;
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

            foreach (string count in new[] { "two", "sixteen", "twenty-one" })
            {
                metadata = TestDict("memoryWrites", new[] { Write("Michael received " + count + " letters.", "I received letters.") });
                Add("spelled_count_withheld_" + count, QuarantineUncoveredDialogueFacts(metadata, payload,
                    new() { TestDict("id", "original_player", "speakerId", "player", "text", "I received letters.") }, "npc").Count == 1,
                    "Written cardinals cannot add an exact total to an unspecified quantity.");
            }
            foreach (var pair in new[] { ("two", "2"), ("2", "two"), ("twenty-one", "21"), ("21", "twenty one") })
            {
                string countSourceText = "I received " + pair.Item2 + " letters.";
                metadata = TestDict("memoryWrites", new[] { Write("Michael received " + pair.Item1 + " letters.", countSourceText) });
                Add("equivalent_count_spelling_kept_" + rows.Count, QuarantineUncoveredDialogueFacts(metadata, payload,
                    new() { TestDict("id", "original_player", "speakerId", "player", "text", countSourceText) }, "npc").Count == 0,
                    "Comparisons normalize small cardinal spellings without rewriting source or prose.");
            }
            metadata = TestDict("memoryWrites", new[] { Write("Michael received 2 letters.", "I received 20 letters.") });
            Add("numeric_token_not_substring", QuarantineUncoveredDialogueFacts(metadata, payload,
                new() { TestDict("id", "original_player", "speakerId", "player", "text", "I received 20 letters.") }, "npc").Count == 1,
                "The digit2 inside20 cannot support an exact count of2.");
            foreach (string idiom in new[] { "No one answered.", "One of them answered.", "One day Michael will return.", "Rhea was in two minds." })
                Add("nonquantity_idiom_not_number_" + rows.Count, !Regex.IsMatch(DialogueSpecificityComparisonText(idiom), @"\d"),
                    "Pronoun one, temporal one day and indecision are not exact object counts.");
            Add("number_name_and_larger_compounds_not_partial", DialogueSpecificityComparisonText("Two Rivers and two hundred letters.")
                == "Two Rivers and two hundred letters.", "Proper names and unsupported larger compounds cannot become partial quantities.");
            var meaningWrite = TestDict("kind", "question", "meaning", "Michael received two letters.", "text", "Michael received letters.",
                "sourceEvidence", TestDict("turnId", "original_player", "speakerId", "player", "quote", "I received letters."));
            metadata = TestDict("continuityWrites", new[] { meaningWrite });
            var meaningPlan = BuildDialogueClaimPlan(metadata, payload,
                new() { TestDict("id", "original_player", "speakerId", "player", "text", "I received letters.") }, "npc");
            Add("canonical_meaning_enters_claim_plan", ReadInt(meaningPlan, "claimCount", 0) == 1
                && CanonicalJson(ReadDictionaryList(meaningPlan, "claims")).Contains("two letters"),
                "A benign text alias cannot hide the canonical continuity meaning.");
            Add("canonical_meaning_extra_count_withheld", QuarantineUncoveredDialogueFacts(metadata, payload,
                new() { TestDict("id", "original_player", "speakerId", "player", "text", "I received letters.") }, "npc").Count == 1,
                "Continuity meaning has the same source sufficiency requirements as episodic text.");
            metadata = TestDict("continuityWrites", new[] { TestDict("kind", "question", "meaning", "Michael received letters.",
                "sourceEvidence", TestDict("turnId", "original_player", "speakerId", "player", "quote", "I received letters.")) });
            Add("supported_continuity_meaning_kept", QuarantineUncoveredDialogueFacts(metadata, payload,
                new() { TestDict("id", "original_player", "speakerId", "player", "text", "I received letters.") }, "npc").Count == 0,
                "Correct continuity meaning survives the newly covered field.");
            foreach (string recall in new[] { "You proposed separate baths.", "You clarified that you meant separate baths.", "Your original proposal was precise." })
                Add("past_speech_act_coverage_" + rows.Count, ExtractDialogueFactualClaims(recall, payload, TestDict("reply", recall), "npc").Count > 0,
                    "Past proposals and clarifications become explicit candidates, not automatic contradiction findings.");

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
                var complete = Program.CompleteConversationStructuredResponse(AgencyTestReply(null), "dialogue", out _);
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
                        var row = Program.CloneDictionary(p); row["status"] = "scored"; row["label"] = "supported"; row["outcome"] = "unresolved";
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
                            var row = Program.CloneDictionary(p); row["status"] = "time_budget"; row["outcome"] = "unresolved";
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
