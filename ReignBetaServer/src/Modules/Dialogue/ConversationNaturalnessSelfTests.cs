using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationNaturalnessSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> add = (id, pass, data) => results.Add(TestDict(
                "caseId", "naturalness_" + id, "passed", pass, "summary", id, "data", data));
            Func<string, string, Dictionary<string, object>> npc = (name, text) => TestDict("role", "npc", "speaker", name, "text", text);
            const string speaker = "Gavene";
            // Reviewed excerpts from the 2026-09-14 incident. These retain the failure's
            // meaning and order without copying the player's campaign database or full history.
            var prior = new List<Dictionary<string, object>>
            {
                npc(speaker, "You have answered everything but the one thing I asked. This way costs a quarter hour. Either answer is allowed. A shrug is not."),
                npc(speaker, "That was an answer. I said either was allowed, so I will not pretend I did not hear it. But I keep names the way other houses keep ledgers, and yours is in mine now.")
            };
            const string badAnswer = "You still have not said which of us you came through that gate for. That is the third time you have answered my question with one of your own. I heard the first two.";
            const string badCorrection = "The wife you slid in behind a sentence about wanting nothing. Three times now. That is what I asked that you could refuse. I would count it and we would walk on. I will not ask a fourth time.";
            Func<string, string, List<Dictionary<string, object>>, List<Dictionary<string, object>>, List<Dictionary<string, object>>> violations =
                (reply, player, own, shared) => FindConversationNaturalnessViolations(TestDict("reply", reply), player, own, shared, "fixture_gavene", speaker);
            var loop = violations(badAnswer, "Don't you want to know more about me?", prior, null);
            add("captured_answer_loop_below_lexical_threshold", prior.All(row => RoleplayTextSimilarity(badAnswer, ReadString(row, "text", "")) < 0.72d)
                && loop.Count == 1, loop);
            var corrected = new List<Dictionary<string, object>>
            {
                npc(speaker, "Say it at the boards and I will count it paid. Now say what you want. You have had mine. Spend yours."),
                npc(speaker, "You are right. I put that word on the boards before you did — a wife. That was answering me, not hooking me. I counted it wrong. So that is paid, and it does not change the toll.")
            };
            const string clarify = "Im not telling you no, what do you mean by something sideways? What did you ask me plainly that I could refuse?";
            add("captured_correction_repeated_lecture", violations(badCorrection, clarify, corrected, null).Count == 1, null);
            var guide = BuildConversationNaturalnessContext(clarify, false);
            add("clarification_is_not_refusal", ReadBool(guide, "clarification", false) && ReadString(guide, "prompt", "").Contains("not refusal or evasion"), guide);
            add("explicit_player_correction", ReadBool(BuildConversationNaturalnessContext("I only said that because of what you said. I wasnt implying anything.", false), "correction", false), null);
            add("apology_can_close_issue", ReadBool(BuildConversationNaturalnessContext("I apologize. I misunderstood you.", true), "apology", false), null);
            add("settled_and_outstanding_points_are_distinct", ConversationNaturalnessContract.Contains("separate outstanding request")
                && ConversationNaturalnessContract.Contains("private comprehension, state and memory"), null);

            const string apology = "I am sorry for the confusion.";
            var group = new List<Dictionary<string, object>> { TestDict("role", "player", "text", apology), npc("Zotoria", "An apology is a coin spent on what is already counted.") };
            add("same_beat_group_echo", violations("I will count it. You have paid the same coin back a fourth time with that apology.", apology, new List<Dictionary<string, object>>(), group)
                .Any(row => ReadString(row, "type", "") == "group_conversation_accounting_echo"), null);
            add("older_group_opinion_is_not_current_echo", violations(badAnswer, "What is your favorite place?", new List<Dictionary<string, object>>(), group).Count == 0, null);
            add("other_speaker_history_does_not_become_own", violations(badAnswer, apology, prior.Select(row => npc("Other speaker", ReadString(row, "text", ""))).ToList(), null).Count == 0, null);
            add("single_metaphor_is_not_a_loop", violations("Your apology pays that debt.", apology, prior.Take(1).ToList(), null).Count == 0, null);
            foreach (string reply in new[]
            {
                "No. I do not want your gift. Please respect that.",
                "I meant that I would like you to tell my mother you came to see me. You already told me that; I am asking whether you will tell her too.",
                "You are right; I brought up marriage. I meant that I would rather you tell me directly when you disagree.",
                "I like carving small wooden animals when the household is quiet.",
                "There are forty soldiers and twelve sacks of grain.",
                "I count the names on the roster each morning so nobody is left behind.",
                "*She counts three paces, then looks up.* Thank you. I accept your apology."
            }) add("preserve_valid_reply_" + results.Count, violations(reply, apology, prior, null).Count == 0, reply);
            foreach (string query in new[] { "How many times did I ask that?", "What price do you want for the grain?", "Please count the soldiers.", "How much do I owe on the loan?" })
                add("literal_accounting_exception_" + results.Count, violations(badAnswer, query, prior, null).Count == 0, query);
            var mixed = loop.Concat(new[] { TestDict("type", "repeated_distinctive_phrase", "match", "she keeps walking beside him") }).ToList();
            add("stage_cleanup_cannot_hide_semantic_loop", !TryRemoveRepeatedStageDirectionsOnly(TestDict("reply", badAnswer), mixed,
                new Dictionary<string, object>(), prior, speaker, "Player", out var ignored, out var remaining, out int removed), null);

            const string generatedA = "Gavene has spent so long reading what men want off their faces at her mother's table that she is not certain what remains of her when the counting stops.";
            const string generatedB = "Gavene has spent so long counting what men want at her mother's table that she is not sure what is left of her to offer when the counting stops.";
            var rows = new List<Dictionary<string, object>>
            {
                TestDict("text", generatedA, "category", "personal_value", "provenance", "npc_generated_personal_history", "importance", 0.85d),
                TestDict("text", generatedB, "category", "personal_value", "provenance", "npc_generated_personal_history", "importance", 0.6d),
                TestDict("text", "Gavene enjoys making shadow puppets with lamplight.", "category", "preference", "topic_key", "shadow_puppets"),
                TestDict("text", "Gavene counts blue lamps before entering a room.", "category", "habit", "topic_key", "lamp_habit", "first_ts", 1L)
            };
            var document = TestDict("active", rows);
            string unchanged = Json.Serialize(document);
            string hobbies = FormatDynamicCharacteristicsForPrompt(document, "What hobbies do you enjoy?");
            add("captured_generated_rhetoric_quarantined", !hobbies.Contains("counting stops") && hobbies.Contains("shadow puppets"), hobbies);
            add("relevance_does_not_fill_with_unrelated_memories", FormatDynamicCharacteristicsForPrompt(document, apology) == "", null);
            add("literal_habit_recall_survives", FormatDynamicCharacteristicsForPrompt(document, "What was your first habit?").Contains("blue lamps"), null);
            add("projection_does_not_mutate_saved_character", Json.Serialize(document) == unchanged, null);
            add("unknown_authored_provenance_preserved", !IsGeneratedConversationRhetoric(TestDict("text", generatedA, "provenance", "authored")), null);
            const string hobbyA = "Gavene carves small wooden horses beside the cedar window every quiet evening.";
            const string hobbyB = "Every quiet evening Gavene carves small wooden horses beside the cedar window.";
            add("paraphrase_deduplicates_across_topic_keys", DynamicCharacteristicsEquivalent(hobbyA, hobbyB), null);
            add("polarity_is_not_merged", !DynamicCharacteristicsEquivalent(hobbyA, "Gavene never carves small wooden horses beside the cedar window every quiet evening."), null);
            add("different_quantities_are_not_merged", !DynamicCharacteristicsEquivalent(hobbyA + " She makes 2 each week.", hobbyA + " She makes 3 each week."), null);
            add("different_objects_are_not_merged", !DynamicCharacteristicsEquivalent(hobbyA, hobbyA.Replace("horses", "birds")), null);
            add("different_locations_are_not_merged", !DynamicCharacteristicsEquivalent(hobbyA, hobbyA.Replace("cedar window", "kitchen table")), null);
            var dupRows = TestDict("active", new List<Dictionary<string, object>> { TestDict("text", hobbyA, "topic_key", "wooden_horses"), TestDict("text", hobbyB, "topic_key", "evening_carving") });
            add("existing_paraphrases_reach_prompt_once", SelectNaturalDynamicCharacteristics(dupRows, "carving wooden horses").Count == 1, null);
            var many = TestDict("active", Enumerable.Range(1, 20).Select(n => TestDict("text", "The speaker's collection contains " + n + " painted vessels.", "category", "preference")).ToList());
            add("relevant_selection_is_bounded", SelectNaturalDynamicCharacteristics(many, "collection").Count == 6, null);
            var recalled = TestDict("active", new List<Dictionary<string, object>>
            {
                TestDict("text", "Gavene learned to mend baskets beside her grandmother's hearth.", "category", "formative_experience"),
                TestDict("text", "Gavene cannot bear spiders near her bedding.", "category", "aversion"),
                TestDict("text", "Gavene hopes to light the shuttered room and make it a home.", "category", "aspiration"),
                TestDict("text", "Gavene believes hospitality means giving a tired guest somewhere quiet to rest.", "category", "personal_value")
            });
            add("childhood_recall_uses_category_even_without_exact_words", FormatDynamicCharacteristicsForPrompt(recalled, "Tell me about your childhood.").Contains("mend baskets"), null);
            add("aversion_recall_understands_dislike", FormatDynamicCharacteristicsForPrompt(recalled, "What do you dislike?").Contains("spiders"), null);
            add("aspiration_recall_understands_want", FormatDynamicCharacteristicsForPrompt(recalled, "What do you really want?").Contains("shuttered room"), null);
            add("values_recall_understands_principles", FormatDynamicCharacteristicsForPrompt(recalled, "What are your principles?").Contains("hospitality"), null);

            var profile = TestDict("name", speaker, "heroStringId", "fixture_gavene");
            var payload = TestDict("worldDay", 1d, "playerHeroStringId", "fixture_player", "mode", "dialogue");
            Func<string, string, List<Dictionary<string, object>>> normalize = (text, topic) => NormalizeDynamicCharacteristicWrites(
                TestDict("dynamicCharacteristicWrites", new List<Dictionary<string, object>> { TestDict("text", text, "category", "habit", "topicKey", topic) }), "fixture_gavene", profile, payload, text);
            var rhetoricalWrite = normalize(generatedA, "counting_people");
            add("new_rhetorical_self_story_rejected", rhetoricalWrite.Count == 1 && ReadString(rhetoricalWrite[0], "status", "") == "rejected", rhetoricalWrite);
            const string financialValue = "Gavene wants fair prices for grain and keeps a ledger for the family business.";
            var financialWrite = normalize(financialValue, "fair_grain_prices");
            add("literal_financial_characterization_is_preserved", financialWrite.Count == 1 && ReadString(financialWrite[0], "status", "") == "active", financialWrite);
            var financialMemory = TestDict("active", new List<Dictionary<string, object>> { TestDict("text", financialValue, "category", "personal_value", "provenance", "npc_generated_personal_history") });
            add("literal_financial_memory_reaches_relevant_prompt", FormatDynamicCharacteristicsForPrompt(financialMemory, "What do you think about grain prices?").Contains("fair prices for grain"), null);
            add("figurative_price_is_still_filtered", IsConversationCharacteristicRhetoric("Gavene charges a price for every apology and keeps a ledger of answers."), null);
            var firstWrite = normalize(hobbyA, "wooden_horses");
            var secondWrite = normalize(hobbyB, "evening_carving");
            string campaign = "naturalness_test_" + Guid.NewGuid().ToString("N");
            UpsertCharacterProfile(campaign, profile);
            StoreDynamicCharacteristicWrites(campaign, "fixture_gavene", firstWrite, payload, TestDict("exchangeId", "original"), "event_original", "dialogue", "", 1, new[] { "fixture_gavene" });
            var duplicateStore = StoreDynamicCharacteristicWrites(campaign, "fixture_gavene", secondWrite, payload, TestDict("exchangeId", "paraphrase"), "event_paraphrase", "dialogue", "", 2, new[] { "fixture_gavene" });
            var persisted = ReadJsonObject(CharacterFile(campaign, "fixture_gavene", "dynamic_characteristics.json"));
            add("paraphrase_write_retains_original_and_rejection_evidence", ReadInt(duplicateStore, "rejectedCount", 0) == 1
                && ReadDictionaryList(persisted, "active").Count == 1 && ReadDictionaryList(persisted, "rejected").Count == 1
                && ReadString(ReadDictionaryList(persisted, "active")[0], "source_exchange_id", "") == "original", duplicateStore);
            foreach (string name in new[] { "world_tone.txt", "noble_prompt.txt" })
            {
                string template = LoadPromptTemplate(name), scoped = ScopeConversationTone(template);
                add("scoped_tone_idempotent_" + name, ScopeConversationTone(scoped) == scoped, null);
            }
            string custom = "CUSTOM VOICE: proud, terse, fond of chess. " + ConversationToneReplacements[0, 0] + " CUSTOM END.";
            string effective = ScopeConversationTone(custom);
            add("legacy_clause_scoped_without_erasing_customization", effective.StartsWith("CUSTOM VOICE: proud, terse, fond of chess.")
                && effective.EndsWith("CUSTOM END.") && !effective.Contains(ConversationToneReplacements[0, 0]) && custom.Contains(ConversationToneReplacements[0, 0]), effective);

            results.AddRange(RunAcceptedItemGiftRoutingSelfTests());
            results.AddRange(RunDynamicCharacteristicsSelfTests());
            return results;
        }

        private static List<Dictionary<string, object>> RunAcceptedItemGiftRoutingSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> add = (id, pass, data) => results.Add(TestDict("caseId", "item_gift_" + id, "passed", pass, "summary", id, "data", data));
            const string playerText = "Hadax is the one I defeated to win the final bout, here is the pernach i won, would you like it? Gavene?";
            var gate = TestDict("needed", true, "commitment", "accepted", "intent", "Gavene accepts the tournament-won pernach from the armed stranger as a kept token rather than payment, and opens House Cassianentis's door to him at Phycaon.");
            var snapshot = DefaultActionTestSnapshot();
            var payload = ReadDictionary(snapshot, "payload");
            var hero = ReadDictionary(payload, "hero");
            var settings = new Dictionary<string, object>(LoadSettings()) { ["enableMinimeMemoryWorker"] = false };
            foreach (int cap in new[] { 3, 10 })
            {
                var choices = BuildAllowedActionPlannerChoices(settings, payload, hero, gate, playerText,
                    "So put it here as the first thing of yours this house holds — proof of a name, not the name itself.", cap);
                add("captured_pernach_survives_top_" + cap, choices.Count <= cap && choices.Any(row => ReadString(row, "command", "") == "transfer_item"), choices.Select(row => ReadString(row, "command", "")).ToList());
            }
            foreach (string noun in new[] { "brooch", "carved toy", "falchion" })
                add("not_specific_to_pernach_" + noun, ShouldPreserveAcceptedItemGift(TestDict("needed", true, "commitment", "accepted", "intent", "The speaker accepts the " + noun + " as a gift."), "Here is the " + noun + ", would you like it?", ""), null);
            foreach (string commitment in new[] { "refused", "conditional", "roleplay_only" })
                add("no_preservation_for_" + commitment, !ShouldPreserveAcceptedItemGift(new Dictionary<string, object>(gate) { ["commitment"] = commitment }, playerText, ""), null);
            add("needed_gate_required", !ShouldPreserveAcceptedItemGift(new Dictionary<string, object>(gate) { ["needed"] = false }, playerText, ""), null);
            add("cash_gift_retains_money_schema", !ShouldPreserveAcceptedItemGift(TestDict("needed", true, "commitment", "accepted", "intent", "She accepts the gift of 200 denars."), "I give you 200 denars.", "transfer_gold"), null);
            foreach (string command in new[] { "trade_package", "ransom_package", "diplomatic_package" })
                add("preserve_atomic_" + command, !ShouldPreserveAcceptedItemGift(gate, playerText, command), null);
            var index = ReadDictionary(payload, "actionResolutionIndex");
            var assets = ReadDictionary(index, "assets");
            var owner = ReadDictionary(assets, "player");
            owner["inventory"] = TestDict("topItems", new List<Dictionary<string, object>> { TestDict("itemId", "fixture_pernach", "name", "Pernach", "count", 1) });
            var terms = new Dictionary<string, object>();
            var trace = new List<Dictionary<string, object>>();
            ResolveItemReference(index, TestDict("Item", "Pernach"), terms, trace);
            add("pernach_resolves_from_native_inventory_evidence", ReadString(terms, "itemId", "") == "fixture_pernach", trace);
            var unknownTerms = new Dictionary<string, object>();
            ResolveItemReference(index, TestDict("Item", "Imaginary Prize"), unknownTerms, new List<Dictionary<string, object>>());
            add("routing_does_not_invent_missing_item", !unknownTerms.ContainsKey("itemId"), unknownTerms);
            return results;
        }
    }
}
