using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunEncounteredResidentTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, Func<object> run)
            {
                try { results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = true,
                    ["caseId"] = "resident_" + id, ["summary"] = id, ["data"] = run() }); }
                catch (Exception ex) { results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = false,
                    ["caseId"] = "resident_" + id, ["summary"] = ex.Message }); }
            }
            void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
            Check("recruitment_visible_consent_and_price", () => {
                foreach (string refused in new[] { "I will join if you pay me.", "Perhaps I will join you.", "I would join you.",
                    "I won't join you.", "I cannot join you.", "Let me think about it.", "I agree, provided you free my brother." })
                    Require(!ResidentHasUnconditionalConsent(refused), "Conditional/refused agreement passed: " + refused);
                Require(ResidentHasUnconditionalConsent("I will join you as your companion for 100 denars."), "Accepted paid recruitment was rejected.");
                Require(ResidentHasUnconditionalConsent("I agree. I'll join you freely."), "Accepted free recruitment was rejected.");
                Require(ResidentHasUnconditionalConsent("*She folds her hands as if to steady them.* We are agreed. I join your clan now, freely. I will not pretend I am a trained steward. I cannot cook fine feasts."), "Unrelated description erased explicit current acceptance.");
                foreach (string refused in new[] { "I agree about the pay. I will not join you.", "I join you only if my husband agrees.",
                    "I join your clan. No, I changed my mind.", "I agree. I cannot leave my family.", "I accept. I won't.", "*I join your clan now.*" })
                    Require(!ResidentHasUnconditionalConsent(refused), "Contradicted or narrated consent passed: " + refused);
                string history = "npc: I will join you.\nplayer: Still ready?\nnpc: I refuse.";
                Require(!ResidentHasUnconditionalConsent(LatestResidentReply(history)), "Old consent overrode the current refusal.");
                var payload = new Dictionary<string, object> { ["heroStringId"] = "resident", ["hero"] = new Dictionary<string, object> {
                    ["heroStringId"] = "resident", ["encounteredResident"] = new Dictionary<string, object> { ["schema"] = "reign-encountered-resident-v1" } } };
                var terms = new Dictionary<string, object> { ["agreementKind"] = "permanent", ["agreedGold"] = 100 };
                var action = new Dictionary<string, object> { ["actorHeroId"] = "wrong-third-person" };
                var errors = new List<string>();
                BindAndValidateResidentAgreement(action, terms, payload, "player: Join as my companion for 100 denars.\nnpc: I will join you as your companion for 100 denars.", "recruit_encountered_resident", errors);
                Require(errors.Count == 0 && ReadBool(terms, "consentConfirmed", false)
                    && ReadString(action, "actorHeroStringId", "") == "resident", "Accepted agreement was not bound to its actual speaker.");
                terms["agreedGold"] = 0; errors.Clear();
                BindAndValidateResidentAgreement(action, terms, payload, "player: Join for 100 denars.\nnpc: I accept the 100 denars. I will join you.", "recruit_encountered_resident", errors);
                Require(errors.Count > 0, "A paid agreement silently became free.");
                var profile = ReadDictionary(payload, "hero");
                var eventPayload = new Dictionary<string, object> { ["speakerHeroStringId"] = "resident" };
                var boundPayload = BindResidentActionProfile(eventPayload, profile);
                terms["agreedGold"] = 100; errors.Clear();
                BindAndValidateResidentAgreement(action, terms, boundPayload,
                    "player: Join as my companion for 100 denars.\nnpc: We are agreed. I join your clan now. I will not pretend I am a steward.",
                    "recruit_encountered_resident", errors);
                Require(errors.Count == 0 && ReadBool(terms, "consentConfirmed", false)
                    && !eventPayload.ContainsKey("hero"), "Homes action lost its resolved resident profile or mutated the request.");
                eventPayload["speakerHeroStringId"] = "other-person";
                Require(!BindResidentActionProfile(eventPayload, profile).ContainsKey("hero"), "Another participant acquired the resident's identity.");
                return true;
            });
            Check("varied_names_seven_cultures", () => {
                var samples = new Dictionary<string, object>();
                foreach (string culture in new[] { "empire", "vlandia", "sturgia", "battania", "aserai", "khuzait", "nord" })
                {
                    var names = new List<string>(); var rng = new Random(1907);
                    for (int i = 0; i < 24; i++)
                    {
                        string next = EncounteredResidentRules.ChooseName(culture, i % 2 == 0, Array.Empty<string>(), names, names, rng.Next);
                        Require(!names.Contains(next), "A full name was duplicated.");
                        Require(!names.AsEnumerable().Reverse().Take(12).Any(old =>
                            EncounteredResidentRules.NamesTooSimilar(old.Split(' ')[0], next.Split(' ')[0])), "Recent first names must be distinguishable.");
                        names.Add(next);
                    }
                    Require(names.Select(n => n.Split(' ').Last()).Distinct().Count() >= 10, "Surname selection lacks variety.");
                    samples[culture] = names.Take(6).ToArray();
                }
                return samples;
            });
            Check("near_name_rejection", () => {
                Require(EncounteredResidentRules.NamesTooSimilar("Marius", "Marios"), "Vowel variants must be rejected.");
                Require(EncounteredResidentRules.NamesTooSimilar("David", "Dawid"), "Phonetic variants must be rejected.");
                Require(!EncounteredResidentRules.NamesTooSimilar("Beatrice", "Hilda"), "Distinct names must remain eligible.");
                return true;
            });
            Check("home_directory_location_lifecycle", () => {
                bool Available(string current, string requested, bool met = true, bool alive = true, bool prisoner = false, bool party = false, bool adult = true)
                    => EncounteredResidentRules.IsAvailable("town-a", current, requested, met, alive, prisoner, party, adult);
                Require(Available("town-a", "town-a"), "Known local resident missing.");
                Require(!Available("town-b", "town-a") && !Available("town-a", "town-b"), "Remote/home scopes leaked.");
                Require(!Available("town-a", "town-a", met:false) && !Available("town-a", "town-a", alive:false)
                    && !Available("town-a", "town-a", prisoner:true) && !Available("town-a", "town-a", party:true)
                    && !Available("town-a", "town-a", adult:false), "An unavailable identity entered Homes.");
                return true;
            });
            Check("job_mechanics", () => {
                Require(EncounteredResidentRules.SkillRole("Weaponsmith", "smith_empire") == "smith", "Smith mechanic routing.");
                Require(EncounteredResidentRules.SkillRole("Tavernkeeper", "keeper") == "trader", "Tavern trade mechanic routing.");
                Require(EncounteredResidentRules.SkillRole("Musician", "musician") == "performer", "Performer mechanic routing.");
                Require(EncounteredResidentRules.SkillRole("ShipWright", "shipwright") == "engineer", "Shipwright mechanic routing.");
                Require(EncounteredResidentRules.SkillRole("Guard", "guard") == "military", "Guard troop baseline/release routing.");
                return true;
            });
            Check("local_authority_without_membership", () => {
                var observer = new Dictionary<string, object> { ["clanId"] = "", ["kingdomId"] = "",
                    ["encounteredResident"] = new Dictionary<string, object> { ["schema"] = "reign-encountered-resident-v1",
                        ["homeRulerId"] = "ruler", ["homeOwnerClanLeaderId"] = "owner" } };
                Require(IsResidentLocalAuthority(observer, "ruler") && IsResidentLocalAuthority(observer, "owner"), "Local authorities must qualify without a clan.");
                Require(!IsResidentLocalAuthority(observer, "foreign") && !IsResidentLocalAuthority(observer, ""), "Foreign/missing authorities must use normal recognition.");
                Require(EncounteredResidentRules.LocalAuthorityRecognitionProbability == .90, "User's recognition probability changed.");
                observer["heroStringId"] = "resident";
                var home = ReadDictionary(observer, "encounteredResident");
                home["homeSettlementId"] = "town-a"; home["homeKingdomId"] = "realm";
                var subject = new Dictionary<string, object> { ["heroStringId"] = "ruler", ["kingdomId"] = "realm", ["isRuler"] = true };
                var settlement = new Dictionary<string, object> { ["id"] = "town-a", ["kingdomId"] = "realm" };
                var native = new Dictionary<string, object> { ["authoritative"] = true, ["subject"] = subject,
                    ["observer"] = new Dictionary<string, object> { ["heroStringId"] = "resident", ["kingdomId"] = "", ["clanId"] = "" },
                    ["settlement"] = settlement };
                var context = new Dictionary<string, object> { ["observer"] = observer, ["nativePoliticalContext"] = native };
                var known = new Dictionary<string, object> { ["canonicalNameAllowed"] = true };
                var authority = BuildCurrentPoliticalAuthorityView(known, context);
                Require(ReadBool(authority, "subjectIsObserverSovereign", false) && !ReadBool(authority, "sameKingdom", true)
                    && !ReadBool(authority, "sameClan", true), "Recognized hometown ruler was classified as foreign or granted membership.");
                Require(!ReadBool(BuildCurrentPoliticalAuthorityView(new Dictionary<string, object>(), context), "publicOfficeKnown", true),
                    "Resident home must not bypass a failed recognition roll.");
                settlement["kingdomId"] = "conqueror";
                Require(!ReadBool(BuildCurrentPoliticalAuthorityView(known, context), "subjectIsObserverSovereign", true), "Stale home metadata survived conquest.");
                settlement["kingdomId"] = "realm"; subject["heroStringId"] = "successor";
                Require(!ReadBool(BuildCurrentPoliticalAuthorityView(known, context), "subjectIsObserverSovereign", true), "A mismatched ruler acquired hometown authority.");
                subject["heroStringId"] = "ruler"; observer["heroStringId"] = "other-resident";
                Require(!ReadBool(BuildCurrentPoliticalAuthorityView(known, context), "subjectIsObserverSovereign", true), "Another observer's home leaked civic authority.");
                return true;
            });
            Check("current_outfit_overrides_every_clothing_route", () => {
                var resident = new Dictionary<string, object> { ["schema"] = "reign-encountered-resident-v1", ["initialPortraitCompleted"] = false };
                var snapshot = new Dictionary<string, object> { ["schema"] = "reign-native-portrait-snapshot-v1", ["campaignId"] = "resident-test",
                    ["heroStringId"] = "person", ["encounteredResident"] = resident };
                var payload = new Dictionary<string, object> { ["campaignId"] = "resident-test", ["heroStringId"] = "person",
                    ["promptPurpose"] = "portrait", ["gender"] = "female", ["ageYears"] = 30, ["cultureId"] = "empire",
                    ["physicalConfidence"] = new Dictionary<string, object> { ["score"] = 100 }, ["nativeCharacterSnapshot"] = snapshot };
                Require(PreserveResidentClothing(payload) && !UsesAdultPortraitClothingEdit(payload), "Initial outfit must bypass the second pass at maximum confidence.");
                string prompt = BuildPortraitPrompt(payload, "change clothes", true);
                Require(prompt.Contains("Retain exactly the clothes") && prompt.Contains("Retain any encountered armor")
                    && !prompt.Contains("Their clan is tier") && !prompt.Contains("No extreme close-up") && !prompt.Contains("change clothes"), "Ordinary identity, exclusion or caller clothing layers leaked.");
                snapshot["heroStringId"] = "wrong";
                Require(!PreserveResidentClothing(payload), "A mismatched snapshot may not set the clothing policy.");
                snapshot["heroStringId"] = "person"; resident["initialPortraitCompleted"] = true;
                Require(PreserveResidentClothing(payload) && !UsesAdultPortraitClothingEdit(payload), "Regeneration must still preserve the resident's current equipment.");
                resident["initialPortraitCompleted"] = false; payload["promptPurpose"] = "castle_scene";
                Require(!PreserveResidentClothing(payload), "Scenery must not enter the portrait exception.");
                return true;
            });
            Check("commoner_prompt_roles_and_persistent_personality", () => {
                var resident = new Dictionary<string, object> { ["schema"] = "reign-encountered-resident-v1",
                    ["occupation"] = "TavernWench", ["homeSettlementName"] = "Zeonica" };
                var profile = new Dictionary<string, object> { ["heroStringId"] = "commoner-fixture", ["name"] = "Sabina Doria",
                    ["occupation"] = "Wanderer", ["encounteredResident"] = resident };
                var traits = new Dictionary<string, object> { ["basePersonalitySummary"] = "Patient with children, fiercely proud of her work, wary of promises." };
                var characteristics = new Dictionary<string, object> { ["traits"] = traits,
                    ["voice"] = new Dictionary<string, object> { ["speechStyle"] = "Dry wit and short, precise answers." } };
                string template = NormalizePromptSegment(LoadPromptTemplate("commoner_prompt.txt"));
                Require(PromptFileNames.Contains("commoner_prompt.txt") && PromptMetadataByName().ContainsKey("commoner_prompt.txt")
                    && DefaultPromptTemplates()["commoner_prompt.txt"].Length > 1000, "Commoner prompt is missing from editable inventory or embedded defaults.");
                string first = BuildStableCharacterPrompt("commoner-fixture", "Sabina Doria", profile, characteristics);
                Require(first.Contains(template) && first.Contains("Tavern work") && first.Contains("Patient with children")
                    && first.Contains("Dry wit"), "Role prompting replaced personality or missed native tavern occupation.");
                string legacy = BuildCharacterFoundationText("verify_commoner", "commoner-fixture", "Sabina Doria", profile, characteristics, new Dictionary<string, object>());
                string construction = BuildCharacterConstructionPrompt("verify_commoner", "commoner-fixture", profile, traits);
                Require(legacy.Contains(template) && construction.Contains(template) && NormalizePromptSegment(construction).Contains(NormalizePromptSegment(LoadPromptTemplate("world_tone.txt"))), "Legacy or first-contact construction omitted commoner/world layers.");
                resident["recruited"] = true;
                string recruited = BuildStableCharacterPrompt("commoner-fixture", "Sabina Doria", profile, characteristics);
                Require(recruited.Contains("not a current shift") && recruited.Contains("Patient with children")
                    && recruited.Contains("Tavern work") && recruited.Contains("Zeonica") && recruited.Contains("TavernWench")
                    && PromptHash(first) != PromptHash(recruited), "Recruitment did not invalidate the cached role or preserve personality.");
                resident["recruited"] = false; resident["occupation"] = "Guard"; resident["military"] = true;
                Require(BuildCommonerPromptBlock(profile).Contains("cannot grant their own release"), "Duty constraints are absent.");
                resident["releasedFromDuty"] = true;
                Require(BuildCommonerPromptBlock(profile).Contains("released from the former duty"), "A released guard retained a stale prompt restriction.");
                resident["military"] = false; resident["occupation"] = "Villager";
                Require(BuildCommonerPromptBlock(profile).Contains("Village life"), "Villager occupational layer is absent.");
                profile["isLord"] = true;
                Require(BuildCommonerPromptBlock(profile) == "", "Ennoblement retained the commoner role.");
                profile["isLord"] = false; profile["isNotable"] = true;
                Require(BuildCommonerPromptBlock(profile) == "", "Existing notable role was overridden.");
                profile.Remove("encounteredResident"); profile["isNotable"] = false;
                Require(BuildCommonerPromptBlock(profile) == "", "An unrelated wanderer was silently treated as an encountered resident.");
                return new { roleVariants = 4, construction = true, editable = true, personalityPreserved = true };
            });
            Check("household_variety_and_chronology", () => {
                var statuses = new HashSet<string>(); var childCounts = new HashSet<int>();
                var partners = new HashSet<string>(); int singleParents = 0;
                for (int i = 0; i < 140; i++)
                {
                    var profile = new Dictionary<string, object> { ["name"] = "Sabina Doria", ["cultureId"] = "empire",
                        ["age"] = 18 + i % 70, ["isFemale"] = i % 2 == 0,
                        ["encounteredResident"] = new Dictionary<string, object> { ["schema"] = "reign-encountered-resident-v1", ["residentId"] = "household-fixture-" + i } };
                    var household = GenerateCommonerHousehold(profile);
                    Require(Json.Serialize(household) == Json.Serialize(GenerateCommonerHousehold(profile)), "A retry rerolled household canon.");
                    string status = ReadString(household, "maritalStatus", ""); statuses.Add(status);
                    var children = ReadDictionaryList(household, "children"); childCounts.Add(children.Count);
                    if (status == "single" && children.Count > 0) singleParents++;
                    Require(children.Count <= 4 && (ReadInt(profile, "age", 0) > 18 || children.Count == 0), "Invalid household size/young parent.");
                    var names = children.Select(x => ReadString(x, "name", "")).ToList();
                    var partner = ReadDictionary(household, "partner");
                    if (partner != null && partner.Count > 0) { names.Add(ReadString(partner, "name", "")); partners.Add(ReadString(partner, "name", "")); }
                    Require(names.Distinct().Count() == names.Count && !names.Contains("Sabina Doria"), "Household names collided.");
                    foreach (var child in children)
                    {
                        int parentAgeAtBirth = ReadInt(profile, "age", 0) - ReadInt(child, "ageAtFirstContact", 0);
                        Require(parentAgeAtBirth >= 18 && (!ReadBool(profile, "isFemale", false) || parentAgeAtBirth <= 45), "Implausible birth chronology.");
                    }
                }
                Require(statuses.Count == 4 && childCounts.Count == 5 && partners.Count > 60 && singleParents > 0, "Household sample lacks marital/parenthood/name variety.");
                return new { samples = 140, maritalStatuses = statuses.ToArray(), childCounts = childCounts.ToArray(), distinctPartners = partners.Count, singleParents };
            });
            Check("household_native_refresh_recruitment_and_save_roundtrip", () => {
                var profile = new Dictionary<string, object> { ["name"] = "Sabina Doria", ["age"] = 31, ["cultureId"] = "empire",
                    ["spouseId"] = "", ["childrenIds"] = new string[0], ["childrenCount"] = 0,
                    ["nativeEncyclopediaText"] = "They have no known clan or family.",
                    ["encounteredResident"] = new Dictionary<string, object> { ["schema"] = "reign-encountered-resident-v1", ["residentId"] = "household-persistence" } };
                PreserveCommonerHousehold(profile, new Dictionary<string, object>());
                var household = ReadDictionary(profile, "commonerHousehold");
                household["maritalStatus"] = "married";
                household["partner"] = new Dictionary<string, object> { ["name"] = "Orestes Varen", ["relationship"] = "spouse" };
                household["children"] = new[] { new Dictionary<string, object> { ["name"] = "Livia Doria", ["ageAtFirstContact"] = 6 } };
                string expected = Json.Serialize(household);
                var restored = TryParseJsonObject(Json.Serialize(profile));
                var native = TryParseJsonObject(Json.Serialize(profile)); native.Remove("commonerHousehold"); native["age"] = 32;
                ReadDictionary(native, "encounteredResident")["recruited"] = true;
                PreserveCommonerHousehold(native, restored);
                Require(Json.Serialize(ReadDictionary(native, "commonerHousehold")) == expected, "Native refresh or recruitment erased/changed saved household.");
                string prompt = BuildCommonerHouseholdPrompt(native);
                Require(prompt.Contains("married") && prompt.Contains("Orestes Varen") && prompt.Contains("Livia Doria (7 years old)")
                    && prompt.Contains("overrides those absences"), "Blank native family fields erased marriage, children or aging.");
                Require(!ReadString(profile, "nativeEncyclopediaText", "").Contains("no known clan or family"), "Legacy native stub still contradicts household.");
                Require(NormalizeResidentFamilyAbsence("Sabina lives in Zeonica. Their personal household is recorded separately from native family links. She is married to Orestes.")
                    == "Sabina lives in Zeonica. She is married to Orestes.", "Visible biography leaked storage details or lost the actual family story.");
                Require(ReadString(native, "spouseId", "") == "" && ReadStringList(native, "childrenIds").Count == 0, "Narrative family fabricated native kinship.");
                Dictionary<string, object> RomanticPosture(Dictionary<string, object> subject)
                {
                    var empty = new Dictionary<string, object>();
                    return CalculateRomanticPosture("verify_commoner", "resident", "visitor", subject, empty, empty, empty, empty,
                        new Dictionary<string, object> { ["skipCooldownQuery"] = true }, "dialogue", "Tell me about your family.");
                }
                var activeHousehold = ReadDictionary(native, "commonerHousehold");
                foreach (string maritalStatus in new[] { "married", "separated", "widowed", "single" })
                {
                    activeHousehold["maritalStatus"] = maritalStatus;
                    var posture = RomanticPosture(native);
                    Require(ReadBool(posture, "married", false) == (maritalStatus == "married" || maritalStatus == "separated")
                        && ReadString(posture, "maritalStatus", "") == maritalStatus
                        && ReadString(posture, "maritalStatusSource", "") == "saved_commoner_household",
                        "Motive guidance inferred marital status from missing native family records.");
                    Require(ReadDouble(posture, "maritalDiscontent", -1) == (maritalStatus == "separated" ? 35 : 0),
                        "Separation was erased or invented by romantic guidance.");
                }
                native["spouseId"] = "real-spouse"; native["spouseName"] = "Real Native Spouse";
                Require(BuildCommonerHouseholdPrompt(native).Contains("Current native spouse: Real Native Spouse [real-spouse]"), "A later real marriage did not govern current status.");
                Require(ReadBool(RomanticPosture(native), "married", false)
                    && ReadString(RomanticPosture(native), "maritalStatusSource", "") == "native_spouse", "Narrative single status erased an actual later marriage.");
                ReadDictionary(native, "encounteredResident")["residentId"] = "different-person";
                native["spouseId"] = "";
                Require(CommonerNarrativeMaritalStatus(native) == "", "Stale household crossed resident identities in motive guidance.");
                PreserveCommonerHousehold(native, restored);
                Require(ReadString(ReadDictionary(native, "commonerHousehold"), "residentId", "") == "different-person"
                    && Json.Serialize(ReadDictionary(native, "commonerHousehold")) != expected, "Reused native character ID inherited another resident's family.");
                return new { providerCalls = 0, preserved = true, actualKinshipUnchanged = true };
            });
            Check("household_does_not_fabricate_native_kinship", () => {
                var speaker = new Dictionary<string, object> { ["heroStringId"] = "resident", ["name"] = "Sabina Doria", ["spouseId"] = "",
                    ["encounteredResident"] = new Dictionary<string, object> { ["schema"] = "reign-encountered-resident-v1", ["residentId"] = "family-map" } };
                var visitor = new Dictionary<string, object> { ["heroStringId"] = "visitor", ["name"] = "Godric Voss" };
                var payload = new Dictionary<string, object> { ["hero"] = speaker, ["heroStringId"] = "resident", ["participantProfiles"] = new[] { speaker, visitor } };
                Require(FindNativeKinshipContradictions(new Dictionary<string, object> { ["reply"] = "My husband Orestes Varen and my daughter Livia live at home." }, payload, "resident").Count == 0,
                    "Native kinship repair erased unmodeled relatives.");
                Require(FindNativeKinshipContradictions(new Dictionary<string, object> { ["reply"] = "Godric Voss is my husband." }, payload, "resident").Count > 0,
                    "Household exception allowed invented kinship with a native participant.");
                string map = BuildInteractionRoleAttribution(payload, "resident", "Sabina Doria", "Visitor");
                Require(map.Contains("never erases that household") && map.Contains("private household is not automatically known"), "Family map conflicts with household or leaks private knowledge.");
                return true;
            });
            Check("commoner_prompt_every_conversation_envelope", () => {
                var profile = new Dictionary<string, object> { ["heroStringId"] = "commoner-fixture", ["name"] = "Sabina Doria",
                    ["encounteredResident"] = new Dictionary<string, object> { ["schema"] = "reign-encountered-resident-v1", ["occupation"] = "TavernWench", ["residentId"] = "commoner-envelopes" } };
                PreserveCommonerHousehold(profile, new Dictionary<string, object>());
                var characteristics = new Dictionary<string, object> { ["traits"] = new Dictionary<string, object> {
                    ["basePersonalitySummary"] = "Distinctive practical outlook and dry humor." } };
                var empty = new Dictionary<string, object>(); var rows = new List<Dictionary<string, object>>();
                string template = NormalizePromptSegment(LoadPromptTemplate("commoner_prompt.txt"));
                var envelopes = new List<PromptEnvelope>();
                envelopes.Add(BuildDialoguePromptEnvelope("verify_commoner", "commoner-fixture", "Sabina Doria", "Visitor", "Visitor",
                    "How is your work?", "An ordinary conversation.", profile, characteristics, empty, empty, empty, rows, rows, rows, rows, empty, empty));
                foreach (string mode in new[] { "settlement_home", "party_chat", "social_event" })
                {
                    var payload = new Dictionary<string, object> { ["mode"] = mode };
                    envelopes.Add(BuildEventPromptEnvelope("verify_commoner", "commoner-event", "commoner-fixture", "Sabina Doria", "Visitor", "Visitor",
                        "Tell me more.", "An ordinary conversation.", payload, profile, characteristics, empty, empty, empty, rows, rows, rows, rows, empty));
                }
                envelopes.Add(BuildCorrespondencePromptEnvelope("verify_commoner", "commoner-fixture", "Sabina Doria", "visitor", "Visitor", 1,
                    "How are you?", profile, characteristics, empty, ""));
                foreach (var envelope in envelopes)
                {
                    string text = string.Join("\n", envelope.Messages.Select(m => ReadString(m, "content", "")));
                    Require(text.Contains(template) && text.Contains("Distinctive practical outlook") && text.Contains("WORLD TONE")
                        && NormalizePromptSegment(text).Contains(NormalizePromptSegment(BuildCommonerHouseholdPrompt(profile))),
                        "A production conversation envelope omitted commoner, household, personality or world layers.");
                }
                return new { providerCalls = 0, conversationEnvelopes = envelopes.Count };
            });
            return results;
        }
    }
}
