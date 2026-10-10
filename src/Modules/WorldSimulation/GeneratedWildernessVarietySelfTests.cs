using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunWildernessVarietySelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            void Check(string name, Action test)
            {
                try { test(); rows.Add(new Dictionary<string, object> { ["passed"] = true, ["caseId"] = "wilderness_" + name, ["summary"] = name }); }
                catch (Exception ex) { rows.Add(new Dictionary<string, object> { ["passed"] = false, ["caseId"] = "wilderness_" + name, ["summary"] = ex.ToString() }); }
            }
            var participants = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["heroStringId"] = "wilderness_a", ["name"] = "Ulagara", ["cultureName"] = "Khuzait" },
                new Dictionary<string, object> { ["heroStringId"] = "wilderness_b", ["name"] = "Temurtai" },
                new Dictionary<string, object> { ["heroStringId"] = "wilderness_c", ["name"] = "Hulara" }
            };
            Dictionary<string, object> Opening(string approach, string opening) => new Dictionary<string, object>
            {
                ["title"] = "A fresh title", ["approachDescription"] = approach, ["openingText"] = opening,
                ["playerHook"] = "What do you think?", ["participantHeroIds"] = new List<string> { "wilderness_a" }
            };
            Check("rotation_and_serialized_history", () =>
            {
                var random = new Random(90224);
                var recent = new List<Dictionary<string, object>>();
                var seen = new HashSet<string>();
                for (int i = 0; i < 1000; i++)
                {
                    var payload = TryParseJsonObject(Json.Serialize(new Dictionary<string, object> { ["recentEvents"] = recent }));
                    var direction = SelectWildernessDirection(payload, random.Next);
                    Require(!recent.Any(row => ReadString(row, "varietyKey", "") == direction[0]), "A direction repeated inside the last eight openings.");
                    Require(!recent.Take(3).Any(row => WildernessDirections.Single(item => item[0] == ReadString(row, "varietyKey", ""))[1] == direction[1]), "A family repeated inside the last three openings.");
                    seen.Add(direction[0]);
                    recent.Insert(0, new Dictionary<string, object> { ["varietyKey"] = direction[0], ["title"] = "Example " + i });
                    recent = recent.Take(8).ToList();
                }
                Require(seen.Count == 36, "The seeded long run did not reach every scene direction.");
                Require(WildernessDirections.Select(item => item[1]).Distinct().Count() == 12, "The catalog lost theme breadth.");
            });
            Check("native_gender_survives_brief_and_prompt", () =>
            {
                var female = new Dictionary<string, object> { ["heroStringId"] = "female", ["name"] = "Mira", ["isFemale"] = true };
                var male = new Dictionary<string, object> { ["heroStringId"] = "male", ["name"] = "Arven", ["isFemale"] = false };
                var unknown = new Dictionary<string, object> { ["heroStringId"] = "unknown", ["name"] = "Taren" };
                var empty = new Dictionary<string, object>();
                var femaleBrief = WildernessCharacterBrief(empty, female);
                Require(ReadBool(femaleBrief, "isFemale", false) && ReadString(femaleBrief, "pronouns", "") == "she/her", "Female native identity was discarded.");
                Require(WildernessCharacterBrief(empty, male).ContainsKey("isFemale") && ReadString(WildernessCharacterBrief(empty, male), "pronouns", "") == "he/him", "Explicit male identity was lost.");
                Require(!WildernessCharacterBrief(empty, unknown).ContainsKey("isFemale") && ReadString(WildernessCharacterBrief(empty, unknown), "pronouns", "").StartsWith("they/them"), "Unknown gender defaulted to male.");
                foreach (object invalid in new object[] { null, "invalid" })
                {
                    unknown["isFemale"] = invalid;
                    Require(!WildernessCharacterBrief(empty, unknown).ContainsKey("isFemale"), "Invalid identity defaulted to male.");
                }
                unknown.Remove("isFemale");
                var payload = new Dictionary<string, object> { ["wildernessInitiatorHeroStringId"] = "female" };
                string prompt = BuildGeneratedWildernessPrompt("isolated_wilderness_fixture", payload, new List<Dictionary<string, object>> { female, male, unknown }, WildernessDirections[0], (id, p) => empty);
                Require(prompt.Contains("she/her") && prompt.Contains("he/him") && prompt.Contains("they/them") && prompt.Contains("Initiating participant id: female"), "Writer lost identity or initiator.");
                Require(prompt.Contains("occupation Lord") && prompt.Contains("never override native identity"), "Writer rule permits title/name to override identity.");
            });
            Check("saved_event_writer_keeps_native_pronouns_over_old_prose", () =>
            {
                var empty = new Dictionary<string, object>();
                var female = new Dictionary<string, object> { ["name"] = "Mira", ["occupation"] = "Lord", ["isFemale"] = true };
                string foundation = BuildStableCharacterPrompt("female", "Mira", female, empty);
                Require(foundation.Contains("female; she/her") && foundation.Contains("earlier event descriptions and transcript wording never override native identity"), "Saved scene wording or a Lord title can override native female identity.");
                female["isFemale"] = false;
                Require(BuildStableCharacterPrompt("male", "Mira", female, empty).Contains("male; he/him"), "Known male foundation lost native pronouns.");
                foreach (object unknown in new object[] { null, "invalid" })
                {
                    female["isFemale"] = unknown;
                    Require(BuildStableCharacterPrompt("unknown", "Mira", female, empty).Contains("unknown; they/them"), "Invalid foundation identity defaulted to male.");
                }
                female.Remove("isFemale");
                Require(BuildStableCharacterPrompt("missing", "Mira", female, empty).Contains("unknown; they/them"), "Missing foundation identity defaulted to male.");
            });
            Check("obvious_named_gender_conflict_rejected_without_rewriting", () =>
            {
                var female = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["heroStringId"] = "wilderness_a", ["name"] = "Mira", ["isFemale"] = true } };
                var bad = Opening("Mira has reined his horse to a walk.", "A fox rests in the grass.");
                string before = Json.Serialize(bad);
                Require(WildernessOpeningRejection(bad, new Dictionary<string, object>(), female) == "participant_gender_conflict", "Wrong native gender was accepted.");
                Require(Json.Serialize(bad) == before, "Guard rewrote provider text.");
                Require(WildernessOpeningRejection(Opening("Mira has reined her horse to a walk.", "A fox rests in the grass."), new Dictionary<string, object>(), female) == "", "Correct female pronoun rejected.");
                Require(WildernessOpeningRejection(Opening("Mira has reined their horse to a walk.", "A fox rests in the grass."), new Dictionary<string, object>(), female) == "", "Neutral pronoun rejected.");
                Require(WildernessOpeningRejection(Opening("Mira reads a note: \"Mira has reined his horse.\"", "A fox rests in the grass."), new Dictionary<string, object>(), female) == "", "Quoted material treated as native narration.");
                var male = female.Select(p => new Dictionary<string, object>(p) { ["isFemale"] = false }).ToList();
                Require(WildernessOpeningRejection(Opening("Mira raises her hand.", "A fox rests in the grass."), new Dictionary<string, object>(), male) == "participant_gender_conflict", "Male identity control missed.");
                var unknown = female.Select(p => new Dictionary<string, object> { ["heroStringId"] = "wilderness_a", ["name"] = "Mira" }).ToList();
                Require(WildernessOpeningRejection(bad, new Dictionary<string, object>(), unknown) == "", "Unknown gender was invented.");
                var hidden = Opening("Mira has reined her horse to a walk.", "A fox rests in the grass."); hidden["hiddenContext"] = "Mira folds his arms.";
                Require(WildernessOpeningRejection(hidden, new Dictionary<string, object>(), female) == "participant_gender_conflict", "Hidden scene context can poison later writer.");
            });
            Check("legacy_missing_and_unknown_keys", () =>
            {
                foreach (string key in new[] { "", "retired_direction" })
                {
                    var payload = new Dictionary<string, object> { ["recentEvents"] = new[] { new Dictionary<string, object> { ["varietyKey"] = key, ["openingText"] = "The old road falls quiet." } } };
                    Require(SelectWildernessDirection(payload, count => count - 1) != null, "Legacy history blocked selection.");
                }
                Require(SelectWildernessDirection(new Dictionary<string, object>(), count => 0) != null, "A new campaign cannot select an event.");
            });
            Check("large_character_stack_is_projected_not_copied", () =>
            {
                string large = new string('x', 110000);
                var stack = new Dictionary<string, object>
                {
                    ["constructed"] = new Dictionary<string, object> { ["duplicatedProfile"] = large },
                    ["dynamicCharacteristics"] = new Dictionary<string, object> { ["history"] = large },
                    ["narrative"] = new Dictionary<string, object> { ["fullNarrative"] = large },
                    ["traits"] = new Dictionary<string, object> { ["basePersonalitySummary"] = "Curious, frank and playful." },
                    ["background"] = new Dictionary<string, object> { ["summary"] = "A Khuzait noble from Akkalat." }
                };
                string before = Json.Serialize(stack);
                int loads = 0;
                string prompt = BuildGeneratedWildernessPrompt("isolated_wilderness_fixture", new Dictionary<string, object>(), participants,
                    WildernessDirections[0], (id, person) => { loads++; return stack; });
                Require(loads == 3 && prompt.Length < 15000, "Three 330k-character stacks exceeded the opening budget.");
                Require(prompt.Contains("Curious, frank and playful.") && prompt.Contains("Akkalat"), "Compact context discarded relevant character grounding.");
                Require(!prompt.Contains(large.Substring(0, 3000)) && !prompt.Contains("duplicatedProfile"), "A full profile copy escaped projection.");
                Require(Json.Serialize(stack) == before, "Projection mutated the stored profile fixture.");
            });
            Check("unicode_escaping_and_history_are_bounded", () =>
            {
                string huge = new string('\u754c', 120000);
                var fields = Enumerable.Range(0, 100).ToDictionary(i => "field" + i, i => (object)huge);
                var stack = new Dictionary<string, object>
                {
                    ["traits"] = new Dictionary<string, object> { ["basePersonalitySummary"] = huge, ["traitPercentages"] = fields },
                    ["background"] = new Dictionary<string, object> { ["summary"] = huge },
                    ["motivations"] = fields, ["voice"] = fields, ["pressure"] = fields
                };
                var payload = new Dictionary<string, object>
                {
                    ["playerName"] = huge, ["terrainKey"] = huge, ["locationText"] = huge, ["timeOfDayText"] = huge,
                    ["externalHeroId"] = huge, ["externalContext"] = huge,
                    ["recentEvents"] = Enumerable.Range(0, 100).Select(i => new Dictionary<string, object>
                    { ["title"] = huge, ["approachDescription"] = huge, ["openingText"] = huge }).ToList()
                };
                int loads = 0;
                var largeParticipants = participants.Concat(participants).Select(person => new Dictionary<string, object>(person)
                { ["name"] = huge, ["cultureName"] = huge, ["generatedRole"] = huge }).ToList();
                string prompt = BuildGeneratedWildernessPrompt("isolated_wilderness_fixture", payload, largeParticipants,
                    WildernessDirections[1], (id, person) => { loads++; return stack; });
                var envelope = BuildSimplePromptEnvelope("generated_wilderness_event", "opening", "Output valid JSON.", prompt);
                string request = Json.Serialize(new Dictionary<string, object> { ["messages"] = envelope.Messages, ["promptEnvelope"] = envelope.Diagnostics });
                Require(loads == 3 && WildernessRecentEvents(payload).Count == 8, "Unbounded history or participant input reached the prompt.");
                Require(prompt.Length < 180000 && request.Length < 220000, "Unicode serialization exceeded the conservative opening allowance: " + prompt.Length + "/" + request.Length);
            });
            Check("recent_premises_and_distinct_direction_reach_prompt", () =>
            {
                var payload = new Dictionary<string, object> { ["terrainKey"] = "water", ["timeOfDayText"] = "night",
                    ["recentEvents"] = new[] { Opening("Ulagara weaves a saddle blanket.", "Temurtai folds her arms and watches her sister's weaving.") } };
                string prompt = BuildGeneratedWildernessPrompt("isolated_wilderness_fixture", payload, participants.Take(1).ToList(),
                    WildernessDirections[15], (id, person) => new Dictionary<string, object>());
                Require(prompt.Contains("saddle blanket") && prompt.Contains("game_riddle") && prompt.Contains("night")
                    && prompt.Contains("travel by water") && prompt.Contains("no secret or distress"), "The opening lost history, selected direction, terrain/time or emotional range.");
            });
            Check("exact_repetition_ignores_title_case_and_punctuation", () =>
            {
                var prior = Opening("A companion begins a riddle beside the road.", "She asks which traveler has no feet, and waits for a guess.");
                var payload = new Dictionary<string, object> { ["recentEvents"] = new[] { prior } };
                var next = Opening("A COMPANION begins a riddle beside the road!", "An altered continuation.");
                Require(WildernessOpeningRejection(next, payload, participants) == "repeated_opening", "A retitled repeated approach was accepted.");
            });
            Check("rephrased_high_overlap_is_rejected", () =>
            {
                var prior = Opening("Ulagara weaves a red wool saddle blanket, pulling loose thread through a crooked pattern.",
                    "Temurtai watches her sister's weaving with folded arms. An unfinished border hangs between their saddles, and a quiet disagreement waits behind their silence.");
                var next = Opening("The red wool saddle blanket lies across Ulagara's knee as she pulls loose thread through the crooked pattern.",
                    "With folded arms Temurtai watches her sister weaving. Their silence leaves a quiet disagreement behind an unfinished border hanging between the saddles.");
                Require(WildernessOpeningRejection(next, new Dictionary<string, object> { ["recentEvents"] = new[] { prior } }, participants) == "repeated_premise_words", "The captured style of blanket premise repetition was accepted.");
            });
            Check("same_company_and_setting_allow_new_activity", () =>
            {
                var prior = Opening("Ulagara weaves a saddle blanket beside the woodland road.", "Temurtai watches with folded arms and a tense expression.");
                var next = Opening("Ulagara taps an uneven rhythm beside the woodland road.", "Temurtai attempts to repeat it, misses a beat, and laughs as she invites a better performance.");
                Require(WildernessOpeningRejection(next, new Dictionary<string, object> { ["recentEvents"] = new[] { prior } }, participants) == "", "A distinct activity with the same company/scenery was rejected.");
            });
            Check("incomplete_and_foreign_participant_responses_fail", () =>
            {
                var next = Opening("A concrete incident.", "");
                Require(WildernessOpeningRejection(next, new Dictionary<string, object>(), participants) == "incomplete_opening", "Missing opening accepted.");
                next["openingText"] = "A question is posed.";
                next["participantHeroIds"] = new[] { "invented_person" };
                Require(WildernessOpeningRejection(next, new Dictionary<string, object>(), participants) == "no_eligible_participants", "Invented participants accepted.");
            });
            Check("long_identical_opening_is_rejected", () =>
            {
                var prior = Opening(string.Join(" ", Enumerable.Repeat("A familiar approach to the scenery.", 35)), "A valid opening.");
                var next = Opening(ReadString(prior, "approachDescription", ""), "Different final words.");
                Require(WildernessOpeningRejection(next, new Dictionary<string, object> { ["recentEvents"] = new[] { prior } }, participants) == "repeated_opening", "History excerpt bounds hid an exact repeated opening.");
            });
            Check("client_saved_history_and_failed_generation_contract", () =>
            {
                string client = Path.Combine(FindVerificationSourceRoot(), "ReignBeta");
                string behavior = File.ReadAllText(Path.Combine(client, "src/Modules/WorldSimulation/Campaign/ReignSocialEventsCampaignBehavior.cs"));
                string record = File.ReadAllText(Path.Combine(client, "src/Modules/WorldSimulation/Events/SocialEventRecord.cs"));
                string adapter = File.ReadAllText(Path.Combine(client, "src/Modules/Platform/Integration/ReignServerClient.cs"));
                Require(!behavior.Contains("BuildFallbackGeneratedWildernessScenario") && !behavior.Contains("Something Ahead"), "The repeated success-shaped fallback remains reachable.");
                Require(behavior.Contains("!recentWilderness.Contains(x)") && behavior.Contains("RecentEvents = _events.Where"), "Recent event history is pruned or not projected.");
                Require(System.Text.RegularExpressions.Regex.IsMatch(record, @"\[SaveableField\(27\)\]\s+public string GeneratedVarietyKey;"), "The direction key is not saveable with its additive field id.");
                Require(System.Text.RegularExpressions.Regex.IsMatch(record, @"\[SaveableField\(28\)\]\s+public string GeneratedInitiatorHeroStringId;"), "Initiator lacks additive native save field 28.");
                Require(adapter.Contains("result.InitiatorHeroStringId = response.Value<string>(\"initiatorHeroStringId\")")
                    && adapter.Contains("[\"wildernessInitiatorHeroStringId\"] = record.IsGeneratedWildernessEvent")
                    && behavior.Contains("scenario.InitiatorHeroStringId"), "Initiator is not wired from generator through saved record to speaking floor.");
                var ids = System.Text.RegularExpressions.Regex.Matches(record, @"SaveableField\((\d+)\)").Cast<System.Text.RegularExpressions.Match>().Select(match => match.Groups[1].Value).ToList();
                Require(ids.Distinct().Count() == ids.Count, "A saveable field id was reused.");
                Require(adapter.Contains("[JsonProperty(\"varietyKey\")]") && adapter.Contains("payload[\"recentEvents\"] = JArray.FromObject"), "The wire history no longer has the server's expected names.");
            });
            return rows;
        }
    }
}
