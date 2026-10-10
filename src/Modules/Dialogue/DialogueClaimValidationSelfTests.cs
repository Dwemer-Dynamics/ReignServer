using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunDialogueClaimValidationAssertions()
        {
            var rows = new List<Dictionary<string, object>>();
            rows.AddRange(RunDialogueClaimEvidenceAssertions());
            void Add(string name, bool passed, string detail) => rows.Add(TestDict("name", "claim_validation_" + name, "passed", passed, "detail", detail));
            var payload = TestDict("playerHeroStringId", "player", "worldDay", 10d, "participantProfiles", new List<Dictionary<string, object>> {
                TestDict("heroStringId", "npc", "name", "Rhea"), TestDict("heroStringId", "player", "name", "Michael") });
            var source = TestDict("id", "original_npc", "speakerId", "npc", "text", "Now stop talking about the future and kiss me again.", "worldDay", 9d);
            var history = new List<Dictionary<string, object>> { source };
            List<Dictionary<string, object>> Errors(string text, Dictionary<string, object> p = null, List<Dictionary<string, object>> h = null) =>
                FindRecalledSpeakerInversions(TestDict("reply", text), p ?? payload, h ?? history, "npc");
            var wrong = "You said stop talking about the future. I said kiss me again. I am glad you stayed.";
            var errors = Errors(wrong);
            var questionHistory = new List<Dictionary<string, object>> {
                TestDict("id", "alwith_question", "role", "npc", "speakerId", "npc", "text", "The question is what I'm willing to pay for it. And I haven't answered that yet.") };
            const string questionInversion = "You asked what I'm willing to pay. You're holding the answer.";
            var questionErrors = Errors(questionInversion, h: questionHistory);
            Add("alwith_own_question_attribution", questionErrors.Count == 1,
                "An NPC's own introduced question cannot be attributed to the player.");
            Add("alwith_question_corrected_without_remote_repair", TryCorrectExactDialogueOwnership(TestDict("reply", questionInversion),
                payload, questionHistory, "npc", questionErrors, out var fixedQuestion)
                && ReadString(fixedQuestion, "reply", "").StartsWith("I asked", StringComparison.Ordinal), "Exact original ownership permits local correction.");
            Add("alwith_correct_own_question", Errors("I asked what I'm willing to pay.", h: questionHistory).Count == 0, "Correct speaker is retained.");
            Add("rhagaea_original_owner", errors.Count == 1 && ReadString(errors[0], "originalRequestOwner", "") == "npc"
                && ReadString(errors[0], "sourceId", "") == "original_npc", "Exclusive original instruction belongs to the NPC.");
            Add("correct_own_instruction", Errors("I said stop talking about the future. I said kiss me again.").Count == 0, "Correct ownership remains unchanged.");
            Add("short_instruction", Errors("You said kiss me again.").Count == 1, "A short instruction still preserves its originating speaker.");
            Add("both_speakers_abstain", Errors(wrong, h: new() { source, TestDict("id", "player_also", "speakerId", "player", "text", "Stop talking about the future.") }).Count == 0,
                "Repeated wording from both speakers cannot establish exclusive ownership.");
            var currentInput = CloneDictionary(payload); currentInput["playerText"] = "Stop talking about the future.";
            Add("current_input_prevents_false_correction", Errors(wrong, currentInput).Count == 0,
                "The current player contribution survives even before storage and prevents false exclusive ownership.");
            currentInput["playerText"] = "Rhea, stop talking about the future.";
            Add("current_vocative_prevents_false_correction", Errors(wrong, currentInput).Count == 0,
                "A current instruction prefixed with its recipient remains relevant original evidence.");
            currentInput["playerText"] = "You said stop talking about the future.";
            Add("current_quotation_abstains", Errors(wrong, currentInput).Count == 0,
                "A current reported phrase requires further interpretation, not automatic ownership correction.");
            var deception = TestDict("reply", wrong, "intent", "deliberate_deception");
            Add("declared_deception_not_corrected", FindRecalledSpeakerInversions(deception, payload, history, "npc").Count == 0,
                "Declared deceptive speech grants no truth or action authority and is excluded from automatic factual correction.");
            Add("perspective_transform", Errors("You said you would rather be someone I can enjoy.", h: new() {
                TestDict("id", "perspective", "speakerId", "player", "text", "I would rather be someone you can enjoy.") }).Count == 0,
                "First/second-person perspective changes preserve identity.");
            foreach (var control in new[] {
                "If you said stop talking about the future, I would listen.", "Perhaps you said stop talking about the future.",
                "I think you said stop talking about the future.", "She said you said stop talking about the future.",
                "You said stop talking about the future?", "I am lying about how you said stop talking about the future.",
                "*You said stop talking about the future.*", "I never said you said stop talking about the future." })
                Add("control_" + rows.Count, Errors(control).Count == 0, "Hypothesis, report, question, subjective impression, explicit deception or narration is not an owned factual claim: " + control);
            Add("unrelated_shared_words", Errors("You said fear makes the plan harder to follow.", h: new() {
                TestDict("id", "fear", "speakerId", "npc", "text", "I fear the plan will be hard to follow.") }).Count == 0,
                "Word overlap alone cannot transfer the NPC's fear to the player.");
            Add("different_episode_abstain", Errors("Last year you said stop talking about the future.").Count == 0, "Unverified time-qualified recalls cannot be resolved by a current-scene phrase match.");
            Add("named_report_not_original", Errors(wrong, h: new() { TestDict("id", "report", "speakerId", "npc", "text", "Michael said stop talking about the future.") }).Count == 0,
                "A report about a named third-party speaker is not the reporting NPC's original instruction.");
            foreach (var sourceControl in new[] { "Do not stop talking about the future.", "I might stop talking about the future.",
                "Michael considered asking you to stop talking about the future.", "I am glad you chose to stop talking about the future.",
                "Stop talking about the future?" })
                Add("source_scope_" + rows.Count, Errors(wrong, h: new() { TestDict("id", "source_control", "speakerId", "npc", "text", sourceControl) }).Count == 0,
                    "A shared phrase in a denial, intention, embedded report, acknowledgement or question does not prove instruction ownership: " + sourceControl);
            var planOriginal = new List<Dictionary<string, object>> { TestDict("id", "plan_ack", "speakerId", "npc", "text", "We are building an independent nation.") };
            Add("unquoted_plan_ack_not_origin", Errors("You said we are building an independent nation.", h: planOriginal).Count == 0,
                "An acknowledgement does not establish who originally explained the plan.");
            Add("literal_complete_quotation", Errors("You said \"We are building an independent nation\".", h: planOriginal).Count == 1,
                "An explicit literal quotation can be compared with complete original spoken words.");
            Add("literal_first_person_is_quoted_speaker", Errors("You said \"I feel very tired\".", h: new() {
                TestDict("id", "npc_tired", "speakerId", "npc", "text", "I feel very tired.") }).Count == 0,
                "The quoted player's I cannot be normalized to the NPC and used for a false correction.");
            Add("literal_player_quote_preserved", Errors("You said \"I feel very tired\".", h: new() {
                TestDict("id", "player_tired", "speakerId", "player", "text", "I feel very tired.") }).Count == 0,
                "Literal first-person quotation preserves its actual original speaker.");
            Add("future_source_excluded", Errors(wrong, h: new() { TestDict("id", "future", "speakerId", "npc", "text", source["text"], "worldDay", 11d) }).Count == 0,
                "Later sources cannot prove a prior claim.");
            var asOf = CloneDictionary(payload); asOf["asOfAcceptedSequence"] = 2L; asOf["asOfTimestamp"] = 50L;
            Add("sequence_source_excluded", Errors(wrong, asOf, new() { TestDict("id", "later", "speakerId", "npc", "text", source["text"], "acceptedSequence", 3L) }).Count == 0, "No later accepted sequence.");
            Add("timestamp_source_excluded", Errors(wrong, asOf, new() { TestDict("id", "later", "speakerId", "npc", "text", source["text"], "ts", 51L) }).Count == 0, "No later timestamp.");
            Add("private_other_actor_excluded", Errors("I said stop talking about the future.", h: new() {
                TestDict("id", "private", "speakerId", "player", "text", source["text"], "privateMental", true) }).Count == 0,
                "Private player thought is not observer-accessible speech.");
            Add("own_thought_is_not_speech", Errors(wrong, h: new() { TestDict("id", "own_thought", "speakerId", "npc", "text", source["text"], "privateMental", true) }).Count == 0,
                "The NPC knowing her own private thought does not prove she spoke that instruction.");
            Add("hypothesis_not_speech", Errors(wrong, h: new() { TestDict("id", "hypothesis", "speakerId", "npc", "text", source["text"], "evidenceType", "hypothesis") }).Count == 0,
                "A hypothesis cannot be used as an original spoken instruction.");
            Add("long_original_not_prefix", Errors(wrong, h: new() {
                TestDict("id", "long", "speakerId", "npc", "text", string.Concat(Enumerable.Repeat("Ordinary scene detail. ", 60)) + source["text"]) }).Count == 1,
                "A decisive clause beyond 450 characters remains available.");
            var many = CloneDictionary(payload);
            many["memoryPrecisionEvidence"] = TestDict("records", new List<Dictionary<string, object>> { TestDict("sourceTurns",
                Enumerable.Range(0, 90).Select(i => TestDict("id", "irrelevant_" + i, "speakerId", "player", "text", "We discussed a different town.")).ToList()) });
            Add("recent_source_protected", Errors(wrong, many).Count == 1, "Large retrieval cannot evict the immediately preceding original.");
            var group = CloneDictionary(payload);
            group["participantProfiles"] = new List<Dictionary<string, object>> { TestDict("heroStringId", "npc", "name", "Rhea"),
                TestDict("heroStringId", "player", "name", "Michael"), TestDict("heroStringId", "other", "name", "Ira") };
            group["participantHeroIds"] = new List<string> { "npc", "player", "other" };
            var addressedSource = CloneDictionary(source); addressedSource["addressedIds"] = new List<string> { "player" };
            Add("ambiguous_group_you", Errors(wrong, group, new() { addressedSource }).Count == 0, "Group you without an explicit audience remains unresolved.");
            var explicitDraft = TestDict("reply", wrong, "addressedHeroStringIds", new List<string> { "player" });
            Add("explicit_group_you", FindRecalledSpeakerInversions(explicitDraft, group, new() { addressedSource }, "npc").Count == 1, "Pinned group audience permits ownership comparison.");
            var currentGroup = CloneDictionary(group); currentGroup["playerText"] = "Stop talking about the future.";
            Add("current_group_input_prevents_false_correction", FindRecalledSpeakerInversions(explicitDraft, currentGroup, new() { addressedSource }, "npc").Count == 0,
                "Unresolved current group audience cannot turn the player's repeated instruction into a false correction.");
            currentGroup["playerText"] = "*I whisper to Ira* Stop talking about the future.";
            Add("current_private_input_projected", FindRecalledSpeakerInversions(explicitDraft, currentGroup, new() { addressedSource }, "npc").Count == 1,
                "An unheard current whisper cannot grant the observing NPC knowledge of private speech.");
            explicitDraft["addressedHeroStringIds"] = new List<string> { "outsider" };
            Add("unknown_group_target", FindRecalledSpeakerInversions(explicitDraft, group, new() { addressedSource }, "npc").Count == 0, "A fabricated addressee grants no identity authority.");
            var parsed = TestDict("reply", wrong, "relationshipAssessments", new List<Dictionary<string, object>> { TestDict("delta", 1, "replyQuote", "I am glad you stayed.") },
                "memoryWrites", new List<Dictionary<string, object>> { TestDict("text", "Good company.", "replyQuote", "I am glad you stayed."),
                    TestDict("text", "The player originated that instruction.", "replyQuote", "You said stop talking about the future.") },
                "proposalDecisions", new List<Dictionary<string, object>> { TestDict("status", "conditional", "replyQuote", "I am glad you stayed.") });
            Add("local_correction", TryCorrectExactDialogueOwnership(parsed, payload, history, "npc", errors, out var corrected)
                && ReadString(corrected, "reply", "").StartsWith("I said stop talking", StringComparison.Ordinal)
                && ReadDictionaryList(corrected, "memoryWrites").Count == 1 && ReadDictionaryList(corrected, "relationshipAssessments").Count == 1
                && ReadDictionaryList(corrected, "proposalDecisions").Count == 1, "Only the proven subject and dependent citation change; unrelated metadata survives.");
            var told = TestDict("reply", "You told me to stop talking about the future. The fire is warm.");
            Add("recipient_change_requires_writer", !TryCorrectExactDialogueOwnership(told, payload, history, "npc", Errors(ReadString(told, "reply", "")), out _),
                "Told/asked me needs recipient correction as well as speaker correction; no subject-only rewrite.");
            var secondary = CloneDictionary(parsed);
            secondary["memoryWrites"] = new List<Dictionary<string, object>> { TestDict("text", "A compound description.", "sourceEvidenceSpans", new List<Dictionary<string, object>> {
                TestDict("quote", "I am glad you stayed."), TestDict("quote", "You said stop talking about the future.") }) };
            Add("secondary_citation_invalidated", TryCorrectExactDialogueOwnership(secondary, payload, history, "npc", errors, out var secondaryCorrected)
                && ReadDictionaryList(secondaryCorrected, "memoryWrites").Count == 0, "Changing any cited contribution invalidates its dependent compound write.");
            var writes = TestDict("reply", "I remember my own instruction.", "memoryWrites", new List<Dictionary<string, object>> {
                TestDict("text", "Michael said stop talking about the future."), TestDict("text", "Rhea said stop talking about the future.") });
            var quarantined = QuarantineDialogueClaimEffects(writes, payload, history, "npc");
            Add("bad_write_quarantined", quarantined.Count == 1 && ReadDictionaryList(writes, "memoryWrites").Count == 1,
                "A false lasting attribution is withheld independently of usable prose and retained in the receipt.");
            var goodWrites = TestDict("memoryWrites", new List<Dictionary<string, object>> { TestDict("text", "Michael said stop talking about the future.", "evidenceType", "subjective_belief") });
            Add("subjective_write_distinct", QuarantineDialogueClaimEffects(goodWrites, payload, history, "npc").Count == 0, "Explicitly subjective belief does not become an objective fact.");

            var priorReplay = DialogueClaimHelperReplay.Value;
            var priorExtractor = DialogueClaimExtractorReplay.Value;
            try
            {
                int calls = 0;
                DialogueClaimHelperReplay.Value = request => {
                    calls++;
                    return TestDict("ok", true, "schema", DialogueClaimSchema, "modelHash", DialogueNliHash, "tokenizerHash", DialogueNliTokenizerHash,
                        "backend", "cpu", "remoteCalls", 0, "rows", ReadDictionaryList(request, "pairs").Select(pair => {
                            var row = CloneDictionary(pair); row["status"] = "scored"; row["label"] = "contradiction"; row["outcome"] = "contradicted";
                            row["scores"] = TestDict("contradiction", .99, "entailment", .005, "neutral", .005); row["inputTokens"] = 40;
                            row["coverage"] = "complete_pair"; row["authority"] = "comparison_hint_only"; return row;
                        }).ToList()); };
                var hints = CompareDialogueRecallClaims(TestDict("reply", "I said stop talking about the future."), payload, history, "npc", TestDict());
                Add("false_positive_is_hint", ReadString(hints, "status", "") == "compared" && Errors("I said stop talking about the future.").Count == 0
                    && !ReadBool(hints, "semanticAccuracyCertified", true), "Even an internally valid high-confidence classifier error cannot correct good prose.");
                Dictionary<string, object> GroundingResponse(Dictionary<string, object> request) => TestDict("ok", true,
                    "schema", DialogueClaimSchema, "modelHash", DialogueGroundingHash, "tokenizerHash", DialogueGroundingTokenizerHash,
                    "weightsHash", DialogueGroundingWeightsHash, "scoringKind", "grounding_binary", "backend", "cpu", "remoteCalls", 0,
                    "rows", ReadDictionaryList(request, "pairs").Select(pair => {
                        var row = CloneDictionary(pair); row["status"] = "scored"; row["label"] = "unsupported"; row["outcome"] = "unsupported";
                        row["scores"] = TestDict("unsupported", .99, "supported", .01); row["inputTokens"] = 40;
                        row["coverage"] = "complete_pair"; row["authority"] = "comparison_hint_only"; return row;
                    }).ToList());
                DialogueClaimHelperReplay.Value = GroundingResponse;
                var repairHints = CompareDialogueRecallClaims(told, payload, history, "npc", TestDict());
                var toldErrors = Errors(ReadString(told, "reply", ""));
                string guidance = BuildDialogueClaimRepairGuidance(repairHints, toldErrors);
                Add("grounding_guides_proven_repair", guidance.Contains("LOCAL SOURCE-BOUND COMPARISON GUIDANCE", StringComparison.Ordinal)
                    && guidance.Contains("identityAliases", StringComparison.Ordinal) && ReadInt(repairHints, "repairGuidanceCount", 0) > 0,
                    "Validated complete comparisons guide the existing writer repair with canonical identities and source pins.");
                var foreignErrors = toldErrors.Select(CloneDictionary).ToList();
                foreignErrors.ForEach(v => v["sourceHash"] = new string('f', 64));
                Add("grounding_guidance_foreign_source_excluded", BuildDialogueClaimRepairGuidance(repairHints, foreignErrors).Length == 0,
                    "A comparison cannot guide a repair for another source revision.");
                bool sawGuidance = false;
                var guided = RetryDialogueIntegrityViolation(TestDict("ok", true, "content", Json.Serialize(told)),
                    TestDict("requestType", "dialogue"), payload, history, "fixture", "fixture", "dialogue", "npc", "",
                    repairRequest => { sawGuidance = ReadDictionaryList(repairRequest, "messages").Any(message =>
                        ReadString(message, "content", "").Contains("LOCAL SOURCE-BOUND COMPARISON GUIDANCE", StringComparison.Ordinal));
                        return TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "I told you to stop talking about the future. The fire is warm."))); }, false);
                Add("grounding_guidance_reaches_writer", sawGuidance && ReadBool(guided, "ok", false),
                    "Actual integrity repair receives helper evidence without adding a separate writer or checker call.");
                hints = CompareDialogueRecallClaims(TestDict("reply", "I said stop talking about the future."), payload, history, "npc", TestDict());
                Add("grounding_false_positive_no_repair_guidance", BuildDialogueClaimRepairGuidance(hints, new()).Length == 0,
                    "A model flag alone does not manufacture a repair or alter correct prose.");
                Add("grounding_unsupported_is_not_false", ReadString(hints, "status", "") == "compared"
                    && ReadString(hints, "scoringKind", "") == "grounding_binary" && Errors("I said stop talking about the future.").Count == 0
                    && ReadDictionaryList(hints, "comparisons").All(r => ReadString(r, "outcome", "") == "unsupported")
                    && !ReadBool(hints, "semanticAccuracyCertified", true), "Unsupported evidence cannot prove false attribution or authorize a correction.");
                foreach (string attack in new[] { "weights", "contradiction", "tokens", "scores", "source" })
                {
                    DialogueClaimHelperReplay.Value = request => {
                        var response = GroundingResponse(request); var row = ReadDictionaryList(response, "rows")[0];
                        if (attack == "weights") response["weightsHash"] = "unbound";
                        if (attack == "contradiction") row["outcome"] = "contradicted";
                        if (attack == "tokens") row["inputTokens"] = 257;
                        if (attack == "scores") row["scores"] = TestDict("unsupported", double.NaN, "supported", .01);
                        if (attack == "source") row["sourceHash"] = "foreign";
                        return response;
                    };
                    var rejectedHints = CompareDialogueRecallClaims(TestDict("reply", wrong), payload, history, "npc", TestDict());
                    Add("grounding_rejects_" + attack, ReadString(rejectedHints, "status", "") == "unsupported_helper_response"
                        && BuildDialogueClaimRepairGuidance(rejectedHints, errors).Length == 0,
                        "Binary grounding requires pinned complete source-bound scores and cannot masquerade as three-way contradiction.");
                }
                DialogueClaimHelperReplay.Value = request => { calls++; return GroundingResponse(request); };
                int before = calls;
                var noClaims = CompareDialogueRecallClaims(TestDict("reply", "I am glad you stayed."), payload, history, "npc", TestDict());
                Add("no_claim_no_inference", calls == before && ReadInt(noClaims, "comparisonCount", -1) == 0, "Ordinary present-tense reply creates no NLI call.");
                DialogueClaimHelperReplay.Value = _ => TestDict("ok", true, "schema", DialogueClaimSchema, "modelHash", "wrong", "rows", new List<Dictionary<string, object>>());
                Add("wrong_model_rejected", ReadString(CompareDialogueRecallClaims(TestDict("reply", wrong), payload, history, "npc", TestDict()), "status", "") == "unsupported_helper_response", "Unpinned scores are not accepted.");
                DialogueClaimHelperReplay.Value = _ => throw new TimeoutException("fixture");
                var original = TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "I said stop talking about the future.", "memoryWrites", new List<Dictionary<string, object>>())));
                int writerCalls = 0;
                var accepted = RetryDialogueIntegrityViolation(original, TestDict("requestType", "dialogue"), payload, history, "fixture", "fixture", "dialogue", "npc", "",
                    _ => { writerCalls++; return TestDict("ok", false); }, false);
                Add("timeout_preserves_good_reply", ReadBool(accepted, "ok", false) && writerCalls == 0
                    && ReadString(TryParseJsonObject(ReadString(accepted, "content", "")), "reply", "") == "I said stop talking about the future.", "Unavailable helper cannot block usable dialogue or create a rewrite.");
                original["content"] = Json.Serialize(parsed);
                accepted = RetryDialogueIntegrityViolation(original, TestDict("requestType", "dialogue"), payload, history, "fixture", "fixture", "dialogue", "npc", "",
                    _ => { writerCalls++; return TestDict("ok", false); }, false);
                var acceptedParsed = TryParseJsonObject(ReadString(accepted, "content", ""));
                var receipt = ReadDictionary(acceptedParsed, "dialogueClaimValidation");
                Add("exact_correction_no_writer", ReadBool(accepted, "ok", false) && writerCalls == 0
                    && ReadString(receipt, "acceptedReplyHash", "") == MemorySelectionTextHash(ReadString(acceptedParsed, "reply", "")).ToLowerInvariant(),
                    "Exact pronoun ownership correction requires no provider and hashes the final marked prose.");
                var named = TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "Michael said stop talking about the future. The fire is warm.", "memoryWrites", new List<Dictionary<string, object>>())));
                accepted = RetryDialogueIntegrityViolation(named, TestDict("requestType", "dialogue"), payload, history, "fixture", "fixture", "dialogue", "npc", "",
                    _ => { writerCalls++; return TestDict("ok", false, "content", "{}"); }, false);
                acceptedParsed = TryParseJsonObject(ReadString(accepted, "content", ""));
                Add("rewrite_failure_keeps_conversation", ReadBool(accepted, "ok", false) && writerCalls == 1 && ReadString(acceptedParsed, "reply", "").Contains("The fire is warm.")
                    && !ReadString(acceptedParsed, "reply", "").Contains("Michael said"), "Bounded failed writer repair withholds only the proven false clause and retains useful speech.");
                int recipientRepairCalls = 0;
                // The prose-only patch is merged into an already accepted,
                // complete dialogue response. A reply-only original would
                // correctly fail the existing structured-response gate.
                var toldOriginal = AgencyTestReply(null);
                toldOriginal["reply"] = ReadString(told, "reply", "");
                toldOriginal.Remove("body");
                toldOriginal = CompleteConversationStructuredResponse(toldOriginal, "dialogue", out _);
                var toldResult = TestDict("ok", true, "content", Json.Serialize(toldOriginal));
                accepted = RetryDialogueIntegrityViolation(toldResult, TestDict("requestType", "dialogue"), payload, history, "fixture", "fixture", "dialogue", "npc", "",
                    _ => { recipientRepairCalls++; return TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "I told you to stop talking about the future. The fire is warm."))); }, false);
                Add("recipient_repair_revalidated", recipientRepairCalls == 1 && ReadBool(accepted, "ok", false)
                    && StructuredResponseIsComplete(ReadString(accepted, "content", ""), "dialogue")
                    && ReadString(TryParseJsonObject(ReadString(accepted, "content", "")), "reply", "").StartsWith("I told you", StringComparison.Ordinal),
                    "One targeted writer repair can correct both roles without producing a self-addressed instruction.");
                DialogueClaimExtractorReplay.Value = request => TestDict("ok", true, "schema", "reign-dialogue-claim-extraction-v1", "sourceId", request["sourceId"],
                    "textHash", request["textHash"], "modelHash", new string('a', 64), "remoteCalls", 0, "offsetEncoding", "utf16", "authority", "claim_candidates_only",
                    "claims", new List<Dictionary<string, object>> { TestDict("speaker", TestDict("text", "You", "start", 0, "end", 3, "confidence", .99),
                        "statement", TestDict("text", "invented", "start", 9, "end", 16, "confidence", .99)) });
                Add("extractor_invented_span_rejected", ReadString(ExtractDialogueClaimCandidates(wrong, TestDict("enableLocalMemoryExtractor", true, "localMemoryExtractorQualifiedModelHash", new string('a', 64))), "status", "") == "unsupported_helper_response",
                    "GLiNER spans must bind exact UTF-16 source text and qualified model; confidence cannot invent a claim.");
            }
            finally { DialogueClaimHelperReplay.Value = priorReplay; DialogueClaimExtractorReplay.Value = priorExtractor; }
            var originalSpans = new List<Dictionary<string, object>> {
                TestDict("turnId", "p", "speakerId", "player", "text", "The harbor has two piers."), TestDict("turnId", "n", "speakerId", "npc", "text", "I agree the harbor has two piers.") };
            var write = TestDict("text", "Michael said the harbor has two piers and Rhea agreed with that description.", "sourceEvidence", TestDict("turnId", "p", "speakerId", "player", "actorIds", new List<string> { "player", "npc" }, "quote", "The harbor has two piers."));
            AttachConversationWriteEvidence(new List<Dictionary<string, object>> { write }, originalSpans, "npc", "");
            Add("compound_single_span_incomplete", ReadString(write, "sourceValidation", "") == "incomplete"
                && ReadString(write, "sourceValidationReason", "") == "compound_actor_claims_need_additional_spans", "One quote cannot establish two spoken contributions.");
            write["sourceEvidence"] = TestDict("turnId", "p", "speakerId", "player", "actorIds", new List<string> { "player", "npc" }, "quote", "The harbor has two piers.");
            write["sourceEvidenceSpans"] = new List<Dictionary<string, object>> { TestDict("turnId", "n", "speakerId", "npc", "quote", "I agree the harbor has two piers.") };
            AttachConversationWriteEvidence(new List<Dictionary<string, object>> { write }, originalSpans, "npc", "");
            Add("compound_multiple_spans_bound", ReadDictionaryList(write, "sourceEvidenceSpans").Count == 2
                && ReadDictionaryList(write, "sourceEvidenceSpans").All(s => ReadString(s, "status", "") == "supported_span")
                && !ReadBool(ReadDictionary(write, "claimSourceCoverage"), "claimEntailmentVerified", true),
                "Both original quotes bind, without pretending the complete summary is semantically verified.");
            var defaults = DefaultPromptTemplates();
            foreach (var kind in new[] { CodexConversationContractKind.Individual, CodexConversationContractKind.Group,
                CodexConversationContractKind.Castle, CodexConversationContractKind.Social, CodexConversationContractKind.Correspondence })
            {
                var selection = CodexConversationSchemas.Select(kind, defaults, defaults, TestDict());
                var properties = ReadDictionary(selection.Schema, "properties");
                bool supported = CheckStrictSchema(selection.Schema, out var schemaError);
                foreach (string field in new[] { "memoryWrites", "beliefWrites", "obligationWrites", "comprehensionWrites", "dynamicCharacteristicWrites", "continuityWrites" })
                {
                    if (!properties.ContainsKey(field)) continue;
                    var item = ReadDictionary(ReadDictionary(properties, field), "items");
                    var itemProperties = ReadDictionary(item, "properties");
                    var spans = ReadDictionary(itemProperties, "sourceEvidenceSpans");
                    supported &= ReadString(spans, "type", "") == "array"
                        && ReadDictionary(ReadDictionary(spans, "items"), "properties").ContainsKey("quote")
                        && ReadStringList(item, "required").Contains("sourceEvidenceSpans");
                }
                supported &= kind == CodexConversationContractKind.Correspondence
                    || properties.ContainsKey("addressedHeroStringIds");
                Add("strict_citation_schema_" + kind, supported,
                    "Strict provider schema supports exact contribution citations and explicit dialogue addressees: " + schemaError);
            }
            return rows;
        }
    }
}
