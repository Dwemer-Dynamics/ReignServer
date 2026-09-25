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
                var ids = System.Text.RegularExpressions.Regex.Matches(record, @"SaveableField\((\d+)\)").Cast<System.Text.RegularExpressions.Match>().Select(match => match.Groups[1].Value).ToList();
                Require(ids.Distinct().Count() == ids.Count, "A saveable field id was reused.");
                Require(adapter.Contains("[JsonProperty(\"varietyKey\")]") && adapter.Contains("payload[\"recentEvents\"] = JArray.FromObject"), "The wire history no longer has the server's expected names.");
            });
            return rows;
        }
    }
}
