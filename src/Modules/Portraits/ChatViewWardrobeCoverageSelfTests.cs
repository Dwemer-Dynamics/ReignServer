using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void RunChatViewCoverageChecks(Action<string, Action> check, Action<bool, string> require)
        {
            const string action = "Bryn parries Alex's wooden blade.";
            string[] ids = { "player", "bryn", "casey" };
            string[] turns = { "p0", "n0", "n1" };
            string[] garments = { "plain training tunic", "rough linen training outfit", "wool practice outfit" };
            Dictionary<string, object> Frame() => TestDict("locationId", "practice_camp", "sceneInterface", "party", "actors",
                ids.Select(id => TestDict("id", id, "name", id, "age", 46.94d, "isChild", false,
                    "clothing", "LITERAL_NATIVE_ARMOR_X")).ToList());
            Dictionary<string, object> Turn(string id, int order, string speaker, string text) => TestDict("turnId", id,
                "turnOrder", order, "exchangeId", order < 4 ? "e1" : "e2", "speakerId", speaker,
                "role", speaker == "player" ? "player" : "npc", "text", text, "sourceHash", PromptHash(text), "frame", Frame());
            Dictionary<string, object> Hint(Dictionary<string, object> t, string id, string actor, string quote = null) {
                string text = ReadString(t, "text", ""); quote ??= text;
                return TestDict("evidenceId", id, "turnId", ReadString(t, "turnId", ""), "quote", quote,
                    "start", text.IndexOf(quote, StringComparison.Ordinal), "length", quote.Length, "sourceHash", PromptHash(text), "characterIds", new[] { actor });
            }
            Dictionary<string, object> Identity() {
                var window = new List<Dictionary<string, object>> {
                    Turn("p0", 1, "player", "I change into a plain training tunic."),
                    Turn("n0", 2, "bryn", "Bryn changes into a rough linen training outfit."),
                    Turn("n1", 3, "casey", "Casey changes into a wool practice outfit."),
                    Turn("p1", 4, "player", "We raise our practice blades."),
                    Turn("n2", 5, "bryn", action + " Afterwards Bryn changes into a silk gown.") };
                return TestDict("window", window, "wardrobeHistory", TestDict("sources", new object[0], "coverage", "legacy_or_incomplete_nominations"),
                    "wardrobeContract", ChatViewWardrobeContract, "wardrobeCoverageContract", ChatViewCoverageContract,
                    "wardrobeCoverageEvidence", ids.Select((id, i) => Hint(window[i], "h" + i, id)).ToList(),
                    "photoPrompt", ChatViewEffectivePrompt(ChatViewPhotoPromptName, ChatViewCurrentPhotoPrompt));
            }
            Dictionary<string, object> Change(Dictionary<string, object> identity, int actor, string turn = null, string phrase = null) {
                var source = ReadDictionaryList(identity, "window").Single(t => ReadString(t, "turnId", "") == (turn ?? turns[actor]));
                return TestDict("scope", "outfit", "operation", "replace", "garmentText", phrase ?? garments[actor], "basis", "explicit",
                    "evidence", new[] { TestDict("turnId", ReadString(source, "turnId", ""), "quote", ReadString(source, "text", ""), "kind", "wearing") });
            }
            Dictionary<string, object> Resolution(string hint, string disposition = "applied", params Dictionary<string, object>[] citations) =>
                TestDict("evidenceId", hint, "disposition", disposition, "reason", "Reviewed against the quoted source and current scene.", "citations", citations);
            Dictionary<string, object> Row(string id, string decision, params Dictionary<string, object>[] resolutions) =>
                TestDict("characterId", id, "decision", decision, "reason", "Reviewed the sources for this character's current clothing.", "evidence", resolutions);
            Dictionary<string, object> Scene(Dictionary<string, object> identity) => TestDict("selectedTurnId", "n2", "characterIds", ids,
                "caption", "Practice in camp", "description", "Bryn and Alex spar while Casey watches from the practice rail.",
                "selectedMoment", TestDict("quote", action), "evidence", new[] { TestDict("turnId", "n2", "quote", action) },
                "wardrobeOverrides", ids.Select((id, i) => TestDict("characterId", id, "changes", new[] { Change(identity, i) })).ToList(),
                "wardrobeCoverage", ids.Select((id, i) => Row(id, "changes", Resolution("h" + i))).ToList());
            void Neutral(Dictionary<string, object> identity, int from) {
                var window = ReadDictionaryList(identity, "window");
                for (int i = from; i < 3; i++) { window[i]["text"] = ids[i] + " picks up a practice blade."; window[i]["sourceHash"] = PromptHash(ReadString(window[i], "text", "")); }
                identity["wardrobeCoverageEvidence"] = ReadDictionaryList(identity, "wardrobeCoverageEvidence").Take(from).ToList();
            }
            string Render(Dictionary<string, object> identity, Dictionary<string, object> scene) {
                ValidateChatViewScene(scene, ReadDictionaryList(identity, "window"));
                var plan = ValidateChatViewWardrobe(scene, ReadDictionaryList(identity, "window"), new List<Dictionary<string, object>>());
                // Actual image-prompt consumer must reject a bypassed incomplete plan.
                return ComposeChatViewImagePrompt(identity, scene, "Cell1=player;Cell2=bryn;Cell3=casey", plan);
            }
            bool Reject(Action run) { try { run(); return false; } catch (InvalidDataException) { return true; } }
            void RejectCase(string label, Action<Dictionary<string, object>, Dictionary<string, object>> mutate) => check(label, () => {
                var identity = Identity(); var scene = Scene(identity); mutate(identity, scene);
                require(Reject(() => Render(identity, scene)), "An incomplete/unverified clothing plan reached the image-prompt consumer.");
            });
            check("coverage_all_three_changes_reach_image_prompt", () => {
                var identity = Identity(); var scene = Scene(identity); string prompt = Render(identity, scene);
                require(garments.All(prompt.Contains) && ReadDictionaryList(scene, "wardrobeCoverageReceipt").Count == 3
                    && prompt.EndsWith(ChatViewIdentityLock, StringComparison.Ordinal), "One actor or immutable identity was lost.");
            });
            RejectCase("coverage_live_shape_three_cast_one_delta_rejected", (identity, scene) => {
                scene["wardrobeOverrides"] = ReadDictionaryList(scene, "wardrobeOverrides").Take(1).ToList();
                scene["wardrobeCoverage"] = ReadDictionaryList(scene, "wardrobeCoverage").Take(1).ToList();
            });
            RejectCase("coverage_missing_array_rejected", (identity, scene) => scene.Remove("wardrobeCoverage"));
            RejectCase("coverage_empty_array_rejected", (identity, scene) => scene["wardrobeCoverage"] = new object[0]);
            RejectCase("coverage_duplicate_actor_rejected", (identity, scene) => ReadDictionaryList(scene, "wardrobeCoverage")[2]["characterId"] = "bryn");
            RejectCase("coverage_unknown_actor_rejected", (identity, scene) => ReadDictionaryList(scene, "wardrobeCoverage")[2]["characterId"] = "absent");
            RejectCase("coverage_uncertain_stops_before_provider", (identity, scene) => ReadDictionaryList(scene, "wardrobeCoverage")[1]["decision"] = "uncertain");
            RejectCase("coverage_decision_contradicts_changes", (identity, scene) => ReadDictionaryList(scene, "wardrobeCoverage")[1]["decision"] = "portrait");
            RejectCase("coverage_false_default_cannot_drop_retained_nomination", (identity, scene) => {
                scene["wardrobeOverrides"] = ReadDictionaryList(scene, "wardrobeOverrides").Skip(1).ToList();
                scene["wardrobeCoverage"] = new[] { Row("player", "portrait"), Row("bryn", "changes", Resolution("h1")), Row("casey", "changes", Resolution("h2")) };
            });
            RejectCase("coverage_wrong_actor_evidence_rejected", (identity, scene) => ReadDictionaryList(scene, "wardrobeCoverage")[1]["evidence"] = new[] { Resolution("h0") });
            RejectCase("coverage_applied_claim_requires_admitted_change", (identity, scene) => {
                scene["wardrobeOverrides"] = ReadDictionaryList(scene, "wardrobeOverrides").Skip(1).ToList();
                ReadDictionaryList(scene, "wardrobeCoverage")[0]["decision"] = "portrait";
            });
            RejectCase("coverage_uncontextualized_dismissal_rejected", (identity, scene) => {
                ReadDictionaryList(scene, "wardrobeCoverage")[0]["evidence"] = new[] { Resolution("h0", "dismissed", TestDict("turnId", "n2", "quote", action)) };
            });
            RejectCase("coverage_invented_dismissal_quote_rejected", (identity, scene) => {
                ReadDictionaryList(scene, "wardrobeCoverage")[0]["evidence"] = new[] { Resolution("h0", "dismissed", TestDict("turnId", "p0", "quote", "I never changed my clothing.")) };
            });
            RejectCase("coverage_future_citation_rejected", (identity, scene) => {
                ReadDictionaryList(scene, "wardrobeCoverage")[0]["evidence"] = new[] { Resolution("h0", "dismissed", TestDict("turnId", "n2", "quote", "Afterwards Bryn changes into a silk gown.")) };
            });
            RejectCase("coverage_missing_frozen_evidence_array_rejected", (identity, scene) => identity.Remove("wardrobeCoverageEvidence"));
            RejectCase("coverage_malformed_frozen_evidence_rejected", (identity, scene) => identity["wardrobeCoverageEvidence"] = TestDict("evidenceId", "h0"));
            RejectCase("coverage_oversized_frozen_evidence_rejected", (identity, scene) => identity["wardrobeCoverageEvidence"] =
                Enumerable.Range(0, ChatViewCoverageEvidenceLimit + 1).Select(i => ReadDictionaryList(identity, "wardrobeCoverageEvidence")[0]).ToList());
            RejectCase("coverage_corrupt_source_hash_rejected", (identity, scene) => ReadDictionaryList(identity, "wardrobeCoverageEvidence")[0]["sourceHash"] = "wrong");
            RejectCase("coverage_duplicate_hint_identity_rejected", (identity, scene) => ReadDictionaryList(identity, "wardrobeCoverageEvidence")[1]["evidenceId"] = "h0");
            RejectCase("coverage_unknown_contract_rejected", (identity, scene) => identity["wardrobeCoverageContract"] = "unknown");
            RejectCase("coverage_identity_and_age_override_rejected", (identity, scene) => ReadDictionaryList(scene, "wardrobeCoverage")[0]["apparentAge"] = 20);
            check("coverage_one_change_two_explicit_defaults", () => {
                var identity = Identity(); Neutral(identity, 1);
                var scene = Scene(Identity()); scene["wardrobeOverrides"] = new[] { TestDict("characterId", "player", "changes", new[] { Change(identity, 0) }) };
                scene["wardrobeCoverage"] = new[] { Row("player", "changes", Resolution("h0")), Row("bryn", "portrait"), Row("casey", "portrait") };
                string prompt = Render(identity, scene);
                require(prompt.Contains("plain training tunic") && prompt.Contains("\"characterId\":\"bryn\",\"wardrobe\":\"mapped_portrait\"")
                    && prompt.Contains("\"characterId\":\"casey\",\"wardrobe\":\"mapped_portrait\""), "Untouched actors were redressed or omitted.");
            });
            check("coverage_all_portrait_defaults_remain_explicit", () => {
                var identity = Identity(); Neutral(identity, 0); var scene = Scene(Identity());
                scene["wardrobeOverrides"] = new object[0]; scene["wardrobeCoverage"] = ids.Select(id => Row(id, "portrait")).ToList();
                string prompt = Render(identity, scene);
                require(ids.All(id => prompt.Contains("\"characterId\":\"" + id + "\",\"wardrobe\":\"mapped_portrait\"")), "A default actor disappeared.");
            });
            check("coverage_refused_proposal_allows_grounded_default", () => {
                var identity = Identity(); Neutral(identity, 0); var window = ReadDictionaryList(identity, "window");
                window[0]["text"] = "I might change into a blue practice coat."; window[0]["sourceHash"] = PromptHash(ReadString(window[0], "text", ""));
                window[1]["text"] = "Bryn says: No, keep the portrait clothes."; window[1]["sourceHash"] = PromptHash(ReadString(window[1], "text", ""));
                identity["wardrobeCoverageEvidence"] = new[] { Hint(window[0], "h0", "player") };
                var scene = Scene(Identity()); scene["wardrobeOverrides"] = new object[0];
                scene["wardrobeCoverage"] = new[] { Row("player", "portrait", Resolution("h0", "dismissed",
                    TestDict("turnId", "p0", "quote", ReadString(window[0], "text", "")), TestDict("turnId", "n0", "quote", ReadString(window[1], "text", "")))), Row("bryn", "portrait"), Row("casey", "portrait") };
                require(Render(identity, scene).Contains("mapped_portrait"), "A reviewed unperformed proposal forced an invented outfit.");
            });
            check("coverage_future_hint_does_not_rewrite_earlier_moment", () => {
                var identity = Identity(); var window = ReadDictionaryList(identity, "window");
                identity["wardrobeCoverageEvidence"] = ReadDictionaryList(identity, "wardrobeCoverageEvidence").Concat(new[] {
                    Hint(window.Last(), "future", "bryn", "Afterwards Bryn changes into a silk gown.") }).ToList();
                string prompt = Render(identity, Scene(identity)); require(!prompt.Contains("silk gown"), "Future clothing leaked into the image.");
            });
            check("coverage_crossing_nomination_retains_earlier_clothing", () => {
                var identity = Identity(); var window = ReadDictionaryList(identity, "window");
                const string wearing = "Bryn changes into a leather training vest.";
                window.Last()["text"] = wearing + " " + action + " Afterwards Bryn changes into a silk gown.";
                window.Last()["sourceHash"] = PromptHash(ReadString(window.Last(), "text", ""));
                identity["wardrobeCoverageEvidence"] = new[] { ReadDictionaryList(identity, "wardrobeCoverageEvidence")[0],
                    Hint(window.Last(), "h1", "bryn"), ReadDictionaryList(identity, "wardrobeCoverageEvidence")[2] };
                var scene = Scene(identity); ReadDictionaryList(scene, "wardrobeOverrides")[1]["changes"] = new[] { TestDict("scope", "outfit", "operation", "replace",
                    "garmentText", "leather training vest", "basis", "explicit", "evidence", new[] { TestDict("turnId", "n2", "quote", wearing, "kind", "wearing") }) };
                string prompt = Render(identity, scene);
                require(prompt.Contains("leather training vest") && !prompt.Contains("silk gown"), "A crossing nomination lost its earlier clothing or leaked its later state.");
                scene["wardrobeOverrides"] = ReadDictionaryList(scene, "wardrobeOverrides").Where(a => ReadString(a, "characterId", "") != "bryn").ToList();
                scene["wardrobeCoverage"] = new[] { Row("player", "changes", Resolution("h0")), Row("bryn", "portrait"), Row("casey", "changes", Resolution("h2")) };
                require(Reject(() => Render(identity, scene)), "A broad quote silently erased known pre-moment clothing evidence.");
            });
            check("coverage_crossing_context_citation_can_exclude_later_text", () => {
                var identity = Identity(); var window = ReadDictionaryList(identity, "window");
                ReadDictionaryList(identity, "wardrobeCoverageEvidence")[1] = Hint(window.Last(), "h1", "bryn");
                identity["wardrobeCoverageEvidence"] = new[] { ReadDictionaryList(identity, "wardrobeCoverageEvidence")[0],
                    Hint(window.Last(), "h1", "bryn"), ReadDictionaryList(identity, "wardrobeCoverageEvidence")[2] };
                var scene = Scene(identity); ReadDictionaryList(scene, "wardrobeCoverage")[1]["evidence"] = new[] {
                    Resolution("h1", "dismissed", TestDict("turnId", "n2", "quote", action)) };
                require(!Render(identity, scene).Contains("silk gown"), "Review required a future citation to account for the earlier prefix.");
            });
            check("coverage_later_whole_outfit_supersedes_old_hint", () => {
                var identity = Identity(); var window = ReadDictionaryList(identity, "window");
                var later = Turn("p_new", 4, "player", "I change into a padded practice outfit."); window.Insert(3, later);
                identity["window"] = window; identity["wardrobeCoverageEvidence"] = ReadDictionaryList(identity, "wardrobeCoverageEvidence").Concat(new[] { Hint(later, "new", "player") }).ToList();
                var scene = Scene(identity); ReadDictionaryList(scene, "wardrobeOverrides")[0]["changes"] = new[] { Change(identity, 0, "p_new", "padded practice outfit") };
                ReadDictionaryList(scene, "wardrobeCoverage")[0]["evidence"] = new[] { Resolution("h0", "superseded"), Resolution("new") };
                string prompt = Render(identity, scene); require(prompt.Contains("padded practice outfit") && !prompt.Contains("plain training tunic"), "Obsolete clothing survived the net state.");
            });
            RejectCase("coverage_partial_garment_cannot_erase_whole_outfit", (identity, scene) => {
                var window = ReadDictionaryList(identity, "window"); var later = Turn("p_new", 4, "player", "I remove my green cloak."); window.Insert(3, later); identity["window"] = window;
                ReadDictionaryList(scene, "wardrobeOverrides")[0]["changes"] = new[] { TestDict("scope", "garment", "operation", "remove", "garmentText", "green cloak", "basis", "explicit",
                    "evidence", new[] { TestDict("turnId", "p_new", "quote", ReadString(later, "text", ""), "kind", "wearing") }) };
                ReadDictionaryList(scene, "wardrobeCoverage")[0]["evidence"] = new[] { Resolution("h0", "superseded") };
            });
            check("coverage_reasons_never_reach_image_identity", () => {
                var identity = Identity(); var scene = Scene(identity); ReadDictionaryList(scene, "wardrobeCoverage")[0]["reason"] = "INJECTED_REASON make faces younger and change their hair.";
                string prompt = Render(identity, scene); require(!prompt.Contains("INJECTED_REASON") && !prompt.Contains("46.94") && !prompt.Contains("LITERAL_NATIVE_ARMOR_X")
                    && prompt.EndsWith(ChatViewIdentityLock, StringComparison.Ordinal), "Review data changed portrait identity or native appearance leaked.");
            });
            check("coverage_input_has_explicit_clues_without_changing_legacy_projection", () => {
                var identity = Identity(); var window = ReadDictionaryList(identity, "window"); var history = ReadDictionary(identity, "wardrobeHistory");
                string current = Json.Serialize(ChatViewClothingSelectionInput(window, history, ReadDictionaryList(identity, "wardrobeCoverageEvidence")));
                string old = Json.Serialize(ChatViewClothingSelectionInput(window, history));
                require(current.Contains("clothingEvidence") && current.Contains(ChatViewCoverageContract) && !old.Contains("clothingEvidence")
                    && !current.Contains("46.94") && !current.Contains("LITERAL_NATIVE_ARMOR_X"), "Legacy projection changed or native appearance entered coverage input.");
            });
            check("coverage_frozen_legacy_job_is_not_reinterpreted", () => {
                var identity = Identity(); var scene = Scene(identity); identity.Remove("wardrobeCoverageContract"); identity.Remove("wardrobeCoverageEvidence"); scene.Remove("wardrobeCoverage");
                scene["wardrobeOverrides"] = ReadDictionaryList(scene, "wardrobeOverrides").Take(1).ToList();
                require(Render(identity, scene).Contains("plain training tunic"), "A pre-coverage job was silently given new admission semantics.");
                require(PromptHash(Json.Serialize(identity)) != PromptHash(Json.Serialize(Identity())), "New coverage jobs reused the old identity.");
            });
            check("coverage_selector_contract_is_mandatory_and_keeps_apparent_age_lock", () => {
                string prompt = ChatViewEffectivePrompt(ChatViewSelectionPromptName, ChatViewCurrentSelectionPrompt);
                require(prompt.Contains("EXACTLY ONE coverage row") && prompt.Contains("COMPLETE NET CLOTHING") && prompt.EndsWith(ChatViewIdentityLock, StringComparison.Ordinal), "Selector still permits silent actor omissions.");
            });
        }
    }
}
