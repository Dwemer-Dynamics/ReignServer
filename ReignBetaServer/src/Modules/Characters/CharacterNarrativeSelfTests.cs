using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunCharacterNarrativeTests()
        {
            var tests = new List<Dictionary<string, object>>();
            Action<string, bool, string> check = (name, passed, detail) => tests.Add(ProfileSelfTest("narrative_" + name, passed, detail, null));
            var library = LoadNarrativeLibrary();
            check("authored_library", library.Count >= 2200 && library.Count(x => ReadString(x, "category", "") == "hobby") >= 600
                && library.Where(x => ReadString(x, "category", "") == "hobby").Select(x => ReadString(x, "family", "")).Distinct().Count() >= 24,
                "At least 600 distinct hobbies across 24 families and 1600 other concepts are shipped.");
            var hero = new Dictionary<string, object> { ["heroStringId"] = "narrative_test_ruler", ["name"] = "Test ruler", ["isRuler"] = true, ["age"] = 46 };
            var blueprint = BuildNarrativeBlueprint(hero, "premade/test");
            var items = ReadDictionaryList(blueprint, "items");
            check("deterministic_identity", Json.Serialize(blueprint) == Json.Serialize(BuildNarrativeBlueprint(hero, "premade/test")), "Premade identity is independent of campaign and process randomness.");
            check("multiple_concerns", ValidateCharacterNarrative(blueprint, false).Count == 0
                && items.Count(x => ReadString(x, "category", "") == "hobby") >= 3 && items.Count(x => ReadString(x, "category", "") == "fear") >= 2,
                "Each character has multiple hobbies and fears, with a valid defining trio.");
            var histogram = new int[10];
            for (int i = 0; i < 100000; i++) histogram[NarrativeInfluence("distribution", i.ToString()) - 1]++;
            check("weighted_influence", Enumerable.Range(0, 10).All(i => Math.Abs(histogram[i] - NarrativeInfluenceWeights[i] * 1000) < 650),
                "100000 independent influence draws follow the specified nonuniform distribution within statistical tolerance.");
            var selections = new HashSet<string>(); bool optionalAbsent = false, allValid = true;
            for (int i = 0; i < 160; i++)
            {
                var sample = BuildNarrativeBlueprint(hero, "population/" + i);
                allValid &= ValidateCharacterNarrative(sample, false).Count == 0;
                optionalAbsent |= !ReadDictionaryList(sample, "items").Any(x => ReadString(x, "category", "") == "wound");
                selections.Add(string.Join("|", ReadStringList(sample, "definingIds")));
            }
            check("population_variety", allValid && optionalAbsent && selections.Count == 160,
                "Bounded population samples have distinct trios and do not force trauma on every person.");
            var tied = Enumerable.Range(0, 6).Select(i => new Dictionary<string, object> { ["id"] = "tie" + i,
                ["title"] = "concern " + i, ["category"] = "hobby", ["influence"] = 10 }).ToList();
            tied.Add(new Dictionary<string, object> { ["id"] = "excluded", ["title"] = "dry speech", ["category"] = "speechStyle", ["influence"] = 10 });
            var chosen = SelectDefiningNarrativeIds(tied, "ties", new List<string>());
            check("stable_ties_and_exclusions", chosen.Count == 3 && !chosen.Contains("excluded")
                && chosen.SequenceEqual(SelectDefiningNarrativeIds(tied.AsEnumerable().Reverse().ToList(), "ties", chosen)),
                "Tie selection survives input reordering; mannerisms cannot become defining motivations.");
            tied[0]["meaningKey"] = "family safety"; tied[1]["meaningKey"] = "family safety";
            var distinct = SelectDefiningNarrativeIds(tied, "ties", new List<string>());
            check("semantic_deduplication", !(distinct.Contains("tie0") && distinct.Contains("tie1")), "Equivalent concerns occupy only one defining slot across categories.");

            var philanthropy = library.Single(x => ReadString(x, "title", "") == "establish a school open to poor children");
            var integrityDream = library.Single(x => ReadString(x, "title", "") == "be remembered as a fair employer");
            var coldPersonality = new Dictionary<string, object> {
                ["traitPercentages"] = new Dictionary<string, object> { ["compassion"] = 15, ["ambition"] = 90, ["powerMotivation"] = 90 },
                ["courtVirtues"] = new Dictionary<string, object> { ["honor"] = 15 },
                ["foundationTraits"] = new Dictionary<string, object> { ["compassion"] = -2, ["honesty"] = -2 } };
            var warmPersonality = new Dictionary<string, object> {
                ["traitPercentages"] = new Dictionary<string, object> { ["compassion"] = 90 },
                ["courtVirtues"] = new Dictionary<string, object> { ["honor"] = 90 } };
            check("dream_personality_hard_exclusion", NarrativeConceptWeight(hero, philanthropy, coldPersonality) == 0
                && NarrativeConceptWeight(hero, integrityDream, coldPersonality) == 0,
                "Low compassion excludes genuine philanthropy; low existing honor excludes integrity-based dreams before selection.");
            var premadePersonality = NarrativeAuthoringPersonality(new Dictionary<string, object> { ["traits"] = coldPersonality });
            check("premade_dream_personality_evidence", NarrativePersonalityScore(premadePersonality, hero, "compassion") == 15
                && NarrativePersonalityScore(premadePersonality, hero, "honor") == 15,
                "Premade construction retains the existing trait percentages and Court honor rather than dropping them at the authoring boundary.");
            check("dream_personality_affinity", NarrativeConceptWeight(hero, philanthropy, warmPersonality) > NarrativeConceptWeight(hero, philanthropy)
                && NarrativeConceptWeight(hero, integrityDream, warmPersonality) > 0,
                "Compatible dreams become more likely with supporting personality rather than all characters receiving identical odds.");
            var caringDishonest = new Dictionary<string, object> {
                ["traitPercentages"] = new Dictionary<string, object> { ["compassion"] = 90 },
                ["courtVirtues"] = new Dictionary<string, object> { ["honor"] = 15 } };
            check("care_and_honor_are_distinct", NarrativeConceptWeight(hero, philanthropy, caringDishonest) > 0
                && NarrativeConceptWeight(hero, integrityDream, caringDishonest) == 0,
                "Compassion and honor are distinct existing traits; dishonesty alone does not erase genuine care.");
            bool coldPopulationCompatible = true;
            for (int i = 0; i < 32; i++)
            {
                var cold = BuildNarrativeBlueprint(hero, "cold-personality/" + i, coldPersonality);
                coldPopulationCompatible &= IncompatibleNarrativeDreamIds(cold, hero, coldPersonality).Count == 0
                    && ReadDictionaryList(cold, "items").Count(x => ReadString(x, "category", "") == "dream") >= 2;
            }
            check("dream_population_compatibility", coldPopulationCompatible,
                "Bounded cold/dishonorable fixtures retain multiple varied dreams and never select a forbidden philanthropic or integrity motive.");
            var boundaryPersonality = new Dictionary<string, object> {
                ["traitPercentages"] = new Dictionary<string, object> { ["compassion"] = 40 } };
            bool lowBoundaryExcluded = NarrativeConceptWeight(hero, philanthropy, boundaryPersonality) == 0;
            ReadDictionary(boundaryPersonality, "traitPercentages")["compassion"] = 41;
            check("dream_personality_band_boundary", lowBoundaryExcluded && NarrativeConceptWeight(hero, philanthropy, boundaryPersonality) > 0,
                "Eligibility honors the existing 0-40 low trait band and does not silently turn low compassion into neutral.");
            check("dream_policy_complete", library.Where(x => ReadString(x, "category", "") == "dream"
                && !ReadString(x, "family", "").Contains("/child_")).All(x => ReadDictionary(x, "personalityRule") != null),
                "Every adult dream has a reviewed explicit rule; new unclassified dreams fail closed when the library loads.");
            var charityDesire = library.Single(x => ReadString(x, "title", "") == "make a small charitable gift privately");
            var kindnessValue = library.Single(x => ReadString(x, "title", "") == "children deserve explanations rather than intimidation");
            var predatoryValue = library.Single(x => ReadString(x, "title", "") == "weak bargaining deserves an unfavorable bargain");
            check("desire_value_personality_eligibility", NarrativeConceptWeight(hero, charityDesire, coldPersonality) == 0
                && NarrativeConceptWeight(hero, kindnessValue, coldPersonality) == 0
                && NarrativeConceptWeight(hero, charityDesire, caringDishonest) > 0,
                "Charitable desires and humane values use actual compassion; low honor alone does not prohibit private kindness.");
            check("high_compassion_rejects_predatory_value", NarrativeConceptWeight(hero, predatoryValue, warmPersonality) == 0
                && NarrativeConceptWeight(hero, predatoryValue, coldPersonality) > 0,
                "A strongly humane or honorable character does not receive an explicitly predatory value as a defining conviction.");
            var upperBoundary = new Dictionary<string, object> { ["traitPercentages"] = new Dictionary<string, object> { ["compassion"] = 60 } };
            bool neutralPredationEligible = NarrativeConceptWeight(hero, predatoryValue, upperBoundary) > 0;
            ReadDictionary(upperBoundary, "traitPercentages")["compassion"] = 61;
            check("concern_upper_band_boundary", neutralPredationEligible && NarrativeConceptWeight(hero, predatoryValue, upperBoundary) == 0,
                "Opposing convictions respect the existing high band beginning at61, separately from the low-band exclusion.");
            check("concern_policy_complete", library.Where(x => new[] { "desire", "value" }.Contains(ReadString(x, "category", ""))
                && !ReadString(x, "family", "").Contains("/child_")).All(x => ReadDictionary(x, "personalityRule") != null),
                "Every adult desire and value has an explicit reviewed compatibility rule.");
            bool compatiblePopulations = true;
            foreach (var personality in new[] { coldPersonality, warmPersonality, caringDishonest })
                for (int i = 0; i < 16; i++)
                {
                    var candidate = BuildNarrativeBlueprint(hero, "concern-personality/" + i, personality);
                    compatiblePopulations &= IncompatibleNarrativeConcernIds(candidate, hero, personality).Count == 0
                        && new[] { "dream", "desire", "value" }.All(category => ReadDictionaryList(candidate, "items")
                            .Count(item => ReadString(item, "category", "") == category) >= 2);
                }
            check("concern_population_compatibility", compatiblePopulations,
                "Mixed moral personalities retain multiple compatible dreams, desires and values without extra provider calls.");
            var siblingMemory = library.Single(x => ReadString(x, "title", "") == "seeing an older sibling admit uncertainty");
            var siblingWound = library.Single(x => ReadString(x, "title", "") == "being compared unfavorably with a sibling");
            var unknownKin = new Dictionary<string, object> { ["heroStringId"] = "unknown_kin", ["age"] = 30 };
            var youngerKin = new Dictionary<string, object> { ["heroStringId"] = "known_kin", ["age"] = 30,
                ["siblingIds"] = new[] { "younger_sibling" }, ["olderSiblingIds"] = new string[0] };
            check("native_sibling_fact_eligibility", NarrativeConceptWeight(unknownKin, siblingMemory) == 0
                && NarrativeConceptWeight(unknownKin, siblingWound) == 0 && NarrativeConceptWeight(youngerKin, siblingWound) > 0
                && NarrativeConceptWeight(youngerKin, siblingMemory) == 0,
                "Unknown kin never creates a sibling; a known younger sibling does not authorize an older-sibling memory.");
            youngerKin["olderSiblingIds"] = new[] { "older_sibling" };
            check("known_older_sibling_allowed", NarrativeConceptWeight(youngerKin, siblingMemory) > 0,
                "Explicit known older-sibling evidence retains the corresponding background possibility.");
            var apprenticeAttachment = library.Single(x => ReadString(x, "title", "") == "a former apprentice whose progress they follow");
            check("adolescent_mentorship_boundary", NarrativeConceptWeight(new Dictionary<string, object> { ["age"] = 17 }, apprenticeAttachment) == 0
                && NarrativeConceptWeight(new Dictionary<string, object> { ["age"] = 21 }, apprenticeAttachment) > 0,
                "A teenager is not assigned years of prior apprentice supervision as an established personal attachment.");
            var carriedDesire = library.Single(x => ReadString(x, "title", "") == "be picked up when tired");
            check("young_child_care_age_boundary", NarrativeConceptWeight(new Dictionary<string, object> { ["age"] = 3 }, carriedDesire) > 0
                && NarrativeConceptWeight(new Dictionary<string, object> { ["age"] = 11 }, carriedDesire) == 0,
                "Routine carrying belongs to the youngest children, rather than infantilizing an older child.");
            var parentFear = library.Single(x => ReadString(x, "title", "") == "a parent dying before questions are answered");
            check("living_parent_fact_eligibility", NarrativeConceptWeight(unknownKin, parentFear) == 0
                && NarrativeConceptWeight(new Dictionary<string, object> { ["age"] = 30, ["livingParentIds"] = new[] { "parent" } }, parentFear) > 0,
                "A fear that presumes a living parent requires known living-parent evidence; it does not revive deceased parents.");
            var kinRoster = new List<Dictionary<string, object>> {
                new Dictionary<string, object> { ["heroStringId"] = "kin_subject", ["sourceFacts"] = new Dictionary<string, object> {
                    ["name"] = "Subject", ["age"] = 14, ["fatherId"] = "kin_father", ["motherId"] = "kin_mother" } },
                new Dictionary<string, object> { ["heroStringId"] = "kin_older", ["sourceFacts"] = new Dictionary<string, object> {
                    ["name"] = "Older", ["age"] = 18, ["fatherId"] = "kin_father" } },
                new Dictionary<string, object> { ["heroStringId"] = "kin_father", ["sourceFacts"] = new Dictionary<string, object> {
                    ["name"] = "Father", ["age"] = 40, ["isAlive"] = false } },
                new Dictionary<string, object> { ["heroStringId"] = "kin_mother", ["sourceFacts"] = new Dictionary<string, object> {
                    ["name"] = "Mother", ["age"] = 38, ["isAlive"] = true } } };
            string kinBefore = Json.Serialize(kinRoster);
            var enrichedKin = NarrativePremadeFacts(kinRoster[0], kinRoster);
            check("premade_native_kin_evidence", ReadStringList(enrichedKin, "siblingIds").SequenceEqual(new[] { "kin_older" })
                && ReadStringList(enrichedKin, "olderSiblingIds").SequenceEqual(new[] { "kin_older" })
                && ReadStringList(enrichedKin, "livingParentIds").SequenceEqual(new[] { "kin_mother" })
                && Json.Serialize(kinRoster) == kinBefore,
                "Known kin derives only from matching native parent IDs and ages; deceased parents are excluded and source facts remain unchanged.");

            var fixtureLife = new Dictionary<string, object> {
                ["publicSummary"] = "A ruler of the native culture whose public life is defined by the responsibilities of the office. The small routines observed by others do not disclose private motives or family confidences.",
                ["summary"] = string.Join(" ", Enumerable.Repeat("An ordinary childhood taught the ruler to observe before deciding and to take care over work shared with others.", 10)),
                ["origin"] = "An upbringing in an ordinary household within the native culture.",
                ["upbringing"] = "Small responsibilities offered practice in patience and careful observation.",
                ["reputation"] = "Known privately for taking time to consider an answer before speaking.",
                ["privateBackstory"] = "Remembers unfinished childhood work and the satisfaction of returning to it." };
            var fixtureVoice = new Dictionary<string, object> { ["nativeVoice"] = "reflective",
                ["speechStyle"] = "Asks a concrete question before offering a carefully considered answer.",
                ["socialMask"] = "Keeps attention on the business at hand while concealing uncertainty.",
                ["tells"] = new[] { "Turns an empty cup once before answering.", "Checks an unfinished stitch while thinking." } };
            Func<Dictionary<string, object>> itemResponse = () => items.Select((x, i) => new { slot = i.ToString(), value = (object)new[] {
                "Makes room for " + ReadString(x, "title", "") + " when circumstances permit.",
                "This concern offers a personal measure of continuity in a changing life.", "distinct aim " + i } }).ToDictionary(x => x.slot, x => x.value);
            int calls = 0;
            Dictionary<string, object> firstRequest = null;
            Func<Dictionary<string, object>, Dictionary<string, object>> provider = request => {
                calls++; firstRequest = request;
                return new Dictionary<string, object> { ["ok"] = true, ["content"] = Json.Serialize(new Dictionary<string, object> {
                    ["life"] = fixtureLife, ["voice"] = fixtureVoice, ["items"] = itemResponse(),
                    ["heroStringId"] = "untrusted_identity", ["definingIds"] = new[] { "untrusted" } }) };
            };
            var written = AuthorCharacterNarrative(hero, new Dictionary<string, object>(), "premade/test", provider);
            string requestText = Json.Serialize(firstRequest);
            check("single_call_complete_character", calls == 1 && ValidateCharacterNarrative(written, true).Count == 0
                && ReadString(written, "heroStringId", "") == ReadString(hero, "heroStringId", "")
                && items.Select(x => ReadString(x, "id", "")).SequenceEqual(ReadDictionaryList(written, "items").Select(x => ReadString(x, "id", "")))
                && !items.Any(x => requestText.Contains(ReadString(x, "id", ""))) && ReadInt(firstRequest, "maxTokens", 0) == 50000
                && !firstRequest.ContainsKey("minTokens") && !firstRequest.ContainsKey("min_tokens"),
                "The 50,000-token ceiling permits one complete compact response without a minimum token target; local IDs, identity, ratings and trio remain authoritative.");
            var transportBody = BuildChatRequestBody(DefaultSettings(), firstRequest,
                ReadDictionaryList(firstRequest, "messages"), "deepseek/deepseek-v4.1-flash");
            check("construction_transport_budget", ReadInt(transportBody, "max_tokens", 0) == 50000
                && ReadString(ReadDictionary(transportBody, "response_format"), "type", "") == "json_object"
                && !transportBody.ContainsKey("min_tokens"),
                "The actual chat adapter sends the authorized maximum and strict JSON without a hidden smaller cap or minimum output length.");
            var conciseLife = DeepCloneProfileDictionary(fixtureLife);
            conciseLife["summary"] = "Household duties taught the ruler to listen before deciding. Those habits now shape how the ruler handles public responsibilities and unfinished work.";
            conciseLife["origin"] = "An ordinary home.";
            var conciseVoice = DeepCloneProfileDictionary(fixtureVoice);
            conciseVoice["speechStyle"] = "Speaks plainly.";
            var conciseItems = itemResponse();
            conciseItems["0"] = new[] { "Mends worn tools.", "Comfort in work.", "care in repair" };
            int conciseCalls = 0;
            var conciseWritten = AuthorCharacterNarrative(hero, new Dictionary<string, object>(), "premade/test", request => {
                conciseCalls++;
                return new Dictionary<string, object> { ["ok"] = true, ["finishReason"] = "length",
                    ["content"] = Json.Serialize(new Dictionary<string, object> {
                        ["life"] = conciseLife, ["voice"] = conciseVoice, ["items"] = conciseItems }) };
            });
            check("length_guidance_accepts_complete_content", conciseCalls == 1 && NarrativeDocumentReady(conciseWritten)
                && Json.Serialize(ReadDictionary(conciseWritten, "life")) == Json.Serialize(conciseLife)
                && ReadString(ReadDictionaryList(conciseWritten, "items")[0], "description", "") == "Mends worn tools.",
                "Complete prose shorter than preferred ranges is retained without padding; a reported token limit does not reject an otherwise complete character.");
            var missingContent = DeepCloneProfileDictionary(conciseWritten);
            ReadDictionaryList(missingContent, "items")[0]["personalMeaning"] = " ";
            var brokenGrounding = DeepCloneProfileDictionary(conciseWritten);
            ReadDictionary(brokenGrounding, "life")["origin"] = "Born into clan_hidden_id.";
            var brokenConsistency = DeepCloneProfileDictionary(conciseWritten);
            brokenConsistency["definingIds"] = new[] { "invalid", "invalid", "invalid" };
            check("length_guidance_keeps_validity_checks", ValidateCharacterNarrative(missingContent, true).Count > 0
                && ValidateCharacterNarrative(brokenGrounding, true).Count > 0
                && ValidateCharacterNarrative(brokenConsistency, true).Count > 0,
                "Removing preferred-length gates retains required content, readable native references and valid distinct character concerns.");
            int repairCalls = 0; bool targetedRepair = false; Dictionary<string, object> repairCheckpoint = null;
            var repaired = AuthorCharacterNarrative(hero, new Dictionary<string, object>(), "premade/test", request => {
                repairCalls++;
                if (repairCalls == 1)
                {
                    var responseItems = itemResponse(); responseItems.Remove("0");
                    return new Dictionary<string, object> { ["ok"] = true, ["finishReason"] = "length", ["content"] = Json.Serialize(new Dictionary<string, object> {
                        ["life"] = fixtureLife, ["voice"] = fixtureVoice, ["items"] = responseItems }) };
                }
                string prompt = ReadString(ReadDictionaryList(request, "messages").Last(), "content", "");
                targetedRepair = prompt.Contains("\"requiredSlots\":[0]") && prompt.Contains("\"writeLife\":false")
                    && prompt.Contains("\"writeVoice\":false") && ReadInt(request, "maxTokens", 0) == 50000;
                return new Dictionary<string, object> { ["ok"] = true, ["content"] = Json.Serialize(new Dictionary<string, object> {
                    ["items"] = new Dictionary<string, object> { ["0"] = itemResponse()["0"] } }) };
            }, null, value => repairCheckpoint = DeepCloneProfileDictionary(value));
            check("targeted_missing_slot_repair", repairCalls == 2 && targetedRepair && NarrativeDocumentReady(repaired)
                && Json.Serialize(ReadDictionary(repaired, "life")) == Json.Serialize(fixtureLife)
                && NarrativeDocumentReady(repairCheckpoint), "A length-finished response that made progress repairs only missing slot zero; accepted life, voice and other concerns survive unchanged.");
            var interrupted = DeepCloneProfileDictionary(written); interrupted["status"] = "blueprint";
            ReadDictionaryList(interrupted, "items")[0]["description"] = "";
            int failedCalls = 0; bool providerFailed = false;
            try { AuthorCharacterNarrative(hero, new Dictionary<string, object>(), "premade/test", request => {
                failedCalls++; return new Dictionary<string, object> { ["ok"] = false, ["error"] = "fixture provider unavailable" };
            }, interrupted, value => repairCheckpoint = DeepCloneProfileDictionary(value)); }
            catch (InvalidOperationException) { providerFailed = true; }
            check("provider_failure_retains_partial", providerFailed && failedCalls == 1 && !NarrativeDocumentReady(repairCheckpoint)
                && Json.Serialize(ReadDictionary(repairCheckpoint, "life")) == Json.Serialize(fixtureLife),
                "Provider failure stops immediately and retains valid sections without publishing an incomplete character.");
            int limitedCalls = 0; bool limitReported = false;
            try { AuthorCharacterNarrative(hero, new Dictionary<string, object>(), "premade/test", request => {
                limitedCalls++;
                return new Dictionary<string, object> { ["ok"] = true, ["content"] = "", ["finishReason"] = "length" };
            }, interrupted, value => repairCheckpoint = DeepCloneProfileDictionary(value)); }
            catch (NarrativeOutputLimitException ex) { limitReported = ex.Message.Contains("50,000-token output limit"); }
            var limitedAttempt = ReadDictionaryList(repairCheckpoint, "authoringAttempts").Last();
            check("output_limit_stops_unchanged_retry", limitReported && limitedCalls == 1 && !NarrativeDocumentReady(repairCheckpoint)
                && ReadInt(limitedAttempt, "maxTokens", 0) == 50000 && ReadString(limitedAttempt, "finishReason", "") == "length"
                && Json.Serialize(ReadDictionary(repairCheckpoint, "life")) == Json.Serialize(fixtureLife),
                "Empty length-limited output retains completed sections, records its actual ceiling and stops after one call instead of repeating the same exhausted budget.");
            int configuredBudget = 0;
            AuthorCharacterNarrative(hero, new Dictionary<string, object>(), "premade/test", request => {
                configuredBudget = ReadInt(request, "maxTokens", 0); return provider(request);
            }, maxTokens: 90000);
            check("budget_hard_ceiling", configuredBudget == 50000,
                "A requested construction budget above 50,000 cannot escape the authorized ceiling.");
            var legacyReady = DeepCloneProfileDictionary(written); legacyReady.Remove("authoringMethod");
            int legacyCalls = 0;
            var legacyResumed = AuthorCharacterNarrative(hero, new Dictionary<string, object>(), "premade/test", request => {
                legacyCalls++; throw new InvalidOperationException("A completed old profile must not call the provider.");
            }, legacyReady);
            check("completed_legacy_profile_reused", legacyCalls == 0 && Json.Serialize(legacyResumed) == Json.Serialize(legacyReady),
                "Changing the writing protocol preserves complete compatible profiles without rewriting their content.");
            int previousCalls = calls;
            var resumed = AuthorCharacterNarrative(hero, new Dictionary<string, object>(), "premade/test", provider, written);
            check("resume_complete_stage", calls == previousCalls && Json.Serialize(resumed) == Json.Serialize(written), "A complete matching checkpoint makes no additional provider calls.");
            var partial = DeepCloneProfileDictionary(written); partial.Remove("voice");
            check("missing_section_fails", ValidateCharacterNarrative(partial, true).Count > 0, "Missing required voice data fails full-profile validation.");
            var readable = NarrativeReadableFacts(new Dictionary<string, object> { ["heroStringId"] = "lord_1_2", ["name"] = "Child",
                ["fatherId"] = "lord_1_1", ["fatherName"] = "Parent", ["clanId"] = "clan_empire_1", ["cultureId"] = "empire", ["age"] = 3 });
            check("readable_authoring_facts", !Json.Serialize(readable).Contains("lord_1_") && !Json.Serialize(readable).Contains("clan_empire_")
                && ReadString(readable, "fatherName", "") == "Parent" && ReadInt(readable, "age", 0) == 3,
                "Authoring retains readable native facts and resolved relatives without exposing internal identity keys as names.");
            var rawIdentity = DeepCloneProfileDictionary(written);
            ReadDictionary(rawIdentity, "life")["publicSummary"] = "Born into clan_empire_south_4 as the daughter of lord_1_30.";
            check("identifier_prose_rejected", ValidateCharacterNarrative(rawIdentity, true).Count > 0
                && !NarrativeProseClean("A damaged name \uFFFD here.") && NarrativeProseClean("Her father's letter; her mother's quiet company."),
                "Internal IDs and damaged text fail prose validation while normal names and punctuation remain valid.");

            var gameProse = DeepCloneProfileDictionary(written);
            ReadDictionary(gameProse, "life")["origin"] = "At the start of play, Rhagaea belongs to a tier-four noble clan with two children in its household.";
            string originalSelection = Json.Serialize(ReadStringList(gameProse, "definingIds"));
            string originalItems = Json.Serialize(ReadDictionaryList(gameProse, "items"));
            PolishNarrativeProse(gameProse);
            string oncePolished = Json.Serialize(gameProse);
            PolishNarrativeProse(gameProse);
            check("mechanical_prose_polish", ReadString(ReadDictionary(gameProse, "life"), "origin", "")
                    == "In the present day, Rhagaea belongs to a noble clan with two children in its household."
                && Json.Serialize(gameProse) == oncePolished && Json.Serialize(ReadDictionaryList(gameProse, "items")) == originalItems
                && Json.Serialize(ReadStringList(gameProse, "definingIds")) == originalSelection
                && PolishNarrativeText("By the start of play, he belonged to a tier-five Imperial clan.") == "In the present day, he belonged to an Imperial clan."
                && PolishNarrativeText("Four children watched the second tier of the wall.") == "Four children watched the second tier of the wall.",
                "Idempotent copy edits remove game framing while preserving names, quantities, concerns, ratings and selected identity.");
            previousCalls = calls;
            var polishedResume = AuthorCharacterNarrative(hero, new Dictionary<string, object>(), "premade/test", provider, gameProse);
            check("polish_keeps_authoring_checkpoint", calls == previousCalls && Json.Serialize(polishedResume) == Json.Serialize(gameProse),
                "Mechanical normalization retains the authoring fingerprint and complete stages without spending provider usage.");
            var almostSameLife = DeepCloneProfileDictionary(written); almostSameLife["heroStringId"] = "second";
            ReadDictionary(almostSameLife, "life")["summary"] = ReadString(fixtureLife, "summary", "") + " Still.";
            ReadDictionary(almostSameLife, "life")["origin"] = "At campaign start the household lived nearby.";
            var unrelatedLife = new Dictionary<string, object> { ["heroStringId"] = "third", ["life"] = new Dictionary<string, object> {
                ["summary"] = "Wind crossed the grass beside the water while horses grazed near distant tents. A traveler returned home carrying a small clay vessel and several letters." } };
            var quality = AuditNarrativeQuality(new List<Dictionary<string, object>> { written, almostSameLife, unrelatedLife });
            check("population_prose_diagnostics", ReadInt(quality, "nearDuplicateLifePairCount", 0) == 1
                && ReadInt(quality, "profilesWithGameWording", 0) == 1 && ReadDictionaryList(quality, "mostRepeatedConcernSentences").Count > 0
                && ReadInt(AuditNarrativeQuality(new List<Dictionary<string, object>>()), "nearDuplicateLifePairCount", -1) == 0,
                "Whole-population diagnostics catch nearly copied lives and repeated concern sentences while distinguishing unrelated prose and empty samples.");

            var target = ReadDictionaryList(written, "items").First(x => ReadString(x, "category", "") == "fear");
            var proposal = new Dictionary<string, object> { ["itemId"] = ReadString(target, "id", ""), ["influence"] = 8,
                ["description"] = "Now approaches this concern with deliberate calm and preparation.",
                ["personalMeaning"] = "Over time, learning to cope has changed the place this concern occupies.",
                ["supportingQuote"] = "Over these months I have come to approach this with a steadier mind.", ["quoteValidated"] = true, ["compatibilityValidated"] = true };
            var history = new List<Dictionary<string, object>>();
            history.Add(EvaluateNarrativeDevelopment(target, proposal, "meeting-1", 1, history, false));
            history.Add(EvaluateNarrativeDevelopment(target, proposal, "meeting-2", 15, history, false));
            check("insufficient_time", ReadString(EvaluateNarrativeDevelopment(target, proposal, "meeting-3", 30, history, false), "decision", "") == "pending",
                "Three encounters spanning only 29 days do not change a defining concern.");
            var accepted = EvaluateNarrativeDevelopment(target, proposal, "meeting-3", 31, history, false);
            check("sustained_development", ReadString(accepted, "decision", "") == "accepted", "Three separate encounters spanning 30 days can support lasting development.");
            var repeatHistory = history.Select(x => { var copy = DeepCloneProfileDictionary(x); copy["sessionId"] = "same"; return copy; }).ToList();
            check("same_encounter_not_growth", ReadString(EvaluateNarrativeDevelopment(target, proposal, "same", 100, repeatHistory, false), "decision", "") == "pending",
                "Repeated turns from one conversation are one encounter even if incorrectly supplied with later days.");
            var casual = DeepCloneProfileDictionary(proposal); casual["supportingQuote"] = "I enjoy carving little wooden figures.";
            check("casual_preference_stable", ReadString(EvaluateNarrativeDevelopment(target, casual, "meeting", 90, history, true), "decision", "") == "rejected",
                "A casual preference cannot trigger development even beside a major event.");
            var falseQuote = NormalizeNarrativeDevelopmentProposal(proposal, "The player said those words, not this reply.");
            check("quote_source_required", !ReadBool(falseQuote, "quoteValidated", true), "Only an exact statement in this NPC's visible reply can support development.");
            check("major_event_gate", ReadString(EvaluateNarrativeDevelopment(target, proposal, "new", 100, new List<Dictionary<string, object>>(), true), "decision", "") == "accepted",
                "A separately verified relevant major event plus lasting response permits development.");
            var stack = new Dictionary<string, object> { ["narrative"] = written, ["dynamicCharacteristics"] = new Dictionary<string, object> {
                ["active"] = new[] { new Dictionary<string, object> { ["last_ts"] = 10L, ["characteristic_id"] = "change", ["payload_json"] = Json.Serialize(new Dictionary<string, object> { ["narrativeDevelopment"] = accepted }) } } } };
            ApplyNarrativePromptProjection(stack);
            check("values_are_proportionate", ReadStringList(ReadDictionary(stack, "motivations"), "linesTheyWillNotCross").Count == 0
                && ReadStringList(ReadDictionary(stack, "motivations"), "values").Count >= 2,
                "Rated values remain meaningful priorities without automatically becoming absolute prohibitions.");
            check("effective_projection", ReadStringList(ReadDictionary(stack, "motivations"), "fears").Any(x => x.Contains("deliberate calm"))
                && ReadString(ResolveEffectiveNarrative(stack), "schema", "") == NarrativeSchema,
                "Accepted development replaces the old concern in legacy motivation projections and the shared effective snapshot.");
            string prompt = BuildCoreSkillAwarenessPrompt(hero, stack);
            string directFoundation = BuildStableCharacterPrompt("narrative_test_ruler", "Test ruler", hero, stack);
            check("projected_list_prose_reaches_prompt", !directFoundation.Contains("System.Collections")
                && directFoundation.Contains(ReadString(ReadDictionaryList(written, "items").First(x => ReadString(x, "category", "") == "formative"), "description", "")),
                "Fresh in-memory projections render real formative prose without relying on a save/reload to normalize list types.");
            check("strong_but_contextual_prompt", prompt.Contains("THREE DEFINING PERSONAL INTERESTS") && prompt.Contains("actual question")
                && prompt.Contains("PRIVATE") && prompt.Contains("Interest is not expertise"),
                "The same core-skill prompt seam carries all three interests, duty/context guards, privacy and native capability boundaries.");
            check("intensity_descriptions", Enumerable.Range(1, 10).Select(x => NarrativeInfluenceDescription("hobby", x)).Distinct().Count() == 10
                && NarrativeInfluenceDescription("fear", 10).Contains("proof"), "Every intensity has distinct wording and category-specific behavioral limits.");
            string freeTime = BuildRelevantInterestsPrompt(stack, "How do you spend your free time?");
            string fears = BuildRelevantInterestsPrompt(stack, "What frightens you?");
            check("ordinary_interest_questions", ReadDictionaryList(written, "items").Where(x => ReadString(x, "category", "") == "hobby")
                .Any(x => freeTime.Contains(ReadString(x, "title", ""))) && ReadDictionaryList(written, "items").Where(x => ReadString(x, "category", "") == "fear")
                .Any(x => fears.Contains(ReadString(x, "title", ""))),
                "Ordinary questions about free time or fear retrieve real stored concerns even without exact title keywords.");
            var unrankedLegacy = DeepCloneProfileDictionary(stack);
            unrankedLegacy["motivations"] = new Dictionary<string, object> { ["fears"] = new System.Collections.ArrayList { "obsolete_unranked_fear_marker" } };
            string rankedFoundation = BuildStableCharacterPrompt("narrative_test_ruler", "Test ruler", hero, unrankedLegacy);
            unrankedLegacy.Remove("narrative");
            check("ranked_motivations_replace_legacy_prompt", !rankedFoundation.Contains("obsolete_unranked_fear_marker")
                && rankedFoundation.Contains("CURRENT PERSONAL IMPORTANCE")
                && BuildStableCharacterPrompt("narrative_test_ruler", "Test ruler", hero, unrankedLegacy).Contains("obsolete_unranked_fear_marker"),
                "A v5 foundation uses its rated defining concerns without repeating every legacy motivation at equal prominence; old campaigns keep their existing motivation prompt.");
            var effective = ResolveEffectiveNarrative(stack);
            var unchanged = MergeEditedNarrative(written, effective, effective);
            check("editor_preserves_development", ReadDictionaryList(unchanged, "items").Select(NarrativeItemFingerprint)
                .SequenceEqual(ReadDictionaryList(written, "items").Select(NarrativeItemFingerprint)),
                "Opening the current effective concerns and saving an unrelated field does not erase their development history.");
            var edited = DeepCloneProfileDictionary(effective);
            ReadDictionaryList(edited, "items").First(x => ReadString(x, "id", "") == ReadString(target, "id", ""))["influence"] = 2;
            var merged = MergeEditedNarrative(written, effective, edited);
            var guardedAccepted = DeepCloneProfileDictionary(accepted);
            guardedAccepted["baseItemFingerprint"] = NarrativeItemFingerprint(target);
            var stale = new Dictionary<string, object> { ["narrative"] = merged, ["dynamicCharacteristics"] = new Dictionary<string, object> {
                ["active"] = new[] { new Dictionary<string, object> { ["category"] = "narrative_development", ["payload_json"] = Json.Serialize(new Dictionary<string, object> { ["narrativeDevelopment"] = guardedAccepted }) } } } };
            check("editor_overrides_old_development", ReadInt(ReadDictionaryList(ResolveEffectiveNarrative(stale), "items")
                .First(x => ReadString(x, "id", "") == ReadString(target, "id", "")), "influence", 0) == 2,
                "An explicit editor correction wins over an accepted change based on an older version of that concern.");
            var fractional = DeepCloneProfileDictionary(written);
            ReadDictionaryList(fractional, "items")[0]["influence"] = 4.5;
            check("integer_strength_required", ValidateCharacterNarrative(fractional, true).Count > 0, "Fractional ratings fail validation rather than silently rounding.");
            var unrelated = DeepCloneProfileDictionary(proposal);
            unrelated["description"] = "Enjoys grooming horses and polishing riding equipment in the stable.";
            unrelated["personalMeaning"] = "Quiet contact with animals offers pleasure and companionship.";
            check("coherent_encounters_required", ReadString(EvaluateNarrativeDevelopment(target, unrelated, "new", 90, history, false), "decision", "") == "pending",
                "Same direction and elapsed time cannot combine unrelated proposed changes into development.");
            var future = history.Select(x => { var copy = DeepCloneProfileDictionary(x); copy["worldDay"] = 120d; return copy; }).ToList();
            check("future_evidence_ignored", ReadString(EvaluateNarrativeDevelopment(target, proposal, "new", 90, future, false), "decision", "") == "pending",
                "Observations from a later campaign day cannot authorize a change on an earlier timeline.");
            check("narrative_temporal_snapshot", !IsSaveSyncFingerprintExcluded("characters/hero/narrative.json")
                && !IsSaveSyncFingerprintExcluded("characters/hero/dynamic_characteristics.json")
                && ShouldIncludeSaveSyncSnapshotFile(System.IO.Path.GetTempPath(), System.IO.Path.Combine(System.IO.Path.GetTempPath(), "characters", "hero", "narrative.json")),
                "Narrative identity and its effective development are temporal save state, included in fingerprints and native-save snapshots.");
            var lowercaseSkills = new Dictionary<string, object> { ["age"] = 30, ["skills"] = new Dictionary<string, object> { ["riding"] = 180 } };
            check("native_skill_opportunity", NarrativeConceptWeight(lowercaseSkills, new Dictionary<string, object> { ["category"] = "hobby", ["family"] = "horses_livestock" }) > 1,
                "Native lowercase skill keys modestly weight relevant hobby opportunities.");
            var child = BuildNarrativeBlueprint(new Dictionary<string, object> { ["heroStringId"] = "toddler", ["age"] = 3 }, "child/test");
            check("age_appropriate_concepts", ValidateCharacterNarrative(child, false).Count == 0
                && ReadDictionaryList(child, "items").All(x => ReadString(x, "family", "").Contains("/child_")),
                "Young children draw from authored play, simple wants, attachment and learning concepts, never adult careers or political ambitions.");
            check("moral_range", library.Count(x => ReadString(x, "family", "").EndsWith("/hard_edges") || ReadString(x, "family", "").EndsWith("/status")) >= 150,
                "The library also supplies selfish, status-seeking, traditional and hard-edged motives; character interpretation preserves the native personality.");
            string previewRoot = System.IO.Path.Combine(VerificationDir, "character-narrative");
            System.IO.Directory.CreateDirectory(previewRoot);
            System.IO.File.WriteAllText(System.IO.Path.Combine(previewRoot, "control-center.html"), ControlCenterHtml());
            WriteJsonObject(System.IO.Path.Combine(previewRoot, "fixture.json"), written);
            check("editor_preview_fixture", true, "Provider-free Control Center HTML and narrative fixture: " + previewRoot);
            var familyConcern = new Dictionary<string, object> { ["title"] = "outliving close family", ["description"] = "Fears losing a loved parent." };
            var death = new Dictionary<string, object> { ["source"] = "native", ["is_complete"] = 1, ["phase"] = "completed", ["event_type"] = "hero_death", ["summary"] = "The parent died." };
            check("relevant_native_family_event", NarrativeMajorEventRelevant(familyConcern, death, "child", new List<string> { "parent" }, new List<string> { "parent" }, "Since my parent died, I no longer take time with family for granted.")
                && !NarrativeMajorEventRelevant(familyConcern, death, "stranger", new List<string>(), new List<string> { "parent" }, "Since the parent died, I no longer take time for granted."),
                "A recorded immediate-family death can be personally relevant; a stranger's unrelated event cannot.");
            var catConcern = new Dictionary<string, object> { ["title"] = "watching kittens", ["description"] = "Enjoys their playful movements." };
            check("major_event_not_universal_growth", !NarrativeMajorEventRelevant(catConcern, death, "parent", new List<string>(), new List<string> { "parent" }, "Since the death I have come to care more about kittens."),
                "The presence of a major event alone cannot change an unrelated hobby.");
            RunNarrativePersistenceTests(written, check);
            var catalog = ReadShippedNarrativeCatalog();
            var catalogProfiles = ReadDictionaryList(catalog, "profiles");
            var unspecified = catalogProfiles.FirstOrDefault(x => ReadString(x, "heroStringId", "") == "lord_6_6");
            if (unspecified != null)
            {
                var facts = NarrativePremadeFacts(unspecified, catalogProfiles);
                check("missing_native_age_is_unknown", !facts.ContainsKey("age") && ReadString(facts, "clanName", "") == "Tigrit"
                    && ReadDictionaryList(BuildNarrativeBlueprint(facts, "premade/lord_6_6"), "items").All(x => !ReadString(x, "family", "").Contains("/child_")),
                    "An omitted native age does not turn Suran into a newborn; verified native clan names reach authoring.");
            }
            if (ReadString(catalog, "packVersion", "") == NarrativePack)
            {
                var audit = AuditNarrativeCatalog(catalog);
                check("entire_shipped_roster", ReadInt(audit, "validCount", 0) == ReadInt(audit, "rosterCount", -1)
                    && ReadInt(audit, "distinctLifeSummaries", 0) == ReadInt(audit, "rosterCount", -1), "Every shipped v5 profile has a complete unique life account.");
                check("shipped_dream_personality_compatibility", ReadInt(audit, "incompatibleDreamCount", -1) == 0,
                    "The complete shipped roster's dreams respect its unchanged native personality documents.");
                check("shipped_concern_personality_compatibility", ReadInt(audit, "incompatibleConcernCount", -1) == 0,
                    "The complete shipped roster's dreams, desires and values respect its unchanged personality documents.");
                check("shipped_narrative_fact_compatibility", ReadInt(audit, "incompatibleFactCount", -1) == 0,
                    "The complete shipped roster does not invent required siblings, living parents or implausible adolescent mentoring histories.");
                var preservedProfiles = ReadDictionaryList(ReadJsonObject(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "ProfileLibrary", "profile_catalog_v4.json")), "profiles");
                var preservedById = preservedProfiles.ToDictionary(x => ReadString(x, "heroStringId", ""), StringComparer.Ordinal);
                bool identitiesPreserved = preservedProfiles.Count > 0 && catalogProfiles.Count == preservedProfiles.Count
                    && catalogProfiles.All(profile => {
                        Dictionary<string, object> original;
                        if (!preservedById.TryGetValue(ReadString(profile, "heroStringId", ""), out original)) return false;
                        return new[] { "sourceFacts", "traits", "mbtiProfile" }.All(part =>
                            Json.Serialize(ReadDictionary(profile, part)) == Json.Serialize(ReadDictionary(original, part)))
                            && ReadString(profile, "source", "") == ReadString(original, "source", "");
                    });
                check("native_identity_and_personality_preserved", identitiesPreserved,
                    "The complete expanded roster retains every original identity, native biography, family, age, native skill, trait and MBTI document exactly.");
                WriteJsonObject(System.IO.Path.Combine(previewRoot, "roster-audit.json"), audit);
            }
            return tests;
        }
    }
}
