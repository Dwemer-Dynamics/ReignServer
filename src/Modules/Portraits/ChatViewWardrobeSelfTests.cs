using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void RunChatViewWardrobeChecks(Action<string, Action> check, Action<bool, string> require)
        {
            var frame = TestDict("actors", new[] { TestDict("id", "player", "name", "Player"), TestDict("id", "ira", "name", "Ira"),
                TestDict("id", "rhagaea", "name", "Rhagaea", "age", 36.94d, "clothing", "native red dress") },
                "locationId", "camp", "sceneInterface", "party");
            Dictionary<string, object> T(string id, int order, string speaker, string text) => TestDict("turnId", id,
                "turnOrder", order, "exchangeId", "e1", "role", speaker == "player" ? "player" : "npc",
                "speakerId", speaker, "text", text, "sourceHash", PromptHash(text), "frame", CloneDictionary(frame));
            var window = new List<Dictionary<string, object>> {
                T("z_proposal", 10, "player", "Let us change into rougher clothes to spar together."),
                T("a_accept", 11, "ira", "Agreed, let us do that."),
                T("m_player", 12, "player", "We pick up our practice blades."),
                T("b_action", 13, "ira", "Ira lunges toward the player. Afterwards she changes into a silk gown.") };
            Dictionary<string, object> E(string id, string quote, string kind) => TestDict("turnId", id, "quote", quote, "kind", kind);
            Dictionary<string, object> Change() => TestDict("scope", "outfit", "operation", "replace", "garmentText", "rougher clothes",
                "basis", "accepted_plan_then_activity", "evidence", new[] { E("z_proposal", "Let us change into rougher clothes to spar together.", "proposal"),
                    E("a_accept", "Agreed, let us do that.", "acceptance"), E("b_action", "Ira lunges toward the player.", "activity") });
            Dictionary<string, object> Scene() => TestDict("selectedTurnId", "b_action", "characterIds", new[] { "player", "ira", "rhagaea" },
                "caption", "Training in camp", "description", "Ira and the player spar while Rhagaea watches.",
                "evidence", new[] { TestDict("turnId", "b_action", "quote", "Ira lunges toward the player.") },
                "selectedMoment", TestDict("quote", "Ira lunges toward the player."),
                "wardrobeOverrides", new[] { TestDict("characterId", "player", "changes", new[] { Change() }),
                    TestDict("characterId", "ira", "changes", new[] { Change() }) });
            Dictionary<string, object> Identity(List<Dictionary<string, object>> turns = null) => TestDict("window", turns ?? window,
                "wardrobeContract", ChatViewWardrobeContract, "photoPrompt", ChatViewEffectivePrompt(ChatViewPhotoPromptName, ChatViewCurrentPhotoPrompt));
            bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
            List<Dictionary<string, object>> Validate(Dictionary<string, object> scene) {
                ValidateChatViewScene(scene, window); return ValidateChatViewWardrobe(scene, window, new List<Dictionary<string, object>>()); }
            check("wardrobe_accepted_plan_then_spar_source_to_prompt", () => {
                var scene = Scene(); var result = Validate(scene);
                string prompt = ComposeChatViewImagePrompt(Identity(), scene, "Cell 1=player; Cell 2=ira; Cell 3=rhagaea", result);
                require(result.Count == 2 && prompt.Contains("rougher clothes") && prompt.Contains("VALIDATED CLOTHING AT THE SELECTED MOMENT")
                    && prompt.EndsWith(ChatViewIdentityLock, StringComparison.Ordinal) && !prompt.Contains("silk gown"), "The enacted shared clothing transition was lost or later clothes leaked backward.");
                require(!result.Any(a => ReadString(a, "characterId", "") == "rhagaea"), "A bystander was redressed.");
            });
            check("wardrobe_opaque_turn_ids_use_source_order", () => require(Validate(Scene()).Count == 2, "Lexical turn-ID order replaced actual history."));
            check("wardrobe_no_clothing_preserves_portrait_default", () => {
                var scene = Scene(); scene["wardrobeOverrides"] = new object[0];
                string prompt = ComposeChatViewImagePrompt(Identity(), scene, "map", Validate(scene));
                require(prompt.Contains("\n[]\n") && !prompt.Contains("rougher clothes") && prompt.Contains("Portrait wardrobe is the default"), "No-change scene redressed the portraits.");
            });
            check("wardrobe_unanswered_plan_rejected", () => {
                var scene = Scene(); ReadDictionaryList(ReadDictionaryList(scene, "wardrobeOverrides")[0], "changes")[0]["evidence"] = new[] {
                    E("z_proposal", "rougher clothes", "proposal"), E("b_action", "Ira lunges toward the player.", "activity") };
                require(Reject(() => Validate(scene)), "A proposal plus activity silently became consent.");
            });
            check("wardrobe_acceptance_without_activity_rejected", () => {
                var scene = Scene(); ReadDictionaryList(ReadDictionaryList(scene, "wardrobeOverrides")[0], "changes")[0]["evidence"] = new[] {
                    E("z_proposal", "rougher clothes", "proposal"), E("a_accept", "Agreed, let us do that.", "acceptance") };
                require(Reject(() => Validate(scene)), "An unperformed plan redressed the scene.");
            });
            check("wardrobe_present_bystander_cannot_inherit_agreement", () => {
                var scene = Scene(); ReadDictionaryList(scene, "wardrobeOverrides")[0]["characterId"] = "rhagaea";
                require(Reject(() => Validate(scene)), "A present non-signing bystander inherited the other two people's change.");
            });
            check("wardrobe_one_speaker_cannot_accept_for_everyone", () => {
                var turns = window.Select(CloneDictionary).ToList(); turns[1]["speakerId"] = "player";
                require(Reject(() => ValidateChatViewWardrobe(Scene(), turns, new List<Dictionary<string, object>>())), "A proposer accepted on another person's behalf.");
            });
            check("wardrobe_future_within_selected_reply_rejected", () => {
                var scene = Scene(); scene["wardrobeOverrides"] = new[] { TestDict("characterId", "ira", "changes", new[] {
                    TestDict("scope", "outfit", "operation", "replace", "garmentText", "silk gown", "basis", "explicit",
                        "evidence", new[] { E("b_action", "Afterwards she changes into a silk gown.", "wearing") }) }) };
                require(Reject(() => Validate(scene)), "A later sentence in the same NPC reply changed the earlier highlight.");
            });
            check("wardrobe_unknown_or_refused_basis_rejected", () => {
                var scene = Scene(); ReadDictionaryList(ReadDictionaryList(scene, "wardrobeOverrides")[0], "changes")[0]["basis"] = "unrejected_proposal";
                require(Reject(() => Validate(scene)), "Silence was treated as an agreement.");
            });
            check("wardrobe_invented_color_rejected", () => {
                var scene = Scene(); ReadDictionaryList(ReadDictionaryList(scene, "wardrobeOverrides")[0], "changes")[0]["garmentText"] = "blue rougher clothes";
                require(Reject(() => Validate(scene)), "A new color was invented.");
            });
            check("wardrobe_duplicate_and_absent_actors_rejected", () => {
                var scene = Scene(); ReadDictionaryList(scene, "wardrobeOverrides")[1]["characterId"] = "player";
                require(Reject(() => Validate(scene)), "Duplicate actor accepted.");
                scene = Scene(); ReadDictionaryList(scene, "wardrobeOverrides")[1]["characterId"] = "absent";
                require(Reject(() => Validate(scene)), "Absent actor accepted.");
            });
            check("wardrobe_invented_quote_rejected", () => {
                var scene = Scene(); ReadDictionaryList(ReadDictionaryList(ReadDictionaryList(scene, "wardrobeOverrides")[0], "changes")[0], "evidence")[0]["quote"] = "We are wearing leather armor.";
                require(Reject(() => Validate(scene)), "A fabricated wardrobe quote reached the image prompt.");
            });
            check("wardrobe_age_and_style_fields_rejected", () => {
                var scene = Scene(); ReadDictionaryList(scene, "wardrobeOverrides")[0]["age"] = 18;
                require(Reject(() => Validate(scene)), "An age override was admitted as clothing.");
                scene = Scene(); ReadDictionaryList(ReadDictionaryList(scene, "wardrobeOverrides")[0], "changes")[0]["stylePrompt"] = "Make her look younger";
                require(Reject(() => Validate(scene)), "Free-form style instructions were admitted as clothing.");
            });
            check("wardrobe_malformed_arrays_rejected", () => {
                var scene = Scene(); scene["wardrobeOverrides"] = TestDict("characterId", "ira");
                require(Reject(() => Validate(scene)), "An object was silently treated as an empty clothing array.");
                scene["wardrobeOverrides"] = new object[] { "not an override" };
                require(Reject(() => Validate(scene)), "Invalid array elements were silently dropped.");
            });
            check("wardrobe_scene_and_actor_continuity_required", () => {
                var turns = window.Select(CloneDictionary).ToList(); var changed = CloneDictionary(frame); changed["room"] = "another place"; turns[2]["frame"] = changed;
                require(Reject(() => ValidateChatViewWardrobe(Scene(), turns, new List<Dictionary<string, object>>())), "Clothes crossed a scene transition.");
                changed = CloneDictionary(frame); changed["actors"] = new[] { TestDict("id", "ira") }; turns[2]["frame"] = changed;
                require(Reject(() => ValidateChatViewWardrobe(Scene(), turns, new List<Dictionary<string, object>>())), "An actor's absence did not invalidate their earlier scene outfit.");
            });
            check("wardrobe_partial_removal_does_not_replace_whole_outfit", () => {
                var turns = window.Select(CloneDictionary).ToList(); turns[0]["text"] = "I remove my cloak."; turns[0]["sourceHash"] = PromptHash("I remove my cloak.");
                var scene = Scene(); scene["wardrobeOverrides"] = new[] { TestDict("characterId", "player", "changes", new[] {
                    TestDict("scope", "garment", "operation", "remove", "garmentText", "cloak", "basis", "explicit",
                        "evidence", new[] { E("z_proposal", "I remove my cloak.", "wearing") }) }) };
                var result = ValidateChatViewWardrobe(scene, turns, new List<Dictionary<string, object>>());
                string prompt = ComposeChatViewImagePrompt(Identity(turns), scene, "map", result);
                require(prompt.Contains("\"scope\":\"garment\"") && prompt.Contains("\"operation\":\"remove\"") && prompt.Contains("every other compatible portrait garment/accessory stays"), "A partial removal replaced the entire outfit.");
            });
            check("wardrobe_quoted_offsets_are_derived_with_unicode", () => {
                var source = T("unicode", 1, "ira", "😀 She parries. She parries.");
                require(Reject(() => ChatViewQuoteSpan(TestDict("quote", "She parries."), source, true)), "Repeated moment was ambiguous.");
                var span = ChatViewQuoteSpan(TestDict("quote", "She parries.", "occurrence", 1), source, true);
                require(ReadInt(span, "start", -1) == ReadString(source, "text", "").LastIndexOf("She parries.", StringComparison.Ordinal), "Verified UTF-16 position was wrong.");
                require(Reject(() => ChatViewQuoteSpan(TestDict("quote", "She parries.", "occurrence", 1, "start", 0), source, true)), "False offsets were accepted.");
            });
            check("wardrobe_partial_replacement_preserves_scope_and_unmentioned_items", () => {
                var turns = window.Select(CloneDictionary).ToList(); turns[0]["text"] = "I put on a coarse linen shirt."; turns[0]["sourceHash"] = PromptHash(ReadString(turns[0], "text", ""));
                var scene = Scene(); scene["wardrobeOverrides"] = new[] { TestDict("characterId", "player", "changes", new[] {
                    TestDict("scope", "garment", "operation", "replace", "garmentText", "coarse linen shirt", "basis", "explicit",
                        "evidence", new[] { E("z_proposal", "I put on a coarse linen shirt.", "wearing") }) }) };
                string prompt = ComposeChatViewImagePrompt(Identity(turns), scene, "map", ValidateChatViewWardrobe(scene, turns, new List<Dictionary<string, object>>()));
                require(prompt.Contains("coarse linen shirt") && prompt.Contains("\"scope\":\"garment\"") && prompt.Contains("every other compatible portrait garment/accessory stays"), "A replacement lost the other portrait garments.");
            });
            check("wardrobe_explicit_outfit_reset_does_not_layer_prior_changes", () => {
                var turns = window.Select(CloneDictionary).ToList(); turns[0]["text"] = "I return to my original clothes."; turns[0]["sourceHash"] = PromptHash(ReadString(turns[0], "text", ""));
                var scene = Scene(); scene["wardrobeOverrides"] = new[] { TestDict("characterId", "player", "changes", new[] {
                    TestDict("scope", "outfit", "operation", "portrait", "garmentText", "original clothes", "basis", "explicit",
                        "evidence", new[] { E("z_proposal", "I return to my original clothes.", "wearing") }) }) };
                var result = ValidateChatViewWardrobe(scene, turns, new List<Dictionary<string, object>>());
                require(ReadString(ReadDictionaryList(result[0], "changes")[0], "operation", "") == "portrait", "A portrait reset did not clear the old outfit.");
            });
            check("wardrobe_source_digest_and_mixed_arrays_rejected", () => {
                var turns = window.Select(CloneDictionary).ToList(); turns[0]["sourceHash"] = "wrong";
                require(Reject(() => ValidateChatViewWardrobe(Scene(), turns, new List<Dictionary<string, object>>())), "Changed source bytes were accepted.");
                var scene = Scene(); ReadDictionaryList(scene, "wardrobeOverrides")[0]["changes"] = new object[] { Change(), "injected" };
                require(Reject(() => Validate(scene)), "Invalid change-array elements were ignored.");
            });
            check("wardrobe_native_age_and_equipment_stay_out_of_both_inputs", () => {
                var source = CloneDictionary(window[0]); source["wardrobeActorIds"] = new[] { "player", "ira" };
                string input = Json.Serialize(ChatViewClothingSelectionInput(window, TestDict("sources", new[] { source }, "coverage", "retained_scene_evidence")));
                require(!input.Contains("36.94") && !input.Contains("native red dress") && input.Contains("contextOnly"), "Native appearance metadata returned through clothing history.");
            });
            check("wardrobe_legacy_job_keeps_frozen_portrait_contract", () => {
                var scene = Scene(); var identity = Identity(); identity.Remove("wardrobeContract");
                identity["photoPrompt"] = ChatViewPortraitPhotoPrompt + "\n" + ChatViewAppearanceLock;
                string prompt = ComposeChatViewImagePrompt(identity, scene, "map", Validate(scene));
                require(prompt.EndsWith(ChatViewAppearanceLock, StringComparison.Ordinal) && !prompt.Contains(ChatViewIdentityLock)
                    && !prompt.Contains("VALIDATED CLOTHING AT THE SELECTED MOMENT\n["), "A historical job was reinterpreted by the new clothing policy.");
            });
            check("wardrobe_custom_style_cannot_restore_old_redressing_ban", () => {
                string prompt = ChatViewEffectivePrompt(ChatViewPhotoPromptName, "My overcast lighting.\n" + ChatViewAppearanceLock);
                require(prompt.Contains("My overcast lighting.") && !prompt.Contains("same outfits") && prompt.EndsWith(ChatViewIdentityLock, StringComparison.Ordinal), "Custom lighting or new clothing/identity authority was lost.");
            });
            check("wardrobe_codex_writer_schema_carries_evidence_without_new_call", () => {
                var templates = CodexConversationSchemas.TemplateNames(CodexConversationContractKind.Individual).ToDictionary(n => n, n => "fixture");
                var schema = CodexConversationSchemas.Select(CodexConversationContractKind.Individual, templates, templates, TestDict());
                require(schema.Schema != null && Json.Serialize(schema.Schema).Contains("viewWardrobeEvidence"), "Strict writer output dropped clothing evidence.");
            });
            check("wardrobe_garment_case_is_canonicalized_from_real_source_words", () => {
                var turns = window.Select(CloneDictionary).ToList(); turns[0]["text"] = "Let us change into ROUGHER CLOTHES to spar together."; turns[0]["sourceHash"] = PromptHash(ReadString(turns[0], "text", ""));
                var scene = Scene(); foreach (var actor in ReadDictionaryList(scene, "wardrobeOverrides"))
                    ReadDictionaryList(ReadDictionaryList(actor, "changes")[0], "evidence")[0]["quote"] = ReadString(turns[0], "text", "");
                var result = ValidateChatViewWardrobe(scene, turns, new List<Dictionary<string, object>>());
                require(ReadString(ReadDictionaryList(result[0], "changes")[0], "garmentText", "") == "ROUGHER CLOTHES", "Model capitalization changed or rejected the real clothing phrase.");
            });
            check("wardrobe_mutable_actor_location_cache_does_not_reset_a_continuing_conversation", () => {
                var request = TestDict("locationId", "camp", "sceneInterface", "party");
                string before = ChatViewWardrobeSceneKey(request);
                request["conversationSceneState"] = TestDict("current", new[] { TestDict("hasNarrativeLocation", true, "location", "by the sparring rail") });
                require(ChatViewWardrobeSceneKey(request) == before, "A per-actor prose/cache update changed the native wardrobe boundary.");
            });
            check("wardrobe_previous_shipped_portrait_defaults_migrate", () => {
                string root = Path.Combine(Path.GetTempPath(), "reign-view-wardrobe-defaults-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    File.WriteAllText(Path.Combine(root, ChatViewSelectionPromptName), ChatViewPortraitSelectionPrompt);
                    File.WriteAllText(Path.Combine(root, ChatViewPhotoPromptName), ChatViewPortraitPhotoPrompt);
                    MigrateChatViewPromptDefaults(root); MigrateChatViewPromptDefaults(root);
                    require(File.ReadAllText(Path.Combine(root, ChatViewSelectionPromptName)) == ChatViewCurrentSelectionPrompt
                        && File.ReadAllText(Path.Combine(root, ChatViewPhotoPromptName)) == ChatViewCurrentPhotoPrompt, "The currently shipped v2 defaults retained the old outfit ban.");
                }
                finally
                {
                    if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                        || !Path.GetFileName(root).StartsWith("reign-view-wardrobe-defaults-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe clothing-default fixture cleanup.");
                    Directory.Delete(root, true);
                }
            });
            RunChatViewWardrobeDatabaseChecks(check, require);
            RunChatViewCoverageChecks(check, require);
        }

        private static void RunChatViewWardrobeDatabaseChecks(Action<string, Action> check, Action<bool, string> require)
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)) throw new InvalidOperationException("Clothing history tests require isolated Verification Lab storage.");
            string campaign = "view_wardrobe_" + Guid.NewGuid().ToString("N");
            try
            {
                using var c = OpenCampaignConnection(campaign);
                ReadMemoryPrecisionState(c); EnsureChatViewSchema(c);
                ExecuteSql(c, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json)
VALUES('clothes',$campaign,'ira','player','open',1,1,'[""player"",""ira""]');", TestDict("campaign", campaign));
                var people = new[] { TestDict("heroStringId", "player", "name", "Player"), TestDict("heroStringId", "ira", "name", "Ira") };
                var payload = TestDict("timelineId", "main", "locationId", "camp", "sceneInterface", "party", "sceneParticipants", people, "activeHeroIds", new[] { "ira" });
                for (int n = 1; n <= 6; n++)
                {
                    string p = n == 1 ? "Let us change into rougher clothes to spar together." : "We continue training.";
                    string answer = n == 1 ? "Agreed, let us do that." : "Ira lunges toward the player.";
                    InsertConversationTurn(c, "p" + n, "clothes", "event" + n, n * 2, "e" + n, "player", "player", "Player", p, "party_chat", 4, 100, "camp", new List<string> { "player", "ira" }, payload);
                    InsertConversationTurn(c, "n" + n, "clothes", "event" + n, n * 2 + 1, "e" + n, "npc", "ira", "Ira", answer, "party_chat", 4, 100, "camp", new List<string> { "player", "ira" }, payload);
                    var rows = QuerySql(c, "SELECT * FROM conversation_turns WHERE session_id='clothes' AND exchange_id=$e ORDER BY turn_order;", TestDict("e", "e" + n));
                    StoreChatViewWardrobeEvidence(c, payload, TestDict("viewWardrobeEvidence", n == 1 ? new[] {
                        TestDict("source", "player", "characterIds", new[] { "player", "ira" }, "quote", p) } : new Dictionary<string, object>[0]), rows, "ira");
                }
                var request = TestDict("campaignId", campaign, "sessionId", "clothes", "playerId", "player", "timelineId", "main", "worldDay", 4d);
                var window = ReadChatViewWindow(c, request);
                var history = ReadChatViewWardrobeHistory(c, request, window);
                var old = ReadDictionaryList(history, "sources");
                check("coverage_database_retains_original_player_hints_and_current_window", () => {
                    var hints = ReadChatViewCoverageEvidence(c, "clothes", window, history);
                    require(hints.Count == 1 && ReadString(hints[0], "turnId", "") == "p1"
                        && ReadStringList(hints[0], "characterIds").Count == 2, "Older source nominations were dropped or multiplied.");
                    using (var transaction = c.BeginTransaction())
                    {
                        var row = QuerySql(c, "SELECT payload_json FROM chat_view_wardrobe_evidence WHERE entry_id='n6';").Single();
                        var nomination = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                        nomination["evidence"] = new[] { TestDict("turnId", "p6", "quote", "We continue training.", "sourceHash", PromptHash("We continue training."),
                            "start", 0, "length", "We continue training.".Length, "characterIds", new[] { "player" }) };
                        ExecuteSql(c, "UPDATE chat_view_wardrobe_evidence SET payload_json=$payload WHERE entry_id='n6';", TestDict("payload", Json.Serialize(nomination)));
                        hints = ReadChatViewCoverageEvidence(c, "clothes", window, history);
                        require(hints.Count == 2 && hints.Any(h => ReadString(h, "turnId", "") == "p6"), "Current-window player evidence was not exposed to the selector.");
                        require(ReadChatViewCoverageEvidence(c, "other", window, history).Count == 0, "Other-session nominations leaked.");
                        transaction.Rollback();
                    }
                });
                check("wardrobe_older_than_four_exchanges_keeps_proposal_and_actual_answer", () => require(old.Count == 2
                    && old.Any(t => ReadString(t, "text", "").Contains("rougher clothes")) && old.Any(t => ReadString(t, "text", "") == "Agreed, let us do that.")
                    && window.Select(t => ReadString(t, "exchangeId", "")).Distinct().SequenceEqual(new[] { "e3", "e4", "e5", "e6" }), "Older clothing or its acceptance disappeared, or the highlight window expanded."));
                check("wardrobe_history_is_not_hourly_native_cache", () => {
                    request["worldDay"] = 10d;
                    require(ReadDictionaryList(ReadChatViewWardrobeHistory(c, request, window), "sources").Count == 2, "Accepted clothing evidence expired by game hour.");
                    request["worldDay"] = 4d;
                });
                check("wardrobe_older_agreed_outfit_reaches_final_image_prompt", () => {
                    var scene = TestDict("selectedTurnId", "n6", "characterIds", new[] { "player", "ira" },
                        "caption", "Sparring in camp", "description", "Ira lunges with a training blade as the player parries.",
                        "selectedMoment", TestDict("quote", "Ira lunges toward the player."),
                        "evidence", new[] { TestDict("turnId", "n6", "quote", "Ira lunges toward the player.") });
                    Dictionary<string, object> Change() => TestDict("scope", "outfit", "operation", "replace", "garmentText", "rougher clothes",
                        "basis", "accepted_plan_then_activity", "evidence", new[] {
                            TestDict("turnId", "p1", "quote", "Let us change into rougher clothes to spar together.", "kind", "proposal"),
                            TestDict("turnId", "n1", "quote", "Agreed, let us do that.", "kind", "acceptance"),
                            TestDict("turnId", "n6", "quote", "Ira lunges toward the player.", "kind", "activity") });
                    scene["wardrobeOverrides"] = new[] { TestDict("characterId", "player", "changes", new[] { Change() }), TestDict("characterId", "ira", "changes", new[] { Change() }) };
                    ValidateChatViewScene(scene, window);
                    var result = ValidateChatViewWardrobe(scene, window, old);
                    string prompt = ComposeChatViewImagePrompt(TestDict("window", window, "wardrobeContract", ChatViewWardrobeContract,
                        "photoPrompt", ChatViewEffectivePrompt(ChatViewPhotoPromptName, ChatViewCurrentPhotoPrompt)), scene, "player, ira", result);
                    require(result.Count == 2 && prompt.Contains("rougher clothes") && prompt.EndsWith(ChatViewIdentityLock, StringComparison.Ordinal), "Older source-grounded clothes did not survive to the actual image prompt.");
                });
                check("wardrobe_unverified_branch_cannot_read_history", () => {
                    request["timelineId"] = "foreign";
                    require(ReadDictionaryList(ReadChatViewWardrobeHistory(c, request, window), "sources").Count == 0, "A foreign branch read clothing history.");
                    request["timelineId"] = "main";
                });
                check("wardrobe_same_scene_other_session_cannot_read_history", () => {
                    request["sessionId"] = "other";
                    require(ReadDictionaryList(ReadChatViewWardrobeHistory(c, request, window), "sources").Count == 0, "Clothes leaked into a different conversation.");
                    request["sessionId"] = "clothes";
                });
                check("wardrobe_rejected_and_invalid_nominations_do_not_change_accepted_words", () => {
                    var rows = QuerySql(c, "SELECT * FROM conversation_turns WHERE session_id='clothes' AND exchange_id='e6' ORDER BY turn_order;");
                    var receipt = StoreChatViewWardrobeEvidence(c, payload, TestDict("viewWardrobeEvidence", new[] {
                        TestDict("source", "reply", "characterIds", new[] { "absent" }, "quote", "invented clothes") }), rows, "ira");
                    require(ReadStringList(receipt, "rejected").Count == 1 && ReadString(rows.Last(), "text", "") == "Ira lunges toward the player.", "Optional clothing metadata rewrote/rejected dialogue.");
                });
                check("wardrobe_evidence_journal_replays_idempotently", () => require(QuerySql(c, "SELECT * FROM chat_view_wardrobe_evidence WHERE session_id='clothes';").Count == 6, "An accepted response got duplicate clothing journal rows."));
                check("wardrobe_scene_transition_discards_earlier_context", () => {
                    var modified = window.Select(CloneDictionary).ToList(); var frame = CloneDictionary(ReadDictionary(modified[0], "frame")); frame["wardrobeScene"] = "another scene"; modified[0]["frame"] = frame;
                    require(ReadDictionaryList(ReadChatViewWardrobeHistory(c, request, modified), "sources").Count == 0, "Clothing context crossed a physical scene boundary.");
                });
                check("wardrobe_refusal_context_and_source_journal_rollback_are_atomic", () => {
                    using (var transaction = c.BeginTransaction())
                    {
                        for (int n = 7; n <= 11; n++)
                        {
                            string text = n == 7 ? "Let us change into red suits." : "We continue training.";
                            string answer = n == 7 ? "No, I will keep my current clothes." : "Ira raises her training blade.";
                            InsertConversationTurn(c, "p" + n, "clothes", "event" + n, n * 2, "e" + n, "player", "player", "Player", text, "party_chat", 4, 100, "camp", new List<string> { "player", "ira" }, payload);
                            InsertConversationTurn(c, "n" + n, "clothes", "event" + n, n * 2 + 1, "e" + n, "npc", "ira", "Ira", answer, "party_chat", 4, 100, "camp", new List<string> { "player", "ira" }, payload);
                            var rows = QuerySql(c, "SELECT * FROM conversation_turns WHERE session_id='clothes' AND exchange_id=$e ORDER BY turn_order;", TestDict("e", "e" + n));
                            StoreChatViewWardrobeEvidence(c, payload, TestDict("viewWardrobeEvidence", n == 7 ? new[] {
                                TestDict("source", "player", "characterIds", new[] { "player", "ira" }, "quote", text) } : new Dictionary<string, object>[0]), rows, "ira");
                        }
                        var later = ReadChatViewWindow(c, request);
                        var context = ReadDictionaryList(ReadChatViewWardrobeHistory(c, request, later), "sources");
                        require(context.Any(t => ReadString(t, "turnId", "") == "n7" && ReadString(t, "text", "") == "No, I will keep my current clothes.")
                            && context.Any(t => ReadString(t, "turnId", "") == "p7" && ReadString(t, "text", "").Contains("red suits")), "A clothing proposal lost its actual refusal.");
                        transaction.Rollback();
                    }
                    require(QuerySql(c, "SELECT * FROM conversation_turns WHERE turn_id='n7';").Count == 0
                        && QuerySql(c, "SELECT * FROM chat_view_wardrobe_evidence WHERE entry_id='n7';").Count == 0, "Rollback retained clothing evidence without its accepted source.");
                });
                c.Dispose();
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "before_clothing_change");
                using (var changed = OpenCampaignConnection(campaign)) ExecuteSql(changed, "DELETE FROM chat_view_wardrobe_evidence;");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "before_clothing_change");
                using var restored = OpenCampaignConnection(campaign);
                check("wardrobe_actual_save_snapshot_restores_frozen_evidence", () => require(ReadDictionaryList(ReadChatViewWardrobeHistory(restored, request, window), "sources").Count == 2, "Save restore lost the independent clothing journal."));
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
        }
    }
}
