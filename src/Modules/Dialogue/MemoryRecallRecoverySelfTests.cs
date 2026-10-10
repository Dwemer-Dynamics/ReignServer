using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void RunMemoryRecallGroundingAssertions(Action<string, bool, object> check)
        {
            void Check(string id, bool ok, object data = null) => check("grounding_" + id, ok, data);
            var payload = TestDict("heroStringId", "npc", "timelineId", "main", "worldDay", 9d,
                "sceneTurnId", "recall_guard", "playerText", "Do you remember Brindle and the crab?");
            const string original = "Brindle is my mule. A crab pinched his nose beside the watermill.";
            string Record(string id, string body, string observer = "npc") => RenderContinuityRecord(id, "targeted_historical_exchange", observer,
                "origin", 1d, 95, true, "Source session metadata: participants=npc, player; channel=in_person.\n- [Day 1] [source="
                    + id + " role=player] Mira: " + body);
            string evidence = Record("original", original);
            List<Dictionary<string, object>> Messages(string value) => new() { TestDict("role", "user", "content", value) };
            void Bind(Dictionary<string, object> context, string expected, string delivered, string observer = "npc")
            {
                CaptureRequiredPromptEvidence(context, "selectedContextAndMemory", expected);
                BindDeliveredMemoryRecallEvidence(context, observer, Messages(delivered), "Aster");
            }
            bool Violates(string reply, Dictionary<string, object> context = null) => FindDeliveredMemoryRecallViolations(
                TestDict("reply", reply), context ?? payload, "npc").Count > 0;
            Bind(payload, evidence, evidence);
            foreach (string bad in new[] { "I have a memory gap about Brindle.", "You recovered my memories of the crab.",
                "I remember it now, like a missing page returned to its place.",
                "The memories were filed under the wrong shelf. It is back.",
                "I don't have the page, but I have the handwriting." })
                Check("reject_loss_" + PromptHash(bad).Substring(0, 8), Violates(bad), bad);
            foreach (string denial in new[] { "I do not remember Brindle.", "I don't remember the crab.", "I have no memory of him.",
                "I cannot recall him.", "I have no recollection of Brindle.", "Aster does not remember Brindle." })
                Check("reject_supported_topic_denial_" + PromptHash(denial).Substring(0, 8), Violates(denial), denial);
            foreach (string good in new[] { "I remember Brindle. A crab pinched his nose.",
                "I have not lost my memories. I remember Brindle.", "That was a mistaken answer, not memory loss.",
                "I haven't lost my memories of the crab.", "There is no memory loss. I spoke too quickly.",
                "You never supplied the groom's name; I cannot tell you who he was.",
                "I cannot recall the groom's name.", "I remember Brindle and the crab. You did not tell me the groom's name, so I cannot recall it.",
                "Earlier I said I do not remember Brindle; that was a mistaken answer.",
                "I remember the book: its missing page had red ink.",
                "I remember reading the old book. A page was missing.",
                "I remember reading the old book. I remember the missing page." })
                Check("allow_recall_and_uncertainty_" + PromptHash(good).Substring(0, 8), !Violates(good), good);
            Check("literal_book_cannot_exempt_explicit_memory_loss",
                Violates("I remember reading the old book. A page was missing. I have a memory gap about Brindle."));
            var literalWrite = TestDict("text", "I remember reading the old book. I remember the missing page.");
            var literalProposal = TestDict("reply", "A crab pinched Brindle's nose.", "memoryWrites", new[] { literalWrite });
            Check("literal_book_write_keeps_its_passage_context", FindDeliveredMemoryRecallViolations(literalProposal, payload, "npc").Count == 0);
            literalProposal["memoryWrites"] = new[] { literalWrite, TestDict("text", "I remember Brindle like a missing page returned to its place.") };
            Check("literal_book_write_cannot_exempt_another_record", FindDeliveredMemoryRecallViolations(literalProposal, payload, "npc").Count > 0);
            literalProposal["memoryWrites"] = new[] { literalWrite };
            literalProposal["reply"] = "I remember Brindle like a missing page returned to its place.";
            Check("literal_book_write_cannot_exempt_reply", FindDeliveredMemoryRecallViolations(literalProposal, payload, "npc").Count > 0);
            var completeProofPayload = CloneDictionary(payload);
            var completeProofRecords = Enumerable.Range(0, 70).Select(i => Record("proof_" + i,
                original + " " + new string('p', 900) + " Archive detail " + i + ".")).ToList();
            string oversizedProof = Record("proof_larger_than_old_budget", original + " " + new string('q', 49000));
            completeProofRecords.Add(oversizedProof);
            string pairedContext = Record("paired_without_repeated_names", "He stood watching the inlet until sunset.");
            string buriedContext = Record("answer_inside_long_source", string.Join(" ", Enumerable.Range(0, 90).Select(i => "opening_" + i))
                + " The crab pinched his nose beside the watermill. "
                + string.Join(" ", Enumerable.Range(0, 90).Select(i => "closing_" + i)));
            completeProofRecords.Add(pairedContext);
            completeProofRecords.Add(buriedContext);
            Check("repair_sources_can_lack_sampled_query_overlap", new[] { pairedContext, buriedContext }.All(r =>
                !MemoryRecallTerms(ReadString(payload, "playerText", "")).Intersect(MemoryRecallTerms(r)).Any()));
            string completeProof = string.Join("\n\n", completeProofRecords);
            Bind(completeProofPayload, completeProof + "\n\n" + Record("not_delivered", original), completeProof);
            var proofViolation = FindDeliveredMemoryRecallViolations(TestDict("reply", "I have a memory gap about Brindle."), completeProofPayload, "npc").Single();
            var proofSources = ReadDictionaryList(proofViolation, "deliveredOriginals");
            Check("repair_proof_keeps_more_than_old_record_limits", proofSources.Count == 73);
            Check("repair_proof_keeps_whole_source_beyond_old_character_limit", proofSources.Any(r =>
                ReadString(r, "original", "") == NormalizePromptSegment(oversizedProof)));
            Check("repair_proof_never_promotes_undelivered_source", proofSources.All(r => ReadString(r, "sourceId", "") != "not_delivered"));
            var proofRequest = TestDict("requestType", "dialogue_continuity_repair", "model", "recall-proof-fixture",
                "maxTokens", 8000, "messages", new[] { TestDict("role", "system", "content", "Repair only from supplied originals.") });
            BindDeliveredRecallRepairEvidence(proofRequest, completeProofPayload, "npc");
            var proofSettings = TestDict("llmProvider", NanoGptProvider, "reasoningMode", "off");
            string proofRoute = NanoGptProvider + ":recall-proof-fixture:" + PromptHash(NanoGptChatCompletionsUrl + "|" + CanonicalJson(TestDict())).Substring(0, 20);
            var proofCertificate = TestDict("endpoint", NanoGptChatCompletionsUrl, "model", "recall-proof-fixture", "contextTokens", 1048576,
                "maxOutputTokens", 8000, "source", "isolated-contract", "validUntilUtc", DateTimeOffset.UtcNow.AddHours(1).ToString("o"));
            proofSettings["verifiedConversationContextWindows"] = TestDict(proofRoute, proofCertificate);
            Dictionary<string, object> ProofWire(Dictionary<string, object> request, Dictionary<string, object> suppliedSettings = null) =>
                ReadDictionary(CaptureMemoryReplayProviderRequest(request, suppliedSettings ?? proofSettings), "providerWireCapture");
            var proofWire = ProofWire(proofRequest);
            var proofPreflight = ReadDictionary(ReadDictionary(proofWire, "providerPreflight"), "continuity");
            string proofBody = ReadString(proofWire, "requestJson", "");
            string proofMessages = string.Join("\n", ReadDictionaryList(TryParseJsonObject(proofBody), "messages").Select(m => ReadString(m, "content", "")));
            Check("repair_wire_keeps_all_complete_bound_originals", ReadString(proofWire, "status", "") == "captured"
                && ReadBool(proofPreflight, "fits", false) && ReadBool(proofPreflight, "requiredSourceCoverageComplete", false)
                && ReadStringList(ReadDictionary(proofPreflight, "requiredSources"), "requiredSourceIds").Count == 73
                && completeProofRecords.All(r => proofMessages.Contains(NormalizePromptSegment(r), StringComparison.Ordinal)));
            Check("repair_wire_preserves_paired_and_buried_source_answers", new[] { pairedContext, buriedContext }.All(r =>
                proofMessages.Contains(NormalizePromptSegment(r), StringComparison.Ordinal)));
            var removedProof = CloneDictionary(proofRequest);
            removedProof["messages"] = new[] { TestDict("role", "user", "content", string.Join(",", proofSources.Select(r => ReadString(r, "sourceId", "")))) };
            var removedWire = ProofWire(removedProof);
            Check("repair_source_ids_cannot_mask_dropped_prose", ReadString(removedWire, "status", "") == "blocked"
                && !ReadBool(ReadDictionary(ReadDictionary(removedWire, "providerPreflight"), "continuity"), "requiredSourceCoverageComplete", true)
                && ReadInt(removedWire, "providerCalls", -1) == 0);
            var changedProof = CloneDictionary(proofRequest);
            var changedProofMessages = ReadDictionaryList(changedProof, "messages");
            changedProofMessages.Last()["content"] = ReadString(changedProofMessages.Last(), "content", "").Replace("pinched his nose", "bit his nose");
            changedProof["messages"] = changedProofMessages;
            Check("repair_source_content_changes_fail_closed", ReadString(ProofWire(changedProof), "status", "") == "blocked");
            var smallProofSettings = CloneDictionary(proofSettings);
            ReadDictionary(ReadDictionary(smallProofSettings, "verifiedConversationContextWindows"), proofRoute)["contextTokens"] = 16000;
            var smallProofWire = ProofWire(proofRequest, smallProofSettings);
            Check("repair_capacity_failure_preserves_sources_without_dispatch", ReadString(smallProofWire, "status", "") == "blocked"
                && ReadString(smallProofWire, "requestJson", "") == proofBody && ReadInt(smallProofWire, "providerCalls", -1) == 0
                && !ReadBool(ReadDictionary(smallProofWire, "providerPreflight"), "fits", true));
            var unknownQuery = CloneDictionary(payload); unknownQuery["playerText"] = "Which groom witnessed Brindle and the crab?";
            Bind(unknownQuery, evidence, evidence);
            Check("unknown_question_cannot_resolve_pronoun_to_known_topic", !Violates("I cannot recall him.", unknownQuery));
            var onlyFailed = CloneDictionary(payload);
            string failedEvidence = Record("prior_failure", "Do you remember Brindle and the crab? I do not remember Brindle.");
            Bind(onlyFailed, failedEvidence, failedEvidence);
            Check("prior_questions_and_denials_are_not_positive_recall_evidence", !Violates("I do not remember Brindle.", onlyFailed));
            var mixedSource = CloneDictionary(payload);
            string mixedEvidence = Record("mixed", original + "\n- [Day 1] [source=failed_npc role=npc] Aster: I have a memory gap about Brindle.");
            Bind(mixedSource, mixedEvidence, mixedEvidence);
            Check("failed_partner_cannot_erase_positive_player_source", Violates("I do not remember Brindle.", mixedSource));
            var contextAnswer = TestDict("reply", "The crab pinched Brindle's nose. The road buried that story, but it is mine again.");
            Check("source_conflict_triggers_existing_rewrite_without_reply_keyword_match",
                BroadRecallLossNarrative(ReadString(contextAnswer, "reply", ""), true).Length == 0
                && FindDeliveredMemoryRecallViolations(contextAnswer, mixedSource, "npc")
                    .Any(v => ReadString(v, "type", "") == "critical_continuity_recall_context"));
            var splitContext = CloneDictionary(payload);
            string splitEvidence = Record("split_context", original
                + "\n- [Day 1] [source=split_npc role=npc] Aster: *She notices a page is missing from that scene.*\n\n"
                + "*She is sorting memory.*\n\nI remember the orchard, but the mule is not there.");
            Bind(splitContext, splitEvidence, splitEvidence);
            Check("same_npc_turn_carries_recall_context_across_sentences",
                FindDeliveredMemoryRecallViolations(contextAnswer, splitContext, "npc")
                    .Any(v => ReadString(v, "type", "") == "critical_continuity_recall_context"));
            var literalContext = CloneDictionary(payload);
            string literalEvidence = Record("split_book", original
                + "\n- [Day 1] [source=book_npc role=npc] Aster: I remember reading the old book.\nA page was missing.");
            Bind(literalContext, literalEvidence, literalEvidence);
            Check("split_literal_book_and_page_are_not_recall_loss", !Violates("The crab pinched Brindle's nose.", literalContext));
            var rememberedLiteralContext = CloneDictionary(payload);
            string rememberedLiteralEvidence = literalEvidence.Replace("A page was missing.", "I remember the missing page.");
            Bind(rememberedLiteralContext, rememberedLiteralEvidence, rememberedLiteralEvidence);
            Check("split_literal_book_recall_remains_literal", !Violates("The crab pinched Brindle's nose.", rememberedLiteralContext));
            var foreignContext = CloneDictionary(payload);
            string foreignEvidence = Record("split_speakers", original
                + "\n- [Day 1] [source=aster_page role=npc] Aster: A page was missing."
                + "\n- [Day 1] [source=korin_recall role=npc] Korin: I remember the orchard.");
            Bind(foreignContext, foreignEvidence, foreignEvidence);
            Check("another_npc_cannot_supply_page_recall_context", !Violates("The crab pinched Brindle's nose.", foreignContext));
            var separateTurnContext = CloneDictionary(payload);
            string separateTurnEvidence = foreignEvidence.Replace("Korin: I remember", "Aster: I remember");
            Bind(separateTurnContext, separateTurnEvidence, separateTurnEvidence);
            Check("separate_npc_turn_cannot_supply_page_recall_context", !Violates("The crab pinched Brindle's nose.", separateTurnContext));
            var splitCausalContext = CloneDictionary(payload);
            string splitCausalEvidence = Record("split_causal", original + " Aster lost her memories after a blow to her head."
                + "\n- [Day 1] [source=causal_npc role=npc] Aster: I lost my memories after a blow to my head.\nI remember an old book.\nA page was missing.");
            Bind(splitCausalContext, splitCausalEvidence, splitCausalEvidence);
            Check("split_literal_page_preserves_independent_causal_event", !Violates("I lost my memories after a blow to my head.", splitCausalContext));
            var reviewed = TestDict("reply", "The crab pinched Brindle's nose beside the watermill.");
            BindDeliveredRecallRewrite(mixedSource, "npc", reviewed);
            Check("reviewed_original_recall_clears_context_risk", FindDeliveredMemoryRecallViolations(reviewed, mixedSource, "npc").Count == 0);
            var changedReview = CloneDictionary(reviewed); changedReview["reply"] = "You later brought Brindle to the mill as promised.";
            Check("changed_reply_cannot_reuse_context_review", FindDeliveredMemoryRecallViolations(changedReview, mixedSource, "npc").Count > 0);
            changedReview = CloneDictionary(reviewed); changedReview["beliefWrites"] = new[] { TestDict("claim", "The story has returned to its shelf.") };
            Check("changed_mental_write_cannot_reuse_context_review", FindDeliveredMemoryRecallViolations(changedReview, mixedSource, "npc").Count > 0);
            foreach (string field in DeliveredRecallWriteFields)
            {
                var changedField = CloneDictionary(reviewed); changedField[field] = new[] { TestDict("text", "The shelf holds the story again.") };
                Check("review_binds_every_durable_field_" + field, FindDeliveredMemoryRecallViolations(changedField, mixedSource, "npc").Count > 0);
                var falseField = TestDict("reply", "The crab pinched Brindle's nose.", field, new[] { TestDict("text", "Aster recovered her lost memories of Brindle.") });
                Check("every_durable_field_rejects_false_loss_" + field, FindDeliveredMemoryRecallViolations(falseField, payload, "npc").Count > 0);
            }
            var unknownAfterConflict = TestDict("reply", "I remember Brindle. You did not tell me the groom's name; I cannot recall it.");
            BindDeliveredRecallRewrite(mixedSource, "npc", unknownAfterConflict);
            Check("source_conflict_review_preserves_narrow_uncertainty", FindDeliveredMemoryRecallViolations(unknownAfterConflict, mixedSource, "npc").Count == 0);
            var stillFalse = TestDict("reply", "I do not remember Brindle.");
            BindDeliveredRecallRewrite(mixedSource, "npc", stillFalse);
            Check("review_attempt_never_exempts_direct_contradiction", FindDeliveredMemoryRecallViolations(stillFalse, mixedSource, "npc").Count > 0);
            BindDeliveredRecallRewrite(mixedSource, "npc", null);
            Check("failed_review_revokes_receipt", FindDeliveredMemoryRecallViolations(unknownAfterConflict, mixedSource, "npc").Count > 0);
            var forgedReview = CloneDictionary(mixedSource); forgedReview["reviewedResponseHash"] = DeliveredRecallResponseHash(forgedReview, "npc", reviewed);
            Bind(forgedReview, mixedEvidence, mixedEvidence);
            Check("client_cannot_supply_context_review", FindDeliveredMemoryRecallViolations(reviewed, forgedReview, "npc").Count > 0);
            var supportedHistory = CloneDictionary(payload);
            string causalHistory = mixedEvidence.Replace("I have a memory gap about Brindle.", "I lost my memories of Brindle after a blow to my head.")
                + "\n" + Record("causal_player", "Aster lost her memories of Brindle after a blow to her head.");
            Bind(supportedHistory, causalHistory, causalHistory);
            Check("independent_causal_history_does_not_trigger_conflict_review", !Violates("I lost my memories after the blow to my head.", supportedHistory));
            Check("denial_is_not_quarantined_state", !IsUnsupportedRecallAbsenceState("That was a mistaken answer, not memory loss.")
                && !IsUnsupportedRecallAbsenceState("I have not lost my memories."));
            var proposed = TestDict("reply", "A crab pinched his nose.", "memoryWrites", new[] { TestDict("text", "Aster recovered her lost memories of Brindle.") });
            Check("lasting_writes_cannot_reintroduce_loss", FindDeliveredMemoryRecallViolations(proposed, payload, "npc").Count > 0);
            var rejected = new List<Dictionary<string, object>>();
            Check("ordinary_memory_write_uses_absence_guard", FilterRoleplayFeedbackWrites(
                new() { TestDict("text", "Aster recovered her lost memories of Brindle.") }, "memory", rejected).Count == 0
                && rejected.Any(r => ReadString(r, "reason", "") == "unsupported_recall_absence"));
            var forged = CloneDictionary(payload); forged["memoryRecallGrounding"] = TestDict("sourceCount", 99);
            Check("client_metadata_cannot_supply_delivery", !Violates("I have a memory gap.", forged));
            var omitted = CloneDictionary(payload); Bind(omitted, evidence, "Unrelated current input.");
            Check("selection_without_delivery_is_not_proof", !Violates("I have a memory gap.", omitted));
            var other = CloneDictionary(payload); Bind(other, Record("private", original, "other"), Record("private", original, "other"));
            Check("another_observer_cannot_supply_proof", !Violates("I have a memory gap.", other));
            payload["sceneTurnId"] = "different_turn";
            Check("stale_turn_receipt_is_rejected", !Violates("I have a memory gap."));
            payload["sceneTurnId"] = "recall_guard";
            string supported = evidence + "\n" + Record("injury", "Aster lost her memories of Brindle after a blow to her head.");
            Bind(payload, supported, supported);
            Check("independent_same_character_same_event_preserved", !Violates("I lost my memories after the blow to my head."));
            Check("player_second_person_loss_is_recognized", BroadRecallLossNarrative("You lost your memories after a blow to your head.").Length > 0);
            Check("legitimate_reply_cannot_exempt_another_loss_write", FindDeliveredMemoryRecallViolations(TestDict(
                "reply", "I lost my memories after the blow to my head.", "memoryWrites", new[] { TestDict("text", "I have memory loss after a fever.") }), payload, "npc").Count > 0);
            Check("legitimate_clause_cannot_exempt_another_loss_clause", Violates("I lost my memories after the blow to my head. I have a memory gap about Brindle."));
            Check("independent_event_cannot_excuse_different_cause", Violates("My memory loss followed the fever."));
            string otherInjury = evidence + "\n" + Record("other_injury", "Korin lost his memories of Brindle after a blow to his head.");
            Bind(payload, otherInjury, otherInjury);
            Check("another_characters_injury_does_not_exempt_speaker", Violates("I lost my memories after the blow to my head."));
            Check("another_characters_source_backed_loss_can_be_reported", !Violates("Korin lost his memories of Brindle after a blow to his head."));
            Check("another_characters_cause_cannot_be_changed", Violates("Korin lost his memories of Brindle after a fever."));
            Bind(payload, supported, supported);
            Check("speakers_injury_cannot_be_assigned_to_another_person", Violates("Korin lost his memories of Brindle after a blow to his head."));
            string hypothetical = evidence + "\n" + Record("question", "Did you lose your memories of Brindle after a blow to your head?");
            Bind(payload, hypothetical, hypothetical);
            Check("hypothetical_cause_is_not_evidence", Violates("I lost my memories after the blow to my head."));
            Bind(payload, supported, evidence);
            Check("undelivered_cause_cannot_exempt_reply", Violates("I lost my memories after the blow to my head."));
            string raw = "I have a memory gap about Brindle. The gate is closed.";
            string rendered = RenderExactHistoryTurn(TestDict("role", "npc", "speaker_id", "npc", "speaker_name", "Aster", "text", raw), null);
            Check("historical_annotation_retains_complete_original", rendered.Contains(raw) && rendered.Contains("Recall source status"));

            var longContext = CloneDictionary(payload);
            string ordinaryHistory = string.Join(" ", Enumerable.Repeat(
                "We watched Brindle beside the orchard gate while the river boat waited at the watermill.", 64));
            string longEvidence = string.Join("\n", Enumerable.Range(0, 32).Select(i => Record("long_" + i,
                original + "\n- [Day 1] [source=long_npc_" + i + " role=npc] Aster: " + ordinaryHistory
                    + (i == 31 ? " I have no memory of Brindle." : ""))));
            var longAnswer = TestDict("reply", string.Join(" ", Enumerable.Repeat(
                "I remember Brindle waiting beside the orchard gate.", 80)));
            var longClock = Stopwatch.StartNew();
            Bind(longContext, longEvidence, longEvidence);
            var longConflict = FindDeliveredMemoryRecallViolations(longAnswer, longContext, "npc");
            BindDeliveredRecallRewrite(longContext, "npc", longAnswer);
            var longReviewed = FindDeliveredMemoryRecallViolations(longAnswer, longContext, "npc");
            longClock.Stop();
            var longDiagnostics = TestDict("sourceCharacters", longEvidence.Length, "sourceRecords", 32,
                "ordinaryNpcClauses", 32 * 64, "replyClauses", 80, "durationMs", longClock.ElapsedMilliseconds,
                "schema", "reign-recall-guard-scale-v1", "sourceSha256", PromptHash(longEvidence),
                "conflictDetected", longConflict.Any(v => ReadString(v, "type", "") == "critical_continuity_recall_context"),
                "reviewAccepted", longReviewed.Count == 0, "maximumMs", 5000);
            string scaleDirectory = Path.Combine(TestsDir, "memory-accuracy");
            Directory.CreateDirectory(scaleDirectory);
            File.WriteAllText(Path.Combine(scaleDirectory, "recall-guard-scale-" + Guid.NewGuid().ToString("N") + ".json"), Json.Serialize(longDiagnostics));
            Check("long_history_preserves_conflict_and_review", ReadBool(longDiagnostics, "conflictDetected", false)
                && ReadBool(longDiagnostics, "reviewAccepted", false), longDiagnostics);
            Check("long_history_guard_finishes_within_five_seconds", longClock.ElapsedMilliseconds < 5000, longDiagnostics);

            Dictionary<string, object> Line(string id, string role, string speaker, string body) => TestDict(
                "id", id, "exchangeId", "recent_" + id, "sessionId", "current", "worldDay", 8d,
                "role", role, "speaker", speaker, "speakerHeroStringId", role == "player" ? "player" : "npc", "text", body);
            var recentPositive = Line("recent_positive", "player", "Mira", original);
            var recentFailed = Line("recent_failed", "npc", "Aster", "I have no memory of him.");
            var recentLines = new List<Dictionary<string, object>> { recentPositive, recentFailed };
            var recentPayload = CloneDictionary(payload); recentPayload["playerHeroStringId"] = "player";
            var knownPlayer = TestDict("usableName", "Mira", "canonicalNameAllowed", true);
            void BindRecent(Dictionary<string, object> context, string transcript, List<Dictionary<string, object>> lines,
                string historical = "", string delivered = null, string section = "canonicalTranscript")
            {
                CaptureRequiredPromptEvidence(context, "selectedContextAndMemory", historical);
                CaptureRequiredPromptEvidence(context, section, transcript);
                BindDeliveredMemoryRecallEvidence(context, "npc", Messages(delivered ?? historical + "\n" + transcript), "Aster", lines);
            }
            bool ContextConflict(Dictionary<string, object> context) => FindDeliveredMemoryRecallViolations(contextAnswer, context, "npc")
                .Any(v => ReadString(v, "type", "") == "critical_continuity_recall_context");
            foreach (string route in new[] { "in_person", "letter", "event" })
            {
                var lines = recentLines.Select(CloneDictionary).ToList();
                if (route == "letter") foreach (var line in lines) { line["channel"] = "correspondence"; line["direction"] = "exchanged"; }
                string Transcript(List<Dictionary<string, object>> rows) => FormatContinuityTranscript(rows, route == "event"
                    ? group => FormatEventLinesForObserver(group, recentPayload, "npc", knownPlayer) : FormatDialogueForPrompt);
                string transcript = Transcript(lines);
                var context = CloneDictionary(recentPayload); BindRecent(context, transcript, lines);
                Check("recent_only_source_conflict_" + route, ContextConflict(context));
                Check("recent_only_positive_rejects_denial_" + route, Violates("I do not remember Brindle.", context));
                var positiveOnly = CloneDictionary(recentPayload); BindRecent(positiveOnly, Transcript(lines.Take(1).ToList()), lines.Take(1).ToList());
                Check("recent_positive_without_failure_avoids_rewrite_" + route, !ContextConflict(positiveOnly)
                    && Violates("I do not remember Brindle.", positiveOnly));
                var mixedRecent = CloneDictionary(recentPayload); BindRecent(mixedRecent, Transcript(lines.Skip(1).ToList()), lines.Skip(1).ToList(), evidence);
                Check("historical_positive_recent_pronoun_failure_" + route, ContextConflict(mixedRecent));
                var omittedRecent = CloneDictionary(recentPayload); BindRecent(omittedRecent, transcript, lines, evidence, evidence);
                Check("undelivered_recent_failure_is_not_proof_" + route, !ContextConflict(omittedRecent));
                var missingRows = CloneDictionary(recentPayload); BindRecent(missingRows, transcript, null, evidence);
                Check("rendered_names_cannot_supply_typed_recent_roles_" + route, !ContextConflict(missingRows));
                var omittedBody = CloneDictionary(recentPayload);
                BindRecent(omittedBody, transcript.Replace("I have no memory of him.", "We watched the mill."), lines, evidence);
                Check("omitted_recent_body_cannot_bind_" + route, !ContextConflict(omittedBody));
                var foreignObserver = CloneDictionary(recentPayload);
                BindRecent(foreignObserver, transcript.Replace("observer=current_observer", "observer=other"), lines, evidence);
                Check("foreign_recent_observer_cannot_bind_" + route, !ContextConflict(foreignObserver));
                var wrongSection = CloneDictionary(recentPayload); BindRecent(wrongSection, transcript, lines, evidence, null, "untrustedRecent");
                Check("recent_marker_requires_canonical_capture_" + route, !ContextConflict(wrongSection));
                var otherNpcLines = lines.Select(CloneDictionary).ToList();
                otherNpcLines[1]["speakerHeroStringId"] = "other";
                var otherNpc = CloneDictionary(recentPayload); BindRecent(otherNpc, Transcript(otherNpcLines), otherNpcLines, evidence);
                Check("other_npc_named_like_observer_cannot_supply_conflict_" + route, !ContextConflict(otherNpc));
                foreach (string speakerKey in new[] { "speakerId", "speaker_id", "speakerHeroStringId", "heroStringId" })
                {
                    var aliasLines = lines.Select(CloneDictionary).ToList();
                    aliasLines[1].Remove("speakerHeroStringId"); aliasLines[1][speakerKey] = "other";
                    var foreignAlias = CloneDictionary(recentPayload); BindRecent(foreignAlias, Transcript(aliasLines), aliasLines, evidence);
                    Check("typed_speaker_alias_rejects_foreign_" + route + "_" + speakerKey, !ContextConflict(foreignAlias));
                    aliasLines[1][speakerKey] = "npc";
                    var currentAlias = CloneDictionary(recentPayload); BindRecent(currentAlias, Transcript(aliasLines), aliasLines, evidence);
                    Check("typed_speaker_alias_accepts_current_" + route + "_" + speakerKey, ContextConflict(currentAlias));
                }
                var stranger = TestDict("canonicalNameAllowed", false, "usableName", "the stranger");
                string ProjectIdentity(string text) => SanitizePromptForIdentity(text, "", "Mira", stranger);
                var anonymousLines = lines.Select(CloneDictionary).ToList(); anonymousLines[0]["text"] = original + " Mira brought him home.";
                string rawAnonymous = Transcript(anonymousLines), projectedAnonymous = ProjectIdentity(rawAnonymous);
                var anonymous = CloneDictionary(recentPayload);
                CaptureRequiredPromptEvidence(anonymous, "canonicalTranscript", rawAnonymous);
                string immutableCapture = CanonicalJson(CapturedRequiredPromptEvidence(anonymous));
                BindDeliveredMemoryRecallEvidence(anonymous, "npc", Messages(projectedAnonymous), "Aster", anonymousLines, ProjectIdentity);
                Check("unknown_identity_keeps_delivered_recall_" + route, !projectedAnonymous.Contains("Mira")
                    && ContextConflict(anonymous) && Violates("I do not remember Brindle.", anonymous)
                    && immutableCapture == CanonicalJson(CapturedRequiredPromptEvidence(anonymous)));
                var anonymousOmitted = CloneDictionary(recentPayload);
                CaptureRequiredPromptEvidence(anonymousOmitted, "canonicalTranscript", rawAnonymous);
                BindDeliveredMemoryRecallEvidence(anonymousOmitted, "npc", Messages(projectedAnonymous.Replace(original, "A mule.")),
                    "Aster", anonymousLines, ProjectIdentity);
                Check("identity_projection_cannot_bless_omitted_recall_" + route, !Violates("I do not remember Brindle.", anonymousOmitted));
                var unknownLines = lines.Select(CloneDictionary).ToList(); unknownLines[1]["text"] = "I cannot recall the groom's name.";
                var recentUnknown = CloneDictionary(recentPayload); BindRecent(recentUnknown, Transcript(unknownLines), unknownLines);
                Check("legitimate_recent_uncertainty_is_not_a_conflict_" + route, !ContextConflict(recentUnknown));
                var causalLines = lines.Select(CloneDictionary).ToList();
                causalLines[0]["text"] = original + " You lost your memories after a blow to your head.";
                causalLines[1]["text"] = "I lost my memories after the blow to my head.";
                var recentCause = CloneDictionary(recentPayload); BindRecent(recentCause, Transcript(causalLines), causalLines);
                Check("typed_recent_player_causal_event_preserved_" + route, !Violates("I lost my memories after the blow to my head.", recentCause));
            }
        }

        private static void IndexMemoryRecallFixtureSources(string campaign, ReignDbConnection connection, Dictionary<string, object> settings)
        {
            // Match the automatic production topic index without invoking any
            // model. Raw-only fixtures missed topic-fusion crowding in alpha.
            foreach (var row in QuerySql(connection, @"SELECT t.*,t.xmin::text AS source_revision FROM conversation_turns t
WHERE t.status='active' AND NOT EXISTS(SELECT 1 FROM memory_specialist_sources i WHERE i.turn_id=t.turn_id);"))
                IndexMemorySpecialistSource(campaign, row, settings, allowExtraction: false);
        }

        private static void RunMemoryRecallOrderingAssertions(Action<string, bool, object> check)
        {
            var snapshots = new List<Dictionary<string, object>>();
            foreach (bool reverse in new[] { false, true })
            {
                string campaign = "memory_recall_order_" + Guid.NewGuid().ToString("N");
                try
                {
                    using var connection = OpenCampaignConnection(campaign);
                    ReadMemoryPrecisionState(connection);
                    var sessions = new[] { "alpha", "middle", "zeta" };
                    foreach (string session in reverse ? sessions.Reverse() : sessions)
                    {
                        ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($id,$campaign,'npc','player','closed',1,10,'[""npc"",""player""]','{""timelineId"":""order_main""}');",
                            TestDict("id", session, "campaign", campaign));
                        foreach (int order in new[] { 0, 1 })
                        {
                            string id = session + "_" + order;
                            ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES($id,$session,$id,$order,$session,$role,$speaker,$name,$text,1,10,'[""npc"",""player""]','{""timelineId"":""order_main""}');",
                                TestDict("id", id, "session", session, "order", order, "role", order == 0 ? "player" : "npc",
                                    "speaker", order == 0 ? "player" : "npc", "name", order == 0 ? "Mira" : "Aster",
                                    "text", order == 0 ? "I left the bronze bell at the " + session + " gate." : "I saw the bell beside the " + session + " gate."));
                        }
                    }
                    var observer = new KnowledgeAccessContext { NpcId = "npc", WorldDay = 2, TimelineId = "order_main" };
                    Dictionary<string, object> Read(int limit, string excluded = "") =>
                        LoadMostRecentClosedConversation(connection, "npc", excluded, 24000, limit, observer);
                    var bounded = Read(2);
                    var complete = Read(6);
                    snapshots.Add(TestDict("bounded", bounded, "complete", complete));
                    string completeText = ReadString(complete, "text", "");
                    check("recent_tied_sessions_remain_grouped_" + reverse,
                        sessions.All(id => completeText.Split(new[] { "[HISTORICAL SOURCE SESSION " + id + "]" }, StringSplitOptions.None).Length == 2)
                        && ReadStringList(complete, "matchedTurnIds").Count == 6, complete);
                    foreach (string session in sessions.Reverse())
                        ExecuteSql(connection, "UPDATE conversation_sessions SET payload_json=payload_json WHERE session_id=$id;", TestDict("id", session));
                    var afterRowUpdates = Read(2);
                    check("recent_ties_ignore_physical_row_updates_" + reverse,
                        ReadString(bounded, "text", "") == ReadString(afterRowUpdates, "text", "")
                        && ReadStringList(bounded, "matchedTurnIds").SequenceEqual(ReadStringList(afterRowUpdates, "matchedTurnIds")), afterRowUpdates);
                    ExecuteSql(connection, "UPDATE conversation_sessions SET end_ts=11 WHERE session_id='alpha';");
                    var latestIds = ReadStringList(Read(2), "matchedTurnIds");
                    var excludedIds = ReadStringList(Read(2, "alpha"), "matchedTurnIds");
                    check("recent_real_timestamp_precedes_tie_break_" + reverse,
                        latestIds.Count == 2 && latestIds.All(id => id.StartsWith("alpha_", StringComparison.Ordinal))
                        && excludedIds.Count == 2 && excludedIds.All(id => !id.StartsWith("alpha_", StringComparison.Ordinal)), null);
                }
                finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            }
            foreach (string scope in new[] { "bounded", "complete" })
            {
                var first = ReadDictionary(snapshots[0], scope); var second = ReadDictionary(snapshots[1], scope);
                check("recent_ties_ignore_insertion_order_" + scope, ReadString(first, "text", "") == ReadString(second, "text", "")
                    && ReadStringList(first, "matchedTurnIds").Count > 0
                    && ReadStringList(first, "matchedTurnIds").SequenceEqual(ReadStringList(second, "matchedTurnIds")), null);
            }
        }

        private static List<Dictionary<string, object>> RunMemoryRecallRecoveryAssertions()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)) throw new InvalidOperationException("Recall recovery requires isolated Verification Lab storage.");
            var results = new List<Dictionary<string, object>>();
            void Check(string name, bool passed, object data = null) => results.Add(TestDict("caseId", "memory_recall_" + name, "passed", passed,
                "summary", name + (passed || data == null ? "" : ": " + LimitText(Json.Serialize(data), 2200)), "data", data));
            RunMemoryRecallGroundingAssertions(Check);
            RunMemoryRecallOrderingAssertions(Check);
            string campaign = "memory_recall_" + Guid.NewGuid().ToString("N");
            const string horse = "My horse is Tempest. He finds frogs and lizards interesting and gets angry when they will not hold still to be properly smelled.";
            const string frog = "His first frog jumped out of the stream while he drank. Tempest ran a full circle, returned halfway, snorted and stamped, then watched the water all evening.";
            const string reaction = "Tempest. And he gets angry at frogs. That is an excellent name for a ridiculous horse.";
            const string frogReply = "His first frog. He ran a full circle! I laughed, and teased him about his grudge against the stream.";
            const string gait = "Stone shifts her weight smoothly from walk to trot when she thinks no one is watching. Watch her left hind leg uncock.";
            const string fence = "When you come back after the season turns, find me at the fence first. Not the keep or the hall. And bring Tempest.";
            const string coining = "Coreining means deciding the outcome before it happens and expecting the loss. You made my name into a verb.";
            const string reunion = "After you fell in the tourney I offered my hand to help you up. You told me to return in spring, brave of you to enter a tourney the 2nd day of the new season. I missed our times together.";
            const string acceptance = "I took your hand and pulled myself up. Second day of spring and I was flat on my back in the list. I missed our times together too.";
            const string interpretedSpan = "*and balance the bronze bell beside a garden flower on my palm.* I brought it back.";
            const string absence = "She has no memory of anything called Tempest and cannot remember their tournament reunion.";
            const string scene = "Campaign day: 45. Current scene: training, guards, armor, hands, ground, battle, sword, border, court, kingdom, lord, drink, travel, rain. ";
            string Narrated(string words) => "*I rest my elbows on the rail and watch the courtyard.* "
                + string.Concat(Enumerable.Repeat("The sound of boots carries from the training ground. A guard crosses the yard and the wind catches my cloak. ", 8))
                + words + " *I turn back toward you.* "
                + string.Concat(Enumerable.Repeat("There is time to stand here quietly. The road, the tournament and the business of the court can wait until later. ", 5)).TrimEnd();
            string longReaction = Narrated(reaction), longFrogReply = Narrated(frogReply), longGait = Narrated(gait),
                longFence = Narrated(fence), longCoining = Narrated(coining);
            var state = TestDict("mood", "quietly settled, recognizing her own voice in lost memory",
                "currentPlan", "Continue recovering the Tempest memory through the player's retelling",
                "currentCrisis", "Protect the political confidence; no memory of the tournament; preserve the physical boundary");
            var context = TestDict("timelineId", "recall_main", "worldDay", 45d, "playerHeroStringId", "player",
                "conversationSessionId", "current", "deferContinuityAllocation", true, "officialMemoryFirewall", false);
            var settings = TestDict("enableSemanticMemory", false, "minimeEnabled", false, "memoryPrecisionMode", "legacy",
                "useMemoryLlmForConsolidation", false, "memoryPrecisionOrdinaryTokens", 2000);
            var sourcePlan = PlanMemoryQuery("npc", "player", "Do you remember Brindle, my mule?", context);
            Check("ordinary_recall_question_is_fallback", !MemoryRecallAnswerCandidate(TestDict("text", "Do you remember Brindle, my mule?"), sourcePlan));
            Check("mixed_new_fact_stays_answer_bearing", MemoryRecallAnswerCandidate(TestDict("text",
                "My mule Brindle now lives at the watermill. Do you remember our last conversation?"), sourcePlan));
            Check("mixed_off_topic_fact_does_not_promote_probe", !MemoryRecallAnswerCandidate(TestDict("text",
                "I respect your boundary. We can rest after training. Do you remember Brindle, my mule?"), sourcePlan));
            Check("query_label_echo_does_not_promote_failed_probe", !MemoryRecallAnswerCandidate(TestDict("text",
                "Your mule Brindle. Tell me the story again. I can wait here."), sourcePlan));
            Check("query_framing_does_not_promote_failed_probe", new[] { "Can you still remember Brindle, my mule?",
                "Did you remember Brindle, my mule?", "Please remind me about Brindle, my mule." }.All(query =>
                    !MemoryRecallAnswerCandidate(TestDict("text", "Your mule Brindle. Tell me the story again. I can still wait here."),
                        PlanMemoryQuery("npc", "player", query, context))));
            Check("mixed_identity_assertion_stays_answer_bearing", MemoryRecallAnswerCandidate(TestDict("text",
                "Brindle is your mule. Tell me the story again."), sourcePlan));
            var unverified = TestDict("role", "npc", "speaker_name", "Corein", "text", "I have joined your party at the northern gate.",
                "payload_json", Json.Serialize(TestDict("conversationGrounding", TestDict("schema", "reign-conversation-grounding-v1", "status", "unresolved"))));
            string unverifiedBefore = Json.Serialize(unverified), renderedUnverified = RenderExactHistoryTurn(unverified, null);
            Check("unresolved_grounding_keeps_attributed_words_with_caveat", renderedUnverified.Contains("Corein: I have joined your party at the northern gate.")
                && renderedUnverified.Contains("unverified NPC statement; not independent factual evidence") && Json.Serialize(unverified) == unverifiedBefore);
            Check("checked_grounding_does_not_inherit_unresolved_label", !RenderExactHistoryTurn(new(unverified) {
                ["payload_json"] = Json.Serialize(TestDict("conversationGrounding", TestDict("status", "checked"))) }, null).Contains("unverified NPC statement"));
            try
            {
                WriteJsonObject(CharacterFile(campaign, "npc", "state.json"), state);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    void Session(string id, long ts) => ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($id,$campaign,'npc','player','closed',$ts,$ts,'[""npc"",""player""]','{""timelineId"":""recall_main""}');",
                        TestDict("id", id, "campaign", campaign, "ts", ts));
                    void Turn(string id, string session, int order, string exchange, string role, string text, double day, long ts,
                        string timeline = "recall_main", string audience = "[\"npc\",\"player\"]") => ExecuteSql(connection, @"INSERT INTO conversation_turns
(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES($id,$session,$id,$order,$exchange,$role,$speaker,$speaker,$text,$day,$ts,$audience,$payload);",
                        TestDict("id", id, "session", session, "order", order, "exchange", exchange, "role", role,
                            "speaker", role == "player" ? "player" : "npc", "text", text, "day", day, "ts", ts, "audience", audience,
                            "payload", Json.Serialize(TestDict("timelineId", timeline))));
                    Session("horses", 10); Session("spring", 20); Session("current", 90);
                    // Important details are separated by many exchanges in the
                    // same session, as in the alpha campaign. One nearby window
                    // cannot accidentally satisfy every recall question.
                    var originalOrders = new HashSet<int> { 6, 7, 18, 19, 20, 34, 35, 36, 46, 47, 52, 53 };
                    for (int i = 0; i < 60; i++)
                        if (!originalOrders.Contains(i)) Turn("aside_" + i, "horses", i, "aside_" + i / 2,
                            i % 2 == 0 ? "player" : "npc", i % 2 == 0 ? "We talked about the western road and the weather."
                                : string.Concat(Enumerable.Repeat("The rain caught in my armor. I rested my hands on the ground and considered the guards, the border, and our training. ", 12)), 10, 10);
                    Turn("horse", "horses", 6, "horse", "player", horse, 10, 10);
                    Turn("reaction", "horses", 7, "horse", "npc", longReaction, 10, 10);
                    Turn("frog", "horses", 18, "frog", "player", frog, 10, 10);
                    Turn("frog_reply", "horses", 19, "frog", "npc", longFrogReply, 10, 10);
                    Turn("frog_callback", "horses", 20, "aside_10", "player", "I laughed with you about that frog. He thinks all the frogs and lizards are the same one taunting him.", 10, 10);
                    Turn("gait_question", "horses", 34, "gait", "player", "Show me how your mare moves.", 10, 10);
                    Turn("gait", "horses", 35, "gait", "npc", longGait, 10, 10);
                    Turn("gait_callback", "horses", 36, "aside_18", "player", "Stone's gait is beautiful. I wait at the fence and watch her feet, just as you showed me.", 10, 10);
                    Turn("fence_question", "horses", 46, "fence", "player", "Where should I meet you after winter?", 10, 10);
                    Turn("fence", "horses", 47, "fence", "npc", longFence, 10, 10);
                    Turn("coining_question", "horses", 52, "coining", "player", "I call that Coreining.", 10, 10);
                    Turn("coining", "horses", 53, "coining", "npc", longCoining, 10, 10);
                    Turn("reunion", "spring", 1, "reunion", "player", reunion, 41, 20);
                    Turn("acceptance", "spring", 2, "reunion", "npc", acceptance, 41, 21);
                    for (int i = 0; i < 36; i++)
                    {
                        string id = "noise_" + i;
                        Session(id, 30 + i);
                        Turn(id + "_p", id, 1, id, "player", "We discussed the guards and border training. " + string.Concat(Enumerable.Repeat(scene, 6)), 42, 30 + i);
                        Turn(id + "_n", id, 2, id, "npc", "I laughed then too and put my hands behind my head. " + string.Concat(Enumerable.Repeat(scene, 8)), 42, 30 + i);
                    }
                    for (int i = 0; i < 12; i++)
                    {
                        string id = "failed_" + i;
                        Session(id, 70 + i);
                        Turn(id + "_p", id, 1, id, "player", "Do you remember Tempest and the frogs?", 44, 70 + i);
                        Turn(id + "_n", id, 2, id, "npc", "I don't have the page. Tell me again about your horse and the tournament.", 44, 70 + i);
                    }
                    Session("mixed", 85);
                    Turn("mixed_p", "mixed", 1, "mixed", "player", "I respect your boundary. We can sit on the ground and rest after training.", 44, 85);
                    Turn("mixed_n", "mixed", 2, "mixed", "npc", "Your horse Tempest and the frogs. Tell me where it happened and what he did. I do not have that page. I will wait while you tell me.", 44, 85);
                    Session("forbidden", 95);
                    Turn("future", "forbidden", 1, "future", "player", "Future forbidden Tempest frogs tournament were invented.", 99, 95);
                    Turn("sibling", "forbidden", 2, "sibling", "player", "Sibling forbidden Tempest frogs tournament were invented.", 44, 95, "abandoned_branch");
                    Turn("private", "forbidden", 3, "private", "player", "Private forbidden Tempest frogs tournament were invented.", 44, 95, audience: "[\"player\",\"other\"]");
                    Session("projection", 97);
                    Turn("projected_player", "projection", 1, "projected", "player", interpretedSpan, 44, 90);
                    Turn("projection_source", "projection", 0, "projected", "player", "Source-only forbidden private bronze bell intention.", 44, 90);
                    ExecuteSql(connection, "UPDATE conversation_turns SET role='player_source',status='source_only',participants_json='[\"player\"]' WHERE turn_id='projection_source';");
                    string projectedPayload = Json.Serialize(TestDict("timelineId", "recall_main", "playerNarrationProjectionVersion", 2, "inputSourceId", "projection_source"));
                    ExecuteSql(connection, "UPDATE conversation_turns SET payload_json=$payload WHERE turn_id IN ('projected_player','private');", TestDict("payload", projectedPayload));
                    ExecuteSql(connection, "INSERT INTO conversation_turn_fts(turn_id,text,speaker,participants,channel) SELECT turn_id,text,speaker_id,participants_json,channel FROM conversation_turns WHERE status='active';");
                    IndexMemoryRecallFixtureSources(campaign, connection, settings);
                    Check("automatic_topic_index_covers_every_source", ReadLong(QuerySql(connection,
                        "SELECT count(*) AS count FROM memory_specialist_sources;").Single(), "count", 0)
                        == ReadLong(QuerySql(connection, "SELECT count(*) AS count FROM conversation_turns WHERE status='active';").Single(), "count", 0));
                    var item = TestDict("ownerId", "npc", "claim", "She values the physical boundary.", "timelineId", "recall_main", "worldDay", 44d);
                    string bad = UpsertTemporalAssertion(connection, campaign, "failed_0_n", "comprehension_fixture", "comprehension", item, 80);
                    // Simulate a pre-fix stored assertion; do not use the guarded writer to seed corruption.
                    ExecuteSql(connection, "UPDATE temporal_knowledge_assertions SET claim=$claim WHERE assertion_id=$id;", TestDict("id", bad, "claim", absence));
                    var access = BuildKnowledgeAccessContext("npc", "player", "", context);
                    var projectedPlayer = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='projected_player';").Single();
                    Check("interpreted_player_mid_clause_survives_eligible_recall", PrecisionTurnVisible(projectedPlayer, "npc", access, connection)
                        && RenderExactHistoryTurn(projectedPlayer, access).Contains(interpretedSpan));
                    Check("interpreted_player_keeps_automatic_public_topic", TextListFromJson(ReadString(QuerySql(connection,
                        "SELECT public_topics_json FROM memory_specialist_sources WHERE turn_id='projected_player';").Single(), "public_topics_json", "[]")).Contains("botany"));
                    Check("interpreted_player_keeps_late_interaction_public_view", ReadString(LateInteractionView(projectedPlayer, "public"), "text", "").Contains(interpretedSpan));
                    Check("interpreted_player_keeps_commitment_source_words", CommitmentVisibleSourceText(projectedPlayer, access).Contains(interpretedSpan));
                    var sourceOnly = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='projection_source';").Single();
                    Check("source_only_original_has_no_public_or_self_index_view", LateInteractionView(sourceOnly, "public") == null
                        && LateInteractionView(sourceOnly, "self") == null && !HasValidatedPlayerSourceProjection(sourceOnly));
                    Check("inactive_interpreted_source_cannot_bypass_projection", !HasValidatedPlayerSourceProjection(new(projectedPlayer) { ["status"] = "inactive" }));
                    string projectionOriginalHash = MemorySpecialistSourceHash(projectedPlayer);
                    ExecuteSql(connection, "UPDATE memory_specialist_sources SET schema_version=$old,public_topics_json='[]',public_entities_json='[]' WHERE turn_id='projected_player';",
                        TestDict("old", MemorySpecialistSchema));
                    ReconcileMemorySpecialistIndex(campaign);
                    var rebuiltProjection = QuerySql(connection, "SELECT schema_version,public_topics_json FROM memory_specialist_sources WHERE turn_id='projected_player';").Single();
                    Check("old_projection_index_automatically_rebuilds_unchanged_original", ReadString(rebuiltProjection, "schema_version", "") == MemorySpecialistIndexSchema
                        && TextListFromJson(ReadString(rebuiltProjection, "public_topics_json", "[]")).Contains("botany")
                        && MemorySpecialistSourceHash(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='projected_player';").Single()) == projectionOriginalHash);
                    Check("legacy_player_row_keeps_complete_narration_without_interpretation_binding", RenderExactHistoryTurn(new(projectedPlayer) {
                        ["payload_json"] = Json.Serialize(TestDict("playerNarrationProjectionVersion", 2)) }, access).Contains("balance the bronze bell"));
                    Check("npc_cannot_use_player_projection_bypass", !RenderExactHistoryTurn(new(projectedPlayer) {
                        ["role"] = "npc", ["speaker_id"] = "other", ["text"] = "*I remember the private spring oath.* Hello." }, access).Contains("private spring oath"));
                    Check("interpreted_player_still_obeys_audience", !PrecisionTurnVisible(QuerySql(connection,
                        "SELECT * FROM conversation_turns WHERE turn_id='private';").Single(), "npc", access, connection));
                    var projectionHits = FindLexicalMemoryRecallTurns(connection,
                        PlanMemoryQuery("npc", "player", "What happened with the bronze bell?", context), access, MemoryRecallLexicalLimit);
                    Check("source_only_original_unindexed_and_unavailable", projectionHits.Any(row => ReadString(row, "turn_id", "") == "projected_player")
                        && projectionHits.All(row => ReadString(row, "turn_id", "") != "projection_source")
                        && QuerySql(connection, "SELECT turn_id FROM memory_specialist_sources WHERE turn_id='projection_source';").Count == 0);
                    var wholeAnchor = RenderExactConversationAnchor(connection, "npc", QuerySql(connection,
                        "SELECT * FROM conversation_turns WHERE turn_id='horse';").Single(), new() { "Tempest" }, 600, access, true);
                    Check("shared_window_budget_keeps_complete_anchor_pair", ReadString(wholeAnchor, "text", "").Contains(horse)
                        && ReadString(wholeAnchor, "text", "").Contains(longReaction), TestDict("horse", ReadString(wholeAnchor, "text", "").Contains(horse),
                            "reaction", ReadString(wholeAnchor, "text", "").Contains(longReaction), "expandedTurnIds", ReadStringList(wholeAnchor, "expandedTurnIds")));
                    var callback = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='gait_callback';").Single();
                    var preceding = RenderExactConversationAnchor(connection, "npc", callback, new() { "gait" }, 600, access, true);
                    Check("long_callback_keeps_preceding_npc_observation", ReadString(preceding, "text", "").Contains(longGait));
                    var covered = ReadStringList(preceding, "expandedTurnIds").ToHashSet(StringComparer.Ordinal);
                    var completedPair = RenderExactConversationAnchor(connection, "npc", QuerySql(connection,
                        "SELECT * FROM conversation_turns WHERE turn_id='gait';").Single(), new() { "gait" }, 600, access, true, covered);
                    Check("overlapping_anchor_adds_missing_partner_without_duplicate", covered.Contains("gait") && !covered.Contains("gait_question")
                        && ReadString(completedPair, "text", "").Contains("Show me how your mare moves.")
                        && !ReadString(completedPair, "text", "").Contains(longGait));
                    var earlier = BuildKnowledgeAccessContext("npc", "player", "", new(context) { ["memoryAsOfTimestamp"] = 9L });
                    Check("preceding_context_cannot_cross_time_boundary", ExpandMemoryRecallPrecedingTurn(connection, new() { callback }, earlier).Count == 1);
                    Session("context_gate", 96);
                    Turn("context_visible", "context_gate", 0, "context_old", "npc", "An older visible observation.", 44, 90);
                    Turn("context_hidden", "context_gate", 1, "context_private", "npc", "An unheard private observation.", 44, 91, audience: "[\"other\",\"player\"]");
                    ExecuteSql(connection, "UPDATE conversation_turns SET speaker_id='other' WHERE turn_id='context_hidden';");
                    Turn("context_answer", "context_gate", 2, "context_answer", "player", "I heard that.", 44, 92);
                    var contextAnswer = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='context_answer';").Single();
                    Check("preceding_context_does_not_skip_unheard_neighbor", ExpandMemoryRecallPrecedingTurn(connection, new() { contextAnswer }, access).Count == 1);
                    Check("actual_temporal_loader_rejects_old_absence", !LoadTemporalKnowledgeForPrompt(connection, campaign, access, new() { "Tempest" }, 20).Any(r => ReadString(r, "assertion_id", "") == bad));
                    Check("assertion_writer_rejects_absence", UpsertTemporalAssertion(connection, campaign, "failed_0_n", "new", "interpretation", new(item) { ["claim"] = absence }, 81) == "");
                    var clean = WithoutRecallAbsenceState(state);
                    Check("gerund_and_lost_memory_quarantined", ReadString(clean, "mood", "") == "" && ReadString(clean, "currentPlan", "") == ""
                        && ReadString(clean, "currentCrisis", "") == "Protect the political confidence; preserve the physical boundary"
                        && ReadString(state, "currentPlan", "").Contains("recovering"));
                    foreach (string wording in new[] { "She may have lost memories of the horse.", "Continue reconstructing their memories.", "The memory is missing.", "She cannot recall the horse." })
                    {
                        var rejected = new List<Dictionary<string, object>>();
                        Check("persistent_absence_variant_" + results.Count, FilterRoleplayFeedbackWrites(new() { TestDict("claim", wording) }, "belief", rejected).Count == 0
                            && NormalizeSafeConversationStateUpdates(TestDict("currentPlan", wording), rejected).Count == 0);
                    }
                    Check("unrelated_memory_and_negative_facts_preserved", !IsUnsupportedRecallAbsenceState("Protect the memory of my father; no supplies remain; remember the promise."));
                    Session("loss_event", 82);
                    const string establishedLoss = "You lost your memories after a blow to your head.";
                    const string lossClaim = "I lost my memories after the blow to my head.";
                    Turn("loss_player", "loss_event", 0, "loss_event", "player", establishedLoss, 43, 82);
                    Turn("loss_npc", "loss_event", 1, "loss_event", "npc", "The physician is attending me.", 43, 83);
                    string lossBefore = ReadString(QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id='loss_player';").Single(), "text", "");
                    Check("established_loss_source_preserves_derived_memory", !IsUnsupportedRecallAbsenceDerived(
                        TestDict("memory_id", "supported_loss", "summary", lossClaim), connection, access));
                    Check("established_loss_preserves_state_and_writes", ReadString(WithoutRecallAbsenceState(TestDict("currentCrisis", lossClaim),
                        campaign, "npc", context), "currentCrisis", "") == lossClaim
                        && NormalizeSafeConversationStateUpdates(TestDict("currentCrisis", lossClaim), new(), context, "npc", campaign).Count == 1
                        && FilterRoleplayFeedbackWrites(new() { TestDict("text", lossClaim) }, "memory", new(), context, "npc", campaign).Count == 1);
                    Check("different_cause_cannot_inherit_loss_exception", !HasStoredRecallLossSupport(connection, access, "I have memory loss after the fever."));
                    var beforeLoss = BuildKnowledgeAccessContext("npc", "player", "", new(context) { ["worldDay"] = 42d });
                    Check("future_loss_event_is_not_support", !HasStoredRecallLossSupport(connection, beforeLoss, lossClaim));
                    var otherObserver = BuildKnowledgeAccessContext("other", "player", "", context);
                    Check("unheard_loss_event_is_not_support", !HasStoredRecallLossSupport(connection, otherObserver, lossClaim));
                    foreach (string unsupported in new[] { "Korin lost his memories after a blow to his head.",
                        "Did you lose your memories after a blow to your head?", "You have not lost your memories after a blow to your head." })
                    {
                        ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='loss_player';", TestDict("text", unsupported));
                        Check("unrelated_hypothetical_or_negated_loss_source_" + results.Count, !HasStoredRecallLossSupport(connection, access, lossClaim), unsupported);
                    }
                    ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='loss_player';", TestDict("text", lossBefore));
                    var supportedMental = TestDict("ownerId", "npc", "subjectId", "npc", "claim", lossClaim, "worldDay", 45d, "timelineId", "recall_main");
                    Check("established_loss_temporal_write_preserved", UpsertTemporalAssertion(connection, campaign, "loss_player", "loss", "interpretation", supportedMental, 84).Length > 0);
                    var fact = TestDict("ownerId", "npc", "subjectId", "player", "predicate", "stable fee", "cardinality", "one", "timelineId", "recall_main", "worldDay", 10d, "claim", "The stable fee is 20 denars.");
                    string before = UpsertTemporalAssertion(connection, campaign, "horse", "fee", "reported", fact, 10);
                    fact["worldDay"] = 43d; fact["claim"] = "The stable fee is 15 denars.";
                    string after = UpsertTemporalAssertion(connection, campaign, "reunion", "fee", "reported", fact, 75);
                    var current = LoadTemporalKnowledgeForPrompt(connection, campaign, access, new() { "stable" }, 20);
                    Check("latest_revision_only_for_same_fact", current.Any(r => ReadString(r, "assertion_id", "") == after)
                        && current.All(r => ReadString(r, "assertion_id", "") != before));
                    var historical = BuildKnowledgeAccessContext("npc", "player", "", new(context) { ["worldDay"] = 20d });
                    Check("historical_revision_before_correction", LoadTemporalKnowledgeForPrompt(connection, campaign, historical, new() { "stable" }, 20)
                        .Any(r => ReadString(r, "assertion_id", "") == before));
                }

                var queries = new[] {
                    (Id: "horse_name", Text: "Do you remember Tempest, my horse?", Required: new[] { horse, longReaction }),
                    (Id: "horse_story", Text: "Do you remember Tempest, my horse, and what happened with the frogs?", Required: new[] { horse, longReaction, frog, longFrogReply }),
                    (Id: "silent_callback", Text: "*I imitate Tempest snorting at a frog in the stream and running a full circle.*", Required: new[] { frog, longFrogReply }),
                    (Id: "frog", Text: "*I put my hands up behind my head to prop it off the ground* You laughed then too. About his neverending battle with the frogs.", Required: new[] { frog, longFrogReply }),
                    (Id: "tourney", Text: "He also considers lizards in the same way sometimes. You showed me how Stone shifts her gait and told me to find you waiting at the fence when the season turned. I waited all winter and returned in the first days of spring. I found you in a tournament if you recall, not the stable.", Required: new[] { horse, longGait, longFence, reunion, acceptance }),
                    (Id: "hand", Text: "How did I find you at the tourney on the second or third day of spring, and what did I do when you fell?", Required: new[] { reunion, acceptance }),
                    (Id: "invitation", Text: "When I came back after the season turned, where had you told me to find you first? What did you want me to bring?", Required: new[] { longFence }),
                    (Id: "coining", Text: "What did we mean by Coreining, and who made that word?", Required: new[] { longCoining })
                };
                foreach (string mode in new[] { "legacy", "precision" })
                foreach (var query in queries)
                {
                    settings["memoryPrecisionMode"] = mode;
                    var callContext = new Dictionary<string, object>(context) { ["memoryQueryText"] = query.Text };
                    var packet = BuildNpcMemoryPacket(campaign, "npc", "player", "", query.Text + "\n" + string.Concat(Enumerable.Repeat(scene, 40)), 2500, callContext, settings);
                    string memory = ReadString(packet, "memoryPacket", "");
                    Check("originals_selected_" + mode + "_" + query.Id, query.Required.All(memory.Contains), TestDict("horse", memory.Contains(horse), "frog", memory.Contains(frog),
                            "reaction", memory.Contains(reaction), "reunion", memory.Contains(reunion), "acceptance", memory.Contains(acceptance),
                            "requiredMatches", query.Required.Select(memory.Contains).ToArray(),
                            "retrieval", ReadDictionary(packet, "recallAudit") ?? ReadDictionary(packet, "precisionEvidence")));
                    Check("forbidden_and_derived_absence_excluded_" + mode + "_" + query.Id, !memory.Contains("forbidden") && !memory.Contains(absence));
                    foreach (string route in new[] { "in_person", "party_chat", "social_event", "correspondence" })
                    {
                        var payload = PromptParityPayload(route, false); foreach (var pair in callContext) payload[pair.Key] = pair.Value;
                        payload["mode"] = route; payload["playerText"] = query.Text;
                        payload["memoryPrecisionEvidence"] = ReadDictionary(packet, "precisionEvidence");
                        var profile = PromptParityProfile(); profile["heroStringId"] = "npc";
                        var empty = TestDict(); var lines = new List<Dictionary<string, object>>();
                        var envelope = route == "correspondence"
                            ? BuildCorrespondencePromptEnvelope(campaign, "npc", "Corein", "player", "Player", 45d, query.Text, profile, PromptParityCharacteristics(), empty, memory, payload)
                            : route == "in_person"
                                ? BuildDialoguePromptEnvelope(campaign, "npc", "Corein", "Player", "Player", query.Text, scene, profile, PromptParityCharacteristics(), state, empty, empty, lines, lines, lines, lines, PromptParityIdentity(), payload, memory, "")
                                : BuildEventPromptEnvelope(campaign, "recall", "npc", "Corein", "Player", "Player", query.Text, scene, payload, profile, PromptParityCharacteristics(), state, empty, empty, lines, lines, lines, lines, PromptParityIdentity(), memory, "");
                        var captured = CaptureMemoryReplayProviderRequest(TestDict("requestType", route == "correspondence" ? "correspondence" : "dialogue",
                            "model", "recall-fixture", "messages", envelope.Messages, "promptEnvelope", envelope.Diagnostics), settings);
                        string wire = ReadString(ReadDictionary(captured, "providerWireCapture"), "requestJson", "");
                        Check("ordinary_recall_authority_in_system_" + route + "_" + mode + "_" + query.Id,
                            ReadDictionaryList(TryParseJsonObject(wire), "messages").Any(message => ReadString(message, "role", "") == "system"
                                && ReadString(message, "content", "").Contains(MemoryRecallAuthorityContract)));
                        Check("delivered_originals_arm_runtime_guard_" + route + "_" + mode + "_" + query.Id,
                            ReadInt(ReadDictionary(envelope.Diagnostics, "memoryRecallGrounding"), "sourceCount", 0) > 0
                            && FindDeliveredMemoryRecallViolations(TestDict("reply", "I have a memory gap about those days."), payload, "npc").Count > 0,
                            ReadDictionary(envelope.Diagnostics, "memoryRecallGrounding"));
                        string Content(string value) => Json.Serialize(value).Trim('"');
                        Check("provider_content_" + route + "_" + mode + "_" + query.Id, wire.Length > 0
                            && query.Required.All(value => wire.Contains(Content(value)))
                            && wire.Contains("RECALL SOURCE PRECEDENCE") && !wire.Contains("Continue recovering the Tempest memory") && !wire.Contains(absence),
                            TestDict("characters", wire.Length, "requiredMatches", query.Required.Select(value => wire.Contains(Content(value))).ToArray(),
                                "sourcePrecedence", wire.Contains("RECALL SOURCE PRECEDENCE"), "recoveryPlanAbsent", !wire.Contains("Continue recovering the Tempest memory"),
                                "derivedAbsenceAbsent", !wire.Contains(absence), "requestSha256", ReadString(ReadDictionary(captured, "providerWireCapture"), "requestSha256", "")));
                    }
                }
                using (var connection = OpenCampaignConnection(campaign))
                {
                    Check("originals_and_failed_speech_unchanged", ReadString(QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id='horse';").Single(), "text", "") == horse
                        && ReadString(QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id='failed_0_n';").Single(), "text", "").Contains("I don't have the page"));
                }
                results.AddRange(RunTemporalRecallRevisionAssertions());
                results.AddRange(RunMemoryRecallGrowthAssertions());
            }
            catch (Exception ex) { Check("suite_exception", false, ex.ToString()); results.Last()["summary"] = LimitText(ex.ToString(), 2200); }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
                TryDeleteDirectory(StrictCampaignDirectory(campaign));
            }
            return results;
        }

        private static List<Dictionary<string, object>> RunMemoryRecallGrowthAssertions()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)) throw new InvalidOperationException("Recall growth requires isolated storage.");
            var results = new List<Dictionary<string, object>>();
            string campaign = "memory_recall_growth_" + Guid.NewGuid().ToString("N");
            const string identity = "My mule is called Brindle. He is fascinated by crabs, but frightened when they scuttle sideways.";
            const string incident = "At the watermill Brindle tried to smell a crab. It pinched his nose; he backed into a barrel, then refused to approach the millpond.";
            const string response = "I laughed at Brindle backing into that barrel. A crab was a formidable enemy for your mule.";
            const string appointment = "Meet me at the north bridge when the thaw comes, and bring the bronze bell we repaired together.";
            var settings = TestDict("enableSemanticMemory", false, "minimeEnabled", false, "memoryPrecisionMode", "legacy", "useMemoryLlmForConsolidation", false);
            var context = TestDict("timelineId", "growth_main", "worldDay", 1000d, "playerHeroStringId", "player",
                "conversationSessionId", "new_session", "deferContinuityAllocation", true, "officialMemoryFirewall", false);
            var cases = new[] {
                (Id: "renamed_animal", Query: "Do you remember Brindle, my mule, and the crab at the watermill?", Required: new[] { identity, incident, response }),
                (Id: "unnamed_callback", Query: "*I put my hands behind my head on the ground.* You laughed when he backed into the barrel. What had happened with that crab?", Required: new[] { incident, response }),
                (Id: "different_episode", Query: "Where had we agreed to meet when the thaw came, and what was I supposed to bring?", Required: new[] { appointment })
            };
            try
            {
                using (var connection = OpenCampaignConnection(campaign))
                {
                    void Session(string id, int day) => ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($id,$campaign,'npc','player','closed',$day,$day,'[""npc"",""player""]','{""timelineId"":""growth_main""}');", TestDict("id", id, "campaign", campaign, "day", day));
                    void Turn(string session, int order, string role, string text, int day) => ExecuteSql(connection, @"INSERT INTO conversation_turns
(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES($id,$session,$id,$order,$exchange,$role,$speaker,$speaker,$text,$day,$day,'[""npc"",""player""]','{""timelineId"":""growth_main""}');",
                        TestDict("id", session + "_" + order, "session", session, "order", order, "exchange", session + "_e" + order / 2,
                            "role", role, "speaker", role == "player" ? "player" : "npc", "text", text, "day", day));
                    Session("old", 1);
                    for (int i = 0; i < 80; i++)
                        Turn("old", i, i % 2 == 0 ? "player" : "npc", i switch {
                            4 => identity, 5 => "Brindle is an amusing name for a mule.", 30 => incident, 31 => response,
                            60 => "When I return after the snow has gone, where should I find you?", 61 => appointment,
                            _ => i % 2 == 0 ? "The road west is passable. We spoke about the weather."
                                : string.Concat(Enumerable.Repeat("I leaned on the wooden rail, thinking about the road and the garrison. The wind caught my cloak. ", 12))
                        }, 1);
                    string oldHash = MemorySourceHash(QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='old' ORDER BY turn_id;"));
                    int added = 0;
                    foreach (int historySize in new[] { 0, 40, 160, 400 })
                    {
                        while (added < historySize)
                        {
                            int number = added++; string session = "later_" + number; Session(session, number + 2);
                            // Varied long narration overlaps generic scene terms.
                            // Mixed failed-recall sessions contain genuine new
                            // dialogue and therefore cannot simply be discarded.
                            Turn(session, 0, "player", number % 9 == 0
                                ? "I respect your boundary. We can rest after training. Do you remember Brindle and the crab?"
                                : "The patrol has returned. I brought the supplies and the report from the western road.", number + 2);
                            Turn(session, 1, "npc", number % 9 == 0
                                ? "Your mule Brindle and that crab. Tell me the story again so I can reconstruct it. I can wait here."
                                : string.Concat(Enumerable.Repeat("I set my hands on the ground and laughed. The guards had come back from the bridge, carrying tools from the watermill, and the season was turning. ", 3 + number % 17)), number + 2);
                        }
                        IndexMemoryRecallFixtureSources(campaign, connection, settings);
                        foreach (string mode in new[] { "legacy", "precision" })
                        foreach (var item in cases)
                        {
                            settings["memoryPrecisionMode"] = mode;
                            var callContext = new Dictionary<string, object>(context) { ["memoryQueryText"] = item.Query };
                            var packet = BuildNpcMemoryPacket(campaign, "npc", "player", "", item.Query, 2500, callContext, settings);
                            string memory = ReadString(packet, "memoryPacket", "");
                            var payload = PromptParityPayload("in_person", false); foreach (var pair in callContext) payload[pair.Key] = pair.Value;
                            payload["playerText"] = item.Query; payload["memoryPrecisionEvidence"] = ReadDictionary(packet, "precisionEvidence");
                            var profile = PromptParityProfile(); profile["heroStringId"] = "npc";
                            var empty = TestDict(); var lines = new List<Dictionary<string, object>>();
                            var envelope = BuildDialoguePromptEnvelope(campaign, "npc", "Aster", "Player", "Player", item.Query, "Current scene: garrison courtyard.",
                                profile, PromptParityCharacteristics(), empty, empty, empty, lines, lines, lines, lines, PromptParityIdentity(), payload, memory, "");
                            var capture = CaptureMemoryReplayProviderRequest(TestDict("requestType", "dialogue", "model", "recall-fixture",
                                "messages", envelope.Messages, "promptEnvelope", envelope.Diagnostics), settings);
                            string wire = ReadString(ReadDictionary(capture, "providerWireCapture"), "requestJson", "");
                            var recallAudit = ReadDictionary(packet, "recallAudit");
                            int retained = mode == "legacy" ? ReadInt(recallAudit, "retainedCandidateCount", 0)
                                : ReadInt(ReadDictionary(packet, "precisionEvidence"), "retainedRecallCandidates", 0);
                            bool bounded = retained <= MemoryRecallLexicalLimit + MemoryRecallSupplementLimit;
                            bool passed = bounded && item.Required.All(value => wire.Contains(Json.Serialize(value).Trim('"')));
                            results.Add(TestDict("caseId", "memory_recall_growth_" + historySize + "_" + mode + "_" + item.Id,
                                "passed", passed, "summary", passed ? "Original answer survives appended history in the provider body."
                                    : "Missing originals: " + string.Join(" | ", item.Required.Where(value => !wire.Contains(Json.Serialize(value).Trim('"')))),
                                "data", TestDict("laterConversations", historySize, "mode", mode, "requestCharacters", wire.Length,
                                    "originalsInPacket", item.Required.All(memory.Contains), "boundedRecallCandidates", bounded,
                                    "retainedCandidates", retained, "retrieval", passed ? null : recallAudit)));
                        }
                    }
                    results.Add(TestDict("caseId", "memory_recall_growth_original_hash_unchanged", "passed", oldHash == MemorySourceHash(QuerySql(connection,
                        "SELECT * FROM conversation_turns WHERE session_id='old' ORDER BY turn_id;")), "summary", "History growth and recall do not rewrite originals."));
                }
            }
            catch (Exception ex) { results.Add(TestDict("caseId", "memory_recall_growth_exception", "passed", false, "summary", LimitText(ex.ToString(), 2200))); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); TryDeleteDirectory(StrictCampaignDirectory(campaign)); }
            return results;
        }

        private static List<Dictionary<string, object>> RunTemporalRecallRevisionAssertions()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)) throw new InvalidOperationException("Temporal recall requires isolated storage.");
            var results = new List<Dictionary<string, object>>();
            void Check(string name, bool passed) => results.Add(TestDict("caseId", "memory_recall_revision_" + name, "passed", passed, "summary", name));
            string campaign = "memory_recall_revision_" + Guid.NewGuid().ToString("N");
            string originalId = "", episodeId = "";
            var point = TestDict("savePointId", "before", "timelineId", "parent", "campaignTimeDays", 20d,
                "worldHistorySequence", 8L, "worldHistoryHeadEventId", "saved_head");
            var context = TestDict("timelineId", "child", "worldDay", 25d, "playerHeroStringId", "player");
            try
            {
                void Source(ReignDbConnection connection, string id, string timeline, double day, long ts, long sequence, string audience = "[\"npc\",\"player\"]") =>
                    ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,accepted_sequence,participants_json,payload_json)
