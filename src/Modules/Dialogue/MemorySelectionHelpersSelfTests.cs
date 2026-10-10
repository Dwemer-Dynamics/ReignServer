using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunMemorySelectionHelperSelfTests()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
                throw new InvalidOperationException("Selection contracts require an isolated verifier.");
            var results = new List<Dictionary<string, object>>();
            Dictionary<string, object> Coverage(string text) => TestDict("sourceCoverage", "complete", "windowCount", 1,
                "maxWindowTokens", 100, "sourceSpansUtf16", new object[] { new[] { 0, text.Length } });
            void Check(string id, bool pass, object data = null) => results.Add(TestDict("caseId", "memory_selection_" + id,
                "passed", pass, "summary", id, "data", data));
            var plan = PlanMemoryQuery("observer", "player", "Continue our discussion of the caravan", TestDict());
            var recentContext = TestDict("memoryEvidenceRecentTurns", new[] {
                TestDict("speakerId", "npc", "text", "The tournament bracket is ready."),
                TestDict("speakerId", "player", "text", "I will meet you there.") });
            var changedTopic = PlanMemoryQuery("observer", "player", "What is this plant in my boot?", TestDict());
            string focusedQuery = BuildMemorySelectionQuery(changedTopic, recentContext);
            Check("current_topic_is_target_with_prior_references_retained", focusedQuery.EndsWith("CURRENT CONTRIBUTION:\nWhat is this plant in my boot?")
                && focusedQuery.Contains("PRIOR npc: The tournament bracket is ready.")
                && focusedQuery.IndexOf("PRIOR npc:", StringComparison.Ordinal) < focusedQuery.IndexOf("PRIOR player:", StringComparison.Ordinal));
            var shortAnswer = PlanMemoryQuery("observer", "player", "Yes, that one.", TestDict());
            Check("short_answer_keeps_reference_context", BuildMemorySelectionQuery(shortAnswer, recentContext).Contains("tournament bracket")
                && BuildMemorySelectionQuery(shortAnswer, recentContext).EndsWith("Yes, that one."));
            Check("transport_hash_preserves_whitespace", MemorySelectionTextHash("NPC: No.  \r\nPlayer: Agreed.")
                != MemorySelectionTextHash("NPC: No.\nPlayer: Agreed.")
                && PromptHash("NPC: No.  \r\nPlayer: Agreed.") == PromptHash("NPC: No.\nPlayer: Agreed."));
            var availableViews = TestDict("selectionHelpers", TestDict("compressionViewsPrepared", true, "compressionApplied", false));
            var unchangedPacket = new MemoryEvidencePacket();
            FinalizeMemorySelectionAudit(availableViews, unchangedPacket, false);
            Check("unused_compact_views_do_not_claim_compression_applied", !ReadBool(ReadDictionary(availableViews, "selectionHelpers"), "compressionApplied", true));
            unchangedPacket.Packing.Add(TestDict("sourceId", "compressed"));
            FinalizeMemorySelectionAudit(availableViews, unchangedPacket, false);
            Check("selected_compact_view_records_actual_use", ReadBool(ReadDictionary(availableViews, "selectionHelpers"), "compressionApplied", false));
            var source = TestDict("turn_id", "old", "speaker_id", "npc", "speaker_name", "Speaker", "role", "npc",
                "text", "*She smiles as she remembers the secret.* We agreed 300 only after arrival.");
            var knowledge = new KnowledgeAccessContext { NpcId = "observer" };
            string projected = ObserverConversationText(ReadString(source, "text", ""), "npc", "observer");
            var transcript = TestDict();
            SetMemoryTranscriptContext(transcript, new() { TestDict("turnId", "old", "speakerId", "npc", "role", "npc", "text", projected) });
            var projectedPlan = PlanMemoryQuery("observer", "player", "caravan", transcript);
            var turn = TestDict("id", "old", "hash", MemoryTranscriptSourceHash(source),
                "viewHash", MemoryProjectedTranscriptHash(source, knowledge), "text", RenderExactHistoryTurn(source, knowledge));
            var record = new MemoryEvidence { SourceId = "old", SourceHash = "canonical", Kind = "reported_exchange", Required = true,
                Text = ReadString(turn, "text", ""), SourceTurns = new() { turn } };
            Check("projected_transcript_dedup_preserves_canonical_source", MemoryTranscriptSourceHash(source) != ReadString(turn, "viewHash", "")
                && SelectPrecisionEvidence(new[] { record }, 4000, projectedPlan.SuppliedSources).Selected.Count == 0);
            projectedPlan.SuppliedSources["old"] = PromptHash("npc\nother_speaker\n" + projected);
            Check("changed_speaker_cannot_hide_evidence", SelectPrecisionEvidence(new[] { record }, 4000, projectedPlan.SuppliedSources).Selected.Count == 1);
            projectedPlan.SuppliedSources["old"] = PromptHash("npc\nnpc\nWe agreed 900.");
            Check("changed_view_cannot_hide_correction", SelectPrecisionEvidence(new[] { record }, 4000, projectedPlan.SuppliedSources).Text.Contains("300 only after arrival"));
            var protectedRecord = new MemoryEvidence { SourceId = "protected", Text = new string('p', 11000), Required = true };
            var answer = new MemoryEvidence { SourceId = "answer", Text = "Player answered: 300 only after arrival. " + new string('a', 5300), Score = 80 };
            var reserved = SelectPrecisionEvidence(new[] { protectedRecord, answer }, 4000);
            Check("moderate_protected_window_leaves_older_answer_allowance", reserved.ProtectedTokens < 8000
                && reserved.Selected.Any(e => e.SourceId == "answer") && reserved.OrdinaryTokens <= 2000,
                TestDict("protected", reserved.ProtectedTokens, "ordinary", reserved.OrdinaryTokens, "budget", reserved.Budget));

            // The protected-history growth step may reserve 8k. Unused space in
            // that reservation must not silently enlarge a configured 2k limit.
            foreach (int floorCharacters in new[] { 6500, 9000, 14000 })
            foreach (bool ranked in new[] { false, true })
            {
                var moderateFloor = new MemoryEvidence { SourceId = "moderate_floor", Required = true,
                    Text = new string('p', floorCharacters) };
                var overSmallBudget = new MemoryEvidence { SourceId = "over_small_budget", Score = 90,
                    Text = "An older complete exchange. " + new string('o', 7200),
                    SelectionRank = ranked ? 0 : null, SelectionRelevance = ranked ? .7d : null };
                var smallFallback = new MemoryEvidence { SourceId = "small_fallback", Score = 80,
                    Text = "The later correction remains available. " + new string('s', 1300),
                    SelectionRank = ranked ? 1 : null, SelectionRelevance = ranked ? .6d : null };
                var bounded = SelectPrecisionEvidence(new[] { moderateFloor, overSmallBudget, smallFallback }, 2000);
                int largeTokens = Math.Max(1, EstimateContinuityTokens(RenderMemoryEvidence(overSmallBudget, "m2")) - 128);
                Check("moderate_floor_respects_small_ordinary_budget_" + floorCharacters + "_" + ranked,
                    largeTokens > 2000 && bounded.ProtectedTokens > 2000 && bounded.ProtectedTokens < 6000
                    && bounded.Text.Contains(moderateFloor.Text) && bounded.Text.Contains(smallFallback.Text)
                    && !bounded.Text.Contains(overSmallBudget.Text) && bounded.OrdinaryTokens <= 2000
                    && bounded.Budget <= bounded.ProtectedTokens + 2000,
                    TestDict("protected", bounded.ProtectedTokens, "ordinary", bounded.OrdinaryTokens,
                        "budget", bounded.Budget, "largeRecordTokens", largeTokens));
            }

            string completeAnswer = "NPC asked what had been agreed. Player answered: 300 only after arrival. "
                + "NPC acknowledged the correction. Nothing has been paid. " + new string('a', 10000);
            var olderAnswer = new MemoryEvidence { SourceId = "ranked_older_answer", Text = completeAnswer, Score = 80 };
            var largeFloor = new MemoryEvidence { SourceId = "recent_window", Text = new string('p', 30000), Required = true };
            int answerTokens = Math.Max(1, EstimateContinuityTokens(RenderMemoryEvidence(olderAnswer, "m2")) - 128);
            var unrankedWindow = SelectPrecisionEvidence(new[] { largeFloor, olderAnswer }, 4000);
            Check("older_answer_pressure_fixture_exceeds_floor_not_configured_allowance", answerTokens > 2000 && answerTokens < 4000
                && !unrankedWindow.Selected.Any(e => e.SourceId == olderAnswer.SourceId));
            olderAnswer.SelectionRank = 0;
            olderAnswer.SelectionRelevance = .99d;
            var rankedWindow = SelectPrecisionEvidence(new[] { largeFloor, olderAnswer }, 4000);
            Check("first_ranked_answer_survives_large_recent_window", rankedWindow.Selected.Any(e => e.SourceId == olderAnswer.SourceId)
                && rankedWindow.Text.Contains(completeAnswer) && rankedWindow.ProtectedTokens == unrankedWindow.ProtectedTokens
                && rankedWindow.Budget <= rankedWindow.ProtectedTokens + 4000
                && rankedWindow.OrdinaryTokens > 2000 && rankedWindow.OrdinaryTokens < 4000,
                TestDict("protected", rankedWindow.ProtectedTokens, "ordinary", rankedWindow.OrdinaryTokens, "budget", rankedWindow.Budget));
            const string laterCorrectionText = "NPC later corrected the destination: the payment is due in the courtyard, not the gate. "
                + "Player acknowledged the destination; the amount and after-arrival condition remained unchanged.";
            var laterCorrection = new MemoryEvidence { SourceId = "separate_later_correction", Text = laterCorrectionText, SelectionRank = 1 };
            var answerAndCorrection = SelectPrecisionEvidence(new[] { largeFloor, olderAnswer, laterCorrection }, 4000);
            Check("expanded_allowance_keeps_separate_later_correction", answerAndCorrection.Text.Contains(completeAnswer)
                && answerAndCorrection.Text.Contains(laterCorrectionText)
                && answerAndCorrection.OrdinaryTokens <= 4000 && answerAndCorrection.ProtectedTokens == rankedWindow.ProtectedTokens);
            var strictWindow = SelectPrecisionEvidence(new[] { largeFloor, olderAnswer, answer }, 2000);
            Check("ranked_answer_cannot_override_explicit_smaller_allowance", !strictWindow.Selected.Any(e => e.SourceId == olderAnswer.SourceId)
                && strictWindow.Selected.Any(e => e.SourceId == answer.SourceId) && strictWindow.Budget <= strictWindow.ProtectedTokens + 2000);
            var oversize = CopyMemoryEvidence(olderAnswer, completeAnswer + new string('b', 16000));
            var boundedWindow = SelectPrecisionEvidence(new[] { largeFloor, oversize, answer }, 4000);
            Check("oversized_first_rank_cannot_grow_allowance_without_bound", !boundedWindow.Selected.Any(e => e.SourceId == oversize.SourceId)
                && boundedWindow.Selected.Any(e => e.SourceId == answer.SourceId) && boundedWindow.Budget <= boundedWindow.ProtectedTokens + 4000);
            var lowerRank = new MemoryEvidence { SourceId = "later_ranked_bundle", Text = completeAnswer, SelectionRank = 1 };
            var firstShort = CopyMemoryEvidence(answer); firstShort.SelectionRank = 0;
            var oneExpansion = SelectPrecisionEvidence(new[] { largeFloor, firstShort, lowerRank }, 4000);
            Check("later_rank_cannot_expand_budget_again", oneExpansion.Selected.Any(e => e.SourceId == firstShort.SourceId)
                && !oneExpansion.Selected.Any(e => e.SourceId == lowerRank.SourceId) && oneExpansion.Budget <= oneExpansion.ProtectedTokens + 2000);
            var missingScore = CopyMemoryEvidence(olderAnswer); missingScore.SelectionRelevance = null;
            var missingScoreFallback = SelectPrecisionEvidence(new[] { largeFloor, missingScore, answer }, 4000);
            Check("missing_rank_score_retains_unranked_fallback", !missingScoreFallback.Selected.Any(e => e.SourceId == missingScore.SourceId)
                && missingScoreFallback.Selected.Any(e => e.SourceId == answer.SourceId)
                && missingScoreFallback.Budget <= missingScoreFallback.ProtectedTokens + 2000);
            foreach (double relevance in new[] { 0d, .17d, .5d })
            {
                var weakFirst = CopyMemoryEvidence(olderAnswer); weakFirst.SelectionRelevance = relevance;
                var relativeOrder = SelectPrecisionEvidence(new[] { largeFloor, weakFirst, laterCorrection }, 4000);
                Check("complete_relative_rank_preserves_answer_and_correction_" + relevance.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    relativeOrder.Text.Contains(completeAnswer) && relativeOrder.Text.Contains(laterCorrectionText)
                    && relativeOrder.ProtectedTokens == rankedWindow.ProtectedTokens
                    && relativeOrder.OrdinaryTokens <= 4000);
            }

            var borderlineFirst = new MemoryEvidence { SourceId = "borderline_complete_answer",
                Text = "Player answered; NPC acknowledged the condition. " + new string('b', 6200),
                SelectionRank = 0, SelectionRelevance = .25d };
            var retainedShort = new MemoryEvidence { SourceId = "retained_short_answer",
                Text = "A separate earlier answer remains valid. " + new string('s', 2400),
                SelectionRank = 1, SelectionRelevance = .2d };
            int borderlineTokens = Math.Max(1, EstimateContinuityTokens(RenderMemoryEvidence(borderlineFirst, "m2")) - 128);
            var supplementary = SelectPrecisionEvidence(new[] { largeFloor, borderlineFirst, retainedShort }, 4000);
            Check("relative_rank_keeps_complete_borderline_and_short_answers", borderlineTokens > 2000
                && supplementary.Selected.Select(e => e.SourceId).SequenceEqual(new[] { largeFloor.SourceId, borderlineFirst.SourceId, retainedShort.SourceId })
                && supplementary.Text.Contains(borderlineFirst.Text) && supplementary.Text.Contains(retainedShort.Text)
                && supplementary.ProtectedTokens == rankedWindow.ProtectedTokens && supplementary.OrdinaryTokens <= 4000
                && supplementary.Rejected.Count == 0,
                TestDict("ordinary", supplementary.OrdinaryTokens, "budget", supplementary.Budget));
            var supplementaryStrict = SelectPrecisionEvidence(new[] { largeFloor, borderlineFirst, retainedShort }, 2000);
            Check("supplement_respects_explicit_small_allowance", !supplementaryStrict.Text.Contains(borderlineFirst.Text)
                && supplementaryStrict.Text.Contains(retainedShort.Text)
                && supplementaryStrict.OrdinaryTokens <= 2000 && supplementaryStrict.Rejected.Count == 1);
            var laterSmallAnswer = new MemoryEvidence { SourceId = "later_small_answer",
                Text = "The later answer is independent. " + new string('l', 1200),
                SelectionRank = 1, SelectionRelevance = .91d };
            var supplementaryLater = SelectPrecisionEvidence(new[] { largeFloor, firstShort, laterSmallAnswer }, 4000);
            Check("supplement_recovers_later_answer_without_replacing_first", supplementaryLater.Text.Contains(firstShort.Text)
                && supplementaryLater.Text.Contains(laterSmallAnswer.Text) && supplementaryLater.OrdinaryTokens <= 4000
                && supplementaryLater.Selected.FindIndex(e => e.SourceId == firstShort.SourceId)
                    < supplementaryLater.Selected.FindIndex(e => e.SourceId == laterSmallAnswer.SourceId));

            var sharedTurn = TestDict("id", "shared_answer", "hash", "shared_hash", "text", retainedShort.Text);
            var addedTurn = TestDict("id", "added_answer", "hash", "added_hash", "text", borderlineFirst.Text);
            var sharedOnly = CopyMemoryEvidence(retainedShort); sharedOnly.Kind = "reported_exchange";
            sharedOnly.SourceTurns = new() { sharedTurn };
            var overlapping = CopyMemoryEvidence(borderlineFirst, retainedShort.Text + "\n" + borderlineFirst.Text,
                new() { sharedTurn, addedTurn });
            overlapping.Kind = "reported_exchange";
            var deduplicatedSupplement = SelectPrecisionEvidence(new[] { largeFloor, overlapping, sharedOnly }, 4000);
            Check("supplement_deduplicates_against_actual_first_pass_views", deduplicatedSupplement.Text.Contains(borderlineFirst.Text)
                && deduplicatedSupplement.Selected.SelectMany(e => e.SourceTurns).Count(t => ReadString(t, "id", "") == "shared_answer") == 1
                && deduplicatedSupplement.Selected.SelectMany(e => e.SourceTurns).Count(t => ReadString(t, "id", "") == "added_answer") == 1
                && overlapping.SourceTurns.Count == 2 && overlapping.Text.Contains(retainedShort.Text)
                && deduplicatedSupplement.OrdinaryTokens <= 4000);

            // Extra capacity must supplement the complete standard allocation.
            // Otherwise a larger early bundle can evict an answer that already fit,
            // either during the first pass or during its deferred-record pass.
            MemoryEvidence SizedRanked(string id, int characters, int rank) => new MemoryEvidence {
                SourceId = id, Text = id + ": " + new string((char)('a' + rank), characters),
                SelectionRank = rank, SelectionRelevance = .9d - rank * .1d };
            var largeFirst = SizedRanked("large_first", 15000, 0);
            var standardAnswer = SizedRanked("standard_answer", 10000, 1);
            var standardShort = SizedRanked("standard_short", 1200, 2);
            var extraCorrection = SizedRanked("extra_correction", 1400, 3);
            var firstPassCandidates = new[] { largeFloor, largeFirst, standardAnswer, standardShort, extraCorrection };
            var firstStandard = SelectPrecisionEvidence(firstPassCandidates, 4000);
            var firstExpanded = SelectPrecisionEvidence(firstPassCandidates, 8000);
            Check("expanded_budget_preserves_standard_first_pass_answers",
                firstStandard.Selected.Any(e => e.SourceId == standardAnswer.SourceId)
                && firstStandard.Selected.Any(e => e.SourceId == standardShort.SourceId)
                && !firstStandard.Selected.Any(e => e.SourceId == extraCorrection.SourceId)
                && firstExpanded.Text.StartsWith(firstStandard.Text, StringComparison.Ordinal)
                && firstExpanded.Selected.Any(e => e.SourceId == extraCorrection.SourceId)
                && firstExpanded.ProtectedTokens == firstStandard.ProtectedTokens
                && firstExpanded.OrdinaryTokens <= 8000,
                TestDict("standard", firstStandard.Selected.Select(e => e.SourceId).ToArray(),
                    "expanded", firstExpanded.Selected.Select(e => e.SourceId).ToArray()));

            var smallFirst = SizedRanked("small_first", 1500, 0);
            var oversizedDeferred = SizedRanked("oversized_deferred", 19500, 1);
            var standardDeferred = SizedRanked("standard_deferred_answer", 6300, 2);
            var smallLater = SizedRanked("small_later", 900, 3);
            var extraDeferred = SizedRanked("extra_deferred_correction", 3300, 4);
            var deferredCandidates = new[] { largeFloor, smallFirst, oversizedDeferred, standardDeferred, smallLater, extraDeferred };
            var deferredOriginalTexts = deferredCandidates.Select(e => e.Text).ToArray();
            var deferredStandard = SelectPrecisionEvidence(deferredCandidates, 4000);
            var deferredExpanded = SelectPrecisionEvidence(deferredCandidates, 8000);
            Check("expanded_budget_completes_standard_deferred_pass_first",
                deferredStandard.Selected.Any(e => e.SourceId == standardDeferred.SourceId)
                && !deferredStandard.Selected.Any(e => e.SourceId == extraDeferred.SourceId)
                && deferredExpanded.Text.StartsWith(deferredStandard.Text, StringComparison.Ordinal)
                && deferredExpanded.Selected.Any(e => e.SourceId == extraDeferred.SourceId)
                && deferredExpanded.ProtectedTokens == deferredStandard.ProtectedTokens
                && deferredExpanded.OrdinaryTokens <= 8000,
                TestDict("standard", deferredStandard.Selected.Select(e => e.SourceId).ToArray(),
                    "expanded", deferredExpanded.Selected.Select(e => e.SourceId).ToArray()));
            Check("expanded_stages_do_not_duplicate_records_without_source_turns",
                deferredExpanded.Selected.All(e => e.SourceTurns.Count == 0)
                && deferredExpanded.Selected.Select(e => e.SourceId).Distinct(StringComparer.Ordinal).Count() == deferredExpanded.Selected.Count
                && !deferredExpanded.Rejected.Any(r => deferredExpanded.Selected.Any(e => e.SourceId == ReadString(r, "sourceId", "")))
                && deferredCandidates.Select(e => e.Text).SequenceEqual(deferredOriginalTexts));
            var unrankedExpanded = SelectPrecisionEvidence(new[] { largeFloor, missingScore, answer }, 8000);
            Check("expanded_budget_keeps_missing_rank_score_fallback",
                unrankedExpanded.Text == missingScoreFallback.Text
                && unrankedExpanded.OrdinaryTokens == missingScoreFallback.OrdinaryTokens
                && unrankedExpanded.Budget == missingScoreFallback.Budget);

            string padding = "*The soft fabric ripples gently across the broad cushion, its faded pattern catching the dim light in long shifting bands around the worn seam.*";
            string spoken = "NPC asked: What price? Player answered: 300 only after arrival. Nothing has been paid. NPC acknowledged the answer.";
            var longRecord = new MemoryEvidence { SourceId = "long", SourceHash = "original", Kind = "reported_exchange",
                Text = "- [Day 20] Speaker: " + padding + " " + spoken, SourceTurns = new() };
            longRecord.SourceTurns.Add(TestDict("id", "full", "hash", "source-original", "text", longRecord.Text));
            var segments = MemoryPackingSegments(longRecord, plan);
            var keep = segments.Where(s => ReadBool(s, "protected", true)).Select(s => ReadInt(s, "index", -1)).ToArray();
            Check("compression_keeps_answers_speakers_conditions_verbatim", segments.Count > 0
                && AcceptMemoryPacking(longRecord, segments, TestDict("textHash", MemorySelectionTextHash(longRecord.Text), "selectedSegments", keep))
                && longRecord.PackingText.Contains(spoken) && longRecord.PackingText.Contains("[Day 20] Speaker:")
                && longRecord.PackingText.Contains("narration omitted") && longRecord.Text.Contains(padding)
                && ReadString(longRecord.PackingTurns.Single(), "viewCoverage", "").Contains("excerpt"));
            Check("compressor_cannot_remove_protected_or_invent_spans",
                !AcceptMemoryPacking(longRecord, segments, TestDict("textHash", MemorySelectionTextHash(longRecord.Text), "selectedSegments", new[] { 1 }))
                && !AcceptMemoryPacking(longRecord, segments, TestDict("textHash", MemorySelectionTextHash(longRecord.Text), "selectedSegments", new[] { 999 }))
                && !AcceptMemoryPacking(longRecord, segments, TestDict("textHash", "changed", "selectedSegments", keep)));
            var exactPlan = PlanMemoryQuery("observer", "player", "What exactly did she say?", TestDict()); exactPlan.ExactWording = true;
            Check("exact_wording_and_recent_sources_never_compressed", MemoryPackingSegments(longRecord, exactPlan).Count == 0
                && MemoryPackingSegments(CopyRequired(), plan).Count == 0);
            var motionQuery = PlanMemoryQuery("observer", "player", "*I reach where the cloth hangs.* We can discuss the caravan.", TestDict());
            Check("incidental_where_does_not_disable_safe_background_packing", motionQuery.ExactWording
                && MemoryPackingSegments(longRecord, motionQuery).Count > 0);
            var gestureQuery = PlanMemoryQuery("observer", "player", "Describe every gesture she made.", TestDict());
            Check("requested_gestures_require_original_narration", MemoryPackingSegments(longRecord, gestureQuery).Count == 0);
            MemoryEvidence CopyRequired() { var copy = CopyMemoryEvidence(longRecord); copy.Required = true; return copy; }
            var consequential = CopyMemoryEvidence(longRecord, "- [Day 20] Speaker: *She gives him the cup, but does not drink from it before the answer.* I accept.");
            Check("narrated_handoffs_negation_and_drinking_are_protected", MemoryPackingSegments(consequential, plan).Count == 0);

            int calls = 0;
            var prior = MemorySelectionReplay.Value;
            try
            {
                var candidates = new List<MemoryEvidence> { new() { SourceId = "unrelated", Text = "The city has a tournament.", Score = 150 },
                    new() { SourceId = "answer", Text = "Michael answered 300 after arrival, and the NPC acknowledged it.", Score = 60 } };
                MemorySelectionReplay.Value = request => {
                    calls++;
                    var inputs = ReadDictionaryList(request, "candidates");
                    return TestDict("schema", MemorySelectionSchema, "operation", "rank", "ok", true,
                        "modelHash", MemorySelectionRankerHash, "tokenizerHash", MemorySelectionRankerTokenizerHash,
                        "runtimeVersion", MemorySelectionRuntimeVersion, "backend", "cpu", "rankingPolicy", MemorySelectionRankingPolicy,
                        "results", inputs.Select(i => TestDict("id", i["id"], "textHash", i["textHash"],
                            "scoringCoverage", Coverage(ReadString(i, "text", "")),
                            "score", ReadString(i, "text", "").Contains("300") ? .99d : .01d)).ToList());
                };
                var options = TestDict("memorySelectionMode", "off");
                ApplyLocalMemorySelection(candidates, plan, TestDict(), options, TestDict(), false);
                Check("off_issues_zero_requests", calls == 0);
                options["memorySelectionMode"] = "replay";
                var ranked = ApplyLocalMemorySelection(candidates, plan, TestDict(), options, TestDict(), false);
                Check("local_rank_does_not_lose_to_untouched_numeric_priority", calls == 1
                    && SelectPrecisionEvidence(ranked).Selected.First().SourceId == "answer"
                    && ranked.Single(e => e.SourceId == "answer").SelectionRelevance == .99d);
                MemorySelectionReplay.Value = _ => TestDict("ok", false, "status", "selection_deadline");
                var untouched = candidates.Select(e => { var c = CopyMemoryEvidence(e); c.SelectionRank = null; return c; }).ToList();
                var diagnostics = TestDict();
                var fallback = ApplyLocalMemorySelection(untouched, plan, TestDict(), options, diagnostics, false);
                Check("timeout_retains_original_candidates_no_effects", fallback.Count == 2 && fallback.All(e => e.SelectionRank == null)
                    && ReadString(ReadDictionary(diagnostics, "selectionHelpers"), "status", "") == "local_fallback");
                var input = new List<Dictionary<string, object>> { TestDict("id", "r0", "textHash", "source", "text", "Late correction: no payment.") };
                var malformed = TestDict("schema", MemorySelectionSchema, "operation", "rank", "ok", true,
                    "modelHash", MemorySelectionRankerHash, "tokenizerHash", MemorySelectionRankerTokenizerHash,
                    "runtimeVersion", MemorySelectionRuntimeVersion, "backend", "cpu", "rankingPolicy", MemorySelectionRankingPolicy,
                    "results", new[] { TestDict("id", "r99", "textHash", "source", "score", .9d) });
                Check("unknown_and_incomplete_rankings_rejected", !ValidateMemorySelectionResponse(malformed, "rank", input, out _));
                malformed["results"] = new[] { TestDict("id", "r0", "textHash", "source", "score", double.NaN) };
                Check("nonfinite_scores_rejected", !ValidateMemorySelectionResponse(malformed, "rank", input, out _));
                malformed["results"] = new[] { TestDict("id", "r0", "textHash", "source", "score", .9d) };
                Check("backend_substitution_rejected", !ValidateMemorySelectionResponse(malformed, "rank", input, out _, "cuda"));
                malformed["rankingPolicy"] = "obsolete";
                Check("changed_scoring_policy_rejected", !ValidateMemorySelectionResponse(malformed, "rank", input, out _));
                malformed["rankingPolicy"] = MemorySelectionRankingPolicy;
                Check("missing_source_coverage_rejected", !ValidateMemorySelectionResponse(malformed, "rank", input, out _));
                var coverage = Coverage("Late correction: no payment.");
                malformed["results"] = new[] { TestDict("id", "r0", "textHash", "source", "score", .9d, "scoringCoverage", coverage) };
                Check("complete_source_coverage_accepted", ValidateMemorySelectionResponse(malformed, "rank", input, out _));
                coverage["sourceSpansUtf16"] = new object[] { new[] { 0, 10 } };
                Check("unscored_late_correction_rejected", !ValidateMemorySelectionResponse(malformed, "rank", input, out _));
                var split = TestDict("sourceCoverage", "complete_overlapping_source_windows", "windowCount", 2,
                    "maxWindowTokens", 100, "sourceSpansUtf16", new object[] { new[] { 0, 4 }, new[] { 3, 6 } });
                Check("overlapping_complete_windows_accepted", ValidateMemoryScoringCoverage(split, "abcdef"));
                split["sourceSpansUtf16"] = new object[] { new[] { 0, 2 }, new[] { 3, 6 } };
                Check("gap_between_scoring_windows_rejected", !ValidateMemoryScoringCoverage(split, "abcdef"));
                split["sourceSpansUtf16"] = new object[] { new[] { 0, 1 }, new[] { 0, 4 } };
                Check("split_unicode_and_repeated_window_rejected", !ValidateMemoryScoringCoverage(split, "🙂ab"));

                string longText = "- [Day 20] Speaker: " + string.Join(" ", Enumerable.Repeat(padding, 80)) + " " + spoken;
                var large = new MemoryEvidence { SourceId = "large", Kind = "reported_exchange", Text = longText,
                    SourceTurns = new() { TestDict("id", "large_turn", "hash", "original", "text", longText) } };
                MemorySelectionReplay.Value = request => {
                    if (ReadString(request, "operation", "") == "compress") throw new InvalidOperationException("compressor_unavailable");
                    return TestDict("schema", MemorySelectionSchema, "operation", "rank", "ok", true,
                        "modelHash", MemorySelectionRankerHash, "tokenizerHash", MemorySelectionRankerTokenizerHash,
                        "runtimeVersion", MemorySelectionRuntimeVersion, "backend", "cpu", "rankingPolicy", MemorySelectionRankingPolicy,
                        "results", ReadDictionaryList(request, "candidates").Select(i => TestDict("id", i["id"], "textHash", i["textHash"], "score", .9d,
                            "scoringCoverage", Coverage(ReadString(i, "text", "")))).ToList());
                };
                options["enableMemoryEvidenceCompression"] = true;
                options["memoryPrecisionOrdinaryTokens"] = 2000;
                var compressionAudit = TestDict();
                var preservedRank = ApplyLocalMemorySelection(new() { large }, plan, TestDict(), options, compressionAudit, false);
                Check("compression_failure_preserves_valid_rank_and_original", preservedRank.Single().SelectionRank == 0
                    && preservedRank.Single().Text == longText && preservedRank.Single().PackingText.Length == 0
                    && ReadString(ReadDictionary(compressionAudit, "selectionHelpers"), "status", "") == "ranked_compression_unavailable");
                var normalAudit = TestDict();
                ApplyLocalMemorySelection(new() { longRecord }, plan, TestDict(), options, normalAudit, false);
                Check("fitting_original_does_not_invoke_compressor", ReadInt(ReadDictionary(normalAudit, "selectionHelpers"), "localRequests", -1) == 1
                    && ReadString(ReadDictionary(normalAudit, "selectionHelpers"), "compressionStatus", "") == "no_beneficial_safe_compaction");
                var largeSegments = MemoryPackingSegments(large, plan);
                Check("only_feasible_compaction_requested", MinimumMemoryPackingFits(large, largeSegments, 2000, 1)
                    && !MinimumMemoryPackingFits(large, largeSegments, 10, 1));
            }
            finally { MemorySelectionReplay.Value = prior; }
            Check("missing_or_development_certificate_cannot_activate", !MemorySelectionQualified(TestDict(), false)
                && !MemorySelectionQualified(TestDict("memorySelectionQualification", TestDict("heldOut", false, "caseCount", 153)), true));
            return results;
        }
    }
}
