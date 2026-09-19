using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal sealed class FinalGauntletDiscovery
    {
        public List<FinalGauntletCaseDescriptor> GeneratedCases =
            new List<FinalGauntletCaseDescriptor>();
        public List<string> MissingCoverage = new List<string>();
    }

    internal static partial class FinalConversationGauntletCatalog
    {
        internal static List<FinalGauntletCaseDescriptor> BuildApprovedCatalog()
        {
            List<FinalGauntletCaseDescriptor> result =
                new List<FinalGauntletCaseDescriptor>();
            foreach (KeyValuePair<string, string[]> family in ApprovedScenarioTitles)
            {
                for (int index = 0; index < family.Value.Length; index++)
                {
                    string caseId = family.Key + "-" + (index + 1).ToString("000");
                    result.Add(BuildApprovedDescriptor(
                        caseId,
                        family.Key,
                        family.Value[index]));
                }
            }
            List<string> errors = FinalGauntletContracts.ValidateDescriptors(
                result,
                FinalGauntletContracts.CurrentFixtureSchemaVersion);
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "Approved final-gauntlet catalog is invalid: "
                    + string.Join(" ", errors));
            return result;
        }

        internal static List<FinalGauntletCaseDescriptor>
            BuildExecutableCatalog(
                IReadOnlyCollection<string> registeredActions)
        {
            List<FinalGauntletCaseDescriptor> catalog =
                BuildApprovedCatalog();
            FinalGauntletDiscovery discovery =
                DiscoverFinalGauntletCoverage(
                    registeredActions,
                    new[]
                    {
                        "Honor", "Boldness", "Mercy", "Generosity",
                        "Calculating", "Ambition", "Patience",
                        "Flirtatiousness", "Confidence"
                    },
                    new[]
                    {
                        "Empire", "Vlandia", "Sturgia", "Battania",
                        "Aserai", "Khuzait", "Nord"
                    },
                    new[]
                    {
                        "individual_chat", "party_chat", "social_event",
                        "wilderness_event", "correspondence"
                    },
                    new[]
                    {
                        "ruler", "lord", "lady", "wanderer", "notable",
                        "governor", "ambassador", "prisoner", "companion"
                    },
                    new[]
                    {
                        "hero", "settlement", "clan", "kingdom",
                        "party", "item", "action"
                    },
                    new[]
                    {
                        "Favored", "Flirt", "Promiscuous", "Unchaste"
                    });
            if (discovery.MissingCoverage.Count > 0)
                throw new InvalidOperationException(
                    "Runtime gauntlet coverage is incomplete: "
                    + string.Join(" ", discovery.MissingCoverage));
            catalog.AddRange(discovery.GeneratedCases);
            List<string> errors = FinalGauntletContracts.ValidateDescriptors(
                catalog,
                FinalGauntletContracts.CurrentFixtureSchemaVersion);
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "Executable final-gauntlet catalog is invalid: "
                    + string.Join(" ", errors));
            FinalConversationGauntletCoverage.ClassifyCatalog(catalog);
            return catalog;
        }

        internal static FinalGauntletDiscovery DiscoverFinalGauntletCoverage(
            IReadOnlyCollection<string> registeredActions,
            IReadOnlyCollection<string> traits,
            IReadOnlyCollection<string> cultures,
            IReadOnlyCollection<string> modes,
            IReadOnlyCollection<string> occupations,
            IReadOnlyCollection<string> resolverTypes,
            IReadOnlyCollection<string> rumorDefinitions)
        {
            FinalGauntletDiscovery result = new FinalGauntletDiscovery();
            RequireRegistry(result, "actions", registeredActions);
            RequireRegistry(result, "traits", traits);
            RequireRegistry(result, "cultures", cultures);
            RequireRegistry(result, "modes", modes);
            RequireRegistry(result, "occupations", occupations);
            RequireRegistry(result, "resolvers", resolverTypes);
            RequireRegistry(result, "rumors", rumorDefinitions);

            AddRegistryCases(result.GeneratedCases, "action", registeredActions);
            AddRegistryCases(result.GeneratedCases, "trait", traits);
            AddRegistryCases(result.GeneratedCases, "culture", cultures);
            AddRegistryCases(result.GeneratedCases, "mode", modes);
            AddRegistryCases(result.GeneratedCases, "occupation", occupations);
            AddRegistryCases(result.GeneratedCases, "resolver", resolverTypes);
            AddRegistryCases(result.GeneratedCases, "rumor", rumorDefinitions);
            AddActionRequirementMatrix(result.GeneratedCases, registeredActions);
            AddPairwiseMatrix(result.GeneratedCases);
            AddHonorBoldnessMatrix(result.GeneratedCases);
            return result;
        }

        private static FinalGauntletCaseDescriptor BuildApprovedDescriptor(
            string caseId,
            string family,
            string title)
        {
            string normalizedFamily = FamilyName(family);
            string evaluation = EvaluationKind(family);
            string execution = ExecutionKind(family);
            return new FinalGauntletCaseDescriptor
            {
                CaseId = caseId,
                Family = normalizedFamily,
                EvaluationKind = evaluation,
                ExecutionKind = execution,
                Mode = DefaultMode(family),
                RequiresProvider = RequiresProvider(family),
                RequiresGame = RequiresGame(family),
                RequirementIds = new[] { caseId },
                Tags = new[]
                {
                    "approved",
                    "family:" + normalizedFamily,
                    "title:" + Slug(title)
                },
                BehavioralRequirement =
                    "Verify " + title + " through the production conversation path, "
                    + "using authoritative state and preserving the NPC's identity, knowledge boundary, and agency.",
                HardProhibitions = FamilyProhibitions(family),
                PrerequisiteCapabilities = FamilyPrerequisites(family),
                EvidenceNeeds = FamilyEvidence(family)
            };
        }

        private static void RequireRegistry(
            FinalGauntletDiscovery discovery,
            string name,
            IReadOnlyCollection<string> values)
        {
            if (values == null || values.Count == 0)
                discovery.MissingCoverage.Add(
                    "Runtime " + name + " registry is empty or unavailable.");
        }

        private static void AddRegistryCases(
            List<FinalGauntletCaseDescriptor> target,
            string kind,
            IReadOnlyCollection<string> values)
        {
            int index = 0;
            foreach (string value in (values ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(GeneratedDescriptor(
                    "DISC-" + kind.ToUpperInvariant() + "-" + (++index).ToString("000"),
                    "runtime_discovery",
                    "Prove runtime " + kind + " '" + value + "' has generated coverage.",
                    new[] { "runtime_discovery", kind + ":" + value }));
            }
        }

        private static FinalGauntletCaseDescriptor GeneratedDescriptor(
            string caseId,
            string family,
            string requirement,
            string[] tags)
        {
            bool behavioral = string.Equals(
                    family, "pairwise_matrix",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    family, "personality_matrix",
                    StringComparison.OrdinalIgnoreCase);
            return new FinalGauntletCaseDescriptor
            {
                CaseId = caseId,
                Family = family,
                EvaluationKind = "hard",
                ExecutionKind = "generated_fixture",
                Mode = "fixture",
                RequiresProvider = behavioral,
                RequiresGame = false,
                RequirementIds = new[] { caseId },
                Tags = tags ?? Array.Empty<string>(),
                BehavioralRequirement = requirement,
                HardProhibitions = new[] { "Do not silently omit enabled runtime coverage." },
                PrerequisiteCapabilities = new[] { "runtime_registry" },
                EvidenceNeeds = new[] { "registry_snapshot", "generated_fixture" }
            };
        }

        private static string FamilyName(string prefix)
        {
            switch (prefix)
            {
                case "CON": return "output_contract";
                case "IDN": return "identity_biography";
                case "SIT": return "situational_awareness";
                case "WLD": return "world_knowledge";
                case "PER": return "personality_agency";
                case "REL": return "relationship_hierarchy";
                case "ROM": return "romance";
                case "MEM": return "memory";
                case "GRP": return "group_awareness";
                case "RUM": return "rumor_reputation";
                case "ACT": return "action_conformance";
                case "STA": return "state_consequence";
                case "MOD": return "mode_specific";
                case "SOC": return "conflict_manipulation";
                case "ADV": return "adversarial";
                case "ROB": return "robustness";
                case "DIF": return "differential";
                case "STO": return "stochastic";
                case "LNG": return "long_horizon";
                case "GAUNTLET": return "final_scene";
                default: return prefix.ToLowerInvariant();
            }
        }

        private static string EvaluationKind(string family)
        {
            if (family == "DIF") return "differential";
            if (family == "STO") return "statistical";
            if (family == "LNG" || family == "GAUNTLET") return "sequence";
            if (family == "PER" || family == "ROM" || family == "SOC")
                return "semantic_and_hard";
            return "hard_and_semantic";
        }

        private static string ExecutionKind(string family)
        {
            if (family == "RUM" || family == "ACT" || family == "STA")
                return "fixture_and_live";
            if (family == "ROB") return "fault_injection";
            if (family == "DIF" || family == "STO") return "paired_fixture";
            if (family == "LNG" || family == "GAUNTLET")
                return "live_sequence";
            return "production_fixture";
        }

        private static string DefaultMode(string family)
        {
            if (family == "GRP") return "party_chat";
            if (family == "MOD" || family == "GAUNTLET") return "varied";
            if (family == "LNG") return "cross_mode";
            return "individual_chat";
        }

        private static bool RequiresProvider(string family)
        {
            return family != "STA";
        }

        private static bool RequiresGame(string family)
        {
            return family == "SIT" || family == "WLD" || family == "GRP"
                || family == "ACT" || family == "STA" || family == "MOD"
                || family == "LNG" || family == "GAUNTLET";
        }

        private static string[] FamilyProhibitions(string family)
        {
            switch (family)
            {
                case "IDN":
                    return new[] { "Do not swap identities or let stale biography override authoritative identity." };
                case "WLD":
                    return new[] { "Do not replace live world state with player claims, rumors, or stale memories." };
                case "MEM":
                    return new[] { "Do not leak, mis-own, duplicate, or convert uncertain memory into fact." };
                case "GRP":
                    return new[] { "Do not merge participants, borrow voices, or grant knowledge to absent listeners." };
                case "ACT":
                case "STA":
                    return new[] { "Do not execute an unresolved, invalid, ineligible, failed, or duplicate action." };
                case "ADV":
                    return new[] { "Do not expose instructions, mechanics, private reasoning, or bypass validation." };
                default:
                    return new[] { "Do not invent authoritative facts, mechanics, identities, or completed world changes." };
            }
        }

        private static string[] FamilyPrerequisites(string family)
        {
            return new[]
            {
                "production_prompt_builder",
                FamilyName(family),
                "authoritative_state_precedence"
            };
        }

        private static string[] FamilyEvidence(string family)
        {
            return new[]
            {
                "fixture_snapshot",
                "outbound_prompt",
                "raw_and_parsed_model_response",
                "state_before_and_after",
                "source_and_correlation_lineage"
            };
        }

        private static string Slug(string value)
        {
            return new string((value ?? string.Empty)
                .ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '_')
                .ToArray())
                .Trim('_');
        }
    }
}