VALUES($id,'history',$id,$sequence,$id,'player','player',$id,$day,$ts,$sequence,$audience,$payload);",
                        TestDict("id", id, "sequence", sequence, "day", day, "ts", ts, "audience", audience, "payload", Json.Serialize(TestDict("timelineId", timeline))));
                Dictionary<string, object> Fact(string claim, string timeline, double day) => TestDict("ownerId", "npc", "subjectId", "player",
                    "predicate", "stable fee", "cardinality", "one", "timelineId", timeline, "worldDay", day, "claim", claim);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('history',$campaign,'npc','player','closed',1,2,'[""npc"",""player""]','{""timelineId"":""parent""}');", TestDict("campaign", campaign));
                    Source(connection, "parent_fact", "parent", 10, 10, 1);
                    Source(connection, "parent_episode", "parent", 11, 11, 2);
                    originalId = UpsertTemporalAssertion(connection, campaign, "parent_fact", "fee", "reported", Fact("The stable fee is twenty denars.", "parent", 10), 10);
                    episodeId = UpsertTemporalAssertion(connection, campaign, "parent_episode", "episode", "reported",
                        TestDict("ownerId", "npc", "subjectId", "npc", "predicate", "spring reunion", "timelineId", "parent", "worldDay", 11d,
                            "claim", "At the spring tournament I took your hand."), 11);
                }
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "before");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "before");
                AdvanceMemoryRestoreGeneration(campaign, point);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    BindRecoveredMemorySources(connection, "parent", "child", 8, "saved_head");
                    var access = BuildKnowledgeAccessContext("npc", "player", "", context);
                    List<Dictionary<string, object>> Read(string query, KnowledgeAccessContext boundary = null) =>
                        LoadTemporalKnowledgeForPrompt(connection, campaign, boundary ?? access, new() { query }, 20);
                    Check("verified_parent_fact_recalled", Read("stable").Any(r => ReadString(r, "assertion_id", "") == originalId));
                    var originalRow = QuerySql(connection, "SELECT * FROM temporal_knowledge_assertions WHERE assertion_id=$id;", TestDict("id", originalId)).Single();
                    string originalHash = MemorySourceHash(new[] { originalRow });
                    Source(connection, "child_fact", "child", 21, 50, 3);
                    string correction = UpsertTemporalAssertion(connection, campaign, "child_fact", "fee", "reported", Fact("The stable fee is fifteen denars.", "child", 21), 50);
                    Check("child_correction_replaces_restored_parent", Read("stable").Count == 1 && ReadString(Read("stable").Single(), "assertion_id", "") == correction);
                    Check("old_wording_resolves_latest_value", Read("twenty").Count == 1 && ReadString(Read("twenty").Single(), "assertion_id", "") == correction);
                    Check("unrelated_old_episode_survives", Read("tournament").Any(r => ReadString(r, "assertion_id", "") == episodeId));
                    var pastDay = BuildKnowledgeAccessContext("npc", "player", "", new(context) { ["worldDay"] = 20d });
                    var pastTime = BuildKnowledgeAccessContext("npc", "player", "", new(context) { ["memoryAsOfTimestamp"] = 49L });
                    var pastSequence = BuildKnowledgeAccessContext("npc", "player", "", context); pastSequence.AsOfAcceptedSequence = 2;
                    foreach (var boundary in new[] { ("day", pastDay), ("timestamp", pastTime), ("sequence", pastSequence) })
                        Check("asof_" + boundary.Item1 + "_retains_parent", Read("stable", boundary.Item2).Count == 1
                            && ReadString(Read("stable", boundary.Item2).Single(), "assertion_id", "") == originalId);
                    Source(connection, "sibling_fact", "sibling", 23, 60, 4);
                    UpsertTemporalAssertion(connection, campaign, "sibling_fact", "fee", "reported", Fact("The stable fee is nine hundred denars.", "sibling", 23), 60);
                    Check("sibling_cannot_override", Read("stable").Count == 1 && ReadString(Read("stable").Single(), "assertion_id", "") == correction);
                    Source(connection, "hidden_fact", "child", 22, 61, 5);
                    var hidden = Fact("The stable fee is hidden gold.", "child", 22); hidden["hiddenFrom"] = new[] { "npc" };
                    UpsertTemporalAssertion(connection, campaign, "hidden_fact", "fee", "reported", hidden, 61);
                    Check("hidden_revision_cannot_mask_known_value", Read("stable").Count == 1 && ReadString(Read("stable").Single(), "assertion_id", "") == correction);
                    Source(connection, "future_fact", "child", 30, 62, 6);
                    UpsertTemporalAssertion(connection, campaign, "future_fact", "fee", "reported", Fact("The stable fee is future silver.", "child", 30), 62);
                    Check("future_revision_cannot_override", Read("stable").Count == 1 && ReadString(Read("stable").Single(), "assertion_id", "") == correction);
                    // The derivative's date can be stale; its original accepted source
                    // must still enforce the sequence/time and audience boundaries.
                    Source(connection, "private_fact", "child", 24, 63, 7, "[\"player\",\"other\"]");
                    UpsertTemporalAssertion(connection, campaign, "private_fact", "fee", "reported", Fact("The stable fee is secret copper.", "child", 24), 63);
                    Check("source_audience_cannot_leak", Read("stable").All(r => !ReadString(r, "claim", "").Contains("secret copper")));
                    Check("parent_snapshot_row_unchanged", originalHash == MemorySourceHash(QuerySql(connection,
                        "SELECT * FROM temporal_knowledge_assertions WHERE assertion_id=$id;", TestDict("id", originalId))));
                }
            }
            catch (Exception ex) { results.Add(TestDict("caseId", "memory_recall_revision_exception", "passed", false, "summary", LimitText(ex.ToString(), 2200))); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); TryDeleteDirectory(StrictCampaignDirectory(campaign)); }
            return results;
        }
    }
}
