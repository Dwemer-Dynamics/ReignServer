using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // This is a requirement graph, not a similarity-based text deduplicator.
        // Scoped rules (different speakers, permissions or exceptions) have different keys.
        private sealed class PromptRuleComposer
        {
            private sealed class Rule
            {
                public string Id, Heading, Text, Source;
                public string[] Dependencies;
            }

            private readonly Dictionary<string, Rule> rules = new Dictionary<string, Rule>(StringComparer.Ordinal);
            private readonly List<string> definitionOrder = new List<string>();
            private readonly List<string> requested = new List<string>();
            private readonly Dictionary<string, HashSet<string>> requesters = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            private readonly List<Dictionary<string, object>> omitted = new List<Dictionary<string, object>>();

            public void Define(string id, string heading, string text, string source, params string[] dependencies)
            {
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException("A required prompt rule is empty: " + id);
                var rule = new Rule { Id = id, Heading = heading, Text = NormalizePromptSegment(text), Source = source, Dependencies = dependencies ?? new string[0] };
                if (rules.TryGetValue(id, out Rule previous))
                {
                    if (previous.Text != rule.Text || previous.Heading != heading || !previous.Dependencies.SequenceEqual(rule.Dependencies))
                        throw new InvalidOperationException("Conflicting prompt rule definition: " + id);
                    return;
                }
                rules.Add(id, rule);
                definitionOrder.Add(id);
            }

            public void Require(string id, string layer)
            {
                if (!requesters.TryGetValue(id, out HashSet<string> layers))
                {
                    requesters.Add(id, layers = new HashSet<string>(StringComparer.Ordinal));
                    requested.Add(id);
                }
                layers.Add(layer);
            }

            public void Add(string heading, string text, string source = null)
            {
                string id = PromptRuleId(heading);
                Define(id, heading, text, source ?? PromptRuleSource(heading), PromptRuleDependencies(heading));
                Require(id, heading);
            }

            public void Omit(string id, string reason)
            {
                omitted.Add(new Dictionary<string, object> { ["ruleId"] = id, ["reason"] = reason });
            }

            public string Render(Dictionary<string, object> diagnostics = null)
            {
                var complete = new HashSet<string>(StringComparer.Ordinal);
                var visiting = new HashSet<string>(StringComparer.Ordinal);
                var ordered = new List<Rule>();
                Action<string, string> visit = null;
                visit = (id, parent) =>
                {
                    if (!rules.TryGetValue(id, out Rule rule))
                        throw new InvalidOperationException("Missing required prompt rule " + id + " for " + parent);
                    if (!requesters.TryGetValue(id, out HashSet<string> layers))
                        requesters[id] = layers = new HashSet<string>(StringComparer.Ordinal);
                    layers.Add(parent);
                    if (complete.Contains(id)) return;
                    if (!visiting.Add(id)) throw new InvalidOperationException("Cyclic prompt rule dependency: " + id);
                    foreach (string dependency in rule.Dependencies) visit(dependency, id);
                    visiting.Remove(id);
                    complete.Add(id);
                    ordered.Add(rule);
                };
                foreach (string id in requested) visit(id, "selected_layer");
                // Dependencies determine inclusion, not instruction precedence. Preserve
                // the declared production order (notably tone followed by fact limits).
                ordered = ordered.OrderBy(rule => definitionOrder.IndexOf(rule.Id)).ToList();
                var builder = new StringBuilder();
                foreach (Rule rule in ordered) AppendPromptSection(builder, rule.Heading, rule.Text);
                if (diagnostics != null)
                {
                    diagnostics["schema"] = "reign-prompt-composition-v1";
                    diagnostics["complete"] = true;
                    diagnostics["rules"] = ordered.Select(rule => new Dictionary<string, object>
                    {
                        ["ruleId"] = rule.Id, ["source"] = rule.Source,
                        ["version"] = PromptHash(rule.Text), ["characters"] = rule.Text.Length,
                        ["estimatedTokens"] = (int)Math.Ceiling(rule.Text.Length / 4d),
                        ["requiredBy"] = requesters[rule.Id].OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                        ["dependencies"] = rule.Dependencies, ["copies"] = 1
                    }).ToList();
                    diagnostics["omitted"] = omitted;
                    diagnostics["uniqueRuleCharacters"] = ordered.Sum(rule => rule.Text.Length);
                    diagnostics["policy"] = "Authoritative applicability only; unknown means include. Observer-specific data is never deduplicated across subjects.";
                }
                return builder.ToString();
            }
        }

        private static void AddPromptFactBoundary(PromptRuleComposer builder)
        {
            builder.Add("AUTHORITATIVE FACT BOUNDARY",
                "Plausibility is not evidence. Treat only supplied native context, attributed memory, transcript lines, and explicit soft personal canon as available facts. "
                + "Never invent current weather, road conditions, market stock, prices, prosperity, loyalty, security, militia, garrison readiness, troop movements, settlement conditions, ownership, diplomacy, nearby parties, or recent events. "
                + "A numeric prosperity, loyalty, security, militia, or garrison value supports only that value and a cautious characterization of it; it does not prove that markets are open, granaries are stocked, patrols are active, soldiers are ready, roads are safe, or any other specific cause or visible condition. "
                + "If the requested current fact is not supplied, say this character cannot presently verify it, or offer an explicitly labeled opinion, rumor, conditional, or general experience that does not assert a current condition. "
                + "Atmospheric description and medieval flavor may not smuggle in unsupplied world-state claims. decisionBrief.facts must likewise contain only supplied or explicitly attributed evidence.");
        }

        private static string PromptRuleId(string heading)
            => heading.ToLowerInvariant().Replace(" - ", ".").Replace(" ", "_");

        private static bool? NativeNoblePromptApplicability(Dictionary<string, object> profile)
        {
            if (profile != null && profile.TryGetValue("isLord", out object value)
                && bool.TryParse(Convert.ToString(value), out bool isLord)) return isLord;
            if (profile != null && UsesCommonerRolePrompt(profile)) return false;
            return null;
        }

        private static string[] PromptRuleDependencies(string heading)
        {
            // A and B each retain the dependency even when they are not selected together.
            switch (heading)
            {
                case "WORLD TONE":
                case "NOBLE PROMPT":
                case "AUTHORITATIVE OFFICE ROLE - NON-OVERRIDABLE":
                case "MEMORY WRITE RULES":
                case "VISIBLE REPLY RULES":
                case "SCENE STATE OUTPUT":
                    return new[] { "authoritative_fact_boundary" };
                case "ACTION GATE RULES":
                    return new[] { "authoritative_fact_boundary", "action_routing_boundary" };
                default: return new string[0];
            }
        }

        private static string PromptRuleSource(string heading)
        {
            switch (heading)
            {
                case "INDIVIDUAL DIALOGUE ENGINE": return "dialogue_system.txt";
                case "SOCIAL EVENT ENGINE": return "event_system.txt";
                case "CORRESPONDENCE ENGINE": return "correspondence_system.txt";
                case "HIDDEN ACTION PLANNER": return "action_planner_system.txt";
                case "WORLD TONE": return "world_tone.txt";
                case "NOBLE PROMPT": return "noble_prompt.txt";
                case "GLOBAL RESPONSE OVERRIDE": return "global_response_override.txt";
                case "ACTION GATE RULES": return "action_suggestion_rules.txt";
                case "MEMORY WRITE RULES": return "memory_write_rules.txt";
                default: return "production_contract:" + PromptRuleId(heading);
            }
        }

        private static Dictionary<string, object> PromptCompositionInventory()
        {
            string[] headings = { "WORLD TONE", "NOBLE PROMPT", "AUTHORITATIVE OFFICE ROLE - NON-OVERRIDABLE", "MEMORY WRITE RULES", "VISIBLE REPLY RULES", "SCENE STATE OUTPUT", "ACTION GATE RULES" };
            return new Dictionary<string, object>
            {
                ["schema"] = "reign-prompt-composition-v1",
                ["priority"] = "character_accuracy_before_token_count",
                ["unknownApplicability"] = "include",
                ["rules"] = headings.Select(heading => new Dictionary<string, object>
                {
                    ["ruleId"] = PromptRuleId(heading), ["source"] = PromptRuleSource(heading),
                    ["dependencies"] = PromptRuleDependencies(heading)
                }).ToList(),
                ["routes"] = new[] { "individual", "homes", "castle", "party", "social", "court", "ambassador", "correspondence", "action_planner" },
                ["sharedRuleScope"] = "policy only; character foundation, observer knowledge and scene evidence remain attributed",
                ["cachePolicy"] = "self-contained requests, stable prefixes, subscription-safe implicit caching; unavailable usage is unreported"
            };
        }
    }
}
