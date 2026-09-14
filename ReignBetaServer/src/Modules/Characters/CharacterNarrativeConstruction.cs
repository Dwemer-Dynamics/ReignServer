using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly ConcurrentDictionary<string, object> NarrativeConstructionLocks = new ConcurrentDictionary<string, object>();

        private sealed class NarrativeOutputLimitException : InvalidOperationException
        {
            public NarrativeOutputLimitException(int maxTokens)
                : base("Character creation reached its " + maxTokens.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                    + "-token output limit before returning a complete character. The completed sections were kept; another identical attempt was not sent.") { }
        }

        private static bool NormalizeCharacterConstructionBudget(Dictionary<string, object> settings)
        {
            bool initialized = ReadBool(settings, "characterConstructionBudgetInitialized", false);
            int previous = ReadInt(settings, "characterConstructionMaxTokens", CharacterConstructionTokenCeiling);
            int maximum = initialized ? CharacterConstructionMaxTokensForAttempt(previous, 1) : CharacterConstructionTokenCeiling;
            settings["characterConstructionMaxTokens"] = maximum;
            settings["characterConstructionBudgetInitialized"] = true;
            return !initialized || maximum != previous;
        }
        private const string NarrativeAuthorPolicy = "Write an individual, psychologically coherent medieval person in Calradia. " +
            "Use readable names or ordinary relationship words in every prose field. Never print internal identifiers, database keys or template names, and never guess an unavailable proper name. " +
            "Authoritative identity, age, culture, native biography, family, station, skills and established personality are fixed. " +
            "Never invent named relatives, marriages, births, deaths, crimes, offices, battles, property or political achievements. " +
            "Respect age and life state: write a deceased person's history in the past tense, and never give a young person decades of adult experiences. " +
            "Use small plausible anonymous formative experiences where canon is silent. A child cannot have an adult career. " +
            "For children, preserve their actual developmental age: a toddler has simple wants, sensory interests, short speech and attachment needs, not an adult philosophy in a small body. " +
            "Describe emerging values as simple expectations learned through play and care; do not label normal childhood distress as a clinical wound or invent neglect. " +
            "Describe what a child actually does in plain language; do not fill their biography with disclaimers about careers, offices or adult reasoning they do not have. " +
            "An interest does not imply access to expensive equipment, literacy, travel or professional expertise. " +
            "Fit its expression to opportunity and station: curiosity, modest practice and remembered observation are valid. " +
            "Avoid generic noble insecurity, obligatory trauma and political ambition in every profile. " +
            "Preserve the full moral range of the supplied personality: vanity, spite, piety, pleasure, duty, ambition and affection can coexist. Do not turn every flawed motive into hidden altruism. " +
            "The seeded concepts are prompts for interpretation, not authoritative life events: adapt their scope to this person, " +
            "and reconcile tensions through different contexts or priorities. Do not change the concept into an unrelated interest. " +
            "No modern technology, terminology, diagnoses or anachronistic leisure institutions. " + NarrativeRealismPolicy;

        private static Dictionary<string, object> NarrativeReadableFacts(Dictionary<string, object> hero)
        {
            // Identity keys remain in the checkpoint and seed. Prose receives only
            // human-readable facts so missing display names cannot become raw IDs.
            var facts = new Dictionary<string, object>();
            foreach (string key in new[] { "name", "age", "ageContext", "occupation", "title", "isLord", "isRuler", "isClanLeader", "isNotable", "isWanderer", "isAlive", "isFemale",
                "clanName", "clanTier", "kingdomName", "houseName", "roleLabel", "homeSettlementName", "fatherName", "motherName", "spouseName", "childrenNames",
                "nativeEncyclopediaText", "traits", "skills" })
                if (hero != null && hero.ContainsKey(key)) facts[key] = hero[key];
            string culture = FirstNonEmpty(ReadString(hero, "cultureName", ""), ReadString(hero, "cultureId", ""));
            if (culture.Length > 0 && !culture.Contains("_")) facts["culture"] = culture;
            string voice = ReadString(hero, "voiceId", "");
            if (voice.Length > 0 && !voice.Contains("_")) facts["nativeVoice"] = voice;
            return facts;
        }

        private static bool NarrativeProseClean(string value)
        {
            return (value ?? "").IndexOf('\uFFFD') < 0 && !System.Text.RegularExpressions.Regex.IsMatch(value ?? "",
                @"\b(?:(?:dead_)?lord_\d|clan_|reign_(?:court|house)_|(?:castle|town)_[A-Z]\d|spc_|child_\d)[A-Za-z0-9_]*\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // Provider and checkpoint injection make the production authoring path testable,
        // resumable, and usable by non-listening artifact-bound authoring tools.
        private static Dictionary<string, object> AuthorCharacterNarrative(
            Dictionary<string, object> hero, Dictionary<string, object> traits, string seed,
            Func<Dictionary<string, object>, Dictionary<string, object>> provider,
            Dictionary<string, object> checkpoint = null, Action<Dictionary<string, object>> saveCheckpoint = null,
            int maxTokens = CharacterConstructionTokenCeiling)
        {
            maxTokens = CharacterConstructionMaxTokensForAttempt(maxTokens, 1);
            var blueprint = BuildNarrativeBlueprint(hero, seed, traits);
            string inputFingerprint = NarrativeAuthoringFingerprint(hero, traits, seed, blueprint);
            var document = checkpoint != null && ReadString(checkpoint, "inputFingerprint", "") == inputFingerprint
                ? DeepCloneProfileDictionary(checkpoint) : blueprint;
            document["inputFingerprint"] = inputFingerprint;
            PolishNarrativeProse(document);
            if (NarrativeDocumentReady(document)) return document;
            var attempts = ReadDictionaryList(document, "authoringAttempts");
            var errors = new List<string>();
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var items = ReadDictionaryList(document, "items");
                bool needLife = NarrativeLifeErrors(document).Any(NarrativeIsLifeError);
                bool needVoice = NarrativeLifeErrors(document).Any(x => !NarrativeIsLifeError(x));
                var slots = Enumerable.Range(0, items.Count).Where(i => !NarrativeItemTextReady(items[i])).ToList();
                int incompleteSectionsBefore = slots.Count + (needLife ? 1 : 0) + (needVoice ? 1 : 0);
                // Reassess all meanings only for a cross-item failure, not an ordinary missing field.
                if (!needLife && !needVoice && slots.Count == 0) slots = Enumerable.Range(0, items.Count).ToList();
                var concerns = new Dictionary<string, object>();
                var retained = new Dictionary<string, object>();
                for (int i = 0; i < items.Count; i++)
                {
                    string slot = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    concerns[slot] = new object[] { ReadString(items[i], "category", ""), ReadString(items[i], "title", ""), ReadInt(items[i], "influence", 1) };
                    if (!slots.Contains(i)) retained[slot] = new[] { ReadString(items[i], "description", ""), ReadString(items[i], "personalMeaning", ""), ReadString(items[i], "meaningKey", "") };
                }
                var context = new Dictionary<string, object> {
                    ["nativeFacts"] = NarrativeReadableFacts(hero), ["personality"] = traits,
                    ["concernsBySlot_category_title_importance"] = concerns,
                    ["requiredSlots"] = slots, ["writeLife"] = needLife, ["writeVoice"] = needVoice };
                if (!needLife) context["retainedLife"] = ReadDictionary(document, "life");
                if (!needVoice) context["retainedVoice"] = ReadDictionary(document, "voice");
                if (retained.Count > 0) context["retainedItems"] = retained;
                string instruction = "Write the complete requested character in ONE JSON object, making biography, voice and concerns consistent together. "
                    + "Return only requested sections: life when writeLife=true, voice when writeVoice=true, and items for requiredSlots. "
                    + "life={summary,publicSummary,origin,upbringing,reputation,privateBackstory}; summary is a coherent historical life account "
                    + "with beginning, development and present circumstances, not a list of motives. publicSummary contains public facts only: "
                    + "no secrets, wounds, jealousy, fears or inner motives. Other life fields use concrete individual details. "
                    + "voice={nativeVoice,speechStyle,socialMask,tells:[...]}; individual speech, social presentation and 2 specific tells. "
                    + "items is an OBJECT keyed by short decimal slots, e.g. {\"0\":[\"How it appears in their life.\",\"Why it matters personally.\",\"underlying aim\"]}. "
                    + "Each value is exactly 3 nonempty strings: description, personalMeaning, meaningKey. "
                    + "As loose length guidance, aim around 900-1100 characters for the life summary, 150-300 for the public summary, "
                    + "one or two sentences for other prose fields, and a few words for meaningKey when that fits this person. "
                    + "Realism, native accuracy and consistent roleplay take priority over every length target: use more or less detail as needed, "
                    + "never pad a short history or cut context needed for continuity. "
                    + "Use concrete varied details, not paraphrases of titles or repeated templates. Include EVERY required slot exactly once; no other slots. "
                    + "The same underlying aim must share a meaningKey even across categories; different aims need distinct keys. "
                    + "Respect retained sections; return only requested repairs. Never output permanent IDs, ratings or metadata. "
                    + "Write economically without omitting concerns or flattening their individual meaning.";
                string userPrompt = Json.Serialize(context) + "\n" + instruction
                    + (errors.Count > 0 ? "\nPrevious validation: " + string.Join("; ", errors) : "");
                var request = new Dictionary<string, object> {
                    // An output ceiling, not a target length. Keep the compact field/slot contract.
                    ["requestType"] = "character_construction", ["maxTokens"] = maxTokens,
                    ["temperature"] = attempt == 1 ? 0.65d : 0.25d,
                    ["messages"] = new List<Dictionary<string, object>> {
                        new Dictionary<string, object> { ["role"] = "system", ["content"] = NarrativeAuthorPolicy + " Return strict JSON only." },
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = userPrompt } },
                    ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" } };
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var response = provider(request);
                timer.Stop();
                var result = ReadBool(response, "ok", false) ? TryParseJsonObject(ReadString(response, "content", "")) : null;
                errors = new List<string>();
                var candidate = DeepCloneProfileDictionary(document);
                if (result == null) errors.Add("Provider did not return a complete JSON object.");
                else
                {
                    if (needLife) candidate["life"] = ReadDictionary(result, "life");
                    if (needVoice) candidate["voice"] = ReadDictionary(result, "voice");
                    PolishNarrativeProse(candidate);
                    var sectionErrors = NarrativeLifeErrors(candidate);
                    if (needLife && !sectionErrors.Any(NarrativeIsLifeError)) document["life"] = candidate["life"];
                    if (needVoice && !sectionErrors.Any(x => !NarrativeIsLifeError(x))) document["voice"] = candidate["voice"];
                    errors.AddRange(sectionErrors);
                    var authored = ReadDictionary(result, "items");
                    var expectedSlots = slots.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
                    var unexpected = authored.Keys.Except(expectedSlots, StringComparer.Ordinal).ToList();
                    if (unexpected.Count > 0) errors.Add("Unexpected slots ignored: " + Json.Serialize(unexpected));
                    foreach (int i in slots)
                    {
                        string slot = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var text = ReadStringList(authored, slot);
                        var item = DeepCloneProfileDictionary(items[i]);
                        if (text.Count == 3)
                        {
                            item["description"] = PolishNarrativeText(text[0].Trim());
                            item["personalMeaning"] = PolishNarrativeText(text[1].Trim());
                            item["meaningKey"] = text[2].Trim();
                        }
                        if (text.Count != 3 || !NarrativeItemTextReady(item)) errors.Add("Slot " + slot + " requires [description, personalMeaning, meaningKey], each meaningful and complete.");
                        else items[i] = item;
                    }
                    document["items"] = items;
                }
                document["definingIds"] = SelectDefiningNarrativeIds(ReadDictionaryList(document, "items"), seed, new List<string>());
                document["status"] = "ready";
                errors.AddRange(ValidateCharacterNarrative(document, true));
                bool accepted = errors.Count == 0;
                if (!accepted) document["status"] = "blueprint";
                attempts.Add(new Dictionary<string, object> { ["stage"] = "single_call", ["attempt"] = attempt,
                    ["ok"] = accepted, ["errors"] = errors.Distinct().ToList(), ["provider"] = ReadString(response, "provider", ""),
                    ["providerError"] = LimitText(ReadString(response, "error", ""), 1200),
                    ["model"] = ReadString(response, "model", ""), ["finishReason"] = ReadString(response, "finishReason", ""),
                    ["durationMs"] = timer.ElapsedMilliseconds, ["maxTokens"] = maxTokens,
                    ["requestCharacters"] = NarrativeAuthorPolicy.Length + userPrompt.Length,
                    ["responseCharacters"] = ReadString(response, "content", "").Length, ["requestedSlots"] = slots.Count,
                    ["completedUtc"] = DateTime.UtcNow.ToString("o") });
                document["authoringAttempts"] = attempts;
                document["authoringMethod"] = "single_call_v1";
                if (accepted) document["authoring"] = "library_plus_character_construction_provider";
                saveCheckpoint?.Invoke(document);
                if (!ReadBool(response, "ok", false)) throw new InvalidOperationException("Narrative provider request failed: " + LimitText(ReadString(response, "error", "Provider unavailable."), 1200));
                if (accepted) return document;
                var remainingLifeErrors = NarrativeLifeErrors(document);
                int incompleteSectionsAfter = ReadDictionaryList(document, "items").Count(item => !NarrativeItemTextReady(item))
                    + (remainingLifeErrors.Any(NarrativeIsLifeError) ? 1 : 0)
                    + (remainingLifeErrors.Any(x => !NarrativeIsLifeError(x)) ? 1 : 0);
                if (incompleteSectionsAfter >= incompleteSectionsBefore
                    && string.Equals(ReadString(response, "finishReason", ""), "length", StringComparison.OrdinalIgnoreCase))
                    throw new NarrativeOutputLimitException(maxTokens);
            }
            throw new InvalidOperationException("Narrative single-call generation failed validation: " + string.Join(" ", errors.Distinct()));
        }

        private static bool NarrativeIsLifeError(string error)
        {
            return error.StartsWith("Missing or incomplete life.") || error.StartsWith("life.");
        }

        private static bool NarrativeItemTextReady(Dictionary<string, object> item)
        {
            return !string.IsNullOrWhiteSpace(ReadString(item, "description", ""))
                && !string.IsNullOrWhiteSpace(ReadString(item, "personalMeaning", ""))
                && !string.IsNullOrWhiteSpace(ReadString(item, "meaningKey", ""))
                && NarrativeProseClean(ReadString(item, "description", "")) && NarrativeProseClean(ReadString(item, "personalMeaning", ""));
        }

        private static string NarrativeAuthoringFingerprint(Dictionary<string, object> hero,
            Dictionary<string, object> traits, string seed, Dictionary<string, object> blueprint = null)
        {
            return PromptHash(Json.Serialize(hero) + Json.Serialize(traits) + seed + NarrativeAuthorPolicy
                + Json.Serialize(LoadNarrativeLibrary()) + Json.Serialize(blueprint ?? BuildNarrativeBlueprint(hero, seed, traits)));
        }

        private static List<string> NarrativeLifeErrors(Dictionary<string, object> narrative)
        {
            var errors = new List<string>();
            var life = ReadDictionary(narrative, "life");
            var voice = ReadDictionary(narrative, "voice");
            foreach (string key in new[] { "summary", "publicSummary", "origin", "upbringing", "reputation", "privateBackstory" })
            {
                if (string.IsNullOrWhiteSpace(ReadString(life, key, ""))) errors.Add("Missing or incomplete life." + key);
                if (!NarrativeProseClean(ReadString(life, key, ""))) errors.Add("life." + key + " contains an internal identifier or damaged text; use a readable name or ordinary relationship word.");
            }
            foreach (string key in new[] { "nativeVoice", "speechStyle", "socialMask" })
            {
                if (string.IsNullOrWhiteSpace(ReadString(voice, key, ""))) errors.Add("Missing or incomplete voice." + key);
                if (!NarrativeProseClean(ReadString(voice, key, ""))) errors.Add("voice." + key + " contains an internal identifier or damaged text.");
            }
            if (ReadStringList(voice, "tells").Count < 2) errors.Add("At least two individually written tells are required.");
            if (ReadStringList(voice, "tells").Any(x => !NarrativeProseClean(x))) errors.Add("Voice tells contain an internal identifier or damaged text.");
            return errors;
        }

        private static Dictionary<string, object> ConstructNarrativeCharacter(string campaignId, string heroId,
            Dictionary<string, object> hero, bool force, string source)
        {
            string path = CharacterFile(campaignId, heroId, "narrative.json");
            var existing = ReadJsonObject(path);
            var traits = ReadJsonObject(CharacterFile(campaignId, heroId, "traits.json"));
            if (!TraitDocumentReady(traits)) traits = BuildTraitDocument(hero);
            string stagedPath = CharacterFile(campaignId, heroId, "narrative_authoring.json");
            var settings = LoadSettings();
            try
            {
                var narrative = !force && NarrativeDocumentReady(existing) ? existing :
                    AuthorCharacterNarrative(hero, traits, "dynamic/" + campaignId + "/" + heroId,
                        ChatWithLlm, force && NarrativeDocumentReady(existing) ? null : ReadJsonObject(stagedPath), value => WriteJsonObject(stagedPath, value),
                        ReadInt(settings, "characterConstructionMaxTokens", CharacterConstructionTokenCeiling));
                // Publish only a complete document. The projections are derived from this
                // single atomically written source, never from partially completed stages.
                WriteJsonObject(path, narrative);
                WriteJsonObject(CharacterFile(campaignId, heroId, "traits.json"), traits);
                var projection = new Dictionary<string, object> { ["narrative"] = narrative,
                    ["background"] = new Dictionary<string, object> { ["nativeEncyclopediaText"] = ReadString(hero, "nativeEncyclopediaText", "") } };
                ApplyNarrativePromptProjection(projection);
                foreach (var part in new Dictionary<string, string> { ["background"] = "background", ["voice"] = "voice", ["motivations"] = "motivations",
                    ["saveQuirks"] = "save_quirks", ["hiddenHistory"] = "hidden_history", ["secrets"] = "secrets" })
                    WriteJsonObject(CharacterFile(campaignId, heroId, part.Value + ".json"), projection[part.Key]);
                var ready = new Dictionary<string, object> { ["ok"] = true, ["status"] = "ready", ["llmUsed"] = true,
                    ["constructionEngineVersion"] = CharacterConstructionEngineVersion, ["narrativeSchema"] = NarrativeSchema,
                    ["source"] = source ?? "character_construction", ["constructedUtc"] = DateTime.UtcNow.ToString("o") };
                WriteJsonObject(CharacterFile(campaignId, heroId, "constructed.json"), ready);
                InvalidatePromptRuntimeCache();
                return ready;
            }
            catch (Exception ex)
            {
                if (ex is CampaignRequestReplacedException) throw;
                var failure = new Dictionary<string, object> { ["ok"] = false, ["status"] = "failed",
                    ["error"] = ex.Message, ["failureKind"] = ex is NarrativeOutputLimitException ? "output_limit"
                        : ex.Message.StartsWith("Narrative provider request failed") ? CharacterFailureKindFromText(ex.Message) : "parse",
                    ["settingsRevision"] = ReadString(settings, "settingsRevision", ""), ["settingsFingerprint"] = SettingsFingerprint(settings),
                    ["constructionEngineVersion"] = CharacterConstructionEngineVersion, ["failedUtc"] = DateTime.UtcNow.ToString("o") };
                if (ReadString(existing, "status", "") != "ready") WriteJsonObject(CharacterFile(campaignId, heroId, "constructed.json"), failure);
                return failure;
            }
        }

        private static Dictionary<string, object> EnsureNarrativeCharacterConstructed(string campaignId, string heroId,
            Dictionary<string, object> hero, string source)
        {
            using (EnterCampaignWorkLock(NarrativeConstructionLocks.GetOrAdd(campaignId + "|" + heroId, _ => new object())))
            {
                if (LoadCharacterProfileLibrary().ContainsKey(heroId))
                {
                    var materialized = MaterializeCharacterProfile(campaignId, heroId, hero, false, false);
                    if (!ReadBool(materialized, "ok", false)) return materialized;
                    var ready = ReadJsonObject(CharacterFile(campaignId, heroId, "constructed.json"));
                    ready["ok"] = NarrativeDocumentReady(ReadJsonObject(CharacterFile(campaignId, heroId, "narrative.json")));
                    return ready;
                }
                var existing = ReadJsonObject(CharacterFile(campaignId, heroId, "constructed.json"));
                bool complete = NarrativeDocumentReady(ReadJsonObject(CharacterFile(campaignId, heroId, "narrative.json")));
                if (complete && ReadString(existing, "narrativeSchema", "") == NarrativeSchema && IsCharacterConstructionReady(existing))
                { existing["alreadyReady"] = true; return existing; }
                if (!complete && ReadString(existing, "status", "") == "failed" && !ShouldRetryFailedConstruction(existing, LoadSettings()))
                { existing["ok"] = false; existing["alreadyFailed"] = true; return existing; }
                // New characters have their full story before speaking. A partial stage
                // stays in its checkpoint and cannot be presented as a completed profile.
                var result = ConstructNarrativeCharacter(campaignId, heroId, hero, false, source);
                if (IsCharacterConstructionReady(result)) MarkCharacterConstructionCompletedOnInteraction(campaignId, heroId, source);
                return result;
            }
        }
    }
}
