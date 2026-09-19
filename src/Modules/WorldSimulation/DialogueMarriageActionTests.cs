using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunDialogueMarriageActionAssertions()
        {
            var rows = new List<Dictionary<string, object>>();
            Action<string, bool> check = (name, passed) => rows.Add(new Dictionary<string, object>
            { ["name"] = name, ["passed"] = passed, ["detail"] = passed ? "Passed." : "Marriage action regression failed." });
            var payload = MarriageActionFixture();
            var hero = ReadDictionary(payload, "hero");
            var emptyQueue = new List<Dictionary<string, object>>();

            foreach (string key in new[] { "source", "reason", "confidence", "requiresAcceptance", "actorHeroStringId",
                "actorClanId", "actorKingdomId", "targetHeroStringId", "targetClanId", "terms.marriageHero1StringId" })
                check("router_preserves_" + key, RouterArgumentTargetKey(NormalizeLookup(key), "marriage_alliance") == "");
            check("router_preserves_natural_transfer_direction",
                RouterArgumentTargetKey("from", "transfer_gold") == "FromHero"
                && RouterArgumentTargetKey("recipient", "transfer_gold") == "ToHero");

            // Reproduce the failed repair shape: source and actor clan/kingdom inside
            // arguments must not become FromHero, TargetClan or TargetKingdom aliases.
            var repairedInput = TestDict("command", "marriage_alliance", "arguments", TestDict(
                "source", "hidden_action_planner", "actorClanId", "player_faction", "actorKingdomId", "new_kingdom",
                "targetClanId", "istranentis", "terms", TestDict(
                    "marriageHero1StringId", "main_hero", "marriageHero2StringId", "zotyra")));
            var normalized = NormalizeRouterActions(repairedInput, new HashSet<string> { "marriage_alliance" },
                payload, hero, "Will you marry me? I accept.", "hidden_action_planner", "Marriage regression").Single();
            check("repair_source_is_not_a_hero", !normalized.ContainsKey("FromHero"));
            check("repair_actor_does_not_become_target", !normalized.ContainsKey("TargetClan") && !normalized.ContainsKey("TargetKingdom"));
            var terms = ReadDictionary(normalized, "terms");
            var errors = new List<string>();
            BindDialogueMarriageParticipants(normalized, terms, payload, "marriage_alliance", errors);
            BindDialogueActionAuthority(normalized, terms, payload, "marriage_alliance", "PoliticsMarriageAlliance", "marriage-regression", errors);
            check("accepted_exact_couple_gets_authority", errors.Count == 0
                && ReadString(normalized, "actorHeroStringId", "") == "main_hero"
                && ReadString(normalized, "targetHeroStringId", "") == "zotyra"
                && ReadString(normalized, "acceptedByHeroStringId", "") == "zotyra"
                && ReadString(ReadDictionary(terms, "authorityReceipt"), "policy", "") == "personal_marriage_consent");
            string initialHash = ReadString(normalized, "termsHash", "");
            BindDialogueActionAuthority(normalized, terms, payload, "marriage_alliance", "PoliticsMarriageAlliance", "marriage-regression", errors);
            check("authority_retry_preserves_bound_terms", errors.Count == 0 && initialHash.Length == 64
                && initialHash == ReadString(normalized, "termsHash", ""));

            foreach (string scenario in new[] { "missing_pair", "third_party", "self_pair", "conflicting_actor", "conflicting_spouse_alias", "missing_live_hero" })
            {
                var input = TestDict("source", "hidden_action_planner", "actorClanId", "player_faction", "targetClanId", "istranentis");
                var pair = scenario == "missing_pair" ? new Dictionary<string, object>() : TestDict(
                    "marriageHero1StringId", "main_hero", "marriageHero2StringId",
                    scenario == "third_party" ? "other_npc" : scenario == "self_pair" ? "main_hero" : "zotyra");
                var fixture = MarriageActionFixture();
                if (scenario == "conflicting_actor") input["actorHeroStringId"] = "hidden_action_planner";
                if (scenario == "conflicting_spouse_alias") pair["requestedMarriageHeroStringId"] = "other_npc";
                if (scenario == "missing_live_hero") ReadDictionary(fixture, "actionResolutionIndex")["heroes"] = new ArrayList();
                var rejected = new List<string>();
                BindDialogueMarriageParticipants(input, pair, fixture, "marriage_alliance", rejected);
                check("marriage_rejects_" + scenario, rejected.Count > 0 && !input.ContainsKey("authorizationMode"));
            }

            var reverse = TestDict("source", "dialogue", "actorKingdomId", "empire_w", "actorClanId", "istranentis");
            var reverseTerms = TestDict("marriageHero1StringId", "zotyra", "marriageHero2StringId", "main_hero");
            errors.Clear();
            BindDialogueMarriageParticipants(reverse, reverseTerms, payload, "marriage_alliance", errors);
            check("reversed_pair_uses_native_affiliations", errors.Count == 0
                && ReadString(reverse, "actorClanStringId", "") == "player_faction"
                && ReadString(reverse, "actorKingdomStringId", "") == "new_kingdom"
                && ReadString(reverseTerms, "marriageHero1StringId", "") == "main_hero");

            var unrelated = TestDict("source", "dialogue", "actorHeroStringId", "other_npc", "targetHeroStringId", "another_npc");
            errors.Clear();
            BindDialogueActionAuthority(unrelated, new Dictionary<string, object>(), payload,
                "transfer_gold", "RegularTransferGold", "authority-regression", errors);
            check("other_action_families_still_reject_third_party", errors.Count > 0);
            errors.Clear();
            check("missing_marriage_terms_cannot_bypass_authority", BindPersonalMarriageConsent(
                TestDict("actorHeroStringId", "main_hero", "targetHeroStringId", "zotyra"),
                new Dictionary<string, object>(), "zotyra", "main_hero", errors) == "personal_marriage_consent" && errors.Count > 0);

            const string acceptance = "I accept. I will marry you.";
            string failed = FinalizeDialogueActionOutcome(acceptance, payload, hero, emptyQueue, new List<string> { "internal identity failure" });
            check("failure_is_visible_without_debug", failed.StartsWith(acceptance, StringComparison.Ordinal)
                && failed.Contains("No game action was queued") && !failed.Contains("internal identity failure"));
            const string claim = "I am your wife now. Husband, the ceremony is complete.";
            string blocked = FinalizeDialogueActionOutcome(claim, payload, hero, emptyQueue, new List<string>());
            check("roleplay_ceremony_cannot_claim_completion", !blocked.Contains("I am your wife") && blocked.Contains("not been confirmed"));
            var queued = new List<Dictionary<string, object>> { TestDict("command", "marriage_alliance", "record", normalized) };
            check("queued_marriage_is_pending_not_completed", FinalizeDialogueActionOutcome(claim, payload, hero, queued,
                new List<string>()).Contains("awaiting confirmation"));
            foreach (string ordinary in new[] { "I will marry you.", "I am not your wife.", "I am your wife if we marry.",
                "What if I am your wife now.", "You wish I am your wife now.",
                "You said \"I am your wife now.\" That was untrue.", "We discussed marriage yesterday." })
                check("marriage_guard_preserves_" + ordinary, FinalizeDialogueActionOutcome(ordinary, payload, hero,
                    emptyQueue, new List<string>()) == ordinary);

            var indexed = ReadDictionaryList(ReadDictionary(payload, "actionResolutionIndex"), "heroes");
            indexed[0]["spouseId"] = "zotyra";
            indexed[1]["spouseId"] = "main_hero";
            check("native_reciprocal_marriage_allows_spouse_language", FinalizeDialogueActionOutcome(claim, payload, hero,
                emptyQueue, new List<string>()) == claim);
            indexed[0]["spouseId"] = "";
            check("one_sided_spouse_data_does_not_claim_completion", !NativePlayerMarriageConfirmed(payload, hero));
            check("marriage_catalog_is_mechanical_and_exact", ActionCapabilityForCommand("marriage_alliance") == "mechanical"
                && ReadStringList(ReadDictionaryList(ActionCatalog(), "commands").Single(x => ReadString(x, "command", "") == "marriage_alliance"), "required")
                    .Contains("terms.marriageHero2StringId"));
            check("marriage_prompt_distinguishes_vows_from_native_state", MarriageDialogueCompletionContract.Contains("not roleplay_only")
                && MarriageDialogueCompletionContract.Contains("Native spouse identity is authoritative"));
            return rows;
        }

        // The Verification Lab owns isolated storage for the production normalizer's
        // resolver audit. Never queue an action or contact a provider in this check.
        private static List<Dictionary<string, object>> VerifyDialogueMarriageNormalization()
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (string scenario in new[] { "exact_couple", "legacy_clan_only", "third_party", "conflicting_actor" })
            {
                var payload = MarriageActionFixture();
                payload["correlationId"] = "marriage-normalization-" + scenario;
                var arguments = TestDict("source", "hidden_action_planner", "actorClanId", "player_faction",
                    "actorKingdomId", "new_kingdom", "targetClanId", "istranentis");
                if (scenario != "legacy_clan_only") arguments["terms"] = TestDict(
                    "marriageHero1StringId", "main_hero", "marriageHero2StringId",
                    scenario == "third_party" ? "other_npc" : "zotyra");
                if (scenario == "conflicting_actor") arguments["actorHeroStringId"] = "hidden_action_planner";
                var candidate = NormalizeRouterActions(TestDict("command", "marriage_alliance", "arguments", arguments),
                    new HashSet<string> { "marriage_alliance" }, payload, ReadDictionary(payload, "hero"),
                    "Michael asks Zotyra to marry him now. Zotyra accepts.", "hidden_action_planner", "Marriage regression").Single();
                var record = NormalizeActionCommand(candidate, "test_campaign", out List<string> errors, payload,
                    "Michael asks Zotyra to marry him now. Zotyra accepts.");
                bool accepted = record != null && errors.Count == 0;
                bool passed = scenario == "exact_couple"
                    ? accepted && ReadString(record, "actorHeroStringId", "") == "main_hero"
                        && ReadString(record, "targetHeroStringId", "") == "zotyra"
                        && ReadString(record, "authorizationMode", "") == "dialogue_acceptance"
                        && ReadString(record, "termsHash", "").Length == 64
                    : !accepted && errors.Count > 0;
                rows.Add(TestDict("name", "production_marriage_" + scenario, "passed", passed,
                    "errors", errors, "detail", accepted ? "Exact couple normalized with consent receipt." : string.Join("; ", errors)));
            }
            return rows;
        }

        private static Dictionary<string, object> MarriageActionFixture()
        {
            var player = TestDict("heroStringId", "main_hero", "name", "Michael", "clanId", "player_faction", "kingdomId", "new_kingdom", "spouseId", "");
            var npc = TestDict("heroStringId", "zotyra", "name", "Zotyra", "clanId", "istranentis", "kingdomId", "empire_w", "spouseId", "");
            return TestDict("hero", npc, "playerHeroStringId", "main_hero", "speakerHeroStringId", "zotyra",
                "playerClanId", "player_faction", "playerKingdomId", "new_kingdom", "actionResolutionIndex", TestDict(
                    "version", 2, "heroes", new ArrayList { player, npc },
                    "settlements", new ArrayList { TestDict("settlementId", "town_EW2", "name", "Ortysia") },
                    "clans", new ArrayList { TestDict("clanId", "player_faction", "name", "Howarton"), TestDict("clanId", "istranentis", "name", "Istranentis") },
                    "kingdoms", new ArrayList { TestDict("kingdomId", "new_kingdom", "name", "Howarton"), TestDict("kingdomId", "empire_w", "name", "western Empire") }));
        }
    }
}
