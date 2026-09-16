using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    /// <summary>
    /// Builds the Codex outputSchema payload from Reign's actual built-in
    /// response contracts.  The prompt files named *_output_schema.json are
    /// examples for the model; they are deliberately never passed through as
    /// JSON Schema.
    /// </summary>
    internal static class CodexConversationSchemas
    {
        private const string Draft = "https://json-schema.org/draft/2020-12/schema";

        // These are the only nulls introduced by strictification to preserve
        // legacy omission semantics. The adapter removes these null entries
        // before the existing semantic normalizers merge durable state.
        internal static readonly IReadOnlyList<string> SchemaIntroducedNullPaths =
            new[]
            {
                "stateUpdates.mood",
                "stateUpdates.currentPlan",
                "stateUpdates.currentCrisis",
                "dynamicCharacteristicWrites[].narrativeDevelopment"
            };

        // This null is part of the existing office response contract and is
        // meaningful; it must survive adapter normalization.
        internal static readonly IReadOnlyList<string> ContractDefinedNullPaths =
            new[] { "chancellorDecision.activeDailySalary" };

        private static readonly string[] CommonFields =
        {
            "reply", "participation", "reactionTargetHeroStringId", "decisionBrief",
            "politicalConduct", "emotion", "intent", "relationshipSignal",
            "relationshipAssessments", "actionGate", "conceptionGate", "socialSignals",
            "identityIntroductions", "memoryWrites", "beliefWrites", "relationshipUpdates",
            "obligationWrites", "comprehensionWrites", "dynamicCharacteristicWrites",
            "courtKnowledgeWrites", "sceneStateUpdates", "drinkingEvents", "stateUpdates",
            "suggestedActions", "chancellorDecision", "rebellionDecision", "campaignOrder"
        };

        internal static IReadOnlyList<string> TemplateNames(CodexConversationContractKind kind)
        {
            switch (kind)
            {
                case CodexConversationContractKind.Individual:
                    return new[] { "dialogue_system.txt", "dialogue_live_turn_template.txt", "dialogue_output_schema.json" };
                case CodexConversationContractKind.Group:
                case CodexConversationContractKind.Castle:
                case CodexConversationContractKind.Social:
                    return new[] { "event_system.txt", "event_live_turn_template.txt", "event_output_schema.json" };
                case CodexConversationContractKind.Correspondence:
                    return new[] { "correspondence_system.txt", "correspondence_live_turn_template.txt" };
                default:
                    return System.Array.Empty<string>();
            }
        }

        internal static CodexConversationSchemaSelection Select(
            CodexConversationContractKind kind,
            IDictionary<string, string> actualTemplates,
            IDictionary<string, string> builtInTemplates,
            Dictionary<string, object> payload)
        {
            string mode = KindMode(kind);
            var selection = new CodexConversationSchemaSelection
            {
                Kind = kind,
                Mode = mode,
                Applicability = "schema not applicable",
                PreservedFields = new List<string>(CommonFields)
            };

            if (kind == CodexConversationContractKind.Unknown)
            {
                selection.Reason = "request mode is outside the standard individual, group, castle, social, and correspondence contracts";
                return selection;
            }

            Dictionary<string, string> actual = CopyTemplates(actualTemplates);
            Dictionary<string, string> defaults = CopyTemplates(builtInTemplates);
            selection.ContractFingerprint = CodexConversationContracts.Sha256(
                CodexConversationContracts.CanonicalValue(actual));

            string missing = TemplateNames(kind)
                .FirstOrDefault(name => !actual.ContainsKey(name) || string.IsNullOrWhiteSpace(actual[name]));
            bool customFlag = HasCustomizationFlag(payload);
            bool builtIn = string.IsNullOrWhiteSpace(missing)
                && !customFlag
                && TemplateNames(kind).All(name => defaults.ContainsKey(name)
                    && string.Equals((actual[name] ?? "").Trim(), (defaults[name] ?? "").Trim(), StringComparison.Ordinal));
            if (!builtIn)
            {
                selection.BuiltInContract = false;
                selection.Reason = !string.IsNullOrWhiteSpace(missing)
                    ? "standard contract template is missing: " + missing
                    : customFlag
                        ? "a customized contract marker was supplied; legacy path preserves the complete response"
                        : "loaded prompt templates differ from the built-in contract; legacy path preserves the complete response";
                return selection;
            }

            selection.BuiltInContract = true;
            selection.Applicability = "applicable";
            selection.SchemaIntroducedNullPaths = kind == CodexConversationContractKind.Correspondence
                ? new List<string>()
                : SchemaIntroducedNullPaths.ToList();
            selection.ContractDefinedNullPaths = kind == CodexConversationContractKind.Individual
                && HasChancellorContract(payload)
                ? ContractDefinedNullPaths.ToList()
                : new List<string>();
            selection.SchemaId = "reign-codex-" + mode + "-v1";
            selection.Schema = kind == CodexConversationContractKind.Correspondence
                ? BuildCorrespondenceSchema()
                : BuildConversationSchema(kind, payload);
            Strictify(selection.Schema);
            selection.Schema["$schema"] = Draft;
            selection.Schema["$id"] = selection.SchemaId;
            selection.OutputSchema = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "json_schema",
                ["name"] = selection.SchemaId,
                ["strict"] = true,
                ["schema"] = selection.Schema
            };
            selection.PreservedFields = ReadSchemaFields(selection.Schema);
            return selection;
        }

        internal static CodexConversationSchemaSelection LegacyFallback(
            CodexConversationContractKind kind,
            string reason)
        {
            return new CodexConversationSchemaSelection
            {
                Kind = kind,
                Mode = KindMode(kind),
                Applicability = "schema not applicable",
                Reason = reason ?? "schema contract evidence was not supplied",
                BuiltInContract = false,
                PreservedFields = new List<string>(CommonFields)
            };
        }

        private static Dictionary<string, string> CopyTemplates(IDictionary<string, string> source)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in source ?? new Dictionary<string, string>())
                result[pair.Key] = pair.Value ?? "";
            return result;
        }

        private static string KindMode(CodexConversationContractKind kind)
        {
            switch (kind)
            {
                case CodexConversationContractKind.Individual: return "individual";
                case CodexConversationContractKind.Group: return "group";
                case CodexConversationContractKind.Castle: return "castle";
                case CodexConversationContractKind.Social: return "social";
                case CodexConversationContractKind.Correspondence: return "correspondence";
                default: return "unknown";
            }
        }

        private static bool HasCustomizationFlag(Dictionary<string, object> payload)
        {
            if (payload == null) return false;
            foreach (string key in new[]
            {
                "customContract", "customPromptContract", "customizedPromptContract",
                "promptContractCustomized", "responseContractCustomized"
            })
            {
                object value;
                if (payload.TryGetValue(key, out value) && ToBool(value)) return true;
            }
            // These markers are copied from the server-built prompt metadata.
            // Their presence means the response contract may contain fields
            // outside the built-in schema. A null marker is not a customization
            // request; a non-null marker is enough to require the legacy path.
            foreach (string key in new[]
            {
                "outputSchema", "customOutputSchema", "customOutputContract",
                "outputContractOverride", "guardedPromptOverride"
            })
            {
                if (payload.TryGetValue(key, out object value) && value != null)
                    return true;
            }
            return false;
        }

        private static bool ToBool(object value)
        {
            if (value is bool boolean) return boolean;
            return bool.TryParse(Convert.ToString(value), out bool parsed) && parsed;
        }

        private static List<string> ReadSchemaFields(Dictionary<string, object> schema)
        {
            var names = new List<string>();
            Dictionary<string, object> properties = null;
            if (schema != null && schema.TryGetValue("properties", out object rawProperties))
                properties = rawProperties as Dictionary<string, object>;
            if (properties != null) names.AddRange(properties.Keys);
            return names;
        }

        private static Dictionary<string, object> BuildConversationSchema(
            CodexConversationContractKind kind,
            Dictionary<string, object> payload)
        {
            var properties = CommonConversationProperties(kind, payload);
            var required = new List<string> { "reply", "decisionBrief", "politicalConduct", "emotion", "intent", "relationshipSignal", "relationshipAssessments", "actionGate" };
            if (kind != CodexConversationContractKind.Individual)
            {
                required.Add("participation");
                required.Add("reactionTargetHeroStringId");
            }
            return Object(properties, required, false, "Reign's complete structured contract for an in-world conversation response.");
        }

        private static Dictionary<string, object> CommonConversationProperties(
            CodexConversationContractKind kind,
            Dictionary<string, object> payload)
        {
            var properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["reply"] = String(),
                ["decisionBrief"] = DecisionBrief(),
                ["politicalConduct"] = PoliticalConduct(),
                ["emotion"] = String(),
                ["intent"] = String(),
                ["relationshipSignal"] = String(),
                ["relationshipAssessments"] = Array(RelationshipAssessment()),
                ["actionGate"] = ActionGate(new[] { "accepted", "commanded", "conditional", "refused", "threat", "roleplay_only", "final_private_report" }),
                ["identityIntroductions"] = Array(IdentityIntroduction()),
                ["memoryWrites"] = Array(MemoryWrite()),
                ["beliefWrites"] = Array(BeliefWrite()),
                ["relationshipUpdates"] = Array(RelationshipUpdate()),
                ["obligationWrites"] = Array(ObligationWrite()),
                ["comprehensionWrites"] = Array(ComprehensionWrite()),
                ["dynamicCharacteristicWrites"] = Array(DynamicCharacteristicWrite()),
                ["sceneStateUpdates"] = Array(SceneStateUpdate()),
                ["drinkingEvents"] = Array(DrinkingEvent()),
                ["stateUpdates"] = Object(new Dictionary<string, object>
                {
                    // Strict structured output requires the keys to be present.
                    // Null means "leave this authoritative state field alone";
                    // the existing finalizer must remove nulls before merging.
                    ["mood"] = NullableString(), ["currentPlan"] = NullableString(), ["currentCrisis"] = NullableString()
                }, null, true),
                ["suggestedActions"] = Array(SuggestedAction())
            };

            if (kind == CodexConversationContractKind.Individual)
            {
                properties["conceptionGate"] = ConceptionGate();
                properties["socialSignals"] = Array(SocialSignal());
                if (HasChancellorContract(payload)) properties["chancellorDecision"] = ChancellorDecision();
                if (HasRebellionContract(payload))
                    properties["rebellionDecision"] = String(new[] { "none", "accept_recruitment", "accept_player_join", "surrender" });
            }
            else
            {
                properties["participation"] = String(new[] { "speak", "agree", "disagree", "quiet" });
                properties["reactionTargetHeroStringId"] = String();
            }
            return properties;
        }

        private static bool HasChancellorContract(Dictionary<string, object> payload)
        {
            Dictionary<string, object> context = null;
            if (payload != null && payload.TryGetValue("chancellorOfficeContext", out object rawContext))
                context = rawContext as Dictionary<string, object>;
            object enabled = null;
            if (context != null) context.TryGetValue("enabled", out enabled);
            return ToBool(enabled)
                || HasNonNullPayloadValue(payload, "chancellorDecision")
                || HasNonNullPayloadValue(payload, "chancellorOfficeContext");
        }

        private static bool HasRebellionContract(Dictionary<string, object> payload)
        {
            if (payload == null) return false;
            return HasNonNullPayloadValue(payload, "rebellionDecision")
                || HasNonNullPayloadValue(payload, "rebellionContext")
                || HasNonNullPayloadValue(payload, "rebellionPreparation")
                || HasNonNullPayloadValue(payload, "rebellionRequest");
        }

        private static bool HasNonNullPayloadValue(Dictionary<string, object> payload, string key)
        {
            return payload != null && payload.TryGetValue(key, out object value) && value != null;
        }

        private static Dictionary<string, object> BuildCorrespondenceSchema()
        {
            var properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["shouldReply"] = Boolean(),
                ["body"] = String(),
                ["reason"] = String(),
                ["actionGate"] = ActionGate(new[] { "none", "conditional", "accepted", "refused", "final_private_report" }),
                ["rebellionDecision"] = String(new[] { "none", "accept_recruitment", "accept_player_join", "surrender" }),
                ["campaignOrder"] = CampaignOrder()
            };
            return Object(properties, new[] { "shouldReply", "body", "reason", "actionGate", "rebellionDecision", "campaignOrder" }, false,
                "Reign's complete structured contract for written correspondence.");
        }

        private static Dictionary<string, object> DecisionBrief()
        {
            return Object(new Dictionary<string, object>
            {
                ["facts"] = Array(String()), ["goals"] = Array(String()), ["constraints"] = Array(String()),
                ["decision"] = String(), ["confidence"] = Number(0, 1), ["activeDomains"] = Array(String()),
                ["appliedEvidenceKeys"] = Array(String()), ["postureAlignment"] = String(),
                ["courtTactic"] = String(), ["courtCharacterCell"] = String()
            }, new[] { "facts", "goals", "constraints", "decision", "confidence", "activeDomains", "appliedEvidenceKeys", "postureAlignment" }, true);
        }

        private static Dictionary<string, object> PoliticalConduct()
        {
            return Object(new Dictionary<string, object>
            {
                ["addressMode"] = String(new[] { "formal", "informal_allowed", "neutral", "first_name" }),
                ["defianceTier"] = Integer(),
                ["stance"] = String(new[] { "compliance", "counsel", "refusal", "defiance", "guarded_neutrality", "quiet" }),
                ["appliedEvidenceKeys"] = Array(String())
            }, new[] { "addressMode", "defianceTier", "stance", "appliedEvidenceKeys" }, true);
        }

        private static Dictionary<string, object> RelationshipAssessment()
        {
            return Object(new Dictionary<string, object>
            {
                ["observerHeroStringId"] = String(), ["targetHeroStringId"] = String(),
                ["valence"] = String(new[] { "positive", "negative" }), ["actKind"] = String(), ["severityTier"] = String(),
                ["confidence"] = Number(0, 1), ["sourceTurnIds"] = Array(String()), ["evidenceSourceIds"] = Array(String()),
                ["currentConductQuote"] = String(), ["lieCheckId"] = String(), ["benefitEventId"] = String(),
                ["giftRecipientHeroStringId"] = String(), ["importance"] = Number(0, 1),
                ["continuedUtility"] = Number(0, 1), ["retainedAppreciation"] = Number(0, 1),
                ["coercionSeverity"] = Number(0, 1), ["summary"] = String(), ["sourceText"] = String()
            }, new[] { "targetHeroStringId", "valence", "actKind", "severityTier", "confidence", "sourceTurnIds", "evidenceSourceIds", "currentConductQuote", "lieCheckId", "benefitEventId", "giftRecipientHeroStringId", "importance", "continuedUtility", "retainedAppreciation", "coercionSeverity", "summary" }, true);
        }

        private static Dictionary<string, object> ActionGate(IEnumerable<string> commitments)
        {
            return Object(new Dictionary<string, object>
            {
                ["needed"] = Boolean(), ["commitment"] = String((commitments ?? Enumerable.Empty<string>()).Concat(new[] { "none" })), ["intent"] = String(),
                ["confidence"] = Number(0, 1), ["reason"] = String()
            }, new[] { "needed", "commitment", "intent", "confidence", "reason" }, true);
        }

        private static Dictionary<string, object> ConceptionGate()
        {
            return Object(new Dictionary<string, object>
            {
                ["needed"] = Boolean(), ["completed"] = Boolean(), ["confidence"] = Number(0, 1), ["reason"] = String()
            }, new[] { "needed", "completed", "confidence", "reason" }, true);
        }

        private static Dictionary<string, object> SocialSignal()
        {
            return Object(new Dictionary<string, object>
            {
                ["type"] = String(new[] { "flirtation", "sexual_intimacy_completed" }),
                ["speakerHeroId"] = String(), ["targetHeroId"] = String(), ["supportingQuote"] = String()
            }, new[] { "type", "speakerHeroId", "targetHeroId", "supportingQuote" }, true);
        }

        private static Dictionary<string, object> IdentityIntroduction()
        {
            return Object(new Dictionary<string, object>
            {
                ["observerHeroStringId"] = String(), ["subjectHeroStringId"] = String(),
                ["introducerHeroStringId"] = String(), ["claimedName"] = String()
            }, new[] { "observerHeroStringId", "subjectHeroStringId", "introducerHeroStringId", "claimedName" }, true);
        }

        private static Dictionary<string, object> MemoryWrite()
        {
            return Object(new Dictionary<string, object>
            {
                ["text"] = String(), ["importance"] = Number(1, 5), ["tags"] = Array(String()),
                ["evidenceSourceIds"] = Array(String()), ["sourceTurnIds"] = Array(String()), ["uncertainty"] = Number(0, 1)
            }, new[] { "text", "importance", "tags" }, true);
        }

        private static Dictionary<string, object> BeliefWrite()
        {
            return Object(new Dictionary<string, object>
            {
                ["claim"] = String(), ["confidence"] = Number(0, 1), ["about_entities"] = Array(String()),
                ["visibility"] = String(), ["evidenceSourceIds"] = Array(String()), ["uncertainty"] = Number(0, 1)
            }, new[] { "claim", "confidence", "about_entities", "visibility" }, true);
        }

        private static Dictionary<string, object> RelationshipUpdate()
        {
            return Object(new Dictionary<string, object>
            {
                ["targetHeroStringId"] = String(), ["signal"] = String(), ["summary"] = String(),
                ["evidenceSourceIds"] = Array(String()), ["uncertainty"] = Number(0, 1)
            }, new[] { "targetHeroStringId" }, true);
        }

        private static Dictionary<string, object> ObligationWrite()
        {
            return Object(new Dictionary<string, object>
            {
                ["description"] = String(), ["owed_by"] = String(), ["owed_to"] = String(),
                ["status"] = String(), ["importance"] = Number(0, 1), ["evidenceSourceIds"] = Array(String()), ["uncertainty"] = Number(0, 1)
            }, new[] { "description", "owed_by", "owed_to", "status", "importance" }, true);
        }

        private static Dictionary<string, object> ComprehensionWrite()
        {
            return Object(new Dictionary<string, object>
            {
                ["text"] = String(), ["stance"] = String(), ["confidence"] = Number(0, 1),
                ["evidenceSourceIds"] = Array(String()), ["uncertainty"] = Number(0, 1)
            }, new[] { "text", "stance", "confidence" }, true);
        }

        private static Dictionary<string, object> DynamicCharacteristicWrite()
        {
            return Object(new Dictionary<string, object>
            {
                ["text"] = String(), ["category"] = String(new[] { "personal_history", "formative_experience", "habit", "preference", "aversion", "local_connection", "skill_practice", "personal_value", "social_tendency", "aspiration" }),
                ["topicKey"] = String(), ["confidence"] = Number(0, 1), ["importance"] = Number(0, 1),
                ["sourceBasis"] = String(new[] { "npc_generated_personal_history" }), ["evidenceSourceIds"] = Array(String()), ["uncertainty"] = Number(0, 1),
                ["narrativeDevelopment"] = NullableObject(new Dictionary<string, object>
                {
                    ["itemId"] = String(), ["influence"] = Integer(), ["description"] = String(),
                    ["personalMeaning"] = String(), ["supportingQuote"] = String(), ["evidenceEventId"] = String()
                }, new[] { "itemId", "influence", "description", "personalMeaning", "supportingQuote", "evidenceEventId" }, true)
            }, new[] { "text", "category", "topicKey", "confidence", "importance", "sourceBasis", "narrativeDevelopment" }, true);
        }

        private static Dictionary<string, object> SceneStateUpdate()
        {
            return Object(new Dictionary<string, object>
            {
                ["heroStringId"] = String(), ["location"] = String(),
                ["locationClass"] = String(new[] { "formal", "travel" }), ["clothing"] = String()
            }, new[] { "heroStringId" }, true);
        }

        private static Dictionary<string, object> DrinkingEvent()
        {
            return Object(new Dictionary<string, object>
            {
                ["actionIndex"] = Integer(), ["serving"] = String(new[] { "drink", "half", "sip" }),
                ["count"] = Integer(), ["completed"] = Boolean(), ["alcohol"] = Boolean()
            }, new[] { "actionIndex", "serving", "count", "completed", "alcohol" }, true);
        }

        private static Dictionary<string, object> SuggestedAction()
        {
            return Object(new Dictionary<string, object>
            {
                ["command"] = String(), ["reason"] = String(), ["requiresAcceptance"] = Boolean()
            }, new[] { "command" }, true);
        }

        private static Dictionary<string, object> ChancellorDecision()
        {
            return Object(new Dictionary<string, object>
            {
                ["kind"] = String(new[] { "none", "appointment_agreement", "appointment_refusal", "dismissal_acknowledged" }),
                ["explicit"] = Boolean(), ["salarySpecified"] = Boolean(),
                ["activeDailySalary"] = NullableInteger(), ["supportingQuote"] = String()
            }, new[] { "kind", "explicit", "salarySpecified", "activeDailySalary", "supportingQuote" }, true);
        }

        private static Dictionary<string, object> CampaignOrder()
        {
            string[] objectives = { "move", "hold_position", "timed_hold", "patrol", "scout_report", "escort", "recruit_resupply", "form_army", "join_army", "leave_army", "disband_army", "raid", "besiege_capture", "defend", "relieve_siege", "hunt_enemy_parties", "engage_party", "withdraw", "return_home" };
            var step = Object(new Dictionary<string, object>
            {
                ["objective"] = String(objectives), ["targetSettlement"] = String(), ["targetParty"] = String(),
                ["targetHero"] = String(), ["region"] = String(), ["durationHours"] = Number(0, null),
                ["minimumTroops"] = Integer(), ["minimumInfantry"] = Integer(), ["minimumArchers"] = Integer(), ["minimumCavalry"] = Integer()
            }, new[] { "objective", "targetSettlement", "targetParty", "targetHero", "region", "durationHours", "minimumTroops", "minimumInfantry", "minimumArchers", "minimumCavalry" }, true);
            return Object(new Dictionary<string, object>
            {
                ["decision"] = String(new[] { "none", "accept", "clarify", "refuse" }), ["reason"] = String(), ["steps"] = Array(step)
            }, new[] { "decision", "reason", "steps" }, true);
        }

        private static void Strictify(Dictionary<string, object> schema)
        {
            if (schema == null) return;
            Dictionary<string, object> properties = null;
            if (schema.TryGetValue("properties", out object rawProperties))
                properties = rawProperties as Dictionary<string, object>;
            if (properties != null)
            {
                foreach (object value in properties.Values)
                    Strictify(value as Dictionary<string, object>);
                schema["additionalProperties"] = false;
                schema["required"] = properties.Keys.ToList();
            }
            Dictionary<string, object> items = null;
            if (schema.TryGetValue("items", out object rawItems))
                items = rawItems as Dictionary<string, object>;
            if (items != null) Strictify(items);
        }

        private static Dictionary<string, object> Object(
            IDictionary<string, object> properties,
            IEnumerable<string> required,
            bool additionalProperties,
            string description = null)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>(properties ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase),
                ["additionalProperties"] = additionalProperties
            };
            List<string> requiredList = (required ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (requiredList.Count > 0) result["required"] = requiredList;
            if (!string.IsNullOrWhiteSpace(description)) result["description"] = description;
            return result;
        }

        private static Dictionary<string, object> String(IEnumerable<string> enumeration = null)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "string" };
            string[] values = (enumeration ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            if (values.Length > 0) result["enum"] = values;
            return result;
        }

        private static Dictionary<string, object> NullableString()
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = new[] { "string", "null" }
            };
        }

        private static Dictionary<string, object> NullableObject(
            IDictionary<string, object> properties,
            IEnumerable<string> required,
            bool additionalProperties)
        {
            Dictionary<string, object> result = Object(properties, required, additionalProperties);
            result["type"] = new[] { "object", "null" };
            return result;
        }

        private static Dictionary<string, object> Boolean()
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "boolean" };
        }

        private static Dictionary<string, object> Integer()
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "integer" };
        }

        private static Dictionary<string, object> NullableInteger()
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = new[] { "integer", "null" } };
        }

        private static Dictionary<string, object> Number(double? minimum, double? maximum)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["type"] = "number" };
            if (minimum.HasValue) result["minimum"] = minimum.Value;
            if (maximum.HasValue) result["maximum"] = maximum.Value;
            return result;
        }

        private static Dictionary<string, object> Array(Dictionary<string, object> items)
        {
            if (items == null) throw new InvalidOperationException("A Codex schema array requires an explicit item contract.");
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = "array", ["items"] = items
            };
        }
    }
}
