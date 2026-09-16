using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> DrinkingFixture(string action, string serving = "", int count = 1)
        {
            var parsed = new Dictionary<string, object> { ["reply"] = "*" + action + "*" };
            if (serving.Length > 0)
                parsed["drinkingEvents"] = new List<Dictionary<string, object>> { new Dictionary<string, object>
                { ["actionIndex"] = 0, ["actionQuote"] = action, ["serving"] = serving, ["count"] = count, ["completed"] = true, ["alcohol"] = true } };
            return parsed;
        }

        private static List<Dictionary<string, object>> RunConversationDrinkingRegressionTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, object actual = null, object expected = null) =>
                rows.Add(new Dictionary<string, object> { ["id"] = id, ["name"] = id, ["caseId"] = id,
                    ["passed"] = passed, ["ok"] = passed, ["suite"] = "conversation_intoxication",
                    ["durationMs"] = 0, ["actual"] = actual, ["expected"] = expected });
            double ReadAmount(string action, DrinkVesselState vessel = null, string serving = "") =>
                ValidatedDrinkAmount(DrinkingFixture(action, serving), "*" + action + "*",
                    new List<Dictionary<string, object>>(), "Mira", true, vessel);
            DrinkVesselState WineCup(double? remaining = 1) => new DrinkVesselState
            { Scope = "fixture", Kind = "cup", Remaining = remaining, Beverage = ResolveDrinkBeverage("wine") };

            // The 77 language examples are the approved review corpus, with fixed,
            // independently authored expected values (not calculated by the recognizer).
            var cases = new (string Id, string Action, double Expected)[]
            {
                ("DRINK-001", "She takes a slow pull of kumis.", 0.25d),
                ("DRINK-002", "She slowly takes a pull of kumis.", 0.25d),
                ("DRINK-003", "She takes another measured swig of koumiss.", 0.25d),
                ("DRINK-004", "She sips hippocras.", 0.25d),
                ("DRINK-005", "She takes a mouthful of mead.", 0.25d),
                ("DRINK-006", "She swallows a mouthful of cider.", 0.25d),
                ("DRINK-007", "She drinks a small sip of wine.", 0.25d),
                ("DRINK-008", "After a pause, Mira sips the ale.", 0.25d),
                ("DRINK-009", "With a smile, she takes a sip of wine.", 0.25d),
                ("DRINK-010", "Mira the Smith takes a sip of wine.", 0.25d),
                ("DRINK-011", "She smiles, then drinks a mouthful of wine.", 0.25d),
                ("DRINK-012", "She offers him wine, then sips from her own cup of wine.", 0.25d),
                ("DRINK-013", "She drinks wine, taking a single small sip.", 0.25d),
                ("DRINK-014", "She does not answer, but takes a sip of wine.", 0.25d),
                ("DRINK-015", "She sips wine while he drinks water.", 0.25d),
                ("DRINK-016", "She picks up the cup, lifts it toward him in a brief acknowledging gesture, and takes a slow drink — kumis, sour and sharp, the kind that bites the back of the throat.", 0.25d),
                ("DRINK-017", "She takes a slow pull of the fresh kumis and sets it down, wiping her lip with the back of her hand.", 0.25d),
                ("DRINK-018", "She drinks half a cup of wine.", 0.5d),
                ("DRINK-019", "She takes two sips of wine.", 0.5d),
                ("DRINK-020", "She takes two slow pulls of kumis.", 0.5d),
                ("DRINK-021", "She drinks an entire freshly filled cup of wine.", 1d),
                ("DRINK-022", "She downs a full new tankard of ale.", 1d),
                ("DRINK-023", "She throws back the whole freshly filled cup of mead.", 1d),
                ("DRINK-024", "She drains a full new goblet of hippocras.", 1d),
                ("DRINK-025", "She drinks three sips of wine.", 0.75d),
                ("DRINK-026", "She drinks two full cups of wine.", 2d),
                ("DRINK-027", "She drinks half a mouthful of wine.", 0.125d),
                ("DRINK-028", "She drinks the wine.", 0.25d),
                ("DRINK-029", "She drinks the wine.", 1d),
                ("DRINK-030", "She sips the wine diluted with water.", 0.25d),
                ("DRINK-031", "She takes a mouthful of kumis, the fermented mare's milk.", 0.25d),
                ("DRINK-032", "She swallows a sip of tea explicitly mixed with brandy.", 0.25d),
                ("DRINK-033", "She drinks water beside a cup of wine.", 0d),
                ("DRINK-034", "She sips tea while talking about wine.", 0d),
                ("DRINK-035", "She sips wine, then takes another sip from the same cup.", 0.5d),
                ("DRINK-036", "She takes a mouthful of wine and swallows it.", 0.25d),
                ("DRINK-037", "She drinks wine from a full cup until it is empty.", 1d),
                ("DRINK-038", "She sips wine. He drains his ale. She takes another sip of wine.", 0.5d),
                ("DRINK-039", "She picks up the cup again, watching him over the rim.", 0d),
                ("DRINK-040", "She raises the cup to her lips.", 0d),
                ("DRINK-041", "She smells the wine.", 0d),
                ("DRINK-042", "She tastes the wine and spits it out.", 0d),
                ("DRINK-043", "She wets her lips with wine without swallowing.", 0d),
                ("DRINK-044", "She offers him a cup of wine.", 0d),
                ("DRINK-045", "She pours wine into her cup.", 0d),
                ("DRINK-046", "She refills the wine cup.", 0d),
                ("DRINK-047", "She spills the wine onto the floor.", 0d),
                ("DRINK-048", "She empties the cup into the bucket.", 0d),
                ("DRINK-049", "She refuses to drink wine.", 0d),
                ("DRINK-050", "She almost drinks the wine.", 0d),
                ("DRINK-051", "She tries to drink but cannot swallow.", 0d),
                ("DRINK-052", "She pretends to sip wine.", 0d),
                ("DRINK-053", "She will drink after the toast.", 0d),
                ("DRINK-054", "She would drink if he asked.", 0d),
                ("DRINK-055", "She recalls drinking wine yesterday.", 0d),
                ("DRINK-056", "She says, \"I drink wine every evening.\"", 0d),
                ("DRINK-057", "She watches him drink wine.", 0d),
                ("DRINK-058", "She hands him wine and he drinks.", 0d),
                ("DRINK-059", "The waitress drinks wine.", 0d),
                ("DRINK-060", "She swallows her pride beside the wine.", 0d),
                ("DRINK-061", "She finishes her story about wine.", 0d),
                ("DRINK-062", "She wipes down the wine jug.", 0d),
                ("DRINK-063", "She drinks from an empty wine cup.", 0d),
                ("DRINK-064", "She drinks water.", 0d),
                ("DRINK-065", "She drinks plain mare's milk.", 0d),
                ("DRINK-066", "She drinks sherbet.", 0d),
                ("DRINK-067", "She drinks non-alcoholic wine.", 0d),
                ("DRINK-068", "She drinks alcohol-free beer.", 0d),
                ("DRINK-069", "She swallows wine vinegar.", 0d),
                ("DRINK-070", "She eats stew cooked with wine.", 0d),
                ("DRINK-071", "She sips the tavern special.", 0d),
                ("DRINK-072", "She sips Golden Mane, the family's honey wine.", 0.25d),
                ("DRINK-073", "She sips from her cup.", 0.25d),
                ("DRINK-074", "She sips from her cup.", 0d),
                ("DRINK-075", "She drinks a mug of kvass.", 0d),
                ("DRINK-076", "She sips non-alcoholic kvass.", 0d),
                ("DRINK-077", "She sips the explicitly alcoholic kvass.", 0.25d),
            };
            foreach (var test in cases)
            {
                string serving = test.Id == "DRINK-016" ? "sip" : test.Id == "DRINK-017" ? "half" : test.Id == "DRINK-029" ? "drink" : "";
                var parsed = DrinkingFixture(test.Action, serving);
                if (test.Id == "DRINK-017") ReadDictionaryList(parsed, "drinkingEvents")[0]["actionIndex"] = 7;
                var evidence = new List<Dictionary<string, object>>();
                var vessel = test.Id == "DRINK-073" || test.Id == "DRINK-029" ? WineCup() : new DrinkVesselState();
                double actual = ValidatedDrinkAmount(parsed, ReadString(parsed, "reply", ""), evidence, "Mira", true, vessel);
                Check(test.Id, Math.Abs(actual - test.Expected) < .000001, new { amount = actual, evidence }, test.Expected);
                if (test.Expected == 0)
                    Check(test.Id + "_metadata_cannot_force", ReadAmount(test.Action, null, "drink") == 0);
            }

            void Sequence(string id, string[] actions, double[] expected, DrinkVesselState vessel = null)
            {
                vessel = vessel ?? new DrinkVesselState();
                var actual = actions.Select(a => ReadAmount(a, vessel)).ToArray();
                Check(id, actual.SequenceEqual(expected), actual, expected);
            }
            Sequence("DRINK-078", new[] { "She accepts a full cup of wine.", "She sips from her cup.",
                "She sips from her cup.", "She finishes the rest of her cup." }, new[] { 0d, .25, .25, .5 });
            Sequence("DRINK-079", new[] { "She accepts a full cup of wine.", "She drinks half of her cup.",
                "She refills her cup with wine.", "She finishes her cup." }, new[] { 0d, .5, 0d, 1d });
            Sequence("DRINK-080", new[] { "She sips wine.", "She switches to a cup of water.", "She sips from her cup." }, new[] { .25, 0d, 0d });
            Sequence("DRINK-081", new[] { "She drinks half of what remains.", "She drinks half of what remains." }, new[] { .5, .25 }, WineCup());
            Sequence("DRINK-082", new[] { "She picks up her empty wine cup.", "She raises her cup.", "She raises her cup again." }, new[] { 0d, 0d, 0d });
            Sequence("same_turn_refill_order", new[] { "She sips wine from her cup, then refills her cup with wine, then finishes her cup." }, new[] { 1.25 }, WineCup());
            Sequence("handoff_invalidates", new[] { "She hands him her wine cup.", "She sips from her cup." }, new[] { 0d, 0d }, WineCup());
            Sequence("spill_remainder", new[] { "She spills half of her cup.", "She finishes her wine cup." }, new[] { 0d, .5 }, WineCup());
            Sequence("unknown_pour_does_not_invent_volume", new[] { "She pours wine into her cup.", "She drinks half of what remains." }, new[] { 0d, 0d });
            Sequence("bulk_unknown_completion", new[] { "She accepts a bottle of wine.", "She drains her bottle of wine." }, new[] { 0d, 0d });
            Sequence("bulk_explicit_portion", new[] { "She accepts a bottle of wine.", "She takes a sip of wine from the bottle." }, new[] { 0d, .25 });
            Sequence("empty_cannot_drink", new[] { "She finishes her wine cup.", "She sips from her cup." }, new[] { 1d, 0d }, WineCup());
            Sequence("other_actor_fill_does_not_change_cup", new[] { "He fills his cup with water.", "She sips from her cup." }, new[] { 0d, .25 }, WineCup());
            Sequence("pour_for_other_actor", new[] { "She pours water into his cup.", "She sips from her cup." }, new[] { 0d, .25 }, WineCup());
            Sequence("unknown_named_drink_never_borrows_wine", new[] { "She sips Golden Mane." }, new[] { 0d }, WineCup());
            Sequence("unknown_special_never_borrows_wine", new[] { "She sips the tavern special." }, new[] { 0d }, WineCup());
            Sequence("multiple_vessels_invalidate", new[] { "She considers both wine cups.", "She sips from her cup." }, new[] { 0d, 0d }, WineCup());
            Sequence("partial_fill", new[] { "She fills her cup halfway with wine.", "She finishes her cup." }, new[] { 0d, .5 });
            Sequence("bulk_fraction_unknown", new[] { "She drinks half a bottle of wine." }, new[] { 0d });
            Sequence("empty_description_updates_known_cup", new[] { "She drinks from her empty wine cup.", "She sips from her cup." }, new[] { 0d, 0d }, WineCup());
            Sequence("known_alcoholic_kvass_variant", new[] { "She accepts a full cup of alcoholic kvass.", "She picks up her cup of kvass.", "She sips the kvass." }, new[] { 0d, 0d, .25 });
            Sequence("known_nonalcoholic_kvass_variant", new[] { "She accepts a full cup of non-alcoholic kvass.", "She sips the kvass." }, new[] { 0d, 0d });
            Sequence("different_kvass_variant_unknown", new[] { "She accepts a full cup of alcoholic kvass.", "She sips different kvass." }, new[] { 0d, 0d });
            Sequence("new_kvass_variant_unknown", new[] { "She accepts a full cup of alcoholic kvass.", "She accepts a new cup of kvass.", "She sips the kvass." }, new[] { 0d, 0d, 0d });
            Sequence("refill_pronoun", new[] { "She drinks half of her cup.", "She refills it.", "She finishes it." }, new[] { .5, 0d, 1d }, WineCup());
            Sequence("spill_pronoun", new[] { "She spills it all.", "She sips from her cup." }, new[] { 0d, 0d }, WineCup());
            Sequence("accepts_cup_from_player", new[] { "She accepts a full cup of wine from him.", "She sips from her cup." }, new[] { 0d, .25 });
            Sequence("takes_cup_from_player", new[] { "She takes his full cup of wine.", "She sips from her cup." }, new[] { 0d, .25 });
            var switched = WineCup();
            ReadAmount("She refills it with water.", switched);
            ReadAmount("She sips from her cup.", switched);
            Check("pronoun_refill_retains_vessel_kind", switched.Kind == "cup" && switched.Remaining == .75
                && switched.Beverage.Alcohol == DrinkAlcohol.No, DrinkVesselRecord(switched));
            Sequence("drink_from_known_partial_cup", new[] { "She takes a drink from her own cup." }, new[] { .5 }, WineCup(.5));
            Sequence("full_word_does_not_refill", new[] { "She finishes her full cup of wine." }, new[] { .5 }, WineCup(.5));

            var runtimeActions = new[] {
                "She picks up the cup, lifts it toward him in a brief acknowledging gesture, and takes a slow drink — kumis, sour and sharp, the kind that bites the back of the throat.",
                "She lets the words hang for a moment, then picks the cup up again and takes a slow pull of kumis.",
                "She picks up the kumis and takes a slow pull, then sets it down hard enough to make a point.",
                "She takes a slow pull of kumis and sets the cup down, then fixes him with a look that's warm and calculating in equal measure.",
                "She takes a slow pull of the fresh kumis and sets it down, wiping her lip with the back of her hand."
            };
            for (int n = 0; n < runtimeActions.Length; n++)
                Check("captured_kumis_" + n, ReadAmount(runtimeActions[n], null, n == 4 ? "half" : "sip") == .25);
            foreach (string beverage in new[] { "kumis", "koumiss", "airag", "fermented mare's milk", "hippocras", "hypocras", "honey wine", "watered wine", "ale", "perry", "arrack" })
                Check("beverage_alias_" + beverage, ReadAmount("She takes a sip of " + beverage + ".") == .25);
            foreach (string adjective in new[] { "slow", "long", "deep", "small", "tiny", "careful", "measured", "deliberate", "tentative", "cautious", "hearty", "quick", "swift", "solid", "real" })
                Check("modified_pull_" + adjective, ReadAmount("She takes a " + adjective + " pull of kumis.") == .25);
            foreach (string verb in new[] { "quaffs", "imbibes", "chugs", "consumes", "sups", "slurps", "knocks back", "throws back", "tosses back", "polishes off" })
                Check("consumption_verb_" + verb, ReadAmount("She " + verb + " a full cup of wine.") == 1);
            Check("three_quarters", ReadAmount("She drinks three quarters of a cup of wine.") == .75);
            Check("numeric_fraction", ReadAmount("She drinks 3/4 of a cup of wine.") == .75);
            Check("half_mouthful", ReadAmount("She takes half a mouthful of wine.") == .125);
            Check("negative_tail_not_a_sip", ReadAmount("She drinks a mouthful of wine, not a sip.") == .25);
            Check("finite_large_text_count_rejected", ReadAmount("She takes 9999999999999999999999 sips of wine.") == 0);
            Check("twenty_sips_bounded", ReadAmount("She takes twenty sips of wine.") == 5);
            Check("twenty_one_sips_rejected", ReadAmount("She takes 21 sips of wine.") == 0);
            Check("quoted_second_sentence_rejected", ReadAmount("She says, \"Listen. She drinks wine.\"") == 0);
            Check("single_quoted_report_rejected", ReadAmount("She says, 'She drinks wine.'") == 0);
            Check("explicit_non_alcohol_overrides_event", ReadAmount("She takes a sip of alcohol-free mead.", null, "drink") == 0);
            Check("nonalcoholic_base_mixed_with_brandy", ReadAmount("She sips alcohol-free beer mixed with brandy.") == .25);
            Check("wine_mixed_with_nonalcoholic_beer", ReadAmount("She sips wine mixed with alcohol-free beer.") == .25);
            Check("all_nonalcoholic_mixture", ReadAmount("She sips tea mixed with alcohol-free brandy.") == 0);
            Check("unknown_named_mouthful", ReadAmount("She takes a mouthful of Golden Mane.", WineCup()) == 0);
            Check("serves_drinks_not_consumption", ReadAmount("She serves drinks of wine.", null, "drink") == 0);

            const string two = "She sips wine, then takes another sip from the same cup.";
            var multi = DrinkingFixture(two);
            multi["drinkingEvents"] = new List<Dictionary<string, object>> {
                new Dictionary<string, object> { ["actionIndex"] = 0, ["actionQuote"] = two, ["occurrenceIndex"] = 0, ["serving"] = "sip", ["count"] = 1, ["completed"] = true, ["alcohol"] = true },
                new Dictionary<string, object> { ["actionIndex"] = 0, ["actionQuote"] = two, ["occurrenceIndex"] = 1, ["serving"] = "sip", ["count"] = 1, ["completed"] = true, ["alcohol"] = true }
            };
            var multiEvents = ReadDictionaryList(multi, "drinkingEvents");
            multiEvents.Add(new Dictionary<string, object>(multiEvents[0]));
            multi["drinkingEvents"] = multiEvents;
            var multiEvidence = new List<Dictionary<string, object>>();
            Check("occurrence_metadata_and_duplicate", ValidatedDrinkAmount(multi, "*" + two + "*", multiEvidence, "Mira", true) == .5
                && multiEvidence.Count(e => ReadDouble(e, "amount", 0) > 0) == 2, multiEvidence);
            var exact = DrinkingFixture("She takes a sip of kumis.", "sip");
            ReadDictionaryList(exact, "drinkingEvents")[0]["actionIndex"] = 8;
            Check("DRINK-086", ValidatedDrinkAmount(exact, ReadString(exact, "reply", ""), new List<Dictionary<string, object>>(), "Mira", true) == .25);
            var cleanup = DrinkingFixture(two, "sip");
            cleanup["reply"] = "*She nods.* *" + two + "*";
            ReadDictionaryList(cleanup, "drinkingEvents")[0]["actionIndex"] = 1;
            RebindDrinkingEventsAfterCleanup(cleanup, ReadString(cleanup, "reply", ""), "*" + two + "*", "Mira");
            Check("DRINK-087", ValidatedDrinkAmount(cleanup, "*" + two + "*", new List<Dictionary<string, object>>(), "Mira", true) == .5);
            RebindDrinkingEventsAfterCleanup(cleanup, "*" + two + "*", "*She nods.*", "Mira");
            Check("DRINK-088", ReadDictionaryList(cleanup, "drinkingEvents").Count == 0);
            const string story = "I once drank three cups at my village contest. Actually, I exaggerated; it was one.";
            var backstory = new Dictionary<string, object> { ["reply"] = story + " *She remembers drinking wine years ago.*" };
            Check("DRINK-090", ValidatedDrinkAmount(backstory, ReadString(backstory, "reply", ""), new List<Dictionary<string, object>>(), "Mira", true) == 0
                && ReadString(backstory, "reply", "").StartsWith(story, StringComparison.Ordinal));

            var sessionPayload = new Dictionary<string, object> { ["conversationSessionId"] = "fixture", ["locationId"] = "tavern" };
            var current = WineCup(.75); current.Scope = "fixture|tavern"; current.Hour = 12;
            Check("same_scene_context", CurrentDrinkVessel(current, sessionPayload, 12).Remaining == .75);
            Check("expired_scene_context", CurrentDrinkVessel(current, sessionPayload, 14).Beverage.Name == "");
            Check("missing_scene_identity", CurrentDrinkVessel(current, new Dictionary<string, object>(), 12).Beverage.Name == "");
            sessionPayload["conversationSessionId"] = "other";
            Check("different_scene_context", CurrentDrinkVessel(current, sessionPayload, 12).Beverage.Name == "");
            Check("old_state_migration", RecoverIntoxication(new Dictionary<string, object> { ["drinks"] = 2d }, 2, 12).Vessel.Beverage.Name == "");
            Check("unknown_volume_roundtrip", !ReadDrinkVessel(TryParseJsonObject(Json.Serialize(DrinkVesselRecord(WineCup(null))))).Remaining.HasValue);
            Check("known_volume_roundtrip", ReadDrinkVessel(TryParseJsonObject(Json.Serialize(DrinkVesselRecord(WineCup(.5))))).Remaining == .5);
            Check("prompt_contract_occurrences", DrinkingOutputContract.Contains("occurrenceIndex") && DrinkingOutputContract.Contains("remaining contents"));
            Check("prompt_unknown_volume", IntoxicationPrompt(new IntoxicationState { Threshold = 5, Vessel = WineCup(null) }).Contains("unknown, not a full cup"));
            RunDrinkingVesselStorageContracts(Check);
            return rows;
        }

        private static void RunDrinkingVesselStorageContracts(Action<string, bool, object, object> check)
        {
            string campaign = "drink_vessel_test_" + Guid.NewGuid().ToString("N");
            try
            {
                var profile = new Dictionary<string, object> { ["name"] = "Mira", ["isFemale"] = true,
                    ["attributes"] = new Dictionary<string, object> { ["endurance"] = 2 } };
                var payload = new Dictionary<string, object> { ["worldDay"] = 10d, ["timelineId"] = "main",
                    ["conversationSessionId"] = "vessel-session", ["locationId"] = "tavern", ["turnId"] = "fill" };
                Dictionary<string, object> Turn(string turn, string action, string hero = "mira")
                {
                    payload["turnId"] = turn;
                    var parsed = DrinkingFixture(action);
                    ApplyConversationDrinking(campaign, hero, payload, profile, null, parsed, ReadString(parsed, "reply", ""));
                    return ReadDictionary(payload, "intoxicationReceipt");
                }
                double Remaining(Dictionary<string, object> receipt) => ReadDouble(ReadDictionary(ReadDictionary(receipt, "state"), "vessel"), "remaining", -1);
                Turn("fill", "She accepts a full cup of wine.");
                var first = Turn("sip-one", "She sips from her cup.");
                var second = Turn("sip-two", "She sips from her cup.");
                check("DRINK-083", ReadDouble(ReadDictionary(second, "state"), "drinks", -1) == .5 && Remaining(second) == .5, second, .5);
                string committed = Json.Serialize(second);
                var replay = Turn("sip-two", "She refills her cup with water.");
                check("replay_does_not_refill", Json.Serialize(replay) == committed, replay, committed);
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "vessel_checkpoint");
                var finish = Turn("finish", "She finishes her cup.");
                check("persisted_finish_remainder", ReadDouble(finish, "consumed", -1) == .5 && Remaining(finish) == 0, finish, .5);
                var empty = Turn("empty", "She sips from her cup.");
                check("persisted_empty_cup", ReadDouble(empty, "consumed", -1) == 0 && Remaining(empty) == 0, empty, 0);
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "vessel_checkpoint");
                BuildIntoxicationPrompt(campaign, "mira", payload, profile, null);
                var restored = ReadDictionary(payload, "intoxicationContext");
                check("DRINK-085", ReadDouble(restored, "drinks", -1) == .5 && ReadDouble(ReadDictionary(restored, "vessel"), "remaining", -1) == .5, restored, .5);
                var restoredFinish = Turn("finish", "She finishes her cup.");
                check("restored_future_receipt_removed", ReadDouble(restoredFinish, "consumed", -1) == .5, restoredFinish, .5);
                Turn("refill", "She refills her cup with wine.");
                payload["worldDay"] = 9d;
                var stale = Turn("stale-refill", "She refills her cup with water.");
                check("stale_turn_cannot_replace_vessel", ReadString(ReadDictionary(ReadDictionary(stale, "state"), "vessel"), "family", "") == "wine" && Remaining(stale) == 1, stale, "wine");
                payload.Remove("worldDay");
                var noClock = Turn("missing-clock", "She sips from her cup.");
                check("missing_clock_cannot_change_volume", Remaining(noClock) == 1 && ReadDouble(noClock, "consumed", -1) == 0, noClock, 1);
                payload["worldDay"] = 10d;

                Turn("concurrent-fill", "She accepts a full cup of wine.", "concurrent");
                System.Threading.Tasks.Parallel.For(0, 8, n =>
                {
                    var p = new Dictionary<string, object>(payload) { ["turnId"] = "concurrent-sip" };
                    var parsed = DrinkingFixture("She sips from her cup.");
                    ApplyConversationDrinking(campaign, "concurrent", p, profile, null, parsed, ReadString(parsed, "reply", ""));
                });
                BuildIntoxicationPrompt(campaign, "concurrent", payload, profile, null);
                var concurrent = ReadDictionary(payload, "intoxicationContext");
                int receipts;
                using (var connection = OpenCampaignConnection(campaign))
                    receipts = QuerySql(connection, "SELECT turn_key FROM conversation_drinking_turns WHERE hero_id=$hero AND turn_key=$turn;",
                        new Dictionary<string, object> { ["hero"] = "concurrent", ["turn"] = "main:concurrent-sip" }).Count;
                check("DRINK-084", ReadDouble(concurrent, "drinks", -1) == .25 && ReadDouble(ReadDictionary(concurrent, "vessel"), "remaining", -1) == .75
                    && receipts == 1, concurrent, .25);
                for (int n = 0; n < 5; n++) Turn("full-" + n, "She drinks a cup of wine.", "blocked");
                var blocked = Turn("blocked", "She takes a sip of kumis.", "blocked");
                check("DRINK-089", ReadDouble(blocked, "consumed", -1) == 0 && ReadBool(ReadDictionary(blocked, "state"), "alcoholBlocked", false), blocked, 0);
                var blockedFill = Turn("blocked-fill", "She refills her cup with kumis and takes a sip of kumis.", "blocked");
                check("suppressed_reply_cannot_store_refill", ReadString(ReadDictionary(ReadDictionary(blockedFill, "state"), "vessel"), "beverage", "") == "", blockedFill, "");
            }
            catch (Exception ex) { check("vessel_storage_integration", false, ex.ToString(), null); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
        }
    }
}
