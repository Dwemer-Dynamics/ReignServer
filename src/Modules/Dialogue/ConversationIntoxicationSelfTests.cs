using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationIntoxicationSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            Action<string, bool> check = (id, passed) => rows.Add(new Dictionary<string, object>
            { ["id"] = id, ["name"] = id, ["caseId"] = id, ["passed"] = passed, ["ok"] = passed,
                ["suite"] = "conversation_intoxication", ["durationMs"] = 0 });
            foreach (double endurance in new[] { 0d, 2d, 3d, 5d, 10d, 15d })
                check("threshold_" + endurance, DrinkingThreshold(endurance) == Math.Max(5, endurance * 2));
            check("invalid_endurance", DrinkingThreshold(double.NaN) == 5 && DrinkingThreshold(-10) == 5);
            check("native_endurance_only", IntoxicationEndurance(new Dictionary<string, object>
            { ["attributes"] = new Dictionary<string, object> { ["endurance"] = 5 },
                ["skills"] = new Dictionary<string, object> { ["athletics"] = 300 } }, null) == 5);
            check("athletics_never_fallback", IntoxicationEndurance(new Dictionary<string, object>
            { ["skills"] = new Dictionary<string, object> { ["athletics"] = 300 } }, null) == 0);
            foreach (var pair in new Dictionary<double, string> { [0] = "sober", [.1] = "mildly affected", [1.25] = "tipsy",
                [2.5] = "very intoxicated", [3.75] = "severely intoxicated", [5] = "overwhelmed" })
                check("stage_" + pair.Key, RecoverIntoxication(new Dictionary<string, object> { ["drinks"] = pair.Key }, 0, 10).Stage == pair.Value);
            var full = new Dictionary<string, object> { ["drinks"] = 5d, ["hour"] = 10d, ["hasClock"] = true, ["alcoholBlocked"] = true };
            check("recovery_two_hours", RecoverIntoxication(full, 2, 12).Drinks == 4);
            check("paused_time", RecoverIntoxication(full, 2, 10).Drinks == 5);
            check("no_negative_time_recovery", RecoverIntoxication(full, 2, 8).Drinks == 5);
            check("blocked_at_half", RecoverIntoxication(full, 2, 15).AlcoholBlocked);
            check("released_below_half", !RecoverIntoxication(full, 2, 15.1).AlcoholBlocked);
            check("sober_clamped", RecoverIntoxication(full, 2, 100).Drinks == 0);
            check("missing_clock_no_recovery", RecoverIntoxication(full, 2, double.NaN).Drinks == 5);
            check("prompt_increases_within_band", IntoxicationPrompt(RecoverIntoxication(new Dictionary<string, object> { ["drinks"] = 2.5 }, 0, 1))
                != IntoxicationPrompt(RecoverIntoxication(new Dictionary<string, object> { ["drinks"] = 3 }, 0, 1)));

            Func<string, string, int, Dictionary<string, object>> response = (action, serving, count) => new Dictionary<string, object>
            { ["reply"] = "*" + action + "* \"Well then.\"", ["drinkingEvents"] = new List<Dictionary<string, object>>
                { new Dictionary<string, object> { ["actionIndex"] = 0, ["serving"] = serving, ["count"] = count, ["completed"] = true, ["alcohol"] = true } } };
            foreach (string action in new[] { "He offers a drink of wine.", "He refuses to drink.", "He recalls drinking yesterday.",
                "He would drink some wine.", "He does not drink.", "He drinks water.", "He pretends to drink wine.", "He raises his cup." })
            {
                var parsed = response(action, "drink", 1);
                check("reject_" + action, ValidatedDrinkAmount(parsed, ReadString(parsed, "reply", ""), new List<Dictionary<string, object>>()) == 0);
            }
            foreach (var serving in new Dictionary<string, double> { ["drink"] = 1, ["half"] = .5, ["sip"] = .25 })
            {
                var parsed = response(serving.Key == "sip" ? "He sips wine." : serving.Key == "half" ? "He drinks half a cup of wine." : "He drinks a cup of wine.", serving.Key, 1);
                check("serving_" + serving.Key, ValidatedDrinkAmount(parsed, ReadString(parsed, "reply", ""), new List<Dictionary<string, object>>()) == serving.Value);
            }
            var duplicate = response("He drains the wine.", "drink", 1);
            var duplicateEvents = ReadDictionaryList(duplicate, "drinkingEvents");
            duplicateEvents.Add(duplicateEvents[0]); duplicate["drinkingEvents"] = duplicateEvents;
            check("duplicate_action_once", ValidatedDrinkAmount(duplicate, ReadString(duplicate, "reply", ""), new List<Dictionary<string, object>>()) == 1);
            check("no_spoken_consumption", ValidatedDrinkAmount(duplicate, "\"I drink wine.\"", new List<Dictionary<string, object>>()) == 0);
            var invalid = response("He drinks wine.", "drink", 21);
            check("bounded_quantity_uses_conservative_fallback", ValidatedDrinkAmount(invalid, ReadString(invalid, "reply", ""), new List<Dictionary<string, object>>()) == .25);
            var exaggerated = response("He drinks a cup of wine.", "drink", 10);
            check("quantity_grounded_in_explicit_text", ValidatedDrinkAmount(exaggerated, ReadString(exaggerated, "reply", ""), new List<Dictionary<string, object>>()) == 1);
            var plural = response("He drinks two cups of wine.", "drink", 2);
            check("explicit_plural", ValidatedDrinkAmount(plural, ReadString(plural, "reply", ""), new List<Dictionary<string, object>>()) == 2);

            // Production failure shapes, with independent fictional test identities.
            // Exercise accepted text, metadata binding, and explicit negative controls.
            Func<string, string[], Dictionary<string, object>> omitted = (reply, facts) => new Dictionary<string, object>
            {
                ["reply"] = reply, ["drinkingEvents"] = new List<Dictionary<string, object>>(),
                ["decisionBrief"] = new Dictionary<string, object> { ["facts"] = facts }
            };
            Func<Dictionary<string, object>, double> amount = parsed => ValidatedDrinkAmount(parsed,
                ReadString(parsed, "reply", ""), new List<Dictionary<string, object>>(), "Mira", true);
            var misindexed = response("Mira enters the tavern; the woman behind the counter is wiping down a jar.", "drink", 1);
            misindexed["reply"] = "*Mira enters the tavern; the woman behind the counter is wiping down a jar.* All right. "
                + "*Mira takes her wine and drinks — a real mouthful, not a sip, not a performance.*";
            var bindingEvidence = new List<Dictionary<string, object>>();
            check("captured_wrong_index_rebound_to_consumption", ValidatedDrinkAmount(misindexed, ReadString(misindexed, "reply", ""), bindingEvidence, "Mira", true) == .25
                && bindingEvidence.Any(e => ReadDouble(e, "amount", 0) == .25 && ReadInt(e, "actionIndex", -1) == 1 && ReadString(e, "binding", "") == "unique_consumption"));
            check("wiping_down_never_consumption", amount(response("Mira enters the tavern; the woman behind the counter is wiping down a jar.", "drink", 1)) == 0);
            var quoted = response("She nods.", "drink", 1);
            quoted["reply"] = "*She nods.* Very well. *She drinks a cup of wine.*";
            ReadDictionaryList(quoted, "drinkingEvents")[0]["actionQuote"] = "She drinks a cup of wine.";
            check("exact_action_quote_overrides_bad_index", amount(quoted) == 1);
            check("missing_event_explicit_alcohol", amount(omitted("*She drinks a cup of wine.*", new string[0])) == 1);
            check("missing_event_current_shared_wine", amount(omitted("*She picks up her cup again and drinks — a measured mouthful, the rosemary sharp on the finish.*",
                new[] { "They are sharing wine at the tavern." })) == .25);
            check("missing_event_unknown_beverage", amount(omitted("*She sips from her cup.*", new string[0])) == 0);
            check("historical_alcohol_does_not_fill_missing_event", amount(omitted("*She sips from her cup.*", new[] { "Earlier they were drinking wine." })) == 0);
            check("hypothetical_alcohol_does_not_fill_missing_event", amount(omitted("*She sips from her cup.*", new[] { "If they are sharing wine, she may relax." })) == 0);
            check("alcohol_in_speech_does_not_establish_contents", amount(omitted("We discussed wine yesterday. *She sips from her cup.*", new string[0])) == 0);
            // Beverage identity now needs evidence independent of alcohol:true.
            check("fresh_cup_once_before_drinking", amount(response("Mira takes the fresh cup of wine when it arrives, turning it once before drinking — a solid mouthful, the rosemary sharper in this jar.", "drink", 1)) == .25);
            check("not_a_sip_is_not_a_refusal", amount(response("She drinks a real mouthful of wine, not a sip.", "drink", 1)) == .25);
            check("unrelated_negative_after_consumption", amount(response("She drinks wine with a directness that has no performance in it.", "drink", 1)) == 1);
            check("actual_sips_remain_fractional", amount(response("She takes three sips of wine.", "sip", 3)) == .75);
            check("takes_drink_from_own_cup", amount(response("She takes a drink from her own cup of wine.", "drink", 1)) == 1);
            check("finished_cup_is_full", amount(response("She finishes her cup of wine.", "drink", 1)) == 1);
            check("mouthful_cannot_be_full_cup", amount(response("She drinks a mouthful of wine.", "drink", 1)) == .25);
            check("female_speaker_does_not_consume_male_action", amount(response("He drinks wine.", "drink", 1)) == 0);
            var namedDrink = response("Mira drinks wine.", "drink", 1);
            check("speaker_first_name_supported", ValidatedDrinkAmount(namedDrink, ReadString(namedDrink, "reply", ""),
                new List<Dictionary<string, object>>(), "Mira of the Hills", true) == 1);
            foreach (string excluded in new[] {
                "Michael drinks a cup of wine.", "The waitress drinks wine.", "She watches Michael drink wine.",
                "She hands him the cup and he drinks wine.", "She thinks Michael drinks wine.",
                "She offers wine and watches him drink.", "She says she drinks wine.",
                "She says, \"Watch closely. She drinks wine. That is how the story ends.\"",
                "She recalls drinking wine.", "She once drank wine.", "She drank wine yesterday.",
                "She drinks wine if he agrees.", "She might drink wine.", "She pretends to drink wine.",
                "She tries to drink wine.", "She refuses to drink wine.", "She does not drink wine.",
                "She raises the cup before drinking the wine.", "She raises her drink.",
                "She orders a drink of wine.", "She picks up a drink of wine.", "She delivers two drinks of wine.", "She serves drinks of wine.",
                "She takes a drink from the waitress.", "She takes a drink from her.", "She counts the drinks of wine.", "She finishes her sentence about wine.",
                "She swallows her pride and studies the wine.", "She drinks water.", "She sips tea.",
                "She drinks non-alcoholic wine.", "She drinks alcohol-free beer.", "She empties the wine onto the floor.",
                "She drinks from her empty cup.", "She sips wine from an empty glass."
            }) check("speaker_and_event_exclusion_" + excluded, amount(response(excluded, "drink", 1)) == 0);

            var deletedEvent = response("She nods.", "drink", 1);
            deletedEvent["reply"] = "*She nods.* *She drinks wine.*";
            ReadDictionaryList(deletedEvent, "drinkingEvents")[0]["actionIndex"] = 1;
            RebindDrinkingEventsAfterCleanup(deletedEvent, ReadString(deletedEvent, "reply", ""), "*She nods.*", "Mira");
            check("deleted_consumption_cannot_retarget_survivor", ReadDictionaryList(deletedEvent, "drinkingEvents").Count == 0);
            var shiftedEvent = response("She nods.", "drink", 1);
            shiftedEvent["reply"] = "*She nods.* *She drinks wine.*";
            ReadDictionaryList(shiftedEvent, "drinkingEvents")[0]["actionIndex"] = 1;
            RebindDrinkingEventsAfterCleanup(shiftedEvent, ReadString(shiftedEvent, "reply", ""), "*She drinks wine.*", "Mira");
            shiftedEvent["reply"] = "*She drinks wine.*";
            check("surviving_consumption_reindexed", ReadInt(ReadDictionaryList(shiftedEvent, "drinkingEvents")[0], "actionIndex", -1) == 0 && amount(shiftedEvent) == 1);
            string repeatedDrink = "She takes a drink from her cup and sets it down with a soft click.";
            var priorDrinks = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["role"] = "npc", ["text"] = "*" + repeatedDrink + "* I heard about that yesterday." } };
            check("drinking_repetition_is_physical_progress", FindRepeatedDistinctiveStageDirection("*" + repeatedDrink + "* The roads are quieter now.", priorDrinks, "Mira") == null);
            string repeatedGesture = "She leans forward and traces the old scratches on the table with one fingertip.";
            var priorGesture = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["role"] = "npc", ["text"] = "*" + repeatedGesture + "* A different answer." } };
            var mixedReply = response(repeatedGesture, "drink", 1);
            const string backstoryCorrection = "I won a village bout once. I exaggerated; you are the tournament champion.";
            mixedReply["reply"] = "*" + repeatedGesture + "* " + backstoryCorrection + " *She drinks a cup of wine.*";
            ReadDictionaryList(mixedReply, "drinkingEvents")[0]["actionIndex"] = 1;
            var violations = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["type"] = "repeated_distinctive_phrase", ["match"] = repeatedGesture } };
            bool cleanedDrink = TryRemoveRepeatedStageDirectionsOnly(mixedReply, violations, new Dictionary<string, object> { ["knowsIdentity"] = true },
                priorGesture, "Mira", "Michael", out var cleanedReply, out var remainingViolations, out int removedCount);
            check("production_cleanup_preserves_drink_binding", cleanedDrink && removedCount == 1 && remainingViolations.Count == 0
                && amount(cleanedReply) == 1 && ReadInt(ReadDictionaryList(cleanedReply, "drinkingEvents")[0], "actionIndex", -1) == 0);
            check("cleanup_does_not_mutate_original_events", ReadInt(ReadDictionaryList(mixedReply, "drinkingEvents")[0], "actionIndex", -1) == 1);
            check("cleanup_preserves_backstory_and_in_character_correction", cleanedDrink
                && ReadString(cleanedReply, "reply", "").Contains(backstoryCorrection));
            check("mixed_action_order", ReignActionText.ToRichText("*He nods.* Yes. *He turns.*") == "<span style=\"Action\">He nods.</span> Yes. <span style=\"Action\">He turns.</span>");
            check("paragraphs", ReignActionText.ToRichText("*He nods.*\n\nYes.").Contains("</span>\n\nYes."));
            check("unmatched_literal", ReignActionText.ToRichText("Yes. *unfinished") == "Yes. *unfinished");
            check("escaped_marker", ReignActionText.ToRichText("\\*literal\\*") == "*literal*");
            check("markup_neutralized", !ReignActionText.ToRichText("<img src=evil> *<a href=evil>nod</a>*").Contains("<img")
                && !ReignActionText.ToRichText("<img src=evil> *<a href=evil>nod</a>*").Contains("<a "));
            check("no_retroactive_guess", ReignActionText.ToRichText("He nods. Yes.") == "He nods. Yes.");
            check("player_not_italicized", ReignActionText.ToRichText("*I nod.*", false) == "*I nod.*");
            check("truncation_balanced", !ReignActionText.ToRichText("*" + new string('x', 5000) + "*").Contains("<span"));
            check("unicode_preserved", ReignActionText.ToRichText("*Él hoche la tête.*") .Contains("Él hoche la tête."));

            string campaign = "intoxication_test_" + Guid.NewGuid().ToString("N");
            try
            {
                var payload = new Dictionary<string, object> { ["worldDay"] = 10d, ["timelineId"] = "main", ["turnId"] = "one" };
                var profile = new Dictionary<string, object> { ["attributes"] = new Dictionary<string, object> { ["endurance"] = 2 } };
                Func<string, string, string> drink = (hero, turn) =>
                {
                    payload["turnId"] = turn;
                    var parsed = response("He drinks a cup of wine.", "drink", 1);
                    return ApplyConversationDrinking(campaign, hero, payload, profile, null, parsed, ReadString(parsed, "reply", ""));
                };
                drink("npc", "one");
                check("commit_one", ReadDouble(ReadDictionary(ReadDictionary(payload, "intoxicationReceipt"), "state"), "drinks", 0) == 1);
                drink("npc", "one");
                check("replayed_turn_once", ReadDouble(ReadDictionary(ReadDictionary(payload, "intoxicationReceipt"), "state"), "drinks", 0) == 1);
                drink("other", "one");
                check("independent_characters", ReadDouble(ReadDictionary(ReadDictionary(payload, "intoxicationReceipt"), "state"), "drinks", 0) == 1);
                BuildIntoxicationPrompt(campaign, "npc", payload, profile, null);
                check("database_reopen", ReadDouble(ReadDictionary(payload, "intoxicationContext"), "drinks", 0) == 1);
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "intoxication_checkpoint");
                foreach (string turn in new[] { "two", "three", "four", "five" }) drink("npc", turn);
                check("threshold_overwhelmed", ReadBool(ReadDictionary(ReadDictionary(payload, "intoxicationReceipt"), "state"), "alcoholBlocked", false));
                string rejected = drink("npc", "six");
                check("cannot_drink_more", ReadDouble(ReadDictionary(payload, "intoxicationReceipt"), "consumed", -1) == 0 && rejected.Contains("No more"));
                payload["worldDay"] = 10d + 5d / 24d;
                drink("npc", "recovery_half");
                check("half_still_blocked", ReadDouble(ReadDictionary(payload, "intoxicationReceipt"), "consumed", -1) == 0);
                payload["worldDay"] = 10d + 6d / 24d;
                drink("npc", "recovery_below");
                check("below_half_can_drink", ReadDouble(ReadDictionary(payload, "intoxicationReceipt"), "consumed", -1) == 1);
                payload["timelineId"] = "branch_after_save_restore";
                BuildIntoxicationPrompt(campaign, "npc", payload, profile, null);
                check("restored_branch_inherits_state", ReadDouble(ReadDictionary(payload, "intoxicationContext"), "drinks", -1) == 3);
                payload["worldDay"] = 9d;
                drink("npc", "stale_request");
                check("stale_request_cannot_add", ReadDouble(ReadDictionary(payload, "intoxicationReceipt"), "consumed", -1) == 0);
                payload.Remove("worldDay");
                drink("npc", "missing_clock");
                check("missing_clock_cannot_add", ReadDouble(ReadDictionary(payload, "intoxicationReceipt"), "consumed", -1) == 0);
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "intoxication_checkpoint");
                payload["worldDay"] = 10d;
                BuildIntoxicationPrompt(campaign, "npc", payload, profile, null);
                check("save_sync_restores_drinks", ReadDouble(ReadDictionary(payload, "intoxicationContext"), "drinks", -1) == 1);
                payload["timelineId"] = "main";
                drink("npc", "two");
                check("save_sync_restores_receipts", ReadDouble(ReadDictionary(ReadDictionary(payload, "intoxicationReceipt"), "state"), "drinks", -1) == 2);
                System.Threading.Tasks.Parallel.For(0, 8, i =>
                {
                    var concurrentPayload = new Dictionary<string, object> { ["worldDay"] = 10d, ["turnId"] = "same_concurrent_turn" };
                    var concurrentReply = response("He drinks a cup of wine.", "drink", 1);
                    ApplyConversationDrinking(campaign, "concurrent_npc", concurrentPayload, profile, null, concurrentReply, ReadString(concurrentReply, "reply", ""));
                });
                BuildIntoxicationPrompt(campaign, "concurrent_npc", payload, profile, null);
                check("concurrent_retries_once", ReadDouble(ReadDictionary(payload, "intoxicationContext"), "drinks", -1) == 1);

                // Run the observed wrong-index and omitted-event shapes through storage
                // and prompt generation at a fixed campaign clock, without a provider.
                var observedProfile = new Dictionary<string, object> { ["name"] = "Mira", ["isFemale"] = true,
                    ["attributes"] = new Dictionary<string, object> { ["endurance"] = 3 } };
                var observedPayload = new Dictionary<string, object> { ["worldDay"] = 10d, ["turnId"] = "observed_one" };
                ApplyConversationDrinking(campaign, "observed_npc", observedPayload, observedProfile, null,
                    misindexed, ReadString(misindexed, "reply", ""));
                string firstReceipt = Json.Serialize(ReadDictionary(observedPayload, "intoxicationReceipt"));
                var secondObserved = omitted("*She drinks a cup of wine.*", new string[0]);
                ApplyConversationDrinking(campaign, "observed_npc", observedPayload, observedProfile, null,
                    secondObserved, ReadString(secondObserved, "reply", ""));
                check("changed_replay_keeps_original_receipt", firstReceipt == Json.Serialize(ReadDictionary(observedPayload, "intoxicationReceipt")));
                observedPayload["turnId"] = "observed_two";
                secondObserved = omitted("*She drinks a cup of wine.*", new string[0]);
                ApplyConversationDrinking(campaign, "observed_npc", observedPayload, observedProfile, null,
                    secondObserved, ReadString(secondObserved, "reply", ""));
                observedPayload["turnId"] = "observed_three";
                var thirdObserved = omitted("*She picks up her cup again and drinks a mouthful.*", new[] { "They are sharing wine." });
                ApplyConversationDrinking(campaign, "observed_npc", observedPayload, observedProfile, null,
                    thirdObserved, ReadString(thirdObserved, "reply", ""));
                string observedPrompt = BuildIntoxicationPrompt(campaign, "observed_npc", observedPayload, observedProfile, null);
                check("observed_sequence_accumulates_fractional_drinks", ReadDouble(ReadDictionary(observedPayload, "intoxicationContext"), "drinks", -1) == 1.5);
                check("observed_sequence_changes_next_prompt", ReadString(ReadDictionary(observedPayload, "intoxicationContext"), "stage", "") == "tipsy"
                    && observedPrompt.Contains("Noticeably expressive"));
            }
            catch (Exception ex) { rows.Add(new Dictionary<string, object> { ["id"] = "database_integration", ["passed"] = false, ["error"] = ex.ToString() }); }
            finally
            {
                // This exact random campaign was created by this invocation.
                // Drop its schemas/snapshot too, not just rows, to avoid fixture leaks.
                ReignPostgreSqlStorage.DropCampaign(campaign);
            }
            rows.AddRange(RunConversationDrinkingRegressionTests());
            return rows;
        }
    }
}
