using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        /// <summary>
        /// Provider-free contracts for the Codex conversation experiments.
        /// These checks deliberately exercise schema selection, prompt mapping,
        /// thread identity, and bounded preparation without starting a Codex
        /// process or writing campaign state.
        /// </summary>
        private static List<Dictionary<string, object>> RunCodexConversationSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            AddCodexSelfTest(results, "options_default_off", TestCodexOptions());
            AddCodexSelfTest(results, "standard_schema_contracts", TestStandardSchemas());
            AddCodexSelfTest(results, "optional_null_semantics", TestOptionalNullSemantics());
            AddCodexSelfTest(results, "optional_null_normalization", TestOptionalNullNormalization());
            AddCodexSelfTest(results, "built_in_example_conformance", TestBuiltInExampleConformance());
            AddCodexSelfTest(results, "custom_contract_fallback", TestCustomFallback());
            AddCodexSelfTest(results, "schema_applicability_guard", TestSchemaApplicabilityGuard());
            AddCodexSelfTest(results, "prompt_segment_mapping", TestPromptSegments());
            AddCodexSelfTest(results, "compact_metadata_semantics", TestCompactMetadata());
            AddCodexSelfTest(results, "thread_identity_safety", TestThreadIdentity());
            AddCodexSelfTest(results, "parallel_preparation_bound", TestParallelPreparation());
            AddCodexSelfTest(results, "trusted_authority_hash", TestTrustedAuthorityHash());
            return results;
        }

        private static void AddCodexSelfTest(
            List<Dictionary<string, object>> results,
            string name,
            Dictionary<string, object> test)
        {
            test = test ?? new Dictionary<string, object>();
            test["name"] = name;
            test["id"] = "codex_conversation_" + name;
            test["passed"] = ReadBool(test, "ok", false);
            test["summary"] = ReadString(test, "detail", "");
            results.Add(test);
        }

        private static Dictionary<string, object> TestCodexOptions()
        {
            Dictionary<string, object> options = CodexConversationContracts.DefaultOptions();
            string[] flags =
            {
                "structuredOutputs", "compactMetadata", "stablePromptMapping",
                "parallelContextPreparation", "reuseThreads", "asyncThreadCleanup", "fastMode"
            };
            bool allOff = flags.All(key => !ReadBool(options, key, true));
            return SelfTest(allOff
                && ReadString(options, "schema", "") == CodexConversationContracts.OptionsSchema
                && ReadString(options, "reasoningMode", "") == "selective",
                allOff ? "all Codex experiment switches default to false" : "a new Codex option defaulted on",
                new Dictionary<string, object> { ["schema"] = ReadString(options, "schema", ""), ["flagsOff"] = allOff });
        }

        private static Dictionary<string, object> TestStandardSchemas()
        {
            Dictionary<string, string> defaults = DefaultPromptTemplates();
            var checks = new List<Dictionary<string, object>>();
            bool all = true;
            foreach (CodexConversationContractKind kind in new[]
            {
                CodexConversationContractKind.Individual,
                CodexConversationContractKind.Group,
                CodexConversationContractKind.Castle,
                CodexConversationContractKind.Social,
                CodexConversationContractKind.Correspondence
            })
            {
                var templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in CodexConversationSchemas.TemplateNames(kind))
                    templates[name] = defaults.ContainsKey(name) ? defaults[name] : "";
                CodexConversationSchemaSelection selection = CodexConversationSchemas.Select(
                    kind, templates, defaults, new Dictionary<string, object>());
                Dictionary<string, object> properties = selection.Schema == null
                    ? null
                    : ReadDictionary(selection.Schema, "properties");
                bool strict = CheckStrictSchema(selection.Schema, out string strictError);
                bool fields = properties != null
                    && properties.ContainsKey("reply") == (kind != CodexConversationContractKind.Correspondence)
                    && properties.ContainsKey(kind == CodexConversationContractKind.Correspondence ? "shouldReply" : "actionGate")
                    && selection.OutputSchema != null
                    && ReadString(selection.OutputSchema, "name", "") == selection.SchemaId;
                if (kind == CodexConversationContractKind.Correspondence)
                    fields = fields && properties.ContainsKey("campaignOrder");
                else
                    fields = fields && properties.ContainsKey("relationshipAssessments")
                        && properties.ContainsKey("memoryWrites")
                        && properties.ContainsKey("sceneStateUpdates")
                        && properties.ContainsKey("dynamicCharacteristicWrites");
                bool ok = selection.BuiltInContract && selection.Applicability == "applicable" && fields && strict;
                all &= ok;
                checks.Add(new Dictionary<string, object>
                {
                    ["mode"] = selection.Mode, ["ok"] = ok,
                    ["schemaId"] = selection.SchemaId, ["fieldCount"] = properties == null ? 0 : properties.Count,
                    ["strictError"] = strictError
                });
            }
            return SelfTest(all, all ? "all five standard contracts have complete JSON Schema envelopes" : "a standard contract schema is incomplete",
                new Dictionary<string, object> { ["modes"] = checks });
        }

        private static bool CheckStrictSchema(Dictionary<string, object> schema, out string error)
        {
            error = "";
            if (schema == null)
            {
                error = "schema missing";
                return false;
            }
            if (SchemaTypeIncludes(schema, "object"))
            {
                Dictionary<string, object> properties = ReadDictionary(schema, "properties") ?? new Dictionary<string, object>();
                if (!ReadBool(schema, "additionalProperties", false))
                {
                    List<string> required = ReadStringList(schema, "required");
                    if (required.Count != properties.Count || properties.Keys.Any(key => !required.Contains(key, StringComparer.OrdinalIgnoreCase)))
                    {
                        error = "object required list does not cover every property";
                        return false;
                    }
                }
                foreach (KeyValuePair<string, object> property in properties)
                    if (!CheckStrictSchema(property.Value as Dictionary<string, object>, out error)) return false;
            }
            Dictionary<string, object> items = SchemaTypeIncludes(schema, "array")
                ? ReadDictionary(schema, "items")
                : null;
            return items == null || CheckStrictSchema(items, out error);
        }

        private static Dictionary<string, object> TestCustomFallback()
        {
            Dictionary<string, string> defaults = DefaultPromptTemplates();
            var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in CodexConversationSchemas.TemplateNames(CodexConversationContractKind.Individual))
                actual[name] = defaults[name];
            actual["dialogue_system.txt"] += "\nCUSTOM CONTRACT MARKER";
            CodexConversationSchemaSelection selection = CodexConversationSchemas.Select(
                CodexConversationContractKind.Individual, actual, defaults, new Dictionary<string, object>());
            bool ok = !selection.BuiltInContract
                && selection.Applicability == "schema not applicable"
                && selection.Schema == null
                && !string.IsNullOrWhiteSpace(selection.Reason)
                && selection.PreservedFields.Contains("memoryWrites");
            return SelfTest(ok, ok ? "custom templates use explicit legacy fallback" : "custom template was forced into a smaller schema",
                new Dictionary<string, object> { ["reason"] = selection.Reason, ["preservedFields"] = selection.PreservedFields });
        }

        private static Dictionary<string, object> TestSchemaApplicabilityGuard()
        {
            bool specializedUnknown = CodexConversationContracts.NormalizeMode(
                "court_life_classifier", new Dictionary<string, object>()) == CodexConversationContractKind.Unknown;
            bool supportedCourt = CodexConversationContracts.NormalizeMode(
                "court_life", new Dictionary<string, object>()) == CodexConversationContractKind.Individual;
            CodexConversationSchemaSelection arbitrary = ResolveCodexConversationSchema(
                "dialogue", new Dictionary<string, object> { ["prompt"] = "arbitrary caller text" });
            Dictionary<string, string> defaults = DefaultPromptTemplates();
            var templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in CodexConversationSchemas.TemplateNames(CodexConversationContractKind.Individual))
                templates[name] = defaults.ContainsKey(name) ? defaults[name] : "";
            CodexConversationSchemaSelection customized = CodexConversationSchemas.Select(
                CodexConversationContractKind.Individual, templates, defaults,
                new Dictionary<string, object> { ["guardedPromptOverride"] = "custom prompt" });
            bool ok = specializedUnknown && supportedCourt
                && !arbitrary.BuiltInContract && arbitrary.Schema == null
                && customized.Schema == null && !customized.BuiltInContract;
            return SelfTest(ok,
                ok ? "unknown specialized modes and untrusted direct requests use explicit schema fallback"
                    : "schema applicability admitted an unknown mode, arbitrary request, or customized contract",
                new Dictionary<string, object>
                {
                    ["specializedUnknown"] = specializedUnknown,
                    ["supportedCourt"] = supportedCourt,
                    ["arbitraryReason"] = arbitrary.Reason,
                    ["customizedReason"] = customized.Reason
                });
        }

        private static Dictionary<string, object> TestOptionalNullSemantics()
        {
            Dictionary<string, string> defaults = DefaultPromptTemplates();
            var templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in CodexConversationSchemas.TemplateNames(CodexConversationContractKind.Individual))
                templates[name] = defaults[name];
            CodexConversationSchemaSelection selection = CodexConversationSchemas.Select(
                CodexConversationContractKind.Individual, templates, defaults, new Dictionary<string, object>());
            Dictionary<string, object> properties = ReadDictionary(selection.Schema, "properties")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> stateUpdates = ReadDictionary(properties, "stateUpdates")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> stateProperties = ReadDictionary(stateUpdates, "properties")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> dynamicItem = ReadDictionary(
                ReadDictionary(properties, "dynamicCharacteristicWrites"), "items")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> dynamicProperties = ReadDictionary(dynamicItem, "properties")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> development = ReadDictionary(dynamicProperties, "narrativeDevelopment")
                ?? new Dictionary<string, object>();
            var chancellorPayload = new Dictionary<string, object>
            {
                ["chancellorOfficeContext"] = new Dictionary<string, object> { ["enabled"] = true }
            };
            CodexConversationSchemaSelection chancellorSelection = CodexConversationSchemas.Select(
                CodexConversationContractKind.Individual, templates, defaults, chancellorPayload);
            Dictionary<string, object> chancellorProperties = ReadDictionary(chancellorSelection.Schema, "properties")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> chancellor = ReadDictionary(chancellorProperties, "chancellorDecision")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> chancellorFields = ReadDictionary(chancellor, "properties")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> salary = ReadDictionary(chancellorFields, "activeDailySalary")
                ?? new Dictionary<string, object>();
            string[] expectedSchemaNulls =
            {
                "stateUpdates.mood", "stateUpdates.currentPlan", "stateUpdates.currentCrisis",
                "dynamicCharacteristicWrites[].narrativeDevelopment"
            };
            bool stateNullable = new[] { "mood", "currentPlan", "currentCrisis" }
                .All(key => IsNullableSchema(ReadDictionary(stateProperties, key)));
            bool developmentNullable = IsNullableSchema(development);
            bool salaryNullable = IsNullableSchema(salary);
            bool pathsMatch = expectedSchemaNulls.SequenceEqual(
                selection.SchemaIntroducedNullPaths ?? new List<string>());
            bool contractPath = (chancellorSelection.ContractDefinedNullPaths ?? new List<string>())
                .SequenceEqual(new[] { "chancellorDecision.activeDailySalary" });
            bool ok = stateNullable && developmentNullable && salaryNullable && pathsMatch && contractPath;
            return SelfTest(ok,
                ok ? "optional omission semantics use explicit nullable paths without fabricating durable values" : "nullable schema paths do not match the legacy contract",
                new Dictionary<string, object>
                {
                    ["schemaIntroducedNullPaths"] = selection.SchemaIntroducedNullPaths,
                    ["contractDefinedNullPaths"] = chancellorSelection.ContractDefinedNullPaths,
                    ["stateNullable"] = stateNullable,
                    ["narrativeDevelopmentNullable"] = developmentNullable,
                    ["salaryNullable"] = salaryNullable
                });
        }

        private static bool IsNullableSchema(Dictionary<string, object> schema)
        {
            if (schema == null) return false;
            if (!schema.TryGetValue("type", out object raw)) return false;
            if (raw is IEnumerable enumerable)
                return enumerable.Cast<object>().Select(Convert.ToString)
                    .Contains("null", StringComparer.OrdinalIgnoreCase);
            return false;
        }

        private static Dictionary<string, object> TestOptionalNullNormalization()
        {
            var original = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["reply"] = "Keep this reply",
                ["stateUpdates"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mood"] = null, ["currentPlan"] = "keep plan", ["currentCrisis"] = null,
                    ["customState"] = null
                },
                ["dynamicCharacteristicWrites"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["text"] = "a durable detail", ["narrativeDevelopment"] = null
                    },
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["text"] = "another detail", ["narrativeDevelopment"] = new Dictionary<string, object>
                        {
                            ["itemId"] = "interest", ["influence"] = 4
                        }
                    }
                },
                ["chancellorDecision"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["activeDailySalary"] = null
                },
                ["unknownNull"] = null
            };
            Dictionary<string, object> normalized = CodexConversationContracts.NormalizeStructuredOptionalNulls(
                "reign-codex-individual-v1", original, out List<string> removed);
            Dictionary<string, object> custom = CodexConversationContracts.NormalizeStructuredOptionalNulls(
                "custom-contract", original, out List<string> customRemoved);
            Dictionary<string, object> state = ReadDictionary(normalized, "stateUpdates")
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> writes = ReadDictionaryList(normalized, "dynamicCharacteristicWrites");
            Dictionary<string, object> second = writes.Count > 1 ? writes[1] : new Dictionary<string, object>();
            bool firstRemoved = writes.Count > 0 && !writes[0].ContainsKey("narrativeDevelopment");
            bool preserved = ReadString(state, "currentPlan", "") == "keep plan"
                && state.ContainsKey("customState") && state["customState"] == null
                && second.ContainsKey("narrativeDevelopment")
                && ReadDictionary(normalized, "chancellorDecision").ContainsKey("activeDailySalary")
                && ReadDictionary(normalized, "chancellorDecision")["activeDailySalary"] == null
                && normalized.ContainsKey("unknownNull") && normalized["unknownNull"] == null
                && ReadDictionary(custom, "stateUpdates").ContainsKey("mood")
                && ReadDictionary(custom, "stateUpdates")["mood"] == null
                && customRemoved.Count == 0
                && ReadString(normalized, "reply", "") == "Keep this reply";
            // The helper reports the dynamic placeholder by its stable path;
            // this keeps diagnostics independent of the array index.
            bool ok = firstRemoved && preserved
                && removed.Count == 3
                && removed.Count(path => path == "dynamicCharacteristicWrites[].narrativeDevelopment") == 1
                && removed.Contains("stateUpdates.mood")
                && removed.Contains("stateUpdates.currentCrisis");
            return SelfTest(ok,
                ok ? "schema placeholders are removed while non-null and contract-defined nulls remain" : "optional null normalization changed a non-placeholder field",
                new Dictionary<string, object>
                {
                    ["removed"] = removed,
                    ["normalized"] = normalized
                });
        }

        private static Dictionary<string, object> TestBuiltInExampleConformance()
        {
            Dictionary<string, string> defaults = DefaultPromptTemplates();
            bool dialogueOk = TryCheckSchemaExample(
                CodexConversationContractKind.Individual, defaults, "dialogue_output_schema.json", out string dialogueError);
            bool eventOk = TryCheckSchemaExample(
                CodexConversationContractKind.Social, defaults, "event_output_schema.json", out string eventError);
            var correspondence = new Dictionary<string, object>
            {
                ["shouldReply"] = true,
                ["body"] = "letter",
                ["reason"] = "reason",
                ["actionGate"] = new Dictionary<string, object>
                {
                    ["needed"] = false, ["commitment"] = "none", ["intent"] = "", ["confidence"] = 0d, ["reason"] = ""
                },
                ["rebellionDecision"] = "none",
                ["campaignOrder"] = new Dictionary<string, object>
                {
                    ["decision"] = "none", ["reason"] = "", ["steps"] = new ArrayList()
                }
            };
            Dictionary<string, string> correspondenceTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in CodexConversationSchemas.TemplateNames(CodexConversationContractKind.Correspondence))
                correspondenceTemplates[name] = defaults[name];
            CodexConversationSchemaSelection correspondenceSelection = CodexConversationSchemas.Select(
                CodexConversationContractKind.Correspondence, correspondenceTemplates, defaults, new Dictionary<string, object>());
            bool correspondenceOk = CheckSchemaExample(
                correspondenceSelection.Schema, correspondence, out string correspondenceError);
            bool ok = dialogueOk && eventOk && correspondenceOk;
            return SelfTest(ok, ok ? "built-in dialogue, event, and correspondence examples fit their raw schemas" : "a built-in example contains an unrepresented field or type",
                new Dictionary<string, object>
                {
                    ["dialogue"] = dialogueError,
                    ["event"] = eventError,
                    ["correspondence"] = correspondenceError
                });
        }

        private static bool TryCheckSchemaExample(
            CodexConversationContractKind kind,
            Dictionary<string, string> defaults,
            string exampleName,
            out string error)
        {
            error = "";
            var templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in CodexConversationSchemas.TemplateNames(kind))
                templates[name] = defaults[name];
            CodexConversationSchemaSelection selection = CodexConversationSchemas.Select(
                kind, templates, defaults, new Dictionary<string, object>());
            Dictionary<string, object> example;
            try
            {
                if (!defaults.TryGetValue(exampleName, out string exampleText))
                {
                    error = "example template missing: " + exampleName;
                    return false;
                }
                example = Json.Deserialize<Dictionary<string, object>>(exampleText);
            }
            catch (Exception ex)
            {
                error = "example parse failed: " + ex.Message;
                return false;
            }
            return CheckSchemaExample(selection.Schema, example, out error);
        }

        private static bool CheckSchemaExample(
            Dictionary<string, object> schema,
            object example,
            out string error)
        {
            error = "";
            if (schema == null)
            {
                error = "schema is missing";
                return false;
            }
            if (example == null)
                return SchemaTypeIncludes(schema, "null");
            if (SchemaTypeIncludes(schema, "object"))
            {
                IDictionary values = example as IDictionary;
                Dictionary<string, object> properties = ReadDictionary(schema, "properties");
                if (values == null || properties == null)
                {
                    error = "expected object with properties";
                    return false;
                }
                foreach (DictionaryEntry entry in values)
                {
                    string key = Convert.ToString(entry.Key) ?? "";
                    if (!properties.ContainsKey(key))
                    {
                        error = "unrepresented field " + key;
                        return false;
                    }
                    if (!CheckSchemaExample(properties[key] as Dictionary<string, object>, entry.Value, out error))
                    {
                        error = key + ": " + error;
                        return false;
                    }
                }
                return true;
            }
            if (SchemaTypeIncludes(schema, "array"))
            {
                IEnumerable values = example as IEnumerable;
                Dictionary<string, object> itemSchema = ReadDictionary(schema, "items");
                if (values == null || itemSchema == null)
                {
                    error = "expected array";
                    return false;
                }
                foreach (object item in values)
                    if (!CheckSchemaExample(itemSchema, item, out error)) return false;
                return true;
            }
            if (SchemaTypeIncludes(schema, "string"))
                return example == null || example is string;
            if (SchemaTypeIncludes(schema, "boolean"))
                return example == null || example is bool;
            if (SchemaTypeIncludes(schema, "integer") || SchemaTypeIncludes(schema, "number"))
                return example == null || example is IConvertible;
            return true;
        }

        private static bool SchemaTypeIncludes(Dictionary<string, object> schema, string expected)
        {
            if (schema == null || string.IsNullOrWhiteSpace(expected)) return false;
            object raw = schema.ContainsKey("type") ? schema["type"] : null;
            if (raw is string text)
                return text.Equals(expected, StringComparison.OrdinalIgnoreCase);
            if (raw is IEnumerable enumerable && !(raw is string))
                return enumerable.Cast<object>().Any(value =>
                    string.Equals(Convert.ToString(value), expected, StringComparison.OrdinalIgnoreCase));
            return false;
        }

        private static Dictionary<string, object> TestPromptSegments()
        {
            List<Dictionary<string, object>> first = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["role"] = "system", ["content"] = "SHARED" },
                new Dictionary<string, object> { ["role"] = "user", ["content"] = "CHARACTER" },
                new Dictionary<string, object> { ["role"] = "user", ["content"] = "TURN ONE" }
            };
            List<Dictionary<string, object>> second = first.Select(CloneDictionary).ToList();
            second[2]["content"] = "TURN TWO";
            List<CodexPromptSegment> a = CodexConversationContracts.BuildPromptSegments(first);
            List<CodexPromptSegment> b = CodexConversationContracts.BuildPromptSegments(second);
            bool stable = a.Count == 3 && b.Count == 3
                && a[0].Stable && a[1].Stable && !a[2].Stable
                && a[0].Hash == b[0].Hash && a[1].Hash == b[1].Hash
                && a[2].Hash != b[2].Hash
                && CodexConversationContracts.StablePromptMapping(a, b, true, true)["cacheUsage"].ToString() == "unknown";
            return SelfTest(stable, stable ? "shared-rule and character hashes remain stable while the turn changes" : "prompt mapping changed a stable segment",
                new Dictionary<string, object> { ["source"] = CodexConversationContracts.SegmentDictionaries(a, false), ["outgoing"] = CodexConversationContracts.SegmentDictionaries(b, false) });
        }

        private static Dictionary<string, object> TestCompactMetadata()
        {
            Dictionary<string, object> policy = CodexConversationContracts.CompactMetadataPolicy("dialogue");
            List<string> fields = ReadStringList(policy, "preserveFields");
            bool ok = ReadString(policy, "schema", "") == "reign-codex-compact-metadata-v1"
                && ReadBool(policy, "emptyCollectionsAllowed", false)
                && ReadString(policy, "dialogueLengthPolicy", "") == "unchanged"
                && !ReadBool(policy, "durableWritesMayBeDropped", true)
                && fields.Contains("evidenceSourceIds") && fields.Contains("uncertainty")
                && fields.Contains("memoryWrites") && fields.Contains("actionGate");
            return SelfTest(ok, ok ? "compact metadata preserves evidence, uncertainty, gates, and durable writes" : "compact metadata omitted a semantic field",
                new Dictionary<string, object> { ["preserveFields"] = fields });
        }

        private static Dictionary<string, object> TestThreadIdentity()
        {
            CodexConversationThreadIdentity one = CodexConversationContracts.BuildThreadIdentity(
                "campaign", "timeline", "load", "session", "dialogue", "speaker", "private", "reign-session", "authority", "prompt", "schema", "gpt", "high", false);
            CodexConversationThreadIdentity two = CodexConversationContracts.BuildThreadIdentity(
                "campaign", "timeline", "load", "session", "dialogue", "speaker", "private", "reign-session", "authority", "prompt", "schema", "gpt", "high", false);
            CodexConversationThreadIdentity changed = CodexConversationContracts.BuildThreadIdentity(
                "campaign", "timeline", "load", "session", "dialogue", "speaker", "private", "reign-session", "authority", "prompt", "schema", "gpt", "high", true);
            bool compatible = CodexConversationContracts.ThreadIdentitiesCompatible(one, two, out string sameReason);
            bool invalidated = !CodexConversationContracts.ThreadIdentitiesCompatible(one, changed, out string changedReason);
            bool incomplete = !CodexConversationContracts.BuildThreadIdentity(
                "campaign", "timeline", "", "session", "dialogue", "speaker", "private", "reign-session", "authority", "prompt", "schema", "gpt", "high", false).CanReuse(out _);
            bool ok = compatible && invalidated && incomplete && sameReason == "compatible" && changedReason == "fast mode changed";
            return SelfTest(ok, ok ? "thread reuse requires complete compatible authority identity" : "thread reuse accepted an incompatible identity",
                new Dictionary<string, object> { ["compatible"] = compatible, ["invalidated"] = invalidated, ["incomplete"] = incomplete, ["changedReason"] = changedReason });
        }

        private static Dictionary<string, object> TestParallelPreparation()
        {
            var two = new List<CodexContextPreparationTask>
            {
                new CodexContextPreparationTask { Id = "facts", Prepare = () => "facts" },
                new CodexContextPreparationTask { Id = "memory", Prepare = () => "memory" }
            };
            CodexParallelPreparationResult parallel = CodexConversationContracts.PrepareInParallel(two, true, true, true, true);
            var five = Enumerable.Range(0, 5).Select(index => new CodexContextPreparationTask
            {
                Id = "task_" + index,
                Prepare = () => index
            }).ToList();
            CodexParallelPreparationResult bounded = CodexConversationContracts.PrepareInParallel(five, true, true, true, true);
            Dictionary<string, object> production = TestProductionPromptParity();
            bool helperOk = parallel.UsedParallel && !parallel.FellBackToSequential
                && parallel.Results.Count == 2 && parallel.Results[0].ToString() == "facts"
                && bounded.FellBackToSequential && !bounded.UsedParallel && bounded.Results.Count == 5
                && bounded.FallbackReason.IndexOf("worker limit", StringComparison.OrdinalIgnoreCase) >= 0;
            bool ok = helperOk && ReadBool(production, "ok", false);
            return SelfTest(ok, ok ? "production dialogue and event envelopes preserve prompt parity while bounded preparation runs in parallel" : "parallel preparation violated the worker bound, ordering, or production prompt parity",
                new Dictionary<string, object>
                {
                    ["helperOk"] = helperOk,
                    ["parallel"] = parallel.Diagnostics,
                    ["bounded"] = bounded.Diagnostics,
                    ["boundedFallbackReason"] = bounded.FallbackReason,
                    ["production"] = production
                });
        }

        private static Dictionary<string, object> TestProductionPromptParity()
        {
            Dictionary<string, object> previousSettings = CodexPerformanceSettings.Value;
            try
            {
                Dictionary<string, object> fixtureSettings = DefaultSettings();
                fixtureSettings["llmProvider"] = CodexSubscriptionProvider;
                fixtureSettings["codexOptions"] = CodexConversationContracts.NormalizeOptions(
                    new Dictionary<string, object> { ["parallelContextPreparation"] = true });
                CodexPerformanceSettings.Value = fixtureSettings;

                PromptEnvelope sequentialDialogue = BuildPromptParityDialogue(false);
                PromptEnvelope parallelDialogue = BuildPromptParityDialogue(true);
                PromptEnvelope sequentialEvent = BuildPromptParityEvent(false);
                PromptEnvelope parallelEvent = BuildPromptParityEvent(true);
                bool dialogueEqual = PromptMessagesEqual(sequentialDialogue, parallelDialogue);
                bool eventEqual = PromptMessagesEqual(sequentialEvent, parallelEvent);
                bool dialogueUsedParallel = PromptEnvelopeReportsParallel(parallelDialogue);
                bool dialogueStayedSequential = !PromptEnvelopeReportsParallel(sequentialDialogue);
                bool eventUsedParallel = PromptEnvelopeReportsParallel(parallelEvent);
                bool eventStayedSequential = !PromptEnvelopeReportsParallel(sequentialEvent);
                bool ok = dialogueEqual && eventEqual && dialogueUsedParallel && dialogueStayedSequential
                    && eventUsedParallel && eventStayedSequential;
                return SelfTest(ok,
                    ok ? "production dialogue and event builders preserve exact message content across sequential and parallel preparation"
                        : "production prompt builders changed message content or did not select the requested preparation path",
                    new Dictionary<string, object>
                    {
                        ["dialogueMessagesEqual"] = dialogueEqual,
                        ["eventMessagesEqual"] = eventEqual,
                        ["dialogueParallelSelected"] = dialogueUsedParallel,
                        ["dialogueSequentialSelected"] = dialogueStayedSequential,
                        ["eventParallelSelected"] = eventUsedParallel,
                        ["eventSequentialSelected"] = eventStayedSequential
                    });
            }
            catch (Exception ex)
            {
                return SelfTest(false, "production prompt parity fixture threw: " + ex.Message,
                    new Dictionary<string, object> { ["exception"] = ex.ToString() });
            }
            finally
            {
                CodexPerformanceSettings.Value = previousSettings;
            }
        }

        private static PromptEnvelope BuildPromptParityDialogue(bool parallel)
        {
            Dictionary<string, object> payload = PromptParityPayload("in_person", parallel);
            return BuildDialoguePromptEnvelope(
                "codex_prompt_parity", "fixture_speaker", "Fixture Speaker", "Traveler", "Traveler",
                "The road is quiet tonight.", "A quiet market street.", PromptParityProfile(),
                PromptParityCharacteristics(), PromptParityState(), new Dictionary<string, object>(),
                new Dictionary<string, object>(), PromptParityLines(), new List<Dictionary<string, object>>(),
                new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                PromptParityIdentity(), payload, "", "");
        }

        private static PromptEnvelope BuildPromptParityEvent(bool parallel)
        {
            Dictionary<string, object> payload = PromptParityPayload("social_event", parallel);
            payload["mode"] = "social_event";
            var eventLines = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["turnId"] = "fixture_event_turn_1", ["speakerHeroStringId"] = "fixture_other",
                    ["speaker"] = "Another Speaker", ["role"] = "npc", ["text"] = "The market is watching."
                }
            };
            Dictionary<string, object> motive = new Dictionary<string, object>
            {
                ["sanitizedState"] = PromptParityState(),
                ["prompt"] = "The speaker weighs the public audience before answering.",
                ["relationshipNpcToTarget"] = new Dictionary<string, object>(),
                ["relationshipNpcToSpouse"] = new Dictionary<string, object>()
            };
            return BuildEventPromptEnvelope(
                "codex_prompt_parity", "fixture_event", "fixture_speaker", "Fixture Speaker", "Traveler", "Traveler",
                "The road is quiet tonight.", "A quiet market street.", payload, PromptParityProfile(),
                PromptParityCharacteristics(), PromptParityState(), new Dictionary<string, object>(),
                new Dictionary<string, object>(), eventLines, new List<Dictionary<string, object>>(),
                new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                PromptParityIdentity(), "", "", motive);
        }

        private static Dictionary<string, object> PromptParityPayload(string mode, bool parallel)
        {
            var payload = new Dictionary<string, object>
            {
                ["conversationMode"] = mode,
                ["officialMemoryFirewall"] = true,
                ["conversationScenePrompt"] = "A quiet market street.",
                ["conversationSceneState"] = new Dictionary<string, object>(),
                ["precomputedNpcRelationshipPrompt"] = new Dictionary<string, object>
                {
                    ["block"] = "", ["targets"] = new List<Dictionary<string, object>>()
                }
            };
            if (parallel)
            {
                payload["codexParallelPreparation"] = new Dictionary<string, object>
                {
                    ["consistentSnapshot"] = true,
                    ["characterInitialized"] = true,
                    ["requiredStateUpdatesComplete"] = true
                };
            }
            return payload;
        }

        private static Dictionary<string, object> PromptParityProfile()
        {
            return new Dictionary<string, object>
            {
                ["name"] = "Fixture Speaker", ["culture"] = "empire", ["occupation"] = "merchant",
                ["isNoble"] = false, ["relationToPlayer"] = 12
            };
        }

        private static Dictionary<string, object> PromptParityCharacteristics()
        {
            return new Dictionary<string, object>
            {
                ["traits"] = new Dictionary<string, object> { ["honor"] = 0.2d, ["valor"] = 0.1d }
            };
        }

        private static Dictionary<string, object> PromptParityState()
        {
            return new Dictionary<string, object>
            {
                ["mood"] = "calm", ["currentPlan"] = "listen", ["currentCrisis"] = ""
            };
        }

        private static List<Dictionary<string, object>> PromptParityLines()
        {
            return new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["turnId"] = "fixture_dialogue_turn_1", ["role"] = "player",
                    ["speakerHeroStringId"] = "player", ["text"] = "Good evening."
                }
            };
        }

        private static Dictionary<string, object> PromptParityIdentity()
        {
            return new Dictionary<string, object>
            {
                ["identityState"] = "verified", ["usableName"] = "Traveler",
                ["canonicalNameAllowed"] = true,
                ["instruction"] = "The current NPC may use the supplied player display name."
            };
        }

        private static bool PromptMessagesEqual(PromptEnvelope first, PromptEnvelope second)
        {
            return first != null && second != null
                && Json.Serialize(first.Messages ?? new List<Dictionary<string, object>>())
                    == Json.Serialize(second.Messages ?? new List<Dictionary<string, object>>());
        }

        private static bool PromptEnvelopeReportsParallel(PromptEnvelope envelope)
        {
            Dictionary<string, object> timing = ReadDictionary(envelope?.Diagnostics, "promptPhaseTiming");
            Dictionary<string, object> preparation = ReadDictionary(timing, "parallelPreparation");
            return ReadBool(preparation, "usedParallel", false);
        }

        private static Dictionary<string, object> TestTrustedAuthorityHash()
        {
            string noMetadata = CodexConversationContracts.AuthorityHash(
                new Dictionary<string, object> { ["campaignId"] = "caller-controlled" },
                new Dictionary<string, object>(), new Dictionary<string, object>());
            string trusted = CodexConversationContracts.AuthorityHash(
                new Dictionary<string, object> { ["campaignId"] = "caller-controlled" },
                new Dictionary<string, object> { ["campaignId"] = "server-campaign", ["timelineId"] = "timeline" },
                new Dictionary<string, object>());
            bool ok = string.IsNullOrWhiteSpace(noMetadata)
                && trusted.StartsWith("sha256:", StringComparison.Ordinal)
                && trusted.Length > 16;
            return SelfTest(ok, ok ? "thread authority hashes require trusted envelope metadata" : "authority hash accepted untrusted request identity",
                new Dictionary<string, object> { ["withoutMetadata"] = noMetadata, ["trustedHash"] = trusted });
        }

        private static Dictionary<string, object> SelfTest(bool ok, string detail, Dictionary<string, object> evidence)
        {
            var result = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["passed"] = ok,
                ["detail"] = detail,
                ["evidence"] = evidence ?? new Dictionary<string, object>()
            };
            return result;
        }
    }
}
