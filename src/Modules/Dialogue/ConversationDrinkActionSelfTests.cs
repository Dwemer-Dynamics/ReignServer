using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Isolated PostgreSQL fixtures exercise the live private-action reducer and
        // final publication path. Legacy parser contracts remain separate below it.
        private static List<Dictionary<string, object>> RunConversationDrinkActionSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => rows.Add(TestDict(
                "id", "private_drink_" + id, "caseId", "private_drink_" + id, "passed", pass,
                "suite", "conversation_intoxication", "summary", id, "data", data));
            string campaign = "private_drink_test_" + Guid.NewGuid().ToString("N");
            var profile = TestDict("name", "Arytha", "isFemale", true, "attributes", TestDict("endurance", 20));
            Dictionary<string, object> Payload(string turn) => TestDict("campaignId", campaign,
                "timelineId", "main", "turnId", turn, "sceneTurnId", turn, "worldDay", 10d,
                "conversationSessionId", "first_chat", "locationId", "castle", "playerHeroStringId", "player",
                "conversationSceneState", TestDict("conversationVenue", "Gersegos Castle", "participants", new[] {
                    TestDict("heroStringId", "arytha"), TestDict("heroStringId", "player"), TestDict("heroStringId", "other") }));
            Dictionary<string, object> Action(string id = "sip_1", string operation = "consume", string beverage = "wine",
                string serving = "sip", string vessel = "my_cup", string holder = "arytha", string quantity = "unknown") => TestDict(
                "id", id, "operation", operation, "beverage", beverage, "serving", serving, "vesselId", vessel,
                "holderHeroId", holder, "kind", "cup", "quantity", quantity, "count", 1, "completed", true, "contentsVersion", 0);
            Dictionary<string, object> Draft(string text, params Dictionary<string, object>[] actions) => TestDict("reply", text, "drinkActions", actions);
            Dictionary<string, object> Turn(Dictionary<string, object> payload, Dictionary<string, object> parsed,
                bool persist = false, string hero = "arytha", Dictionary<string, object> nativeProfile = null)
            {
                string text = ReadString(parsed, "reply", "");
                string visible = ApplyConversationDrinking(campaign, hero, payload, nativeProfile ?? profile, null, parsed, text, persist);
                Check("prose_unchanged_" + ReadString(payload, "turnId", ""), visible == text && ReadString(parsed, "reply", "") == text);
                return ReadDictionary(payload, "intoxicationReceipt");
            }
            double Amount(Dictionary<string, object> receipt) => ReadDouble(receipt, "consumed", -1);
            bool Diagnostic(Dictionary<string, object> receipt, string reason) => ReadDictionaryList(receipt, "diagnostics")
                .Any(d => ReadString(d, "reason", "") == reason);
            Dictionary<string, object> State(string hero = "arytha") {
                using var c = OpenCampaignConnection(campaign); EnsureIntoxicationSchema(c);
                return TryParseJsonObject(ReadString(QuerySql(c, "SELECT state_json FROM conversation_intoxication WHERE hero_id=$hero;",
                    TestDict("hero", hero)).SingleOrDefault(), "state_json", "{}"));
            }
            Dictionary<string, object> Scene() {
                using var c = OpenCampaignConnection(campaign); EnsureIntoxicationSchema(c);
                return TryParseJsonObject(ReadString(QuerySql(c, "SELECT state_json FROM conversation_drink_scene WHERE scene_id='active';")
                    .SingleOrDefault(), "state_json", "{}"));
            }
            void ClearScene() {
                using var c = OpenCampaignConnection(campaign); EnsureIntoxicationSchema(c);
                ExecuteSql(c, "DELETE FROM conversation_drink_scene WHERE scene_id='active';");
            }
            string Generation() {
                using var c = OpenCampaignConnection(campaign); return ReadString(ReadMemoryPrecisionState(c), "restore_generation", "");
            }
            try
            {
                string[] naturalReplies = {
                    "*She takes a sip — short, testing, the way you test a blade's edge with your thumb before you commit to the swing.*",
                    "*She drinks again, slower this time. Lets the wine sit on her tongue a moment before swallowing.*",
                    "*She drinks, the way someone drinks when they would rather listen than answer.* Tell me more.",
                    "*A small mouthful interrupts her answer; she savours it, then swallows.* I remember.",
                    "*La coupe à ses lèvres, elle savoure une gorgée.* Continue.",
                    "I had wondered whether you would come back." };
                for (int i = 0; i < naturalReplies.Length; i++) {
                    var receipt = Turn(Payload("character_style_" + i), Draft(naturalReplies[i], Action()));
                    Check("free_narration_single_authoritative_event_" + i, Amount(receipt) == .25
                        && ReadInt(ReadDictionary(receipt, "eventAccounting"), "consumptionEvents", -1) == 1, receipt);
                }
                var proseOnly = Turn(Payload("prose_without_action"), TestDict("reply", "*She drinks wine. She drinks again.*"));
                Check("prose_does_not_independently_count", Amount(proseOnly) == 0 && Diagnostic(proseOnly, "missing_private_actions"));
                var old = DrinkingFixture("She drinks wine.");
                old["drinkActions"] = new[] { Action() };
                old["drinkConsumptions"] = new[] { TestDict("completed", true, "serving", "drink", "count", 20) };
                Check("old_channels_cannot_double_count", Amount(Turn(Payload("old_channels"), old)) == .25);
                var duplicate = Action();
                Check("duplicate_id_once", Amount(Turn(Payload("duplicate_id"), Draft(naturalReplies[0], duplicate, CloneDictionary(duplicate)))) == .25);
                Check("separate_ids_twice", Amount(Turn(Payload("separate_ids"), Draft(naturalReplies[1], Action(), Action("sip_2")))) == .5);
                var anonymous = Action(); anonymous.Remove("id");
                var inferred = Turn(Payload("inferred_id"), Draft(naturalReplies[0], anonymous, CloneDictionary(anonymous)));
                Check("missing_ids_locally_deduplicated", Amount(inferred) == .25 && Diagnostic(inferred, "private_action_id_inferred"));
                foreach (var malformed in new object[] { null, "drink wine", TestDict("operation", "consume"), 5, new object[] { "bad", null } }) {
                    var receipt = Turn(Payload("malformed_" + rows.Count), TestDict("reply", "Tell me more.", "drinkActions", malformed));
                    Check("malformed_metadata_nonblocking_" + rows.Count, Amount(receipt) == 0
                        && !ReadBool(receipt, "dialogueBlocked", true) && ReadInt(receipt, "additionalProviderCalls", -1) == 0, receipt);
                }
                var mixed = Turn(Payload("mixed_metadata"), TestDict("reply", naturalReplies[0], "drinkActions", new object[] { "bad", Action() }));
                Check("valid_actions_survive_malformed_sibling", Amount(mixed) == .25);
                var incomplete = Action(); incomplete["completed"] = false;
                Check("offered_refused_or_planned_action_not_consumed", Amount(Turn(Payload("not_completed"), Draft("Perhaps another time.", incomplete))) == 0);
                Check("other_actor_never_doses_speaker", Amount(Turn(Payload("wrong_actor"), Draft("*He drinks.*", Action(holder: "player")))) == 0);
                var exaggerated = Action(serving: "drink"); exaggerated["count"] = 1000;
                Check("per_action_quantity_bounded", Amount(Turn(Payload("quantity_bound"), Draft("*She drinks.*", exaggerated))) == 20);
                var excessive = Enumerable.Range(0, 40).Select(i => Action("sip_" + i)).ToArray();
                var bounded = Turn(Payload("action_bound"), Draft(naturalReplies[0], excessive));
                Check("action_count_bounded", Amount(bounded) == 4 && Diagnostic(bounded, "private_action_limit"));
                foreach (double invalid in new[] { 0d, -1d, .5d, double.NaN, double.PositiveInfinity }) {
                    var action = Action(); action["count"] = invalid;
                    // JSON cannot carry NaN/Infinity: exercise finite strings as a custom-provider input.
                    if (!FiniteDrinkNumber(invalid)) action["count"] = invalid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Check("invalid_count_nonblocking_" + invalid, Amount(Turn(Payload("invalid_count_" + rows.Count), Draft("Continue.", action))) == 0);
                }
                foreach (var pair in new Dictionary<string, double> { ["wine"] = .25, ["watered wine"] = .25, ["mead"] = .25,
                    ["kumis"] = .25, ["nonalcoholic ale"] = 0, ["water"] = 0, ["kvass"] = 0, ["alcoholic kvass"] = .25,
                    ["boza"] = 0, ["mystery drink"] = 0, ["tea mixed with brandy"] = .25, ["wine or water"] = 0,
                    ["wine vinegar"] = 0, ["fresh cider"] = 0 }) {
                    var receipt = Turn(Payload("catalogue_" + pair.Key), Draft(naturalReplies[0], Action(beverage: pair.Key)));
                    Check("engine_beverage_classification_" + pair.Key, Amount(receipt) == pair.Value
                        && ReadInt(ReadDictionary(receipt, "eventAccounting"), "consumptionEvents", -1) == 1, receipt);
                }
                var lied = Action(); lied["alcohol"] = false;
                Check("model_cannot_override_alcohol_catalogue", Amount(Turn(Payload("catalogue_authority"), Draft(naturalReplies[0], lied))) == .25);
                var invalidCount = Action(); invalidCount["count"] = "many";
                Check("malformed_count_no_assumed_dose", Amount(Turn(Payload("malformed_count"), Draft(naturalReplies[0], invalidCount))) == 0);

                string arrival = "*The wine arrives — two clay cups, dark red, nothing fancy. She pushes one toward him and wraps her hand around the other.*";
                var opening = Turn(Payload("ambient_opening"), Draft(arrival + naturalReplies[0],
                    Action("serve_npc", "establish"), Action("serve_player", "establish", vessel: "his_cup", holder: "player"), Action()), true);
                var cups = ReadDictionaryList(Scene(), "vessels");
                string cupId = ReadString(cups.Single(v => ReadString(v, "holderHeroId", "") == "arytha"), "id", "");
                Check("ambient_service_and_sip_share_one_response", Amount(opening) == .25 && cups.Count == 2 && cupId.Length > 0, opening);
                var reopened = Payload("reopened"); reopened["conversationSessionId"] = "new_chat";
                BuildIntoxicationPrompt(campaign, "arytha", reopened, profile, null);
                Check("reopened_scene_retains_identity", ReadString(ReadDictionary(ReadDictionary(reopened, "intoxicationContext"), "vessel"), "id", "") == cupId);
                var staleRevision = Action(vessel: cupId); staleRevision["contentsVersion"] = 900;
                var reversion = Turn(reopened, Draft(naturalReplies[1], staleRevision), true);
                Check("stale_revision_is_diagnostic", Amount(reversion) == .25 && Diagnostic(reversion, "stale_cup_revision_observed"));
                var oversizedBeverage = Action(vessel: cupId); oversizedBeverage["beverage"] = new string('x', 201);
                var malformedIdentity = Turn(Payload("oversized_beverage"), Draft(naturalReplies[0], oversizedBeverage));
                Check("malformed_identity_cannot_borrow_old_wine", Amount(malformedIdentity) == 0 && Diagnostic(malformedIdentity, "malformed_beverage"));
                Turn(Payload("unknown_refill"), Draft("*She changes the contents.*", Action("refill", "refill", "mystery drink", vessel: cupId)), true);
                var unresolved = Turn(Payload("unresolved_after_refill"), Draft("*She tastes it.*", Action(beverage: "", vessel: cupId)), true);
                Check("unknown_refill_cannot_borrow_old_wine_identity", Amount(unresolved) == 0 && Diagnostic(unresolved, "consumption_contents_unresolved"));
                var restoredWine = Turn(Payload("explicit_wine_repairs_identity"), Draft(naturalReplies[1], Action(vessel: cupId)), true);
                Check("explicit_beverage_repairs_missing_contents", Amount(restoredWine) == .25);

                Turn(Payload("full_refill"), Draft("*She refills the cup.*", Action("refill", "refill", vessel: cupId, quantity: "full")), true);
                Turn(Payload("portion_half"), Draft("*She drinks half.*", Action(serving: "half", vessel: cupId)), true);
                var finished = Turn(Payload("portion_finish"), Draft("*She savours the last of it.*", Action(serving: "finish", vessel: cupId)), true);
                Check("finish_uses_known_remainder", Amount(finished) == .5 && ReadDouble(ReadDictionaryList(Scene(), "vessels")
                    .Single(v => ReadString(v, "id", "") == cupId), "remaining", -1) == 0);
                Check("known_empty_cup_no_consumption", Amount(Turn(Payload("empty_cup"), Draft(naturalReplies[0], Action(vessel: cupId)), true)) == 0);
                Turn(Payload("water_refill"), Draft("*She pours water.*", Action("refill", "refill", "water", vessel: cupId, quantity: "full")), true);
                var water = Turn(Payload("water_finish"), Draft("*She drinks the water.*", Action(beverage: "water", serving: "finish", vessel: cupId)), true);
                Check("water_counts_event_without_alcohol", Amount(water) == 0 && ReadInt(ReadDictionary(water, "eventAccounting"), "consumptionEvents", -1) == 1
                    && ReadInt(ReadDictionary(water, "eventAccounting"), "alcoholConsumptionEvents", -1) == 0);
                Turn(Payload("wine_again"), Draft("*She refills it.*", Action("refill", "refill", vessel: cupId)), true);
                var estimated = Turn(Payload("unknown_finish"), Draft("*She finishes it.*", Action(serving: "finish", vessel: cupId)), true);
                Check("unknown_finish_bounded_estimate", Amount(estimated) == .25 && Diagnostic(estimated, "finish_volume_estimated"));
                Turn(Payload("handoff"), Draft("*She passes it to him.*", Action("pass", "transfer", vessel: cupId, holder: "player")), true);
                Check("known_custody_respected", Amount(Turn(Payload("wrong_custody"), Draft(naturalReplies[0], Action(vessel: cupId)), true)) == 0);
                var returned = Turn(Payload("return_and_sip"), Draft("*She accepts it back and tastes it.*",
                    Action("return", "transfer", vessel: cupId), Action(vessel: cupId)), true);
                Check("accepted_return_preserves_cup_and_counts_sip", Amount(returned) == .25 && ReadDictionaryList(Scene(), "vessels")
                    .Any(v => ReadString(v, "id", "") == cupId && ReadString(v, "holderHeroId", "") == "arytha"));
                Turn(Payload("discard"), Draft("*She leaves the cup.*", Action("discard", "discard", vessel: cupId)), true);
                Check("discard_removes_exact_cup", !ReadDictionaryList(Scene(), "vessels").Any(v => ReadString(v, "id", "") == cupId));
                var gap = Turn(Payload("missing_cup_explicit_wine"), Draft(naturalReplies[1], Action(vessel: "missing_cup")), true);
                Check("missing_cup_never_erases_identified_sip", Amount(gap) == .25);

                // Final acceptance uses current actions and current locked state.
                ClearScene();
                string generation = Generation();
                var finalPayload = Payload("final_prose");
                var finalDraft = Draft(naturalReplies[0], Action(holder: "final_npc"));
                Turn(finalPayload, finalDraft, hero: "final_npc");
                Check("preview_does_not_persist", ReadDouble(State("final_npc"), "drinks", 0) == 0);
                string changedProse = "*She pauses over the cup, taking an unhurried taste.* Go on.";
                var final = CommitFinalConversationEffects(campaign, "final_npc", finalPayload, TestDict(), profile, null, finalDraft, changedProse, generation);
                Check("final_prose_change_needs_no_drink_repair", ReadBool(final, "ok", false) && ReadDouble(State("final_npc"), "drinks", -1) == .25
                    && ReadString(ReadDictionary(final, "drinking"), "reply", "") == changedProse, final);
                var changedReplay = CommitFinalConversationEffects(campaign, "final_npc", finalPayload, TestDict(), profile, null, finalDraft, "Changed retry text.", generation);
                Check("changed_replay_never_redoses_or_blocks_on_drinking", ReadBool(changedReplay, "ok", false) && ReadDouble(State("final_npc"), "drinks", -1) == .25);
                var removedPayload = Payload("removed_private_action"); var removedDraft = Draft(naturalReplies[0], Action(holder: "final_npc"));
                Turn(removedPayload, removedDraft, hero: "final_npc"); removedDraft["drinkActions"] = new Dictionary<string, object>[0];
                var removed = CommitFinalConversationEffects(campaign, "final_npc", removedPayload, TestDict(), profile, null, removedDraft, changedProse, generation);
                Check("removed_action_recomputed_without_prose_scanning", ReadBool(removed, "ok", false) && Amount(ReadDictionary(removed, "drinking")) == 0
                    && ReadDouble(State("final_npc"), "drinks", -1) == .25);
                var racingPayload = Payload("prepared_before_other_turn"); var racingDraft = Draft(naturalReplies[0], Action(holder: "racing_npc"));
                Turn(racingPayload, racingDraft, hero: "racing_npc");
                Turn(Payload("intervening_turn"), Draft(naturalReplies[1], Action(holder: "racing_npc")), true, "racing_npc");
                var rebased = CommitFinalConversationEffects(campaign, "racing_npc", racingPayload, TestDict(), profile, null, racingDraft, naturalReplies[0], generation);
                Check("concurrent_state_locally_rebased", ReadBool(rebased, "ok", false) && ReadDouble(State("racing_npc"), "drinks", -1) == .5
                    && ReadBool(ReadDictionary(rebased, "drinking"), "locallyRebased", false), rebased);
                var rollbackPayload = Payload("private_atomicity"); var rollbackDraft = Draft(naturalReplies[0], Action(holder: "rollback_npc"));
                Turn(rollbackPayload, rollbackDraft, hero: "rollback_npc");
                bool rolledBack = false;
                try { CommitFinalConversationEffects(campaign, "rollback_npc", rollbackPayload, TestDict(), profile, null,
                    rollbackDraft, naturalReplies[0], generation, phase => { if (phase == "before_commit") throw new InvalidOperationException("Injected rollback."); }); }
                catch (InvalidOperationException) { rolledBack = true; }
                Check("private_actions_rollback_with_publication", rolledBack && ReadDouble(State("rollback_npc"), "drinks", 0) == 0);
                var silentPayload = Payload("silent_private_actions"); var silentDraft = Draft(naturalReplies[0], Action(holder: "silent_npc"));
                Turn(silentPayload, silentDraft, hero: "silent_npc");
                CommitFinalConversationEffects(campaign, "silent_npc", silentPayload, TestDict(), profile, null, silentDraft, "", generation);
                Check("silent_response_discards_prepared_actions", !silentPayload.ContainsKey("intoxicationReceipt") && ReadDouble(State("silent_npc"), "drinks", 0) == 0);

                var limitProfile = TestDict("attributes", TestDict("endurance", 2));
                var large = Action(serving: "drink", holder: "limited_npc"); large["count"] = 8;
                var limit = Turn(Payload("threshold"), Draft(naturalReplies[1], large), true, "limited_npc", limitProfile);
                Check("threshold_caps_amount_without_canned_reply", Amount(limit) == 5 && ReadBool(ReadDictionary(limit, "state"), "alcoholBlocked", false)
                    && ReadString(limit, "reply", "") == naturalReplies[1]);
                Check("blocked_alcohol_nonblocking", Amount(Turn(Payload("limit_blocked"), Draft(naturalReplies[1], Action(holder: "limited_npc")), true, "limited_npc", limitProfile)) == 0);
                var recovering = Payload("recovery"); recovering["worldDay"] = 10d + 6d / 24d;
                var recovered = Turn(recovering, Draft(naturalReplies[0], Action(holder: "limited_npc")), true, "limited_npc", limitProfile);
                Check("recovery_releases_below_half", Amount(recovered) == .25 && ReadDouble(State("limited_npc"), "drinks", -1) == 2.25);
                var missingClock = Payload("missing_clock"); missingClock.Remove("worldDay");
                Check("missing_clock_no_dose_no_rewrite", Amount(Turn(missingClock, Draft(naturalReplies[0], Action()))) == 0);
                var oldClock = Payload("stale_clock"); oldClock["worldDay"] = 9d;
                Check("rewound_clock_no_dose_no_rewrite", Amount(Turn(oldClock, Draft(naturalReplies[0], Action()))) == 0);

                ClearScene();
                var savedPayload = Payload("save_one"); var savedDraft = Draft(naturalReplies[0], Action(holder: "save_npc"));
                Turn(savedPayload, savedDraft, true, "save_npc");
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "private_drink_checkpoint");
                Turn(Payload("save_two"), Draft(naturalReplies[0], Action(holder: "save_npc")), true, "save_npc");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "private_drink_checkpoint");
                Check("snapshot_restores_private_counter", ReadDouble(State("save_npc"), "drinks", -1) == .25);
                var branch = Payload("save_two"); branch["timelineId"] = "fresh_restored_branch";
                Turn(branch, Draft(naturalReplies[0], Action(holder: "save_npc")), true, "save_npc");
                Turn(savedPayload, savedDraft, true, "save_npc");
                Check("restored_branch_and_old_turn_idempotence", ReadDouble(State("save_npc"), "drinks", -1) == .5);
                System.Threading.Tasks.Parallel.For(0, 8, i => {
                    var request = Payload("concurrent_retry");
                    ApplyConversationDrinking(campaign, "retry_npc", request, profile, null,
                        Draft(naturalReplies[0], Action(holder: "retry_npc")), naturalReplies[0]);
                });
                Check("concurrent_retry_exactly_once", ReadDouble(State("retry_npc"), "drinks", -1) == .25
                    && ReadLong(State("retry_npc"), "consumptionEvents", -1) == 1);
                var departure = Draft("*She leaves the hall.*");
                departure["sceneStateUpdates"] = new[] { TestDict("heroStringId", "arytha", "location", "Courtyard") };
                Turn(Payload("departure"), departure, true);
                Check("departure_expires_physical_cups", ReadDictionaryList(Scene(), "vessels").Count == 0);

                var defaults = DefaultPromptTemplates();
                foreach (var mode in new[] { CodexConversationContractKind.Individual, CodexConversationContractKind.Group,
                    CodexConversationContractKind.Castle, CodexConversationContractKind.Social }) {
                    var selection = CodexConversationSchemas.Select(mode, defaults, defaults, TestDict());
                    Check("schema_" + mode, ReadDictionary(selection.Schema, "properties")?.ContainsKey("drinkActions") == true
                        && selection.PreservedFields.Contains("drinkActions"));
                }
                var original = Draft(naturalReplies[0], Action());
                var repaired = TestDict("reply", changedProse); PreserveFormatRepairMetadata(original, repaired);
                Check("unrelated_format_repair_preserves_actions", CanonicalJson(original["drinkActions"]) == CanonicalJson(repaired["drinkActions"]));
                var merged = MergeDialogueIntegrityProse(original, TestDict("reply", changedProse), new List<Dictionary<string, object>>());
                Check("unrelated_prose_repair_cannot_choose_new_drinks", CanonicalJson(merged["drinkActions"]) == CanonicalJson(original["drinkActions"]));
                var failed = VisibleFailedRepairWithoutEffects(original, "Unavailable.");
                Check("failed_response_clears_private_actions", ReadDictionaryList(failed, "drinkActions").Count == 0);

                var generationPayload = Payload("old_generation"); var generationDraft = Draft(naturalReplies[0], Action(holder: "generation_npc"));
                Turn(generationPayload, generationDraft, hero: "generation_npc");
                string beforeGeneration = Generation(); AdvanceMemoryRestoreGeneration(campaign);
                bool replaced = false;
                try { CommitFinalConversationEffects(campaign, "generation_npc", generationPayload, TestDict(), profile, null,
                    generationDraft, naturalReplies[0], beforeGeneration); }
                catch (InvalidOperationException) { replaced = true; }
                Check("campaign_restore_boundary_still_enforced", replaced && ReadDouble(State("generation_npc"), "drinks", 0) == 0);
            }
            catch (Exception ex) { Check("integration_exception", false, ex.ToString()); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return rows;
        }
    }
}
