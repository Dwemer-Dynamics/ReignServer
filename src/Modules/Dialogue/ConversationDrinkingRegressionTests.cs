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

            // Language examples use fixed,
            // independently authored expected values (not calculated by the recognizer).
            var cases = new (string Id, string Action, double Expected)[]
            {
                ("DRINK-001", "She takes a slow pull of kumis.", 0.25d),
                ("DRINK-101", "She pulls a drink of whiskey, then hands the skin back.", 0.25d),
                ("DRINK-102", "She picks up the whiskey skin. A pull, short, then sets it down.", 0.25d),
                ("DRINK-103", "She takes the skin of whiskey when he hands it back. A pull, short, handed over.", 0.25d),
                ("DRINK-104", "She swallows it before it becomes an answer.", 0d),
                ("DRINK-105", "He picks up the whiskey skin. A pull, short, handed over.", 0d),
                ("DRINK-106", "She considers taking the whiskey skin. A pull would help.", 0d),
                ("DRINK-107", "She picks up the reins. A pull, short and firm.", 0d),
                ("DRINK-108", "She does not pick up the whiskey skin. A pull, imagined only.", 0d),
                ("DRINK-109", "She picks up the whiskey skin and hands it to him. He takes a pull.", 0d),
                ("DRINK-110", "She picks up the whiskey skin and hands it to him. A pull, short, then handed back.", 0d),
                ("DRINK-111", "She picks up the whiskey skin then puts it down. A pull, only imagined.", 0d),
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
                ("DRINK-091", "She picks up the wine again, this time taking a measured sip.", 0.25d),
                ("DRINK-092", "She smiles, taking a measured sip of wine.", 0.25d),
                ("DRINK-093", "She took a measured sip of wine.", 0.25d),
                ("DRINK-094", "She smiles without taking a sip of wine.", 0d),
                ("DRINK-095", "She considers taking a sip of wine.", 0d),
                ("DRINK-096", "She watches him taking a sip of wine.", 0d),
                ("DRINK-097", "She drinks wine, taking another small sip.", 0.5d),
                ("DRINK-098", "She recalls that she took a sip of wine yesterday.", 0d),
                ("DRINK-099", "She says, \"I took a sip of wine.\"", 0d),
                ("DRINK-100", "She is planning on taking a sip of wine.", 0d),
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
            Sequence("drink_from_known_partial_cup", new[] { "She takes a drink from her own cup." }, new[] { .25 }, WineCup(.5));
            Sequence("full_word_does_not_refill", new[] { "She finishes her full cup of wine." }, new[] { .5 }, WineCup(.5));
            Sequence("refilled_pickup_restores_capacity", new[] { "She picks up her refilled cup of wine.",
                "She takes a sip from her cup.", "She takes a sip from her cup.", "She takes a sip from her cup." },
                new[] { 0d, .25, .25, .25 });
            Sequence("handshake_does_not_rebind_vessel", new[] { "She accepts the handshake for what it is.",
                "She takes a sip from her cup." }, new[] { 0d, .25 }, WineCup());
            Sequence("rope_does_not_rebind_vessel", new[] { "She accepts the rope for what it is.",
                "She takes a sip from her cup." }, new[] { 0d, .25 }, WineCup());
            Check("swallow_hard_is_same_ingestion",
                ReadAmount("She takes a mouthful of wine and swallows hard.") == .25);
            var offered = CurrentPlayerOfferedBeverage(new Dictionary<string, object>
            { ["playerText"] = "I offer her my whiskey skin and hold it out to her." });
            var offeredEvidence = new List<Dictionary<string, object>>();
            var acceptedOffer = DrinkingFixture("She accepts the skin and takes a short pull.");
            Check("accepted_player_whiskey_offer", ValidatedDrinkAmount(acceptedOffer,
                ReadString(acceptedOffer, "reply", ""), offeredEvidence, "Mira", true, null, offered) == .25
                && offeredEvidence.Any(x => ReadString(x, "beverageSource", "") == "accepted_player_offer"), offeredEvidence);
            Check("ambiguous_player_offer_rejected", CurrentPlayerOfferedBeverage(new Dictionary<string, object>
            { ["playerText"] = "I offer her a skin of whiskey and wine." }).Alcohol == DrinkAlcohol.Unknown);
            var compoundHandoffs = new[] {
                "*I take a seat and wait for her to sit also, pulling my skin of whiskey out and taking a drink before passing it to her.*",
                "*I take a drink from my whiskey skin and hand it to her*",
                "*I smile as I grab the whiskey skin and take a drink before passing it to her*"
            };
            var ellipticalActions = new[] {
                "She takes the skin. Drinks. Her thumb wipes the mouth of it before passing it back.",
                "She takes the skin. Drinks — a real one, not the measured sip. She hands it back.",
                "She takes the skin. Drinks — shorter this time, the kind of pull that's more about holding something warm than needing it. She hands it back."
            };
            for (int i = 0; i < compoundHandoffs.Length; i++)
            {
                var contents = CurrentPlayerOfferedBeverage(TestDict("playerText", compoundHandoffs[i]));
                var evidence = new List<Dictionary<string, object>>();
                double amount = ValidatedDrinkAmount(DrinkingFixture(ellipticalActions[i]), "*" + ellipticalActions[i] + "*",
                    evidence, "Corein", true, null, contents);
                var accounting = BindAcceptedDrinkEvents(evidence, "lord_5_10", "corein-handoff-" + i, false, false, amount);
                Check("corein_compound_handoff_and_elliptical_drink_" + i, amount == .25
                    && ReadInt(accounting, "consumptionEvents", 0) == 1
                    && ReadInt(accounting, "alcoholConsumptionEvents", 0) == 1, evidence, .25);
            }
            Check("corein_bare_drink_with_local_whiskey", ReadAmount(
                "She takes the skin. Drinks. The whiskey burns, and she hands it back.") == .25);
            Check("generic_drink_uses_known_held_vessel", ReadAmount("She takes a drink.", WineCup()) == .25);
            Check("generic_named_unknown_drink_never_borrows_wine", ReadAmount("She takes a drink of an unknown preparation.", WineCup()) == 0);
            foreach (string negative in new[] {
                "He takes the whiskey skin. Drinks.", "She considers taking the whiskey skin. Drinks.",
                "She takes the whiskey skin and hands it to him. Drinks.", "She takes the reins. Drinks.",
                "She takes the whiskey skin. He nods. Drinks.", "She takes the whiskey skin. Drinks only in her imagination.",
                "She takes the whiskey skin. Never drinks.", "She takes the whiskey skin. She says, \"Drinks.\""
            })
                Check("elliptical_drink_negative_" + PromptHash(negative).Substring(0, 8), ReadAmount(negative) == 0);
            foreach (string negative in new[] {
                "*I watch him take my whiskey skin and pass it to her.*",
                "*I ask him to take my whiskey skin and pass it to her.*",
                "*I take my whiskey skin while he passes it to her.*",
                "*I take my whiskey skin and hand it to her tomorrow.*",
                "*I take my whiskey skin and do not hand it to her.*",
                "*I take a skin of whiskey and wine and pass it to her.*",
                "I say, \"I take my whiskey skin and pass it to her.\""
            })
                Check("compound_handoff_negative_" + PromptHash(negative).Substring(0, 8),
                    CurrentPlayerOfferedBeverage(TestDict("playerText", negative)).Alcohol == DrinkAlcohol.Unknown);
            const string mixedPlanAndOffer = "I would accept a stated fee, but I would refuse an open-ended favor. No fee has been agreed. "
                + "*I swallow my annoyance. I slide my wine cup within your reach without taking another drink.* You may have the wine if you want it.";
            var slidWine = CurrentPlayerOfferedBeverage(TestDict("playerText", mixedPlanAndOffer));
            var slidEvidence = new List<Dictionary<string, object>>();
            const string acceptedSlide = "She pulls the cup the last inch toward her and takes a short pull, setting it down with a quiet click against the table.";
            double slidAmount = ValidatedDrinkAmount(DrinkingFixture(acceptedSlide), "*" + acceptedSlide + "*", slidEvidence, "Mira", true, null, slidWine);
            var slidEvents = BindAcceptedDrinkEvents(slidEvidence, "mira", "slid-cup", false, false, slidAmount);
            Check("native_present_handoff_survives_unrelated_modal_plan", slidAmount == .25
                && ReadInt(slidEvents, "consumptionEvents", 0) == 1
                && ReadInt(slidEvents, "alcoholConsumptionEvents", 0) == 1
                && slidEvidence.Any(x => ReadString(x, "beverageSource", "") == "accepted_player_offer"), slidEvidence);
            foreach (string noOffer in new[] { "I would slide my wine cup within your reach.", "Yesterday I handed her my wine cup.",
                "*I do not offer her my wine cup.*", "*He passes her a wine cup.*", "I say, \"I offer her my wine cup.\"",
                "*I slide my wine cup away from the map.*", "*I set my wine cup down without drinking.*",
                "*I offer her my wine cup. I offer her my whiskey flask.*" })
                Check("handoff_negative_" + PromptHash(noOffer).Substring(0, 8),
                    CurrentPlayerOfferedBeverage(TestDict("playerText", noOffer)).Alcohol == DrinkAlcohol.Unknown);
            foreach (string gesture in new[] { "She lifts the cup slightly, a gesture more of acknowledgment than drinking, and sets it back.",
                "She raises her wine cup in salute instead of drinking.", "She nods rather than taking a sip of wine." })
                Check("native_gesture_not_consumption_" + PromptHash(gesture).Substring(0, 8),
                    ReadNarratedDrinks(gesture, "Mira", true).Count == 0);
            const string gestureThenSip = "She raises the wine cup in acknowledgment rather than drinking, then takes a small sip of wine.";
            Check("comparison_does_not_hide_later_actual_sip", ReadNarratedDrinks(gestureThenSip, "Mira", true).Count == 1
                && ReadAmount(gestureThenSip) == .25);
            Check("adjacent_sentence_beverage_context",
                ReadAmount("Whiskey glints in the open skin. She takes a drink from it.") == .25);
            var flexible = DrinkingFixture("She takes a sip of wine.", "sip");
            ReadDictionaryList(flexible, "drinkingEvents")[0]["evidenceQuote"] = "sip of wine";
            var flexibleEvidence = new List<Dictionary<string, object>>();
            Check("unique_contained_evidence_span", ValidatedDrinkAmount(flexible,
                ReadString(flexible, "reply", ""), flexibleEvidence, "Mira", true) == .25
                && flexibleEvidence.Any(x => ReadString(x, "binding", "") == "unique_evidence_span"), flexibleEvidence);
            var unknownEvidence = new List<Dictionary<string, object>>();
            ValidatedDrinkAmount(DrinkingFixture("She drains the bottle."), "*She drains the bottle.*",
                unknownEvidence, "Mira", true);
            Check("unknown_bulk_completion_is_audited_unquantified", unknownEvidence.Any(x =>
                ReadString(x, "measurementQuality", "") == "unquantified_consumption"), unknownEvidence);

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
            Check("prompt_contract_private_actions", DrinkingOutputContract.Contains("drinkActions is the sole drinking record") && DrinkingOutputContract.Contains("no drinking judge, repair or extra provider call"));
            Check("prompt_unknown_volume", IntoxicationPrompt(new IntoxicationState { Threshold = 5, Vessel = WineCup(null) }).Contains("unknown, not a full cup"));
            Dictionary<string, object> Accounting(string action, DrinkVesselState vessel = null, bool stale = false, bool suppressed = false)
            {
                var parsed = DrinkingFixture(action);
                var evidence = new List<Dictionary<string, object>>();
                double amount = ValidatedDrinkAmount(parsed, ReadString(parsed, "reply", ""), evidence, "Mira", true, vessel);
                var receipt = BindAcceptedDrinkEvents(evidence, "mira", "main:counts", stale, suppressed, stale ? 0 : amount);
                receipt["evidence"] = evidence;
                return receipt;
            }
            var pulls = Accounting("She takes two small pulls of wine.");
            Check("drink_events_are_not_whole_drinks", ReadInt(pulls, "consumptionEvents", -1) == 2
                && ReadInt(pulls, "alcoholConsumptionEvents", -1) == 2
                && ReadDouble(pulls, "estimatedAlcoholServings", -1) == .5
                && pulls["alcoholVolumeMl"] == null && pulls["standardDrinkCount"] == null, pulls);
            var twoOccurrences = Accounting(two);
            var occurrences = ReadDictionaryList(twoOccurrences, "evidence").Where(e => ReadInt(e, "acceptedConsumptionEvents", 0) > 0).ToList();
            Check("drink_event_identity_per_occurrence", occurrences.Count == 2
                && occurrences.Select(e => ReadString(e, "eventId", "")).Distinct().Count() == 2
                && occurrences.All(e => ReadString(e, "drinkerId", "") == "mira"), twoOccurrences);
            Check("drink_event_identity_repeatable", Json.Serialize(twoOccurrences) == Json.Serialize(Accounting(two)));
            var unknownBulk = Accounting("She finishes the jug of wine.");
            Check("drink_unknown_volume_keeps_event", ReadInt(unknownBulk, "consumptionEvents", -1) == 1
                && ReadDouble(unknownBulk, "estimatedAlcoholServings", -1) == 0
                && unknownBulk["standardDrinkCount"] == null, unknownBulk);
            var water = Accounting("She takes a sip of water.");
            Check("drink_water_event_without_alcohol", ReadInt(water, "consumptionEvents", -1) == 1
                && ReadInt(water, "alcoholConsumptionEvents", -1) == 0
                && ReadDouble(water, "estimatedAlcoholServings", -1) == 0, water);
            var emptyWaterVessel = WineCup(0); emptyWaterVessel.Beverage = ResolveDrinkBeverage("water");
            var emptyWater = Accounting("She sips from her cup.", emptyWaterVessel);
            Check("drink_empty_water_is_not_event", ReadInt(emptyWater, "consumptionEvents", -1) == 0, emptyWater);
            foreach (string action in new[] { "She swallows hard.", "She handles the cup of wine.", "She considers taking a sip of wine.", "He takes a sip of wine." })
                Check("drink_event_negative_" + action, ReadInt(Accounting(action), "consumptionEvents", -1) == 0);
            var staleCount = Accounting("She takes a sip of wine.", stale: true);
            Check("drink_stale_event_not_applied", ReadInt(staleCount, "consumptionEvents", -1) == 0
                && ReadDouble(staleCount, "estimatedAlcoholServings", -1) == 0, staleCount);
            var replacement = Accounting("She takes two sips of wine.", suppressed: true);
            Check("drink_replacement_does_not_invent_count", ReadInt(replacement, "consumptionEvents", -1) == 0
                && !ReadBool(replacement, "eventCountComplete", true), replacement);
            var migratedCounts = RecoverIntoxication(new Dictionary<string, object> { ["drinks"] = 2d }, 2, 12);
            Check("drink_old_state_count_starts_at_observation", migratedCounts.ConsumptionEvents == 0
                && migratedCounts.AlcoholConsumptionEvents == 0 && migratedCounts.EventCountSinceHour == 12);
            RunDrinkingVesselStorageContracts(Check);
            RunQuietDrinkingContracts(Check);
            return rows;
        }

        private static Dictionary<string, object> QuietDrinkFixture(DrinkVesselState vessel, string serving = "sip", string reply = "Tell me more about your clan.")
            => TestDict("reply", reply, "drinkConsumptions", new List<Dictionary<string, object>> {
                TestDict("vesselId", vessel.Id, "contentsVersion", vessel.ContentsVersion, "serving", serving, "count", 1, "completed", true) });

        private static void RunQuietDrinkingContracts(Action<string, bool, object, object> check)
        {
            var original = new DrinkVesselState { Scope = "quiet-fixture", Hour = 12, Kind = "cup", Remaining = .75,
                Beverage = ResolveDrinkBeverage("wine"), ContentsVersion = 3, IdentitySeed = "quiet-test" };
            IdentifyDrinkVessel(original, "known_cup");
            double Quiet(Dictionary<string, object> parsed, DrinkVesselState vessel, List<Dictionary<string, object>> evidence = null) =>
                ValidateQuietDrinkConsumption(parsed, ReadString(parsed, "reply", ""), "mira", vessel, null, evidence ?? new List<Dictionary<string, object>>());
            var plain = QuietDrinkFixture(original);
            var sip = original.Copy();
            var details = new List<Dictionary<string, object>>();
            check("quiet_drink_plain_speech_without_actions", Quiet(plain, sip, details) == .25 && sip.Remaining == .5
                && !ReadString(plain, "reply", "").Contains('*') && ReadInt(details[0], "consumptionEvents", 0) == 1, details, .25);
            foreach (var portion in new[] { ("sip", .25), ("half", .5), ("drink", .75) })
                check("quiet_drink_known_remainder_" + portion.Item1, Quiet(QuietDrinkFixture(original, portion.Item1), original.Copy()) == portion.Item2, null, portion.Item2);
            foreach (string invalid in new[] { "unknown_id", "wrong_version", "fractional_version", "wrong_actor", "not_completed", "zero_count", "many_count", "fractional_count", "invalid_serving" })
            {
                var parsed = QuietDrinkFixture(original);
                var item = ReadDictionaryList(parsed, "drinkConsumptions")[0];
                if (invalid == "unknown_id") item["vesselId"] = "invented";
                if (invalid == "wrong_version") item["contentsVersion"] = 2;
                if (invalid == "fractional_version") item["contentsVersion"] = 3.1;
                if (invalid == "wrong_actor") item["drinkerId"] = "player";
                if (invalid == "not_completed") item["completed"] = false;
                if (invalid == "zero_count") item["count"] = 0;
                if (invalid == "many_count") item["count"] = 20;
                if (invalid == "fractional_count") item["count"] = .5;
                if (invalid == "invalid_serving") item["serving"] = "jug";
                var copy = original.Copy();
                check("quiet_drink_reject_" + invalid, Quiet(parsed, copy) == 0 && copy.Remaining == .75, null, 0);
            }
            foreach (string refusal in new[] { "No thanks.", "I don't drink.", "I won't sip.", "I refuse to drink.", "No more." })
                check("quiet_drink_refusal_" + PromptHash(refusal).Substring(0, 8), Quiet(QuietDrinkFixture(original, "sip", refusal), original.Copy()) == 0, null, 0);
            foreach (string unrelated in new[] { "No more taxes on my people.", "No thanks; I won't join your clan." })
                check("quiet_drink_unrelated_refusal_" + PromptHash(unrelated).Substring(0, 8), Quiet(QuietDrinkFixture(original, "sip", unrelated), original.Copy()) == .25, null, .25);
            var duplicate = QuietDrinkFixture(original);
            var duplicates = ReadDictionaryList(duplicate, "drinkConsumptions");
            duplicates.Add(duplicates[0]); duplicate["drinkConsumptions"] = duplicates;
            check("quiet_drink_duplicate_metadata_rejected", Quiet(duplicate, original.Copy()) == 0, null, 0);
            check("quiet_drink_narration_and_metadata_count_once", Quiet(plain, original.Copy(),
                new List<Dictionary<string, object>> { TestDict("consumptionEvents", 1) }) == 0, null, 0);
            var unknown = original.Copy(); unknown.Remaining = null; unknown.Kind = "skin";
            check("quiet_drink_bulk_unknown_sip", Quiet(QuietDrinkFixture(unknown), unknown.Copy()) == .25, null, .25);
            check("quiet_drink_bulk_unknown_full_rejected", Quiet(QuietDrinkFixture(unknown, "drink"), unknown.Copy()) == 0, null, 0);
            var empty = original.Copy(); empty.Remaining = 0;
            check("quiet_drink_empty_rejected", Quiet(QuietDrinkFixture(empty), empty) == 0 && empty.Remaining == 0, null, 0);
            var held = original.Copy(); held.Holder = "player"; held.SharedWithPlayer = true;
            check("quiet_drink_player_custody_unavailable", Quiet(QuietDrinkFixture(held), held.Copy()) == 0, null, 0);
            var water = original.Copy(); water.Beverage = ResolveDrinkBeverage("water");
            var waterEvidence = new List<Dictionary<string, object>>();
            check("quiet_drink_water_counts_event_without_alcohol", Quiet(QuietDrinkFixture(water), water, waterEvidence) == 0
                && water.Remaining == .5 && ReadInt(waterEvidence[0], "consumptionEvents", 0) == 1
                && ReadString(waterEvidence[0], "alcohol", "") == "No", waterEvidence, 0);
            var unknownContents = original.Copy(); unknownContents.Beverage = new BeverageIdentity();
            check("quiet_drink_unknown_contents_rejected", Quiet(QuietDrinkFixture(unknownContents), unknownContents) == 0, null, 0);
            var roundtrip = ReadDrinkVessel(TryParseJsonObject(Json.Serialize(DrinkVesselRecord(held))));
            check("drink_record_custody_identity_roundtrip", roundtrip.Id == held.Id && roundtrip.Holder == "player"
                && roundtrip.SharedWithPlayer && roundtrip.ContentsVersion == 3 && roundtrip.Remaining == .75, DrinkVesselRecord(roundtrip), null);
            var same = CurrentPlayerDrinkOffer(TestDict("playerText", "*I hand the drink back to her.*"), held, true);
            check("drink_record_generic_return_preserves_identity", same?.Vessel.Id == held.Id && same.Vessel.Beverage.Family == "wine"
                && same.Vessel.Remaining == .75, same == null ? null : DrinkVesselRecord(same.Vessel), null);
            check("drink_record_spoken_offer_without_action", CurrentPlayerDrinkOffer(TestDict("playerText", "Want another sip?"), held, true)?.Vessel.Id == held.Id, null, held.Id);
            foreach (string rejected in new[] { "I might hand it back to her.", "I never hand it to her.", "I watch him hand it to her.",
                "I hand the rope to her.", "I hand a mystery drink to her.", "I hand it to him.", "I hand it to the waitress.", "I offer her two full cups of wine." })
                check("drink_record_invalid_offer_" + PromptHash(rejected).Substring(0, 8),
                    CurrentPlayerDrinkOffer(TestDict("playerText", rejected), held, true)?.Vessel.Id != held.Id, null, null);
            var defaults = DefaultPromptTemplates();
            foreach (var mode in new[] { CodexConversationContractKind.Individual, CodexConversationContractKind.Group, CodexConversationContractKind.Castle, CodexConversationContractKind.Social })
            {
                var schema = CodexConversationSchemas.Select(mode, defaults, defaults, TestDict()).Schema;
                var properties = ReadDictionary(schema, "properties");
                check("quiet_drink_schema_" + mode, properties?.ContainsKey("drinkConsumptions") == true, null, true);
            }
            var formatRepair = TestDict("reply", "More about your clan?");
            PreserveFormatRepairMetadata(plain, formatRepair);
            check("quiet_drink_format_repair_preserves_choice", ReadDictionaryList(formatRepair, "drinkConsumptions").Count == 1, null, 1);
            var filled = original.Copy();
            ValidatedDrinkAmount(DrinkingFixture("She refills her cup with water."), "*She refills her cup with water.*",
                new List<Dictionary<string, object>>(), "Mira", true, filled);
            check("drink_record_refill_keeps_id_advances_contents", filled.Id == original.Id && filled.ContentsVersion == 4
                && filled.Beverage.Alcohol == DrinkAlcohol.No && Quiet(QuietDrinkFixture(original), filled) == 0, DrinkVesselRecord(filled), null);
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
                    ApplyLegacyConversationDrinking(campaign, hero, payload, profile, null, parsed, ReadString(parsed, "reply", ""));
                    return ReadDictionary(payload, "intoxicationReceipt");
                }
                double Remaining(Dictionary<string, object> receipt) => ReadDouble(ReadDictionary(ReadDictionary(receipt, "state"), "vessel"), "remaining", -1);
                Turn("fill", "She accepts a full cup of wine.");
                var first = Turn("sip-one", "She sips from her cup.");
                var second = Turn("sip-two", "She sips from her cup.");
                check("DRINK-083", ReadDouble(ReadDictionary(second, "state"), "drinks", -1) == .5 && Remaining(second) == .5, second, .5);
                check("drink_event_count_persists", ReadLong(ReadDictionary(second, "state"), "consumptionEvents", -1) == 2
                    && ReadLong(ReadDictionary(second, "state"), "alcoholConsumptionEvents", -1) == 2, second, 2);
                string committed = Json.Serialize(second);
                var replay = Turn("sip-two", "She refills her cup with water.");
                check("replay_does_not_refill", Json.Serialize(replay) == committed, replay, committed);
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "vessel_checkpoint");
                var finish = Turn("finish", "She finishes her cup.");
                check("persisted_finish_remainder", ReadDouble(finish, "consumed", -1) == .5 && Remaining(finish) == 0, finish, .5);
                var empty = Turn("empty", "She sips from her cup.");
                check("persisted_empty_cup", ReadDouble(empty, "consumed", -1) == 0 && Remaining(empty) == 0, empty, 0);
                check("drink_empty_cup_keeps_cumulative_count", ReadLong(ReadDictionary(empty, "state"), "consumptionEvents", -1) == 3, empty, 3);
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "vessel_checkpoint");
                BuildIntoxicationPrompt(campaign, "mira", payload, profile, null);
                var restored = ReadDictionary(payload, "intoxicationContext");
                check("DRINK-085", ReadDouble(restored, "drinks", -1) == .5 && ReadDouble(ReadDictionary(restored, "vessel"), "remaining", -1) == .5, restored, .5);
                check("drink_event_count_snapshot_restore", ReadLong(restored, "consumptionEvents", -1) == 2
                    && ReadLong(restored, "alcoholConsumptionEvents", -1) == 2, restored, 2);
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
                    ApplyLegacyConversationDrinking(campaign, "concurrent", p, profile, null, parsed, ReadString(parsed, "reply", ""));
                });
                BuildIntoxicationPrompt(campaign, "concurrent", payload, profile, null);
                var concurrent = ReadDictionary(payload, "intoxicationContext");
                int receipts;
                using (var connection = OpenCampaignConnection(campaign))
                    receipts = QuerySql(connection, "SELECT turn_key FROM conversation_drinking_turns WHERE hero_id=$hero AND turn_key=$turn;",
                        new Dictionary<string, object> { ["hero"] = "concurrent", ["turn"] = "main:concurrent-sip" }).Count;
                check("DRINK-084", ReadDouble(concurrent, "drinks", -1) == .25 && ReadDouble(ReadDictionary(concurrent, "vessel"), "remaining", -1) == .75
                    && receipts == 1, concurrent, .25);
                check("drink_concurrent_retry_one_event", ReadLong(concurrent, "consumptionEvents", -1) == 1
                    && ReadLong(concurrent, "alcoholConsumptionEvents", -1) == 1, concurrent, 1);
                for (int n = 0; n < 5; n++) Turn("full-" + n, "She drinks a cup of wine.", "blocked");
                var blocked = Turn("blocked", "She takes a sip of kumis.", "blocked");
                check("DRINK-089", ReadDouble(blocked, "consumed", -1) == 0 && ReadBool(ReadDictionary(blocked, "state"), "alcoholBlocked", false), blocked, 0);
                check("drink_threshold_replacement_count_incomplete", ReadBool(ReadDictionary(blocked, "state"), "incompleteEventCount", false)
                    && ReadLong(ReadDictionary(blocked, "state"), "consumptionEvents", -1) == 4
                    && ReadInt(ReadDictionary(blocked, "eventAccounting"), "consumptionEvents", -1) == 0, blocked, 4);
                var blockedFill = Turn("blocked-fill", "She refills her cup with kumis and takes a sip of kumis.", "blocked");
                check("suppressed_reply_cannot_store_refill", ReadString(ReadDictionary(ReadDictionary(blockedFill, "state"), "vessel"), "beverage", "") == "", blockedFill, "");

                payload["playerText"] = "";
                Dictionary<string, object> Vessel(Dictionary<string, object> receipt) => ReadDictionary(ReadDictionary(receipt, "state"), "vessel");
                var playerLines = new[] {
                    "*I take a seat and wait for her to sit also, pulling my skin of whiskey out and taking a drink before passing it to her.*",
                    "*I smile as I take another drink of whiskey and pass it back to her*",
                    "*I look over and hold her gaze as I hand the drink back to her again.*",
                    "*I take a drink from my whiskey skin and hand it to her*",
                    "*I smile as I grab the whiskey skin and take a drink before passing it to her*" };
                var npcLines = new[] {
                    "She takes the skin. Drinks. Her thumb wipes the mouth of it before passing it back — practical or proprietary, and she doesn't clarify which.",
                    "She takes the skin. Drinks. The whiskey burns the same way it did seventeen days ago, and she hands it back without wiping the mouth this time.",
                    "She takes the skin. Her fingers close around it but she doesn't drink — just holds it. She takes a drink. A real one — not the measured sip from before. She hands it back.",
                    "She takes the skin. Drinks — a real one, not the measured sip. Her thumb doesn't wipe the mouth this time. She hands it back.",
                    "She takes the skin. Drinks — shorter this time, the kind of pull that's more about holding something warm than needing it. She hands it back." };
                string sharedId = "";
                Dictionary<string, object> sharedReceipt = null;
                for (int i = 0; i < playerLines.Length; i++)
                {
                    payload["playerText"] = playerLines[i];
                    sharedReceipt = Turn("corein-replay-" + i, npcLines[i], "corein-fixture");
                    string id = ReadString(Vessel(sharedReceipt), "id", "");
                    if (i == 0) sharedId = id;
                    check("drink_record_corein_replay_" + i, ReadDouble(sharedReceipt, "consumed", -1) == .25
                        && id == sharedId && id.Length > 0 && ReadString(Vessel(sharedReceipt), "holder", "") == "player", sharedReceipt, .25);
                }
                check("drink_record_corein_five_events_fractional_amount", ReadDouble(ReadDictionary(sharedReceipt, "state"), "drinks", -1) == 1.25
                    && ReadLong(ReadDictionary(sharedReceipt, "state"), "alcoholConsumptionEvents", -1) == 5, sharedReceipt, 1.25);
                payload["playerText"] = "*I offer her a full new cup of wine.*";
                var refused = Turn("refused-new-cup", "She declines the cup.", "corein-fixture");
                check("drink_record_refused_offer_preserves_original", ReadString(Vessel(refused), "id", "") == sharedId
                    && ReadString(Vessel(refused), "family", "") == "spirits", refused, sharedId);
                payload["playerText"] = "*I hand a full new cup of water to her.*";
                var switched = Turn("new-water-cup", "She accepts the cup and takes a sip.", "corein-fixture");
                check("drink_record_new_water_never_borrows_whiskey", ReadDouble(switched, "consumed", -1) == 0
                    && ReadString(Vessel(switched), "id", "") != sharedId && ReadString(Vessel(switched), "alcohol", "") == "No", switched, 0);

                payload["turnId"] = "quiet-initial";
                payload["playerText"] = "I offer her a full cup of wine.";
                string prompt = BuildIntoxicationPrompt(campaign, "quiet", payload, profile, null);
                var available = ReadDrinkVessel(ReadDictionary(ReadDictionary(payload, "intoxicationContext"), "availableDrink"));
                var quietParsed = QuietDrinkFixture(available);
                var quietReply = ReadString(quietParsed, "reply", "");
                ApplyLegacyConversationDrinking(campaign, "quiet", payload, profile, null, quietParsed, quietReply, persist: false);
                check("quiet_drink_prompt_offer_and_prepare", prompt.Contains(available.Id) && available.Id.Length > 0
                    && ReadDouble(ReadDictionary(payload, "intoxicationReceipt"), "consumed", -1) == .25
                    && ReadString(ReadDictionary(payload, "intoxicationReceipt"), "commitState", "") == "prepared", null, .25);
                string generation;
                using (var connection = OpenCampaignConnection(campaign))
                    generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                var final = CommitFinalConversationEffects(campaign, "quiet", payload, null, profile, null, quietParsed, quietReply, generation);
                var quietCommitted = ReadDictionary(final, "drinking");
                check("quiet_drink_plain_reply_atomic_acceptance", ReadBool(final, "ok", false)
                    && ReadDouble(quietCommitted, "consumed", -1) == .25
                    && ReadString(Vessel(quietCommitted), "id", "") == available.Id && Remaining(quietCommitted) == .75, final, .25);
                string quietId = available.Id;
                payload["playerText"] = "";
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "quiet_record_checkpoint");
                Dictionary<string, object> QuietTurn(string turn, string serving = "sip")
                {
                    payload["turnId"] = turn;
                    BuildIntoxicationPrompt(campaign, "quiet", payload, profile, null);
                    var current = ReadDrinkVessel(ReadDictionary(ReadDictionary(payload, "intoxicationContext"), "vessel"));
                    var parsed = QuietDrinkFixture(current, serving);
                    ApplyLegacyConversationDrinking(campaign, "quiet", payload, profile, null, parsed, ReadString(parsed, "reply", ""));
                    return ReadDictionary(payload, "intoxicationReceipt");
                }
                var quietSecond = QuietTurn("quiet-second");
                check("quiet_drink_multiple_plain_turns", ReadDouble(ReadDictionary(quietSecond, "state"), "drinks", -1) == .5
                    && Remaining(quietSecond) == .5, quietSecond, .5);
                var quietReplay = QuietTurn("quiet-second", "drink");
                check("quiet_drink_retry_reuses_receipt", Json.Serialize(quietReplay) == Json.Serialize(quietSecond), quietReplay, quietSecond);
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "quiet_record_checkpoint");
                BuildIntoxicationPrompt(campaign, "quiet", payload, profile, null);
                var restoredQuiet = ReadDictionary(payload, "intoxicationContext");
                check("quiet_drink_snapshot_restores_record_and_count", ReadDouble(restoredQuiet, "drinks", -1) == .25
                    && ReadString(ReadDictionary(restoredQuiet, "vessel"), "id", "") == quietId
                    && ReadDouble(ReadDictionary(restoredQuiet, "vessel"), "remaining", -1) == .75, restoredQuiet, .25);
                using (var connection = OpenCampaignConnection(campaign))
                    generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                payload["turnId"] = "quiet-corrected-refusal";
                var corrected = QuietDrinkFixture(ReadDrinkVessel(ReadDictionary(restoredQuiet, "vessel")));
                ApplyLegacyConversationDrinking(campaign, "quiet", payload, profile, null, corrected, ReadString(corrected, "reply", ""), persist: false);
                var correction = CommitFinalConversationEffects(campaign, "quiet", payload, null, profile, null, corrected, "No thanks.", generation);
                check("quiet_drink_final_refusal_rechecks_prepared_choice", ReadDouble(ReadDictionary(correction, "drinking"), "consumed", -1) == 0, correction, 0);
                payload["turnId"] = "quiet-cleared-metadata";
                var cleared = QuietDrinkFixture(ReadDrinkVessel(ReadDictionary(restoredQuiet, "vessel")));
                ApplyLegacyConversationDrinking(campaign, "quiet", payload, profile, null, cleared, ReadString(cleared, "reply", ""), persist: false);
                cleared["drinkConsumptions"] = new List<Dictionary<string, object>>();
                var noChoice = CommitFinalConversationEffects(campaign, "quiet", payload, null, profile, null, cleared, ReadString(cleared, "reply", ""), generation);
                check("quiet_drink_cleared_metadata_cannot_commit_old_choice", ReadDouble(ReadDictionary(noChoice, "drinking"), "consumed", -1) == 0, noChoice, 0);
                var currentQuiet = ReadDrinkVessel(ReadDictionary(restoredQuiet, "vessel"));
                System.Threading.Tasks.Parallel.For(0, 8, n =>
                {
                    var concurrentPayload = new Dictionary<string, object>(payload) { ["turnId"] = "quiet-concurrent" };
                    var parsed = QuietDrinkFixture(currentQuiet);
                    ApplyLegacyConversationDrinking(campaign, "quiet", concurrentPayload, profile, null, parsed, ReadString(parsed, "reply", ""));
                });
                BuildIntoxicationPrompt(campaign, "quiet", payload, profile, null);
                var quietConcurrent = ReadDictionary(payload, "intoxicationContext");
                check("quiet_drink_concurrent_retry_counts_once", ReadDouble(quietConcurrent, "drinks", -1) == .5
                    && ReadLong(quietConcurrent, "consumptionEvents", -1) == 2
                    && ReadDouble(ReadDictionary(quietConcurrent, "vessel"), "remaining", -1) == .5, quietConcurrent, .5);
                Turn("quiet-handoff", "She hands her cup back to you.", "quiet");
                payload["playerText"] = "Want another sip?";
                payload["turnId"] = "quiet-spoken-invitation";
                BuildIntoxicationPrompt(campaign, "quiet", payload, profile, null);
                var invitedRecord = ReadDrinkVessel(ReadDictionary(ReadDictionary(payload, "intoxicationContext"), "availableDrink"));
                var invitedReply = QuietDrinkFixture(invitedRecord);
                ApplyLegacyConversationDrinking(campaign, "quiet", payload, profile, null, invitedReply, ReadString(invitedReply, "reply", ""));
                var invitedReceipt = ReadDictionary(payload, "intoxicationReceipt");
                check("quiet_drink_spoken_invitation_accepts_same_record", ReadDouble(invitedReceipt, "consumed", -1) == .25
                    && ReadString(Vessel(invitedReceipt), "id", "") == quietId && Remaining(invitedReceipt) == .25, invitedReceipt, .25);
                payload["playerText"] = "";
                payload["worldDay"] = 10d + 2d / 24d;
                payload["turnId"] = "quiet-expired";
                var expiredReply = QuietDrinkFixture(invitedRecord);
                ApplyLegacyConversationDrinking(campaign, "quiet", payload, profile, null, expiredReply, ReadString(expiredReply, "reply", ""));
                check("quiet_drink_expired_record_rejected", ReadDouble(ReadDictionary(payload, "intoxicationReceipt"), "consumed", -1) == 0,
                    ReadDictionary(payload, "intoxicationReceipt"), 0);
            }
            catch (Exception ex) { check("vessel_storage_integration", false, ex.ToString(), null); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
        }
    }
}
