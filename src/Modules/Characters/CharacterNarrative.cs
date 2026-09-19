using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string NarrativeSchema = "reign-narrative-v1";
        private const string NarrativePack = "reign_profiles_v5";
        private static readonly object NarrativeLibraryLock = new object();
        private static readonly object NarrativeStateLock = new object();
        private static List<Dictionary<string, object>> NarrativeLibraryCache;
        private static readonly int[] NarrativeInfluenceWeights = { 5, 10, 15, 20, 20, 12, 8, 5, 3, 2 };
        private static readonly string[] NarrativeInfluenceLevels = {
            "Peripheral: an occasional pastime or persistent thought with little effect on choices.",
            "Mild: noticed when prompted and easily set aside.",
            "Modest: returns periodically and influences small preferences.",
            "Meaningful: regularly shapes attention and everyday choices.",
            "Important: receives deliberate time, thought, or avoidance.",
            "Strong: influences priorities and justifies noticeable effort or compromise.",
            "Deep: shapes important choices and produces strong reactions when engaged.",
            "Central: organizes a substantial part of private life and sometimes outweighs competing personal interests.",
            "Dominant: frequently occupies private thought and can justify serious, proportionate personal sacrifice.",
            "Consuming personal importance: exceptionally enduring and absorbing when relevant, without compulsive speech or automatic loss of judgment."
        };
        private static readonly string[] NarrativeEligibleCategories = {
            "hobby", "dream", "desire", "fear", "value", "attachment", "wound", "jealousy", "temptation", "secret", "formative"
        };
        private const string NarrativeRealismPolicy =
            "DEFINING INTERESTS ARE STRONG MOTIVATIONAL LENSES, NOT COMPULSORY TALKING POINTS. " +
            "Separate personal importance from situational relevance and proportionate consequences. " +
            "Even influence 10 is not an order to mention an interest constantly, interrupt unrelated business, abandon dependents, or lose judgment. " +
            "A ruler may dearly love cats, enjoy their company and fund sensible care; that does not make kittens relevant to a military council or justify neglecting the realm. " +
            "Attend first to the actual question, immediate danger, duties, native facts, relationships and competent judgment. " +
            "Let relevant drives substantially shape attention, decisions, curiosity, warmth, reluctance and boundaries; allow irrelevant interests to remain unspoken. " +
            "Do not repeat an interest merely because it appeared in a previous reply. No mandatory metaphors, hobby anecdotes or self-descriptions. " +
            "Interest is not expertise: native skill evidence still governs capability. A fear is not evidence of an external threat; jealousy is not evidence of another person's wrongdoing. " +
            "Private material can shape guarded behavior without becoming a confession. Never expose influence numbers, item IDs, selection rules or private facts to an unjustified audience.";

        private static ulong NarrativeDraw(string seed, string domain)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(NarrativeSchema + "\n" + seed + "\n" + domain));
                ulong value = 0;
                for (int i = 0; i < 8; i++) value = (value << 8) | bytes[i];
                return value;
            }
        }

        private static int NarrativeInfluence(string seed, string itemId)
        {
            int roll = (int)(NarrativeDraw(seed, "influence/" + itemId) % 100);
            for (int i = 0; i < NarrativeInfluenceWeights.Length; i++)
            {
                if (roll < NarrativeInfluenceWeights[i]) return i + 1;
                roll -= NarrativeInfluenceWeights[i];
            }
            return 10;
        }

        private static string NarrativeInfluenceDescription(string category, int level)
        {
            string expression;
            switch (category)
            {
                case "fear": expression = "Fear changes anticipation, caution and avoidance only where there is a credible connection; it never supplies proof of danger."; break;
                case "wound": expression = "This experience affects sensitivity and coping when its meaning is engaged; it does not require constant distress or retelling."; break;
                case "jealousy": expression = "Jealousy affects comparison, insecurity and guardedness; suspicion must remain distinct from evidence and justified action."; break;
                case "hobby": expression = "Enjoyment affects voluntary practice, curiosity, pleasure and reasonable personal spending, not automatic professional authority or dereliction of duty."; break;
                case "secret": expression = "Private importance affects discretion, vulnerability and choices about disclosure; it is not an instruction to confess."; break;
                case "attachment": expression = "Attachment affects care, time, concern and proportionate sacrifice while respecting other responsibilities."; break;
                case "value": expression = "Commitment affects moral priorities and reasoned boundaries while allowing nuanced judgment."; break;
                case "temptation": expression = "Temptation makes an option emotionally attractive; personality, judgment, commitments and consequences can still restrain it."; break;
                case "formative": expression = "The experience informs learned expectations and choices when analogous circumstances arise; it need not be narrated."; break;
                default: expression = "Motivation affects initiative, persistence, bargaining and proportionate tradeoffs; it is not permission to invent completed achievements."; break;
            }
            return NarrativeInfluenceLevels[Math.Max(1, Math.Min(10, level)) - 1] + " " + expression;
        }

        private static List<Dictionary<string, object>> LoadNarrativeLibrary()
        {
            lock (NarrativeLibraryLock)
            {
                if (NarrativeLibraryCache != null) return NarrativeLibraryCache;
                var result = new List<Dictionary<string, object>>();
                foreach (string file in new[] { "hobbies.txt", "drives.txt", "additional.txt" })
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles", "narrative", file);
                    if (!File.Exists(path)) throw new InvalidOperationException("Narrative library is missing: " + file);
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8).Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("#")))
                    {
                        string[] parts = line.Split('|');
                        string category = file == "hobbies.txt" ? "hobby" : parts[0].Split('/')[0];
                        foreach (string raw in parts.Skip(1))
                        {
                            string title = raw.Trim();
                            if (title.Length == 0) throw new InvalidOperationException("Empty narrative library concept.");
                            result.Add(new Dictionary<string, object> {
                                ["id"] = category + "_" + PromptHash(category + "|" + title).Substring(0, 16).ToLowerInvariant(),
                                ["category"] = category, ["family"] = parts[0], ["title"] = title,
                                ["minAge"] = parts[0].Contains("/child_") ? 0 : 12,
                                ["maxAge"] = parts[0].Contains("/child_") ? 11 : 150
                            });
                        }
                    }
                }
                if (result.Count < 2200 || result.Select(x => ReadString(x, "id", "")).Distinct().Count() != result.Count)
                    throw new InvalidOperationException("Narrative library must contain at least 2200 distinct concepts.");
                AttachNarrativeDreamRules(result);
                NarrativeLibraryCache = result;
                return result;
            }
        }

        private static Dictionary<string, object> BuildNarrativeBlueprint(Dictionary<string, object> hero, string seed,
            Dictionary<string, object> personality = null)
        {
            var items = new List<Dictionary<string, object>>();
            var ranges = new Dictionary<string, int[]> {
                ["hobby"] = new[] {3,7}, ["formative"] = new[] {4,8}, ["dream"] = new[] {2,5},
                ["desire"] = new[] {2,5}, ["fear"] = new[] {2,6}, ["value"] = new[] {2,5},
                ["attachment"] = new[] {1,4}, ["wound"] = new[] {0,3}, ["jealousy"] = new[] {0,3},
                ["temptation"] = new[] {0,3}, ["secret"] = new[] {0,3}
            };
            foreach (var range in ranges)
            {
                int count = range.Value[0] + (int)(NarrativeDraw(seed, "count/" + range.Key) % (ulong)(range.Value[1] - range.Value[0] + 1));
                var candidates = LoadNarrativeLibrary().Where(x => ReadString(x, "category", "") == range.Key)
                    .Where(x => NarrativeConceptWeight(hero, x, personality) > 0)
                    .OrderBy(x => -Math.Log(((NarrativeDraw(seed, "candidate/" + ReadString(x, "id", "")) >> 11) + 1d) / 9007199254740993d)
                        / NarrativeConceptWeight(hero, x, personality)).ToList();
                // Initial hobby candidates span families; an editor can intentionally add several related pursuits.
                if (range.Key == "hobby") candidates = candidates.GroupBy(x => ReadString(x, "family", "")).Select(x => x.First()).ToList();
                if (candidates.Count < count) throw new InvalidOperationException("Insufficient compatible narrative concepts for " + range.Key + ".");
                foreach (var concept in candidates.Take(count))
                {
                    var item = DeepCloneProfileDictionary(concept);
                    string id = ReadString(item, "id", "");
                    item["influence"] = NarrativeInfluence(seed, id);
                    item["description"] = ReadString(item, "title", "");
                    item["personalMeaning"] = "";
                    item["visibility"] = new[] { "fear", "wound", "jealousy", "temptation", "secret" }.Contains(range.Key) ? "private" : "personal";
                    item["source"] = "authored_library_seed";
                    items.Add(item);
                }
            }
            var narrative = new Dictionary<string, object> {
                ["schema"] = NarrativeSchema, ["heroStringId"] = CharacterIdFrom(hero), ["seed"] = seed,
                ["revision"] = 1, ["status"] = "blueprint", ["items"] = items,
                ["definingIds"] = SelectDefiningNarrativeIds(items, seed, new List<string>()),
                ["influenceLevels"] = NarrativeInfluenceLevels, ["realismPolicy"] = NarrativeRealismPolicy
            };
            return narrative;
        }

        private static double NarrativeConceptWeight(Dictionary<string, object> hero, Dictionary<string, object> concept,
            Dictionary<string, object> personality = null)
        {
            double age = ReadDouble(hero, "age", 30);
            if (age >= 0 && (age < ReadInt(concept, "minAge", 0) || age > ReadInt(concept, "maxAge", 150))) return 0d;
            if (!NarrativeFactsPermit(hero, concept)) return 0d;
            if (ReadString(concept, "category", "") != "hobby") return NarrativeConcernPersonalityWeight(hero, personality, concept);
            string title = ReadString(concept, "title", ""), family = ReadString(concept, "family", "");
            if (age > 0 && age < 18 && System.Text.RegularExpressions.Regex.IsMatch(title,
                @"\b(wine|ale|beer|mead|brandy|spirits|drinking|tavern)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return 0d;
            // Familiar skills modestly affect opportunity. They neither dictate a
            // person's leisure nor grant expertise in an unrelated pursuit.
            var skills = ReadDictionary(hero, "skills") ?? new Dictionary<string, object>();
            string skill = family == "horses_livestock" ? "Riding" : family == "physical_pursuits" ? "Athletics"
                : family == "woodwork" || family == "metal_leather" ? "Engineering"
                : family == "social_observation" || family == "story_performance" ? "Charm"
                : family == "hospitality_service" ? "Steward" : family == "travel_places" ? "Scouting"
                : family == "gardens" ? "Medicine" : "";
            string nativeKey = skills.Keys.FirstOrDefault(x => x.Equals(skill, StringComparison.OrdinalIgnoreCase)) ?? skill;
            return skill.Length == 0 ? 1d : 1d + Math.Min(0.6d, Math.Max(0, ReadDouble(skills, nativeKey, 0)) / 300d);
        }

        private static List<string> SelectDefiningNarrativeIds(List<Dictionary<string, object>> items, string seed, List<string> prior)
        {
            return items.Where(x => NarrativeEligibleCategories.Contains(ReadString(x, "category", ""))
                    && ReadString(x, "status", "active") != "superseded")
                .GroupBy(NarrativeMeaningKey).Select(x => x.OrderByDescending(i => ReadInt(i, "influence", 0))
                    .ThenBy(i => prior.Contains(ReadString(i, "id", "")) ? 0 : 1)
                    .ThenBy(i => NarrativeDraw(seed, "meaning-tie/" + ReadString(i, "id", ""))).First())
                .OrderByDescending(x => ReadInt(x, "influence", 0))
                .ThenBy(x => prior.Contains(ReadString(x, "id", "")) ? 0 : 1)
                .ThenBy(x => NarrativeDraw(seed, "defining-tie/" + ReadString(x, "id", "")))
                .Take(3).Select(x => ReadString(x, "id", "")).ToList();
        }

        private static List<string> ValidateCharacterNarrative(Dictionary<string, object> narrative, bool requireWritten)
        {
            var errors = new List<string>();
            if (ReadString(narrative, "schema", "") != NarrativeSchema) errors.Add("Unsupported narrative schema.");
            var items = ReadDictionaryList(narrative, "items");
            var ids = items.Select(x => ReadString(x, "id", "")).ToList();
            if (items.Count == 0 || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct().Count() != items.Count) errors.Add("Narrative IDs must be present and unique.");
            if (items.Any(x => !NarrativeEligibleCategories.Contains(ReadString(x, "category", "")) || ReadInt(x, "influence", 0) < 1 || ReadInt(x, "influence", 0) > 10)) errors.Add("Invalid category or influence.");
            if (items.Any(x => string.IsNullOrWhiteSpace(ReadString(x, "title", "")))) errors.Add("Every item requires a meaningful title.");
            if (items.Any(x => ReadDouble(x, "influence", 0) != ReadInt(x, "influence", 0))) errors.Add("Influence must be a whole number.");
            if (items.Any(x => !new[] { "private", "personal" }.Contains(ReadString(x, "visibility", "")))) errors.Add("Every concern requires a valid disclosure boundary.");
            if (requireWritten) errors.AddRange(NarrativeLifeErrors(narrative));
            if (requireWritten && items.Any(x => string.IsNullOrWhiteSpace(ReadString(x, "description", ""))
                || string.IsNullOrWhiteSpace(ReadString(x, "personalMeaning", "")))) errors.Add("Each item requires individual description and personal meaning.");
            if (requireWritten && items.Any(x => !NarrativeProseClean(ReadString(x, "title", "")) || !NarrativeProseClean(ReadString(x, "description", ""))
                || !NarrativeProseClean(ReadString(x, "personalMeaning", "")))) errors.Add("Interest prose contains an internal identifier or damaged text; use readable names or ordinary relationship words.");
            foreach (string category in new[] { "hobby", "dream", "desire", "fear", "value", "formative" })
                if (items.Count(x => ReadString(x, "category", "") == category) < 2) errors.Add("Multiple " + category + " items are required.");
            var defining = ReadStringList(narrative, "definingIds");
            if (defining.Count != 3 || defining.Distinct().Count() != 3 || defining.Any(x => !ids.Contains(x))) errors.Add("Exactly three valid distinct defining interests are required.");
            if (defining.Count == 3 && defining.All(ids.Contains))
            {
                int weakest = items.Where(x => defining.Contains(ReadString(x, "id", ""))).Min(x => ReadInt(x, "influence", 0));
                var selectedMeanings = items.Where(x => defining.Contains(ReadString(x, "id", ""))).Select(NarrativeMeaningKey).ToList();
                if (selectedMeanings.Distinct().Count() != 3) errors.Add("Defining interests must have distinct meanings.");
                if (items.Any(x => !selectedMeanings.Contains(NarrativeMeaningKey(x)) && ReadInt(x, "influence", 0) > weakest)) errors.Add("A defining interest was selected below a stronger item.");
            }
            return errors;
        }

        private static string NarrativeMeaningKey(Dictionary<string, object> item)
        {
            return NormalizeLookup(FirstNonEmpty(ReadString(item, "meaningKey", ""), ReadString(item, "title", "")));
        }

        private static bool NarrativeDocumentReady(Dictionary<string, object> narrative)
        {
            return ReadString(narrative, "status", "") == "ready" && ValidateCharacterNarrative(narrative, true).Count == 0;
        }

        private static Dictionary<string, object> ResolveEffectiveNarrative(Dictionary<string, object> characteristics)
        {
            var baseNarrative = ReadDictionary(characteristics, "narrative") ?? new Dictionary<string, object>();
            if (ReadString(baseNarrative, "schema", "") != NarrativeSchema) return new Dictionary<string, object>();
            var effective = DeepCloneProfileDictionary(baseNarrative);
            var items = ReadDictionaryList(effective, "items");
            var dynamic = ReadDictionary(characteristics, "dynamicCharacteristics");
            var baseItems = ReadDictionaryList(baseNarrative, "items");
            foreach (var row in ReadDictionaryList(dynamic, "active")
                .Where(x => ReadString(x, "category", "narrative_development") == "narrative_development")
                .OrderBy(x => ReadLong(x, "last_ts", 0)).ThenBy(x => ReadString(x, "characteristic_id", ""), StringComparer.Ordinal))
            {
                var payload = TryParseJsonObject(ReadString(row, "payload_json", "{}")) ?? new Dictionary<string, object>();
                var development = ReadDictionary(payload, "narrativeDevelopment");
                if (ReadString(development, "decision", "") != "accepted") continue;
                var target = items.FirstOrDefault(x => ReadString(x, "id", "") == ReadString(development, "itemId", ""));
                if (target == null) continue;
                string basisFingerprint = ReadString(development, "baseItemFingerprint", "");
                var baseItem = baseItems.FirstOrDefault(x => ReadString(x, "id", "") == ReadString(target, "id", ""));
                if (basisFingerprint.Length > 0 && basisFingerprint != NarrativeItemFingerprint(baseItem)) continue;
                target["description"] = ReadString(development, "description", ReadString(target, "description", ""));
                target["personalMeaning"] = ReadString(development, "personalMeaning", ReadString(target, "personalMeaning", ""));
                target["influence"] = ReadInt(development, "influence", ReadInt(target, "influence", 1));
                target["developmentId"] = ReadString(row, "characteristic_id", "");
            }
            effective["items"] = items;
            effective["definingIds"] = SelectDefiningNarrativeIds(items, ReadString(effective, "seed", ""), ReadStringList(baseNarrative, "definingIds"));
            return effective;
        }

        private static string FormatNarrativeInterest(Dictionary<string, object> item)
        {
            string category = ReadString(item, "category", "");
            return "- " + ReadString(item, "title", "") + " [PRIVATE reference " + ReadString(item, "id", "") + "; influence " + ReadInt(item, "influence", 1) + "/10]: " + ReadString(item, "description", "")
                + " Personal meaning: " + ReadString(item, "personalMeaning", "") + " "
                + NarrativeInfluenceDescription(category, ReadInt(item, "influence", 1))
                + (category == "hobby" && ReadInt(item, "influence", 1) >= 8
                    ? " In safe voluntary time, show this strong interest through sustained attention, concrete curiosity and a wish to return to it; fit the expression to this person's voice. Actual obligations still bound that time." : "")
                + (category == "hobby" && ReadInt(item, "influence", 1) == 10
                    ? " This consuming interest deserves substantial available leisure, not merely an occasional pleasant hour." : "")
                + (ReadString(item, "visibility", "private") == "private" ? " PRIVATE: influence conduct without automatic disclosure." : " Personal information is not automatically known to other speakers.");
        }

        private static string BuildDefiningInterestsPrompt(Dictionary<string, object> characteristics)
        {
            var narrative = ResolveEffectiveNarrative(characteristics);
            var ids = ReadStringList(narrative, "definingIds");
            if (ids.Count != 3) return "";
            var items = ReadDictionaryList(narrative, "items");
            return NarrativeRealismPolicy + "\nCURRENT PERSONAL IMPORTANCE: the supplied current influence governs present strength, including after development or an editor correction. Historical examples of occasional practice do not cap a now-strong interest. "
                + "When responsibilities are finished and the subject is personally relevant, let strong interests noticeably shape voluntary attention, enthusiasm, plans and personal tradeoffs. Do not invent extra duties merely to suppress them. "
                + "The three concerns below are the strongest distinct personal priorities; other concerns enter when relevant.\n"
                + string.Join("\n", ids.Select(id => items.FirstOrDefault(x => ReadString(x, "id", "") == id)).Where(x => x != null).Select(FormatNarrativeInterest));
        }

        private static string BuildRelevantInterestsPrompt(Dictionary<string, object> characteristics, string query)
        {
            var narrative = ResolveEffectiveNarrative(characteristics);
            string question = (query ?? "").ToLowerInvariant();
            var intentPatterns = new Dictionary<string, string> {
                ["hobby"] = @"\b(hobbies|hobby|pastime|leisure|unwind|free time|spare time|for fun|enjoy doing|do you enjoy|do you like)\b",
                ["fear"] = @"\b(fear|fears|afraid|frighten\w*|terrifi\w*|dread\w*|worr\w*)\b",
                ["dream"] = @"\b(dream|dreams|aspir\w*|hope to|hope for|ambition\w*)\b",
                ["desire"] = @"\b(desire\w*|long for|wish for|want most|want from life)\b",
                ["value"] = @"\b(values|principles|believe in|stand for|moral\w*)\b",
                ["attachment"] = @"\b(cherish\w*|treasur\w*|attached|dear to you|care about)\b",
                ["wound"] = @"\b(hurt you|painful past|old wound\w*|past regret\w*)\b",
                ["jealousy"] = @"\b(jealous\w*|envy|envious)\b",
                ["temptation"] = @"\b(tempt\w*|hard to resist)\b",
                ["secret"] = @"\b(secret\w*|private truth\w*|never told|keep hidden)\b",
                ["formative"] = @"\b(shaped you|childhood|upbringing|learned growing up)\b" };
            var intents = intentPatterns.Where(x => System.Text.RegularExpressions.Regex.IsMatch(question, x.Value)).Select(x => x.Key).ToList();
            var terms = System.Text.RegularExpressions.Regex.Matches((query ?? "").ToLowerInvariant(), @"[a-z]{4,}")
                .Cast<System.Text.RegularExpressions.Match>().Select(x => x.Value)
                .Where(x => !new[] { "your", "what", "that", "this", "have", "would", "could", "about", "with", "from", "they", "their", "when", "tell",
                    "know", "really", "please", "like", "things", "something", "yourself", "think", "feel", "there", "does", "else", "more" }.Contains(x)).Distinct().ToList();
            if (terms.Count == 0 && intents.Count == 0) return "";
            var defining = ReadStringList(narrative, "definingIds");
            var relevant = ReadDictionaryList(narrative, "items").Where(x => !defining.Contains(ReadString(x, "id", "")))
                .Select(x => new { Item = x, Score = (intents.Contains(ReadString(x, "category", "")) ? 8 : 0)
                    + terms.Sum(t => ReadString(x, "title", "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ? 3
                        : ReadString(x, "description", "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0) })
                .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenByDescending(x => ReadInt(x.Item, "influence", 0)).Take(3).ToList();
            return relevant.Count == 0 ? "" : "Additional interests relevant to this question; their disclosure boundaries still apply.\n"
                + string.Join("\n", relevant.Select(x => FormatNarrativeInterest(x.Item)));
        }

        private static Dictionary<string, object> NarrativePromptDiagnostics(Dictionary<string, object> characteristics)
        {
            var narrative = ResolveEffectiveNarrative(characteristics);
            return new Dictionary<string, object> { ["schema"] = ReadString(narrative, "schema", "unavailable"),
                ["definingIds"] = ReadStringList(narrative, "definingIds"), ["effectiveFingerprint"] = PromptHash(Json.Serialize(narrative)),
                ["realismGuard"] = true, ["selection"] = "highest_influence_persisted_random_ties" };
        }

        // All prompt routes consume the same effective snapshot. Derived legacy fields
        // are replaced together so an accepted development cannot compete with its past.
        private static void ApplyNarrativePromptProjection(Dictionary<string, object> stack)
        {
            var narrative = ResolveEffectiveNarrative(stack);
            if (ReadString(narrative, "status", "") != "ready") return;
            var items = ReadDictionaryList(narrative, "items");
            Func<string, System.Collections.ArrayList> values = category => new System.Collections.ArrayList(items.Where(x => ReadString(x, "category", "") == category)
                .Select(x => ReadString(x, "description", "") + " " + ReadString(x, "personalMeaning", "")).ToArray());
            var life = ReadDictionary(narrative, "life") ?? new Dictionary<string, object>();
            var previousBackground = ReadDictionary(stack, "background") ?? new Dictionary<string, object>();
            stack["background"] = new Dictionary<string, object> {
                ["version"] = 5, ["summary"] = BuildAroundNativeBackground(ReadString(previousBackground, "nativeEncyclopediaText", ""), ReadString(life, "summary", "")),
                ["nativeEncyclopediaText"] = ReadString(previousBackground, "nativeEncyclopediaText", ""),
                ["nativeTextPreserved"] = !string.IsNullOrWhiteSpace(ReadString(previousBackground, "nativeEncyclopediaText", "")),
                ["encyclopediaText"] = BuildAroundNativeBackground(ReadString(previousBackground, "nativeEncyclopediaText", ""), ReadString(life, "publicSummary", "")),
                ["origin"] = ReadString(life, "origin", ""), ["upbringing"] = ReadString(life, "upbringing", ""),
                ["reputation"] = ReadString(life, "reputation", ""), ["formativeEvents"] = values("formative") };
            stack["voice"] = DeepCloneProfileDictionary(ReadDictionary(narrative, "voice"));
            stack["motivations"] = new Dictionary<string, object> { ["version"] = 5,
                ["dreams"] = values("dream"), ["desires"] = values("desire"), ["fears"] = values("fear"),
                ["values"] = values("value"), ["attachments"] = values("attachment"), ["linesTheyWillNotCross"] = new List<string>() };
            stack["saveQuirks"] = new Dictionary<string, object> { ["version"] = 5,
                ["secretInterests"] = values("secret"), ["jealousyTargets"] = values("jealousy"), ["temptations"] = values("temptation") };
            stack["hiddenHistory"] = new Dictionary<string, object> { ["version"] = 5,
                ["privateBackstory"] = ReadString(life, "privateBackstory", ""), ["secretWounds"] = values("wound") };
            stack["secrets"] = new Dictionary<string, object> { ["version"] = 5,
                ["privateTruths"] = values("secret"), ["rumors"] = new List<string>() };
        }

        private static string NarrativeItemFingerprint(Dictionary<string, object> item)
        {
            return PromptHash(string.Join("\n", new[] { "id", "category", "title", "description", "personalMeaning", "meaningKey", "influence", "visibility" }
                .Select(key => Convert.ToString(item != null && item.ContainsKey(key) ? item[key] : "", CultureInfo.InvariantCulture))));
        }

        private static void PrepareEditorNarrative(Dictionary<string, object> documents)
        {
            var narrative = ReadDictionary(documents, "narrative");
            if (narrative == null || narrative.Count == 0) return;
            narrative["definingIds"] = SelectDefiningNarrativeIds(ReadDictionaryList(narrative, "items"),
                ReadString(narrative, "seed", ""), ReadStringList(narrative, "definingIds"));
            var errors = ValidateCharacterNarrative(narrative, true);
            if (errors.Count > 0) throw new InvalidOperationException("Interests were not saved: " + string.Join(" ", errors));
        }

        private static Dictionary<string, object> InspectCharacterNarrative(Dictionary<string, string> query)
        {
            string campaign = query.ContainsKey("campaignId") ? query["campaignId"] : "";
            string hero = query.ContainsKey("heroId") ? query["heroId"] : "";
            if (string.IsNullOrWhiteSpace(hero)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "heroId is required." };
            Dictionary<string, object> narrative;
            var dynamic = new Dictionary<string, object>();
            var catalog = ReadShippedNarrativeCatalog();
            if (string.IsNullOrWhiteSpace(campaign))
                narrative = ReadDictionary(ReadDictionaryList(catalog, "profiles").FirstOrDefault(x => ReadString(x, "heroStringId", "") == hero), "narrative");
            else
            {
                narrative = ReadJsonObject(CharacterFile(campaign, hero, "narrative.json"));
                dynamic = ReadJsonObject(CharacterFile(campaign, hero, "dynamic_characteristics.json"));
            }
            var stack = new Dictionary<string, object> { ["narrative"] = narrative, ["dynamicCharacteristics"] = dynamic };
            return new Dictionary<string, object> { ["ok"] = true, ["schema"] = NarrativeSchema,
                ["available"] = narrative != null && narrative.Count > 0, ["baseNarrative"] = narrative,
                ["effectiveNarrative"] = ResolveEffectiveNarrative(stack), ["selection"] = NarrativePromptDiagnostics(stack),
                ["developmentHistory"] = dynamic, ["historyScope"] = "retained character projection; no materialization or database writes",
                ["catalogDiversity"] = AuditNarrativeCatalog(catalog) };
        }

        private static Dictionary<string, object> AuditNarrativeCatalog(Dictionary<string, object> catalog)
        {
            var profiles = ReadDictionaryList(catalog, "profiles");
            var narratives = profiles.Select(x => ReadDictionary(x, "narrative")).Where(x => x != null && x.Count > 0).ToList();
            var allItems = narratives.SelectMany(x => ReadDictionaryList(x, "items")).ToList();
            var incompatibleDreams = profiles.Where(x => ReadDictionary(x, "narrative") != null).SelectMany(profile =>
                IncompatibleNarrativeDreamIds(ReadDictionary(profile, "narrative"), NarrativePremadeFacts(profile, profiles),
                    NarrativeAuthoringPersonality(profile)).Select(id => new Dictionary<string, object> {
                        ["heroStringId"] = ReadString(profile, "heroStringId", ""), ["dreamId"] = id })).ToList();
            var incompatibleConcerns = profiles.Where(x => ReadDictionary(x, "narrative") != null).SelectMany(profile =>
                IncompatibleNarrativeConcernIds(ReadDictionary(profile, "narrative"), NarrativePremadeFacts(profile, profiles),
                    NarrativeAuthoringPersonality(profile)).Select(id => new Dictionary<string, object> {
                        ["heroStringId"] = ReadString(profile, "heroStringId", ""), ["itemId"] = id })).ToList();
            var incompatibleFacts = profiles.Where(x => ReadDictionary(x, "narrative") != null).SelectMany(profile =>
                IncompatibleNarrativeFactIds(ReadDictionary(profile, "narrative"), NarrativePremadeFacts(profile, profiles))
                    .Select(id => new Dictionary<string, object> { ["heroStringId"] = ReadString(profile, "heroStringId", ""), ["itemId"] = id })).ToList();
            return new Dictionary<string, object> { ["rosterCount"] = profiles.Count, ["narrativeCount"] = narratives.Count,
                ["incompatibleDreamCount"] = incompatibleDreams.Count,
                ["incompatibleDreams"] = incompatibleDreams.Take(30).ToList(),
                ["incompatibleConcernCount"] = incompatibleConcerns.Count,
                ["incompatibleConcerns"] = incompatibleConcerns.Take(30).ToList(),
                ["incompatibleFactCount"] = incompatibleFacts.Count,
                ["incompatibleFacts"] = incompatibleFacts.Take(30).ToList(),
                ["validCount"] = narratives.Count(x => ValidateCharacterNarrative(x, true).Count == 0),
                ["distinctHobbies"] = allItems.Where(x => ReadString(x, "category", "") == "hobby").Select(x => ReadString(x, "id", "")).Distinct().Count(),
                ["distinctConcernConcepts"] = allItems.Select(x => ReadString(x, "id", "")).Distinct().Count(),
                ["distinctLifeSummaries"] = narratives.Select(x => ReadString(ReadDictionary(x, "life"), "summary", "")).Distinct().Count(),
                ["distinctVoices"] = narratives.Select(x => ReadString(ReadDictionary(x, "voice"), "speechStyle", "")).Distinct().Count(),
                ["distinctDefiningTrios"] = narratives.Select(x => string.Join("|", ReadStringList(x, "definingIds").OrderBy(id => id))).Distinct().Count(),
                ["quality"] = AuditNarrativeQuality(narratives),
                ["influenceHistogram"] = Enumerable.Range(1, 10).Select(level => new Dictionary<string, object> {
                    ["influence"] = level, ["count"] = allItems.Count(x => ReadInt(x, "influence", 0) == level) }).ToList() };
        }

        private static Dictionary<string, object> ReadShippedNarrativeCatalog()
        {
            var catalog = ReadJsonObject(CharacterProfileCatalogPath);
            if (ReadString(catalog, "packVersion", "") != NarrativePack) return catalog;
            var profiles = ReadDictionaryList(catalog, "profiles");
            foreach (var profile in profiles)
            {
                string id = ReadString(profile, "heroStringId", "");
                string file = PromptHash(id).Substring(0, 24) + ".json";
                if (ReadString(profile, "narrativeFile", "") != file) throw new InvalidDataException("Invalid shipped narrative reference: " + id);
                var narrative = ReadJsonObject(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles", "narrative", "profiles", file));
                if (ReadString(narrative, "heroStringId", "") != id || ReadString(narrative, "status", "") != "ready"
                    || ValidateCharacterNarrative(narrative, true).Count > 0) throw new InvalidDataException("Incomplete shipped narrative: " + id);
                profile["narrative"] = narrative;
                var projection = new Dictionary<string, object> { ["narrative"] = narrative,
                    ["background"] = new Dictionary<string, object> { ["nativeEncyclopediaText"] = ReadString(ReadDictionary(profile, "sourceFacts"), "nativeEncyclopediaText", "") } };
                ApplyNarrativePromptProjection(projection);
                foreach (string part in new[] { "background", "voice", "motivations", "saveQuirks", "hiddenHistory", "secrets" }) profile[part] = projection[part];
            }
            catalog["profiles"] = profiles;
            return catalog;
        }
    }
}
