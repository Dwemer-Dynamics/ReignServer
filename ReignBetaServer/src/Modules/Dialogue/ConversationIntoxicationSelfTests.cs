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
            check("bounded_quantity", ValidatedDrinkAmount(invalid, ReadString(invalid, "reply", ""), new List<Dictionary<string, object>>()) == 0);
            var exaggerated = response("He drinks a cup of wine.", "drink", 10);
            check("quantity_grounded", ValidatedDrinkAmount(exaggerated, ReadString(exaggerated, "reply", ""), new List<Dictionary<string, object>>()) == 0);
            var plural = response("He drinks two cups of wine.", "drink", 2);
            check("explicit_plural", ValidatedDrinkAmount(plural, ReadString(plural, "reply", ""), new List<Dictionary<string, object>>()) == 2);
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
            }
            catch (Exception ex) { rows.Add(new Dictionary<string, object> { ["id"] = "database_integration", ["passed"] = false, ["error"] = ex.ToString() }); }
            finally
            {
                // This exact random campaign was created by this invocation.
                // Drop its schemas/snapshot too, not just rows, to avoid fixture leaks.
                ReignPostgreSqlStorage.DropCampaign(campaign);
            }
            return rows;
        }
    }
}
